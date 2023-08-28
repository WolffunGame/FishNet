using FishNet.Connection;
using FishNet.Managing.Client;
using FishNet.Managing.Logging;
using FishNet.Managing.Server;
using FishNet.Object;
using FishNet.Serializing.Helping;
using FishNet.Transporting;
using FishNet.Utility;
using FishNet.Utility.Extension;
using FishNet.Utility.Performance;
using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace FishNet.Managing.Scened
{
    /// <summary>
    /// Handles loading, unloading, and scene visibility for clients.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FishNet/Manager/SceneManager")]
    public sealed class SceneManager : MonoBehaviour
    {
        #region Types.

        internal enum LightProbeUpdateType
        {
            Asynchronous = 0,
            BlockThread = 1,
            Off = 2,
        }

        #endregion

        #region Public.

        /// <summary>
        /// Called after the active scene has been set, immediately after scene loads. This will occur before NetworkBehaviour callbacks run for the scene's objects.
        /// The boolean will indicate if the scene set active was specified by the user.
        /// </summary>
        public event Action<bool> OnActiveSceneSet;

        /// <summary>
        /// Called when a client loads initial scenes after connecting. Boolean will be true if asServer. This will invoke even if the SceneManager is not used when the client completes fully connecting to the server.
        /// </summary>
        public event Action<NetworkConnection, bool> OnClientLoadedStartScenes;

        /// <summary>
        /// Called when a scene change queue has begun. This will only call if a scene has succesfully begun to load or unload. The queue may process any number of scene events. For example: if a scene is told to unload while a load is still in progress, then the unload will be placed in the queue.
        /// </summary>
        public event Action OnQueueStart;

        /// <summary>
        /// Called when the scene queue is emptied.
        /// </summary>
        public event Action OnQueueEnd;

        /// <summary>
        /// Called when a scene load starts.
        /// </summary>
        public event Action<SceneLoadStartEventArgs> OnLoadStart;

        /// <summary>
        /// Called when completion percentage changes while loading a scene. Value is between 0f and 1f, while 1f is 100% done. Can be used for custom progress bars when loading scenes.
        /// </summary>
        public event Action<SceneLoadPercentEventArgs> OnLoadPercentChange;

        /// <summary>
        /// Called when a scene load ends.
        /// </summary>
        public event Action<SceneLoadEndEventArgs> OnLoadEnd;

        /// <summary>
        /// Called when a scene unload starts.
        /// </summary>
        public event Action<SceneUnloadStartEventArgs> OnUnloadStart;

        /// <summary>
        /// Called when a scene unload ends.
        /// </summary>
        public event Action<SceneUnloadEndEventArgs> OnUnloadEnd;

        /// <summary>
        /// Called when a client presence changes within a scene, before the server rebuilds observers.
        /// </summary>
        public event Action<ClientPresenceChangeEventArgs> OnClientPresenceChangeStart;

        /// <summary>
        /// Called when a client presence changes within a scene, after the server rebuilds observers.
        /// </summary>
        public event Action<ClientPresenceChangeEventArgs> OnClientPresenceChangeEnd;

        /// <summary>
        /// Connections within each scene.
        /// </summary>
        public Dictionary<Scene, HashSet<NetworkConnection>> SceneConnections { get; } = new();

        /// <summary>
        /// 
        /// </summary>
        [Tooltip(
            "Script to handle Addressable loading and unloading. This field may be blank if Addressable are not being used.")]
        [SerializeField]
        private SceneProcessorBase _sceneProcessor;

        /// <summary>
        /// Script to handle Addressable loading and unloading. This field may be blank if Addressable are not being used.
        /// </summary>
        /// <returns></returns>
        public SceneProcessorBase GetSceneProcessor() => _sceneProcessor;

        /// <summary>
        /// Sets the SceneProcessor to use.
        /// </summary>
        /// <param name="value"></param>
        public void SetSceneProcessor(SceneProcessorBase value) => _sceneProcessor = value;

        /// <summary>
        /// NetworkManager for this script.
        /// </summary>
        public NetworkManager NetworkManager { get; private set; }

        #endregion

        #region Internal.

        /// <summary>
        /// Called after the active scene has been set, immediately after scene loads.
        /// </summary>
        internal event Action OnActiveSceneSetInternal;

        #endregion

        #region Serialized.

        /// <summary>
        /// How to update light probes after loading or unloading scenes.
        /// </summary>
        [Tooltip("How to update light probes after loading or unloading scenes.")] [SerializeField]
        private LightProbeUpdateType _lightProbeUpdating = LightProbeUpdateType.Asynchronous;

        /// <summary>
        /// True to move objects visible to clientHost that are within an unloading scene. This ensures the objects are despawned on the client side rather than when the scene is destroyed.
        /// </summary>
        [Tooltip(
            "True to move objects visible to clientHost that are within an unloading scene. This ensures the objects are despawned on the client side rather than when the scene is destroyed.")]
        [SerializeField]
        private bool _moveClientHostObjects = true;

        /// <summary>
        /// True to automatically set active scenes when loading and unloading scenes.
        /// </summary>
        [Tooltip("True to automatically set active scenes when loading and unloading scenes.")] [SerializeField]
        private bool _setActiveScene = true;

        #endregion

        #region Private.

        /// <summary>
        /// ServerManager for this script.
        /// </summary>
        private ServerManager serverManager => NetworkManager.ServerManager;

        /// <summary>
        /// ClientManager for this script.
        /// </summary>
        private ClientManager clientManager => NetworkManager.ClientManager;

        /// <summary>
        /// Scenes which are currently loaded as networked scenes. All players should have networked scenes loaded.
        /// </summary>
        private string[] _globalScenes = Array.Empty<string>();

        /// <summary>
        /// Lastest SceneLoadData for a global load.
        /// </summary>
        private SceneLoadData _globalSceneLoadData = new();

        /// <summary>
        /// Scenes to load or unload, in order.
        /// </summary>
        private List<object> _queuedOperations = new();

        /// <summary>
        /// Scenes which must be manually unloaded, even when emptied.
        /// </summary>
        private HashSet<Scene> _manualUnloadScenes = new();

        /// <summary>
        /// Scene containing moved objects when changing single scene. On client this will contain all objects moved until the server destroys them.
        /// The network only sends spawn messages once per-client, per server side scene load. If a scene load is performed only for specific connections
        /// then the server is not resetting their single scene, but rather the single scene for those connections only. Because of this, any objects
        /// which are to be moved will not receive a second respawn message, as they are never destroyed on server, only on client.
        /// While on server only this scene contains objects being moved temporarily, before being moved to the new scene.
        /// </summary>
        private Scene _movedObjectsScene;

        /// <summary>
        /// Scene containing objects awaiting to be destroyed by the client-host.
        /// This is required when unloading scenes where the client-host has visibility.
        /// Otherwise the objects would become destroyed when the scene unloads on the server
        /// which would cause missing networkobjects on clients when receiving despawn messages.
        /// </summary>
        private Scene _delayedDestroyScene;

        /// <summary>
        /// A scene to be set as the active scene where there are no global scenes.
        /// This is used to prevent connection scenes and MovedObjectsScene from becoming the active scene.
        /// </summary>
        private Scene _fallbackActiveScene;

        /// <summary>
        /// Becomes true when when a scene first successfully begins to load or unload. Value is reset to false when the scene queue is emptied.
        /// </summary>
        private bool _sceneQueueStartInvoked;

        /// <summary>
        /// Objects being moved from MovedObjects scene to another. 
        /// </summary>
        private List<GameObject> _movingObjects = new List<GameObject>();

        /// <summary>
        /// How many scene load confirmations the server is expecting from a client.
        /// Unloads do not need to be checked because server does not require confirmation for those.
        /// This is used to prevent attacks.
        /// </summary>
        private Dictionary<NetworkConnection, int>
            _pendingClientSceneChanges = new Dictionary<NetworkConnection, int>();

        ///// <summary>
        ///// Cache of SceneLookupData.
        ///// </summary>
        //private SceneLookupData _sceneLookupDataCache = new SceneLookupData();
        /// <summary>
        /// GlobalScenes currently loading on the server.
        /// </summary>
        private HashSet<string> _serverGlobalScenesLoading = new HashSet<string>();

        #endregion

        #region Consts.

        /// <summary>
        /// String to use when scene data used to load is invalid.
        /// </summary>
        private const string INVALID_SCENELOADDATA =
            "One or more datas in SceneLoadData are invalid.This generally occurs when calling this method without specifying any scenes or when data fields are null.";

        /// <summary>
        /// String to use when scene data used to unload is invalid.
        /// </summary>
        private const string INVALID_SCENEUNLOADDATA =
            "One or more datas in SceneLoadData are invalid.This generally occurs when calling this method without specifying any scenes or when data fields are null.";

        #endregion

        #region Unity callbacks and initialization.

        private void Awake()
        {
            UnitySceneManager.sceneUnloaded += SceneManager_SceneUnloaded;
            if (_sceneProcessor == null)
                _sceneProcessor = gameObject.AddComponent<DefaultSceneProcessor>();
            _sceneProcessor.Initialize(this);
        }

        private void Start()
        {
            //No need to unregister since managers are on the same object.
            NetworkManager.ServerManager.OnRemoteConnectionState += ServerManager_OnRemoteConnectionState;
            NetworkManager.ServerManager.OnServerConnectionState += ServerManager_OnServerConnectionState;
            clientManager.RegisterBroadcast<LoadScenesBroadcast>(OnLoadScenes);
            clientManager.RegisterBroadcast<UnloadScenesBroadcast>(OnUnloadScenes);
            serverManager.RegisterBroadcast<ClientScenesLoadedBroadcast>(OnClientLoadedScenes);
            serverManager.RegisterBroadcast<EmptyStartScenesBroadcast>(OnServerEmptyStartScenes);
            clientManager.RegisterBroadcast<EmptyStartScenesBroadcast>(OnClientEmptyStartScenes);
        }

        private void OnDestroy()
        {
            UnitySceneManager.sceneUnloaded -= SceneManager_SceneUnloaded;
        }

        /// <summary>
        /// Called when the server connection state changes.
        /// </summary>
        private void ServerManager_OnServerConnectionState(ServerConnectionStateArgs obj)
        {
            //If no servers are started.
            if (!NetworkManager.ServerManager.AnyServerStarted())
                ResetValues();
        }

        /// <summary>
        /// Resets as if first use.
        /// </summary>
        private void ResetValues()
        {
            SceneConnections.Clear();
            _globalScenes = new string[0];
            _globalSceneLoadData = new SceneLoadData();
            _queuedOperations.Clear();
            _manualUnloadScenes.Clear();
            _sceneQueueStartInvoked = false;
            _movingObjects.Clear();
        }

        /// <summary>
        /// Called when a connection state changes for a remote client.
        /// </summary>
        private void ServerManager_OnRemoteConnectionState(NetworkConnection arg1, RemoteConnectionStateArgs arg2)
        {
            if (arg2.ConnectionState == RemoteConnectionState.Stopped)
                ClientDisconnected(arg1);
        }

        /// <summary>
        /// Initializes this script for use.
        /// </summary>
        /// <param name="manager"></param>
        internal void InitializeOnce_Internal(NetworkManager manager)
        {
            NetworkManager = manager;
        }

        /// <summary>
        /// Received when a scene is unloaded.
        /// </summary>
        private void SceneManager_SceneUnloaded(Scene scene)
        {
            if (!NetworkManager.IsServer)
                return;

            /* Remove any unloaded scenes from local variables. This shouldn't
             * be needed if the user properly utilizes this scene manager,
             * but just incase, we don't want a memory leak. */
            SceneConnections.Remove(scene);
            _manualUnloadScenes.Remove(scene);
            RemoveFromGlobalScenes(scene);
        }

        #endregion

        #region Initial synchronizing.

        /// <summary>
        /// Invokes OnClientLoadedStartScenes if connection just loaded start scenes.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="asServer"></param>
        private void TryInvokeLoadedStartScenes(NetworkConnection connection, bool asServer)
        {
            if (connection.SetLoadedStartScenes(asServer))
                OnClientLoadedStartScenes?.Invoke(connection, asServer);
        }

        /// <summary>
        /// Called when authenticator has concluded a result for a connection. Boolean is true if authentication passed, false if failed. This invokes before OnClientAuthenticated so FishNet may run operations on authenticated clients before user code does.
        /// </summary>
        /// <param name="connection"></param>
        internal void OnClientAuthenticated(NetworkConnection connection)
        {
            AddPendingLoad(connection);

            //No global scenes to load.
            if (_globalScenes.Length == 0)
            {
                /* Invoke that client had loaded the default scenes immediately,
                 * since there are no scenes to load. */
                //OnClientLoadedScenes(connection, new ClientScenesLoadedBroadcast());
                //Tell the client there are no scenes to load.
                var msg = new EmptyStartScenesBroadcast();
                connection.Broadcast(msg);
            }
            else
            {
                var globalsNotLoading = GlobalScenesExcludingLoading();
                //If there are globals that can be sent now.
                if (globalsNotLoading != null)
                {
                    var sld = new SceneLoadData(globalsNotLoading);
                    sld.Params = _globalSceneLoadData.Params;
                    sld.Options = _globalSceneLoadData.Options;
                    sld.ReplaceScenes = _globalSceneLoadData.ReplaceScenes;

                    var qd = new LoadQueueData(SceneScopeType.Global, Array.Empty<NetworkConnection>(), sld,
                        _globalScenes, false);
                    //Send message to load the networked scenes.
                    var msg = new LoadScenesBroadcast()
                    {
                        QueueData = qd
                    };

                    connection.Broadcast(msg);
                }
            }
        }

        /// <summary>
        /// Received on client when the server has no start scenes.
        /// </summary>
        private void OnClientEmptyStartScenes(EmptyStartScenesBroadcast msg)
        {
            TryInvokeLoadedStartScenes(clientManager.Connection, false);
            clientManager.Broadcast(msg);
        }

        /// <summary>
        /// Received on server when client confirms there are no start scenes.
        /// </summary>
        private void OnServerEmptyStartScenes(NetworkConnection conn, EmptyStartScenesBroadcast msg)
        {
            //Already received, shouldn't be happening again.
            if (conn.LoadedStartScenes(true))
                conn.Kick(KickReason.ExploitAttempt, LoggingType.Common,
                    $"Received multiple EmptyStartSceneBroadcast from connectionId {conn.ClientId}. Connection will be kicked immediately.");
            else
                OnClientLoadedScenes(conn, new ClientScenesLoadedBroadcast());
        }

        #endregion

        #region Player disconnect.

        /// <summary>
        /// Received when a player disconnects from the server.
        /// </summary>
        /// <param name="conn"></param> //finish.
        private void ClientDisconnected(NetworkConnection conn)
        {
            _pendingClientSceneChanges.Remove(conn);
            /* Remove connection from all scenes. While doing so check
             * if scene should be unloaded provided there are no more clients
             * in the scene, and it's set to automatically unload. This situation is a bit
             * unique since a client disconnect happens outside the manager, so there
             * isn't much code we can re-use to perform this operation. */
            var scenesToUnload = new List<Scene>();
            //Current active scene.
            var activeScene = UnitySceneManager.GetActiveScene();
            foreach (var item in SceneConnections)
            {
                var scene = item.Key;
                var hs = item.Value;

                var removed = hs.Remove(conn);
                /* If no more observers for scene, not a global scene, and not to be manually unloaded
                 * then remove scene from SceneConnections and unload it. */
                if (removed && hs.Count == 0 &&
                    !IsGlobalScene(scene) && !_manualUnloadScenes.Contains(scene) &&
                    (scene != activeScene))
                    scenesToUnload.Add(scene);
            }

            //If scenes should be unloaded.
            if (scenesToUnload.Count > 0)
            {
                foreach (var s in scenesToUnload)
                    SceneConnections.Remove(s);
                var sud = new SceneUnloadData(SceneLookupData.CreateData(scenesToUnload));
                UnloadConnectionScenes(Array.Empty<NetworkConnection>(), sud);
            }
        }

        #endregion

        #region Server received messages.

        /// <summary>
        /// Received on server when a client loads scenes.
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="msg"></param>
        private void OnClientLoadedScenes(NetworkConnection conn, ClientScenesLoadedBroadcast msg)
        {
            int pendingLoads;
            _pendingClientSceneChanges.TryGetValueIL2CPP(conn, out pendingLoads);

            //There's no loads or unloads pending, kick client.
            if (pendingLoads == 0)
            {
                conn.Kick(KickReason.ExploitAttempt, LoggingType.Common,
                    $"Received excessive ClientScenesLoadedBroadcast from connectionId {conn.ClientId}. Connection will be kicked immediately.");
                return;
            }
            //If there is a load pending then update pending count.
            else
            {
                pendingLoads--;
                if (pendingLoads == 0)
                    _pendingClientSceneChanges.Remove(conn);
                else
                    _pendingClientSceneChanges[conn] = pendingLoads;
            }

            if (!Comparers.IsDefault(msg))
            {
                foreach (var item in msg.SceneLookupDatas)
                {
                    var s = item.GetScene(out _);
                    if (s.IsValid())
                        AddConnectionToScene(conn, s);
                }
            }

            TryInvokeLoadedStartScenes(conn, true);
        }

        #endregion

        #region Events.

        /// <summary>
        /// Checks if OnQueueStart should invoke, and if so invokes.
        /// </summary>
        private void TryInvokeOnQueueStart()
        {
            if (_sceneQueueStartInvoked)
                return;

            _sceneQueueStartInvoked = true;
            OnQueueStart?.Invoke();
        }

        /// <summary>
        /// Checks if OnQueueEnd should invoke, and if so invokes.
        /// </summary>
        private void TryInvokeOnQueueEnd()
        {
            if (!_sceneQueueStartInvoked)
                return;

            _sceneQueueStartInvoked = false;
            OnQueueEnd?.Invoke();
        }

        /// <summary>
        /// Invokes that a scene load has started. Only called when valid scenes will be loaded.
        /// </summary>
        /// <param name="qd"></param>
        private void InvokeOnSceneLoadStart(LoadQueueData qd)
        {
            TryInvokeOnQueueStart();
            OnLoadStart?.Invoke(new SceneLoadStartEventArgs(qd));
        }

        /// <summary>
        /// Invokes that a scene load has ended. Only called after a valid scene has loaded.
        /// </summary>
        /// <param name="qd"></param>
        /// <param name="loadedScenes"></param>
        /// <param name="unloadedSceneNames"></param>
        /// <param name="requestedLoadScenes"></param>
        private void InvokeOnSceneLoadEnd(LoadQueueData qd, List<string> requestedLoadScenes, List<Scene> loadedScenes,
            string[] unloadedSceneNames)
        {
            //Make new list to not destroy original data.
            var skippedScenes = requestedLoadScenes.ToList();
            //Remove loaded scenes from requested scenes.
            foreach (var t in loadedScenes)
                skippedScenes.Remove(t.name);

            var args = new SceneLoadEndEventArgs(qd, skippedScenes.ToArray(), loadedScenes.ToArray(),
                unloadedSceneNames);
            OnLoadEnd?.Invoke(args);
        }

        /// <summary>
        /// Invokes that a scene unload has started. Only called when valid scenes will be unloaded.
        /// </summary>
        /// <param name="sqd"></param>
        private void InvokeOnSceneUnloadStart(UnloadQueueData sqd)
        {
            TryInvokeOnQueueStart();
            OnUnloadStart?.Invoke(new SceneUnloadStartEventArgs(sqd));
        }

        /// <summary>
        /// Invokes that a scene unload has ended. Only called after a valid scene has unloaded.
        /// </summary>
        /// <param name="sqd"></param>
        /// <param name="unloadedScenes"></param>
        /// <param name="newUnloadedScenes"></param>
        private void InvokeOnSceneUnloadEnd(UnloadQueueData sqd, List<Scene> unloadedScenes,
            List<UnloadedScene> newUnloadedScenes)
        {
            var args = new SceneUnloadEndEventArgs(sqd, unloadedScenes, newUnloadedScenes);
            OnUnloadEnd?.Invoke(args);
        }

        /// <summary>
        /// Invokes when completion percentage changes while unloading or unloading a scene. Value is between 0f and 1f, while 1f is 100% done.
        /// </summary>
        /// <param name="qd"></param>
        /// <param name="value"></param>
        private void InvokeOnScenePercentChange(LoadQueueData qd, float value)
        {
            value = Mathf.Clamp(value, 0f, 1f);
            var slp = new SceneLoadPercentEventArgs(qd, value);
            OnLoadPercentChange?.Invoke(slp);
        }

        #endregion

        #region Scene queue processing.

        /// <summary>
        /// Queues a load or unload operation and starts queue if needed.
        /// </summary>
        /// <param name="data"></param>
        private void QueueOperation(object data)
        {
            //Add to scene queue data.        
            _queuedOperations.Add(data);
            /* If only one entry then scene operations are not currently in progress.
             * Should there be more than one entry then scene operations are already
             * occuring. The coroutine will automatically load in order. */

            if (_queuedOperations.Count == 1)
                __ProcessSceneQueue();
        }

        /// <summary>
        /// Processes queued scene operations.
        /// </summary>
        private async void __ProcessSceneQueue()
        {
            /* Queue start won't invoke unless a scene load or unload actually occurs.
             * For example: if a scene is already loaded, and nothing needs to be loaded,
             * queue start will not invoke. */

            while (_queuedOperations.Count > 0)
            {
                //If a load scene.
                if (_queuedOperations[0] is LoadQueueData)
                    await __LoadScenes();
                //If an unload scene.
                else if (_queuedOperations[0] is UnloadQueueData)
                    await __UnloadScenes();

                if (_queuedOperations.Count > 0)
                    _queuedOperations.RemoveAt(0);
            }

            TryInvokeOnQueueEnd();
        }

        #endregion

        /// <summary>
        /// Returns global scenes which are not currently being loaded by the server.
        /// </summary>
        /// <returns></returns>
        private string[] GlobalScenesExcludingLoading()
        {
            HashSet<string> excludedScenes = null;
            foreach (var gs in _globalScenes)
            {
                if (_serverGlobalScenesLoading.Contains(gs))
                {
                    if (excludedScenes == null)
                        excludedScenes = new HashSet<string>();

                    excludedScenes.Add(gs);
                }
            }

            //Some scenes are excluded.
            if (excludedScenes != null)
            {
                //All are excluded, quick exit to save perf.
                var remaining = (_globalScenes.Length - excludedScenes.Count);
                if (remaining <= 0)
                    return null;
                //Some are excluded.
                var results = new List<string>();
                foreach (var globalScene in _globalScenes)
                {
                    if (!excludedScenes.Contains(globalScene))
                        results.Add(globalScene);
                }

                return results.ToArray();
            }
            //No scenes are excluded.
            else
            {
                return _globalScenes;
            }
        }

        //#region IsQueuedScene.
        ///// <summary>
        ///// Returns if this SceneManager has a scene load or unload in queue for server or client.
        ///// </summary>
        ///// <param name="loading">True to check loading scenes, false to check unloading.</param>
        ///// <param name="asServer">True to check if data in queue is for server, false if for client.
        ///// <returns></returns>
        //public bool IsQueuedScene(string sceneName, bool loading, bool asServer)
        //{
        //    _sceneLookupDataCache.Update(sceneName, 0);
        //    return IsQueuedScene(_sceneLookupDataCache, loading, asServer);
        //}
        ///// <summary>
        ///// Returns if this SceneManager has a scene load or unload in queue for server or client.
        ///// </summary>
        ///// <param name="loading">True to check loading scenes, false to check unloading.</param>
        ///// <param name="asServer">True to check if data in queue is for server, false if for client.
        ///// <returns></returns>
        //public bool IsQueuedScene(int handle, bool loading, bool asServer)
        //{
        //    _sceneLookupDataCache.Update(string.Empty, handle);
        //    return IsQueuedScene(_sceneLookupDataCache, loading, asServer);
        //}
        ///// <summary>
        ///// Returns if this SceneManager has a scene load or unload in queue for server or client.
        ///// </summary>
        ///// <param name="loading">True to check loading scenes, false to check unloading.</param>
        ///// <param name="asServer">True to check if data in queue is for server, false if for client.
        ///// <returns></returns>
        //public bool IsQueuedScene(Scene scene, bool loading, bool asServer)
        //{
        //    _sceneLookupDataCache.Update(scene.name, scene.handle);
        //    return IsQueuedScene(_sceneLookupDataCache, loading, asServer);
        //}
        ///// <summary>
        ///// Returns if this SceneManager has a scene load or unload in queue for server or client.
        ///// </summary>
        ///// <param name="loading">True to check loading scenes, false to check unloading.</param>
        ///// <param name="asServer">True to check if data in queue is for server, false if for client.
        ///// <returns></returns>
        //public bool IsQueuedScene(SceneLookupData sld, bool loading, bool asServer)
        //{
        //    foreach (object item in _queuedOperations)
        //    {
        //        SceneLookupData[] lookupDatas = null;
        //        //Loading check.
        //        if (loading && item is SceneLoadData loadData)
        //            lookupDatas = loadData.SceneLookupDatas;
        //        else if (!loading && item is SceneUnloadData unloadData)
        //            lookupDatas = unloadData.SceneLookupDatas;

        //        if (lookupDatas != null)
        //        {
        //            foreach (SceneLookupData operationSld in lookupDatas)
        //            {
        //                if (operationSld == sld)
        //                    return true;
        //            }
        //        }
        //    }

        //    //Fall through, not found in any queue operations.
        //    return false;
        //}
        //#endregion

        #region LoadScenes

        /// <summary>
        /// Loads scenes on the server and for all clients. Future clients will automatically load these scenes.
        /// </summary>
        /// <param name="sceneLoadData">Data about which scenes to load.</param>
        public void LoadGlobalScenes(SceneLoadData sceneLoadData)
        {
            LoadGlobalScenes_Internal(sceneLoadData, _globalScenes, true);
        }

        /// <summary>
        /// Adds to load scene queue.
        /// </summary>
        /// <param name="sceneLoadData"></param>
        /// <param name="globalScenes"></param>
        /// <param name="asServer"></param>
        private void LoadGlobalScenes_Internal(SceneLoadData sceneLoadData, string[] globalScenes, bool asServer)
        {
            if (!CanExecute(asServer, true))
                return;
            if (SceneDataInvalid(sceneLoadData, true))
                return;
            if (sceneLoadData.Options.AllowStacking)
            {
                NetworkManager.LogError($"Stacking scenes is not allowed with Global scenes.");
                return;
            }

            var lqd = new LoadQueueData(SceneScopeType.Global, Array.Empty<NetworkConnection>(), sceneLoadData,
                globalScenes, asServer);
            QueueOperation(lqd);
        }

        /// <summary>
        /// Loads scenes on server and tells connections to load them as well. Other connections will not load this scene.
        /// </summary>
        /// <param name="conn">Connections to load scenes for.</param>
        /// <param name="sceneLoadData">Data about which scenes to load.</param>
        public void LoadConnectionScenes(NetworkConnection conn, SceneLoadData sceneLoadData)
        {
            LoadConnectionScenes(new[] { conn }, sceneLoadData);
        }

        /// <summary>
        /// Loads scenes on server and tells connections to load them as well. Other connections will not load this scene.
        /// </summary>
        /// <param name="conns">Connections to load scenes for.</param>
        /// <param name="sceneLoadData">Data about which scenes to load.</param>
        public void LoadConnectionScenes(NetworkConnection[] conns, SceneLoadData sceneLoadData)
        {
            LoadConnectionScenes_Internal(conns, sceneLoadData, _globalScenes, true);
        }

        /// <summary>
        /// Loads scenes on server without telling clients to load the scenes.
        /// </summary>
        /// <param name="sceneLoadData">Data about which scenes to load.</param>
        public void LoadConnectionScenes(SceneLoadData sceneLoadData)
        {
            LoadConnectionScenes_Internal(Array.Empty<NetworkConnection>(), sceneLoadData, _globalScenes, true);
        }

        /// <summary>
        /// Adds to load scene queue.
        /// </summary>
        /// <param name="conns"></param>
        /// <param name="sceneLoadData"></param>
        /// <param name="globalScenes"></param>
        /// <param name="asServer"></param>
        private void LoadConnectionScenes_Internal(NetworkConnection[] conns, SceneLoadData sceneLoadData,
            string[] globalScenes, bool asServer)
        {
            if (!CanExecute(asServer, true))
                return;
            if (SceneDataInvalid(sceneLoadData, true))
                return;

            var lqd = new LoadQueueData(SceneScopeType.Connections, conns, sceneLoadData, globalScenes, asServer);
            QueueOperation(lqd);
        }

        /// <summary>
        /// Returns if a NetworkObject can be moved.
        /// </summary>
        /// <param name="nob"></param>
        /// <param name="warn"></param>
        /// <returns></returns>
        private bool CanMoveNetworkObject(NetworkObject nob, bool warn)
        {
            //Null.
            if (nob == null)
                return WarnAndReturnFalse($"NetworkObject is null.");
            //Not networked.
            if (!nob.IsNetworked)
                return WarnAndReturnFalse($"NetworkObject {nob.name} cannot be moved as it is not networked.");
            //Not spawned.
            if (!nob.IsSpawned)
                return WarnAndReturnFalse($"NetworkObject {nob.name} canot be moved as it is not spawned.");
            //SceneObject.
            if (nob.IsSceneObject)
                return WarnAndReturnFalse($"NetworkObject {nob.name} cannot be moved as it is a scene object.");
            //Not root.
            if (nob.transform.parent != null)
                return WarnAndReturnFalse(
                    $"NetworkObject {nob.name} cannot be moved because it is not the root object. Unity can only move root objects between scenes.");
            //In DDOL and IsGlobal.
            if (nob.IsGlobal && (nob.gameObject.scene.name == DDOLFinder.GetDDOL().gameObject.scene.name))
                return WarnAndReturnFalse(
                    "NetworkObject {nob.name} cannot be moved because it is global. Global objects must remain in the DontDestroyOnLoad scene.");

            //Fall through success.
            return true;

            bool WarnAndReturnFalse(string msg)
            {
                if (warn)
                    NetworkManager.LogWarning(msg);
                return false;
            }
        }

        /// <summary>
        /// Loads a connection scene queue data. This behaves just like a networked scene load except it sends only to the specified connections, and it always loads as an additive scene on server.
        /// </summary>
        /// <returns></returns>
        private async UniTask __LoadScenes()
        {
            try
            {
                if (_queuedOperations[0] is not LoadQueueData data)
                    return;
                var sceneLoadData = data.SceneLoadData;
                //True if running as server.
                var asServer = data.AsServer;
                //True if running as client, while network server is active.
                var asHost = (!asServer && NetworkManager.IsServer);

                //If connection went inactive.
                if (!ConnectionActive(asServer))
                    return;

                /* Scene sanity checks. */
                if (sceneLoadData.SceneLookupDatas.Length == 0)
                {
                    NetworkManager.LogWarning($"No scenes specified to load.");
                    return;
                }

                //True if replacing scenes with specified ones.
                var replaceScenes = sceneLoadData.ReplaceScenes;

                //May be unset if on server, this is fine.
                var localConnection = NetworkManager.ClientManager.Connection;
                switch (asServer)
                {
                    /* Immediately set new global scenes. If on client this is whatever
                     * server passes in. This should be set even if scope type
                     * is not global because clients might get a connection scene first.
                     */
                    case false:
                    {
                        if (!asHost)
                            _globalScenes = data.GlobalScenes;
                        break;
                    }
                    /* However, if server, then only update global scenes if scope
                     * is global. */
                    case true when data.ScopeType == SceneScopeType.Global:
                    {
                        _globalSceneLoadData = sceneLoadData;
                        var names = sceneLoadData.SceneLookupDatas.GetNames();
                        //Add to server global scenes which are currently loading.
                        foreach (var item in names)
                            _serverGlobalScenesLoading.Add(item);
                        //If replacing.
                        if (replaceScenes != ReplaceOption.None)
                        {
                            _globalScenes = names;
                        }
                        //Add onto.
                        else
                        {
                            var index = _globalScenes.Length;
                            Array.Resize(ref _globalScenes, _globalScenes.Length + names.Length);
                            Array.Copy(names, 0, _globalScenes, index, names.Length);
                        }

                        data.GlobalScenes = _globalScenes;
                        break;
                    }
                }


                /* Scene queue data scenes.
                 * All scenes in the scene queue data whether they will be loaded or not. */
                var requestedLoadSceneNames = new List<string>();
                var requestedLoadSceneHandles = new List<int>();

                /* Make a null filled array. This will be populated
                 * using loaded scenes, or already loaded (eg cannot be loaded) scenes. */
                var broadcastLookupDatas = new SceneLookupData[sceneLoadData.SceneLookupDatas.Length];

                /* LoadableScenes and SceneReferenceDatas.
                /* Will contain scenes which may be loaded.
                 * Scenes might not be added to loadableScenes
                 * if for example loadOnlyUnloaded is true and
                 * the scene is already loaded. */
                var loadableScenes = new List<SceneLookupData>();
                for (var i = 0; i < sceneLoadData.SceneLookupDatas.Length; i++)
                {
                    var lookupData = sceneLoadData.SceneLookupDatas[i];
                    //Scene to load.
                    bool byHandle;
                    var s = lookupData.GetScene(out byHandle);
                    //If found then add it to requestedLoadScenes.
                    if (s.IsValid())
                    {
                        requestedLoadSceneNames.Add(s.name);
                        if (byHandle)
                            requestedLoadSceneHandles.Add(s.handle);
                    }

                    if (CanLoadScene(data, lookupData))
                    {
                        //Don't load if as host, server side would have loaded already.
                        if (!asHost)
                            loadableScenes.Add(lookupData);
                    }
                    //Only the server needs to find scene handles to send to client. Client will send these back to the server.
                    else if (asServer)
                    {
                        /* If here then scene cannot be loaded, which
                         * can only happen if the scene already exists.
                         * Find the scene using sld and set to datas. */
                        /* Set at the index of i. This way should the current
                         * SLD not be the first scene it won't fill the
                         * first slot in broadcastLookupDatas. This is important
                         * because the first slot is used for the single scene
                         * when using replace scenes. */
                        broadcastLookupDatas[i] = new SceneLookupData(s);
                    }
                }

                /* Move identities
                 * to holder scene to preserve them.
                 * Required if a single scene is specified. Cannot rely on
                 * loadSingleScene since it is only true if the single scene
                 * must be loaded, which may be false if it's already loaded on
                 * the server. */
                //Do not run if running as client, and server is active. This would have already run as server.
                if (!asHost)
                {
                    foreach (var nob in sceneLoadData.MovedNetworkObjects)
                    {
                        //NetworkObject might be null if client lost observation of it.
                        if (nob != null && CanMoveNetworkObject(nob, true))
                            UnitySceneManager.MoveGameObjectToScene(nob.gameObject, GetMovedObjectsScene());
                    }
                }

                //Connection scenes handles prior to ConnectionScenes being modified.
                var connectionScenesHandlesCached = new List<int>();
                //If replacing scenes.
                if (replaceScenes != ReplaceOption.None)
                {
                    /* Resetting SceneConnections. */
                    /* If server and replacing scenes.
                     * It's important to run this AFTER moving MovedNetworkObjects
                     * so that they are no longer in the scenes they are leaving. Otherwise
                     * the scene condition would pick them up as still in the leaving scene. */
                    if (asServer)
                    {
                        var sceneConnectionsKeys = SceneConnections.Keys.ToArray();
                        for (var i = 0; i < sceneConnectionsKeys.Length; i++)
                            connectionScenesHandlesCached.Add(sceneConnectionsKeys[i].handle);

                        //If global then remove all connections from all scenes.
                        if (data.ScopeType == SceneScopeType.Global)
                        {
                            foreach (var s in sceneConnectionsKeys)
                                RemoveAllConnectionsFromScene(s);
                        }
                        //Connections.
                        else if (data.ScopeType == SceneScopeType.Connections)
                        {
                            RemoveConnectionsFromNonGlobalScenes(data.Connections);
                        }
                    }
                    //As client set scenes id cache to local connection scenes.
                    else
                    {
                        foreach (var s in NetworkManager.ClientManager.Connection.Scenes)
                            connectionScenesHandlesCached.Add(s.handle);
                    }
                }


                /* Scene unloading if replacing scenes.
                 *
                 * Unload all scenes except MovedObjectsHolder. Also don't
                 * unload GlobalScenes if loading as connection. */
                var unloadableScenes = new List<Scene>();
                //Do not run if running as client, and server is active. This would have already run as server.
                if ((replaceScenes != ReplaceOption.None) && !asHost)
                {
                    //See what scenes can be unloaded based on replace options.
                    for (var i = 0; i < UnitySceneManager.sceneCount; i++)
                    {
                        var s = UnitySceneManager.GetSceneAt(i);
                        //MovedObjectsScene will never be unloaded.
                        if (s == GetMovedObjectsScene())
                            continue;
                        /* Scene is in one of the scenes being loaded.
                         * This can occur when trying to load additional clients
                         * into an existing scene. */
                        if (requestedLoadSceneNames.Contains(s.name))
                            continue;
                        //Same as above but using handles.
                        if (requestedLoadSceneHandles.Contains(s.handle))
                            continue;
                        /* Cannot unload global scenes. If
                         * replace scenes was used for a global
                         * load then global scenes would have been reset
                         * before this. */
                        if (IsGlobalScene(s))
                            continue;
                        //If scene must be manually unloaded then it cannot be unloaded here.
                        if (_manualUnloadScenes.Contains(s))
                            continue;

                        var inScenesCache = connectionScenesHandlesCached.Contains(s.handle);
                        HashSet<NetworkConnection> conns;
                        var inScenesCurrent = SceneConnections.ContainsKey(s);
                        //If was in scenes previously but isnt now then no connections reside in the scene.
                        if (inScenesCache && !inScenesCurrent)
                        {
                            //Intentionally left blank.
                        }
                        //If still in cache see if any connections exist.
                        else if (SceneConnections.TryGetValueIL2CPP(s, out conns))
                        {
                            //Still has clients in scene.
                            if (conns != null && conns.Count > 0)
                                continue;
                        }
                        //An offline scene.
                        else
                        {
                            //If not replacing all scenes then skip offline scenes.
                            if (replaceScenes != ReplaceOption.All)
                                continue;
                        }

                        unloadableScenes.Add(s);
                    }
                }

                /* Start event. */
                if (unloadableScenes.Count > 0 || loadableScenes.Count > 0)
                {
                    InvokeOnSceneLoadStart(data);
                    _sceneProcessor.LoadStart(data);
                }

                //Unloaded scenes by name. Only used for information within callbacks.
                var unloadedNames = new string[unloadableScenes.Count];
                for (var i = 0; i < unloadableScenes.Count; i++)
                    unloadedNames[i] = unloadableScenes[i].name;
                /* Before unloading if !asServer and !asHost and replacing scenes
                 * then move all non scene networked objects to the moved
                 * objects holder. Otherwise network objects would get destroyed
                 * on the scene change and never respawned if server doesn't
                 * have a reason to update visibility. */
                if (!data.AsServer && !asHost && (replaceScenes != ReplaceOption.None))
                {
                    var s = GetMovedObjectsScene();
                    foreach (var nob in NetworkManager.ClientManager.Objects.Spawned.Values)
                    {
                        if (CanMoveNetworkObject(nob, false))
                            UnitySceneManager.MoveGameObjectToScene(nob.gameObject, s);
                    }
                }

                /* Unloading scenes. */
                _sceneProcessor.UnloadStart(data);
                for (var i = 0; i < unloadableScenes.Count; i++)
                {
                    MoveClientHostObjects(unloadableScenes[i], asServer);
                    //Unload one at a time.
                    _sceneProcessor.BeginUnloadAsync(unloadableScenes[i]);
                    while (!_sceneProcessor.IsPercentComplete())
                        await UniTask.NextFrame();
                }

                _sceneProcessor.UnloadEnd(data);

                //Scenes loaded.
                var loadedScenes = new List<Scene>();
                /* Scene loading.
                /* Use additive to not thread lock server. */
                for (var i = 0; i < loadableScenes.Count; i++)
                {
                    //Start load async and wait for it to finish.
                    var loadSceneParameters = new LoadSceneParameters()
                    {
                        loadSceneMode = LoadSceneMode.Additive,
                        localPhysicsMode = sceneLoadData.Options.LocalPhysics
                    };

                    /* How much percentage each scene load can be worth
                     * at maximum completion. EG: if there are two scenes
                     * 1f / 2f is 0.5f. */
                    var maximumIndexWorth = 1f / loadableScenes.Count;

                    _sceneProcessor.BeginLoadAsync(loadableScenes[i].Name, loadSceneParameters);
                    while (!_sceneProcessor.IsPercentComplete())
                    {
                        var percent = _sceneProcessor.GetPercentComplete();
                        InvokePercentageChange(i, maximumIndexWorth, percent);
                        await UniTask.NextFrame();
                    }

                    //Invokes OnScenePercentChange with progress.
                    void InvokePercentageChange(int index, float maximumWorth, float currentScenePercent)
                    {
                        /* Total percent will be how much percentage is complete
                         * in total. Initialize it with a value based on how many
                         * scenes are already fully loaded. */
                        var totalPercent = (index * maximumWorth);
                        //Add this scenes progress onto total percent.
                        totalPercent += Mathf.Lerp(0f, maximumWorth, currentScenePercent);
                        //Dispatch with total percent.
                        InvokeOnScenePercentChange(data, totalPercent);
                    }

                    //Add to loaded scenes.
                    var loaded = UnitySceneManager.GetSceneAt(UnitySceneManager.sceneCount - 1);
                    loadedScenes.Add(loaded);
                    _sceneProcessor.AddLoadedScene(loaded);
                }

                //When all scenes are loaded invoke with 100% done.
                InvokeOnScenePercentChange(data, 1f);

                /* Add to ManuallyUnloadScenes. */
                if (data.AsServer && !sceneLoadData.Options.AutomaticallyUnload)
                {
                    foreach (var s in loadedScenes)
                        _manualUnloadScenes.Add(s);
                }

                /* Move identities to first scene. */
                if (!asHost)
                {
                    //Find the first valid scene to move objects to.
                    Scene firstValidScene = default;
                    //If to stack scenes.
                    if (sceneLoadData.Options.AllowStacking)
                    {
                        var firstScene = sceneLoadData.GetFirstLookupScene();
                        /* If the first lookup data contains a handle and the scene
                         * is found for that handle then use that as the moved to scene.
                         * Nobs always move to the first specified scene. */
                        if (sceneLoadData.SceneLookupDatas[0].Handle != 0 && !string.IsNullOrEmpty(firstScene.name))
                        {
                            firstValidScene = firstScene;
                        }
                        //If handle is not specified then used the last scene that has the same name as the first lookupData.
                        else
                        {
                            Scene lastSameSceneName = default;
                            for (var i = 0; i < UnitySceneManager.sceneCount; i++)
                            {
                                var s = UnitySceneManager.GetSceneAt(i);
                                if (s.name == firstScene.name)
                                    lastSameSceneName = s;
                            }

                            /* Shouldn't be possible since the scene will always exist either by
                             * just being loaded or already loaded. */
                            if (string.IsNullOrEmpty(lastSameSceneName.name))
                                NetworkManager.LogError(
                                    $"Scene {sceneLoadData.SceneLookupDatas[0].Name} could not be found in loaded scenes.");
                            else
                                firstValidScene = lastSameSceneName;
                        }
                    }
                    //Not stacking.
                    else
                    {
                        firstValidScene = sceneLoadData.GetFirstLookupScene();
                        //If not found by look then try first loaded.
                        if (string.IsNullOrEmpty(firstValidScene.name))
                            firstValidScene = GetFirstLoadedScene();
                    }

                    //Gets first scene loaded this method call.
                    Scene GetFirstLoadedScene() => loadedScenes.Count > 0 ? loadedScenes[0] : default;

                    //If firstValidScene is still invalid then throw.
                    if (string.IsNullOrEmpty(firstValidScene.name))
                    {
                        NetworkManager.LogError(
                            $"Unable to move objects to a new scene because new scene lookup has failed.");
                    }
                    // ReSharper disable once CommentTypo
                    //Move objects from movedobejctsscene to first valid scene.
                    else
                    {
                        var s = GetMovedObjectsScene();
                        s.GetRootGameObjects(_movingObjects);

                        foreach (var go in _movingObjects)
                            UnitySceneManager.MoveGameObjectToScene(go, firstValidScene);
                    }
                }

                _sceneProcessor.ActivateLoadedScenes();
                //Wait until everything is loaded (done).
                await _sceneProcessor.WaitAsyncIsDone();
                _sceneProcessor.LoadEnd(data);

                /* Wait until loadedScenes are all marked as done.
                 * This is an extra precautionary step because on some devices
                 * the AsyncIsDone returns true before scenes are actually loaded. */
                bool allScenesLoaded;
                do
                {
                    //Reset state for iteration https://github.com/FirstGearGames/FishNet/issues/322
                    allScenesLoaded = true;
                    foreach (var s in loadedScenes)
                    {
                        if (!s.isLoaded)
                        {
                            allScenesLoaded = false;
                            break;
                        }
                    }

                    await UniTask.NextFrame();
                } while (!allScenesLoaded);

                SetActiveSceneLocal();

                void SetActiveSceneLocal()
                {
                    var preferredActiveScene =
                        GetUserPreferredActiveScene(sceneLoadData.PreferredActiveScene, out var byUser);
                    //If preferred still is not set then try to figure it out.
                    if (!preferredActiveScene.IsValid())
                    {
                        /* Populate preferred scene to first loaded if replacing
                         * scenes for connection. Does not need to be set for
                         * global because when a global exist it's always set
                         * as the active scene.
                         *
                         * Do not set preferred scene if server as this could cause
                         * problems when stacking or connection specific scenes. Let the
                         * user make those changes. */
                        if (sceneLoadData.ReplaceScenes != ReplaceOption.None &&
                            data.ScopeType == SceneScopeType.Connections && !NetworkManager.IsServer)
                            preferredActiveScene = sceneLoadData.GetFirstLookupScene();
                    }

                    SetActiveScene(preferredActiveScene, byUser);
                }

                //Only the server needs to find scene handles to send to client. Client will send these back to the server.
                if (asServer)
                {
                    //Populate broadcast Lookup data with any loaded scenes.
                    foreach (var s in loadedScenes)
                    {
                        SetInFirstNullIndex(s);
                        continue;

                        //Sets scene in the first null index of broadcast Lookup data.
                        void SetInFirstNullIndex(Scene scene)
                        {
                            for (var i = 0; i < broadcastLookupDatas.Length; i++)
                            {
                                if (broadcastLookupDatas[i] == null)
                                {
                                    broadcastLookupDatas[i] = new SceneLookupData(scene);
                                    return;
                                }
                            }

                            //If here there are no null entries.
                            NetworkManager.LogError($"Cannot add scene to broadcastLookupDatas, collection is full.");
                        }
                    }
                }

                /* If running as server and server is
                 * active then send scene changes to client.
                 * Making sure server is still active should it maybe
                 * have dropped during scene loading. */
                if (data.AsServer && NetworkManager.IsServer)
                {
                    //Tell clients to load same scenes.
                    var msg = new LoadScenesBroadcast()
                    {
                        QueueData = data
                    };
                    //Replace scene lookup datas with ones intended to broadcast to client.
                    msg.QueueData.SceneLoadData.SceneLookupDatas = broadcastLookupDatas;
                    switch (data.ScopeType)
                    {
                        //If networked scope then send to all.
                        case SceneScopeType.Global:
                        {
                            var cons = serverManager.Clients.Values.ToArray();
                            AddPendingLoad(cons);
                            serverManager.Broadcast(msg);
                            break;
                        }
                        //If connections scope then only send to connections.
                        case SceneScopeType.Connections:
                        {
                            AddPendingLoad(data.Connections);
                            foreach (var t in data.Connections)
                                if (t.Authenticated)
                                    t.Broadcast(msg);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                /* If running as client then send a message
                 * to the server to tell them the scene was loaded.
                 * This allows the server to add the client
                 * to the scene for checkers. */
                else if (!data.AsServer && NetworkManager.IsClient)
                {
                    //Remove from old scenes.
                    foreach (var item in unloadableScenes)
                        if (item.IsValid())
                            localConnection.RemoveFromScene(item);
                    //Add local client to scenes.
                    foreach (var item in loadedScenes)
                        localConnection.AddToScene(item);

                    TryInvokeLoadedStartScenes(clientManager.Connection, false);

                    var msg = new ClientScenesLoadedBroadcast()
                    {
                        SceneLookupDatas = sceneLoadData.SceneLookupDatas
                    };
                    clientManager.Broadcast(msg);
                }

                InvokeOnSceneLoadEnd(data, requestedLoadSceneNames, loadedScenes, unloadedNames);
            }
            finally
            {
                _serverGlobalScenesLoading.Clear();
            }
        }

        /// <summary>
        /// Received on client when connection scenes must be loaded.
        /// </summary>
        /// <param name="msg"></param>
        private void OnLoadScenes(LoadScenesBroadcast msg)
        {
            //Null data is sent by the server when there are no start scenes to load.
            if (msg.QueueData == null)
            {
                TryInvokeLoadedStartScenes(clientManager.Connection, false);
            }
            else
            {
                var qd = msg.QueueData;
                if (qd.ScopeType == SceneScopeType.Global)
                    LoadGlobalScenes_Internal(qd.SceneLoadData, qd.GlobalScenes, false);
                else
                    LoadConnectionScenes_Internal(Array.Empty<NetworkConnection>(), qd.SceneLoadData, qd.GlobalScenes,
                        false);
            }
        }

        #endregion

        #region UnloadScenes.

        /// <summary>
        /// Unloads scenes on the server and for all clients.
        /// </summary>
        /// <param name="sceneUnloadData">Data about which scenes to unload.</param>
        public void UnloadGlobalScenes(SceneUnloadData sceneUnloadData)
        {
            if (!CanExecute(true, true))
                return;

            UnloadGlobalScenes_Internal(sceneUnloadData, _globalScenes, true);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="globalScenes"></param>
        /// <param name="asServer"></param>
        /// <param name="sceneUnloadData"></param>
        private void UnloadGlobalScenes_Internal(SceneUnloadData sceneUnloadData, string[] globalScenes, bool asServer)
        {
            var uqd = new UnloadQueueData(SceneScopeType.Global, Array.Empty<NetworkConnection>(), sceneUnloadData,
                globalScenes, asServer);
            QueueOperation(uqd);
        }


        /// <summary>
        /// Unloads scenes on server and tells a connection to unload them as well. Other connections will not unload this scene.
        /// </summary>
        /// <param name="connection">Connection to unload scenes for.</param>
        /// <param name="sceneUnloadData">Data about which scenes to unload.</param>
        public void UnloadConnectionScenes(NetworkConnection connection, SceneUnloadData sceneUnloadData) =>
            UnloadConnectionScenes(new[] { connection }, sceneUnloadData);

        /// <summary>
        /// Unloads scenes on server and tells connections to unload them as well. Other connections will not unload this scene.
        /// </summary>
        /// <param name="connections">Connections to unload scenes for.</param>
        /// <param name="sceneUnloadData">Data about which scenes to unload.</param>
        public void UnloadConnectionScenes(NetworkConnection[] connections, SceneUnloadData sceneUnloadData)
            => UnloadConnectionScenes_Internal(connections, sceneUnloadData, _globalScenes, true);

        /// <summary>
        /// Unloads scenes on server without telling any connections to unload them.
        /// </summary>
        /// <param name="sceneUnloadData">Data about which scenes to unload.</param>
        public void UnloadConnectionScenes(SceneUnloadData sceneUnloadData)
        {
            UnloadConnectionScenes_Internal(Array.Empty<NetworkConnection>(), sceneUnloadData, _globalScenes, true);
        }

        /// <summary>
        /// Unloads scenes for connections.
        /// </summary>
        /// <param name="connections"></param>
        /// <param name="sceneUnloadData"></param>
        /// <param name="globalScenes"></param>
        /// <param name="asServer"></param>
        private void UnloadConnectionScenes_Internal(NetworkConnection[] connections, SceneUnloadData sceneUnloadData,
            string[] globalScenes, bool asServer)
        {
            if (!CanExecute(asServer, true))
                return;
            if (SceneDataInvalid(sceneUnloadData, true))
                return;

            var uqd = new UnloadQueueData(SceneScopeType.Connections, connections, sceneUnloadData, globalScenes,
                asServer);
            QueueOperation(uqd);
        }

        /// <summary>
        /// Loads scenes within QueuedSceneLoads.
        /// </summary>
        /// <returns></returns>
        private async UniTask __UnloadScenes()
        {
            if (_queuedOperations[0] is not UnloadQueueData data)
                return;
            
            var sceneUnloadData = data.SceneUnloadData;
            //If connection went inactive.
            if (!ConnectionActive(data.AsServer))
                return;

            /* Some actions should not run as client if server is also active.
             * This is to keep things from running twice. */
            var asClientHost = (!data.AsServer && NetworkManager.IsServer);
            // True if running asServer.
            var asServer = data.AsServer;

            //Get scenes to unload.
            var scenes = GetScenes(sceneUnloadData.SceneLookupDatas);
            /* No scenes found. Only run this if not asHost.
             * While asHost scenes will possibly not exist because
             * server side has already unloaded them. But rest of
             * the unload should continue. */
            if (scenes.Length == 0 && !asClientHost)
            {
                NetworkManager.LogWarning($"No scenes were found to unload.");
                return;
            }

            /* Remove from global scenes
             * if server and scope is global.
             * All passed in scenes should be removed from global
             * regardless of if they're valid or not. If they are invalid,
             * then they shouldn't be in global to begin with. */
            if (asServer && data.ScopeType == SceneScopeType.Global)
            {
                RemoveFromGlobalScenes(sceneUnloadData.SceneLookupDatas);
                //Update queue data.
                data.GlobalScenes = _globalScenes;
            }

            /* Remove connections. */
            if (asServer)
            {
                foreach (var s in scenes)
                {
                    //If global then remove all connections.
                    if (data.ScopeType == SceneScopeType.Global)
                        RemoveAllConnectionsFromScene(s);
                    //Connections.
                    else if (data.ScopeType == SceneScopeType.Connections)
                        RemoveConnectionsFromScene(data.Connections, s);
                }
            }


            /* This will contain all scenes which can be unloaded.
             * The collection will be modified through various checks. */
            var unloadableScenes = scenes.ToList();
            /* Unloaded scenes manually created to overcome
             * the empty names in Scene structs after Unity unloads
             * a scene. */
            var unloadedScenes = new List<UnloadedScene>();
            /* If asServer and KeepUnused then clear all unloadables.
             * The clients will still unload the scenes. */
            if ((asServer || asClientHost) && sceneUnloadData.Options.Mode == UnloadOptions.ServerUnloadMode.KeepUnused)
                unloadableScenes.Clear();
            /* Check to remove global scenes unloadableScenes.
             * This will need to be done if scenes are being unloaded
             * for connections. Global scenes cannot be unloaded as
             * connection. */
            if (data.ScopeType == SceneScopeType.Connections)
                RemoveGlobalScenes(unloadableScenes);
            //If set to unload unused only.
            if (sceneUnloadData.Options.Mode == UnloadOptions.ServerUnloadMode.UnloadUnused)
                RemoveOccupiedScenes(unloadableScenes);

            //If there are scenes to unload.
            if (unloadableScenes.Count > 0)
            {
                InvokeOnSceneUnloadStart(data);
                _sceneProcessor.UnloadStart(data);

                //Begin unloading.
                foreach (var s in unloadableScenes)
                {
                    unloadedScenes.Add(new UnloadedScene(s));
                    MoveClientHostObjects(s, asServer);
                    /* Remove from manualUnloadedScenes.
                     * Scene may not be in this collection
                     * but removing is one call vs checking
                     * then removing. */
                    _manualUnloadScenes.Remove(s);

                    _sceneProcessor.BeginUnloadAsync(s);
                    while (!_sceneProcessor.IsPercentComplete())
                        await UniTask.NextFrame();
                }

                _sceneProcessor.UnloadEnd(data);
            }

            /* Must yield after sceneProcessor handles things.
             * This is a Unity bug of sorts. I'm not entirely sure what
             * is happening, but without the yield it seems as though
             * the processor logic doesn't complete. This doesn't make much
             * sense given unity is supposed to be single threaded. Must be
             * something to do with the coroutine. */
            await UniTask.NextFrame();
            var preferredActiveScene =
                GetUserPreferredActiveScene(sceneUnloadData.PreferredActiveScene, out var byUser);
            SetActiveScene(preferredActiveScene, byUser);

            switch (asServer)
            {
                /* If running as server then make sure server
                 * is still active after the unloads. If so
                 * send out unloads to clients. */
                case true when ConnectionActive(true):
                {
                    //Tell clients to unload same scenes.
                    var msg = new UnloadScenesBroadcast()
                    {
                        QueueData = data
                    };
                    switch (data.ScopeType)
                    {
                        //Global.
                        case SceneScopeType.Global:
                            serverManager.Broadcast(msg);
                            break;
                        //Connections.
                        case SceneScopeType.Connections when data.Connections != null:
                        {
                            foreach (var t in data.Connections)
                                t?.Broadcast(msg);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    break;
                }
                case false:
                {
                    var localConnection = NetworkManager.ClientManager.Connection;
                    //Remove from old scenes.
                    foreach (var item in unloadableScenes)
                        if (item.IsValid())
                            localConnection.RemoveFromScene(item);
                    break;
                }
            }

            InvokeOnSceneUnloadEnd(data, unloadableScenes, unloadedScenes);
        }


        /// <summary>
        /// Received on clients when networked scenes must be unloaded.
        /// </summary>
        /// <param name="msg"></param>
        private void OnUnloadScenes(UnloadScenesBroadcast msg)
        {
            var qd = msg.QueueData;
            if (qd.ScopeType == SceneScopeType.Global)
                UnloadGlobalScenes_Internal(qd.SceneUnloadData, qd.GlobalScenes, false);
            else
                UnloadConnectionScenes_Internal(Array.Empty<NetworkConnection>(), qd.SceneUnloadData, qd.GlobalScenes,
                    false);
        }

        #endregion

        /// <summary>
        /// Move objects visible to clientHost that are within an unloading scene.This ensures the objects are despawned on the client side rather than when the scene is destroyed.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="asServer"></param>
        private void MoveClientHostObjects(Scene scene, bool asServer)
        {
            if (!_moveClientHostObjects)
                return;
            /* The asServer isn't really needed. I could only call
             * this method when asServer is true. But for the sake
             * of preventing user-error (me being the user this time)
             * I've included it into the parameters. */
            if (!asServer)
                return;
            //Don't need to perform if not host.
            // Server will pooling for now
            // if (!NetworkManager.IsClient)
            //     return;

            var clientConn = NetworkManager.ClientManager.Connection;
            /* It would be nice to see if the client wasn't even in the scene
             * here using SceneConnections but it's possible that the scene had been
             * wiped from SceneConnections earlier depending on how scenes are
             * loaded or unloaded. Instead we must iterate through spawned objects. */

            var movingNobs = CollectionCaches<NetworkObject>.RetrieveList();
            /* Rather than a get all networkobjects in scene
             * let's iterate the spawned objects instead. I imagine
             * in most scenarios iterating spawned would be faster.
             * That's a long one! */
            foreach (var nob in NetworkManager.ServerManager.Objects.Spawned.Values)
            {
                //Not in the scene being destroyed.
                if (nob.gameObject.scene != scene)
                    continue;
                //ClientHost doesn't have visibility.
                // if (!nob.Observers.Contains(clientConn))
                //     continue;
                //Cannot move if not root.
                if (nob.transform.root != null && nob.transform.root != nob.transform)
                    continue;

                /* If here nob is in the same being
                 * destroyed and clientHost has visiblity. */
                movingNobs.Add(nob);
            }

            var count = movingNobs.Count;
            if (count > 0)
            {
                var moveScene = GetDelayedDestroyScene();
                for (var i = 0; i < count; i++)
                {
                    var nob = movingNobs[i];
                    /* Force as not a scene object
                     * so that it becomes destroyed
                     * rather than disabled. */
                    nob.ClearRuntimeSceneObject();
                    /* If the object is already being despawned then
                     *just disable and move it. Otherwise despawn it
                     * on the server then move it. */
                    //Not deinitializing, despawn it then.
                    if (!nob.IsDeinitializing)
                        nob.Despawn();
                    else
                        nob.gameObject.SetActive(false);

                    UnitySceneManager.MoveGameObjectToScene(nob.gameObject, moveScene);
                }
            }

            CollectionCaches<NetworkObject>.Store(movingNobs);
        }

        /// <summary>
        /// Returns if a connection is in a scene using SceneConnections.
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="scene"></param>
        /// <returns></returns>
        internal bool InSceneConnections(NetworkConnection conn, Scene scene)
        {
            if (!SceneConnections.TryGetValueIL2CPP(scene, out var hs))
                return false;
            else
                return hs.Contains(conn);
        }

        /// <summary>
        /// Adds the owner of nob to the gameObjects scene if there are no global scenes.
        /// </summary>
        public void AddOwnerToDefaultScene(NetworkObject nob)
        {
            //No owner.
            if (!nob.Owner.IsValid)
            {
                NetworkManager.LogWarning($"NetworkObject {nob.name} does not have an owner.");
                return;
            }

            //Won't add to default if there are globals.
            if (_globalScenes != null && _globalScenes.Length > 0)
                return;

            AddConnectionToScene(nob.Owner, nob.gameObject.scene);
        }

        /// <summary>
        /// Adds a connection to a scene. This will always be called one connection at a time because connections are only added after they invidually validate loading the scene.
        /// Exposed for power users, use caution.
        /// </summary>
        /// <param name="conn">Connection to add.</param>
        /// <param name="scene">Scene to add the connection to.</param>
        public void AddConnectionToScene(NetworkConnection conn, Scene scene)
        {
            HashSet<NetworkConnection> hs;
            //Scene doesn't have any connections yet.
            var inSceneConnections = SceneConnections.TryGetValueIL2CPP(scene, out hs);
            if (!inSceneConnections)
                hs = new HashSet<NetworkConnection>();

            var added = hs.Add(conn);
            if (added)
            {
                conn.AddToScene(scene);

                //If not yet added to scene connections.
                if (!inSceneConnections)
                    SceneConnections[scene] = hs;

                var arrayConn = new[] { conn };
                InvokeClientPresenceChange(scene, arrayConn, true, true);
                RebuildObservers(arrayConn.ToArray());
                InvokeClientPresenceChange(scene, arrayConn, true, false);

                /* Also need to rebuild all networkobjects
                 * for connection so other players can
                 * see them. */
                RebuildObservers(conn.Objects.ToArray());
            }
        }


        /// <summary>
        /// Removes connections from any scene which is not global.
        /// Exposed for power users, use caution.
        /// </summary>
        /// <param name="conns"></param>
        public void RemoveConnectionsFromNonGlobalScenes(NetworkConnection[] conns)
        {
            var removedScenes = new List<Scene>();

            foreach (var item in SceneConnections)
            {
                var scene = item.Key;
                //Cannot remove from globla scenes.
                if (IsGlobalScene(scene))
                    continue;

                var hs = item.Value;
                var connectionsRemoved = new List<NetworkConnection>();
                //Remove every connection from the scene.
                foreach (var c in conns)
                {
                    var removed = hs.Remove(c);
                    if (removed)
                    {
                        c.RemoveFromScene(scene);
                        connectionsRemoved.Add(c);
                    }
                }

                //If hashset is empty then remove scene from SceneConnections.
                if (hs.Count == 0)
                    removedScenes.Add(scene);

                if (connectionsRemoved.Count > 0)
                {
                    var connectionsRemovedArray = connectionsRemoved.ToArray();
                    InvokeClientPresenceChange(scene, connectionsRemovedArray, false, true);
                    RebuildObservers(connectionsRemovedArray);
                    InvokeClientPresenceChange(scene, connectionsRemovedArray, false, false);
                }
            }

            foreach (var s in removedScenes)
                SceneConnections.Remove(s);

            /* Also rebuild observers for objects owned by connection.
             * This ensures other connections will lose visibility if
             * they no longer share a scene. */
            foreach (var c in conns)
                RebuildObservers(c.Objects.ToArray());
        }


        /// <summary>
        /// Removes connections from specified scenes.
        /// Exposed for power users, use caution.
        /// </summary>
        /// <param name="conns">Connections to remove.</param>
        /// <param name="scene">Scene to remove from.</param>
        public void RemoveConnectionsFromScene(NetworkConnection[] conns, Scene scene)
        {
            HashSet<NetworkConnection> hs;
            //No hashset for scene, so no connections are in scene.
            if (!SceneConnections.TryGetValueIL2CPP(scene, out hs))
                return;

            var connectionsRemoved = new List<NetworkConnection>();
            //Remove every connection from the scene.
            foreach (var c in conns)
            {
                var removed = hs.Remove(c);
                if (removed)
                {
                    c.RemoveFromScene(scene);
                    connectionsRemoved.Add(c);
                }
            }

            //If hashset is empty then remove scene from SceneConnections.
            if (hs.Count == 0)
                SceneConnections.Remove(scene);

            if (connectionsRemoved.Count > 0)
            {
                var connectionsRemovedArray = connectionsRemoved.ToArray();
                InvokeClientPresenceChange(scene, connectionsRemovedArray, false, true);
                RebuildObservers(connectionsRemovedArray);
                InvokeClientPresenceChange(scene, connectionsRemovedArray, false, false);
            }

            /* Also rebuild observers for objects owned by connection.
             * This ensures other connections will lose visibility if
             * they no longer share a scene. */
            foreach (var c in conns)
                RebuildObservers(c.Objects.ToArray());
        }

        /// <summary>
        /// Removes all connections from a scene.
        /// </summary>
        /// <param name="scene">Scene to remove connections from.</param>
        public void RemoveAllConnectionsFromScene(Scene scene)
        {
            HashSet<NetworkConnection> hs;
            //No hashset for scene, so no connections are in scene.
            if (!SceneConnections.TryGetValueIL2CPP(scene, out hs))
                return;

            //On each connection remove them from specified scene.
            foreach (var c in hs)
                c.RemoveFromScene(scene);
            //Make hashset into list for presence change.
            var connectionsRemoved = hs.ToArray();

            //Clear hashset and remove entry from sceneconnections.
            hs.Clear();
            SceneConnections.Remove(scene);

            if (connectionsRemoved.Length > 0)
            {
                InvokeClientPresenceChange(scene, connectionsRemoved, false, true);
                RebuildObservers(connectionsRemoved);
                InvokeClientPresenceChange(scene, connectionsRemoved, false, false);
            }

            /* Also rebuild observers for objects owned by connection.
             * This ensures other connections will lose visibility if
             * they no longer share a scene. */
            foreach (var c in connectionsRemoved)
                RebuildObservers(c.Objects.ToArray());
        }

        #region Can Load/Unload Scene.

        /// <summary>
        /// Returns if a scene can be loaded locally.
        /// </summary>
        /// <returns></returns>
        private bool CanLoadScene(LoadQueueData qd, SceneLookupData sld)
        {
            var s = sld.GetScene(out var foundByHandle);
            //Try to find if scene is already loaded.
            var alreadyLoaded = !string.IsNullOrEmpty(s.name);

            if (alreadyLoaded)
            {
                //Only servers can load the same scene multiple times for stacking.
                if (!qd.AsServer)
                    return false;
                //If can only load scenes which aren't loaded yet and scene is already loaded.
                if (!qd.SceneLoadData.Options.AllowStacking)
                    return false;
                /* Found by handle, this means the user is trying to specify
                 * exactly which scene to load into. When a handle is specified
                 * new instances will not be created, so a new scene cannot
                 * be loaded. */
                if (foundByHandle)
                    return false;
            }

            //Fall through.
            return true;
        }

        #endregion

        #region Helpers.

        /// <summary>
        /// Rebuilds observers for networkObjects.
        /// </summary>
        /// <param name="networkObjects"></param>
        private void RebuildObservers(NetworkObject[] networkObjects)
        {
            foreach (var nob in networkObjects)
            {
                if (nob != null && nob.IsSpawned)
                    serverManager.Objects.RebuildObservers(nob);
            }
        }

        /// <summary>
        /// Rebuilds all NetworkObjects for connection.
        /// </summary>
        internal void RebuildObservers(NetworkConnection connection)
        {
            RebuildObservers(new[] { connection });
        }

        /// <summary>
        /// Rebuilds all NetworkObjects for connections.
        /// </summary>
        internal void RebuildObservers(NetworkConnection[] connections)
        {
            foreach (var c in connections)
                serverManager.Objects.RebuildObservers(c);
        }

        /// <summary>
        /// Invokes OnClientPresenceChange start or end.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="conns"></param>
        /// <param name="added"></param>
        /// <param name="start"></param>
        private void InvokeClientPresenceChange(Scene scene, NetworkConnection[] conns, bool added, bool start)
        {
            foreach (var c in conns)
            {
                var cpc = new ClientPresenceChangeEventArgs(scene, c, added);
                if (start)
                    OnClientPresenceChangeStart?.Invoke(cpc);
                else
                    OnClientPresenceChangeEnd?.Invoke(cpc);
            }
        }

        #endregion

        #region GetScene.

        /// <summary>
        /// Gets scenes from SceneLookupData.
        /// </summary>
        /// <param name="datas"></param>
        /// <returns></returns>
        private Scene[] GetScenes(SceneLookupData[] datas)
        {
            var result = new List<Scene>();
            foreach (var sld in datas)
            {
                var s = sld.GetScene(out _);
                if (!string.IsNullOrEmpty(s.name))
                    result.Add(s);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Returns a scene by name.
        /// </summary>
        /// <param name="sceneName"></param>
        /// <returns></returns>
        public static Scene GetScene(string sceneName)
        {
            return UnitySceneManager.GetSceneByName(sceneName);
        }

        /// <summary>
        /// Returns a scene by handle.
        /// </summary>
        /// <param name="sceneHandle"></param>
        /// <returns></returns>
        public static Scene GetScene(int sceneHandle)
        {
            var count = UnitySceneManager.sceneCount;
            for (var i = 0; i < count; i++)
            {
                var s = UnitySceneManager.GetSceneAt(i);
                if (s.handle == sceneHandle)
                    return s;
            }

            return new Scene();
        }

        #endregion

        /// <summary>
        /// Returns if GlobalScenes contains scene.
        /// </summary>
        /// <returns></returns>
        private bool IsGlobalScene(Scene scene)
        {
            foreach (var t in _globalScenes)
                if (t == scene.name)
                    return true;
            return false;
        }

        /// <summary>
        /// Removes data from GlobalScenes.
        /// </summary>
        /// <param name="scene"></param>
        private void RemoveFromGlobalScenes(Scene scene)
            => RemoveFromGlobalScenes(new[] { SceneLookupData.CreateData(scene) });

        /// <summary>
        /// Removes data from GlobalScenes.
        /// </summary>
        /// <param name="lookupData"></param>
        private void RemoveFromGlobalScenes(IEnumerable<SceneLookupData> lookupData)
        {
            var newGlobalScenes = _globalScenes.ToList();
            var startCount = newGlobalScenes.Count;
            //Remove scenes.
            foreach (var t in lookupData)
                newGlobalScenes.Remove(t.Name);

            //If any were removed remake global scenes.
            if (startCount != newGlobalScenes.Count)
                _globalScenes = newGlobalScenes.ToArray();
        }

        /// <summary>
        /// Removes GlobalScenes from scenes.
        /// </summary>
        /// <param name="scenes"></param>
        /// <returns></returns>
        private void RemoveGlobalScenes(IList<Scene> scenes)
        {
            var max = scenes.Count - 1;
            for (var i = max; i >= 0; i--)
                foreach (var gs in _globalScenes)
                    if (gs == scenes[i].name)
                        scenes.RemoveAt(i);
        }

        /// <summary>
        /// Removes occupied scenes from scenes.
        /// </summary>
        /// <param name="scenes"></param>
        private void RemoveOccupiedScenes(IList<Scene> scenes)
        {
            var max = scenes.Count - 1;
            for (var i = max; i >= 0; i--)
                if (SceneConnections.ContainsKey(scenes[i]))
                    scenes.RemoveAt(i);
        }

        /// <summary>
        /// Adds a pending load for a connection.
        /// </summary>
        private void AddPendingLoad(NetworkConnection conn)
            => AddPendingLoad(new[] { conn });

        /// <summary>
        /// Adds a pending load for a connection.
        /// </summary>
        private void AddPendingLoad(NetworkConnection[] conns)
        {
            foreach (var c in conns)
            {
                /* Make sure connection is active. This should always be true
                 * but perhaps disconnect happened as scene was loading on server
                 * therefor it cannot be sent to the client.
                 * Also only authenticated clients can load scenes. */
                if (!c.IsActive || !c.Authenticated)
                    continue;

                if (_pendingClientSceneChanges.TryGetValue(c, out var result))
                    _pendingClientSceneChanges[c] = result + 1;
                else
                    _pendingClientSceneChanges[c] = 1;
            }
        }

        /// <summary>
        /// Sets the first global scene as the active scene.
        /// If a global scene is not available then FallbackActiveScene is used.
        /// </summary>
        private void SetActiveScene(Scene preferredScene = default, bool byUser = false)
        {
            //Setting active scene is not used.
            if (!_setActiveScene)
            {
                //Still invoke event with current scene.
                var s = UnitySceneManager.GetActiveScene();
                CompleteSetActive(s);
                return;
            }

            //If user specified then skip figuring it out checks.
            if (byUser && preferredScene.IsValid())
            {
                CompleteSetActive(preferredScene);
            }
            //Need to figure out which scene to use.
            else
            {
                Scene s = default;

                if (_globalScenes != null && _globalScenes.Length > 0)
                    s = GetScene(_globalScenes[0]);
                else if (preferredScene.IsValid())
                    s = preferredScene;

                /* If scene isn't set from global then make
                 * sure currently active isn't the MovedObjectsScene.
                 * If it is, then use the fallback scene. */
                if (string.IsNullOrEmpty(s.name) && UnitySceneManager.GetActiveScene() == _movedObjectsScene)
                    s = GetFallbackActiveScene();

                CompleteSetActive(s);
            }

            return;

            //Completes setting the active scene with specified value.
            void CompleteSetActive(Scene scene)
            {
                var sceneValid = scene.IsValid();
                if (sceneValid)
                    UnitySceneManager.SetActiveScene(scene);

                OnActiveSceneSet?.Invoke(byUser);
                OnActiveSceneSetInternal?.Invoke();

                if (sceneValid)
                {
                    //Also update light probes.
                    if (_lightProbeUpdating == LightProbeUpdateType.Asynchronous)
                        LightProbes.TetrahedralizeAsync();
                    else if (_lightProbeUpdating == LightProbeUpdateType.BlockThread)
                        LightProbes.Tetrahedralize();
                }
            }
        }

        /// <summary>
        /// Returns the FallbackActiveScene.
        /// </summary>
        /// <returns></returns>
        private Scene GetFallbackActiveScene()
        {
            if (string.IsNullOrEmpty(_fallbackActiveScene.name))
                _fallbackActiveScene = UnitySceneManager.CreateScene("FallbackActiveScene");
            return _fallbackActiveScene;
        }

        /// <summary>
        /// Returns the MovedObjectsScene.
        /// </summary>
        /// <returns></returns>
        private Scene GetMovedObjectsScene()
        {
            //Create moved objects scene. It will probably be used eventually. If not, no harm either way.
            if (string.IsNullOrEmpty(_movedObjectsScene.name))
                _movedObjectsScene = UnitySceneManager.CreateScene("MovedObjectsHolder");
            return _movedObjectsScene;
        }

        /// <summary>
        /// Returns the DelayedDestroyScene.
        /// </summary>
        /// <returns></returns>
        private Scene GetDelayedDestroyScene()
        {
            //Create moved objects scene. It will probably be used eventually. If not, no harm either way.
            if (string.IsNullOrEmpty(_delayedDestroyScene.name))
                _delayedDestroyScene = UnitySceneManager.CreateScene("DelayedDestroy");
            return _delayedDestroyScene;
        }

        /// <summary>
        /// Returns a preferred active scene to use.
        /// </summary>
        private Scene GetUserPreferredActiveScene(SceneLookupData sld, out bool byUser)
        {
            byUser = false;
            if (sld == null)
                return default;
            var s = sld.GetScene(out _);
            if (s.IsValid())
                byUser = true;
            return s;
        }

        #region Sanity checks.

        /// <summary>
        /// Returns if a SceneLoadData is valid.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private bool SceneDataInvalid(SceneLoadData data, bool error)
        {
            var result = data.DataInvalid();
            if (result && error)
                NetworkManager.LogError(INVALID_SCENELOADDATA);
            return result;
        }

        /// <summary>
        /// Returns if a SceneLoadData is valid.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private bool SceneDataInvalid(SceneUnloadData data, bool error)
        {
            var result = data.DataInvalid();
            if (result && error)
                NetworkManager.LogError(INVALID_SCENEUNLOADDATA);
            return result;
        }

        /// <summary>
        /// Returns if connection is active for server or client in association with AsServer.
        /// </summary>
        /// <param name="asServer"></param>
        /// <returns></returns>
        private bool ConnectionActive(bool asServer)
            => asServer ? NetworkManager.IsServer : NetworkManager.IsClient;

        /// <summary>
        /// Returns if a method can execute.
        /// </summary>
        /// <param name="asServer"></param>
        /// <param name="warn"></param>
        /// <returns></returns>
        private bool CanExecute(bool asServer, bool warn)
        {
            bool result;
            if (asServer)
            {
                result = NetworkManager.IsServer;
                if (!result && warn)
                    NetworkManager.LogWarning($"Method cannot be called as the server is not active.");
            }
            else
            {
                result = NetworkManager.IsClient;
                if (!result && warn)
                    NetworkManager.LogWarning($"Method cannot be called as the client is not active.");
            }

            return result;
        }

        #endregion
    }
}