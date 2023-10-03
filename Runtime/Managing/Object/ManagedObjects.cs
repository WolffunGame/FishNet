using FishNet.Component.Observing;
using FishNet.Connection;
using FishNet.Managing.Utility;
using FishNet.Object;
using FishNet.Serializing;
using FishNet.Transporting;
using FishNet.Utility.Extension;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.SceneManagement;
using UnityEngine;
using FishNet.Managing.Logging;

namespace FishNet.Managing.Object
{
    public abstract partial class ManagedObjects
    {
        #region Static

        private static Scene _delayDestroyScene;

        private static Scene DelayDestroyScene
        {
            get
            {
                if (string.IsNullOrEmpty(_delayDestroyScene.name))
                    _delayDestroyScene = SceneManager.CreateScene("DelayedDestroyTemp");
                return _delayDestroyScene;
            }
        }

        #endregion

        #region Public.

        /// <summary>
        /// NetworkObjects which are currently active.
        /// </summary>
        public readonly Dictionary<int, NetworkObject> Spawned = new();

        /// <summary>
        /// NetworkObjects which are currently active on the local client.
        /// TODO Move this to ClientObjects.
        /// </summary>
        public readonly List<NetworkObject> LocalClientSpawned = new();

        #endregion

        #region Protected.

        /// <summary>
        /// Returns the next ObjectId to use.
        /// </summary>
        protected internal virtual int GetNextNetworkObjectId(bool errorCheck = true) =>
            NetworkObject.UNSET_OBJECTID_VALUE;

        /// <summary>
        /// NetworkManager handling this.
        /// </summary>
        protected NetworkManager NetworkManager { get; private set; }

        /// <summary>
        /// Objects in currently loaded scenes. These objects can be active or inactive.
        /// Key is the objectId while value is the object. Key is not the same as NetworkObject.ObjectId.
        /// </summary>
        protected readonly Dictionary<ulong, NetworkObject> SceneObjects = new();

        #endregion

        // ReSharper disable once VirtualMemberNeverOverridden.Global
        protected virtual void Initialize(NetworkManager manager)
        {
            NetworkManager = manager;
            manager.GetInstance<HashGrid>(false);
        }

        /// <summary>
        /// Subscribes to SceneManager.SceneLoaded event.
        /// </summary>
        /// <param name="subscribe"></param>
        internal void SubscribeToSceneLoaded(bool subscribe)
        {
            if (subscribe)
                SceneManager.sceneLoaded += SceneManager_sceneLoaded;
            else
                SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
        }

        /// <summary>
        /// Called when a scene is loaded.
        /// </summary>
        /// <param name="s"></param>
        /// <param name="arg1"></param>
        protected virtual void SceneManager_sceneLoaded(Scene s, LoadSceneMode arg1)
        {
        }

        /// <summary>
        /// Called when a NetworkObject runs Deactivate.
        /// </summary>
        /// <param name="nob"></param>
        /// <param name="asServer"></param>
        internal virtual void NetworkObjectUnexpectedlyDestroyed(NetworkObject nob, bool asServer)
        {
            if (nob == null)
                return;
            RemoveFromSpawned(nob, true, asServer);
        }

        /// <summary>
        /// Removes a NetworkedObject from spawned.
        /// </summary>
        private void RemoveFromSpawned(NetworkObject nob, bool unexpectedlyDestroyed, bool asServer)
        {
            Spawned.Remove(nob.ObjectId);
            if (!asServer)
                LocalClientSpawned.Remove(nob);
            //Do the same with SceneObjects.
            if (unexpectedlyDestroyed && nob.IsSceneObject)
                RemoveFromSceneObjects(nob);
            if (nob.transform == nob.transform.root)
                SceneManager.MoveGameObjectToScene(nob.gameObject, DelayDestroyScene);
        }

