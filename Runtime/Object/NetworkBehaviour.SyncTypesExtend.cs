using System.Buffers;

namespace FishNet.Object
{
    public abstract partial class NetworkBehaviour
    {
        private void OnDestroy()
        {
            if(_syncTypeWriters != null)
                foreach (var syncType in _syncTypeWriters)
                    syncType?.Dispose();
        }
    }
}