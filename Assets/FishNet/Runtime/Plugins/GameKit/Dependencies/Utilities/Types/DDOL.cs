using UnityEngine;

namespace GameKit.Dependencies.Utilities.Types
{
    public class DDOL : MonoBehaviour
    {
        #region Public.
        /// <summary>
        /// Created instance of DDOL.
        /// </summary>
        private static DDOL _instance;
        #endregion

        /// <summary>
        /// Returns the current DDOL or creates one if not yet created.
        /// </summary>
        public static DDOL GetDDOL()
        {
            //Not yet made.
            if (_instance != null) return _instance;
            var obj = new GameObject
            {
                name = "FirstGearGames DDOL"
            };
            var ddol = obj.AddComponent<DDOL>();
            DontDestroyOnLoad(ddol);
            _instance = ddol;
            return ddol;
            //Already  made.
        }
    }


}