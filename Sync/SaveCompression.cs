using System;
using System.IO;
using System.IO.Compression;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Gzip for the world snapshot. A reliable LiteNetLib channel moves one 64-packet window per round
    /// trip, so the time the host stays frozen for a joining client grows with the size of the save
    /// times the ping; a Sailwind save shrinks to about a quarter.
    /// </summary>
    internal static class SaveCompression
    {
        private const int MaxRatio = 1100;

        /// <summary>The packed bytes, or null when packing failed, did not make the data smaller, or
        /// does not unpack to the same bytes here (the caller then sends the data as it is).</summary>
        internal static byte[] Pack(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            try
            {
                byte[] packed;
                using (var buffer = new MemoryStream(raw.Length / 4 + 64))
                {
                    using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                        gzip.Write(raw, 0, raw.Length);
                    packed = buffer.ToArray();
                }
                if (packed.Length >= raw.Length) return null;
                // The joining side runs the same runtime: what unpacks here unpacks there.
                byte[] check = Unpack(packed, raw.Length);
                for (int i = 0; i < raw.Length; i++)
                    if (check[i] != raw[i]) return null;
                return packed;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Unpacks to exactly <paramref name="rawLength"/> bytes; anything else is a broken
        /// transfer. The length arrives from the network, so no more than it is ever read.</summary>
        internal static byte[] Unpack(byte[] packed, int rawLength)
        {
            if (packed == null || rawLength <= 0) throw new InvalidDataException("empty world snapshot");
            // Deflate cannot expand beyond about 1032:1. A larger announced length is a broken header,
            // and it must not size the buffer: the packed bytes were received, this number was only told.
            if (rawLength > (long)packed.Length * MaxRatio)
                throw new InvalidDataException("world snapshot of " + packed.Length + " bytes cannot unpack to " + rawLength);
            var raw = new byte[rawLength];
            using (var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                int total = 0, read;
                while (total < rawLength && (read = gzip.Read(raw, total, rawLength - total)) > 0) total += read;
                if (total != rawLength)
                    throw new InvalidDataException("world snapshot unpacked to " + total + " of " + rawLength + " bytes");
                if (gzip.Read(new byte[1], 0, 1) != 0)
                    throw new InvalidDataException("world snapshot is longer than the announced " + rawLength + " bytes");
            }
            return raw;
        }
    }
}
