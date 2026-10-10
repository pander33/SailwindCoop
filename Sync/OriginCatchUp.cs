using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Brings the floating origin back under the player after a jump.
    ///
    /// In play <see cref="FloatingOriginManager"/> shifts one <c>shiftDistance</c> step at a time,
    /// each behind a two-second wake fade. That keeps up with a sailing boat, but after a teleport
    /// (the debug panel, "Teleport to boat", the host's boat arriving somewhere else on a client)
    /// the camera stays tens of thousands of units from zero for minutes, and at that distance
    /// float precision makes the whole picture shake and the shadows jump. The game's own teleports
    /// (recovery, <c>DebugBoatTeleporter</c>) avoid it by raising <c>GameState.recovering</c>,
    /// which switches the manager to instant shifting; that flag also drives sleep, needs, the
    /// embark triggers and autosave, so here only the shifting itself is done: the manager's own
    /// instant branch, in one <c>Shift</c> call for the whole distance.
    ///
    /// The prefix of the manager's <c>Update</c> acts when the shifter is further from zero than
    /// sailing ever takes it, so no teleport site has to ask for it, and the manager then finds
    /// the shifter in range and starts no smooth shift of its own. It acts without a session
    /// too: the debug panel and "Teleport to boat" work offline.
    /// </summary>
    public static class OriginCatchUp
    {
        // One smooth shift is pending for about two seconds; a sailing boat does not cover a
        // second step in that time.
        private const float TriggerSteps = 2f;

        private static MethodInfo _mShift;
        private static FloatingOriginManager _stuck;
        private static Vector3 _lastReal;
        private static int _lastRealFrame = -1;

        public static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(FloatingOriginManager), "Update", Type.EmptyTypes, target =>
            {
                _mShift = AccessTools.Method(typeof(FloatingOriginManager), "Shift", new[] { typeof(int), typeof(int) });
                if (_mShift == null) throw new MissingMethodException("FloatingOriginManager", "Shift");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(OriginCatchUp), nameof(PreUpdate)));
            });
            PatchHealth.Report("Origin catch-up", hooks);
            Plugin.Logger.LogInfo("[OriginCatchUp] " + hooks.Detail);
        }

        private static void Report(Exception error)
        {
            Plugin.Logger.LogWarning("[OriginCatchUp] " + error);
            PatchHealth.Set("Origin catch-up", PatchHealthState.Failed, error.Message);
            _mShift = null;
        }

        private static bool PreUpdate(FloatingOriginManager __instance, bool ___shifting, bool ___shiftingSmoothly,
                                      ref bool ___instantShifting)
        {
            // This runs every frame of the game: a plain try instead of PatchGuard, whose lambda
            // would allocate each time. A fault is reported the same way.
            bool instant = ___instantShifting;
            try
            {
                // A jump is still going on: were the manager to run now, it would start a smooth
                // shift of one step, and the catch-up would have to wait several seconds for it.
                if (Plan(__instance, ___shifting || ___shiftingSmoothly, out int x, out int z)) return false;
                if (x == 0 && z == 0) return true;

                // NewShift reads the flag only in the part that runs inside this call.
                ___instantShifting = true;
                Shift(__instance, x, z);
            }
            catch (Exception error) { Report(error); }
            ___instantShifting = instant;
            return true;
        }

        /// <summary>The shift to make now, in steps; true when the manager must skip this frame.</summary>
        private static bool Plan(FloatingOriginManager m, bool shiftingNow, out int x, out int z)
        {
            x = z = 0;
            if (_mShift == null || m == _stuck) return false;
            Transform shifter = m.shifterObject;
            float step = m.shiftDistance;
            if (shifter == null || step <= 0f) return false;

            // A jump is over when the shifter has stopped covering whole steps in a frame: a
            // client's boat is interpolated to the new place over several frames, and may
            // overshoot it when the next packet is late.
            Vector3 p = shifter.position;
            Vector3 real = m.ShiftingPosToRealPos(p);
            bool settled = _lastRealFrame == Time.frameCount - 1 && (real - _lastReal).sqrMagnitude < step * step;
            _lastReal = real;
            _lastRealFrame = Time.frameCount;

            // In these states the manager does not shift at all, or shifts instantly by itself.
            if (!GameState.playing || GameState.currentlyLoading || GameState.justStarted || GameState.recovering) return false;
            // A smooth shift is on its way: its coroutine would still shift after ours, and its
            // rigidbodies are prepared for a shift that has not happened yet. Wait for it.
            if (shiftingNow) return false;
            if (Mathf.Abs(p.x) <= step * TriggerSteps && Mathf.Abs(p.z) <= step * TriggerSteps) return false;
            if (!settled) return true;

            // Not "the nearest step to zero". The host's wave phases fit a client only at the same
            // origin offset, and a client that joins later gets its offset from the game's own
            // loop on load, which starts at zero and stops at the first step within range. End
            // on that very offset, wherever this machine's origin was before.
            Vector3 offset = m.outCurrentOffset;
            x = LoadedOffsetSteps(real.x, step) - Mathf.RoundToInt(offset.x / step);
            z = LoadedOffsetSteps(real.z, step) - Mathf.RoundToInt(offset.z / step);
            return false;
        }

        /// <summary>
        /// The origin offset along one axis, in steps, that the game's instant loop reaches from
        /// zero for a shifter at real coordinate <paramref name="real"/>: it shifts while the
        /// shifter is further than one step away.
        /// </summary>
        private static int LoadedOffsetSteps(float real, float step)
        {
            if (real > step) return -(Mathf.CeilToInt(real / step) - 1);
            if (real < -step) return Mathf.CeilToInt(-real / step) - 1;
            return 0;
        }

        private static void Shift(FloatingOriginManager m, int x, int z)
        {
            Transform shifter = m.shifterObject;
            Vector3 before = shifter.position;
            _mShift.Invoke(m, new object[] { x, z });

            if (shifter.position == before)
            {
                // The shifter is not under the shifting world: the world moved and it did not.
                _stuck = m;
                Plugin.Logger.LogWarning("[OriginCatchUp] the shift did not move '" + shifter.name + "', catch-up stopped for this world");
                return;
            }
            Plugin.Logger.LogInfo("[OriginCatchUp] instant shift " + x + ", " + z + " steps, offset " + m.outCurrentOffset);

            // A host with clients stops its world for a moment. The boat's new place goes out while
            // nothing moves, each client makes this same shift when its boat arrives, and then the
            // wave phases follow (Crest has just folded the whole distance into them).
            var coop = CoopBehaviour.Instance;
            if (coop == null || coop.Net == null || coop.Net.State != LinkState.Connected ||
                coop.Net.Role != Role.Host || coop.Net.PeerCount == 0) return;
            coop.CrestWater?.OnHostOriginJump();
            if (coop.SavingForJoin) return;
            // The hold switches Physics.autoSyncTransforms off, and every collider has just moved.
            Physics.SyncTransforms();
            coop.Pause?.HoldToSettle(SettleSec);
        }

        /// <summary>How long the host's world stands still after its teleport. Longer than
        /// <c>CrestWaterSync.JumpResendSec</c>: the phases go out inside the hold.</summary>
        private const float SettleSec = 1.5f;
    }
}
