using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>Hands the stock Escape menu's clock ownership to co-op while a session runs.</summary>
    internal static class GameMenuPause
    {
        private static StartMenu _openMenu;
        private static bool _ownsClock;
        private static bool _savingOwnWorld;
        private static float _resumeScale;
        private static bool _resumeAutoSync;

        internal static bool InSession
        {
            get
            {
                var net = CoopBehaviour.Instance?.Net;
                return net != null && net.Role != Role.None && net.State == LinkState.Connected;
            }
        }

        internal static bool HoldsMovement =>
            _savingOwnWorld || (_openMenu != null && GameState.inCursorMenu && InSession);

        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            foreach (string method in new[] { "GameToSettings", "SettingsToGame" })
            {
                hooks.Install(typeof(StartMenu), method, Type.EmptyTypes, target => harmony.Patch(target,
                    transpiler: new HarmonyMethod(typeof(GameMenuPause), nameof(ReplaceClockWrites))));
            }
            hooks.Install(typeof(OVRPlayerController), nameof(OVRPlayerController.UpdateMovement), Type.EmptyTypes,
                target => harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(GameMenuPause), nameof(BeforeMovement))));
            PatchHealth.Report("Escape menu clock", hooks);
            if (hooks.Installed != hooks.Total) throw new InvalidOperationException(hooks.Detail);
        }

        private static bool BeforeMovement(OVRPlayerController __instance, ref Vector3 ___MoveThrottle)
        {
            if (!HoldsMovement || __instance != Refs.ovrController) return true;
            // Keep the controller's gravity and collision step while suppressing player input.
            ___MoveThrottle.x = 0f;
            ___MoveThrottle.z = 0f;
            return false;
        }

        private static IEnumerable<CodeInstruction> ReplaceClockWrites(IEnumerable<CodeInstruction> source,
            MethodBase __originalMethod)
        {
            var setScale = AccessTools.PropertySetter(typeof(Time), nameof(Time.timeScale));
            var setAutoSync = AccessTools.PropertySetter(typeof(Physics), nameof(Physics.autoSyncTransforms));
            var apply = AccessTools.Method(typeof(GameMenuPause),
                __originalMethod.Name == "GameToSettings" ? nameof(OpenClock) : nameof(CloseClock));
            var result = new List<CodeInstruction>();
            int scaleWrites = 0, syncWrites = 0;
            foreach (var instruction in source)
            {
                if (instruction.Calls(setScale))
                {
                    scaleWrites++;
                    var loadMenu = new CodeInstruction(OpCodes.Ldarg_0);
                    loadMenu.labels.AddRange(instruction.labels);
                    loadMenu.blocks.AddRange(instruction.blocks);
                    result.Add(loadMenu);
                    result.Add(new CodeInstruction(OpCodes.Call, apply));
                }
                else if (instruction.Calls(setAutoSync))
                {
                    syncWrites++;
                    var discard = new CodeInstruction(OpCodes.Pop);
                    discard.labels.AddRange(instruction.labels);
                    discard.blocks.AddRange(instruction.blocks);
                    result.Add(discard);
                }
                else result.Add(instruction);
            }
            if (scaleWrites != 1 || syncWrites != 1)
                throw new InvalidOperationException("Unexpected clock writes in " + __originalMethod.Name);
            return result;
        }

        private static void OpenClock(float scale, StartMenu menu)
        {
            _openMenu = menu;
            _ownsClock = false;
            if (!InSession && !_savingOwnWorld) TakeClock(scale);
        }

        private static void CloseClock(float vanillaResumeScale, StartMenu menu)
        {
            if (_openMenu == menu)
            {
                if (_ownsClock)
                {
                    Time.timeScale = _resumeScale;
                    Physics.autoSyncTransforms = true;
                }
                _openMenu = null;
                _ownsClock = false;
            }
            else if (!InSession)
            {
                Time.timeScale = vanillaResumeScale;
                Physics.autoSyncTransforms = true;
            }
        }

        internal static void HandOffToSession()
        {
            if (_openMenu == null || !_ownsClock) return;
            Time.timeScale = _resumeScale;
            Physics.autoSyncTransforms = _resumeAutoSync;
            _ownsClock = false;
        }

        internal static void ReturnToSolo()
        {
            if (_openMenu != null && !_ownsClock && !_savingOwnWorld && GameState.playing && GameState.inCursorMenu)
                TakeClock(0f);
        }

        /// <summary>Temporarily releases a solo Escape-menu pause so the game can save before joining,
        /// while keeping movement suppressed until the save completes or aborts.</summary>
        internal static void BeginOwnSaveHold()
        {
            _savingOwnWorld = true;
            if (_ownsClock)
            {
                Time.timeScale = _resumeScale > 0f ? _resumeScale : 1f;
                Physics.autoSyncTransforms = true;
                _ownsClock = false;
            }
        }

        /// <summary>Ends the pre-join save hold, restoring the solo Escape-menu pause if the world stays open.</summary>
        internal static void EndOwnSaveHold(bool restorePause)
        {
            if (!_savingOwnWorld) return;
            _savingOwnWorld = false;
            if (restorePause && _openMenu != null && !_ownsClock && !InSession &&
                GameState.playing && GameState.inCursorMenu)
                TakeClock(0f);
        }

        /// <summary>Clears static Escape-menu pause state across a full scene reload.</summary>
        internal static void Reset()
        {
            _openMenu = null;
            _ownsClock = false;
            _savingOwnWorld = false;
            _resumeScale = 1f;
            _resumeAutoSync = true;
            Physics.autoSyncTransforms = true;
        }

        private static void TakeClock(float scale)
        {
            _resumeScale = Time.timeScale;
            _resumeAutoSync = Physics.autoSyncTransforms;
            _ownsClock = true;
            Time.timeScale = scale;
            Physics.autoSyncTransforms = false;
        }

        internal static bool HasOpenWorldMenu(StartMenu menu) => _openMenu != null && _openMenu == menu;
    }
}