        /// <summary>
        /// DeSpawns a NetworkObject.
        /// </summary>
        internal virtual void Despawn(NetworkObject nob, DespawnType despawnType, bool asServer)
        {
            if (nob == null)
            {
                NetworkManager.LogWarning($"Cannot despawn a null NetworkObject.");
                return;
            }

            //True if should be destroyed, false if deactivated.
            bool destroy = false;
            /* Only modify object state if asServer,
             * or !asServer and not host. This is so clients, when acting as
             * host, don't destroy objects they lost observation of. */

            /* Nested prefabs can never be destroyed. Only check to
             * destroy if not nested. By nested prefab, this means the object
             * DeSpawning is part of another prefab that is also a spawned
             * network object. */
            if (!nob.IsNested)
            {
                //If as server.
                if (asServer)
                {
                    //Scene object.
                    if (!nob.IsSceneObject)
                    {
                        /* If client-host has visibility
                         * then disable and wait for client-host to get destroy
                         * message. Otherwise destroy immediately. */
                        if (nob.Observers.Contains(NetworkManager.ClientManager.Connection))
                            NetworkManager.ServerManager.Objects.AddToPending(nob);
                        else
                            destroy = true;
                    }
                }
                //Not as server.
                else
                {
                    bool isServer = NetworkManager.IsServer;
                    //Only check to destroy if not a scene object.
                    if (!nob.IsSceneObject)
                    {
                        /* If was removed from pending then also destroy.
                         * Pending objects are ones that exist on the server
                         * side only to await destruction from client side.
                         * Objects can also be destroyed if server is not
                         * active. */
                        destroy = (!isServer || NetworkManager.ServerManager.Objects.RemoveFromPending(nob.ObjectId));
                    }
                }
            }

            //Deinitialize to invoke callbacks.
            nob.Deinitialize(asServer);
            //Remove from match condition only if server.
            if (asServer)
                MatchCondition.RemoveFromMatchWithoutRebuild(nob, NetworkManager);
            RemoveFromSpawned(nob, false, asServer);

            //If to destroy.
            if (destroy)
            {
                if (despawnType == DespawnType.Destroy)
                    UnityEngine.Object.Destroy(nob.gameObject);
                else
                    NetworkManager.StorePooledInstantiated(nob, asServer);
            }
            /* If to potentially disable instead of destroy.
             * This is such as something is DeSpawning server side
             * but a clientHost is present, or if a scene object. */
            else
            {
                //If as server.
                if (asServer)
                {
                    //If not clientHost then the object can be disabled.
                    if (!NetworkManager.IsClient)
                        nob.gameObject.SetActive(false);
                }
                //Not as server.
                else
                {
                    //If the server is not active then the object can be disabled.
                    if (!NetworkManager.IsServer)
                    {
                        nob.gameObject.SetActive(false);
                    }
                    //If also server then checks must be done.
                    else
                    {
                        /* Object is still spawned on the server side. This means
                         * the clientHost likely lost visibility. When this is the case
                         * update clientHost renderers. */
                        if (NetworkManager.ServerManager.Objects.Spawned.ContainsKey(nob.ObjectId))
                            nob.SetRenderersVisible(false);
                        /* No longer spawned on the server, can
                         * deactivate on the client. */
                        else
                            nob.gameObject.SetActive(false);
                    }
                }

                /* Also despawn child objects.
                 * This only must be done when not destroying
                 * as destroying would result in the despawn being
                 * forced.
                 *
                 * Only run if asServer as well. The server will send
                 * individual DeSpawns for each child. */
                if (!asServer) return;
                foreach (var childNob in nob.ChildNetworkObjects)
                    if (childNob != null && !childNob.IsDeinitializing)
                        Despawn(childNob, despawnType, true);
            }
        }


        /// <summary>
        /// Updates NetworkBehaviours on nob.
        /// </summary>
        /// <param name="nob"></param>
        /// <param name="asServer"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void UpdateNetworkBehavioursForSceneObject(NetworkObject nob, bool asServer)
        {
            //Would have already been done on server side.
            if (!asServer && NetworkManager.IsServer)
                return;
            InitializePrefab(nob, -1);
        }

        /// <summary>
        /// Initializes a prefab, not to be mistaken for initializing a spawned object.
        /// </summary>
        /// <param name="prefab">Prefab to initialize.</param>
        /// <param name="index">Index within spawnable prefabs.</param>
        /// <param name="collectionId"></param>
        public static void InitializePrefab(NetworkObject prefab, int index, ushort? collectionId = null)
        {
            if (prefab == null)
                return;
            /* Only set the Id if not -1.
             * A value of -1 would indicate it's a scene
             * object. */
            if (index != -1)
            {
                //Use +1 because 0 indicates unset.
                prefab.PrefabId = (ushort)index;
                if (collectionId != null)
                    prefab.SpawnableCollectionId = collectionId.Value;
            }

            byte componentIndex = 0;
            prefab.UpdateNetworkBehaviours(null, ref componentIndex);
        }

        /// <summary>
        /// DeSpawns Spawned NetworkObjects. Scene objects will be disabled, others will be destroyed.
        /// </summary>
        internal virtual void DespawnWithoutSynchronization(bool asServer)
        {
            foreach (var nob in Spawned.Values)
            {
                if (nob == null)
                    continue;
                DespawnWithoutSynchronization(nob, asServer, nob.GetDefaultDespawnType(), false);
            }

            Spawned.Clear();
        }

