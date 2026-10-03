using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>The host must simulate an owned boat near a guest even when its own observer is far away.</summary>
    internal static class BoatActivityPatches
    {
        private static CoopLog.Repeat _errors;
        public static void Apply(Harmony harmony)
        {
            var method = typeof(BoatHorizonPerformanceSwitcher).GetMethod("SetHidden", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) { PatchHealth.Report("BoatActivity", 0, 1); return; }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(BoatActivityPatches), nameof(PreSetHidden)));
            PatchHealth.Report("BoatActivity", 1, 1);
        }

        private static void PreSetHidden(BoatHorizon horizon, ref bool newState)
        {
            try
            {
                var runtime = CoopBehaviour.Instance;
                if (runtime == null || runtime.Net.Role != Role.Host || runtime.Net.State != LinkState.Connected ||
                    horizon == null || newState || GameState.currentlyLoading || !GameState.playing) return;
                Transform root = horizon.transform.parent;
                foreach (var boat in BoatLocator.FindBoats())
                {
                    if (boat == null || root == null || !(boat == root || boat.IsChildOf(root) || root.IsChildOf(boat))) continue;
                    if (BoatAuthority.Instance?.AnyActorNear(boat, horizon.fullySunkDistance) ?? false) newState = true;
                    break;
                }
            }
            catch (Exception e)
            {
                try { Plugin.Logger?.ReportError("[BoatActivity] host activation failed", e, ref _errors); } catch { }
            }
        }
    }
}
