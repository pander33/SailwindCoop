using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization.Formatters.Binary;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P4.4 — save policy. We play in the HOST's world (host's save). A connected guest must not write
    /// the world to disk: autosave, sleep-save and manual save would all clobber the guest's own local
    /// save with the host's world (or spawn junk saves). <see cref="SavePatches"/> blocks the single
    /// choke point <c>SaveLoadManager.SaveGame(bool)</c> on the client; the host saves normally. The guard
    /// is dynamic (role + link state) so single-player saving is restored the moment the guest disconnects.
    /// </summary>
    public static class SavePatches
    {
        private static FieldInfo _saveOwner, _saveBusy;
        private static readonly HashSet<int> FailedSlots = new HashSet<int>();

        // A failed write may have left a nonempty, incomplete primary file.
        internal static bool CurrentSlotSaveFailed => FailedSlots.Contains(SaveSlots.currentSlot);

        public static void Apply(Harmony harmony)
        {
            bool save = TryPatch(harmony, typeof(SaveLoadManager), "SaveGame", new[] { typeof(bool) }, nameof(PreSaveGame));
            Plugin.Logger.LogInfo("[SavePatches] Save patch (guest does not write host world): SaveGame=" + save);
            SailwindCoop.Runtime.PatchHealth.Report("Save", save ? 1 : 0, 1);
        }

        internal static void ApplyRecovery(Harmony harmony)
        {
            var method = AccessTools.DeclaredMethod(typeof(SaveLoadManager), "DoSaveGame", new[] { typeof(bool) });
            var moveNext = method == null ? null : AccessTools.EnumeratorMoveNext(method);
            _saveOwner = moveNext?.DeclaringType?.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _saveBusy = AccessTools.DeclaredField(typeof(SaveLoadManager), "busy");
            if (moveNext == null || moveNext.ReturnType != typeof(bool) ||
                _saveOwner?.FieldType != typeof(SaveLoadManager) || _saveBusy?.FieldType != typeof(bool))
                throw new MissingMethodException("Could not bind SaveLoadManager.DoSaveGame iterator and busy flag");

            harmony.Patch(moveNext, finalizer: new HarmonyMethod(typeof(SavePatches), nameof(FinishSave)));
            PatchHealth.Report("Save recovery", 1, 1);
            PatchHealth.Install("World save write", () => {
                harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(SavePatches), nameof(StageWorldSave)));
                PatchHealth.Report("World save write", 1, 1);
            }, message => Plugin.Logger.LogWarning("[SavePatches] " + message));
        }

        private static IEnumerable<CodeInstruction> StageWorldSave(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var rotate = AccessTools.DeclaredMethod(typeof(SaveSlots), nameof(SaveSlots.PushBackups));
            var formatter = typeof(BinaryFormatter).GetConstructor(Type.EmptyTypes);
            var path = AccessTools.DeclaredMethod(typeof(SaveSlots), nameof(SaveSlots.GetCurrentSavePath));
            var create = AccessTools.DeclaredMethod(typeof(File), nameof(File.Create), new[] { typeof(string) });
            var serialize = AccessTools.DeclaredMethod(typeof(BinaryFormatter), nameof(BinaryFormatter.Serialize), new[] { typeof(Stream), typeof(object) });
            var close = AccessTools.DeclaredMethod(typeof(Stream), nameof(Stream.Close));
            int start = code.FindIndex(instruction => instruction.Calls(rotate));
            if (start < 0 || start + 9 >= code.Count ||
                code[start + 1].opcode != OpCodes.Newobj || !Equals(code[start + 1].operand, formatter) ||
                !code[start + 2].Calls(path) || !code[start + 3].Calls(create) ||
                !code[start + 4].IsStloc() || !code[start + 5].IsLdloc() || !code[start + 6].IsLdloc() ||
                !code[start + 7].Calls(serialize) || !code[start + 8].IsLdloc() || !code[start + 9].Calls(close) ||
                !Equals(code[start + 4].operand, code[start + 5].operand) ||
                !Equals(code[start + 4].operand, code[start + 8].operand) ||
                code.GetRange(start + 1, 9).Any(instruction => instruction.labels.Count != 0 || instruction.blocks.Count != 0))
                throw new InvalidOperationException("Could not match the original world save write");

            // Preserve the original branch target and load the same SaveContainer local.
            var original = code[start];
            original.opcode = code[start + 6].opcode;
            original.operand = code[start + 6].operand;
            code.RemoveRange(start + 1, 9);
            code.Insert(start + 1, new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(SavePatches), nameof(WriteWorld))));
            return code;
        }

        private static void WriteWorld(object data)
        {
            int slot = SaveSlots.currentSlot;
            var formatter = new BinaryFormatter();
            AtomicSaveFile.WriteWorld(SaveSlots.GetSlotSavePath(slot), index => SaveSlots.GetBackupPath(slot, index),
                stream => formatter.Serialize(stream, data));
        }

        private static void FinishSave(object __instance, bool __result, Exception __exception)
        {
            try
            {
                if (__exception != null)
                {
                    int slot = SaveSlots.currentSlot;
                    FailedSlots.Add(slot);
                    // Act on the manager that owned this iterator, even if the singleton changed.
                    var owner = _saveOwner.GetValue(__instance) as SaveLoadManager;
                    if (owner != null) _saveBusy.SetValue(owner, false);
                    Plugin.Logger.LogError("[SavePatches] Save failed in slot " + slot +
                        "; another save can be attempted. " + __exception);
                }
                else if (!__result)
                {
                    FailedSlots.Remove(SaveSlots.currentSlot);
                }
            }
            catch (Exception e) { Plugin.Logger.LogError("[SavePatches] Save recovery failed: " + e); }
        }

        /// <summary>True while we are a connected guest — saving the host's world locally is suppressed.</summary>
        private static bool GuestConnected()
        {
            var net = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net : null;
            return net != null && net.Role == Role.Client && net.State == LinkState.Connected;
        }

        private static bool TryPatch(Harmony harmony, Type type, string method, Type[] args, string prefixName)
        {
            try
            {
                var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null) return false;
                var prefix = new HarmonyMethod(typeof(SavePatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SavePatches] " + method + ": " + e.Message);
                return false;
            }
        }

        // Return false to skip vanilla SaveGame on a connected guest.
        private static bool PreSaveGame()
        {
            try
            {
                if (GuestConnected())
                {
                    // Don't write the host's world to the guest's disk; persist the guest's CHARACTER instead.
                    CoopProfile.SaveFromGame();
                    Plugin.Logger.LogInfo("[SavePatches] Host world not saved; guest character profile was written");
                    return false;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SavePatches] PreSaveGame: " + e.Message); }
            return true;
        }
    }
}
