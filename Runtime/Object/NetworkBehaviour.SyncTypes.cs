using System;
using System.Buffers;
using FishNet.Documenting;
using FishNet.Managing.Transporting;
using FishNet.Object.Synchronizing;
using FishNet.Object.Synchronizing.Internal;
using FishNet.Serializing;
using FishNet.Transporting;
using FishNet.Utility.Extension;
using System.Collections.Generic;

namespace FishNet.Object
{
    public abstract partial class NetworkBehaviour
    {
        #region Types.

        /// <summary>
        /// Used to generate data sent from sync types.
        /// </summary>
        private class SyncTypeWriter : IDisposable
        {
            /// <summary>
            /// Clients which can be synchronized.
            /// </summary>
            public readonly ReadPermission ReadPermission;

            /// <summary>
            /// Writers for each channel.
            /// </summary>
            public PooledWriter[] Writers { get; private set; }

            public SyncTypeWriter(ReadPermission readPermission)
            {
                ReadPermission = readPermission;
                Writers = ArrayPool<PooledWriter>.Shared.Rent(TransportManager.CHANNEL_COUNT);
                for (var i = 0; i < Writers.Length; i++)
                    Writers[i] = WriterPool.Retrieve();
            }

            /// <summary>
            /// Resets Writers.
            /// </summary>
            public void Reset()
            {
                if (Writers == null)
                    return;
                foreach (var t in Writers)
                    t.Reset();
            }

            public void Dispose()
            {
                if (Writers == null)
                    return;
                foreach (var t in Writers)
                    t.Store();
                ArrayPool<PooledWriter>.Shared.Return(Writers);
                Writers = null;
            }
        }

        #endregion

        #region Private.

        /// <summary>
        /// Writers for syncTypes. A writer will exist for every ReadPermission type.
        /// </summary>
        private SyncTypeWriter[] _syncTypeWriters;

        /// <summary>
        /// SyncVars within this NetworkBehaviour.
        /// </summary>
        private readonly Dictionary<uint, SyncBase> _syncVars = new();

        /// <summary>
        /// True if at least one syncVar is dirty.
        /// </summary>
        private bool _syncVarDirty;

        /// <summary>
        /// SyncVars within this NetworkBehaviour.
        /// </summary>
        private readonly Dictionary<uint, SyncBase> _syncObjects = new();

        /// <summary>
        /// True if at least one syncObject is dirty.
        /// </summary>
        private bool _syncObjectDirty;

        /// <summary>
        /// All ReadPermission values.
        /// </summary>
        private static ReadPermission[] _readPermissions;

        #endregion

        /// <summary>
        /// Registers a SyncType.
        /// </summary>
        /// <param name="sb"></param>
        /// <param name="index"></param>
        internal void RegisterSyncType(SyncBase sb, uint index)
        {
            if (sb.IsSyncObject)
                _syncObjects.Add(index, sb);
            else
                _syncVars.Add(index, sb);
        }

        /// <summary>
        /// Sets a SyncVar as dirty.
        /// </summary>
        /// <param name="isSyncObject">True if dirtying a syncObject.</param>
        /// <returns>True if able to dirty SyncType.</returns>
        internal bool DirtySyncType(bool isSyncObject)
        {
            if (!IsServer)
                return false;
            /* No reason to dirty if there are no observers.
             * This can happen even if a client is going to see
             * this object because the server side initializes
             * before observers are built. */
            if (_networkObjectCache.Observers.Count == 0)
                return false;

            var alreadyDirtied = (isSyncObject) ? _syncObjectDirty : _syncVarDirty;
            if (isSyncObject)
                _syncObjectDirty = true;
            else
                _syncVarDirty = true;

            if (!alreadyDirtied)
                _networkObjectCache.NetworkManager.ServerManager.Objects.SetDirtySyncType(this, isSyncObject);

            return true;
        }

        /// <summary>
        /// Initializes SyncTypes. This will only call once even as host.
        /// </summary>
        private void InitializeOnceSyncTypes(bool asServer)
        {
            if (asServer)
            {
                if (!_initializedOnceServer)
                {
                    //optimization Cache sync typewriters on despawn and get from cache on spawn.
                    //Only need to initialize read permissions once, it's static.
                    if (_readPermissions == null)
                    {
                        var arr = Enum.GetValues(typeof(ReadPermission));
                        _readPermissions = new ReadPermission[arr.Length];
                        var count = 0;
                        foreach (ReadPermission rp in arr)
                        {
                            _readPermissions[count] = rp;
                            count++;
                        }
                    }

                    //Build writers for observers and owner.
                    _syncTypeWriters = ArrayPool<SyncTypeWriter>.Shared.Rent(_readPermissions.Length);
                    for (var i = 0; i < _syncTypeWriters.Length; i++)
                        _syncTypeWriters[i] = new SyncTypeWriter(_readPermissions[i]);
                }
                else
                    foreach (var t in _syncTypeWriters)
                        t.Reset();
            }

            /* Initialize sync types every spawn because there could be
             * callbacks which occur that the user or even we may implement
             * during the initialization. */
            foreach (var sb in _syncVars.Values)
                sb.PreInitialize(_networkObjectCache.NetworkManager);
            foreach (var sb in _syncObjects.Values)
                sb.PreInitialize(_networkObjectCache.NetworkManager);
        }


