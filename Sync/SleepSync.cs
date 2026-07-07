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
    /// (<c>Sleep.FallAsleep</c> → <c>StartSleepTimeWarp</c>); time itself is host-authoritative
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

        public string SleepText
        {
            get
            {
                if (_net.Role == Role.Host) return "host";
                if (_net.Role != Role.Client) return "—";
                if (!_clientAsleep) return "—";
                try { return "sleep (slaved), energy " + PlayerNeeds.sleep.ToString("0") + "%"; }
                catch { return "sleep (slaved)"; }
            }
        }

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
            Plugin.Logger.LogInfo("[SleepSync] out sleep=" + sleeping);
        }

        public void OnSleepState(SleepStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            Plugin.Logger.LogInfo("[SleepSync] in sleep=" + msg.Sleeping);
            SetClientAsleep(msg.Sleeping);
        }

        public void Tick(float dt)
        {
            if (_net.Role != Role.Client || !_clientAsleep) return;
            _safetyTimer += dt;
            if (_safetyTimer > MaxBlackoutSeconds)
            {
                Plugin.Logger.LogWarning("[SleepSync] blackout timeout - restoring control");
                SetClientAsleep(false);
                return;
            }
            RestoreNeeds(dt);
        }

        /// <summary>
        /// Восстановление сил во время ведомого сна. У клиента <c>GameState.sleeping == false</c>
        /// (свой timeScale мы сознательно не варпим), поэтому ванильный
        /// <c>PlayerNeeds.LateUpdate</c> продолжает ТРАТИТЬ сон, а не копить. Начисляем ванильную
        /// скорость восстановления вручную, умноженную на timeScale хоста (16 при тайм-варпе,
        /// приходит в EnvState — см. <see cref="EnvironmentSync.HostTimeScale"/>): хост
        /// восстанавливается в ускоренном времени, клиент должен успеть столько же за реальное.
        /// </summary>
        private void RestoreNeeds(float dt)
        {
            try
            {
                if (PlayerNeeds.instance == null || Sun.sun == null) return;

                float hostTs = 1f;
                var env = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Env : null;
                if (env != null && env.WaveClockValid) hostTs = Mathf.Max(1f, env.HostTimeScale);

                // Та же формула, что ванильная ветка GameState.sleeping в PlayerNeeds.LateUpdate:
                // сначала гасится sleepDebt, на сон идёт лишь 20 % — пока долг не закрыт.
                float num = dt * 8f * Sun.sun.timescale * hostTs;
                if (PlayerNeeds.sleepDebt < 100f)
                {
                    PlayerNeeds.sleepDebt = Mathf.Min(100f, PlayerNeeds.sleepDebt + num);
                    num *= 0.2f;
                }
                // Плюс компенсация ванильного расхода сна, который у бодрствующего (по мнению
                // движка) клиента не останавливается.
                num += dt * Sun.sun.timescale * (5f + 15f * (PlayerNeeds.alcohol / 100f));
                PlayerNeeds.sleep = Mathf.Min(100f, PlayerNeeds.sleep + num);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SleepSync] restore: " + e.Message); }
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
    /// <c>Sleep.FallAsleep</c> (→ sleeping) and <c>Sleep.WakeUp</c> (→ awake). Host-gated
    /// inside <see cref="SleepSync.BroadcastSleep"/>, so a client's own (blocked) sleep never echoes.
    /// Движковый класс называется <c>Sleep</c> — прежний резолв по имени "PlayerSleep" находил null,
    /// и патчи молча не ставились (совместный сон не работал вовсе).
    /// </summary>
    public static class SleepPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool fall = TryPatch(harmony, "FallAsleep", nameof(PostFallAsleep));
            bool wake = TryPatch(harmony, "WakeUp", nameof(PostWakeUp));
            Plugin.Logger.LogInfo("[SleepPatches] Sleep patches: FallAsleep=" + fall + ", WakeUp=" + wake);
            SailwindCoop.Runtime.PatchHealth.Report("Sleep", (fall ? 1 : 0) + (wake ? 1 : 0), 2);
        }

        private static bool TryPatch(Harmony harmony, string method, string postfixName)
        {
            try
            {
                var t = typeof(Sleep);
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
