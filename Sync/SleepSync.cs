using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P4.2 — shared sleep / time-skip. Sleeping warps <c>Time.timeScale</c> locally
    /// (<c>PlayerSleep.FallAsleep</c> → <c>StartSleepTimeWarp</c>); time itself is host-authoritative
    /// (EnvironmentSync re-anchors the sun each snapshot), so the client must NOT warp its own clock.
    /// Model (safe v1): only the HOST initiates. When the host falls asleep we broadcast it; the client
    /// mirrors the blackout and locks its controls but keeps <c>Time.timeScale = 1</c> — the host's
    /// advanced time arrives normally via EnvironmentSync. When the host wakes, the client fades back in.
    /// A safety timer restores the client if a wake message is ever lost (no soft-lock). Client-initiated
    /// sleep with a consent prompt is a later refinement; for now the guest's sleep buttons stay HostOnly.
    /// </summary>
    public sealed class SleepSync
    {
        public static SleepSync Instance { get; private set; }

        private readonly CoopNet _net;
        private bool _clientAsleep;
        private float _safetyTimer;

        /// <summary>Safety cap: if a wake message is lost, restore the client after this long.</summary>
        public const float MaxBlackoutSeconds = 45f;
        public const float FadeSeconds = 2.5f;

        public string SleepText => _net.Role == Role.Client ? (_clientAsleep ? "сон (ведомый)" : "—")
                                                            : (_net.Role == Role.Host ? "хост" : "—");

        public SleepSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>Host: our local sleep state changed (FallAsleep/WakeUp). Tell the clients.</summary>
        public void BroadcastSleep(bool sleeping)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            _net.Broadcast(new SleepStateMsg { Sleeping = sleeping }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[SleepSync] исх sleep=" + sleeping);
        }

        public void OnSleepState(SleepStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            Plugin.Logger.LogInfo("[SleepSync] вх sleep=" + msg.Sleeping);
            SetClientAsleep(msg.Sleeping);
        }

        public void Tick(float dt)
        {
            if (_net.Role != Role.Client || !_clientAsleep) return;
            _safetyTimer += dt;
            if (_safetyTimer > MaxBlackoutSeconds)
            {
                Plugin.Logger.LogWarning("[SleepSync] таймаут blackout — восстанавливаю управление");
                SetClientAsleep(false);
            }
        }

        private void SetClientAsleep(bool sleeping)
        {
            if (sleeping == _clientAsleep) return;
            _clientAsleep = sleeping;
            _safetyTimer = 0f;
            try
            {
                var beh = CoopBehaviour.Instance;
                if (beh != null) beh.StartCoroutine(Blackout.FadeTo(sleeping ? 1f : 0f, FadeSeconds));
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SleepSync] fade: " + e.Message); }
            try { Refs.SetPlayerControl(!sleeping); }
            catch (Exception e) { Plugin.Logger.LogWarning("[SleepSync] control: " + e.Message); }
        }

        public void Clear()
        {
            if (_clientAsleep)
            {
                try { var beh = CoopBehaviour.Instance; if (beh != null) beh.StartCoroutine(Blackout.FadeTo(0f, 0.5f)); } catch { }
                try { Refs.SetPlayerControl(true); } catch { }
            }
            _clientAsleep = false;
            _safetyTimer = 0f;
        }
    }

    /// <summary>
    /// Harmony bridge: the host broadcasts its own sleep transitions. Postfix on
    /// <c>PlayerSleep.FallAsleep</c> (→ sleeping) and <c>PlayerSleep.WakeUp</c> (→ awake). Host-gated
    /// inside <see cref="SleepSync.BroadcastSleep"/>, so a client's own (blocked) sleep never echoes.
    /// </summary>
    public static class SleepPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool fall = TryPatch(harmony, "FallAsleep", nameof(PostFallAsleep));
            bool wake = TryPatch(harmony, "WakeUp", nameof(PostWakeUp));
            Plugin.Logger.LogInfo("[SleepPatches] Патчи сна: FallAsleep=" + fall + ", WakeUp=" + wake);
        }

        private static bool TryPatch(Harmony harmony, string method, string postfixName)
        {
            try
            {
                var t = AccessTools.TypeByName("PlayerSleep");
                if (t == null) return false;
                var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi == null) return false;
                var postfix = new HarmonyMethod(typeof(SleepPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SleepPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static void PostFallAsleep()
        {
            try { SleepSync.Instance?.BroadcastSleep(true); }
            catch (Exception e) { Plugin.Logger.LogWarning("[SleepPatches] PostFallAsleep: " + e.Message); }
        }

        private static void PostWakeUp()
        {
            try { SleepSync.Instance?.BroadcastSleep(false); }
            catch (Exception e) { Plugin.Logger.LogWarning("[SleepPatches] PostWakeUp: " + e.Message); }
        }
    }
}
