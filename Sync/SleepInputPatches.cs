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
            Install(harmony, hooks, typeof(global::Sleep), "FallAsleep", Type.EmptyTypes, nameof(PreFallAsleep), null);
            Install(harmony, hooks, typeof(global::Sleep), "WakeUp", Type.EmptyTypes, nameof(PreWakeUp), null);
            Install(harmony, hooks, typeof(global::Sleep), "LeaveBed", Type.EmptyTypes, nameof(PreLeaveBed), nameof(PostLeaveBed));
            Install(harmony, hooks, typeof(global::Sleep), "Update", Type.EmptyTypes, nameof(PreSleepLoop), nameof(PostSleepLoop));
            hooks.Install(typeof(Tavern), "ClickSleepButton", Type.EmptyTypes, target => harmony.Patch(target,
                prefix: Callback(nameof(PreTavern)), postfix: Callback(nameof(PostInput)), finalizer: Callback(nameof(FinishInput))));
            Install(harmony, hooks, typeof(GPButtonOnsenEntrance), "OnActivate", Type.EmptyTypes, nameof(PreEntrance), nameof(PostEntrance));
            Install(harmony, hooks, typeof(PlayerNeeds), "LateUpdate", Type.EmptyTypes, nameof(PreNeeds), nameof(PostNeeds));
            hooks.Install(typeof(PlayerNeedsUI), "PlayWarning", new[] { typeof(Transform), typeof(bool) }, target => harmony.Patch(target,
                prefix: Callback(nameof(PreNeedsWarning)), finalizer: Callback(nameof(EndNeedsWarning))));
            Install(harmony, hooks, typeof(BoatDamage), "Impact", new[] { typeof(Collider), typeof(float) }, nameof(PreBoatHarm), nameof(PostImpact));
            Install(harmony, hooks, typeof(BoatDamage), "Overflow", new[] { typeof(float) }, nameof(PreBoatHarm), nameof(PostOverflow));
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
        private static void PreNeeds(PlayerNeeds __instance, out SleepSync.NeedsSnapshot __state)
        {
            __state = default(SleepSync.NeedsSnapshot);
            try
            {
                if (Active && SleepSync.Instance.AdjustsNeeds && !__instance.godMode && GameState.playing &&
                    !GameState.recovering && GameState.currentShipyard == null &&
                    !(EconomyUI.instance.uiActive && !Debugger.buildDebugModeOn) && !__instance.applyOverride)
                    __state = new SleepSync.NeedsSnapshot
                    {
                        Captured = true, Sleep = PlayerNeeds.sleep, Debt = PlayerNeeds.sleepDebt,
                        Food = PlayerNeeds.food, FoodDebt = PlayerNeeds.foodDebt, Water = PlayerNeeds.water,
                        Protein = PlayerNeeds.protein, Vitamins = PlayerNeeds.vitamins
                    };
            }
            catch (Exception error) { Warn(nameof(PreNeeds), error); }
        }

        private static void PostNeeds(SleepSync.NeedsSnapshot __state)
        {
            try { if (__state.Captured && Active) SleepSync.Instance.AdjustNeeds(__state); }
            catch (Exception error) { Warn(nameof(PostNeeds), error); }
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
                if (GameState.eyesFullyClosed) SleepSync.Instance.Wake(_inSleepLoop, _inNeedsWarning);
            }
            catch (Exception error) { Warn(nameof(PreWakeUp), error); }
            return false;
        }

        /// <summary>A rested player lying in a shared sleep is not asleep for the game, so any key
        /// would take him out of bed with the screen still dark.</summary>
        private static bool PreLeaveBed()
        {
            try { if (Active && SleepSync.Instance.HoldsBed) return false; }
            catch (Exception error) { Warn(nameof(PreLeaveBed), error); }
            return true;
        }

        private static void PostLeaveBed()
        {
            try { if (Active && GameState.inBed == null) SleepSync.Instance.LeftBed(); }
            catch (Exception error) { Warn(nameof(PostLeaveBed), error); }
        }

        // Sleep.Update wakes the player when he has slept enough; every other WakeUp caller is an
        // emergency (collision, running aground, water coming in). SleepSync tells them apart by this.
        private static bool _inSleepLoop;
        private static void PreSleepLoop() { _inSleepLoop = true; }
        private static void PostSleepLoop() { _inSleepLoop = false; }

        // PlayerNeedsUI.PlayWarning wakes the player whose own hunger or thirst is low. That is his
        // wake-up alone, not the crew's.
        private static bool _inNeedsWarning;
        private static void PreNeedsWarning() { _inNeedsWarning = true; }
        private static Exception EndNeedsWarning(Exception __exception) { _inNeedsWarning = false; return __exception; }

        // The game wakes its own sleeper from a hit that did damage and from water coming over the
        // side. A player asleep alone is not asleep for the game, so the host passes these on.
        private static void PreBoatHarm(BoatDamage __instance, out Vector2 __state)
        {
            __state = Vector2.zero;
            try { if (Active) __state = new Vector2(__instance.hullDamage, __instance.waterLevel); }
            catch (Exception error) { Warn(nameof(PreBoatHarm), error); }
        }

        private static void PostImpact(BoatDamage __instance, Collider __0, float __1, Vector2 __state)
        {
            try
            {
                if (!Active) return;
                if (__instance.hullDamage > __state.x) SleepSync.Instance.BoatAlarm(__instance.transform);
                // The game calls this only for a hard hit of the local player's boat, and wakes the
                // sleeper right after. What threw the boat is the question this line answers.
                if (SleepSync.Instance.HoldsBed)
                {
                    var body = __instance.GetComponent<Rigidbody>();
                    Plugin.Logger.LogWarning("[SleepPatches] hit in shared sleep: boat=" + __instance.name +
                        " against=" + (__0 == null ? "?" : __0.name + "/" + __0.transform.root.name) +
                        " relative=" + __1.ToString("F1") + " speed=" + (body == null ? "?" : body.velocity.magnitude.ToString("F1")) +
                        " spin=" + (body == null ? "?" : body.angularVelocity.magnitude.ToString("F2")) +
                        " timeScale=" + Time.timeScale + " fixedStep=" + Time.fixedDeltaTime.ToString("F4") +
                        " phaseAge=" + SleepSync.Instance.PhaseAge.ToString("F1") + " eyesClosed=" + GameState.eyesFullyClosed +
                        " role=" + CoopBehaviour.Instance?.Net?.Role);
                }
            }
            catch (Exception error) { Warn(nameof(PostImpact), error); }
        }

        private static void PostOverflow(BoatDamage __instance, Vector2 __state)
        {
            try
            {
                if (Active && __instance.waterLevel > __state.y && __instance.waterLevel > 0.1f)
                    SleepSync.Instance.BoatAlarm(__instance.transform);
            }
            catch (Exception error) { Warn(nameof(PostOverflow), error); }
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
