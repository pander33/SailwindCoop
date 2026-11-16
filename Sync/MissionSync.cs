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
    /// ItemSync). Delivery (<c>PortDude.OnTriggerEnter</c>) always runs on the host; a client forwards
    /// the trigger for goods it carries (<see cref="RequestDeliver"/>). Slots: 0..4.
    /// </summary>
    public sealed class MissionSync
    {
        public static MissionSync Instance { get; private set; }

        private const int Slots = 5;

        private readonly CoopNet _net;
        private string _lastSig = null;     // host: last broadcast journal signature
        private const float CheckSeconds = 0.25f;
        private float _checkTimer;

        public string MissionText { get; private set; } = "—";

        public MissionSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            // The signature is a string, a list and an array: built 4 times a second, not every frame.
            _checkTimer += Time.unscaledDeltaTime;   // also while the host's world is held (timeScale 0)
            if (_lastSig != null && _checkTimer < CheckSeconds) return;
            _checkTimer = 0f;
            bool includeMod = EconomyOverhaulCompat.Present && _net.HasModPeer(ModFeatures.EconomyOverhaul);
            string sig = BuildSignature(includeMod, out var entries);
            if (sig == _lastSig) return;
            _lastSig = sig;
            var journal = new MissionJournalMsg { Missions = entries };
            if (includeMod)
            {
                var vanilla = (MissionEntry[])entries.Clone();
                for (int i = 0; i < vanilla.Length; i++)
                {
                    vanilla[i].ModModifiers = 0;
                    vanilla[i].ModFlags = 0;
                    vanilla[i].ModOffered = 0;
                }
                _net.BroadcastForFeature(ModFeatures.EconomyOverhaul, journal, new MissionJournalMsg { Missions = vanilla });
            }
            else _net.Broadcast(journal, LiteNetLib.DeliveryMethod.ReliableOrdered);
            MissionText = entries.Length + " missions (host)";
        }

        /// <summary>Host: the journal for one client whose world has just loaded. The broadcast in
        /// <see cref="Tick"/> is change-only, and the load replaces whatever arrived before it.</summary>
        public void SendBaseline(LiteNetLib.NetPeer peer)
        {
            if (_net.Role != Role.Host || peer == null) return;
            BuildSignature((_net.FeaturesOf(peer) & ModFeatures.EconomyOverhaul) != 0, out var entries);
            peer.Send(new MissionJournalMsg { Missions = entries }, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        // -----------------------------------------------------------------
        // Host: build the wire journal + a cheap change signature.
        // -----------------------------------------------------------------

        private string BuildSignature(bool includeMod, out MissionEntry[] entries)
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
                    if (PostalExpansionCompat.SyncEnabled)
                    {
                        var entry = list[list.Count - 1];
                        PostalExpansionCompat.Describe(m, out entry.PostalKind, out entry.PostalLetterId);
                        list[list.Count - 1] = entry;
                        sb.Append(entry.PostalKind).Append(':').Append(entry.PostalLetterId).Append(':');
                    }
                    if (includeMod && EconomyOverhaulCompat.ReadRecord(d.missionIndex, out int modifiers, out bool world, out int offered))
                    {
                        var withMod = list[list.Count - 1];
                        withMod.ModModifiers = modifiers;
                        withMod.ModFlags = (byte)(1 | (world ? 2 : 0));
                        withMod.ModOffered = offered;
                        list[list.Count - 1] = withMod;
                        sb.Append(modifiers).Append(world ? 'w' : 'p').Append(offered).Append(':');
                    }
                    // Every sent field: a slot refilled between two ticks must not look unchanged.
                    sb.Append(d.missionIndex).Append(':').Append(d.originPort).Append(':').Append(d.destinationPort).Append(':')
                        .Append(d.goodPrefabIndex).Append(':').Append(d.goodCount).Append(':').Append(d.totalPrice).Append(':')
                        .Append(d.insuranceLevel).Append(':').Append(d.distance).Append(':')
                        .Append(d.deliveredGoods).Append(':').Append(d.dueDay).Append('|');
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
            // Always applied: the host sends only a change or the post-load baseline, and a world load
            // may have replaced the local journal since the previous one.
            ApplyJournal(msg.Missions);
            if (PostalExpansionCompat.GuestBoard) PostalExpansionCompat.RefreshGuestBoard();
            MissionText = msg.Missions.Length + " missions (mirror)";
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
                    if (e.PostalKind != PostalExpansionCompat.KindNone && !PostalExpansionCompat.GuestBoard) continue;
                    try
                    {
                        var data = new SaveMissionData((int)e.MissionIndex, e.OriginPort, e.DestinationPort,
                            e.GoodPrefabIndex, e.GoodCount, e.TotalPrice, e.InsuranceLevel, e.Distance,
                            e.DeliveredGoods, e.DueDay);
                        var mission = new Mission(data);
                        if (PostalExpansionCompat.GuestBoard)
                            PostalExpansionCompat.Attach(mission, e.PostalKind, e.PostalLetterId);
                        missions[e.MissionIndex] = mission;
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] mission " + e.MissionIndex + ": " + ex.Message); }
                }
                if ((_net.HostModFeatures & ModFeatures.EconomyOverhaul) != 0)
                    EconomyOverhaulCompat.MirrorRecords(entries);
                Plugin.Logger.LogInfo("[MissionSync] journal mirrored: " + entries.Length + " missions");
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
            var entry = new MissionEntry
            {
                OriginPort = d.originPort,
                DestinationPort = d.destinationPort,
                GoodPrefabIndex = d.goodPrefabIndex,
                GoodCount = d.goodCount,
                TotalPrice = d.totalPrice,
                InsuranceLevel = d.insuranceLevel,
                Distance = d.distance,
                DueDay = d.dueDay,
            };
            PostalExpansionCompat.Describe(mission, out entry.PostalKind, out entry.PostalLetterId);
            if (PostalExpansionCompat.SyncEnabled && GameState.inPortMissionList)
                entry.PostalTab = PostalExpansionCompat.CurrentTab(MissionListUI.instance?.worldMissions == true);
            if ((entry.PostalKind != PostalExpansionCompat.KindNone || entry.PostalTab == 2) && !PostalExpansionCompat.GuestBoard)
            {
                NotificationUi.instance?.ShowNotification("Postal missions require the same Postal Expansion version as the host.");
                return true;
            }
            // Economy Overhaul rolls modifiers per offer object on this machine; the host builds a new one.
            if ((_net.HostModFeatures & ModFeatures.EconomyOverhaul) != 0 &&
                EconomyOverhaulCompat.ReadOffer(mission, out int modifiers, out bool world, out int offered))
            {
                entry.ModModifiers = modifiers;
                entry.ModFlags = (byte)(1 | (world ? 2 : 0));
                entry.ModOffered = offered;
            }
            _net.Broadcast(new MissionAcceptMsg { Mission = entry }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] out accept dest=" + d.destinationPort + " good=" + d.goodPrefabIndex);
            return true;
        }

        /// <summary>Client: forward an abandon (by shared journal slot). Returns true if forwarded.</summary>
        public bool RequestAbandon(int missionIndex)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return false;
            if (missionIndex < 0 || missionIndex >= Slots) return false;
            _net.Broadcast(new MissionAbandonMsg { MissionIndex = (byte)missionIndex }, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] out abandon slot=" + missionIndex);
            return true;
        }

        public void OnMissionAccept(MissionAcceptMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            try
            {
                var e = msg.Mission;
                bool postal = e.PostalKind != PostalExpansionCompat.KindNone || e.PostalTab == 2;
                if (postal && (!PostalExpansionCompat.SyncEnabled ||
                    (_net.FeaturesOf(fromPeer) & ModFeatures.PostalExpansion) == 0)) return;
                var data = new SaveMissionData(-1, e.OriginPort, e.DestinationPort, e.GoodPrefabIndex,
                    e.GoodCount, e.TotalPrice, e.InsuranceLevel, e.Distance, 0, e.DueDay);
                var mission = postal
                    ? PostalExpansionCompat.FindOffer(PostalExpansionCompat.PortAt(e.OriginPort), e.PostalTab, e)
                    : new Mission(data);
                if (mission == null) return;
                if (postal)
                {
                    mission.goodCount = e.GoodCount;
                    mission.totalPrice = e.TotalPrice;
                }
                if ((e.ModFlags & 1) != 0) EconomyOverhaulCompat.WriteOffer(mission, e.ModModifiers, (e.ModFlags & 2) != 0, e.ModOffered);
                PlayerMissions.AcceptMission(mission);   // host vanilla: assigns slot, spawns goods, reduces demand
                if (postal && GameState.inPortMissionList) PostalExpansionCompat.RefreshHostBoard(MissionListUI.instance);
                Plugin.Logger.LogInfo("[MissionSync] in accept dest=" + e.DestinationPort + " good=" + e.GoodPrefabIndex);
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
                    Plugin.Logger.LogInfo("[MissionSync] in abandon slot=" + idx);
                }
                else Plugin.Logger.LogInfo("[MissionSync] in abandon: empty slot " + idx);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] OnMissionAbandon: " + ex.Message); }
        }

        // -----------------------------------------------------------------
        // Delivery by a client. The host copy of a carried good has no collider, so the trigger
        // can't fire there; the client forwards it instead.
        // -----------------------------------------------------------------

        private int _lastDeliverId;
        private float _lastDeliverAt;

        public bool IsConnectedClient => _net.Role == Role.Client && _net.State == LinkState.Connected;

        /// <summary>Client: forward a delivery trigger, only for goods we carry.</summary>
        public bool RequestDeliver(ShipItem item, int portIndex, bool postalDropOff = false, uint requestId = 0)
        {
            if (!IsConnectedClient || item == null) return false;
            var items = ItemSync.Instance;
            if (items == null) return false;
            int id, prefab;
            if (!(postalDropOff ? items.TrySharedIdentity(item, out id, out prefab)
                                : items.TryGetLocallyHeldKey(item, out id, out prefab))) return false;
            // The trigger fires repeatedly for the same crate.
            if (id == _lastDeliverId && Time.realtimeSinceStartup - _lastDeliverAt < 1f) return false;
            _lastDeliverId = id;
            _lastDeliverAt = Time.realtimeSinceStartup;
            _net.Broadcast(new MissionDeliverMsg { InstanceId = id, PrefabIndex = prefab, PortIndex = portIndex,
                           PostalDropOff = postalDropOff, RequestId = requestId },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] out deliver item=" + id + " port=" + portIndex);
            return true;
        }

        public void OnMissionDeliver(MissionDeliverMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            try
            {
                var ports = Port.ports;
                Port port = ports != null && msg.PortIndex >= 0 && msg.PortIndex < ports.Length ? ports[msg.PortIndex] : null;
                ShipItem item = ItemSync.Instance?.HostFindItem(msg.InstanceId, msg.PrefabIndex);
                Good good = item != null && item.CompareTag("Good") ? item.GetComponent<Good>() : null;
                Mission mission = good != null ? good.GetAssignedMission() : null;
                if (port == null || mission == null)
                {
                    // Duplicate request or not a mission good.
                    Plugin.Logger.LogInfo("[MissionSync] in deliver ignored item=" + msg.InstanceId + " port=" + msg.PortIndex);
                    if (msg.PostalDropOff) ReplyDelivery(fromPeer, msg, false, "This letter is no longer available for delivery.");
                    return;
                }

                // Same checks as vanilla PortDude.OnTriggerEnter.
                if (mission.destinationPort == port)
                {
                    bool letter = PostalExpansionCompat.IsLetter(item.GetComponent<SaveablePrefab>());
                    if (msg.PostalDropOff != letter || (letter &&
                        (_net.FeaturesOf(fromPeer) & ModFeatures.PostalExpansion) == 0))
                    {
                        if (msg.PostalDropOff) ReplyDelivery(fromPeer, msg, false, "This letter cannot be delivered here.");
                        return;
                    }
                    if (letter && !PostalExpansionCompat.HostMayDeliver(mission, out string refusal))
                    {
                        ReplyDelivery(fromPeer, msg, false, refusal);
                        return;
                    }
                    string text = "Delivered " + (PostalExpansionCompat.DisplayName(mission) ?? mission.goodPrefab.GetComponent<ShipItem>().name) +
                                  "\n( " + (mission.GetDeliveredCount() + 1) + " / " + mission.goodCount + " )";
                    good.Deliver();                          // reward reaches clients via PostDeliver
                    ItemSync.Instance?.HostRefreshNextTick(); // despawn on clients
                    ReplyDelivery(fromPeer, msg, true, text);
                    Plugin.Logger.LogInfo("[MissionSync] in deliver ok item=" + msg.InstanceId + " port=" + msg.PortIndex);
                }
                else if (msg.PostalDropOff || mission.originPort != port)
                    ReplyDelivery(fromPeer, msg, false, "You are at the wrong port!");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[MissionSync] OnMissionDeliver: " + ex.Message);
                if (msg.PostalDropOff) ReplyDelivery(fromPeer, msg, false, "The host could not complete this delivery.");
            }
        }

        private static void ReplyDelivery(LiteNetLib.NetPeer peer, MissionDeliverMsg request, bool delivered, string text)
            => peer?.Send(new MissionDeliverResultMsg { Delivered = delivered, Text = text, RequestId = request.RequestId },
                          LiteNetLib.DeliveryMethod.ReliableOrdered);

        public void OnMissionDeliverResult(MissionDeliverResultMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            try
            {
                if (msg.RequestId != 0 && !PostalExpansionCompat.CompleteLetterDelivery(msg.RequestId, msg.Delivered)) return;
                if (!string.IsNullOrEmpty(msg.Text)) NotificationUi.instance?.ShowNotification(msg.Text);
                if (msg.Delivered) UISoundPlayer.instance?.PlayGoldSound();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] OnMissionDeliverResult: " + ex.Message); }
        }

        /// <summary>
        /// Host: a delivery just paid the whole reward into the host's wallet. It is divided equally
        /// between the players who are in the world: every guest's part leaves the host's wallet and
        /// goes to that guest's own. Reputation is not divided.
        /// </summary>
        public void ForwardReward(MissionRewardMsg reward)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected) return;
            if (reward == null || (reward.Amount <= 0 && !reward.PostalLetter) || reward.Region < 0) return;
            uint[] guests = _net.ReadyClientIds();
            if (guests.Length == 0) return;
            if (WalletSync.Instance != null && WalletSync.Instance.Shared)
            {
                // One wallet: the reward stays where the game put it. Guests still get the message
                // for reputation, the mission log and the notification; they add no money.
                foreach (uint guest in guests)
                    _net.SendToPlayer(guest, reward, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Plugin.Logger.LogInfo("[MissionSync] out reward region=" + reward.Region + " total=" + reward.Amount +
                                      " shared wallet rep=" + reward.RepAmount);
                return;
            }
            int total = reward.Amount;
            int players = guests.Length + 1;
            int each = WalletShare.Each(total, players);
            int hostKeeps = WalletShare.HostKeeps(total, players);
            if (each > 0 && PlayerGold.currency != null && reward.Region < PlayerGold.currency.Length)
            {
                PlayerGold.currency[reward.Region] -= total - hostKeeps;
                // The game has just shown the whole reward; show what actually stays.
                try { MoneyNotification.instance?.PlayNotif(hostKeeps, reward.Region); } catch { }
            }
            reward.Amount = each;
            reward.ExpectedReward = WalletShare.Each(reward.ExpectedReward, players);
            foreach (uint guest in guests)
                _net.SendToPlayer(guest, reward, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[MissionSync] out reward region=" + reward.Region + " total=" + total +
                                  " players=" + players + " each=" + each + " hostKeeps=" + hostKeeps +
                                  " rep=" + reward.RepAmount);
        }

        public void OnMissionReward(MissionRewardMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            try
            {
                if (PlayerGold.currency != null && msg.Region >= 0 && msg.Region < PlayerGold.currency.Length &&
                    (msg.Amount > 0 || msg.PostalLetter))
                {
                    // With a shared wallet the money is already in it and arrives as its new balance.
                    bool shared = WalletSync.Instance != null && WalletSync.Instance.Shared;
                    if (!shared) PlayerGold.currency[msg.Region] += msg.Amount;
                    if (msg.PostalLetter)
                    {
                        if (msg.OriginRegion >= 0 && msg.PostalOriginRep != 0)
                            PlayerReputation.ChangeReputation(msg.PostalOriginRep, (PortRegion)msg.OriginRegion);
                        if (msg.DestinationRegion >= 0 && msg.PostalDestinationRep != 0)
                            PlayerReputation.ChangeReputation(msg.PostalDestinationRep, (PortRegion)msg.DestinationRegion);
                        if (msg.PostalOriginRep != 0 || msg.PostalDestinationRep != 0)
                            ReputationNotifUI.instance?.ShowNotif();
                    }
                    else if (msg.RepAmount != 0)
                    {
                        if (msg.OriginRegion >= 0) PlayerReputation.ChangeReputation(msg.RepAmount, (PortRegion)msg.OriginRegion);
                        if (msg.DestinationRegion >= 0 && msg.DestinationRegion != msg.OriginRegion)
                            PlayerReputation.ChangeReputation(msg.RepAmount, (PortRegion)msg.DestinationRegion);
                        try { ReputationNotifUI.instance?.ShowNotif(); } catch { }
                    }
                    if (msg.GoodPrefabIndex > 0)
                    {
                        try { MissionLog.instance?.AddToLog(msg.GoodPrefabIndex, msg.DestinationName ?? "", msg.Amount, msg.RepAmount, msg.Region); } catch { }
                    }
                    try
                    {
                        if (DayLogs.instance != null && DayLogs.instance.dayLogs != null &&
                            msg.Region >= 0 && msg.Region < DayLogs.instance.dayLogs.Length &&
                            DayLogs.instance.dayLogs[msg.Region] != null)
                            DayLogs.instance.dayLogs[msg.Region].LogMissionDelivery(msg.ExpectedReward, msg.Amount);
                    }
                    catch { }
                    if (msg.Amount > 0)
                        try { MoneyNotification.instance?.PlayNotif(msg.Amount, msg.Region); } catch { }
                    Plugin.Logger.LogInfo("[MissionSync] in reward +" + msg.Amount + " region=" + msg.Region);
                    SaveGuestProfileNow("mission reward");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] OnMissionReward: " + ex.Message); }
        }

        public void SaveGuestProfileNow(string reason)
        {
            try
            {
                if (_net.Role == Role.Client && _net.State == LinkState.Connected)
                {
                    CoopProfile.SaveFromGame();
                    Plugin.Logger.LogInfo("[MissionSync] client profile saved: " + reason);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[MissionSync] SaveGuestProfileNow: " + ex.Message); }
        }

        public void Clear()
        {
            _lastSig = null;
            _checkTimer = 0f;
            _lastDeliverId = 0;
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
        private sealed class DeliveryState
        {
            public int Region;
            public int CurrencyBefore;
            public int OriginRegion;
            public int DestinationRegion;
            public int OriginRepBefore;
            public int DestinationRepBefore;
            public int GoodPrefabIndex;
            public string DestinationName;
            public int ExpectedReward;
            public bool PostalLetter;
        }

        public static void Apply(Harmony harmony)
        {
            bool deliver = TryPatch(harmony, "DeliverGood", nameof(PreDeliver), nameof(PostDeliver));
            // Accept/abandon are host-authoritative: a connected client forwards the request and skips the
            // local effect (which would spawn phantom goods and diverge the journal).
            bool accept = TryPatchStatic(harmony, "AcceptMission", new[] { AccessTools.TypeByName("Mission") }, nameof(PreAcceptMission));
            bool abandon = TryPatchStatic(harmony, "AbandonMission", new[] { typeof(int) }, nameof(PreAbandonMission));
            bool buyGood = TryPatchEconomy(harmony, "BuyGood", nameof(PostEconomyChanged));
            bool sellGood = TryPatchEconomy(harmony, "SellGood", nameof(PostEconomyChanged));
            bool receipt = TryPatchEconomy(harmony, "PrintReceipt", nameof(PostEconomyChanged));
            bool portDude = TryPatchPortDude(harmony);
            Plugin.Logger.LogInfo("[MissionPatches] Mission patch: DeliverGood=" + deliver + ", Accept=" + accept +
                                  ", Abandon=" + abandon + ", BuyGood=" + buyGood + ", SellGood=" + sellGood +
                                  ", PrintReceipt=" + receipt + ", PortDudeTrigger=" + portDude);
            SailwindCoop.Runtime.PatchHealth.Report("Missions",
                (deliver ? 1 : 0) + (accept ? 1 : 0) + (abandon ? 1 : 0) +
                (buyGood ? 1 : 0) + (sellGood ? 1 : 0) + (receipt ? 1 : 0) + (portDude ? 1 : 0), 7);
        }

        private static FieldInfo _fPortDudePort;

        private static bool TryPatchPortDude(Harmony harmony)
        {
            try
            {
                var mi = typeof(PortDude).GetMethod("OnTriggerEnter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                                    null, new[] { typeof(Collider) }, null);
                _fPortDudePort = typeof(PortDude).GetField("port", BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null || _fPortDudePort == null) return false;
                var prefix = new HarmonyMethod(typeof(MissionPatches).GetMethod(nameof(PrePortDudeTrigger), BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PortDude.OnTriggerEnter: " + e.Message); return false; }
        }

        // A connected client never runs vanilla delivery; its journal is a read-only mirror.
        private static bool PrePortDudeTrigger(PortDude __instance, Collider other)
        {
            var s = MissionSync.Instance;
            if (s == null || !s.IsConnectedClient) return true;
            try
            {
                if (other != null && !other.CompareTag("Player") && other.CompareTag("Good"))
                {
                    var port = _fPortDudePort != null ? _fPortDudePort.GetValue(__instance) as Port : null;
                    var item = other.GetComponent<ShipItem>();
                    if (port != null && item != null && !PostalExpansionCompat.IsLetter(item.GetComponent<SaveablePrefab>()))
                        s.RequestDeliver(item, port.portIndex);
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PrePortDudeTrigger: " + e.Message); }
            return false;
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

        private static bool TryPatchEconomy(Harmony harmony, string method, string postfixName)
        {
            try
            {
                var mi = typeof(EconomyUI).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi == null) return false;
                var postfix = new HarmonyMethod(typeof(MissionPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, postfix: postfix);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] EconomyUI." + method + ": " + e.Message); return false; }
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
        private static void PreDeliver(Mission __instance, out DeliveryState __state)
        {
            __state = null;
            try
            {
                if (__instance == null || __instance.destinationPort == null) return;
                bool letter = PostalExpansionCompat.IsLetterMission(__instance);
                int region = letter ? (int)Currency.gold : (int)__instance.destinationPort.region;
                if (PlayerGold.currency == null || region < 0 || region >= PlayerGold.currency.Length) return;
                var data = __instance.PrepareSaveData();
                int originRegion = __instance.originPort != null ? (int)__instance.originPort.region : -1;
                int destinationRegion = __instance.destinationPort != null ? (int)__instance.destinationPort.region : -1;
                __state = new DeliveryState
                {
                    Region = region,
                    CurrencyBefore = PlayerGold.currency[region],
                    OriginRegion = originRegion,
                    DestinationRegion = destinationRegion,
                    OriginRepBefore = originRegion >= 0 ? PlayerReputation.GetRep(originRegion) : 0,
                    DestinationRepBefore = destinationRegion >= 0 ? PlayerReputation.GetRep(destinationRegion) : 0,
                    GoodPrefabIndex = data.goodPrefabIndex,
                    DestinationName = __instance.destinationPort.GetPortName(),
                    ExpectedReward = data.goodCount > 0 ? data.totalPrice / data.goodCount : 0,
                    PostalLetter = letter,
                };
            }
            catch { __state = null; }
        }

        private static void PostDeliver(DeliveryState __state)
        {
            try
            {
                if (__state == null) return;
                int delta = PlayerGold.currency[__state.Region] - __state.CurrencyBefore;
                if (delta <= 0 && !__state.PostalLetter) return;
                int origin = __state.OriginRegion >= 0 ? PlayerReputation.GetRep(__state.OriginRegion) - __state.OriginRepBefore : 0;
                int destination = __state.DestinationRegion >= 0 ? PlayerReputation.GetRep(__state.DestinationRegion) - __state.DestinationRepBefore : 0;
                MissionSync.Instance?.ForwardReward(new MissionRewardMsg
                {
                    Region = __state.Region,
                    Amount = delta,
                    OriginRegion = __state.OriginRegion,
                    DestinationRegion = __state.DestinationRegion,
                    RepAmount = __state.PostalLetter
                        ? (Math.Abs(origin) >= Math.Abs(destination) ? origin : destination)
                        : Math.Max(0, Math.Max(origin, destination)),
                    GoodPrefabIndex = __state.GoodPrefabIndex,
                    DestinationName = __state.DestinationName,
                    ExpectedReward = __state.ExpectedReward,
                    PostalLetter = __state.PostalLetter,
                    PostalOriginRep = __state.PostalLetter ? origin : 0,
                    PostalDestinationRep = __state.PostalLetter && __state.OriginRegion != __state.DestinationRegion ? destination : 0,
                });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PostDeliver: " + e.Message); }
        }

        private static void PostEconomyChanged()
        {
            try { MissionSync.Instance?.SaveGuestProfileNow("economy"); }
            catch (Exception e) { Plugin.Logger.LogWarning("[MissionPatches] PostEconomyChanged: " + e.Message); }
        }
    }
}
