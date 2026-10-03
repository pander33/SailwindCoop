using System;
using System.IO;

namespace SailwindCoop.Sync
{
    /// <summary>Commit a fully written file on the same volume; never delete the old target.</summary>
    internal static class AtomicSaveFile
    {
        public static void Write(string path, Action<Stream> serialize, bool preserveBackup = false)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    serialize(stream);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(temp, path, preserveBackup ? null : path + ".bak");
                else
                    File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public static T Read<T>(string path, Func<Stream, T> deserialize, out bool recovered)
        {
            recovered = false;
            try { using (var stream = File.OpenRead(path)) return deserialize(stream); }
            catch (Exception primary)
            {
                try
                {
                    using (var stream = File.OpenRead(path + ".bak"))
                    {
                        T result = deserialize(stream);
                        recovered = true;
                        return result;
                    }
                }
                catch (Exception backup) { throw new AggregateException("Save and backup could not be read", primary, backup); }
            }
        }
    }
}