        /// <summary>
        /// Reads a SyncVar.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="length"></param>
        /// <param name="isSyncObject"></param>
        /// <param name="asServer"></param>
        internal void OnSyncType(PooledReader reader, int length, bool isSyncObject, bool asServer = false)
        {
            var readerStart = reader.Position;
            while (reader.Position - readerStart < length)
            {
                var index = reader.ReadByte();
                if (isSyncObject)
                {
                    if (_syncObjects.TryGetValueIL2CPP(index, out var sb))
                        sb.Read(reader, asServer);
                    else
                        NetworkManager.LogWarning(
                            $"SyncObject not found for index {index} on {transform.name}. Remainder of packet may become corrupt.");
                }
                else
                {
                    if (_syncVars.ContainsKey(index))
                        ReadSyncVar(reader, index, asServer);
                    else
                        NetworkManager.LogWarning(
                            $"SyncVar not found for index {index} on {transform.name}. Remainder of packet may become corrupt.");
                }
            }
        }

        /// <summary>
        /// Codegen overrides this method to read syncVars for each script which inherits NetworkBehaviour.
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="index"></param>
        /// <param name="asServer">True if reading into SyncVars for the server, false for client. This would be true for predicted spawning if the predicted spawner sent sync vars.</param>
        [APIExclude]
        internal virtual bool ReadSyncVar(PooledReader reader, uint index, bool asServer) => false;

        /// <summary>
        /// Writers dirty SyncTypes if their write tick has been met.
        /// </summary>
        /// <returns>True if there are no pending dirty sync types.</returns>
        internal bool WriteDirtySyncTypes(bool isSyncObject, bool ignoreInterval = false)
        {
            /* Can occur when a sync type is queued after
             * the object is marked for destruction. This should not
             * happen under most conditions since sync types will be
             * pushed through when despawn is called. */
            if (!IsSpawned)
            {
                ResetSyncTypes();
                return true;
            }

            /* If there is nothing dirty then return true, indicating no more
             * pending dirty checks. */
            if (isSyncObject && (!_syncObjectDirty || _syncObjects.Count == 0))
                return true;
            if (!isSyncObject && (!_syncVarDirty || _syncVars.Count == 0))
                return true;

            /* True if writers have been reset for this check.
             * For perf writers are only reset when data is to be written. */
            var writersReset = false;
            var tick = _networkObjectCache.NetworkManager.TimeManager.Tick;

            //True if a sync var is found to still be dirty.
            var dirtyFound = false;
            //True if data has been written and is ready to send.
            var dataWritten = false;
            var collection = (isSyncObject) ? _syncObjects : _syncVars;

            foreach (var sb in collection.Values)
            {
                if (!sb.IsDirty)
                    continue;

                dirtyFound = true;
                if (ignoreInterval || sb.SyncTimeMet(tick))
                {
                    //If writers still need to be reset.
                    if (!writersReset)
                    {
                        writersReset = true;
                        //Reset writers.
                        foreach (var t in _syncTypeWriters)
                            t.Reset();
                    }

                    //Find channel.
                    var channel = (byte)sb.Channel;
                    sb.ResetDirty();
                    //If ReadPermission is owner but no owner skip this sync var write.
                    if (sb.Settings.ReadPermission == ReadPermission.OwnerOnly && !_networkObjectCache.Owner.IsValid)
                        continue;

                    dataWritten = true;
                    //Find PooledWriter to use.
                    PooledWriter writer = null;
                    foreach (var t in _syncTypeWriters)
                    {
                        if (t.ReadPermission != sb.Settings.ReadPermission) continue;
                        /* Channel for syncVar is beyond available channels in transport.
                         * Use default reliable. */
                        /* Channel for syncVar is beyond available channels in transport.
                         * Use default reliable. */
                        if (channel >= t.Writers.Length)
                            channel = (byte)Channel.Reliable;
                        writer = t.Writers[channel];
                        break;
                    }

                    if (writer == null)
                        NetworkManager.LogError(
                            $"Writer couldn't be found for permissions {sb.Settings.ReadPermission} on channel {channel}.");
                    else
                        sb.WriteDelta(writer);
                }
            }

            //If no dirty were found.
            if (!dirtyFound)
            {
                if (isSyncObject)
                    _syncObjectDirty = false;
                else
                    _syncVarDirty = false;
                return true;
            }
            //At least one sync type was dirty.

            if (!dataWritten) return false;
            foreach (var t in _syncTypeWriters)
            {
                for (byte channel = 0; channel < t.Writers.Length; channel++)
                {
                    var channelWriter = t.Writers[channel];
                    //If there is data to send.
                    if (channelWriter.Length <= 0) continue;
                    var headerWriter = WriterPool.Retrieve();
                    //Write the packetId and NB information.
                    var packetId = (isSyncObject) ? PacketId.SyncObject : PacketId.SyncVar;
                    headerWriter.WritePacketId(packetId);
                    var dataWriter = WriterPool.Retrieve();
                    dataWriter.WriteNetworkBehaviour(this);

                    /* SyncVars need length written regardless because amount
                     * of data being sent per sync var is unknown, and the packet may have
                     * additional data after the sync vars. Because of this we should only
                     * read up to sync var length then assume the remainder is another packet.
                     *
                     * Reliable always has data written as well even if syncObject. This is so
                     * if an object does not exist for whatever reason the packet can be
                     * recovered by skipping the data.
                     *
                     * Realistically everything will be a sync var or on the reliable channel unless
                     * the user makes a custom sync object that utilizes unreliable. */
                    /* SyncVars need length written regardless because amount
                     * of data being sent per sync var is unknown, and the packet may have
                     * additional data after the sync vars. Because of this we should only
                     * read up to sync var length then assume the remainder is another packet.
                     *
                     * Reliable always has data written as well even if syncObject. This is so
                     * if an object does not exist for whatever reason the packet can be
                     * recovered by skipping the data.
                     *
                     * Realistically everything will be a sync var or on the reliable channel unless
                     * the user makes a custom sync object that utilizes unreliable. */
                    if (!isSyncObject || (Channel)channel == Channel.Reliable)
                        dataWriter.WriteBytesAndSize(channelWriter.GetBuffer(), 0, channelWriter.Length);
                    else
                        dataWriter.WriteBytes(channelWriter.GetBuffer(), 0, channelWriter.Length);

                    //Attach data onto packetWriter.
                    headerWriter.WriteArraySegment(dataWriter.GetArraySegment());
                    dataWriter.Store();


                    //If only sending to owner.
                    if (t.ReadPermission == ReadPermission.OwnerOnly)
                    {
                        _networkObjectCache.NetworkManager.TransportManager.SendToClient(channel,
                            headerWriter.GetArraySegment(), _networkObjectCache.Owner);
                    }
                    //Sending to observers.
                    else
                    {
                        var excludeOwner = (t.ReadPermission == ReadPermission.ExcludeOwner);
                        SetNetworkConnectionCache(false, excludeOwner);
                        _networkObjectCache.NetworkManager.TransportManager.SendToClients(channel,
                            headerWriter.GetArraySegment(), _networkObjectCache.Observers,
                            _networkConnectionCache);
                    }

                    headerWriter.Store();
                }
            }
            /* Fall through. If here then sync types are still pending
             * being written or were just written this frame. */
            return false;
        }


