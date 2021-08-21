﻿namespace FishNet.Managing.Scened.Data
{
    public class UnloadParams
    {
        /// <summary>
        /// Objects which are included in callbacks on the server when unloading a scene. Can be useful for including unique information about the scene, such as match id. These are not sent to clients; use ClientParams for this.
        /// </summary>
        [System.NonSerialized]
        public object[] ServerParams = null;
        /// <summary>
        /// Bytes which are sent to clients during scene unloads. Can contain any information.
        /// </summary>
        public byte[] ClientParams = null;
    }

}