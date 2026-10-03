using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using SailwindCoop.Sync;
using SailwindCoop.Runtime;
using BepInEx.Logging;

internal static class Program
{
    private static int _passed, _failed;
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Write(string path, string data, bool preserve = false)
        => AtomicSaveFile.Write(path, s => { byte[] b = Encoding.UTF8.GetBytes(data); s.Write(b, 0, b.Length); }, preserve);
    private static string Read(Stream s)
    {
        using (var reader = new StreamReader(s, Encoding.UTF8))
        {
            string value = reader.ReadToEnd();
            if (value.StartsWith("corrupt")) throw new InvalidDataException("corrupt");
            return value;
        }
    }
    private static void ExpectFailure(Action action)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Assert(failed, "expected failure");
    }
    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
    }
    private static int Main(string[] args)
    {
        if (args.Length == 2)
        {
            if (args[0] == "before") AtomicSaveFile.Write(args[1], s => {
                s.WriteByte(99); s.Flush(); Environment.Exit(23);
            });
            else { Write(args[1], "after"); Environment.Exit(23); }
            return 23;
        }
        string dir = Path.Combine(Path.GetTempPath(), "sailwind-runtime-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "profile.dat");
        try
        {
            Test("new target", () => { Write(path, "old"); Assert(File.ReadAllText(path) == "old", "contents"); });
            Test("replace and backup", () => { Write(path, "new"); Assert(File.ReadAllText(path + ".bak") == "old", "backup"); });
            Test("serialization failure preserves both files", () => {
                ExpectFailure(() => AtomicSaveFile.Write(path, s => { s.WriteByte(1); throw new IOException("injected"); }));
                Assert(File.ReadAllText(path) == "new" && File.ReadAllText(path + ".bak") == "old", "old files changed");
                Assert(Directory.GetFiles(dir, "*.tmp").Length == 0, "temp leak");
            });
            Test("locked target", () => {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    ExpectFailure(() => Write(path, "broken"));
                Assert(File.ReadAllText(path) == "new" && File.ReadAllText(path + ".bak") == "old", "old files changed");
            });
            Test("backup destination failure", () => {
                string blocked = Path.Combine(dir, "blocked.dat");
                File.WriteAllText(blocked, "good"); Directory.CreateDirectory(blocked + ".bak");
                ExpectFailure(() => Write(blocked, "bad"));
                Assert(File.ReadAllText(blocked) == "good", "target changed on failed replace");
            });
            Test("corrupt primary and preserved recovery", () => {
                File.WriteAllText(path, "corrupt primary");
                bool recovered; Assert(AtomicSaveFile.Read(path, Read, out recovered) == "old" && recovered, "recovery");
                Write(path, "recovered", true); Assert(File.ReadAllText(path + ".bak") == "old", "backup overwritten");
            });
            Test("both corrupt", () => {
                File.WriteAllText(path, "corrupt primary"); File.WriteAllText(path + ".bak", "corrupt backup");
                ExpectFailure(() => { bool recovered; AtomicSaveFile.Read(path, Read, out recovered); });
                Assert(File.ReadAllText(path) == "corrupt primary" && File.ReadAllText(path + ".bak") == "corrupt backup", "evidence changed");
            });
            Test("missing primary backup recovery", () => {
                File.Delete(path); File.WriteAllText(path + ".bak", "backup");
                bool recovered; Assert(AtomicSaveFile.Read(path, Read, out recovered) == "backup" && recovered, "recovery");
            });
            Test("process exit before commit", () => {
                File.WriteAllText(path, "stable"); Child("before", path);
                Assert(File.ReadAllText(path) == "stable", "old target lost");
                Assert(Directory.GetFiles(dir, "*.tmp").Length == 1, "interrupted temp not left");
                Write(path, "next"); Assert(File.ReadAllText(path + ".bak") == "stable", "orphan temp interfered");
            });
            Test("process exit after commit", () => {
                Child("after", path); Assert(File.ReadAllText(path) == "after" && File.ReadAllText(path + ".bak") == "next", "commit incomplete");
            });
            Test("patch exceptions and throwing diagnostics", () => {
                int reports = 0;
                PatchGuard.Run(() => { throw new Exception("sync fault"); }, e => { reports++; throw new Exception("logger fault"); });
                Assert(PatchGuard.Prefix(() => { throw new Exception("sync fault"); }, e => { reports++; throw new Exception("logger fault"); }), "prefix must continue vanilla");
                Assert(reports == 2 && !PatchGuard.Prefix(() => false, e => {}), "normal prefix result changed");
            });
            Test("hatch target waits for the previous animation", () => {
                bool open = true, inMotion = true; int accepted = 0;
                var target = new DeferredToggle { Target = false };
                Action activate = () => { if (!inMotion) { open = !open; inMotion = true; accepted++; } };
                Assert(!target.TryApply(() => open, activate) && open, "busy hatch lost its pending close");
                inMotion = false;
                Assert(target.TryApply(() => open, activate) && !open && accepted == 1, "close did not converge");
                Assert(target.TryApply(() => open, activate) && accepted == 1, "duplicate state toggled hatch");
            });
            Test("latest hatch target replaces a pending toggle", () => {
                bool open = false, inMotion = true; int accepted = 0;
                var target = new DeferredToggle { Target = true };
                Action activate = () => { if (!inMotion) { open = !open; accepted++; } };
                Assert(!target.TryApply(() => open, activate), "busy animation accepted a toggle");
                target.Target = false;
                Assert(target.TryApply(() => open, activate) && accepted == 0, "stale target won");
                target.Target = true; inMotion = false;
                Assert(target.TryApply(() => open, activate) && open && accepted == 1, "late join state did not converge");
            });
            Test("mooring rejects pre-request snapshots and older replies", () => {
                var state = new PendingMooringState<string>(); string applied = "local";
                state.BeginRequest(41);
                Assert(!state.Receive(0, 0, 7, "old Unmoor"), "snapshot acknowledged a request");
                state.BeginRequest(42);
                Assert(!state.Receive(7, 41, 7, "old Moor"), "older reply undid a newer request");
                state.TryApply(false, value => { applied = value; return true; });
                Assert(applied == "local", "pending action was overwritten");
                Assert(state.Receive(7, 42, 7, "latest"), "latest reply was ignored");
                state.TryApply(false, value => { applied = value; return true; });
                Assert(applied == "latest", "latest reply did not converge");
            });
            Test("mooring acknowledgements belong to the authenticated requester", () => {
                var state = new PendingMooringState<string>(); string applied = null;
                state.BeginRequest(1);
                Assert(!state.Receive(8, 1, 7, "other guest"), "other guest released the pending action");
                Assert(state.Receive(7, 1, 7, "own reply"), "own reply ignored");
                state.TryApply(false, value => { applied = value; return true; });
                Assert(applied == "own reply", "wrong guest state applied");
            });
            Test("mooring held rope waits and then uses the latest host state", () => {
                var state = new PendingMooringState<string>(); string applied = null; int calls = 0;
                state.BeginRequest(9); state.Receive(7, 9, 7, "ack");
                state.TryApply(true, value => { calls++; applied = value; return true; });
                state.Receive(0, 0, 7, "new snapshot");
                state.TryApply(true, value => { calls++; return true; });
                Assert(calls == 0, "held rope was moved");
                state.TryApply(false, value => { calls++; applied = value; return true; });
                state.TryApply(false, value => { calls++; return true; });
                Assert(applied == "new snapshot" && calls == 1, "release lost state or repeated it");
            });
            Test("mooring local action discards a deferred state", () => {
                var state = new PendingMooringState<string>(); int calls = 0;
                state.Receive(0, 0, 7, "old snapshot"); state.BeginRequest(10);
                state.TryApply(false, value => { calls++; return true; });
                Assert(calls == 0, "pre-action held snapshot replayed after release");
                state.Receive(7, 10, 7, "ack");
                state.TryApply(false, value => { calls++; return true; });
                Assert(calls == 1, "ack did not release state");
            });
            Test("mooring missing dock acknowledgement does not strand a request", () => {
                var state = new PendingMooringState<string>(); string applied = null;
                state.BeginRequest(11);
                Assert(state.Receive(7, 11, 7, null), "unavailable-state ack ignored");
                state.TryApply(false, value => { throw new Exception("invented dock state"); });
                Assert(state.Receive(0, 0, 7, "dock loaded"), "pending request stranded");
                state.TryApply(false, value => { applied = value; return true; });
                Assert(applied == "dock loaded", "later state lost");
            });
            Test("mooring unresolved target retries independently of another rope", () => {
                var state = new PendingMooringState<string>();
                var other = new PendingMooringState<string>(); int retries = 0; string applied = null;
                state.Receive(0, 0, 7, "Moor"); other.BeginRequest(12);
                state.TryApply(false, value => { retries++; return false; });
                state.TryApply(false, value => { retries++; applied = value; return true; });
                state.TryApply(false, value => { retries++; return true; });
                Assert(retries == 2 && applied == "Moor", "dock target lost, repeated or blocked by another rope");
                Assert(!other.Receive(0, 0, 7, "snapshot"), "another rope lost its pending request");
            });
            Test("repeating patch faults use log budget", () => {
                var sink = new ManualLogSource("RuntimeSmoke"); int lines = 0;
                sink.LogEvent += (sender, e) => lines++;
                var log = new CoopLog(sink, false); var repeat = new CoopLog.Repeat();
                for (int i = 0; i < 1000; i++) PatchGuard.Run(() => { throw new Exception("injected"); }, e => log.ReportError("patch", e.Message, ref repeat));
                Assert(repeat.Occurrences == 1000 && lines <= 12, "silent log flood: " + lines);
                lines = 0; log.Enabled = true;
                for (int i = 0; i < 1000; i++) log.ReportError("patch", "injected", ref repeat);
                Assert(lines == 6, "enabled throttle: " + lines);
            });
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Runtime smoke: " + _passed + " passed, " + _failed + " failed (.NET Framework; Unity Mono still needs in-game verification)");
        return _failed == 0 ? 0 : 1;
    }
    private static void Child(string stage, string path)
    {
        var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
            stage + " \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true };
        using (var process = Process.Start(info))
        {
            if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("child timeout"); }
            Assert(process.ExitCode == 23, "child did not interrupt expected stage");
        }
    }
}
