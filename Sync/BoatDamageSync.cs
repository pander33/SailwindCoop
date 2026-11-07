using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Host-authoritative damage/flooding sync for the current boat.
    ///
    /// The local game still owns its normal simulation on the host. Clients receive scalar
    /// snapshots and write them onto their local <c>BoatDamage</c>.
    /// </summary>
    public sealed class BoatDamageSync
    {
        public static BoatDamageSync Instance { get; private set; }

        private readonly Transform _boundBoat;
        private readonly ushort _boatId = BoatLocator.NoBoat;
        private uint _layoutHash;
        private readonly BoatContexts<BoatDamageSync> _fleet;

        private readonly CoopNet _net;
        private Transform _cachedBoat;
        private BoatDamage _damage;
        private BilgePump[] _pumps = System.Array.Empty<BilgePump>();

        private readonly BilgePumpSync _pumpSync;

        private float _sendTimer;
        private uint _revision;
        private readonly ItemStateGate _stateGate = new ItemStateGate();
        private string _lastRepair = "—";
        private long _lastRepairTick;

        public float SnapshotHz = 4f;

        public BoatDamageSync(CoopNet net)
        {
            _net = net;
            Instance = this;
            _pumpSync = new BilgePumpSync(net);
            _fleet = new BoatContexts<BoatDamageSync>((boat, id) => new BoatDamageSync(net, boat, id), c => c.Clear(),
                boat => BoatLayout.Stamp(boat.GetComponentsInChildren<BoatDamage>(true), boat.GetComponentsInChildren<BilgePump>(true)));
        }

        private BoatDamageSync(CoopNet net, Transform boat, ushort id)
        {
            _net = net; _boundBoat = boat; _boatId = id;
            RefreshBoat();
        }

        public string DamageText
        {
            get
            {

                if (_fleet != null) return _fleet.Describe(c => "boat " + c._boatId + ": " + c.DamageText);
                if (_damage == null) return "no BoatDamage";
                string repair = "—";
                if (_lastRepairTick != 0)
                {
                    long age = _net.Clock.ServerTick - _lastRepairTick;
                    if (age < 0) age = 0;
                    repair = _lastRepair + " " + age + "ms";
                }
                return "water=" + _damage.waterLevel.ToString("0.000") +
                       " hull=" + _damage.hullDamage.ToString("0.000") +
                       " oakum=" + _damage.oakum.ToString("0.0") +
                       " pumps=" + _pumps.Length + " · " + repair;
            }
        }

        public void Tick(float dt)
        {
            if (_fleet != null)
            {
                _pumpSync.Tick(dt);
                foreach (var c in _fleet.Values) c.Tick(dt);
                return;
            }
            if (_net.State != LinkState.Connected) return;
            RefreshBoat();

            if (_net.Role == Role.Host) SendSnapshot(dt);
            else if (_net.Role == Role.Client) AskResync();
        }

        internal void RecordPumpStroke(BilgePump pump, float input)
            => _pumpSync?.Record(pump, input);

        public void OnPumpStroke(PumpStrokeMsg msg, LiteNetLib.NetPeer peer)
            => _pumpSync?.Receive(msg, peer);

        public void OnDamageState(BoatDamageStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading || !_net.IsHostPeer(fromPeer)) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "BoatDamageSync")) c.OnDamageState(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Client) return;
            bool changed, pose;
            if (!_stateGate.Receive(msg.Revision, msg.Tick, 0, 0, 0, out changed, out pose)) return;
            RefreshBoat();
            if (_damage == null) return;

            _damage.waterLevel = msg.WaterLevel;
            _damage.hullDamage = msg.HullDamage;
            _damage.oakum = msg.Oakum;
            _damage.waterIntakeChunk = msg.WaterIntakeChunk;
            _damage.sunk = msg.Sunk;
        }

        public void NotifyLocalDamageAction(DamageAction action, float amount, BoatDamage damage)
        {
            if (InteractionContext.Suppressed) return;
            if (_fleet != null)
            {
                foreach (var c in _fleet.Values) if (c._damage == damage && damage != null)
                    { c.NotifyLocalDamageAction(action, amount, damage); break; }
                return;
            }
            var request = new DamageRequestMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Action = action, Amount = amount };
            if (ItemOperationCapture.Damage(request)) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (amount <= 0.00001f) return;

            _net.Broadcast(request,
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            RememberRepair("out " + action + " +" + amount.ToString("0.000"));
        }

        public void OnDamageRequest(DamageRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading) return;
                if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(fromPeer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "BoatDamageSync")) c.OnDamageRequest(msg, fromPeer);
                return;
            }
            if (_net.Role != Role.Host) return;
            RefreshBoat();
            if (_damage == null) return;

            if (msg.Action == DamageAction.AddOakum)
            {
                _damage.oakum += msg.Amount;
                RememberRepair("in oakum +" + msg.Amount.ToString("0.000"));
            }
            else if (msg.Action == DamageAction.BailWater)
            {
                // Not a judgement of the request: a delta computed against the guest's older water level
                // would otherwise leave the host's hull below empty.
                _damage.waterLevel = Mathf.Max(0f, _damage.waterLevel - msg.Amount);
                RememberRepair("in water -" + msg.Amount.ToString("0.000"));
            }

            BroadcastSnapshot();
        }

        private readonly ChangeStream _stream = new ChangeStream();
        private float _sentWater = float.NaN, _sentHull = float.NaN, _sentOakum = float.NaN, _sentIntake = float.NaN;
        private bool _sentSunk, _resyncAsked;
        /// <summary>Host: a client just bound this hull and has no state for it yet.</summary>
        public void Resync(ushort boat) { var context = _fleet?.Get(boat); if (context != null) context._stream.Reset(); }
        private void AskResync()
        {
            if (_resyncAsked || !GameState.playing || GameState.currentlyLoading) return;
            _resyncAsked = true;
            _net.Broadcast(new ResyncRequestMsg { BoatIndex = _boatId, Domain = ResyncDomain.Damage }, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
        /// <summary>The state is sent on change; this often it also goes out unchanged, so a copy
        /// that came apart from the host's for any reason does not stay so.</summary>
        private const float RefreshSeconds = 30f;
        private float _refreshTimer;
        private void SendSnapshot(float dt)
        {
            if (_damage == null) return;

            _refreshTimer += dt;
            float interval = 1f / Mathf.Max(0.5f, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            // A dry, undamaged hull at rest sends nothing.
            bool changed = !_damage.waterLevel.Equals(_sentWater) || !_damage.hullDamage.Equals(_sentHull) ||
                !_damage.oakum.Equals(_sentOakum) || !_damage.waterIntakeChunk.Equals(_sentIntake) || _damage.sunk != _sentSunk;
            var mode = _stream.Next(changed);
            if (mode != StreamSend.None) BroadcastSnapshot(mode == StreamSend.Reliable, periodic: true);
            else if (_refreshTimer >= RefreshSeconds) BroadcastSnapshot(reliable: true, periodic: true);
        }

        internal BoatDamageStateMsg OperationState(ushort boatId)
        {
            if (_fleet != null) return _fleet.Get(boatId)?.OperationState(boatId);
            RefreshBoat();
            if (_damage == null) return null;
            _revision = SessionCounters.NextRevision();
            return new BoatDamageStateMsg {
                BoatIndex = _boatId, LayoutHash = _layoutHash, Generation = BoatGenerationBook.Session.Get(_boatId), Revision = _revision, Tick = _net.Clock.ServerTick,
                WaterLevel = _damage.waterLevel, HullDamage = _damage.hullDamage, Oakum = _damage.oakum,
                WaterIntakeChunk = _damage.waterIntakeChunk, Sunk = _damage.sunk
            };
        }
        internal void ApplyOperationDamage(DamageRequestMsg msg)
        {
            if (_fleet != null && ShipyardSync.Instance?.DeferEmbedded(msg, () => ApplyOperationDamage(msg)) == true) return;
            if (_fleet != null) { _fleet.Get(msg.BoatIndex)?.ApplyOperationDamage(msg); return; }
            RefreshBoat(); if (_damage == null) return;
            if (msg.Action == DamageAction.AddOakum) _damage.oakum += msg.Amount;
            if (msg.Action == DamageAction.BailWater) _damage.waterLevel = Mathf.Max(0f, _damage.waterLevel - msg.Amount);
        }
        internal void ApplyOperationState(BoatDamageStateMsg msg)
        {
            if (_fleet != null && ShipyardSync.Instance?.DeferEmbedded(msg, () => ApplyOperationState(msg)) == true) return;
            if (_fleet != null) { _fleet.Get(msg.BoatIndex)?.ApplyOperationState(msg); return; }
            if (BoatGenerationBook.Session.Compare(msg.BoatIndex, msg.Generation) == GenerationOrder.Current) OnDamageState(msg, null);
        }
        private void BroadcastSnapshot(bool reliable = false, bool periodic = false)
        {
            var state = OperationState(_boatId);
            if (state == null) return;
            if (!periodic) _stream.Sent(reliable);
            _sentWater = state.WaterLevel; _sentHull = state.HullDamage; _sentOakum = state.Oakum;
            _sentIntake = state.WaterIntakeChunk; _sentSunk = state.Sunk;
            _refreshTimer = 0f;
            _net.Broadcast(state, reliable ? LiteNetLib.DeliveryMethod.ReliableOrdered : LiteNetLib.DeliveryMethod.Unreliable);
        }

        private void RefreshBoat()
        {
            Transform boat = _boundBoat;
            if (boat == _cachedBoat) return;

            _cachedBoat = boat;
            _sendTimer = 0f;

            if (boat == null)
            {
                _damage = null;
                _pumps = System.Array.Empty<BilgePump>();
                return;
            }

            _damage = boat.GetComponent<BoatDamage>()
                      ?? boat.GetComponentInParent<BoatDamage>()
                      ?? boat.GetComponentInChildren<BoatDamage>(true);
            _pumps = boat.GetComponentsInChildren<BilgePump>(true);
            _layoutHash = BoatLayout.Hash(boat, boat.GetComponentsInChildren<BoatDamage>(true), _pumps);

            Plugin.Logger.LogInfo("[BoatDamageSync] Boat changed: damage=" + (_damage != null) +
                                  ", pumps=" + _pumps.Length + " ('" + boat.name + "')");
        }

        private void RememberRepair(string text)
        {
            _lastRepair = text;
            _lastRepairTick = _net.Clock.ServerTick;
        }

        public void InvalidateHull(ushort id)
        {
            _pumpSync?.Invalidate(id);
            _fleet?.Invalidate(id);
        }

        public void Clear()
        {
            if (_fleet != null) { _pumpSync.Clear(); _fleet.Clear(); return; }
            _cachedBoat = null;
            _damage = null;
            _stateGate.Clear();
            _revision = 0;
            _pumps = System.Array.Empty<BilgePump>();
            _sendTimer = 0f;
            _lastRepair = "—";
            _lastRepairTick = 0L;
        }
    }

    /// <summary>
    /// Damage-domain item interactions. Prefix stores the authoritative scalar before vanilla
    /// item logic; postfix sends the positive delta to the host. Postfixes never throw into
    /// Sailwind's input flow.
    /// </summary>
    public static class BoatDamagePatches
    {
        private static void WarnPatch(string text) { try { Plugin.Logger?.LogWarning(text); } catch { } }
        public static void Apply(Harmony harmony)
        {
            bool hull = TryPatch(harmony, typeof(HullDamageButton), "OnItemClick", new[] { typeof(PickupableItem) },
                nameof(PreHullOakum), nameof(PostHullOakum));
            bool water = TryPatch(harmony, typeof(BoatDamageWaterButton), "OnItemClick", new[] { typeof(PickupableItem) },
                nameof(PreWaterBail), nameof(PostWaterBail));
            bool oakumAlt = TryPatch(harmony, typeof(ShipItemOakum), "OnAltActivate", System.Type.EmptyTypes,
                nameof(PreOakumAlt), nameof(PostOakumAlt));

            bool impact = TryPatch(harmony, typeof(BoatDamage), "Impact", new[] { typeof(Collider), typeof(float) }, nameof(PreClientImpact), null);
            bool overflow = TryPatch(harmony, typeof(BoatDamage), "Overflow", new[] { typeof(float) }, nameof(PreClientOverflow), null);
            bool update = TryPatch(harmony, typeof(BoatDamage), "Update", System.Type.EmptyTypes, nameof(PreClientUpdate), nameof(PostClientUpdate));
            bool daily = TryPatch(harmony, typeof(BoatDamage), "DailyDamage", System.Type.EmptyTypes, nameof(PreClientDailyDamage), null);
            bool pump = TryPatch(harmony, typeof(BilgePump), "ApplyRotation", System.Type.EmptyTypes, null, nameof(PostPumpRotation));

            Plugin.Logger.LogInfo("[BoatDamagePatches] Damage patches: HullOakum=" + hull +
                                  ", WaterBail=" + water + ", OakumAlt=" + oakumAlt +
                                  ", ClientImpact=" + impact + ", ClientOverflow=" + overflow +
                                  ", ClientUpdate=" + update + ", ClientDailyDamage=" + daily + ", PumpRotation=" + pump);
            SailwindCoop.Runtime.PatchHealth.Report("Damage", (hull ? 1 : 0) + (water ? 1 : 0) + (oakumAlt ? 1 : 0) +
                                                              (impact ? 1 : 0) + (overflow ? 1 : 0) +
                                                              (update ? 1 : 0) + (daily ? 1 : 0) + (pump ? 1 : 0), 8);
        }

        private static void PostPumpRotation(BilgePump __instance, float ___currentInput)
        {
            try { BoatDamageSync.Instance?.RecordPumpStroke(__instance, ___currentInput); }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] Pump rotation: " + e.Message); }
        }

        private struct ClientWater
        {
            public bool Captured;
            public float Water, Intake;
        }

        // The game's own leak, rain and drain (BoatDamage.UpdateWaterAndDrag) run on the client's
        // copy too, and the leak only for the boat this machine's player sails: a guest alone on a
        // boat would fill and sink a copy the host keeps dry. The rest of Update (drag, buoyancy,
        // the sinking itself) still runs, from the host's water. No closure: this is every frame.
        private static void PreClientUpdate(BoatDamage __instance, out ClientWater __state)
        {
            __state = default(ClientWater);
            try
            {
                if (!ClientCopy(__instance)) return;
                __state.Captured = true; __state.Water = __instance.waterLevel; __state.Intake = __instance.waterIntakeChunk;
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PreClientUpdate: " + e.Message); }
        }

        private static void PostClientUpdate(BoatDamage __instance, ClientWater __state)
        {
            try
            {
                if (!__state.Captured || __instance == null) return;
                // The game's own limits stay: a guest's pump on a dry hull takes the level below zero.
                __instance.waterLevel = Mathf.Clamp01(__state.Water); __instance.waterIntakeChunk = __state.Intake;
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PostClientUpdate: " + e.Message); }
        }

        // The daily wear of oakum and hull belongs to the host's boat.
        private static bool PreClientDailyDamage(BoatDamage __instance)
        {
            try { return !ClientCopy(__instance); }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PreClientDailyDamage: " + e.Message); return true; }
        }

        // The host owns hull damage and water. A client's copy of the boat still has a body under
        // this machine's physics, so the game would count that copy's own hits and water over the
        // side; state comes only when the host's changes, and such damage would stay.
        // No closures here: these run on every machine, the second one possibly every physics step.
        private static bool PreClientImpact(BoatDamage __instance)
        {
            try
            {
                if (!ClientCopy(__instance)) return true;
                BoatSync.IgnoredImpacts++;
                return false;
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PreClientImpact: " + e.Message); return true; }
        }

        private static bool PreClientOverflow(BoatDamage __instance)
        {
            try
            {
                if (!ClientCopy(__instance)) return true;
                BoatSync.IgnoredOverflows++;
                return false;
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PreClientOverflow: " + e.Message); return true; }
        }

        private static bool ClientCopy(BoatDamage damage)
        {
            var boats = SailwindCoop.Runtime.CoopBehaviour.Instance?.Boats;
            return boats != null && damage != null && boats.Drives(damage.transform);
        }

        private static bool TryPatch(Harmony harmony, System.Type type, string method, System.Type[] args, string prefixName, string postfixName)
        {
            try
            {
                var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null)
                {
                    WarnPatch("[BoatDamagePatches] Not found " + type.Name + "." + method);
                    return false;
                }
                var prefix = prefixName == null ? null
                    : new HarmonyMethod(typeof(BoatDamagePatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = postfixName == null ? null
                    : new HarmonyMethod(typeof(BoatDamagePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (System.Exception e)
            {
                WarnPatch("[BoatDamagePatches] " + type.Name + "." + method + ": " + e.Message);
                return false;
            }
        }

        private static void PreHullOakum(HullDamageButton __instance, out float __state)
        {
            __state = ReadOakum(GetHullDamage(__instance));
        }

        private static void PostHullOakum(HullDamageButton __instance, float __state)
        {
            try
            {
                float delta = ReadOakum(GetHullDamage(__instance)) - __state;
                if (delta > 0.00001f)
                    BoatDamageSync.Instance?.NotifyLocalDamageAction(DamageAction.AddOakum, delta, GetHullDamage(__instance));
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PostHullOakum: " + e.Message); }
        }

        private struct WaterBailState
        {
            public float Water;
            public float Amount;
            public float Health;
        }

        private static void PreWaterBail(BoatDamageWaterButton __instance, PickupableItem __0, out WaterBailState __state)
        {
            __state = new WaterBailState { Water = ReadWater(GetWaterDamage(__instance)) };
            var bottle = __0 as ShipItemBottle;
            if (bottle != null)
            {
                __state.Amount = bottle.amount;
                __state.Health = bottle.health;
            }
        }

        private static void PostWaterBail(BoatDamageWaterButton __instance, PickupableItem __0, WaterBailState __state)
        {
            try
            {
                float waterNow = ReadWater(GetWaterDamage(__instance));
                float delta = __state.Water - waterNow;
                var bottle = __0 as ShipItemBottle;
                bool bottleChanged = bottle != null &&
                                     (Mathf.Abs(bottle.amount - __state.Amount) > 0.0001f ||
                                      Mathf.Abs(bottle.health - __state.Health) > 0.0001f);

                if (delta > 0.00001f)
                {
                    BoatDamageSync.Instance?.NotifyLocalDamageAction(DamageAction.BailWater, delta, GetWaterDamage(__instance));
                }
                if (bottleChanged)
                    ItemSync.Instance?.NotifyHeldItemStateChanged(bottle, "bail-water");

                if (delta > 0.00001f || bottleChanged)
                    Plugin.Logger.LogInfo("[BoatDamagePatches] WaterBail item=" +
                                          (__0 != null ? __0.GetType().Name : "null") +
                                          " delta=" + delta.ToString("0.####") +
                                          " amount " + __state.Amount.ToString("0.##") + "->" +
                                          (bottle != null ? bottle.amount.ToString("0.##") : "?") +
                                          " health " + __state.Health.ToString("0.##") + "->" +
                                          (bottle != null ? bottle.health.ToString("0.##") : "?"));
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PostWaterBail: " + e.Message); }
        }

        private static void PreOakumAlt(ShipItemOakum __instance, out float __state)
        {
            __state = ReadOakum(CurrentBoatDamage());
        }

        private static void PostOakumAlt(ShipItemOakum __instance, float __state)
        {
            try
            {
                float delta = ReadOakum(CurrentBoatDamage()) - __state;
                if (delta > 0.00001f)
                    BoatDamageSync.Instance?.NotifyLocalDamageAction(DamageAction.AddOakum, delta, CurrentBoatDamage());
            }
            catch (System.Exception e) { WarnPatch("[BoatDamagePatches] PostOakumAlt: " + e.Message); }
        }

        private static BoatDamage GetHullDamage(HullDamageButton button)
        {
            var tex = button != null ? button.GetComponent<HullDamageTexture>() : null;
            return tex != null ? tex.damage : null;
        }

        private static BoatDamage GetWaterDamage(BoatDamageWaterButton button)
        {
            var water = button != null ? button.GetComponent<BoatDamageWater>() : null;
            return water != null ? water.damage : null;
        }

        private static BoatDamage CurrentBoatDamage()
        {
            try
            {
                return GameState.currentBoat != null && GameState.currentBoat.parent != null
                    ? GameState.currentBoat.parent.GetComponent<BoatDamage>()
                    : null;
            }
            catch { return null; }
        }

        private static float ReadOakum(BoatDamage damage) => damage != null ? damage.oakum : 0f;
        private static float ReadWater(BoatDamage damage) => damage != null ? damage.waterLevel : 0f;
    }
}
