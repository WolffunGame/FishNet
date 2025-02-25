﻿using System.Collections.Generic;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;
using UnityScene = UnityEngine.SceneManagement.Scene;
using System.Collections;
using Cysharp.Threading.Tasks;

namespace FishNet.Managing.Scened
{

    public class DefaultSceneProcessor : SceneProcessorBase
    {
        #region Private.
        /// <summary>
        /// Currently active loading AsyncOperations.
        /// </summary>
        protected List<AsyncOperation> LoadingAsyncOperations = new List<AsyncOperation>();
        /// <summary>
        /// A collection of scenes used both for loading and unloading.
        /// </summary>
        protected List<UnityScene> Scenes = new List<UnityScene>();
        /// <summary>
        /// Current AsyncOperation being processed.
        /// </summary>
        protected AsyncOperation CurrentAsyncOperation;
        #endregion

        /// <summary>
        /// Called when scene loading has begun.
        /// </summary>
        public override void LoadStart(LoadQueueData queueData)
        {
            base.LoadStart(queueData);
            ResetValues();
        }

        public override void LoadEnd(LoadQueueData queueData)
        {
            base.LoadEnd(queueData);
            ResetValues();
        }

        /// <summary>
        /// Resets values for a fresh load or unload.
        /// </summary>
        private void ResetValues()
        {
            CurrentAsyncOperation = null;
            LoadingAsyncOperations.Clear();
        }

        /// <summary>
        /// Called when scene unloading has begun within an unload operation.
        /// </summary>
        /// <param name="queueData"></param>
        public override void UnloadStart(UnloadQueueData queueData)
        {
            base.UnloadStart(queueData);
            Scenes.Clear();
        }

        /// <summary>
        /// Begin loading a scene using an async method.
        /// </summary>
        /// <param name="sceneName">Scene name to load.</param>
        public override void BeginLoadAsync(string sceneName, UnityEngine.SceneManagement.LoadSceneParameters parameters)
        {
            AsyncOperation ao = UnitySceneManager.LoadSceneAsync(sceneName, parameters);
            LoadingAsyncOperations.Add(ao);
            
            CurrentAsyncOperation = ao;
            CurrentAsyncOperation.allowSceneActivation = false;
        }

        /// <summary>
        /// Begin unloading a scene using an async method.
        /// </summary>
        /// <param name="sceneName">Scene name to unload.</param>
        public override void BeginUnloadAsync(UnityScene scene)
        {
            CurrentAsyncOperation = UnitySceneManager.UnloadSceneAsync(scene);
        }

        /// <summary>
        /// Returns if a scene load or unload percent is done.
        /// </summary>
        /// <returns></returns>
        public override bool IsPercentComplete()
        {
            return (GetPercentComplete() >= 0.9f);
        }

        /// <summary>
        /// Returns the progress on the current scene load or unload.
        /// </summary>
        /// <returns></returns>
        public override float GetPercentComplete()
        {
            return (CurrentAsyncOperation == null) ? 1f : CurrentAsyncOperation.progress;
        }

        /// <summary>
        /// Adds a loaded scene.
        /// </summary>
        /// <param name="scene">Scene loaded.</param>
        public override void AddLoadedScene(UnityScene scene)
        {
            base.AddLoadedScene(scene);
            Scenes.Add(scene);
        }

        /// <summary>
        /// Returns scenes which were loaded during a load operation.
        /// </summary>
        public override List<UnityScene> GetLoadedScenes()
        {
            return Scenes;
        }

        /// <summary>
        /// Activates scenes which were loaded.
        /// </summary>
        public override void ActivateLoadedScenes()
        {
            foreach (AsyncOperation ao in LoadingAsyncOperations)
                ao.allowSceneActivation = true;
        }

        /// <summary>
        /// Returns if all asynchronized tasks are considered IsDone.
        /// </summary>
        /// <returns></returns>
        public override async UniTask WaitAsyncIsDone()
        {
            foreach (var ao in LoadingAsyncOperations)
                await ao;
        }
    }
}