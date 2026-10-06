using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using SailwindCoop.Sync;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using BepInEx.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;

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
            ItemReliabilityTests.Run(Test);
            ModSharingTests.Run(Test);
            TunnelTests.Run(Test);
            Test("boat configuration barrier holds future packets and discards old indices", () => {
                var book = new BoatGenerationBook(); var queue = new BoatGenerationQueue<string>();
                queue.Add(1, 2, "boat1 new rope"); queue.Add(2, 1, "boat2 unchanged"); queue.Add(1, 1, "boat1 old rope");
                var ready = queue.TakeReady(book, id => id == 1);
                Assert(ready.SequenceEqual(new[] { "boat2 unchanged" }), "preview blocked another hull");
                Assert(book.Next(1) == 2 && book.Get(2) == 1, "generation changed another hull");
                ready = queue.TakeReady(book, id => false);
                Assert(ready.SequenceEqual(new[] { "boat1 new rope" }), "old index escaped or future packet lost");
                Assert(queue.TakeReady(book, id => false).Count == 0, "packet applied twice");
            });
            Test("boat preview cancellation resumes same-generation requests in order", () => {
                var book = new BoatGenerationBook(); var queue = new BoatGenerationQueue<int>();
                queue.Add(3, 1, 10); queue.Add(3, 1, 11);
                Assert(queue.TakeReady(book, id => true).Count == 0, "request applied to preview");
                Assert(queue.TakeReady(book, id => false).SequenceEqual(new[] { 10, 11 }), "cancel lost causal order");
                queue.Add(3, 2, 12); queue.Clear(); book.Clear();
                Assert(queue.TakeReady(book, id => false).Count == 0 && book.Get(3) == 1, "session retained layout work");
            });
            Test("suspended hull keeps one unreliable snapshot per kind and every reliable packet", () => {
                var book = new BoatGenerationBook(); var queue = new BoatGenerationQueue<string>();
                for (int i = 0; i < 500; i++) queue.Add(6, 1, "control" + i, coalesce: 33);
                queue.Add(6, 1, "event A");
                for (int i = 0; i < 500; i++) queue.Add(6, 1, "anchor" + i, coalesce: 34);
                queue.Add(6, 1, "event B");
                queue.Add(6, 1, "control last", coalesce: 33);
                queue.Add(7, 1, "other hull control", coalesce: 33);
                queue.Add(6, 2, "next configuration control", coalesce: 33);
                Assert(queue.Count == 6, "periodic snapshots accumulated: " + queue.Count);
                Assert(queue.TakeReady(book, id => id == 6).SequenceEqual(new[] { "other hull control" }), "coalescing crossed hulls");
                Assert(queue.TakeReady(book, id => false).SequenceEqual(new[] { "event A", "anchor499", "event B", "control last" }),
                    "reliable packet dropped or snapshot left ahead of a later event");
                Assert(queue.Count == 1, "future configuration snapshot was merged into the current one");
            });
            Test("boat generation wrap orders configurations independently per hull", () => {
                var book = new BoatGenerationBook(); book.Set(4, uint.MaxValue);
                Assert(book.Compare(4, 1) == GenerationOrder.Future, "wrap future");
                Assert(book.Next(4) == 1 && book.Compare(4, uint.MaxValue) == GenerationOrder.Past, "wrap old configuration");
                Assert(book.Compare(5, 1) == GenerationOrder.Current, "unrelated hull inherited revision");
            });
            Test("compound operation deduplicates effects and retains partial result", () => {
                var ledger = new OperationLedger<List<string>>(); List<string> previous;
                var result = new List<string>(); int effects = 0;
                Assert(ledger.TryBegin(10, 7, result, out previous), "first operation rejected");
                ExpectFailure(() => { effects++; result.Add("resource applied"); throw new InvalidOperationException("Unity fault before creation"); });
                Assert(!ledger.TryBegin(10, 7, new List<string>(), out previous), "partial operation could run again");
                Assert(effects == 1 && ReferenceEquals(previous, result) && previous.Count == 1, "actual partial result lost");
                Assert(ledger.TryBegin(11, 7, new List<string>(), out previous), "different client operation collided");
                ledger.Clear();
                Assert(ledger.TryBegin(10, 7, new List<string>(), out previous), "new session reused stale result");
            });
            Test("item revision orders all metadata independently of pose tick", () => {
                var gate = new ItemStateGate(); bool semantic, pose;
                Assert(gate.Receive(9, 500, 0, 0, 10, out semantic, out pose) && semantic && pose, "initial full state rejected");
                Assert(gate.Receive(10, 500, 0, 0, 10, out semantic, out pose) && semantic && pose, "same-tick result rejected");
                Assert(!gate.Receive(9, 900, 0, 0, 10, out semantic, out pose), "old metadata won with newer pose tick");
                Assert(gate.Receive(10, 501, 0, 0, 10, out semantic, out pose) && !semantic && pose, "same-revision pose stopped");
                Assert(!gate.Receive(10, 499, 0, 0, 10, out semantic, out pose), "older pose admitted");
                var wrap = new ItemStateGate();
                Assert(wrap.Receive(uint.MaxValue, 1, 0, 0, 10, out semantic, out pose), "wrap baseline");
                Assert(wrap.Receive(1, 1, 0, 0, 10, out semantic, out pose), "revision wrap rejected");
            });
            Test("unanswered request stops blocking host state after the timeout", () => {
                var clock = ItemStateGate.Clock; long now = 1000; int expired = 0;
                ItemStateGate.Clock = () => now; ItemStateGate.Expired = request => { if (request == 21) expired++; };
                try
                {
                    var gate = new ItemStateGate(); bool semantic, pose;
                    Assert(gate.Receive(1, 100, 0, 0, 10, out semantic, out pose), "baseline rejected");
                    gate.Begin(21);
                    now += ItemStateGate.PendingTimeoutMs - 1;
                    Assert(!gate.Receive(2, 200, 0, 0, 10, out semantic, out pose) && gate.Pending, "pending released before the timeout");
                    now += 1;
                    Assert(gate.Receive(2, 300, 0, 0, 10, out semantic, out pose) && semantic && !gate.Pending, "host state still blocked after the timeout");
                    Assert(expired == 1, "abandoned request not reported exactly once");
                    gate.Begin(22); now += 10;
                    Assert(gate.Receive(3, 400, 10, 22, 10, out semantic, out pose) && !gate.Pending, "acknowledgement inside the window rejected");
                    Assert(expired == 1, "acknowledged request reported as abandoned");
                }
                finally { ItemStateGate.Clock = clock; ItemStateGate.Expired = null; }
            });
            Test("state stream is silent while nothing changes and ends every run reliably", () => {
                var stream = new ChangeStream();
                Assert(stream.Next(false) == StreamSend.Reliable, "first value not delivered reliably");
                for (int i = 0; i < 100; i++) Assert(stream.Next(false) == StreamSend.None, "unchanged value sent");
                Assert(stream.Next(true) == StreamSend.Unreliable && stream.Next(true) == StreamSend.Unreliable, "changing value not streamed");
                Assert(stream.Next(false) == StreamSend.Reliable, "resting value after a run not delivered reliably");
                Assert(stream.Next(false) == StreamSend.None, "resting value repeated");
                stream.Sent(false);
                Assert(stream.Next(false) == StreamSend.Reliable, "out-of-band unreliable send left without a reliable follow-up");
                stream.Sent(true);
                Assert(stream.Next(false) == StreamSend.None, "out-of-band reliable send repeated");
                stream.Reset();
                Assert(stream.Next(false) == StreamSend.Reliable && stream.Next(false) == StreamSend.None, "resync did not send the current value exactly once");
            });
            Test("request ids and revisions survive a one-sided boat context rebuild", () => {
                // A rebuilt context draws from the same session counters, so the other side never sees a smaller number.
                var hostOrder = new ItemRequestOrder(); var clientGate = new ItemStateGate(); bool semantic, pose;
                uint before = SessionCounters.NextRequest();
                Assert(hostOrder.Accept(10, before, false), "first request rejected");
                uint afterRebuild = SessionCounters.NextRequest();
                Assert(hostOrder.Accept(10, afterRebuild, false), "request after a client-side rebuild rejected as stale");
                uint oldRevision = SessionCounters.NextRevision();
                Assert(clientGate.Receive(oldRevision, 100, 0, 0, 10, out semantic, out pose), "state before rebuild rejected");
                uint newRevision = SessionCounters.NextRevision();
                Assert(clientGate.Receive(newRevision, 200, 0, 0, 10, out semantic, out pose) && semantic, "state after a host-side rebuild rejected as stale");
            });
            Test("item latest own request requires matching authenticated acknowledgement", () => {
                var gate = new ItemStateGate(); bool semantic, pose;
                gate.Begin(12); gate.Begin(13);
                Assert(!gate.Receive(2, 500, 10, 12, 10, out semantic, out pose) && gate.Pending, "old ack released newer action");
                Assert(!gate.Receive(2, 500, 11, 13, 10, out semantic, out pose) && gate.Pending, "another player ack released action");
                Assert(!gate.Receive(3, 600, 0, 0, 10, out semantic, out pose), "periodic state rolled back prediction");
                Assert(gate.Receive(2, 500, 10, 13, 10, out semantic, out pose) && !gate.Pending, "final ack lost");
                gate.Begin(14);
                Assert(gate.Receive(2, 500, 10, 14, 10, out semantic, out pose) && !gate.Pending, "no-op result couldn't acknowledge at same revision/tick");
            });
            Test("item request order rejects pre-drop pose without adding owner checks", () => {
                var order = new ItemRequestOrder();
                Assert(order.Accept(10, 3, false), "pickup");
                Assert(order.Accept(10, 4, false), "drop");
                Assert(!order.Accept(10, 3, true), "pre-drop held pose admitted");
                Assert(!order.Accept(10, 4, false), "discrete request repeated");
                Assert(order.Accept(11, 1, false), "different actor restricted");
                Assert(order.Accept(10, 4, true), "current pose epoch rejected");
                var wrap = new ItemRequestOrder();
                Assert(wrap.Accept(10, uint.MaxValue, false) && wrap.Accept(10, 1, false), "request wrap rejected");
            });
            Test("operation origin nests, suppresses relay and restores after faults", () => {
                Assert(InteractionContext.Source == InteractionSource.WorldSimulation, "dirty initial origin");
                using (InteractionContext.Begin(InteractionSource.LocalInput))
                {
                    Assert(InteractionContext.HasInput, "input missing");
                    ExpectFailure(() => { using (InteractionContext.Begin(InteractionSource.RemoteApply)) {
                        using (InteractionContext.Begin(InteractionSource.ActiveHold))
                            Assert(InteractionContext.Suppressed && !InteractionContext.HasInput, "remote apply became input");
                        throw new InvalidOperationException("apply fault");
                    }});
                    Assert(InteractionContext.HasInput, "fault leaked suppression");
                    var baseline = InteractionContext.Begin(InteractionSource.Baseline);
                    using (InteractionContext.Begin(InteractionSource.LocalInput))
                        Assert(InteractionContext.Suppressed, "baseline became input");
                    baseline.Dispose(); baseline.Dispose();
                    Assert(InteractionContext.HasInput, "duplicate cleanup damaged parent");
                }
                Assert(InteractionContext.Source == InteractionSource.WorldSimulation, "scope leaked");
            });
            Test("Harmony input finalizer restores origin and preserves vanilla fault", () => {
                var harmony = new Harmony("sailwind.runtime-smoke.input-scope");
                try {
                    harmony.Patch(typeof(InputFixture).GetMethod(nameof(InputFixture.Input)),
                        prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(BeginFixtureInput), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(EndFixtureInput), BindingFlags.Static | BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(Program).GetMethod(nameof(EndFixtureInput), BindingFlags.Static | BindingFlags.NonPublic)));
                    new InputFixture().Input(false);
                    Assert(InteractionContext.Source == InteractionSource.WorldSimulation, "normal call leaked");
                    ExpectFailure(() => new InputFixture().Input(true));
                    Assert(InteractionContext.Source == InteractionSource.WorldSimulation, "original fault leaked");
                } finally { harmony.UnpatchSelf(); }
            });
            Test("hook catalog rejects wrong overload and inherited empty fallback", () => {
                var hooks = new PatchHookCatalog(); int installed = 0;
                Assert(hooks.Install(typeof(HookFixture), "Action", Type.EmptyTypes, m => installed++), "no-arg missing");
                Assert(hooks.Install(typeof(HookFixture), "Action", Type.EmptyTypes, m => installed++), "duplicate changed result");
                Assert(!hooks.Install(typeof(HookFixture), "Action", new[] { typeof(object) }, m => installed++), "empty base overload counted");
                PatchHealth.Report("smoke signatures", hooks);
                Assert(installed == 1 && hooks.Installed == 1 && hooks.Total == 2 && hooks.Ready == 1, "duplicate/denominator drift");
                Assert(PatchHealth.StateOf("smoke signatures") == PatchHealthState.Partial &&
                    hooks.Detail.Contains("HookFixture.Action(Object)"), "missing signature hidden");
            });
            Test("a faulted patch set stays in its domain and only a required one blocks sessions", () => {
                int ran = 0; string logged = null;
                Assert(!PatchHealth.Install("smoke set A", () => { throw new TypeLoadException("removed type"); },
                    text => { logged = text; throw new Exception("logger fault"); }), "fault reported as installed");
                Assert(PatchHealth.Install("smoke set B", () => ran++, null) && ran == 1, "set after a faulted one skipped");
                Assert(PatchHealth.StateOf("smoke set A") == PatchHealthState.Failed && logged.Contains("smoke set A") &&
                    PatchHealth.FaultedSets == "smoke set A", "fault hidden: " + PatchHealth.FaultedSets);
                Assert(PatchHealth.Blocker == null, "optional set blocked sessions");
                PatchHealth.Install("smoke base", () => { throw new MissingMethodException("GoPointer", "LateUpdate"); }, null, required: true);
                Assert(PatchHealth.Blocker != null && PatchHealth.Blocker.Contains("smoke base"), "required fault did not block");
            });
            Test("hook installation failure and pending relay stay visible", () => {
                var hooks = new PatchHookCatalog();
                Assert(!hooks.Install(typeof(HookFixture), "Action", Type.EmptyTypes,
                    m => { throw new InvalidOperationException("installation fault"); }), "failure counted ready");
                hooks.Inspect(typeof(SleepEntryFixture), "SleepEntryFixture", "OnAltActivate", Type.EmptyTypes, "T10: request pending");
                PatchHealth.Report("smoke coverage", hooks);
                Assert(hooks.Ready == 0 && hooks.Total == 2 && hooks.Detail.Contains("Failed HookFixture.Action()") &&
                    hooks.Detail.Contains("Pending SleepEntryFixture.OnAltActivate()"), "failure/pending conflated");
                Assert(PatchHealth.StateOf("smoke coverage") != PatchHealthState.Ok, "false green coverage");
            });
            Test("no-arg sleep prefix blocks before pointer overload without blocking pickup", () => {
                var harmony = new Harmony("sailwind.runtime-smoke.sleep-entry");
                try
                {
                    var hooks = new PatchHookCatalog();
                    hooks.Install(typeof(SleepEntryFixture), "OnAltActivate", Type.EmptyTypes,
                        m => harmony.Patch(m, prefix: new HarmonyMethod(typeof(Program).GetMethod(
                            nameof(BlockFixtureSleep), BindingFlags.Static | BindingFlags.NonPublic))));
                    Assert(hooks.Ready == 1, hooks.Detail);
                    var bed = new SleepEntryFixture();
                    _fixtureClient = true;
                    bed.Pickup(); bed.OnAltActivate(); bed.OnAltActivate(new object());
                    Assert(bed.SleepEntries == 0 && bed.Pickups == 1, "sleep ran before pointer guard / pickup blocked");
                    _fixtureClient = false; bed.OnAltActivate();
                    Assert(bed.SleepEntries == 1, "host/offline sleep blocked");
                }
                finally { _fixtureClient = false; harmony.UnpatchSelf(); }
            });
            Test("installed game matches all 95 types and 184 declared input signatures", VerifyGameInputCatalog);
            Test("installed game sleep and item actions use the catalogued overloads", VerifyGameActionEntries);
            Test("installed game recipe/component fields match typed adapters", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.FullName);
                    var required = new Dictionary<string, string[]> {
                        { "FoodState", new[] { "dried", "smoked", "salted", "spoiled", "inWater" } },
                        { "CookableFood", new[] { "currentHeat", "currentTrigger" } },
                        { "ShipItemSoup", new[] { "currentWater", "currentEnergy", "currentUncookedEnergy", "currentVitamins", "currentProtein", "currentSpoiled", "currentSalted" } },
                        { "ShipItemKettle", new[] { "currentWater", "currentTeaAmount", "currentCookedTeaAmount", "currentTeaType" } },
                        { "StoveFuel", new[] { "lit", "inserted", "fuelTrigger", "cookTrigger" } },
                        { "StoveFuelTrigger", new[] { "currentFuel", "cookTrigger" } },
                        { "ShipItemStove", new[] { "currentHeat", "slots" } },
                        { "ShipItemPipe", new[] { "currentHeat", "tobaccoGraphics" } }
                    };
                    foreach (var pair in required)
                        foreach (string field in pair.Value)
                            Assert(types[pair.Key].Fields.Any(f => f.Name == field), pair.Key + "." + field + " binding missing");
                    Assert(types["Rainbow"].Methods.Any(m => m.Name == "ForceShowRainbow" && m.Parameters.Count == 0), "elixir world effect moved");
                }
            });
            Test("released control waits for final acknowledgement independently of newer node pose", () => {
                var rope = new ItemStateGate(); var wheel = new ItemStateGate(); bool semantic, pose;
                Assert(rope.Receive(4, 100, 0, 0, 10, out semantic, out pose), "rope baseline");
                Assert(wheel.Receive(7, 100, 0, 0, 10, out semantic, out pose), "wheel baseline");
                rope.Begin(21); wheel.Begin(22); // input already released; no timeout may clear these.
                Assert(!rope.Receive(4, 900, 0, 0, 10, out semantic, out pose), "newer snapshot rolled back released rope");
                Assert(!wheel.Receive(7, 900, 0, 0, 10, out semantic, out pose), "newer snapshot unlocked wheel");
                Assert(rope.Receive(5, 110, 10, 21, 10, out semantic, out pose), "rope ack lost behind unrelated newer node tick");
                Assert(wheel.Receive(8, 110, 10, 22, 10, out semantic, out pose), "wheel lock ack lost behind newer node tick");
                Assert(!wheel.Receive(7, 950, 0, 0, 10, out semantic, out pose), "old wheel input undid acknowledged lock");
                wheel.Begin(23);
                Assert(wheel.Receive(8, 110, 10, 23, 10, out semantic, out pose) && !wheel.Pending, "no-op final ack stranded control");
            });
            Test("installed steering wheel lock/input hooks match actual declared methods", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var wheel = game.MainModule.Types.Single(t => t.Name == "GPButtonSteeringWheel");
                    Assert(wheel.Fields.Any(f => f.Name == "locked" && f.FieldType.FullName == "System.Boolean"), "absolute lock field changed");
                    foreach (string method in new[] { "Lock", "Unlock", "ExtraLateUpdate", "ApplyRudderRotation" })
                        Assert(wheel.Methods.Any(m => m.Name == method && m.Parameters.Count == 0 && !m.IsStatic), method + " hook signature changed");
                    Assert(wheel.Methods.Single(m => m.Name == "Lock").Body.Instructions.Any(i =>
                        i.Operand is MethodReference target && target.Name == "UnStickyClick"), "fixture no longer exercises release-before-lock path");
                }
            });
            Test("anchor drop waits for its own latest reply", () => {
                var gate = new AnchorStateGate();
                Assert(gate.Receive(10, 0, 0, 7), "initial snapshot");
                gate.Begin(1); gate.Begin(2);
                Assert(!gate.Receive(13, 0, 0, 7) && gate.HasPending, "snapshot undid local drop");
                Assert(!gate.Receive(11, 7, 1, 7), "old pickup reply unlocked drop");
                Assert(!gate.Receive(12, 8, 2, 7), "another player unlocked drop");
                Assert(gate.Receive(12, 7, 2, 7) && !gate.HasPending, "drop reply rejected after ignored snapshot");
                Assert(gate.Receive(13, 0, 0, 7), "next snapshot rejected");
            });
            Test("mooring dock wait cannot acknowledge or erase latest local intent", () => {
                var pending = new PendingMooringState<string>(); string applied = null;
                pending.BeginRequest(7);
                Assert(!pending.Receive(10, 7, 10, null, waitingTarget: true), "unloaded target completed request");
                Assert(!pending.Receive(0, 0, 10, "Unmoor"), "actual unmoored snapshot erased waiting Moor");
                pending.TryApply(false, value => { applied = value; return true; });
                Assert(applied == null, "waiting request applied a fabricated outcome");
                pending.BeginRequest(8);
                Assert(!pending.Receive(10, 7, 10, "Moor"), "loaded old dock completed newer request");
                Assert(pending.Receive(10, 8, 10, "Moor"), "loaded matching dock did not complete request");
                pending.TryApply(false, value => { applied = value; return true; });
                Assert(applied == "Moor", "final mooring missing");
            });
            Test("installed mooring carry uses pickup objects and final pointer drop hooks", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    foreach (string name in new[] { "PickupableBoatMooringRope", "MooringRopeLengthAdjuster" })
                        Assert(types[name].BaseType.Name == "PickupableItem", name + " must not route through ShipItem");
                    Assert(types["PickupableBoatMooringRope"].Fields.Any(f => f.Name == "lengthAdjuster" && f.FieldType.Name == "MooringRopeLengthAdjuster"), "adjuster binding changed");
                    foreach (string field in new[] { "pickedUpFromMooring", "returnSequencePlaying", "renderer", "coiledRopeVisual", "defaultMaterial" })
                        Assert(types["MooringRopeLengthAdjuster"].Fields.Any(f => f.Name == field), "carry visual binding missing: " + field);
                    var pickup = types["GoPointer"].Methods.Single(m => m.Name == "PickUpItem");
                    int assigned = -1, notified = -1;
                    for (int i = 0; i < pickup.Body.Instructions.Count; i++)
                    {
                        var operand = pickup.Body.Instructions[i].Operand;
                        if (operand is FieldReference field && field.Name == "held" && pickup.Body.Instructions[i].OpCode.Code == Mono.Cecil.Cil.Code.Stfld) assigned = i;
                        if (operand is MethodReference method && method.Name == "OnPickup") notified = i;
                    }
                    Assert(assigned >= 0 && notified > assigned, "pickup must capture assigned local hand");
                    Assert(types["GoPointer"].Methods.Single(m => m.Name == "DropItem").Body.Instructions.Any(i =>
                        i.Operand is FieldReference field && field.Name == "held" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld), "drop does not clear hand before final hook");
                }
            });
            Test("totem effect revision survives an older storm snapshot at the same tick", () => {
                var weather = new ItemStateGate(); bool semantic, pose;
                Assert(weather.Receive(2, 100, 0, 0, 0, out semantic, out pose), "weather baseline");
                Assert(weather.Receive(3, 100, 0, 0, 0, out semantic, out pose), "same-tick cast effect rejected");
                Assert(!weather.Receive(2, 101, 0, 0, 0, out semantic, out pose), "old periodic attraction undid cast");
                Assert(weather.Receive(4, 102, 0, 0, 0, out semantic, out pose), "authoritative attraction decay stopped");
            });
            Test("document chunks replace only after complete identified revision", () => {
                var chunks = new ChunkAccumulator<string>();
                Assert(chunks.Add("map1/rev3", 2, 3, new[] { "C" }) == null, "partial document became visible");
                Assert(chunks.Add("map1/rev3", 2, 3, new[] { "C" }) == null, "duplicate counted twice");
                Assert(chunks.Add("map1/rev3", 0, 3, new[] { "A" }) == null, "missing middle accepted");
                var complete = chunks.Add("map1/rev3", 1, 3, new[] { "B" });
                Assert(string.Join("", complete) == "ABC", "out-of-order document assembled incorrectly");
                Assert(chunks.Add("map1/rev4", 0, 2, new[] { "old" }) == null, "incomplete old document accepted");
                Assert(chunks.Add("map1/rev5", 1, 2, new[] { "new2" }) == null, "mixed revisions completed");
                Assert(string.Join("", chunks.Add("map1/rev5", 0, 2, new[] { "new1" })) == "new1new2", "older chunk leaked into newer revision");
                ExpectFailure(() => chunks.Add("invalid", 2, 2, new string[0]));
                chunks.Add("count", 0, 2, new string[0]);
                ExpectFailure(() => chunks.Add("count", 1, 3, new string[0]));
            });
            Test("technical document failure clears only matching pending request and retains revision", () => {
                var gate = new ItemStateGate(); bool semantic, pose;
                gate.Receive(5, 5, 0, 0, 10, out semantic, out pose); gate.Begin(21);
                gate.Cancel(11, 21, 10); Assert(gate.Pending, "other player's missing result cleared prediction");
                gate.Cancel(10, 20, 10); Assert(gate.Pending, "old failure cleared newer edit");
                gate.Cancel(10, 21, 10); Assert(!gate.Pending && gate.Version == 5, "technical failure reset known document revision");
                Assert(!gate.Receive(4, 6, 0, 0, 10, out semantic, out pose), "failure made old document acceptable");
            });
            Test("installed chart data is saved per map prefab and previews are separate", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    Assert(types["SavePrefabData"].Fields.Any(f => f.Name == "chartData" && f.FieldType.Name == "ChartData"), "map document save identity changed");
                    foreach (string name in new[] { "tempLine", "lines", "points" }) Assert(types["ChartData"].Fields.Any(f => f.Name == name), name + " missing");
                    Assert(types["MapChart"].Fields.Any(f => f.Name == "originalParent" && f.FieldType.Name == "Transform"), "stable map parent binding changed");
                    Assert(types["MapChart"].Methods.Single(m => m.Name == "OnActivate").Parameters.Single().ParameterType.Name == "Vector3", "chart final hook changed");
                    Assert(types["SaveablePrefab"].Methods.Single(m => m.Name == "PrepareSaveData").Body.Instructions.Any(i =>
                        i.Operand is FieldReference field && field.Name == "chartData" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld), "host committed chart would not enter save");
                }
            });
            Test("dirt chunks keep a pending stroke protected until the entire result arrives", () => {
                var chunks = new ChunkAccumulator<byte>(); var gate = new ItemStateGate(); bool semantic, pose;
                gate.Receive(3, 3, 0, 0, 10, out semantic, out pose); gate.Begin(8);
                Assert(!gate.Receive(4, 4, 0, 0, 10, out semantic, out pose), "quiet daily dirt rolled back own stroke");
                Assert(chunks.Add("scene7/rev5/actor10/request8", 1, 2, new byte[] { 3, 4 }) == null && gate.Pending, "partial texture acknowledged stroke");
                var png = chunks.Add("scene7/rev5/actor10/request8", 0, 2, new byte[] { 1, 2 });
                Assert(png.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "texture bytes mixed/reordered");
                Assert(gate.Receive(5, 5, 10, 8, 10, out semantic, out pose) && !gate.Pending, "complete host result did not release stroke");
                Assert(!gate.Receive(4, 4, 10, 7, 10, out semantic, out pose), "late previous stroke replaced final texture");
            });
            Test("wind orb drop rejects its preceding pose and wind epoch", () => {
                var order = new ItemRequestOrder(); var gate = new ItemStateGate(); bool semantic, pose;
                Assert(order.Accept(10, 1, false) && order.Accept(10, 1, true), "pickup/current wind rejected");
                Assert(order.Accept(10, 2, false), "drop rejected");
                Assert(!order.Accept(10, 1, true), "pre-drop wind could be reapplied");
                Assert(order.Accept(11, 1, false), "another player's interaction restricted");
                gate.Begin(2); Assert(!gate.Receive(4, 900, 10, 1, 10, out semantic, out pose), "late pickup rolled back drop");
                Assert(gate.Receive(5, 900, 10, 2, 10, out semantic, out pose), "drop final reply rejected");
            });
            Test("installed orb and touch-wheel hooks match non-item identity and direct input path", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    Assert(types["WindTotemOrb"].BaseType.Name == "PickupableItem", "orb cannot route as ShipItem");
                    foreach (string name in new[] { "totem", "orbParticles", "audio", "minPitch", "maxPitch", "particlesMinSize", "particlesMaxSize", "maxCarryDistance" })
                        Assert(types["WindTotemOrb"].Fields.Any(f => f.Name == name), "orb binding changed: " + name);
                    Assert(types["WindTotemOrb"].Methods.Single(m => m.Name == "Update").Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "ForceNewWind"), "orb world effect moved");
                    var touch = types["GPButtonSteeringWheel"].Methods.Single(m => m.Name == "ApplyRudderRotationFromWheel");
                    Assert(touch.Parameters.Single().ParameterType.Name == "Single", "touch-wheel input hook changed");
                    Assert(!touch.Body.Instructions.Any(i => i.Operand is FieldReference field && field.Name == "currentInput" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld), "touch-wheel adapter should be re-audited: game now updates currentInput itself");
                }
            });
            Test("installed shipyard pays before installation and saves native configuration", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    var calls = types["Shipyard"].Methods.Single(m => m.Name == "ConfirmOrder").Body.Instructions;
                    int payment = calls.ToList().FindIndex(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stind_I4);
                    int install = calls.ToList().FindIndex(i => i.Operand is MethodReference method && method.Name == "InstallSails");
                    int parts = calls.ToList().FindIndex(i => i.Operand is MethodReference method && method.Name == "ApplyCurrentOrder");
                    Assert(payment >= 0 && payment < install && install < parts, "paid-success marker is before payment or actual install sequence changed");
                    foreach (string name in new[] { "AdmitShip", "CancelOrder", "DischargeShip", "ConfirmOrder" })
                        Assert(types["Shipyard"].Methods.Any(m => m.Name == name && m.HasBody), "shipyard hook missing: " + name);
                    Assert(types["Shipyard"].Fields.Any(f => f.Name == "originalData" && f.FieldType.Name == "SaveBoatCustomizationData"), "cancel data binding changed");
                    Assert(types["SaveableBoatCustomization"].Methods.Single(m => m.Name == "LoadData").Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "LoadSail"), "native refit loader changed");
                    Assert(types["Mast"].Methods.Any(m => m.Name == "LoadSail" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "SaveSailData"), "sail loader signature changed");
                    Assert(types["Sail"].Methods.Any(m => m.Name == "IsInstalled" && m.Parameters.Count == 0), "partial-result filter cannot distinguish installed sails");
                    foreach (string name in new[] { "prefabIndex", "mastIndex", "installHeight", "sailColor", "scaleY", "scaleZ" })
                        Assert(types["SaveSailData"].Fields.Any(f => f.Name == name), "saved sail field missing: " + name);
                }
            });
            Test("installed dirt hooks bind actual UV and the texture saved on its scene object", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    foreach (string name in new[] { "dirtMaterial", "saveable" }) Assert(types["CleanableObject"].Fields.Any(f => f.Name == name), "dirt binding changed: " + name);
                    foreach (string name in new[] { "canvasTexture", "brushMaterial", "colorMaterial" }) Assert(types["MasterPainter"].Fields.Any(f => f.Name == name), "painter binding changed: " + name);
                    var paint = types["MasterPainter"].Methods.Single(m => m.Name == "PaintObject");
                    Assert(string.Join(",", paint.Parameters.Select(p => p.ParameterType.Name)) == "CleanableObject,Vector2,Texture", "UV hook signature changed");
                    Assert(types["Cleaner"].Methods.Single(m => m.Name == "LateUpdate").Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "PaintObject"), "stroke no longer comes from actual cleaner UV");
                    Assert(types["CleanableObject"].Methods.Single(m => m.Name == "ApplyNewDirtTexture").Body.Instructions.Any(i => i.Operand is FieldReference field && field.Name == "extraTexture" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld), "applied dirt is not saved by SaveableObject");
                    Assert(types["Shipyard"].Methods.Single(m => m.Name == "ConfirmOrder").Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "CleanFully"), "confirmed cleaning domain entry changed");
                }
            });
            Test("transform timeline replaces a same-tick final result including its first sample", () => {
                var samples = new SnapshotBuffer<Tuple<long, string>>(s => s.Item1, 3);
                samples.Push(Tuple.Create(100L, "before")); samples.Push(Tuple.Create(100L, "final"));
                Assert(samples.Count == 1 && samples[0].Item2 == "final", "first same-tick result lost");
                samples.Push(Tuple.Create(300L, "C")); samples.Push(Tuple.Create(200L, "B"));
                Assert(samples[1].Item2 == "B" && samples[2].Item2 == "C", "late bracket order lost");
                samples.Push(Tuple.Create(200L, "new B")); Assert(samples.Count == 3 && samples[1].Item2 == "new B", "duplicate bracket not replaced");
                samples.Push(Tuple.Create(50L, "stale")); Assert(samples.Count == 3 && samples[0].Item1 == 100, "stale sample resurrected");
                samples.Push(Tuple.Create(400L, "D")); Assert(samples.Count == 3 && samples[0].Item1 == 200, "buffer capacity not enforced");
                samples.Clear(); Assert(samples.Count == 0, "disconnect retained samples");
            });
            Test("child pose cannot cross its parent's drop epoch or older semantic revision", () => {
                var parent = new ItemStateGate(); var child = new ItemStateGate(); var order = new ItemRequestOrder(); bool semantic, pose;
                parent.Receive(3, 100, 0, 0, 10, out semantic, out pose); parent.Begin(9);
                order.Accept(10, 8, false); order.Accept(10, 9, false);
                Assert(!order.Accept(10, 8, true) && order.Accept(10, 9, true), "pre-drop bobber passed parent request ordering");
                Assert(parent.Pending, "child delivery acknowledged the root drop");
                parent.Receive(4, 100, 10, 9, 10, out semantic, out pose);
                Assert(unchecked((int)(3u - parent.Version)) < 0, "older child root revision became current");
                Assert(child.Receive(5, 100, 0, 0, 10, out semantic, out pose), "child final baseline rejected");
                Assert(!child.Receive(4, 110, 0, 0, 10, out semantic, out pose), "later tick admitted older child state");
                Assert(child.Receive(6, 100, 0, 0, 10, out semantic, out pose), "same-tick final child rejected");
            });
            Test("installed rod and chip-log have separately bound bobbers and passive visual methods", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    foreach (string type in new[] { "ShipItemFishingRod", "ShipItemChipLog" })
                    {
                        foreach (string name in new[] { "bobberJoint", "bobberBody", "currentTargetLength", "activated", "holding", "throwing", "targetReelVolume", "reelAudio" })
                            Assert(types[type].Fields.Any(f => f.Name == name), "child binding changed: " + type + "." + name);
                        foreach (string method in new[] { "Update", "ExtraLateUpdate", "UpdateRope", "OnAltActivate" }) Assert(types[type].Methods.Any(m => m.Name == method && m.Parameters.Count == 0), "child hook changed: " + type + "." + method);
                        Assert(types[type].Methods.Single(m => m.Name == "OnLoad").Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "set_parent"), "bobber isn't an independent shifting-world object anymore");
                    }
                    foreach (string name in new[] { "rod", "rodRotator", "currentFish", "fishEnergy", "currentTargetTension", "tensionAudio" }) Assert(types["FishingRodFish"].Fields.Any(f => f.Name == name), "fish visual binding changed: " + name);
                    Assert(types["ShipItemFishingRod"].Methods.Any(m => m.Name == "UpdateBend"), "passive bend renderer absent");
                    Assert(types["ShipItemChipLog"].Fields.Any(f => f.Name == "thrown"), "chip-log deployed state absent");
                }
            });
            Test("installed instrument targets and totem completion match typed adapters", () => {
                using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
                {
                    var types = game.MainModule.Types.ToDictionary(t => t.Name);
                    var required = new Dictionary<string, string[]> {
                        { "ShipItemClock", new[] { "lid", "lidOpen", "lidAnimPlaying" } },
                        { "ShipItemQuadrant", new[] { "rotatingParent", "inspecting", "initialRot", "inspectRot" } },
                        { "ShipItemScroll", new[] { "currentPage", "pages", "page", "filter", "openMesh", "closedMesh", "arrowUp", "arrowDown" } },
                        { "ShipItemTotem", new[] { "rune", "totem", "casting", "castingTime" } }
                    };
                    foreach (var pair in required) foreach (string field in pair.Value)
                        Assert(types[pair.Key].Fields.Any(f => f.Name == field), pair.Key + "." + field + " binding missing");
                    Assert(types["ShipItemClock"].Methods.Any(m => m.Name == "RotateLid" && m.Parameters.Count == 1), "absolute clock animator absent");
                    Assert(types["ShipItemQuadrant"].Methods.Any(m => m.Name == "SmoothlyRotate" && m.Parameters.Count == 1), "absolute quadrant animator absent");
                    var finish = types["ShipItemTotem"].Methods.Single(m => m.Name == "FinishCast" && m.Parameters.Count == 0);
                    Assert(finish.Body.Instructions.Any(i => i.Operand is FieldReference field && field.Name == "totemAttraction" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stsfld), "cast world result moved");
                }
            });
            Test("anchor state ordering survives equal ticks and revision wrap", () => {
                var gate = new AnchorStateGate();
                Assert(gate.Receive(uint.MaxValue, 0, 0, 7), "first state");
                Assert(gate.Receive(0, 0, 0, 7), "revision wrap");
                Assert(!gate.Receive(uint.MaxValue, 0, 0, 7) && !gate.Receive(0, 0, 0, 7), "old or duplicate state accepted");
                Assert(gate.Receive(1, 0, 0, 7), "later state");
            });
            Test("anchor pending hand is independent per boat", () => {
                var a = new AnchorStateGate(); var b = new AnchorStateGate();
                a.Begin(1);
                Assert(b.Receive(5, 0, 0, 7) && !b.HasPending && a.HasPending, "one boat blocked the other");
                Assert(a.Receive(5, 7, 1, 7), "pickup reply");
                a.Begin(2);
                Assert(!a.Receive(6, 7, 1, 7) && a.HasPending, "pickup echo undid new drop");
                Assert(a.Receive(7, 7, 2, 7), "drop reply");
            });
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
            Test("hatch baseline is quiet and duplicate baselines do not toggle", () => {
                bool open = false; int toggles = 0;
                var target = new DeferredToggle();
                target.Receive(true, interaction: false);
                Assert(!target.HasInteraction, "initial state invented an interaction");
                Assert(target.TryApply(() => open, () => { open = !open; toggles++; }), "baseline did not converge");
                target.Receive(true, interaction: false);
                Assert(target.TryApply(() => open, () => { open = !open; toggles++; }) && toggles == 1,
                    "duplicate baseline toggled the hatch");
            });
            Test("hatch baseline cannot erase a queued interaction during animation", () => {
                bool open = false, inMotion = true;
                var target = new DeferredToggle();
                target.Receive(true, interaction: true);
                target.Receive(false, interaction: false);
                target.Receive(true, interaction: false);
                Assert(target.HasInteraction, "baseline erased a queued real interaction");
                Action activate = () => { if (!inMotion) open = !open; };
                Assert(!target.TryApply(() => open, activate), "busy animation accepted the target");
                inMotion = false;
                Assert(target.TryApply(() => open, activate) && open, "latest queued target did not converge");
            });
            Test("hatch interaction supersedes an initial baseline", () => {
                bool open = false; int toggles = 0;
                var target = new DeferredToggle();
                target.Receive(true, interaction: false);
                target.Receive(false, interaction: true);
                Assert(target.HasInteraction && !target.Target, "stale baseline replaced the click");
                Assert(target.TryApply(() => open, () => { open = !open; toggles++; }) && toggles == 0,
                    "obsolete initial state was animated");
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
    private class HookBase { public virtual void Action(object pointer) { } }
    private sealed class InputFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Input(bool fail)
        {
            Assert(InteractionContext.HasInput, "prefix didn't set source");
            if (fail) throw new InvalidOperationException("vanilla input fault");
        }
    }
    private static void BeginFixtureInput(out InteractionContext.Scope __state)
        => __state = InteractionContext.Begin(InteractionSource.LocalInput);
    private static void EndFixtureInput(InteractionContext.Scope __state) => __state?.Dispose();
    private sealed class HookFixture : HookBase { public void Action() { } }
    private sealed class SleepEntryFixture
    {
        internal int SleepEntries, Pickups;
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnAltActivate() { SleepEntries++; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnAltActivate(object pointer) { }
        public void Pickup() { Pickups++; }
    }
    private static bool _fixtureClient;
    private static bool BlockFixtureSleep() => PatchGuard.Prefix(() => !_fixtureClient, e => { });

    private static string GameAssemblyPath()
    {
        var metadata = (AssemblyMetadataAttribute)Assembly.GetExecutingAssembly().GetCustomAttributes(
            typeof(AssemblyMetadataAttribute), false).First(a => ((AssemblyMetadataAttribute)a).Key == "GameDir");
        return Path.Combine(metadata.Value, "Sailwind_Data", "Managed", "Assembly-CSharp.dll");
    }

    private static bool Declares(TypeDefinition type, InteractionActionCatalog.Input input)
        => type != null && type.Methods.Any(m => m.Name == input.Method && !m.IsAbstract &&
            m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(input.Parameters));

    private static void VerifyGameInputCatalog()
    {
        using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
        {
            var types = game.MainModule.Types.ToDictionary(t => t.FullName);
            var actual = new HashSet<string>();
            foreach (var type in types.Values)
            {
                var current = type;
                while (current != null)
                {
                    if (current.FullName == "GoPointerButton") { actual.Add(type.FullName); break; }
                    if (current.BaseType == null || !types.TryGetValue(current.BaseType.FullName, out current)) break;
                }
            }
            Assert(actual.SetEquals(InteractionActionCatalog.Types), "type inventory differs from installed game");
            Assert(actual.Count == 95 && InteractionActionCatalog.Inputs.Length == 184, "audited baseline changed");
            foreach (var input in InteractionActionCatalog.Inputs)
                Assert(Declares(types[input.TypeName], input), input.TypeName + "." + input.Method + " signature absent");
        }
    }

    private static void VerifyGameActionEntries()
    {
        using (var game = AssemblyDefinition.ReadAssembly(GameAssemblyPath()))
        {
            var types = game.MainModule.Types.ToDictionary(t => t.FullName);
            Assert(ItemActionCatalog.Routes.Length == 30 &&
                ItemActionCatalog.Routes.Count(r => r.Input.Method == "OnAltHeld" && r.Input.Parameters.Length == 0) == 9 &&
                ItemActionCatalog.Routes.Count(r => r.Input.Method == "OnAltActivate" && r.Input.Parameters.Length == 0) == 19,
                "held/alt catalog incomplete");
            foreach (var route in ItemActionCatalog.Routes)
                Assert(Declares(types[route.Input.TypeName], route.Input), route.Input.TypeName + " action signature absent");
            Assert(InteractionActionCatalog.HostOnlyInputs.Any(input => input.TypeName == "GPButtonAutosaveToggle" && input.Method == "OnActivate"),
                "host-only autosave guard missing");
            var autosave = InteractionActionCatalog.HostOnlyInputs.Single(input => input.TypeName == "GPButtonAutosaveToggle" && input.Method == "OnActivate");
            Assert(Declares(types[autosave.TypeName], autosave), "host-only autosave signature absent");
            var bed = types["ShipItemBed"].Methods.Single(m => m.Name == "OnAltActivate" && m.Parameters.Count == 0);
            Assert(bed.Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "EnterBed"),
                "sleep entry moved; revisit guard");
            bool noArgFirst = types["GoPointer"].Methods.Where(m => m.HasBody).Any(m => {
                var calls = m.Body.Instructions.Select(i => i.Operand as MethodReference)
                    .Where(c => c != null && c.Name == "OnAltActivate").ToArray();
                return calls.Length >= 2 && calls[0].Parameters.Count == 0 && calls[1].Parameters.Count == 1;
            });
            Assert(noArgFirst, "pointer call order changed; revisit guard");
            var sleep = types["Sleep"];
            Assert(sleep.Methods.Any(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody), "native sleep Update route missing");
            Assert(sleep.Methods.Any(m => m.Name == "FallAsleep" && m.Parameters.Count == 0 && !m.IsStatic), "native FallAsleep signature changed");
            Assert(sleep.Methods.Any(m => m.Name == "LeaveBed" && m.Parameters.Count == 0 && !m.IsStatic), "native LeaveBed signature changed");
            Assert(sleep.Fields.Any(f => f.Name == "timeskipSleep" && f.IsStatic && f.FieldType.FullName == "System.Boolean"), "native timeskip flag changed");
            Assert(ItemActionCatalog.Routes.Any(r => r.Input.TypeName == "ShipItemElixir" && r.Domain == "Item results") &&
                ItemActionCatalog.Routes.Any(r => r.Input.TypeName == "ShipItemCrate" && r.Domain == "Local"),
                "personal effect must use result domain; crate UI stays local");
        }
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
