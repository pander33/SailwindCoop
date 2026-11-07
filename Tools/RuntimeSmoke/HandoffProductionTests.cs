using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class HandoffProductionTests
{
    private static bool Calls(MethodDefinition m, string name) => m.Body.Instructions.Any(i => i.Operand is MethodReference call && call.Name == name);
    internal static void Run(Action<string, Action> test, Action<bool, string> assert, string gamePath)
    {
        test("instrument handoff restores the full snapshot before discarding it and before native simulation", () =>
        {
            string pluginPath = Environment.GetEnvironmentVariable("SAILWINDCOOP_SMOKE_PLUGIN") ??
                Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(gamePath))), "BepInEx", "plugins", "SailwindCoop", "SailwindCoop.dll");
            using (var plugin = AssemblyDefinition.ReadAssembly(pluginPath))
            using (var game = AssemblyDefinition.ReadAssembly(gamePath))
            {
                var items = plugin.MainModule.Types.Single(t => t.Name == "ItemSync");
                var restore = items.Methods.Single(m => m.Name == "RestoreInstrument");
                var calls = restore.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToList();
                assert(calls.IndexOf("ApplyInstrumentFields") >= 0 && calls.IndexOf("ResumeFishSimulation") > calls.IndexOf("ApplyInstrumentFields") &&
                    calls.IndexOf("ClearInstrumentSamples") > calls.IndexOf("ResumeFishSimulation"), "handoff clears the only full fish snapshot before applying it");
                var resume = items.Methods.Single(m => m.Name == "ResumeFishSimulation");
                string[] fields = { "fishTimer", "pullForce", "snapTimer", "lastLineLength" };
                var fish = game.MainModule.Types.Single(t => t.Name == "FishingRodFish");
                foreach (string field in fields)
                    assert(fish.Fields.Any(f => f.Name == field) && resume.Body.Instructions.Any(i => Equals(i.Operand, field)), "missing native handoff field " + field);
                var patches = plugin.MainModule.Types.Single(t => t.Name == "InstrumentPosePatches");
                assert(Calls(patches.Methods.Single(m => m.Name == "PreFish"), "SuppressInstrumentSimulation") &&
                    Calls(patches.Methods.Single(m => m.Name == "PreSimulation"), "SuppressInstrumentSimulation"), "native simulation bypasses authority handoff");
                assert(Calls(items.Methods.Single(m => m.Name == "SuppressInstrumentSimulation"), "RestoreInstrument"), "authority check does not finish restoring the child");
            }
        });
        test("historical held poses have separate ingress and relay without weakening action gates", () =>
        {
            string pluginPath = Environment.GetEnvironmentVariable("SAILWINDCOOP_SMOKE_PLUGIN");
            if (string.IsNullOrEmpty(pluginPath)) pluginPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(gamePath))),
                "BepInEx", "plugins", "SailwindCoop", "SailwindCoop.dll");
            using (var plugin = AssemblyDefinition.ReadAssembly(pluginPath))
            {
                var items = plugin.MainModule.Types.Single(t => t.Name == "ItemSync");
                assert(Calls(items.Methods.Single(m => m.Name == "OnItemRequest"), "ReceiveHistoricalHeldPose"), "host loses history before it can reach third player");
                var historical = items.Methods.Single(m => m.Name == "ReceiveHistoricalHeldPose");
                var localInsert = historical.Body.Instructions.Single(i => i.Operand is MethodReference call &&
                    call.DeclaringType.Name == "SupportPoseBuffer" && call.Name == "Push");
                assert(localInsert.Next.OpCode.Code == Mono.Cecil.Cil.Code.Pop,
                    "relay is still conditional on the host's local playback accepting the sample");
                assert(Calls(historical, "Broadcast") && !historical.Body.Instructions.Any(i =>
                    i.Operand is FieldReference f && f.Name == "HeldSample" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld), "relay replaced the newest source pose");
                assert(Calls(items.Methods.Single(m => m.Name == "TryApplyItemState"), "SameHeldAttachment"), "client history bypasses lifecycle identity");
                var players = plugin.MainModule.Types.Single(t => t.Name == "PlayerSync");
                assert(Calls(players.Methods.Single(m => m.Name == "OnPlayerState"), "get_Restarted"),
                    "avatar did not discard stale segments when its source history restarted");
                var support = plugin.MainModule.Types.Single(t => t.Name == "SupportPoseBuffer");
                assert(Calls(support.Methods.Single(m => m.Name == "Push"), "get_Restarted"),
                    "held poses did not discard stale segments when their source history restarted");
                assert(Calls(players.Methods.Single(m => m.Name == "InsertReorderedPose"), "EndOfRun"),
                    "avatar does not rebuild support groups from ordered source samples");
                var anchor = plugin.MainModule.Types.Single(t => t.Name == "AnchorSync");
                var mooring = plugin.MainModule.Types.Single(t => t.Name == "MooringSync").NestedTypes.Single(t => t.Name == "Carry");
                assert(anchor.Fields.Single(f => f.Name == "_slave").FieldType.Name == "SupportPoseBuffer" &&
                    mooring.Fields.Single(f => f.Name == "Pose").FieldType.Name == "SupportPoseBuffer", "specialized carry still clears a single-frame buffer");
            }
        });
    }
}
