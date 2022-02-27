﻿using FishNet.Transporting;
using UnityEngine;
using MultipassTransport = Multipass.Multipass;

namespace FishNet.Managing.Transporting
{

    /// <summary>
    /// Communicates with the Transport to send and receive data.
    /// </summary>
    public sealed partial class TransportManager : MonoBehaviour
    {
        #region Public.
        /// <summary>
        /// Returns IsLocalTransport for the current transport.
        /// </summary>
        public bool IsLocalTransport(int connectionId) => (Transport == null) ? false : Transport.IsLocalTransport(connectionId);
        #endregion

        /// <summary>
        /// Gets transport of type T.
        /// </summary>
        /// <returns>Returns the found transport which is of type T. Returns null if not found.</returns>
        public Transport GetTransport<T>()
        {
            //If using multipass try to find the correct transport.
            if (Transport is MultipassTransport mp)
            {
                return mp.GetTransport<T>();
            }
            //Not using multipass.
            else
            {
                if (Transport.GetType() == typeof(T))
                    return Transport;
                else
                    return null;
            }
        }
    }

}