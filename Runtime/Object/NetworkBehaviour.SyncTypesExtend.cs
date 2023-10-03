using System.Buffers;

namespace FishNet.Object
{
    public abstract partial class NetworkBehaviour
    {
        protected virtual void OnDestroy()
        {
            if (_syncTypeWriters == null) return;
            foreach (var syncType in _syncTypeWriters)
                syncType?.Dispose();
        }
    }
}