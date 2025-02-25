﻿using FishNet.Connection;
using FishNet.Managing.Logging;
using FishNet.Managing.Object;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Serializing;
using FishNet.Transporting;
using FishNet.Utility;
using FishNet.Utility.Extension;
using FishNet.Utility.Performance;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FishNet.Managing.Server
{
    /// <summary>
    /// Handles objects and information about objects for the server. See ManagedObjects for inherited options.
    /// </summary>
    public partial class ServerObjects : ManagedObjects
    {
        #region Public.
        /// <summary>
        /// Called right before client objects are destroyed when a client disconnects.
        /// </summary>
        public event Action<NetworkConnection> OnPreDestroyClientObjects;
        #endregion

        #region Internal.
        /// <summary>
        /// Collection of NetworkObjects recently despawned.
        /// Key: objectId.
        /// Value: despawn tick.
        /// This is used primarily to track if client is sending messages for recently despawned objects.
        /// Objects are automatically removed after RECENTLY_DESPAWNED_DURATION seconds.
        /// </summary>
        internal Dictionary<int, uint> RecentlyDespawnedIds = new Dictionary<int, uint>();
        #endregion

        #region Private.
        /// <summary>
        /// Cached ObjectIds which may be used when exceeding available ObjectIds.
        /// </summary>
        private Queue<int> _objectIdCache = new Queue<int>();
        /// <summary>
        /// Returns the ObjectId cache.
        /// </summary>
        /// <returns></returns>
        internal Queue<int> GetObjectIdCache() => _objectIdCache;
        /// <summary>
        /// NetworkBehaviours which have dirty SyncVars.
        /// </summary>
        private List<NetworkBehaviour> _dirtySyncVarBehaviours = new List<NetworkBehaviour>(20);
        /// <summary>
        /// NetworkBehaviours which have dirty SyncObjects.
        /// </summary>
        private List<NetworkBehaviour> _dirtySyncObjectBehaviours = new List<NetworkBehaviour>(20);
        /// <summary>
        /// Objects which need to be destroyed next tick.
        /// This is needed when running as host so host client will get any final messages for the object before they're destroyed.
        /// </summary>
        private Dictionary<int, NetworkObject> _pendingDestroy = new Dictionary<int, NetworkObject>();
        /// <summary>
        /// Scenes which were loaded that need to be setup.
        /// </summary>
        private List<(int, Scene)> _loadedScenes = new List<(int frame, Scene scene)>();
        /// <summary>
        /// Cache of spawning objects, used for recursively spawning nested NetworkObjects.
        /// </summary>
        private List<NetworkObject> _spawnCache = new List<NetworkObject>();
        /// <summary>
        /// True if one or more scenes are currently loading through the SceneManager.
        /// </summary>
        private bool _scenesLoading;
        /// <summary>
        /// Number of ticks which must pass to clear a recently despawned.
        /// </summary>
        private uint _cleanRecentlyDespawnedMaxTicks => base.NetworkManager.TimeManager.TimeToTicks(30d, TickRounding.RoundUp);
        #endregion

        internal ServerObjects(NetworkManager networkManager)
        {
            base.Initialize(networkManager);
            networkManager.SceneManager.OnLoadStart += SceneManager_OnLoadStart;
            networkManager.SceneManager.OnActiveSceneSetInternal += SceneManager_OnActiveSceneSet;
            networkManager.TimeManager.OnUpdate += TimeManager_OnUpdate;
        }

        /// <summary>
        /// Called when MonoBehaviours call Update.
        /// </summary>
        private void TimeManager_OnUpdate()
        {
            if (!base.NetworkManager.IsServer)
            {
                _scenesLoading = false;
                _loadedScenes.Clear();
                return;
            }

            CleanRecentlyDespawned();

            if (!_scenesLoading)
                IterateLoadedScenes(false);
            Observers_OnUpdate();
        }

        #region Checking dirty SyncTypes.
        /// <summary>
        /// Iterates NetworkBehaviours with dirty SyncTypes.
        /// </summary>
        internal void WriteDirtySyncTypes()
        {
            /* Tells networkbehaviours to check their
             * dirty synctypes. */
            IterateCollection(_dirtySyncVarBehaviours, false);
            IterateCollection(_dirtySyncObjectBehaviours, true);

            void IterateCollection(List<NetworkBehaviour> collection, bool isSyncObject)
            {
                for (var i = 0; i < collection.Count; i++)
                {
                    var dirtyCleared = collection[i].WriteDirtySyncTypes(isSyncObject);
                    if (dirtyCleared)
                    {
                        collection.RemoveAt(i);
                        i--;
                    }
                }
            }
        }
        /// <summary>
        /// Sets that a NetworkBehaviour has a dirty syncVars.
        /// </summary>
        /// <param name="nb"></param>
        internal void SetDirtySyncType(NetworkBehaviour nb, bool isSyncObject)
        {
            if (isSyncObject)
                _dirtySyncObjectBehaviours.Add(nb);
            else
                _dirtySyncVarBehaviours.Add(nb);
        }
        #endregion

        #region Connection Handling.
        /// <summary>
        /// Called when the connection state changes for the local server.
        /// </summary>
        /// <param name="args"></param>
        internal void OnServerConnectionState(ServerConnectionStateArgs args)
        {

            //If server just connected.
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                /* If there's no servers started besides the one
                 * that just started then build Ids and setup scene objects. */
                if (base.NetworkManager.ServerManager.OneServerStarted())
                {
                    BuildObjectIdCache();
                    SetupSceneObjects();
                }
            }
            //Server in anything but started state.
            else
            {
                //If no servers are started then reset.
                if (!base.NetworkManager.ServerManager.AnyServerStarted())
                {
                    base.DespawnWithoutSynchronization(true);
                    base.SceneObjects.Clear();
                    _objectIdCache.Clear();
                    base.NetworkManager.ClearClientsCollection(base.NetworkManager.ServerManager.Clients);
                }
                //If at least one server is started then only clear for disconnecting server.
                else
                {
                    //Remove connections only for transportIndex.
                    base.NetworkManager.ClearClientsCollection(base.NetworkManager.ServerManager.Clients, args.TransportIndex);
                }
            }
        }

        /// <summary>
        /// Called when a client disconnects.
        /// </summary>
        /// <param name="connection"></param>
        internal void ClientDisconnected(NetworkConnection connection)
        {
            RemoveFromObserversWithoutSynchronization(connection);

            OnPreDestroyClientObjects?.Invoke(connection);

            /* A cache is made because the Objects
             * collection would end up modified during
             * iteration from removing ownership and despawning. */
            var nobs = CollectionCaches<NetworkObject>.RetrieveList();
            foreach (var nob in connection.Objects)
                nobs.Add(nob);

            var nobsCount = nobs.Count;
            for (var i = 0; i < nobsCount; i++)
            {
                /* Objects may already be deinitializing when a client disconnects
                 * because the root object could have been despawned first, and in result
                 * all child objects would have been recursively despawned. 
                 * 
                 * EG: object is:
                 *      A (nob)
                 *          B (nob)
                 * 
                 * Both A and B are owned by the client so they will both be
                 * in collection. Should A despawn first B will recursively despawn
                 * from it. Then once that finishes and the next index of collection
                 * is run, which would B, the object B would have already been deinitialized. */
                if (!nobs[i].IsDeinitializing)
                    base.NetworkManager.ServerManager.Despawn(nobs[i]);
            }

            CollectionCaches<NetworkObject>.Store(nobs);
        }
        #endregion

        #region ObjectIds.
        /// <summary>
        /// Builds the ObjectId cache with all possible Ids.
        /// </summary>
        private void BuildObjectIdCache()
        {
            _objectIdCache.Clear();

            /* Shuffle Ids to make it more difficult
             * for clients to track spawned object
             * count. */
            var shuffledCache = new List<int>();
            //Ignore ushort.maxvalue as that indicates null.
            for (var i = 0; i < (ushort.MaxValue - 1); i++)
                shuffledCache.Add(i);
            /* Only shuffle when NOT in editor and not
             * development build.
             * Debugging could be easier when Ids are ordered. */
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
            shuffledCache.Shuffle();
#endif
            //Add shuffled to objectIdCache.
            //Build Id cache.
            var cacheCount = shuffledCache.Count;
            for (var i = 0; i < cacheCount; i++)
                _objectIdCache.Enqueue(shuffledCache[i]);
        }
        /// <summary>
        /// Caches a NetworkObject ObjectId.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CacheObjectId(NetworkObject nob)
        {
            if (nob.ObjectId != NetworkObject.UNSET_OBJECTID_VALUE)
                CacheObjectId(nob.ObjectId);
        }
        /// <summary>
        /// Adds an ObjectId to objectId cache.
        /// </summary>
        /// <param name="id"></param>
        internal void CacheObjectId(int id)
        {
            _objectIdCache.Enqueue(id);
        }

        /// <summary>
        /// Gets the next ObjectId to use for NetworkObjects.
        /// </summary>
        /// <returns></returns>
        protected internal override int GetNextNetworkObjectId(bool errorCheck = true)
        {
            //Either something went wrong or user actually managed to spawn ~64K networked objects.
            if (_objectIdCache.Count == 0)
            {
                base.NetworkManager.LogError($"No more available ObjectIds. How the heck did you manage to have {ushort.MaxValue} objects spawned at once?");
                return -1;
            }
            else
            {
                return _objectIdCache.Dequeue();
            }
        }
        #endregion

        #region Initializing Objects In Scenes.
        /// <summary>
        /// Called when a scene load starts.
        /// </summary>
        private void SceneManager_OnLoadStart(Scened.SceneLoadStartEventArgs obj)
        {
            _scenesLoading = true;
        }
        /// <summary>
        /// Called after the active scene has been scene, immediately after scene loads.
        /// </summary>
        private void SceneManager_OnActiveSceneSet()
        {
            _scenesLoading = false;
            IterateLoadedScenes(true);
        }
        /// <summary>
        /// Iterates loaded scenes and sets them up.
        /// </summary>
        /// <param name="ignoreFrameRestriction">True to ignore the frame restriction when iterating.</param>
        internal void IterateLoadedScenes(bool ignoreFrameRestriction)
        {
            //Not started, clear loaded scenes.
            if (!NetworkManager.ServerManager.Started)
                _loadedScenes.Clear();

            for (var i = 0; i < _loadedScenes.Count; i++)
            {
                (int frame, Scene scene) value = _loadedScenes[i];
                if (ignoreFrameRestriction || (Time.frameCount > value.frame))
                {
                    SetupSceneObjects(value.scene);
                    _loadedScenes.RemoveAt(i);
                    i--;
                }
            }
        }

        /// <summary>
        /// Called when a scene loads on the server.
        /// </summary>
        /// <param name="s"></param>
        /// <param name="arg1"></param>
        protected override void SceneManager_sceneLoaded(Scene s, LoadSceneMode arg1)
        {
            base.SceneManager_sceneLoaded(s, arg1);

            if (!NetworkManager.ServerManager.Started)
                return;
            //Add to loaded scenes so that they are setup next frame.
            _loadedScenes.Add((Time.frameCount, s));
        }

        /// <summary>
        /// Setup all NetworkObjects in scenes. Should only be called when server is active.
        /// </summary>
        protected void SetupSceneObjects()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
                SetupSceneObjects(SceneManager.GetSceneAt(i));

            var ddolScene = DDOLFinder.GetDDOL().gameObject.scene;
            if (ddolScene.isLoaded)
                SetupSceneObjects(ddolScene);
        }

        /// <summary>
        /// Setup NetworkObjects in a scene. Should only be called when server is active.
        /// </summary>
        /// <param name="s"></param>
        private void SetupSceneObjects(Scene s)
        {
            if (!s.IsValid())
                return;

            var nobs = CollectionCaches<NetworkObject>.RetrieveList();
            SceneFN.GetSceneNetworkObjects(s, false, ref nobs);

            var isHost = base.NetworkManager.IsHost;
            var nobsCount = nobs.Count;
            for (var i = 0; i < nobsCount; i++)
            {
                var nob = nobs[i];
                //Only setup if a scene object and not initialzied.
                if (nob.IsNetworked && nob.IsSceneObject && nob.IsDeinitializing)
                {
                    base.UpdateNetworkBehavioursForSceneObject(nob, true);
                    base.AddToSceneObjects(nob);
                    /* If was active in the editor (before hitting play), or currently active
                     * then PreInitialize without synchronizing to clients. There is no reason
                     * to synchronize to clients because the scene just loaded on server,
                     * which means clients are not yet in the scene. */
                    if (nob.ActiveDuringEdit || nob.gameObject.activeInHierarchy)
                    {
                        //If not host then object doesn't need to be spawned until a client joins.
                        if (!isHost)
                            SetupWithoutSynchronization(nob);
                        //Otherwise spawn object so observers update for clientHost.
                        else
                            SpawnWithoutChecks(nob);
                    }
                }
            }

            CollectionCaches<NetworkObject>.Store(nobs);
        }

        /// <summary>
        /// Performs setup on a NetworkObject without synchronizing the actions to clients.
        /// </summary>
        /// <param name="objectId">Override ObjectId to use.</param>
        private void SetupWithoutSynchronization(NetworkObject nob, NetworkConnection ownerConnection = null, int? objectId = null)
        {
            if (nob.IsNetworked)
            {
                if (objectId == null)
                    objectId = GetNextNetworkObjectId();
                nob.Preinitialize_Internal(NetworkManager, objectId.Value, ownerConnection, true);
                base.AddToSpawned(nob, true);
                nob.gameObject.SetActive(true);
                nob.Initialize(true, true);
            }
        }
        #endregion

        #region Spawning.
        /// <summary>
        /// Spawns an object over the network.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Spawn(NetworkObject networkObject, NetworkConnection ownerConnection = null)
        {
            //Default as false, will change if needed.
            var predictedSpawn = false;

            if (networkObject == null)
            {
                base.NetworkManager.LogError($"Specified networkObject is null.");
                return;
            }
            if (!NetworkManager.ServerManager.Started)
            {
                //Neither server nor client are started.
                if (!NetworkManager.ClientManager.Started)
                {
                    base.NetworkManager.LogWarning("Cannot spawn object because server nor client are active.");
                    return;
                }
                //Server has predicted spawning disabled.
                if (!NetworkManager.PredictionManager.GetAllowPredictedSpawning())
                {
                    base.NetworkManager.LogWarning("Cannot spawn object because server is not active and predicted spawning is not enabled.");
                    return;
                }
                //Various predicted spawn checks.
                if (!base.CanPredictedSpawn(networkObject, NetworkManager.ClientManager.Connection, ownerConnection, false))
                    return;

                predictedSpawn = true;
            }
            if (!networkObject.gameObject.scene.IsValid())
            {
                base.NetworkManager.LogError($"{networkObject.name} is a prefab. You must instantiate the prefab first, then use Spawn on the instantiated copy.");
                return;
            }
            if (ownerConnection != null && ownerConnection.IsActive && !ownerConnection.LoadedStartScenes(!predictedSpawn))
            {
                base.NetworkManager.LogWarning($"{networkObject.name} was spawned but it's recommended to not spawn objects for connections until they have loaded start scenes. You can be notified when a connection loads start scenes by using connection.OnLoadedStartScenes on the connection, or SceneManager.OnClientLoadStartScenes.");
            }
            if (networkObject.IsSpawned)
            {
                base.NetworkManager.LogWarning($"{networkObject.name} is already spawned.");
                return;
            }
            if (networkObject.ParentNetworkObject != null && !networkObject.ParentNetworkObject.IsSpawned)
            {
                base.NetworkManager.LogError($"{networkObject.name} cannot be spawned because it has a parent NetworkObject {networkObject.ParentNetworkObject} which is not spawned.");
                return;
            }

            if (predictedSpawn)
                base.NetworkManager.ClientManager.Objects.PredictedSpawn(networkObject, ownerConnection);
            else
                SpawnWithoutChecks(networkObject, ownerConnection);
        }

        /// <summary>
        /// Spawns networkObject without any checks.
        /// </summary>
        private void SpawnWithoutChecks(NetworkObject networkObject, NetworkConnection ownerConnection = null, int? objectId = null)
        {
            /* Setup locally without sending to clients.
            * When observers are built for the network object
            * during initialization spawn messages will
            * be sent. */
            networkObject.SetIsNetworked(true);
            _spawnCache.Add(networkObject);
            SetupWithoutSynchronization(networkObject, ownerConnection, objectId);

            foreach (var item in networkObject.ChildNetworkObjects)
            {
                /* Only spawn recursively if the nob state is unset.
                 * Unset indicates that the nob has not been */
                if (item.gameObject.activeInHierarchy || item.State == NetworkObjectState.Spawned)
                    SpawnWithoutChecks(item, ownerConnection);
            }

            /* Copy to a new cache then reset _spawnCache
             * just incase rebuilding observers would lead to 
             * more additions into _spawnCache. EG: rebuilding
             * may result in additional objects being spawned
             * for clients and if _spawnCache were not reset
             * the same objects would be rebuilt again. This likely
             * would not affect anything other than perf but who
             * wants that. */
            var spawnCacheCopy = CollectionCaches<NetworkObject>.RetrieveList();
            spawnCacheCopy.AddRange(_spawnCache);
            _spawnCache.Clear();
            //Also rebuild observers for the object so it spawns for others.
            RebuildObservers(spawnCacheCopy);

            var spawnCacheCopyCount = spawnCacheCopy.Count;
            /* If also client then we need to make sure the object renderers have correct visibility.
             * Set visibility based on if the observers contains the clientHost connection. */
            if (NetworkManager.IsClient)
            {
                var count = spawnCacheCopyCount;
                for (var i = 0; i < count; i++)
                    spawnCacheCopy[i].SetRenderersVisible(networkObject.Observers.Contains(NetworkManager.ClientManager.Connection));
            }

            CollectionCaches<NetworkObject>.Store(spawnCacheCopy);
        }

        /// <summary>
        /// Reads a predicted spawn.
        /// </summary>
        internal void ReadPredictedSpawn(PooledReader reader, NetworkConnection conn)
        {
            sbyte initializeOrder;
            ushort collectionId;
            int prefabId;
            var objectId = reader.ReadNetworkObjectForSpawn(out initializeOrder, out collectionId, out _);
            //If objectId is not within predicted ids for conn.
            if (!conn.PredictedObjectIds.Contains(objectId))
            {
                reader.Clear();
                conn.Kick(KickReason.ExploitAttempt, LoggingType.Common, $"Connection {conn.ClientId} used predicted spawning with a non-reserved objectId of {objectId}.");
                return;
            }

            var owner = reader.ReadNetworkConnection();
            var st = (SpawnType)reader.ReadByte();
            //Not used at the moment.
            var componentIndex = reader.ReadByte();

            //Read transform values which differ from serialized values.
            Vector3? localPosition;
            Quaternion? localRotation;
            Vector3? localScale;
            base.ReadTransformProperties(reader, out localPosition, out localRotation, out localScale);

            NetworkObject nob;
            var isGlobal = false;
            if (SpawnTypeEnum.Contains(st, SpawnType.Scene))
            {
                var sceneId = reader.ReadUInt64(AutoPackType.Unpacked);
                nob = base.GetSceneNetworkObject(sceneId);
                if (!base.CanPredictedSpawn(nob, conn, owner, true))
                    return;
            }
            else
            {
                //Not used right now.
                var spt = (SpawnParentType)reader.ReadByte();
                prefabId = reader.ReadNetworkObjectId();
                //Invalid prefabId.
                if (prefabId == NetworkObject.UNSET_PREFABID_VALUE)
                {
                    reader.Clear();
                    conn.Kick(KickReason.UnusualActivity, LoggingType.Common, $"Spawned object has an invalid prefabId of {prefabId}. Make sure all objects which are being spawned over the network are within SpawnableObjects on the NetworkManager. Connection {conn.ClientId} will be kicked immediately.");
                    return;
                }

                var prefabObjects = NetworkManager.GetPrefabObjects<PrefabObjects>(collectionId, false);
                //PrefabObjects not found.
                if (prefabObjects == null)
                {
                    reader.Clear();
                    conn.Kick(KickReason.UnusualActivity, LoggingType.Common, $"PrefabObjects collection is not found for CollectionId {collectionId}. Be sure to add your addressables NetworkObject prefabs to the collection on server and client before attempting to spawn them over the network. Connection {conn.ClientId} will be kicked immediately.");
                    return;
                }
                //Check if prefab allows predicted spawning.
                var nPrefab = prefabObjects.GetObject(true, prefabId);
                if (!base.CanPredictedSpawn(nPrefab, conn, owner, true))
                    return;

                nob = NetworkManager.GetPooledInstantiated(prefabId, collectionId, false);
                isGlobal = SpawnTypeEnum.Contains(st, SpawnType.InstantiatedGlobal);
            }

            var t = nob.transform;
            //Parenting predicted spawns is not supported yet.
            t.SetParent(null, true);
            base.GetTransformProperties(localPosition, localRotation, localScale, t, out var pos, out var rot, out var scale);
            t.SetLocalPositionRotationAndScale(pos, rot, scale);
            nob.SetIsGlobal(isGlobal);

            //Initialize for prediction.
            nob.InitializePredictedObject_Server(base.NetworkManager, conn);

            /* Only read sync types if allowed for the object.
             * If the client did happen to send synctypes while not allowed
             * this will create a parse error on the server,
             * resulting in the client being kicked. */
            if (nob.AllowPredictedSyncTypes)
            {
                var syncValues = reader.ReadArraySegmentAndSize();
                var syncTypeReader = ReaderPool.Retrieve(syncValues, base.NetworkManager);
                foreach (var nb in nob.NetworkBehaviours)
                {
                    //SyncVars.
                    var length = syncTypeReader.ReadInt32();
                    nb.OnSyncType(syncTypeReader, length, false, true);
                    //SyncObjects
                    length = syncTypeReader.ReadInt32();
                    nb.OnSyncType(syncTypeReader, length, true, true);
                }
                syncTypeReader.Store();
            }

            SpawnWithoutChecks(nob, owner, objectId);

            //Send the spawner a new reservedId.
            WriteResponse(true);
            //Writes a predicted spawn result to a client.
            void WriteResponse(bool success)
            {
                var writer = WriterPool.Retrieve();
                writer.WritePacketId(PacketId.PredictedSpawnResult);
                writer.WriteNetworkObjectId(nob.ObjectId);
                writer.WriteBoolean(success);

                if (success)
                {
                    var objectIdCache = NetworkManager.ServerManager.Objects.GetObjectIdCache();
                    //Write next objectId to use.
                    var invalidId = NetworkObject.UNSET_OBJECTID_VALUE;
                    var nextId = (objectIdCache.Count > 0) ? objectIdCache.Dequeue() : invalidId;
                    writer.WriteNetworkObjectId(nextId);
                    //If nextId is valid then also add it to spawners local cache.
                    if (nextId != invalidId)
                        conn.PredictedObjectIds.Enqueue(nextId);
                    ////Update RPC links.
                    //foreach (NetworkBehaviour nb in nob.NetworkBehaviours)
                    //    nb.WriteRpcLinks(writer);
                }

                conn.SendToClient((byte)Channel.Reliable, writer.GetArraySegment());
            }

        }
        #endregion

        #region Despawning.
        /// <summary>
        /// Cleans recently despawned objects.
        /// </summary>
        private void CleanRecentlyDespawned()
        {
            //Only iterate if frame ticked to save perf.
            if (!base.NetworkManager.TimeManager.FrameTicked)
                return;

            var intCache = CollectionCaches<int>.RetrieveList();

            var requiredTicks = _cleanRecentlyDespawnedMaxTicks;
            var currentTick = base.NetworkManager.TimeManager.LocalTick;
            //Iterate 20, or 5% of the collection, whichever is higher.
            var iterations = Mathf.Max(20, (int)(RecentlyDespawnedIds.Count * 0.05f));
            /* Given this is a dictionary there is no gaurantee which order objects are
             * added. Because of this it's possible some objects may take much longer to
             * be removed. This is okay so long as a consistent chunk of objects are removed
             * at a time; eventually all objects will be iterated. */
            var count = 0;
            foreach (var kvp in RecentlyDespawnedIds)
            {
                long result = (currentTick - kvp.Value);
                //If enough ticks have passed to remove.
                if (result > requiredTicks)
                    intCache.Add(kvp.Key);

                count++;
                if (count == iterations)
                    break;
            }

            //Remove cached entries.
            var cCount = intCache.Count;
            for (var i = 0; i < cCount; i++)
                RecentlyDespawnedIds.Remove(intCache[i]);

            CollectionCaches<int>.Store(intCache);
        }

        /// <summary>
        /// Returns if an objectId was recently despawned.
        /// </summary>
        /// <param name="objectId">ObjectId to check.</param>
        /// <param name="ticks">Passed ticks to be within to be considered recently despawned.</param>
        /// <returns>True if an objectId was despawned with specified number of ticks.</returns>
        public bool RecentlyDespawned(int objectId, uint ticks)
        {
            uint despawnTick;
            if (!RecentlyDespawnedIds.TryGetValue(objectId, out despawnTick))
                return false;

            return ((NetworkManager.TimeManager.LocalTick - despawnTick) <= ticks);
        }
        /// <summary>
        /// Adds to objects pending destroy due to clientHost environment.
        /// </summary>
        /// <param name="nob"></param>
        internal void AddToPending(NetworkObject nob)
        {
            _pendingDestroy[nob.ObjectId] = nob;
        }
        /// <summary>
        /// Tries to removes objectId from PendingDestroy and returns if successful.
        /// </summary>
        internal bool RemoveFromPending(int objectId)
        {
            return _pendingDestroy.Remove(objectId);
        }
        /// <summary>
        /// Returns a NetworkObject in PendingDestroy.
        /// </summary>
        internal NetworkObject GetFromPending(int objectId)
        {
            NetworkObject nob;
            _pendingDestroy.TryGetValue(objectId, out nob);
            return nob;
        }
        /// <summary>
        /// Destroys NetworkObjects pending for destruction.
        /// </summary>
        internal void DestroyPending()
        {
            foreach (var item in _pendingDestroy.Values)
            {
                if (item != null)
                    MonoBehaviour.Destroy(item.gameObject);
            }

            _pendingDestroy.Clear();
        }

        /// <summary>
        /// Despawns an object over the network.
        /// </summary>
        internal override void Despawn(NetworkObject networkObject, DespawnType despawnType, bool asServer)
        {
            //Default as false, will change if needed.
            var predictedDespawn = false;

            if (networkObject == null)
            {
                base.NetworkManager.LogWarning($"NetworkObject cannot be despawned because it is null.");
                return;
            }
            if (networkObject.IsDeinitializing)
            {
                base.NetworkManager.LogWarning($"Object {networkObject.name} cannot be despawned because it is already deinitializing.");
                return;
            }

            if (!NetworkManager.ServerManager.Started)
            {
                //Neither server nor client are started.
                if (!NetworkManager.ClientManager.Started)
                {
                    base.NetworkManager.LogWarning("Cannot despawn object because server nor client are active.");
                    return;
                }
                //Server has predicted spawning disabled.
                if (!NetworkManager.PredictionManager.GetAllowPredictedSpawning())
                {
                    base.NetworkManager.LogWarning("Cannot despawn object because server is not active and predicted spawning is not enabled.");
                    return;
                }
                //Various predicted despawn checks.
                if (!base.CanPredictedDespawn(networkObject, NetworkManager.ClientManager.Connection, false))
                    return;

                predictedDespawn = true;
            }
            if (!networkObject.gameObject.scene.IsValid())
            {
                base.NetworkManager.LogError($"{networkObject.name} is a prefab. You must instantiate the prefab first, then use Spawn on the instantiated copy.");
                return;
            }

            if (predictedDespawn)
            {
                base.NetworkManager.ClientManager.Objects.PredictedDespawn(networkObject);
            }
            else
            {
                FinalizeDespawn(networkObject, despawnType);
                RecentlyDespawnedIds[networkObject.ObjectId] = base.NetworkManager.TimeManager.LocalTick;
                base.Despawn(networkObject, despawnType, asServer);
            }
        }

        /// <summary>
        /// Called when a NetworkObject is destroyed without being deactivated first.
        /// </summary>
        /// <param name="nob"></param>
        internal override void NetworkObjectUnexpectedlyDestroyed(NetworkObject nob, bool asServer)
        {
            FinalizeDespawn(nob, DespawnType.Destroy);
            base.NetworkObjectUnexpectedlyDestroyed(nob, asServer);
        }

        /// <summary>
        /// Finalizes the despawn process. By the time this is called the object is considered unaccessible.
        /// </summary>
        /// <param name="nob"></param>
        private void FinalizeDespawn(NetworkObject nob, DespawnType despawnType)
        {
            if (nob != null && nob.ObjectId != NetworkObject.UNSET_OBJECTID_VALUE)
            {
                nob.WriteDirtySyncTypes();
                WriteDespawnAndSend(nob, despawnType);
                CacheObjectId(nob);
            }
        }

        /// <summary>
        /// Writes a despawn and sends it to clients.
        /// </summary>
        /// <param name="nob"></param>
        private void WriteDespawnAndSend(NetworkObject nob, DespawnType despawnType)
        {
            var everyoneWriter = WriterPool.Retrieve();
            WriteDespawn(nob, despawnType, everyoneWriter);

            var despawnSegment = everyoneWriter.GetArraySegment();

            //Add observers to a list cache.
            var cache = CollectionCaches<NetworkConnection>.RetrieveList();
            cache.AddRange(nob.Observers);
            var cacheCount = cache.Count;
            for (var i = 0; i < cacheCount; i++)
            {
                //Invoke ondespawn and send despawn.
                var conn = cache[i];
                nob.InvokeOnServerDespawn(conn);
                NetworkManager.TransportManager.SendToClient((byte)Channel.Reliable, despawnSegment, conn);
                //Remove from observers.
                //nob.Observers.Remove(conn);
            }

            everyoneWriter.Store();
            CollectionCaches<NetworkConnection>.Store(cache);
        }
        /// <summary>
        /// Reads a predicted despawn.
        /// </summary>
        internal void ReadPredictedDespawn(Reader reader, NetworkConnection conn)
        {
            var nob = reader.ReadNetworkObject();

            //Maybe server destroyed the object so don't kick if null.
            if (nob == null)
            {
                reader.Clear();
                return;
            }
            //Does not allow predicted despawning.
            if (!nob.AllowPredictedDespawning)
            {
                reader.Clear();
                conn.Kick(KickReason.ExploitAttempt, LoggingType.Common, $"Connection {conn.ClientId} used predicted despawning for object {nob.name} when it does not support predicted despawning.");
            }

            //Despawn object.
            nob.Despawn();
        }
        #endregion
    }


}
