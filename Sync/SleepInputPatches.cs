using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public static class SleepPatches
    {
        private static SleepAddress _input;

        public static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            Install(harmony, hooks, typeof(global::Sleep), "EnterBed", new[] { typeof(Transform) }, null, nameof(PostEnterBed));
            Install(harmony, hooks, typeof(global::Sleep), "FallAsleep", Type.EmptyTypes, nameof(PreFallAsleep), null);
            Install(harmony, hooks, typeof(global::Sleep), "WakeUp", Type.EmptyTypes, nameof(PreWakeUp), null);
            Install(harmony, hooks, typeof(global::Sleep), "LeaveBed", Type.EmptyTypes, nameof(PreLeaveBed), nameof(PostLeaveBed));
            hooks.Install(typeof(Tavern), "ClickSleepButton", Type.EmptyTypes, target => harmony.Patch(target,
                prefix: Callback(nameof(PreTavern)), postfix: Callback(nameof(PostInput)), finalizer: Callback(nameof(FinishInput))));
            Install(harmony, hooks, typeof(GPButtonOnsenEntrance), "OnActivate", Type.EmptyTypes, nameof(PreEntrance), nameof(PostEntrance));
            Install(harmony, hooks, typeof(PlayerNeeds), "LateUpdate", Type.EmptyTypes, nameof(PreNeeds), nameof(PostNeeds));
            hooks.Inspect(typeof(GPButtonBed), "GPButtonBed", "OnActivate", Type.EmptyTypes, null);
            hooks.Inspect(typeof(GPButtonTavernSleep), "GPButtonTavernSleep", "OnActivate", Type.EmptyTypes, null);
            hooks.Inspect(typeof(ShipItemBed), "ShipItemBed", "OnAltActivate", Type.EmptyTypes, null);
            PatchHealth.Report("Sleep", hooks);
            Plugin.Logger.LogInfo("[SleepPatches] " + hooks.Detail);
        }

        private static void Install(Harmony harmony, PatchHookCatalog hooks, Type type, string method,
            Type[] args, string prefix, string postfix)
        {
            hooks.Install(type, method, args, target => harmony.Patch(target,
                prefix: prefix == null ? null : Callback(prefix), postfix: postfix == null ? null : Callback(postfix)));
        }

        private static HarmonyMethod Callback(string name)
            => new HarmonyMethod(typeof(SleepPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        private static bool Active => SleepSync.Instance?.Connected == true && !SleepSync.Instance.Applying;
        private struct NeedsBefore
        {
            internal bool Captured;
            internal float Sleep, Debt;
        }

        private static void PreNeeds(PlayerNeeds __instance, out NeedsBefore __state)
        {
            __state = default(NeedsBefore);
            try
            {
                if (Active && SleepSync.Instance.ClientAsleep && !__instance.godMode && GameState.playing &&
                    !GameState.recovering && GameState.currentShipyard == null &&
                    !(EconomyUI.instance.uiActive && !Debugger.buildDebugModeOn) && !__instance.applyOverride)
                    __state = new NeedsBefore { Captured = true, Sleep = PlayerNeeds.sleep, Debt = PlayerNeeds.sleepDebt };
            }
            catch (Exception error) { Warn(nameof(PreNeeds), error); }
        }

        private static void PostNeeds(NeedsBefore __state)
        {
            try { if (__state.Captured && Active) SleepSync.Instance.RestoreNeeds(__state.Sleep, __state.Debt); }
            catch (Exception error) { Warn(nameof(PostNeeds), error); }
        }

        private static void PostEnterBed(Transform __0)
        {
            try { if (Active && GameState.inBed == __0) SleepSync.Instance.EnteredBed(__0); }
            catch (Exception error) { Warn(nameof(PostEnterBed), error); }
        }

        private static bool PreFallAsleep()
        {
            try
            {
                if (!Active) return true;
                SleepSync.Instance.FallAsleep(_input);
            }
            catch (Exception error) { Warn(nameof(PreFallAsleep), error); }
            return false;
        }

        private static bool PreWakeUp()
        {
            try
            {
                if (!Active) return true;
                if (GameState.eyesFullyClosed) SleepSync.Instance.Wake(false);
            }
            catch (Exception error) { Warn(nameof(PreWakeUp), error); }
            return false;
        }

        private static void PreLeaveBed(out bool __state)
        {
            __state = false;
            try { __state = Active && GameState.inBed != null; }
            catch (Exception error) { Warn(nameof(PreLeaveBed), error); }
        }

        private static void PostLeaveBed(bool __state)
        {
            try { if (__state && Active) SleepSync.Instance.Wake(true); }
            catch (Exception error) { Warn(nameof(PostLeaveBed), error); }
        }

        private static void PreTavern(Tavern __instance)
        {
            try { if (Active) _input = SleepSync.AddressFor(__instance.transform, SleepSource.Tavern); }
            catch (Exception error) { Warn(nameof(PreTavern), error); }
        }

        private static void PostInput()
        {
            try { _input = null; }
            catch (Exception error) { Warn(nameof(PostInput), error); }
        }

        private static Exception FinishInput(Exception __exception)
        {
            try { _input = null; }
            catch (Exception error) { Warn(nameof(FinishInput), error); }
            return __exception;
        }

        private static void PreEntrance(GPButtonOnsenEntrance __instance, out int __state)
        {
            __state = 0;
            try { if (Active) __state = PlayerGold.currency[(int)__instance.currency]; }
            catch (Exception error) { Warn(nameof(PreEntrance), error); }
        }

        private static void PostEntrance(GPButtonOnsenEntrance __instance, int __state)
        {
            try
            {
                if (Active && __instance.cols != null && !__instance.cols.activeSelf &&
                    PlayerGold.currency[(int)__instance.currency] == __state - __instance.price)
                    SleepSync.Instance.EntranceCommitted(__instance);
            }
            catch (Exception error) { Warn(nameof(PostEntrance), error); }
        }

        private static void Warn(string site, Exception error)
            => Plugin.Logger.LogWarning("[SleepPatches] " + site + " role=" + CoopBehaviour.Instance?.Net?.Role + ": " + error);
    }
}
