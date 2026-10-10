using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The game's recovery (passed out, the Recover button, the world border) in a session. The
    /// boat is the host's: a host's recovery takes it to the port with whoever is aboard, a
    /// client's recovery leaves it alone. The fee is paid by the player who is recovered.
    /// </summary>
    internal static class RecoveryPatches
    {
        private static float _share = -1f;
        private static FieldInfo _boatRecovered;
        // The recovery in progress. The game's two coroutines run inside Run, which stops stepping
        // them once this has moved on: a recovery ended from outside does nothing more.
        private static int _generation;

        public static void Apply(Harmony harmony)
        {
            int patched = 0;
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(Recovery), "RecoverPlayer"),
                prefix: new HarmonyMethod(typeof(RecoveryPatches), nameof(PreRecover)),
                postfix: new HarmonyMethod(typeof(RecoveryPatches), nameof(PostRecover))); patched++; }, Report);
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(Recovery), "DoRecoverPlayer"),
                postfix: new HarmonyMethod(typeof(RecoveryPatches), nameof(PostRecoverSteps))); patched++; }, Report);
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(Recovery), "RecoverBoat"),
                prefix: new HarmonyMethod(typeof(RecoveryPatches), nameof(PreRecoverBoat)),
                postfix: new HarmonyMethod(typeof(RecoveryPatches), nameof(PostRecoverBoat))); patched++; }, Report);
            PatchGuard.Run(() => { harmony.Patch(AccessTools.Method(typeof(DayLog), "LogRecovery"),
                prefix: new HarmonyMethod(typeof(RecoveryPatches), nameof(PreLogFee))); patched++; }, Report);
            _boatRecovered = AccessTools.Field(typeof(Recovery), "boatRecovered");
            if (_boatRecovered != null) patched++;
            else Plugin.Logger.LogWarning("[RecoveryPatches] Recovery.boatRecovered not found: a client's recovery runs the game's boat step on its own copy");
            PatchHealth.Report("Recovery", patched, 5, patched + "/5");
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[RecoveryPatches] " + error.Message);

        private static bool InSession
        {
            get
            {
                var net = CoopBehaviour.Instance?.Net;
                return net != null && net.State == LinkState.Connected;
            }
        }

        private static bool Client => InSession && CoopBehaviour.Instance.Net.Role == Role.Client;

        private static bool HostWithCrew
        {
            get
            {
                var net = CoopBehaviour.Instance?.Net;
                return InSession && net.Role == Role.Host && net.PeerCount > 0;
            }
        }

        /// <summary>
        /// Ends the recovery in progress for good: what is left of the game's coroutines (the fee,
        /// giving control back, clearing the flag) will not run later, over whatever the player is
        /// doing then.
        /// </summary>
        internal static void Cancel() => _generation++;

        // The game works the fee's share out where the player passed out, before it moves him.
        private static void PreRecover()
        {
            _share = -1f;
            // A recovery still under way would share the game's one boat flag with the new one.
            _generation++;
            PatchGuard.Run(() => { if (Client) _share = Recovery.GetRecoveryPercentageCost() * 0.01f; }, Report);
        }

        // The recovery waits for its boat step at the end. A client has none: without this a
        // client whose game finds no boat to recover would wait for ever.
        private static void PostRecover()
            => PatchGuard.Run(() => { if (Client) _boatRecovered?.SetValue(null, true); }, Report);

        private static void PostRecoverSteps(ref IEnumerator __result)
        {
            IEnumerator steps = __result;
            PatchGuard.Run(() => { if (steps != null && InSession) steps = Run(steps, _generation, false); }, Report);
            __result = steps;
        }

        // On a client this would cast off the ropes, reset the anchor and bail the water out of its
        // copy alone, and none of that reaches the host.
        private static bool PreRecoverBoat(ref IEnumerator __result)
        {
            bool skip = false;
            PatchGuard.Run(() => {
                if (!Client || _boatRecovered == null) return;
                _boatRecovered.SetValue(null, true);
                skip = true;
            }, Report);
            if (!skip) return true;
            __result = Nothing();
            return false;
        }

        private static void PostRecoverBoat(ref IEnumerator __result)
        {
            IEnumerator steps = __result;
            PatchGuard.Run(() => { if (steps != null && InSession) steps = Run(steps, _generation, true); }, Report);
            __result = steps;
        }

        /// <summary>Steps one of the game's recovery coroutines while its recovery is the current one.</summary>
        private static IEnumerator Run(IEnumerator steps, int mine, bool boat)
        {
            bool failed = false;
            while (mine == _generation)
            {
                bool more;
                try { more = steps.MoveNext(); }
                catch (Exception error)
                {
                    // The game would stop here and leave the recovery waiting for this step for ever.
                    if (!boat) throw;
                    Plugin.Logger.LogWarning("[RecoveryPatches] the game's boat step failed: " + error);
                    failed = true;
                    break;
                }
                if (!more) break;
                yield return steps.Current;
            }

            if (!boat || mine != _generation) yield break;
            PatchGuard.Run(() => {
                if (failed) _boatRecovered?.SetValue(null, true);
                // The host's boat stands in the port now, with whoever was aboard.
                if (!HostWithCrew) return;
                Plugin.Logger.LogInfo("[Recovery] host boat recovered: settling the world and resending the boats' state");
                OriginCatchUp.AlignToLoadedOffset();
                CoopBehaviour.Instance.ResendBoatState();
                OriginCatchUp.SettleHost();
            }, Report);
        }

        private static void PreLogFee(DayLog __instance, ref int price)
        {
            int paid = price;
            PatchGuard.Run(() => {
                var wallet = WalletSync.Instance;
                if (_share < 0f || !Client || wallet == null || !wallet.Shared || DayLogs.instance == null) return;
                int currency = Array.IndexOf(DayLogs.instance.dayLogs, __instance);
                paid = -wallet.MoveFeeToOwn(currency, -paid, _share);
            }, Report);
            price = paid;
        }

        private static IEnumerator Nothing() { yield break; }
    }
}
