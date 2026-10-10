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

            Vector3 offset = m.outCurrentOffset;
            if (Time.unscaledTime < _hostOffsetUntil && IsClient())
            {
                // Where the shifter would stand under the host's offset. Within one step of zero
                // the game's own loop leaves it alone, so the two machines stay on one offset.
                float hx = real.x + _hostX * step, hz = real.z + _hostZ * step;
                if (Mathf.Abs(hx) <= step && Mathf.Abs(hz) <= step)
                {
                    if (!settled) return true;
                    x = _hostX - Mathf.RoundToInt(offset.x / step);
                    z = _hostZ - Mathf.RoundToInt(offset.z / step);
                    _hostOffsetUntil = 0f;
                    Plugin.Logger.LogInfo("[OriginCatchUp] host offset " + _hostX + ", " + _hostZ + " steps taken" +
                                          (x == 0 && z == 0 ? ", already on it" : ": shift " + x + ", " + z));
                    return false;
                }
            }

            if (Mathf.Abs(p.x) <= step * TriggerSteps && Mathf.Abs(p.z) <= step * TriggerSteps) return false;
            if (!settled) return true;

            // Not "the nearest step to zero". The host's wave phases fit a client only at the same
            // origin offset, and a client that joins later gets its offset from the game's own
            // loop on load, which starts at zero and stops at the first step within range. End
            // on that very offset, wherever this machine's origin was before.
            x = LoadedOffsetSteps(real.x, step) - Mathf.RoundToInt(offset.x / step);
            z = LoadedOffsetSteps(real.z, step) - Mathf.RoundToInt(offset.z / step);
            return false;
        }

        // The host's offset after its jump, in steps, and until when it is worth taking.
        private static int _hostX, _hostZ;
        private static float _hostOffsetUntil;
        private const float HostOffsetSec = 10f;

        private static bool IsClient()
        {
            var net = CoopBehaviour.Instance?.Net;
            return net != null && net.State == LinkState.Connected && net.Role == Role.Client;
        }

        /// <summary>The session is over: its host's offset belongs to no later world.</summary>
        internal static void ForgetHostOffset() => _hostOffsetUntil = 0f;

        /// <summary>
        /// Client: the host's offset after its teleport or recovery. Computed here from the own
        /// position, the offset differs from the host's by a step whenever a step line lies
        /// between the two players; a player who came along with the host takes the host's.
        /// It waits for the boat to arrive: until then this player is not where the host is.
        /// </summary>
        public static void OnNotice(GameplayNoticeMsg msg, CoopNet net)
        {
            try
            {
                if (msg == null || msg.Kind != GameplayNoticeKind.OriginOffset || net == null || net.Role != Role.Client) return;
                string[] parts = (msg.Detail ?? "").Split(',');
                // The sign is written the same way on every machine, whatever its regional settings.
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.AllowLeadingSign;
                if (parts.Length != 2 || !int.TryParse(parts[0], style, inv, out int x) ||
                    !int.TryParse(parts[1], style, inv, out int z)) return;
                _hostX = x;
                _hostZ = z;
                _hostOffsetUntil = Time.unscaledTime + HostOffsetSec;
            }
            catch (Exception error) { Plugin.Logger.LogWarning("[OriginCatchUp] host offset: " + error.Message); }
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

        private static void Shift(FloatingOriginManager m, int x, int z, bool settle = true)
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
            if (settle) SettleHost();
        }

        /// <summary>
        /// A host with clients stops its world for a moment. The boat's new place goes out while
        /// nothing moves, each client makes this same shift when its boat arrives, and then the
        /// wave phases follow (Crest has just folded the whole distance into them).
        /// </summary>
        internal static void SettleHost()
        {
            var coop = CoopBehaviour.Instance;
            if (coop == null || coop.Net == null || coop.Net.State != LinkState.Connected ||
                coop.Net.Role != Role.Host || coop.Net.PeerCount == 0) return;
            coop.CrestWater?.OnHostOriginJump();
            var m = FloatingOriginManager.instance;
            if (m != null && m.shiftDistance > 0f)
            {
                Vector3 offset = m.outCurrentOffset;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string steps = Mathf.RoundToInt(offset.x / m.shiftDistance).ToString(inv) + "," +
                               Mathf.RoundToInt(offset.z / m.shiftDistance).ToString(inv);
                coop.Net.BroadcastNotice(GameplayNoticeKind.OriginOffset, 0, steps);
                Plugin.Logger.LogInfo("[OriginCatchUp] host offset sent: " + steps + " steps");
            }
            if (coop.SavingForJoin) return;
            // The hold switches Physics.autoSyncTransforms off, and every collider has just moved.
            Physics.SyncTransforms();
            coop.Pause?.HoldToSettle(SettleSec);
        }

        /// <summary>
        /// After the game's own teleport (its recovery, which shifts the origin itself while
        /// <c>GameState.recovering</c> is set): that loop starts from the old offset and stops at
        /// the first step within range, which need not be the offset a client ends on. Called
        /// while the recovery is still on, so the manager is in its instant mode.
        /// </summary>
        internal static void AlignToLoadedOffset()
        {
            try
            {
                // Outside that mode a shift waits for a physics step, and Shift would take the
                // shifter that has not moved yet for one that never will.
                if (!GameState.recovering) return;
                var m = FloatingOriginManager.instance;
                if (_mShift == null || m == null || m == _stuck) return;
                Transform shifter = m.shifterObject;
                float step = m.shiftDistance;
                if (shifter == null || step <= 0f) return;
                Vector3 real = m.ShiftingPosToRealPos(shifter.position);
                Vector3 offset = m.outCurrentOffset;
                int x = LoadedOffsetSteps(real.x, step) - Mathf.RoundToInt(offset.x / step);
                int z = LoadedOffsetSteps(real.z, step) - Mathf.RoundToInt(offset.z / step);
                // The caller settles the host once, after everything else it has to send.
                if (x != 0 || z != 0) Shift(m, x, z, false);
            }
            catch (Exception error) { Report(error); }
        }

        /// <summary>How long the host's world stands still after its teleport. Longer than
        /// <c>CrestWaterSync.JumpResendSec</c>: the phases go out inside the hold.</summary>
        private const float SettleSec = 1.5f;
    }
}
