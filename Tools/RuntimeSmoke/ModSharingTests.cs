using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SailwindCoop.Net;
using SailwindCoop.Sync;

internal static class ModSharingTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("mod file names cannot leave their folder", () => {
            foreach (string bad in new[] { "", "..", "../x.dll", "a/../../x.dll", "/x.dll", "C:/x.dll", "a\\b.dll", "a//b.dll",
                                           "x.dll.", "x.dll ", " x.dll", "CON", "nul.dll", "com1.txt", "a/LPT9", "a:b", "a\0b", "a|b" })
                Assert(!ModPathRules.IsSafeRelativePath(bad), "accepted '" + bad + "'");
            foreach (string good in new[] { "x.dll", "data/assets", "a/b/c.bundle", "My Mod (v2).dll", "console.dll", "com10.txt" })
                Assert(ModPathRules.IsSafeRelativePath(good), "refused '" + good + "'");
            Assert(!ModPathRules.IsSafeName("a/b") && ModPathRules.IsSafeName("SeaLifeMod"), "folder name rule");
            string root = Path.Combine(Path.GetTempPath(), "coop-mods-root");
            Assert(ModPathRules.Resolve(root, "a/b.dll") == Path.Combine(root, "a", "b.dll"), "resolve");
            ExpectFailure(() => ModPathRules.Resolve(root, "../b.dll"));
        });

        test("mod manifest entries are validated before use", () => {
            Func<ModEntry> valid = () => new ModEntry { Folder = "Mod", Complete = true,
                Plugins = new[] { new ModPlugin { Guid = "g" } },
                Files = new[] { new ModListFile { Path = "Mod.dll", Size = 3, Hash = new byte[32] } } };
            Assert(ModPathRules.Validate(valid()) == null, "valid entry refused");
            var e = valid(); e.Folder = "../Mod"; Assert(ModPathRules.Validate(e) != null, "folder traversal");
            e = valid(); e.Files[0].Path = "../../core/BepInEx.dll"; Assert(ModPathRules.Validate(e) != null, "file traversal");
            e = valid(); e.Files[0].Size = -1; Assert(ModPathRules.Validate(e) != null, "negative size");
            e = valid(); e.Files[0].Size = ModLimits.MaxFileBytes + 1; Assert(ModPathRules.Validate(e) != null, "oversized file");
            e = valid(); e.Files[0].Hash = new byte[4]; Assert(ModPathRules.Validate(e) != null, "short hash");
            e = valid(); e.Plugins = new ModPlugin[0]; Assert(ModPathRules.Validate(e) != null, "no plugins");
            e = valid(); e.Files = new[] { e.Files[0], new ModListFile { Path = "MOD.DLL", Size = 1, Hash = new byte[32] } };
            Assert(ModPathRules.Validate(e) != null, "duplicate path differing only by case");
            e = valid(); e.Folder = ""; e.Files[0].Path = "sub/Mod.dll"; Assert(ModPathRules.Validate(e) != null, "single-file mod in a subfolder");
        });

        test("host mod catalog lists folders and lone DLLs, skips excluded and symbol files", () => WithDirs((host, client, staging) => {
            var plugins = HostFixture(host);
            var log = new List<string>();
            List<ModEntry> mods = ModCatalog.Build(host, plugins, new[] { "personal.translator" }, log.Add);
            Assert(mods.Select(m => m.DisplayName).SequenceEqual(new[] { "Lone.dll", "SeaLife" }), "units: " + string.Join("|", mods.Select(m => m.DisplayName)));
            ModEntry sea = mods[1];
            Assert(sea.Complete && sea.Plugins.Length == 2 && sea.Plugins[0].Guid == "sea.a" && sea.Plugins[1].Guid == "sea.b", "plugins of one folder");
            Assert(sea.Files.Select(f => f.Path).SequenceEqual(new[] { "SeaLife.dll", "data/assets", "data/empty" }), "files: " + string.Join("|", sea.Files.Select(f => f.Path)));
            Assert(sea.Files[1].Size == 40000 && ModCatalog.Equal(sea.Files[1].Hash, ModCatalog.HashFile(Path.Combine(host, "SeaLife", "data", "assets"))), "size/hash");
            Assert(mods[0].Folder == "" && mods[0].Files.Length == 1 && mods.All(m => ModPathRules.Validate(m) == null), "lone DLL entry");
        }));

        test("mod comparison reports missing, different version and different build", () => WithDirs((host, client, staging) => {
            List<ModEntry> mods = ModCatalog.Build(host, HostFixture(host), new string[0], null);
            Assert(mods.Count == 3, "host units");

            var local = new List<LoadedPlugin>();
            Assert(ModCatalog.Compare(mods, client, local).All(d => d.Kind == ModDiffKind.Missing), "empty client must miss everything");

            // Same files under another folder name: the same mod.
            CopyDir(Path.Combine(host, "SeaLife"), Path.Combine(client, "Author-SeaLife"));
            local.Add(new LoadedPlugin { Guid = "sea.a", Version = "1.0", Location = Path.Combine(client, "Author-SeaLife", "SeaLife.dll") });
            local.Add(new LoadedPlugin { Guid = "sea.b", Version = "1.0", Location = Path.Combine(client, "Author-SeaLife", "SeaLife.dll") });
            // The lone DLL renamed and with another version.
            File.Copy(Path.Combine(host, "Lone.dll"), Path.Combine(client, "Renamed.dll"));
            local.Add(new LoadedPlugin { Guid = "lone", Version = "0.9", Location = Path.Combine(client, "Renamed.dll") });
            local.Add(new LoadedPlugin { Guid = "only.here", Name = "Only Here", Version = "1", Location = Path.Combine(client, "Only.dll") });

            var diffs = ModCatalog.Compare(mods, client, local);
            Assert(Kind(diffs, "SeaLife") == ModDiffKind.Same, "renamed folder must match");
            Assert(Kind(diffs, "Lone.dll") == ModDiffKind.Different && diffs.First(d => d.Entry.DisplayName == "Lone.dll").Detail.Contains("0.9"), "version difference");
            Assert(Kind(diffs, "Translator") == ModDiffKind.Missing, "missing mod");
            Assert(ModCatalog.LocalOnly(mods, local, new[] { "nothing" }).SequenceEqual(new[] { "Only Here" }), "local-only list");

            local.First(p => p.Guid == "lone").Version = "1.0";
            Assert(Kind(ModCatalog.Compare(mods, client, local), "Lone.dll") == ModDiffKind.Same, "renamed lone DLL with same bytes");
            File.WriteAllBytes(Path.Combine(client, "Author-SeaLife", "SeaLife.dll"), new byte[] { 9, 9, 9 });
            Assert(Kind(ModCatalog.Compare(mods, client, local), "SeaLife") == ModDiffKind.Different, "rebuilt DLL with the same version");
        }));

        test("mod download stages outside plugins, verifies and installs without overwriting", () => WithDirs((host, client, staging) => {
            List<ModEntry> mods = ModCatalog.Build(host, HostFixture(host), new[] { "personal.translator" }, null);
            Directory.CreateDirectory(Path.Combine(client, "SeaLife"));   // an unrelated folder already uses the name
            File.WriteAllText(Path.Combine(client, "SeaLife", "keep.txt"), "mine");

            using (var download = new ModDownload(staging, client, mods.Select((m, i) => new KeyValuePair<int, ModEntry>(i, m))))
            {
                Transfer(download, host, mods, null);
                Assert(!Directory.Exists(Path.Combine(client, "SeaLife (coop)")) && !File.Exists(Path.Combine(client, "Lone.dll")), "installed before commit");
                Assert(download.ReceivedBytes == download.TotalBytes && download.TotalBytes == mods.Sum(m => m.TotalBytes), "byte counters");
                List<string> installed = download.Commit("192.168.1.5");
                Assert(installed.SequenceEqual(new[] { "Lone.dll", "SeaLife (coop)" }), "installed: " + string.Join("|", installed));
            }
            Assert(File.ReadAllText(Path.Combine(client, "SeaLife", "keep.txt")) == "mine", "existing folder was touched");
            string target = Path.Combine(client, "SeaLife (coop)");
            foreach (ModListFile file in mods[1].Files)
                Assert(ModCatalog.Equal(ModCatalog.HashFile(Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar))), file.Hash), "content of " + file.Path);
            Assert(File.ReadAllText(Path.Combine(target, ModCatalog.MarkerFile)).Contains("192.168.1.5"), "install marker");
            Assert(ModCatalog.Equal(ModCatalog.HashFile(Path.Combine(client, "Lone.dll")), mods[0].Files[0].Hash), "lone DLL content");
            Assert(!Directory.Exists(staging), "staging left behind");
            // The marker must not travel if this machine later hosts the mod.
            var again = ModCatalog.Build(client, new[] { new LoadedPlugin { Guid = "sea.a", Location = Path.Combine(target, "SeaLife.dll") } }, new string[0], null);
            Assert(again[0].Files.All(f => !f.Path.EndsWith(ModCatalog.MarkerFile)), "marker shared onward");
        }));

        test("mod download rejects corrupt, reordered, foreign and refused data and installs nothing", () => WithDirs((host, client, staging) => {
            List<ModEntry> mods = ModCatalog.Build(host, HostFixture(host), new[] { "personal.translator" }, null);
            var wanted = mods.Select((m, i) => new KeyValuePair<int, ModEntry>(i, m)).ToList();

            foreach (string fault in new[] { "flip", "swap", "short", "refused", "wrong-file", "extra" })
            {
                using (var download = new ModDownload(staging, client, wanted))
                {
                    ExpectFailure(() => Transfer(download, host, mods, fault));
                    ExpectFailure(() => download.Commit("host"));
                }
                Assert(!Directory.Exists(staging), fault + ": staging left behind");
                Assert(Directory.GetFileSystemEntries(client).Length == 0, fault + ": something was installed");
            }

            // A manifest whose hash does not match the bytes the host actually has.
            mods[0].Files[0].Hash = new byte[32];
            using (var download = new ModDownload(staging, client, wanted))
                ExpectFailure(() => Transfer(download, host, mods, null));
            Assert(Directory.GetFileSystemEntries(client).Length == 0, "wrong manifest hash installed");
        }));

        test("mod download refuses unsafe or comparison-only entries and existing targets", () => WithDirs((host, client, staging) => {
            List<ModEntry> mods = ModCatalog.Build(host, HostFixture(host), new[] { "personal.translator" }, null);
            var escape = new ModEntry { Folder = "X", Complete = true, Plugins = new[] { new ModPlugin { Guid = "x" } },
                Files = new[] { new ModListFile { Path = "../../core/evil.dll", Size = 1, Hash = new byte[32] } } };
            ExpectFailure(() => new ModDownload(staging, client, new[] { new KeyValuePair<int, ModEntry>(0, escape) }));
            mods[1].Complete = false;
            ExpectFailure(() => new ModDownload(staging, client, new[] { new KeyValuePair<int, ModEntry>(1, mods[1]) }));

            File.WriteAllText(Path.Combine(client, "Lone.dll"), "already here");
            using (var download = new ModDownload(staging, client, new[] { new KeyValuePair<int, ModEntry>(0, mods[0]) }))
            {
                Transfer(download, host, mods, null);
                ExpectFailure(() => download.Commit("host"));
            }
            Assert(File.ReadAllText(Path.Combine(client, "Lone.dll")) == "already here", "existing DLL overwritten");
        }));

        test("mod upload refuses a file that changed after the manifest", () => WithDirs((host, client, staging) => {
            List<ModEntry> mods = ModCatalog.Build(host, HostFixture(host), new[] { "personal.translator" }, null);
            File.WriteAllBytes(Path.Combine(host, "Lone.dll"), new byte[5]);
            ExpectFailure(() => new ModUpload(Path.Combine(host, "Lone.dll"), mods[0].Files[0], 0, 0));
        }));

        test("oversized mod is listed for comparison only", () => WithDirs((host, client, staging) => {
            string big = Path.Combine(host, "Big");
            Directory.CreateDirectory(big);
            File.WriteAllBytes(Path.Combine(big, "Big.dll"), new byte[] { 1, 2 });
            for (int i = 0; i <= ModLimits.MaxFilesPerMod; i++) File.WriteAllBytes(Path.Combine(big, "f" + i + ".txt"), new byte[0]);
            var log = new List<string>();
            var mods = ModCatalog.Build(host, new[] { new LoadedPlugin { Guid = "big", Location = Path.Combine(big, "Big.dll") } }, new string[0], log.Add);
            Assert(mods.Count == 1 && !mods[0].Complete && mods[0].Files.Length == 1 && mods[0].Files[0].Path == "Big.dll" && log.Count == 1, "comparison-only entry");
            Assert(ModPathRules.Validate(mods[0]) == null, "comparison-only entry must still validate");
        }));
    }

    // Host fixture: a folder mod with two plugins and nested data, a lone DLL, and a personal mod.
    private static List<LoadedPlugin> HostFixture(string host)
    {
        string sea = Path.Combine(host, "SeaLife");
        Directory.CreateDirectory(Path.Combine(sea, "data"));
        File.WriteAllBytes(Path.Combine(sea, "SeaLife.dll"), Bytes(5000, 1));
        File.WriteAllBytes(Path.Combine(sea, "SeaLife.pdb"), Bytes(100, 2));
        File.WriteAllBytes(Path.Combine(sea, "data", "assets"), Bytes(40000, 3));   // more than two chunks
        File.WriteAllBytes(Path.Combine(sea, "data", "empty"), new byte[0]);
        File.WriteAllBytes(Path.Combine(host, "Lone.dll"), Bytes(ModLimits.ChunkSize, 4));   // exactly one chunk
        Directory.CreateDirectory(Path.Combine(host, "Translator"));
        File.WriteAllBytes(Path.Combine(host, "Translator", "T.dll"), Bytes(10, 5));
        return new List<LoadedPlugin>
        {
            new LoadedPlugin { Guid = "sea.b", Name = "Sea B", Version = "1.0", Location = Path.Combine(sea, "SeaLife.dll") },
            new LoadedPlugin { Guid = "sea.a", Name = "Sea A", Version = "1.0", Location = Path.Combine(sea, "SeaLife.dll") },
            new LoadedPlugin { Guid = "lone", Name = "Lone", Version = "1.0", Location = Path.Combine(host, "Lone.dll") },
            new LoadedPlugin { Guid = "personal.translator", Name = "Translator", Version = "5", Location = Path.Combine(host, "Translator", "T.dll") },
            new LoadedPlugin { Guid = "elsewhere", Name = "Elsewhere", Version = "1", Location = Path.Combine(Path.GetTempPath(), "not-in-plugins.dll") },
        };
    }

    /// <summary>Plays the host for a download, optionally injecting one fault.</summary>
    private static void Transfer(ModDownload download, string host, List<ModEntry> mods, string fault)
    {
        bool injected = fault == null;
        while (download.TryNext(out ushort mod, out ushort file))
        {
            ModEntry entry = mods[mod];
            string root = entry.Folder.Length > 0 ? Path.Combine(host, entry.Folder) : host;
            if (!injected && fault == "refused") { download.OnEnd(mod, file, false); return; }
            if (!injected && fault == "wrong-file") { download.OnChunk(mod, (ushort)(file + 1), 0, new byte[1]); return; }
            using (var upload = new ModUpload(ModPathRules.Resolve(root, entry.Files[file].Path), entry.Files[file], mod, file))
            {
                var chunks = new List<byte[]>();
                while (!upload.Done) chunks.Add(upload.NextChunk(out _));
                if (!injected && chunks.Count > 0)
                {
                    injected = true;
                    if (fault == "flip") chunks[0][0] ^= 0xFF;
                    else if (fault == "short") chunks.RemoveAt(chunks.Count - 1);
                    else if (fault == "extra") chunks.Add(new byte[1]);
                    else if (fault == "swap")
                    {
                        if (chunks.Count < 2) { injected = false; }
                        else { download.OnChunk(mod, file, 1, chunks[1]); return; }
                    }
                }
                for (int i = 0; i < chunks.Count; i++) download.OnChunk(mod, file, i, chunks[i]);
                download.OnEnd(mod, file, true);
            }
        }
        if (!injected) throw new Exception("fault '" + fault + "' was never injected");
    }

    private static ModDiffKind Kind(List<ModDiff> diffs, string name) => diffs.First(d => d.Entry.DisplayName == name).Kind;

    private static byte[] Bytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to));
    }

    private static void WithDirs(Action<string, string, string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "sailwind-mod-smoke-" + Guid.NewGuid().ToString("N"));
        string host = Path.Combine(root, "host-plugins"), client = Path.Combine(root, "client-plugins");
        Directory.CreateDirectory(host); Directory.CreateDirectory(client);
        try { body(host, client, Path.Combine(root, "cache", "modsync")); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void ExpectFailure(Action action)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Assert(failed, "expected failure");
    }

    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
