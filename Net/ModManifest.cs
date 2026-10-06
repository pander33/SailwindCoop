using System;
using System.IO;

namespace SailwindCoop.Net
{
    /// <summary>Ceilings on everything a mod manifest or a mod file transfer may carry. Every number
    /// here sizes an array or a file on the receiving machine, so each is checked before use.</summary>
    public static class ModLimits
    {
        public const int MaxMods = 64;
        public const int MaxPluginsPerMod = 16;
        public const int MaxFilesPerMod = 512;
        public const int MaxFileBytes = 256 * 1024 * 1024;
        public const long MaxModBytes = 512L * 1024 * 1024;
        public const int MaxPathChars = 200;
        public const int MaxTextChars = 128;
        public const int ChunkSize = 16 * 1024;
        public const int HashBytes = 32;
    }

    /// <summary>One BepInEx plugin inside a shared mod.</summary>
    public sealed class ModPlugin
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
    }

    /// <summary>One file of a shared mod. <see cref="Path"/> is relative to the mod's folder and
    /// uses '/' on the wire.</summary>
    public sealed class ModFile
    {
        public string Path = "";
        public int Size;
        public byte[] Hash = Array.Empty<byte>();
    }

    /// <summary>
    /// One shared mod: a top-level folder of <c>BepInEx/plugins</c>, or a single DLL that sits
    /// directly in it (<see cref="Folder"/> empty, exactly one file).
    /// </summary>
    public sealed class ModEntry
    {
        public string Folder = "";
        /// <summary>False when the host listed the mod for comparison only (too large, too many
        /// files, an unsafe file name): <see cref="Files"/> then holds its DLLs alone.</summary>
        public bool Downloadable;
        public ModPlugin[] Plugins = new ModPlugin[0];
        public ModFile[] Files = new ModFile[0];

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Folder)) return Folder;
                if (Files.Length > 0) return Files[0].Path;
                return Plugins.Length > 0 ? Plugins[0].Name : "?";
            }
        }

        public long TotalBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Files.Length; i++) total += Files[i].Size;
                return total;
            }
        }
    }

    /// <summary>
    /// File-name rules for anything a host names in a manifest. The receiver writes these paths to
    /// its own disk, so a name is accepted only when it cannot leave the folder it is written into.
    /// </summary>
    public static class ModPathRules
    {
        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        /// <summary>One path segment: a plain file or folder name.</summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > ModLimits.MaxPathChars) return false;
            if (name == "." || name == "..") return false;
            // Windows silently drops a trailing dot or space, so "x.dll." and "x.dll" are one file.
            char last = name[name.Length - 1];
            if (last == '.' || last == ' ' || name[0] == ' ') return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 32 || c == '/' || c == '\\' || c == ':') return false;
                if (Array.IndexOf(InvalidNameChars, c) >= 0) return false;
            }
            return !IsDeviceName(name);
        }

        /// <summary>A relative path with '/' separators, every segment a safe name.</summary>
        public static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > ModLimits.MaxPathChars) return false;
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
                if (!IsSafeName(parts[i])) return false;
            return true;
        }

        /// <summary>Joins a checked relative path onto <paramref name="root"/> and verifies the
        /// result still lies inside it. Throws when it does not.</summary>
        public static string Resolve(string root, string relativePath)
        {
            if (!IsSafeRelativePath(relativePath))
                throw new InvalidDataException("unsafe mod file path: " + relativePath);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("mod file path leaves its folder: " + relativePath);
            return full;
        }

        /// <summary>Checks a whole entry as received from a host. Returns null when it is acceptable,
        /// otherwise the reason it is not.</summary>
        public static string Validate(ModEntry entry)
        {
            if (entry == null) return "empty entry";
            if (entry.Plugins == null || entry.Plugins.Length == 0 || entry.Plugins.Length > ModLimits.MaxPluginsPerMod)
                return "plugin count";
            if (entry.Files == null || entry.Files.Length > ModLimits.MaxFilesPerMod) return "file count";
            bool single = string.IsNullOrEmpty(entry.Folder);
            if (!single && !IsSafeName(entry.Folder)) return "folder name";
            if (single && entry.Downloadable && entry.Files.Length != 1) return "single-file mod with several files";
            long total = 0;
            for (int i = 0; i < entry.Files.Length; i++)
            {
                ModFile file = entry.Files[i];
                if (file == null) return "empty file";
                if (!IsSafeRelativePath(file.Path)) return "file path";
                if (single && file.Path.IndexOf('/') >= 0) return "single-file mod in a subfolder";
                if (file.Size < 0 || file.Size > ModLimits.MaxFileBytes) return "file size";
                if (file.Hash == null || file.Hash.Length != ModLimits.HashBytes) return "file hash";
                for (int j = 0; j < i; j++)
                    if (string.Equals(entry.Files[j].Path, file.Path, StringComparison.OrdinalIgnoreCase))
                        return "duplicate file path";
                total += file.Size;
            }
            if (total > ModLimits.MaxModBytes) return "mod size";
            return null;
        }

        private static bool IsDeviceName(string name)
        {
            int dot = name.IndexOf('.');
            string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '0' && stem[3] <= '9')
                return true;
            return false;
        }
    }
}
