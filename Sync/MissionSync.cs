using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P4.3 — shared mission journal + reward-to-both (separate-money model). The mission log is
    /// host-authoritative: the host broadcasts <c>PlayerMissions.missions</c> as a read-only mirror so
    /// the client's mission UI shows the same active missions. Accepting/abandoning stays on the host
    /// (the client's mission buttons remain HostOnly; the mission goods spawn host-side and replicate via
    /// ItemSync). Delivery is collision-triggered at the destination port and only fires on the host (the
    /// client's goods are collider-less puppets), so the host pays its own wallet via vanilla and forwards
    /// the same amount to the client's wallet — the reward is duplicated to both players. Slots: 0..4.
    /// </summary>
    public sealed class MissionSync
    {
        public static MissionSync Instance { get; private set; }

        private const int Slots = 5;

        private readonly CoopNet _net;
        private string _lastSig = null;     // host: last broadcast journal signature
        private string _lastApplied = null; // client: last applied journal signature
        private float _heartbeat;

        /// <summary>Resend the journal this often so a freshly-joined client gets the current missions.</summary>
        public float HeartbeatSeconds = 4f;

        public string MissionText { get; private set; } = "—";

        public MissionSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            _heartbeat += dt;
            string sig = BuildSignature(out var entries);
            bool beat = _heartbeat >= HeartbeatSeconds;
            if (sig == _lastSig && !beat) return;
            _heartbeat = 0f;
            _lastSig = sig;
            _net.Broadcast(new MissionJournalMsg { Missions = entries }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            MissionText = entries.Length + " миссий (хост)";
        }

        // -----------------------------------------------------------------
        // Host: build the wire journal + a cheap change signature.
        // -----------------------------------------------------------------

        private string BuildSignature(out MissionEntry[] entries)
        {
            var list = new System.Collections.Generic.List<MissionEntry>(Slots);
            var sb = new StringBuilder();
            var missions = PlayerMissions.missions;
            if (missions != null)
            {
                for (int i = 0; i < missions.Length && i < Slots; i++)
                {
                    var m = missions[i];
                    if (m == null) continue;
                    SaveMissionData d;
                    try { d = m.PrepareSaveData(); }
                    catch { continue; }
                    list.Add(new MissionEntry
                    {
                        MissionIndex = (byte)d.missionIndex,
                        OriginPort = d.originPort,
                        DestinationPort = d.destinationPort,
                        GoodPrefabIndex = d.goodPrefabIndex,
                        GoodCount = d.goodCount,
                        TotalPrice = d.totalPrice,
                        InsuranceLevel = d.insuranceLevel,
                        Distance = d.distance,
                        DeliveredGoods = d.deliveredGoods,
                        DueDay = d.dueDay,
                    });
                    sb.Append(d.missionIndex).Append(':').Append(d.deliveredGoods).Append(':').Append(d.dueDay).Append('|');
                }
            }
            entries = list.ToArray();
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // Client: mirror the journal + receive reward.
        // -----------------------------------------------------------------

        public void OnMissionJournal(MissionJournalMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            var sb = new StringBuilder();
            foreach (var e in msg.Missions)
                sb.Append(e.MissionIndex).Append(':').Append(e.DeliveredGoods).Append(':').Append(e.DueDay).Append('|');
            string sig = sb.ToString();
            if (sig == _lastApplied) return;
            _lastApplied = sig;
            ApplyJournal(msg.Missions);
            MissionText = msg.Missions.Length + " миссий (зеркало)";
        }

        private void ApplyJournal(MissionEntry[] entries)
        {
            try
            {
                if (PlayerMissions.missions == null || PlayerMissions.missions.Length < Slots)
                    PlayerMissions.missions = new Mission[Slots];
                var missions = PlayerMissions.missions;
                for (int i = 0; i < missions.Length; i++) missions[i] = null;

                foreach (var e in entries)
                {
                    if (e.MissionIndex >= missions.Length) continue;
                    try
                    {
                        var data = new SaveMissionData((int)e.MissionIndex, e.OriginPort, e.DestinationPort,
                            e.GoodPrefabIndex, e.GoodCount, e.TotalPrice, e.InsuranceLevel, e.Distance,
                            e.DeliveredGoods, e.DueDay);
                        missions[e.MissionIndex] = new Mission(data);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] миссия " + e.MissionIndex + ": " + ex.Message); }
                }
                Plugin.Logger.LogInfo("[MissionSync] журнал зеркалирован: " + entries.Length + " миссий");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] ApplyJournal: " + ex.Message); }
        }

        // -----------------------------------------------------------------
        // Accept / abandon — host-authoritative (mediated). The mission OFFER list is generated per
        // machine (depends on personal reputation/prices), so a client sends the FULL mission spec and
        // the host accepts THAT, not an offer-list index. Goods spawn host-side and replicate via ItemSync.
        // -----------------------------------------------------------------

        /// <summary>Client: forward an accept to the host instead of running it locally. Returns true if forwarded.</summary>
        public bool RequestAccept(Mission mission)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || mission == null) return false;
            SaveMissionData d;
            try { d = mission.PrepareSaveData(); }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionSync] PrepareSaveData: " + e.Message); return false; }
            _net.Broadcast(new MissionAcceptMsg
            {
                Mission = new MissionEntry
                {
                    OriginPort = d.originPort,
                    DestinationPort = d.destinationPort,
                    GoodPrefabIndex = d.goodPrefabIndex,
                    GoodCount = d.goodCount,
                    TotalPrice = d.totalPrice,
                    InsuranceLevel = d.insuranceLevel,
                    Distance = d.distance,
                    DueDay = d.dueDay,
                },
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] исх accept dest=" + d.destinationPort + " good=" + d.goodPrefabIndex);
            return true;
        }

        /// <summary>Client: forward an abandon (by shared journal slot). Returns true if forwarded.</summary>
        public bool RequestAbandon(int missionIndex)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return false;
            if (missionIndex < 0 || missionIndex >= Slots) return false;
            _net.Broadcast(new MissionAbandonMsg { MissionIndex = (byte)missionIndex }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] исх abandon slot=" + missionIndex);
            return true;
        }

        public void OnMissionAccept(MissionAcceptMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            try
            {
                var e = msg.Mission;
                var data = new SaveMissionData(-1, e.OriginPort, e.DestinationPort, e.GoodPrefabIndex,
                    e.GoodCount, e.TotalPrice, e.InsuranceLevel, e.Distance, 0, e.DueDay);
                var mission = new Mission(data);
                PlayerMissions.AcceptMission(mission);   // host vanilla: assigns slot, spawns goods, reduces demand
                _heartbeat = HeartbeatSeconds;           // force a journal resend next Tick
                Plugin.Logger.LogInfo("[MissionSync] вх accept dest=" + e.DestinationPort + " good=" + e.GoodPrefabIndex);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] OnMissionAccept: " + ex.Message); }
        }

        public void OnMissionAbandon(MissionAbandonMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            try
            {
                int idx = msg.MissionIndex;
                if (PlayerMissions.missions != null && idx >= 0 && idx < PlayerMissions.missions.Length && PlayerMissions.missions[idx] != null)
                {
                    PlayerMissions.AbandonMission(idx);
                    _heartbeat = HeartbeatSeconds;
                    Plugin.Logger.LogInfo("[MissionSync] вх abandon slot=" + idx);
                }
                else Plugin.Logger.LogInfo("[MissionSync] вх abandon: пустой слот " + idx);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] OnMissionAbandon: " + ex.Message); }
        }

        /// <summary>Host: a delivery just paid out — forward the same amount to the client's own wallet.</summary>
        public void ForwardReward(int region, int amount)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            if (amount <= 0 || region < 0) return;
            _net.Broadcast(new MissionRewardMsg { Region = region, Amount = amount }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] исх reward region=" + region + " amount=" + amount);
        }

        public void OnMissionReward(MissionRewardMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            try
            {
                if (PlayerGold.currency != null && msg.Region >= 0 && msg.Region < PlayerGold.currency.Length && msg.Amount > 0)
                {
                    PlayerGold.currency[msg.Region] += msg.Amount;
                    try { MoneyNotification.instance?.PlayNotif(msg.Amount, msg.Region); } catch { }
                    Plugin.Logger.LogInfo("[MissionSync] вх reward +" + msg.Amount + " region=" + msg.Region);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] OnMissionReward: " + ex.Message); }
        }

        public void Clear()
        {
            _lastSig = null;
            _lastApplied = null;
            _heartbeat = 0f;
            MissionText = "—";
        }
    }

    /// <summary>
    /// Harmony bridge: forward a mission delivery payout to the client. <c>Mission.DeliverGood</c> credits
    /// <c>PlayerGold.currency[destinationPort.region]</c>; we capture the wallet delta around it and send
    /// it. Host-gated inside <see cref="MissionSync.ForwardReward"/>.
    /// </summary>
    public static class MissionPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool deliver = TryPatch(harmony, "DeliverGood", nameof(PreDeliver), nameof(PostDeliver));
            // Accept/abandon are host-authoritative: a connected client forwards the request and skips the
            // local effect (which would spawn phantom goods and diverge the journal).
            bool accept = TryPatchStatic(harmony, "AcceptMission", new[] { AccessTools.TypeByName("Mission") }, nameof(PreAcceptMission));
            bool abandon = TryPatchStatic(harmony, "AbandonMission", new[] { typeof(int) }, nameof(PreAbandonMission));
            Plugin.Logger.LogInfo("[MissionPatches] Патч миссий: DeliverGood=" + deliver + ", Accept=" + accept + ", Abandon=" + abandon);
        }

        private static bool TryPatch(Harmony harmony, string method, string prefixName, string postfixName)
        {
            try
            {
                var t = AccessTools.TypeByName("Mission");
                if (t == null) return false;
                var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi == null) return false;
                var prefix = new HarmonyMethod(typeof(MissionPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = new HarmonyMethod(typeof(MissionPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] " + method + ": " + e.Message); return false; }
        }

        private static bool TryPatchStatic(Harmony harmony, string method, Type[] args, string prefixName)
        {
            try
            {
                var t = AccessTools.TypeByName("PlayerMissions");
                if (t == null || args == null || args[0] == null) return false;
                var mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, args, null);
                if (mi == null) return false;
                var prefix = new HarmonyMethod(typeof(MissionPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] " + method + ": " + e.Message); return false; }
        }

        // Return false to skip the local accept/abandon (forwarded to host). Host/offline runs vanilla.
        private static bool PreAcceptMission(Mission mission)
        {
            try { var s = MissionSync.Instance; if (s != null && s.RequestAccept(mission)) return false; }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PreAcceptMission: " + e.Message); }
            return true;
        }

        private static bool PreAbandonMission(int missionIndex)
        {
            try { var s = MissionSync.Instance; if (s != null && s.RequestAbandon(missionIndex)) return false; }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PreAbandonMission: " + e.Message); }
            return true;
        }

        // Capture region + wallet before, so the postfix can forward the delta the payout added.
        private static void PreDeliver(Mission __instance, out int[] __state)
        {
            __state = null;
            try
            {
                if (__instance == null || __instance.destinationPort == null) return;
                int region = (int)__instance.destinationPort.region;
                if (PlayerGold.currency == null || region < 0 || region >= PlayerGold.currency.Length) return;
                __state = new[] { region, PlayerGold.currency[region] };
            }
            catch { __state = null; }
        }

        private static void PostDeliver(int[] __state)
        {
            try
            {
                if (__state == null) return;
                int region = __state[0];
                int delta = PlayerGold.currency[region] - __state[1];
                if (delta > 0) MissionSync.Instance?.ForwardReward(region, delta);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PostDeliver: " + e.Message); }
        }
    }
}
