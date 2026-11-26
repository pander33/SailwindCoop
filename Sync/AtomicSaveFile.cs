using System;
using System.IO;

namespace SailwindCoop.Sync
{
    /// <summary>Commit a fully written file on the same volume; never delete the old target.</summary>
    internal static class AtomicSaveFile
    {
        public static void Write(string path, Action<Stream> serialize, bool preserveBackup = false)
            => WriteStaged(path, serialize, temp => {
                if (File.Exists(path))
                    File.Replace(temp, path, preserveBackup ? null : path + ".bak");
                else
                    File.Move(temp, path);
            });

        internal static void WriteWorld(string path, Func<int, string> backupPath, Action<Stream> serialize)
            => WriteStaged(path, serialize, temp => {
                const int backups = 5;
                File.Delete(backupPath(backups));
                for (int i = backups - 1; i >= 1; i--)
                {
                    string backup = backupPath(i);
                    if (File.Exists(backup)) File.Move(backup, backupPath(i + 1));
                }
                if (File.Exists(path)) File.Replace(temp, path, backupPath(1));
                else File.Move(temp, path);
            });

        private static void WriteStaged(string path, Action<Stream> serialize, Action<string> commit)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    serialize(stream);
                    stream.Flush(true);
                }
                commit(temp);
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
