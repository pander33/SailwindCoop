using System;
using System.IO;

namespace SailwindCoop.Sync
{
    /// <summary>Collects one identified revision without exposing partial replacement state.</summary>
    internal sealed class ChunkAccumulator<T>
    {
        private string _key;
        private T[][] _chunks;
        private int _received;
        internal T[] Add(string key, int index, int count, T[] data)
        {
            if (count <= 0 || index < 0 || index >= count || data == null) throw new InvalidDataException("invalid chunk format");
            if (_key != key) { _key = key; _chunks = new T[count][]; _received = 0; }
            if (_chunks.Length != count) throw new InvalidDataException("inconsistent chunk count");
            if (_chunks[index] == null) { _chunks[index] = data; _received++; }
            if (_received != count) return null;
            int total = 0; foreach (var chunk in _chunks) total = checked(total + chunk.Length);
            var result = new T[total]; int offset = 0;
            foreach (var chunk in _chunks) { Array.Copy(chunk, 0, result, offset, chunk.Length); offset += chunk.Length; }
            Clear(); return result;
        }
        internal void Clear() { _key = null; _chunks = null; _received = 0; }
    }
}
