using System.Buffers;

namespace FishNet.Object
{
    public abstract partial class NetworkBehaviour
    {
        private void OnDestroy()
        {
            foreach (var syncType in _syncTypeWriters)
                syncType.Dispose();
            ArrayPool<SyncTypeWriter>.Shared.Return(_syncTypeWriters);
        }
    }
}