        /// <summary>
        /// Resets all SyncTypes for this NetworkBehaviour for server and client side.
        /// </summary>
        private void ResetSyncTypes()
        {
            foreach (var item in _syncVars.Values)
                item.Reset();
            foreach (var item in _syncObjects.Values)
                item.Reset();

            _syncObjectDirty = false;
            _syncVarDirty = false;
        }


        /// <summary>
        /// Resets all SyncTypes for this NetworkBehaviour.
        /// </summary>
        internal void ResetSyncTypes(bool asServer)
        {
            if (!asServer && IsServer) return;
            foreach (var item in _syncVars.Values)
                item.Reset();
            foreach (var item in _syncObjects.Values)
                item.Reset();
        }

        /// <summary>
        /// Writers syncVars for a spawn message.
        /// </summary>
        internal void WriteSyncTypesForSpawn(PooledWriter writer, SyncTypeWriteType writeType)
        {
            //Write for owner if writing all or owner, but not observers.
            var ownerWrite = (writeType != SyncTypeWriteType.Observers);
            WriteSyncType(_syncVars);
            WriteSyncType(_syncObjects);
            return;

            void WriteSyncType(Dictionary<uint, SyncBase> collection)
            {
                var syncTypeWriter = WriterPool.Retrieve();
                /* Since all values are being written everything is
                 * written in order so there's no reason to pass
                 * indexes. */
                foreach (var sb in collection.Values)
                {
                    //If not for owner and sync var is owner only.
                    if (!ownerWrite && sb.Settings.ReadPermission == ReadPermission.OwnerOnly)
                    {
                        //If there is an owner then skip.
                        if (_networkObjectCache.Owner.IsValid)
                            continue;
                    }

                    sb.WriteFull(syncTypeWriter);
                }

                writer.WriteBytesAndSize(syncTypeWriter.GetBuffer(), 0, syncTypeWriter.Length);
                syncTypeWriter.Store();
            }
        }


        /// <summary>
        /// Manually marks a SyncType as dirty, be it SyncVar or SyncObject.
        /// </summary>
        /// <param name="syncType">SyncType variable to dirty.</param>
        protected void DirtySyncType(object syncType)
        {
            /* This doesn't actually do anything.
             * The codegen replaces calls to this method
             * with a Dirty call for syncType. */
        }
    }
}