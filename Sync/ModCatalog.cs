using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>A plugin the loader reports as loaded on this machine.</summary>
    public sealed class LoadedPlugin
    {
        public string Guid = "";
        public string Name = "";
        public string Version = "";
        /// <summary>Full path of the plugin DLL.</summary>
        public string Location = "";
    }

    public enum ModDiffKind : byte
    {
        Same,
        /// <summary>None of the mod's plugins is loaded here.</summary>
        Missing,
        /// <summary>Loaded here, but another version or another build.</summary>
        Different,
    }

    public sealed class ModDiff
    {
        public int Index;
        public ModEntry Entry;
        public ModDiffKind Kind;
        /// <summary>For <see cref="ModDiffKind.Different"/>: what differs, for the player.</summary>
        public string Detail = "";
    }

    /// <summary>
    /// File-system side of mod sharing: lists the loaded mods as manifest entries and compares a
    /// host's manifest with this machine. No Unity or loader types, so the smoke tool runs it.
    /// </summary>
    public static class ModCatalog
    {
        /// <summary>Files this mod writes into another mod's folder start with this; never listed.</summary>
        public const string MarkerPrefix = ".coop-";
#if !THUNDERSTORE
        public const string MarkerFile = MarkerPrefix + "installed.json";
#endif

        /// <summary>Where a plugin's mod starts: its top-level folder under <paramref name="pluginsRoot"/>
        /// (<paramref name="folder"/> set), or the DLL itself when it sits directly in the root
        /// (<paramref name="folder"/> empty). False for a plugin outside the root.</summary>
        public static bool TryUnit(string pluginsRoot, string location, out string folder, out string relative)
        {
            folder = ""; relative = "";
            if (string.IsNullOrEmpty(pluginsRoot) || string.IsNullOrEmpty(location)) return false;
            string root = Path.GetFullPath(pluginsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(location);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = full.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
            int slash = rest.IndexOf('/');
            if (slash < 0) { relative = rest; return true; }
            folder = rest.Substring(0, slash);
            relative = rest.Substring(slash + 1);
            return true;
        }

        /// <summary>
        /// Builds the manifest entries for the loaded plugins. A folder holding any excluded plugin is
        /// left out whole. A mod that cannot be shared as files (too large, too many files, a file
        /// name the receiver would refuse) is still listed, with its DLLs only, so a client can at
        /// least compare versions.
        /// </summary>
        public static List<ModEntry> Build(string pluginsRoot, IEnumerable<LoadedPlugin> plugins,
                                           ICollection<string> excludedGuids, Action<string> log)
        {
            var units = new SortedDictionary<string, List<LoadedPlugin>>(StringComparer.OrdinalIgnoreCase);
            var excludedUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LoadedPlugin plugin in plugins)
            {
                if (!TryUnit(pluginsRoot, plugin.Location, out string folder, out string relative)) continue;
                string key = folder.Length > 0 ? folder + "/" : relative;
                if (Contains(excludedGuids, plugin.Guid)) excludedUnits.Add(key);
                if (!units.TryGetValue(key, out var list)) units[key] = list = new List<LoadedPlugin>();
                list.Add(plugin);
            }

            var result = new List<ModEntry>();
            foreach (var unit in units)
            {
                if (excludedUnits.Contains(unit.Key)) continue;
                if (result.Count >= ModLimits.MaxMods)
                {
                    log?.Invoke("mod list is full (" + ModLimits.MaxMods + "); '" + unit.Key + "' is not listed");
                    continue;
                }
                try
                {
                    bool isFolder = unit.Key.EndsWith("/");
                    ModEntry entry = isFolder
                        ? BuildFolder(pluginsRoot, unit.Key.TrimEnd('/'), log)
                        : BuildSingle(pluginsRoot, unit.Key);
                    if (entry == null) continue;
                    unit.Value.Sort((a, b) => string.CompareOrdinal(a.Guid, b.Guid));
                    int count = Math.Min(unit.Value.Count, ModLimits.MaxPluginsPerMod);
                    entry.Plugins = new ModPlugin[count];
                    for (int i = 0; i < count; i++)
                        entry.Plugins[i] = new ModPlugin { Guid = unit.Value[i].Guid, Name = unit.Value[i].Name, Version = unit.Value[i].Version };
                    result.Add(entry);
                }
                catch (Exception e)
                {
                    log?.Invoke("mod '" + unit.Key + "' could not be read and is not listed: " + e.Message);
                }
            }
            return result;
        }

        private static ModEntry BuildSingle(string pluginsRoot, string fileName)
        {
            if (!ModPathRules.IsSafeName(fileName)) return null;
            var info = new FileInfo(Path.Combine(pluginsRoot, fileName));
            if (!info.Exists || info.Length > ModLimits.MaxFileBytes) return null;
            return new ModEntry
            {
                Folder = "",
                Complete = true,
                Files = new[] { new ModListFile { Path = fileName, Size = (int)info.Length, Hash = HashFile(info.FullName) } },
            };
        }

        private static ModEntry BuildFolder(string pluginsRoot, string folder, Action<string> log)
        {
            if (!ModPathRules.IsSafeName(folder)) return null;
            string root = Path.Combine(pluginsRoot, folder);
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
            var paths = new List<string>();
            var sizes = new List<long>();
            string reason = null;
            long total = 0;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetFullPath(file).Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                if (Skipped(relative)) continue;
                long length = new FileInfo(file).Length;
                if (!ModPathRules.IsSafeRelativePath(relative)) reason = reason ?? "file name '" + relative + "'";
                else if (length > ModLimits.MaxFileBytes) reason = reason ?? "file '" + relative + "' is too large";
                paths.Add(relative); sizes.Add(length); total += length;
            }
            if (paths.Count > ModLimits.MaxFilesPerMod) reason = reason ?? paths.Count + " files";
            if (total > ModLimits.MaxModBytes) reason = reason ?? "total size";

            var entry = new ModEntry { Folder = folder, Complete = reason == null };
            if (reason != null)
                log?.Invoke("mod '" + folder + "' is listed for comparison only (" + reason + ")");
            var files = new List<ModListFile>();
            string[] order = paths.ToArray();
            long[] orderSizes = sizes.ToArray();
            Array.Sort(order, orderSizes, StringComparer.Ordinal);
            for (int i = 0; i < order.Length && files.Count < ModLimits.MaxFilesPerMod; i++)
            {
                // Comparison-only entries keep just the DLLs: that is all the diff reads.
                if (reason != null && (!IsDll(order[i]) || !ModPathRules.IsSafeRelativePath(order[i]) ||
                                       orderSizes[i] > ModLimits.MaxFileBytes)) continue;
                files.Add(new ModListFile
                {
                    Path = order[i],
                    Size = (int)orderSizes[i],
                    Hash = HashFile(Path.Combine(root, order[i].Replace('/', Path.DirectorySeparatorChar))),
                });
            }
            entry.Files = files.ToArray();
            return entry;
        }

        /// <summary>Compares a host manifest with the plugins loaded here.</summary>
        public static List<ModDiff> Compare(IList<ModEntry> hostMods, string pluginsRoot, IEnumerable<LoadedPlugin> localPlugins)
        {
            var local = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
            foreach (LoadedPlugin plugin in localPlugins)
                if (!string.IsNullOrEmpty(plugin.Guid)) local[plugin.Guid] = plugin;

            var result = new List<ModDiff>();
            for (int i = 0; i < hostMods.Count; i++)
            {
                ModEntry entry = hostMods[i];
                var diff = new ModDiff { Index = i, Entry = entry, Kind = ModDiffKind.Same };
                LoadedPlugin anchor = null;
                int present = 0;
                foreach (ModPlugin plugin in entry.Plugins)
                {
                    if (!local.TryGetValue(plugin.Guid, out LoadedPlugin mine)) continue;
                    present++;
                    anchor = anchor ?? mine;
                    if (!string.Equals(mine.Version, plugin.Version, StringComparison.Ordinal) && diff.Detail.Length == 0)
                        diff.Detail = "host " + plugin.Version + ", you " + mine.Version;
                }
                if (present == 0) diff.Kind = ModDiffKind.Missing;
                else if (diff.Detail.Length > 0) diff.Kind = ModDiffKind.Different;
                else if (present < entry.Plugins.Length)
                {
                    diff.Kind = ModDiffKind.Different;
                    diff.Detail = "some of its plugins are not loaded here";
                }
                else if (!SameDlls(entry, pluginsRoot, anchor))
                {
                    diff.Kind = ModDiffKind.Different;
                    diff.Detail = "same version, different build";
                }
                result.Add(diff);
            }
            return result;
        }

        /// <summary>Loaded plugins the host did not list, by name. Excluded GUIDs are skipped.</summary>
        public static List<string> LocalOnly(IList<ModEntry> hostMods, IEnumerable<LoadedPlugin> localPlugins,
                                             ICollection<string> excludedGuids)
        {
            var host = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry entry in hostMods)
                foreach (ModPlugin plugin in entry.Plugins) host.Add(plugin.Guid);
            var result = new List<string>();
            foreach (LoadedPlugin plugin in localPlugins)
                if (!host.Contains(plugin.Guid) && !Contains(excludedGuids, plugin.Guid))
                    result.Add(string.IsNullOrEmpty(plugin.Name) ? plugin.Guid : plugin.Name);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool SameDlls(ModEntry entry, string pluginsRoot, LoadedPlugin anchor)
        {
            if (anchor == null || !TryUnit(pluginsRoot, anchor.Location, out string folder, out string relative)) return true;
            string anchorName = Path.GetFileName(anchor.Location);
            foreach (ModListFile file in entry.Files)
            {
                if (!IsDll(file.Path)) continue;
                try
                {
                    // The same mod may be laid out differently here (another folder name, a lone DLL
                    // in the root, a renamed file), so the loaded plugin DLL is the fallback.
                    string path = null;
                    if (folder.Length > 0 && !string.IsNullOrEmpty(entry.Folder))
                    {
                        path = ModPathRules.Resolve(Path.Combine(pluginsRoot, folder), file.Path);
                        if (!File.Exists(path)) path = null;
                    }
                    if (path == null && (string.IsNullOrEmpty(entry.Folder) ||
                        string.Equals(Path.GetFileName(file.Path.Replace('/', Path.DirectorySeparatorChar)), anchorName,
                                      StringComparison.OrdinalIgnoreCase)))
                        path = anchor.Location;
                    if (path == null || !File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
                    if (!Equal(HashFile(path), file.Hash)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        public static byte[] HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return sha.ComputeHash(stream);
        }

        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static string ShortHash(byte[] hash)
        {
            if (hash == null || hash.Length < 4) return "";
            var text = new StringBuilder(8);
            for (int i = 0; i < 4; i++) text.Append(hash[i].ToString("x2"));
            return text.ToString();
        }

        public static bool IsDll(string path) => path != null && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        /// <summary>Files that are never listed: debug symbols and this mod's own marker files.</summary>
        private static bool Skipped(string relative)
        {
            string name = relative.Substring(relative.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            return relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(ICollection<string> guids, string guid)
        {
            if (guids == null || guid == null) return false;
            foreach (string item in guids)
                if (string.Equals(item, guid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
