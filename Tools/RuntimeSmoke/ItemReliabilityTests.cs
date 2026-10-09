using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using SailwindCoop.Sync;

internal static class ItemReliabilityTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("authored same-prefab results bind by request even when reordered", () => {
            var authoring = new ItemAuthoring<object>(); var first = new object(); var second = new object();
            authoring.Add(41, first, 12); authoring.Add(42, second, 12);
            Assert(ReferenceEquals(authoring.Resolve(10, 10, 42, 12, 902), second), "second result stole first object");
            authoring.Bind(42, 902);
            Assert(ReferenceEquals(authoring.Resolve(10, 10, 41, 12, 901), first), "first result lost after reorder");
            authoring.Bind(41, 901);
            Assert(!authoring.Contains(first) && !authoring.Contains(second), "bound objects still awaiting identity");
        });
        test("unrelated spawn and foreign requester cannot consume authored identity", () => {
            var authoring = new ItemAuthoring<object>(); var item = new object(); authoring.Add(9, item, 12);
            Assert(authoring.Resolve(10, 10, 0, 12, 901) == null, "uncorrelated same-prefab spawn matched");
            Assert(authoring.Resolve(11, 10, 9, 12, 901) == null, "foreign author matched local request");
            Assert(authoring.Resolve(10, 10, 8, 12, 901) == null, "unknown request matched");
            Assert(authoring.Resolve(10, 10, 9, 13, 901) == null && authoring.Contains(item), "wrong prefab consumed pending object");
            Assert(ReferenceEquals(authoring.Resolve(10, 10, 9, 12, 901), item), "valid ack lost after unrelated packets");
        });
        test("authored duplicate acknowledgement retains exact host identity and resets per session", () => {
            var authoring = new ItemAuthoring<object>(); var item = new object(); authoring.Add(9, item, 12);
            authoring.Bind(9, 901);
            Assert(ReferenceEquals(authoring.Resolve(10, 10, 9, 12, 901), item), "duplicate valid ack lost");
            Assert(authoring.Resolve(10, 10, 9, 12, 902) == null, "one request rebound to another host identity");
            authoring.Clear(); Assert(authoring.Resolve(10, 10, 9, 12, 901) == null, "previous session identity leaked");
        });
        test("authored requests of destroyed items are dropped once bound, kept while waiting", () => {
            var authoring = new ItemAuthoring<object>(); var bound = new object(); var waiting = new object(); var alive = new object();
            authoring.Add(1, bound, 12); authoring.Add(2, waiting, 12); authoring.Add(3, alive, 12);
            authoring.Bind(1, 901); authoring.Bind(3, 903);
            authoring.Prune(item => ReferenceEquals(item, alive));
            Assert(authoring.Count == 2, "bound request of a destroyed item kept, or a live one dropped");
            Assert(authoring.Resolve(10, 10, 1, 12, 901) == null, "destroyed item still resolves");
            Assert(ReferenceEquals(authoring.Resolve(10, 10, 2, 12, 902), waiting) && authoring.Contains(waiting), "request awaiting its id was dropped");
            Assert(ReferenceEquals(authoring.Resolve(10, 10, 3, 12, 903), alive), "live bound item lost");
        });
        test("a host item without a local counterpart is reported once and then retried slowly", () => {
            var wait = new SpawnWait(); bool report; int reports = 0, tries = 0;
            for (float now = 0f; now < SpawnWait.ReportAfter; now += 0.25f)
            { Assert(wait.Due(7, now, out report) && !report, "early wait throttled or reported"); }
            for (float now = SpawnWait.ReportAfter; now < SpawnWait.ReportAfter + 5f; now += 0.25f)
            { if (wait.Due(7, now, out report)) tries++; if (report) reports++; }
            Assert(reports == 1, "long wait reported " + reports + " times");
            Assert(tries >= 4 && tries <= 6, "slow retry ran " + tries + " times in 5 s");
            wait.Remove(7); Assert(wait.Count == 0 && wait.Due(7, 100f, out report) && !report, "a resolved item kept its wait");
            wait.Clear(); Assert(wait.Count == 0, "session reset");
        });
        test("empty item baseline settles only in a loaded playing world", () => {
            Assert(ItemBaseline.Ready(true, false, 0, 4, 4), "valid empty baseline never ready");
            Assert(!ItemBaseline.Ready(false, false, 0, 100, 4), "main menu became a baseline");
            Assert(!ItemBaseline.Ready(true, true, 0, 100, 4), "loading became a baseline");
            Assert(!ItemBaseline.Ready(true, false, -1, 100, 4), "unscanned baseline ready");
            Assert(!ItemBaseline.Ready(true, false, 0, 3.9f, 4), "baseline settled too early");
        });
        test("position matching is restricted to actual unclaimed baseline items", () => {
            Assert(ItemBaseline.CanMatch(true, true, false, false), "actual baseline match rejected");
            Assert(!ItemBaseline.CanMatch(false, true, false, false), "runtime spawn used baseline matching");
            Assert(!ItemBaseline.CanMatch(true, false, false, false), "runtime object used as baseline candidate");
            Assert(!ItemBaseline.CanMatch(true, true, true, false), "already shared identity stolen");
            Assert(!ItemBaseline.CanMatch(true, true, false, true), "pending authored same-prefab object stolen");
        });
        test("compound missing target stays pending and retries only unfinished element", () => {
            var progress = new ItemResultProgress(); int lifecycle = 0, changed = 0; bool available = false;
            Assert(progress.Begin() && !progress.Begin(), "reentrant apply allowed");
            bool complete = progress.TryApply(ItemResultSection.Identity, 0, () => { lifecycle++; return true; }, Fail);
            complete &= progress.TryApply(ItemResultSection.Changed, 0, () => { if (!available) return false; changed++; return true; }, Fail);
            progress.End(complete);
            Assert(!progress.Completed && lifecycle == 1 && changed == 0, "missing target falsely completed");
            available = true; Assert(progress.Begin(), "pending result could not retry");
            complete = progress.TryApply(ItemResultSection.Identity, 0, () => { lifecycle++; return true; }, Fail);
            complete &= progress.TryApply(ItemResultSection.Changed, 0, () => { changed++; return true; }, Fail);
            progress.End(complete);
            Assert(progress.Completed && lifecycle == 1 && changed == 1, "retry duplicated lifecycle or lost missing state");
        });
        test("compound partial fault retains actual progress in every section", () => {
            foreach (ItemResultSection section in Enum.GetValues(typeof(ItemResultSection)))
            {
                var progress = new ItemResultProgress(); int applied = 0, faults = 0, attempts = 0;
                Assert(progress.Begin(), "first apply rejected");
                bool complete = progress.TryApply(section, 0, () => { applied++; return true; }, Fail);
                complete &= progress.TryApply(section, 1, () => { attempts++; throw new InvalidOperationException("injected between elements"); }, error => faults++);
                progress.End(complete);
                Assert(!progress.Completed && applied == 1 && faults == 1, "partial failure discarded progress");
                Assert(progress.Begin(), "partial failure became permanent");
                complete = progress.TryApply(section, 0, () => { applied++; return true; }, Fail);
                complete &= progress.TryApply(section, 1, () => { attempts++; applied++; return true; }, Fail);
                progress.End(complete);
                Assert(progress.Completed && applied == 2 && attempts == 2 && faults == 1, "successful element reapplied");
            }
        });
        test("compound completed result never reapplies lifecycle or benefits", () => {
            var progress = new ItemResultProgress(); int effects = 0;
            Assert(progress.Begin(), "initial result rejected");
            bool complete = true;
            foreach (ItemResultSection section in Enum.GetValues(typeof(ItemResultSection)))
                complete &= progress.TryApply(section, 0, () => { effects++; return true; }, Fail);
            progress.End(complete);
            Assert(progress.Completed && !progress.Begin() && effects == Enum.GetValues(typeof(ItemResultSection)).Length, "completed result accepted again");
            var empty = new ItemResultProgress(); Assert(empty.Begin(), "empty result rejected"); empty.End(true);
            Assert(empty.Completed && !empty.Begin(), "empty result never completed");
        });
        test("host retained partial result is replayed without rerunning effects before client completion", () => {
            var ledger = new OperationLedger<object[]>(); var result = new object[] { new object(), new object() };
            int effects = 0;
            Action applyHost = () => { if (ledger.TryBegin(10, 41, result, out _)) effects++; };
            applyHost(); applyHost();
            Assert(ledger.TryGet(10, 41, out var previous) && ReferenceEquals(previous, result), "host repeated partial effects");
            Assert(ledger.TryGet(10, 41, out previous) && previous.Length == 2 && effects == 1, "partial subset lost");
            var progress = new ItemResultProgress(); int received = 0;
            Assert(progress.Begin(), "client refused retained result");
            bool complete = progress.TryApply(ItemResultSection.Consumed, 0, () => { received++; return true; }, Fail);
            complete &= progress.TryApply(ItemResultSection.CreatedState, 0, () => false, Fail); progress.End(complete);
            Assert(!progress.Completed && progress.Begin(), "missing client target completed");
            complete = progress.TryApply(ItemResultSection.Consumed, 0, () => { received++; return true; }, Fail);
            complete &= progress.TryApply(ItemResultSection.CreatedState, 0, () => true, Fail); progress.End(complete);
            Assert(progress.Completed && received == 1 && effects == 1, "replayed retained result duplicated effects");
        });
        test("item apply fault does not commit ordering and retry cannot restore stale metadata", () => {
            var gate = new ItemStateGate(); bool semantic, pose;
            Assert(gate.Receive(9, 100, 0, 0, 10, out semantic, out pose), "baseline"); gate.Begin(41);
            var attempt = gate.Copy(); Assert(attempt.Receive(10, 100, 10, 41, 10, out semantic, out pose), "result rejected");
            Assert(gate.Pending && gate.Version == 9, "uncommitted failed apply advanced metadata");
            attempt = gate.Copy(); Assert(attempt.Receive(10, 100, 10, 41, 10, out semantic, out pose), "retry rejected after fault"); gate.Commit(attempt);
            Assert(!gate.Pending && gate.HasState && gate.Version == 10, "successful retry not committed");
            Assert(!gate.Copy().Receive(9, 999, 10, 40, 10, out semantic, out pose), "stale result restored metadata");
        });
        test("snapshot save identity includes post-settle fish but not unrelated same-prefab objects", () => {
            var saved = new ItemSaveIdentity(); saved.Add(901, 12);
            Assert(saved.Contains(901, 12), "save-loaded runtime item lost exact identity");
            Assert(!saved.Contains(902, 12) && !saved.Contains(901, 13), "same-prefab or wrong-prefab item matched");
            saved.Clear(); Assert(!saved.Contains(901, 12), "snapshot identity survived session reset");
        });
        test("membership pending and partial insert fault cannot commit a result gate", () => {
            var membership = new ItemMembership<object>(); var item = new object();
            var gate = new ItemStateGate(); bool semantic, pose;
            gate.Receive(9, 100, 0, 0, 10, out semantic, out pose); gate.Begin(41);
            var accepted = gate.Copy(); accepted.Receive(10, 100, 10, 41, 10, out semantic, out pose);
            bool ready = false, listed = false, finished = false; int attempts = 0, faults = 0;
            Func<ItemApplyStatus> apply = () => membership.Apply(item, () => listed, () => ready, () => {
                attempts++; listed = true;
                if (!finished) throw new InvalidOperationException("fault after id/list insertion before visual state");
            }, error => faults++);
            Assert(gate.Apply(accepted, apply) == ItemApplyStatus.Pending && attempts == 0 && gate.Pending, "missing carrier committed");
            ready = true;
            Assert(gate.Apply(accepted, apply) == ItemApplyStatus.Fault && listed && gate.Pending && gate.Version == 9, "partial insert committed");
            finished = true;
            Assert(gate.Apply(accepted, apply) == ItemApplyStatus.Applied && !gate.Pending && gate.Version == 10 && attempts == 2 && faults == 1,
                "partial insert did not finish before commit");
            Assert(gate.Apply(accepted, apply) == ItemApplyStatus.Applied && attempts == 2, "completed insert repeated");
        });
        test("membership id without actual required list membership stays pending", () => {
            var membership = new ItemMembership<object>(); var item = new object(); int attempts = 0;
            bool listed = false;
            Assert(membership.Apply(item, () => listed, () => true, () => attempts++, Fail) == ItemApplyStatus.Pending,
                "silent no-op insert completed");
            Assert(membership.Apply(item, () => listed, () => true, () => { attempts++; listed = true; }, Fail) == ItemApplyStatus.Applied && attempts == 2,
                "no-op insert did not retry");
        });
        test("host cook and fuel readiness waits for addressed components targets and slots", () => {
            Assert(!ItemTargetReadiness.Binding(true, true, true, false, false), "missing stove ready");
            Assert(!ItemTargetReadiness.Binding(true, true, true, true, false), "missing cook slot or fuel trigger ready");
            Assert(!ItemTargetReadiness.Binding(true, false, false, false, false), "missing component ready");
            Assert(ItemTargetReadiness.Binding(true, true, false, false, false), "detached component must not wait for stove");
            Assert(ItemTargetReadiness.Binding(true, true, true, true, true), "loaded slot not ready");
        });
        test("disconnect drops only unstarted actor waits and retains started operation ledger", () => {
            var waiting = new Dictionary<ulong, object> { { ((ulong)10 << 32) | 41, new object() }, { ((ulong)11 << 32) | 41, new object() } };
            var ledger = new OperationLedger<object>(); var retained = new object();
            Assert(ledger.TryBegin(10, 42, retained, out _), "started result missing");
            ItemOperationWaiting.RemoveActor(waiting, 10);
            Assert(waiting.Count == 1 && waiting.ContainsKey(((ulong)11 << 32) | 41), "disconnect kept actor wait or dropped other guest");
            Assert(ledger.TryGet(10, 42, out var previous) && ReferenceEquals(previous, retained), "disconnect discarded started effects");
        });
        test("production T11 snapshot binding membership commit preflight and disconnect paths are wired", VerifyProductionPaths);
        test("production consumed cleanup retries past tombstone instead of falsely completing", VerifyConsumedCleanup);
    }
    private static string PluginPath()
    {
        var metadata = (AssemblyMetadataAttribute)Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
            .First(value => ((AssemblyMetadataAttribute)value).Key == "GameDir");
        return Path.Combine(metadata.Value, "BepInEx", "plugins", "SailwindCoop", "SailwindCoop.dll");
    }
    private static MethodDefinition Method(TypeDefinition type, string name) => type.Methods.Single(method => method.Name == name);
    private static bool Calls(MethodDefinition method, string name) => method.Body.Instructions.Any(instruction =>
        instruction.Operand is MethodReference called && called.Name == name);
    private static IEnumerable<MethodDefinition> Methods(TypeDefinition type)
        => type.Methods.Concat(type.NestedTypes.SelectMany(Methods));
    private static void VerifyProductionPaths()
    {
        using (var plugin = AssemblyDefinition.ReadAssembly(PluginPath()))
        {
            var items = plugin.MainModule.Types.Single(type => type.FullName == "SailwindCoop.Sync.ItemSync");
            var components = plugin.MainModule.Types.Single(type => type.FullName == "SailwindCoop.Sync.ItemComponents");
            var gate = plugin.MainModule.Types.Single(type => type.FullName == "SailwindCoop.Sync.ItemStateGate");
            var save = plugin.MainModule.Types.Single(type => type.FullName == "SailwindCoop.Sync.SaveTransferSync");
            var resolve = Method(items, "ResolveClient");
            Assert(resolve.Body.Instructions.Any(instruction => instruction.Operand is MethodReference called &&
                called.DeclaringType.Name == "ItemSaveIdentity" && called.Name == "Contains") &&
                Calls(resolve, "SaveLoaded") && !Calls(resolve, "FindUnclaimedMatch"), "snapshot correlation not used by resolver");
            Assert(Method(components, "SaveLoaded").Body.Instructions.Any(instruction => Equals(instruction.Operand, "loaded")), "resolver accepts objects without proof of save load");
            var transfer = Method(save, "ApplyHostSave").Body.Instructions.Select(instruction => instruction.Operand as MethodReference).Where(value => value != null).ToList();
            Assert(transfer.FindIndex(value => value.Name == "SetSaveBaseline") >= 0 &&
                transfer.FindIndex(value => value.Name == "SetSaveBaseline") < transfer.FindIndex(value => value.Name == "MergeInto"), "snapshot captured after personal belt injection");
            Assert(Calls(Method(items, "TryApplyItemState"), "Apply") && Calls(Method(gate, "Apply"), "Commit"), "production state bypasses success commit helper");
            var callbacks = Methods(items).Where(method => method.HasBody && Calls(method, "TryApplyCrateMembership") && Calls(method, "TryApplyCargoMembership")).ToArray();
            Assert(callbacks.Any(method => method.Name.Contains("TryApplyItemState")), "state does not propagate both membership results");
            foreach (string name in new[] { "TryApplyCrateMembership", "TryApplyCargoMembership" })
                Assert(Calls(Method(items, name), "Apply"), name + " bypasses fault-aware membership helper");
            Assert(Methods(items).Any(method => method.HasBody && Calls(method, "CrateListsMatch")) &&
                Methods(items).Any(method => method.HasBody && Calls(method, "CargoListsMatch")), "membership completion ignores stale list membership");
            var host = Method(items, "ApplyOperation").Body.Instructions;
            int begin = host.ToList().FindIndex(instruction => instruction.Operand is MethodReference called && called.Name == "TryBegin");
            foreach (string name in new[] { "BindingsReady", "MembershipTargetsReady", "CreationMembershipTargetsReady" })
                Assert(host.ToList().FindIndex(instruction => instruction.Operand is MethodReference called && called.Name == name) is int at && at >= 0 && at < begin,
                    name + " checked after effects ledger started");
            Assert(Calls(Method(items, "ClearRemoteActor"), "ForgetWaitingOperations") && Calls(Method(items, "ForgetWaitingOperations"), "RemoveActor") &&
                !Calls(Method(items, "ClearRemoteActor"), "ClearOperations"), "disconnect wait cleanup missing or erases ledger");
        }
    }
    private static void VerifyConsumedCleanup()
    {
        using (var plugin = AssemblyDefinition.ReadAssembly(PluginPath()))
        {
            var items = plugin.MainModule.Types.Single(type => type.FullName == "SailwindCoop.Sync.ItemSync");
            var despawn = Method(items, "OnDespawnObject");
            Assert(!despawn.Body.Instructions.Any(instruction => instruction.Operand is MethodReference called && called.Name == "Contains"),
                "tombstone became an early-return guard for cleanup");
            // The cleanup body is shared with the manifest-end prune, so it lives in DestroyClientEntry.
            Assert(Calls(despawn, "DestroyClientEntry"), "despawn no longer runs the cleanup");
            var destroy = Method(items, "DestroyClientEntry");
            int cleanup = destroy.Body.Instructions.ToList().FindIndex(instruction => instruction.Operand is MethodReference called && called.Name == "RemoveBindings");
            Assert(cleanup >= 0 && destroy.Body.Instructions.Skip(cleanup + 1).Any(instruction => instruction.Operand is MethodReference called && called.Name == "Destroy"),
                "cleanup is missing or no longer precedes destroy");
            Assert(Methods(items).Any(method => method.HasBody && method.Name.Contains("ApplyOperationResult") && Calls(method, "OnDespawnObject")),
                "compound consumed path does not invoke cleanup");
        }
        var progress = new ItemResultProgress(); var tombstones = new HashSet<int>(); int attempts = 0, faults = 0;
        Func<bool> consumed = () => { tombstones.Add(901); attempts++; if (attempts == 1) throw new InvalidOperationException("RemoveBindings fault"); return true; };
        Assert(progress.Begin(), "first cleanup refused");
        bool complete = progress.TryApply(ItemResultSection.Consumed, 0, consumed, error => faults++); progress.End(complete);
        Assert(!progress.Completed && faults == 1 && tombstones.Contains(901) && progress.Begin(), "faulting cleanup falsely completed");
        complete = progress.TryApply(ItemResultSection.Consumed, 0, consumed, Fail); progress.End(complete);
        Assert(progress.Completed && attempts == 2, "tombstoned consumed cleanup not retried");
    }
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Fail(Exception error) { throw new Exception("unexpected apply fault", error); }
}
