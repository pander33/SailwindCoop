using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>Host side of one mod file transfer: reads the file in manifest-sized chunks.</summary>
    public sealed class ModUpload : IDisposable
    {
        public readonly ushort Mod;
        public readonly ushort File;
        private readonly FileStream _stream;
        private readonly int _size;
        private int _sent;
        private int _index;

        /// <summary>Opens the file. Throws when it no longer has the size the manifest promised:
        /// the client would reject it by hash anyway, after downloading all of it.</summary>
        public ModUpload(string path, ModFile file, ushort mod, ushort fileIndex)
        {
            Mod = mod; File = fileIndex; _size = file.Size;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (_stream.Length != _size)
            {
                _stream.Dispose();
                throw new InvalidDataException("file size changed since the manifest was built");
            }
        }

        public bool Done => _sent >= _size;

        public byte[] NextChunk(out int index)
        {
            index = _index++;
            var data = new byte[Math.Min(ModLimits.ChunkSize, _size - _sent)];
            int read = 0;
            while (read < data.Length)
            {
                int n = _stream.Read(data, read, data.Length - read);
                if (n <= 0) throw new EndOfStreamException("mod file ended early");
                read += n;
            }
            _sent += read;
            return data;
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>
    /// Client side of a mod download. Files are written to a staging folder outside
    /// <c>BepInEx/plugins</c> (the loader scans that tree recursively, so a half-written DLL there
    /// would load on the next start), checked against the manifest's size and SHA-256, and only then
    /// moved into place — never over an existing file or folder.
    /// </summary>
    public sealed class ModDownload : IDisposable
    {
        private readonly string _staging;
        private readonly string _plugins;
        private readonly List<KeyValuePair<int, ModEntry>> _mods = new List<KeyValuePair<int, ModEntry>>();
        private int _mod;          // position in _mods
        private int _file = -1;    // file of the current mod being received
        private FileStream _stream;
        private SHA256 _sha;
        private int _received;
        private int _nextChunk;

        public long TotalBytes { get; private set; }
        public long ReceivedBytes { get; private set; }
        public int ModCount => _mods.Count;

        /// <param name="staging">Emptied and recreated; must be on the same volume as the plugins.</param>
        /// <param name="wanted">Manifest index and entry of every mod to fetch.</param>
        public ModDownload(string staging, string pluginsRoot, IEnumerable<KeyValuePair<int, ModEntry>> wanted)
        {
            _staging = staging; _plugins = pluginsRoot;
            foreach (var mod in wanted)
            {
                string problem = ModPathRules.Validate(mod.Value);
                if (problem == null && !mod.Value.Downloadable) problem = "not offered for download";
                if (problem == null && mod.Value.Files.Length == 0) problem = "no files";
                if (problem != null) throw new InvalidDataException("mod '" + mod.Value.DisplayName + "': " + problem);
                _mods.Add(mod);
                TotalBytes += mod.Value.TotalBytes;
            }
            if (Directory.Exists(_staging)) Directory.Delete(_staging, true);
            Directory.CreateDirectory(_staging);
        }

        /// <summary>Opens the next file to request. False once every file has arrived.</summary>
        public bool TryNext(out ushort mod, out ushort file)
        {
            mod = 0; file = 0;
            CloseStream();
            _file++;
            while (_mod < _mods.Count && _file >= _mods[_mod].Value.Files.Length) { _mod++; _file = 0; }
            if (_mod >= _mods.Count) return false;

            ModEntry entry = _mods[_mod].Value;
            string path = ModPathRules.Resolve(StageRoot(_mod), entry.Files[_file].Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _sha = SHA256.Create();
            _received = 0; _nextChunk = 0;
            mod = (ushort)_mods[_mod].Key; file = (ushort)_file;
            return true;
        }

        public void OnChunk(ushort mod, ushort file, int index, byte[] data)
        {
            ModFile expected = Current(mod, file);
            int size = Math.Min(ModLimits.ChunkSize, expected.Size - _received);
            if (index != _nextChunk || data == null || data.Length != size || size == 0)
                throw new InvalidDataException("unexpected chunk " + index + " of '" + expected.Path + "'");
            _stream.Write(data, 0, data.Length);
            _sha.TransformBlock(data, 0, data.Length, null, 0);
            _received += data.Length; ReceivedBytes += data.Length; _nextChunk++;
        }

        /// <summary>Closes the current file and verifies it. Throws when the host could not send it
        /// or the bytes do not match the manifest.</summary>
        public void OnEnd(ushort mod, ushort file, bool ok)
        {
            ModFile expected = Current(mod, file);
            if (!ok) throw new InvalidDataException("the host could not send '" + expected.Path + "'");
            if (_received != expected.Size)
                throw new InvalidDataException("'" + expected.Path + "' is incomplete (" + _received + " of " + expected.Size + " bytes)");
            _sha.TransformFinalBlock(new byte[0], 0, 0);
            if (!ModCatalog.Equal(_sha.Hash, expected.Hash))
                throw new InvalidDataException("'" + expected.Path + "' does not match the host's checksum");
            CloseStream();
        }

        /// <summary>Moves every staged mod into the plugins folder and returns what was installed
        /// (paths relative to it). Call only after <see cref="TryNext"/> returned false.</summary>
        public List<string> Commit(string source)
        {
            if (_mod < _mods.Count) throw new InvalidOperationException("download is not complete");
            CloseStream();
            // Resolve every target first: nothing moves unless all of them are free.
            var targets = new string[_mods.Count];
            for (int i = 0; i < _mods.Count; i++)
            {
                ModEntry entry = _mods[i].Value;
                if (string.IsNullOrEmpty(entry.Folder))
                {
                    targets[i] = ModPathRules.Resolve(_plugins, entry.Files[0].Path);
                    if (File.Exists(targets[i]) || Directory.Exists(targets[i]))
                        throw new IOException("'" + entry.Files[0].Path + "' already exists in the plugins folder");
                    continue;
                }
                string name = entry.Folder;
                if (Exists(name)) name = entry.Folder + " (coop)";
                if (Exists(name)) throw new IOException("folder '" + entry.Folder + "' already exists in the plugins folder");
                for (int j = 0; j < i; j++)
                    if (string.Equals(targets[j], ModPathRules.Resolve(_plugins, name), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("two mods would install into '" + name + "'");
                targets[i] = ModPathRules.Resolve(_plugins, name);
            }

            var installed = new List<string>();
            for (int i = 0; i < _mods.Count; i++)
            {
                ModEntry entry = _mods[i].Value;
                if (string.IsNullOrEmpty(entry.Folder))
                    File.Move(ModPathRules.Resolve(StageRoot(i), entry.Files[0].Path), targets[i]);
                else
                {
                    File.WriteAllText(Path.Combine(StageRoot(i), ModCatalog.MarkerFile), Marker(entry, source), new UTF8Encoding(false));
                    Directory.Move(StageRoot(i), targets[i]);
                }
                installed.Add(Path.GetFileName(targets[i]));
            }
            return installed;
        }

        public void Dispose()
        {
            CloseStream();
            try { if (Directory.Exists(_staging)) Directory.Delete(_staging, true); } catch { }
        }

        private bool Exists(string name)
        {
            string path = ModPathRules.Resolve(_plugins, name);
            return Directory.Exists(path) || File.Exists(path);
        }

        private string StageRoot(int position) => Path.Combine(_staging, position.ToString());

        private ModFile Current(ushort mod, ushort file)
        {
            if (_stream == null || _mod >= _mods.Count || _mods[_mod].Key != mod || _file != file)
                throw new InvalidDataException("data for a file that was not requested (mod " + mod + ", file " + file + ")");
            return _mods[_mod].Value.Files[_file];
        }

        private void CloseStream()
        {
            if (_stream != null) { _stream.Dispose(); _stream = null; }
            if (_sha != null) { _sha.Dispose(); _sha = null; }
        }

        private static string Marker(ModEntry entry, string source)
        {
            var text = new StringBuilder();
            text.Append("{\n  \"installedBy\": \"SailwindCoop\",\n  \"host\": \"").Append(Escape(source));
            text.Append("\",\n  \"installedUtc\": \"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            text.Append("\",\n  \"files\": [");
            for (int i = 0; i < entry.Files.Length; i++)
            {
                text.Append(i == 0 ? "\n" : ",\n").Append("    { \"path\": \"").Append(Escape(entry.Files[i].Path));
                text.Append("\", \"size\": ").Append(entry.Files[i].Size).Append(", \"sha256\": \"");
                foreach (byte b in entry.Files[i].Hash) text.Append(b.ToString("x2"));
                text.Append("\" }");
            }
            return text.Append("\n  ]\n}\n").ToString();
        }

        private static string Escape(string value)
        {
            var text = new StringBuilder();
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32) text.Append(' ');
                else text.Append(c);
            }
            return text.ToString();
        }
    }
}