        /// <summary>
        /// DeSpawns a network object.
        /// </summary>
        /// <param name="nob"></param>
        /// <param name="asServer"></param>
        /// <param name="despawnType"></param>
        /// <param name="removeFromSpawned"></param>
        protected virtual void DespawnWithoutSynchronization(NetworkObject nob, bool asServer, DespawnType despawnType,
            bool removeFromSpawned)
        {
            //Null can occur when running as host and server already DeSpawns such as when stopping.
            if (nob == null)
                return;

            nob.Deinitialize(asServer);
            /* Only run if asServer, or not
             * asServer and server isn't running. This
             * prevents objects from affecting the server
             * as host when being modified client side. */
            if (!asServer && NetworkManager.IsServer) return;
            if (removeFromSpawned)
                RemoveFromSpawned(nob, false, asServer);
            if (nob.IsSceneObject)
            {
                nob.gameObject.SetActive(false);
            }
            else
            {
                if (despawnType == DespawnType.Destroy)
                    UnityEngine.Object.Destroy(nob.gameObject);
                else
                    NetworkManager.StorePooledInstantiated(nob, asServer);
            }
        }

        /// <summary>
        /// Adds a NetworkObject to Spawned.
        /// </summary>
        /// <param name="nob"></param>
        /// <param name="asServer"></param>
        internal void AddToSpawned(NetworkObject nob, bool asServer)
        {
            Spawned[nob.ObjectId] = nob;
            if (asServer) return;
            LocalClientSpawned.Add(nob);
            //If being added as client and is also server.
            if (NetworkManager.IsServer)
                nob.SetRenderersVisible(true);
        }

        /// <summary>
        /// Adds a NetworkObject to SceneObjects.
        /// </summary>
        /// <param name="nob"></param>
        protected void AddToSceneObjects(NetworkObject nob) => SceneObjects[nob.SceneId] = nob;

        /// <summary>
        /// Removes a NetworkObject from SceneObjects.
        /// </summary>
        /// <param name="nob"></param>
        private void RemoveFromSceneObjects(NetworkObject nob) => SceneObjects.Remove(nob.SceneId);

        /// <summary>
        /// Finds a NetworkObject within Spawned.
        /// </summary>
        /// <param name="objectId"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // ReSharper disable once UnusedMember.Global
        protected internal NetworkObject GetSpawnedNetworkObject(int objectId)
        {
            if (!Spawned.TryGetValueIL2CPP(objectId, out var r))
                NetworkManager.LogError($"Spawned NetworkObject not found for ObjectId {objectId}.");
            return r;
        }

        /// <summary>
        /// Tries to skip data length for a packet.
        /// </summary>
        /// <param name="packetId"></param>
        /// <param name="reader"></param>
        /// <param name="dataLength"></param>
        /// <param name="rpcLinkObjectId"></param>
        protected void SkipDataLength(ushort packetId, PooledReader reader, int dataLength,
            int rpcLinkObjectId = -1)
        {
            switch (dataLength)
            {
                /* -1 means length wasn't set, which would suggest a reliable packet.
                 * Object should never be missing for reliable packets since spawns
                 * and deSpawns are reliable in order. */
                case (int)MissingObjectPacketLength.Reliable:
                {
                    string msg;
                    var isRpcLink = (packetId >= NetworkManager.StartingRpcLinkIndex);
                    if (isRpcLink)
                    {
                        msg = (rpcLinkObjectId == -1)
                            ? $"RPCLink of Id {(PacketId)packetId} could not be found. Remaining data will be purged."
                            : $"ObjectId {rpcLinkObjectId} for RPCLink {(PacketId)packetId} could not be found.";
                    }
                    else
                    {
                        msg =
                            $"NetworkBehaviour could not be found for packetId {(PacketId)packetId}. Remaining data will be purged.";
                    }

                    /* Default logging for server is errors only. Use error on client and warning
                     * on servers to reduce chances of allocation attacks. */
#if DEVELOPMENT_BUILD || UNITY_EDITOR || !UNITY_SERVER
                    NetworkManager.LogError(msg);
#else
                if (NetworkManager.CanLog(LoggingType.Warning))
                    Debug.LogWarning(msg);
#endif
                    reader.Clear();
                    break;
                }
                /* If length is known then is unreliable packet. It's possible
                 * this packetId arrived before or after the object was spawned/destroyed.
                 * Skip past the data for this packet and use rest in reader. With non-linked
                 * RPCs length is sent before object information. */
                case >= 0:
                    reader.Skip(Math.Min(dataLength, reader.Remaining));
                    break;
                /* -2 indicates the length is very long. Don't even try saving
                 * the packet, user shouldn't be sending this much data over unreliable. */
                case (int)MissingObjectPacketLength.PurgeRemaiming:
                    reader.Clear();
                    break;
            }
        }

        /// <summary>
        /// Parses a ReplicateRpc.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ParseReplicateRpc(PooledReader reader, NetworkConnection conn, Channel channel)
        {
            NetworkBehaviour nb = reader.ReadNetworkBehaviour();
            int dataLength = Packets.GetPacketLength((ushort)PacketId.ServerRpc, reader, channel);
            if (nb != null)
                nb.OnReplicateRpc(null, reader, conn, channel);
            else
                SkipDataLength((ushort)PacketId.ServerRpc, reader, dataLength);
        }
    }
}