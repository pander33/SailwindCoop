using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    /// <summary>Session lifetime deduplication; record before Unity effects and retain the actual result.</summary>
    internal sealed class OperationLedger<T> where T : class
    {
        private readonly Dictionary<ulong, T> _results = new Dictionary<ulong, T>();
        internal bool TryGet(uint actor, uint operation, out T result)
            => _results.TryGetValue(((ulong)actor << 32) | operation, out result);
        internal bool TryBegin(uint actor, uint operation, T initial, out T previous)
        {
            ulong key = ((ulong)actor << 32) | operation;
            if (_results.TryGetValue(key, out previous)) return false;
            _results[key] = initial; return true;
        }
        internal void Clear() => _results.Clear();
    }
}
