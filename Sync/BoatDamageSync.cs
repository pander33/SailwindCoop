using System.Collections.Generic;
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
    /// snapshots and write them onto their local <c>BoatDamage</c>. Remote bilge-pump use is
    /// special: a client's <c>BilgePump.OnActivate/OnUnactivate</c> pair becomes a held request;
    /// the host drains its authoritative <c>BoatDamage.waterLevel</c> while that request is down.
    /// </summary>
    public sealed class BoatDamageSync
    {
        public static BoatDamageSync Instance { get; private set; }

        private readonly CoopNet _net;
        private PlayerEmbarkerNew _emb;
        private Transform _cachedBoat;
        private BoatDamage _damage;
        private BilgePump[] _pumps = System.Array.Empty<BilgePump>();

        // actor NetId -> pump indexes currently held by that actor
        private readonly Dictionary<uint, HashSet<ushort>> _heldPumpsByActor = new Dictionary<uint, HashSet<ushort>>();

        private float _sendTimer;
        private string _lastPump = "—";
        private long _lastPumpTick;
        private string _lastRepair = "—";
        private long _lastRepairTick;

        private const float PumpInput = 50f; // BilgePump's own LimitInput() max; remote hold means active pumping.

        public float SnapshotHz = 4f;

        public BoatDamageSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        public string DamageText
        {
            get
            {
                if (_damage == null) return "no BoatDamage";
                string pump = "—";
                if (_lastPumpTick != 0)
                {
                    long age = _net.Clock.ServerTick - _lastPumpTick;
                    if (age < 0) age = 0;
                    pump = _lastPump + " " + age + "ms";
                }
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
                       " pump " + ActivePumpCount() + "/" + _pumps.Length +
                       " · " + pump + " · " + repair;
            }
        }

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            RefreshBoat();

            if (_net.Role == Role.Host)
            {
                ApplyRemotePumps(dt);
                SendSnapshot(dt);
            }
        }

        /// <summary>Host: update one remote actor's held-pump state from a HoldRequest.</summary>
        public void SetRemotePump(ushort index, bool down, uint actorNetId)
        {
            if (_net.Role != Role.Host) return;
            RefreshBoat();
            if (index >= _pumps.Length)
            {
                Plugin.Logger.LogWarning("[BoatDamageSync] Запрос помпы #" + index + ": на лодке только " + _pumps.Length);
                return;
            }

            if (!_heldPumpsByActor.TryGetValue(actorNetId, out var set))
            {
                set = new HashSet<ushort>();
                _heldPumpsByActor[actorNetId] = set;
            }

            if (down) set.Add(index);
            else set.Remove(index);
            if (set.Count == 0) _heldPumpsByActor.Remove(actorNetId);

            RememberPump((down ? "вх down" : "вх up") + " #" + index + " p" + actorNetId);
            Plugin.Logger.LogInfo("[BoatDamageSync] Помпа #" + index + " от игрока " + actorNetId + ": " + (down ? "зажата" : "отпущена"));
        }

        public void ClearRemoteActor(uint actorNetId)
        {
            if (_heldPumpsByActor.Remove(actorNetId))
                Plugin.Logger.LogInfo("[BoatDamageSync] Сброшены удержания помпы игрока " + actorNetId);
        }

        public void OnDamageState(BoatDamageStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            RefreshBoat();
            if (_damage == null) return;

            _damage.waterLevel = Mathf.Clamp01(msg.WaterLevel);
            _damage.hullDamage = Mathf.Clamp01(msg.HullDamage);
            _damage.oakum = Mathf.Max(0f, msg.Oakum);
            _damage.waterIntakeChunk = Mathf.Max(0f, msg.WaterIntakeChunk);
            _damage.sunk = msg.Sunk;
        }

        public void NotifyLocalDamageAction(DamageAction action, float amount)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (amount <= 0.00001f) return;

            _net.Broadcast(new DamageRequestMsg { Action = action, Amount = amount },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            RememberRepair("исх " + action + " +" + amount.ToString("0.000"));
        }

        public void OnDamageRequest(DamageRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshBoat();
            if (_damage == null) return;

            float amount = Mathf.Max(0f, msg.Amount);
            if (amount <= 0f) return;

            if (msg.Action == DamageAction.AddOakum)
            {
                float need = Mathf.Max(0f, _damage.hullDamage * _damage.waterUnitsCapacity - _damage.oakum);
                float applied = Mathf.Min(amount, need);
                if (applied > 0f) _damage.oakum += applied;
                RememberRepair("вх пакля +" + applied.ToString("0.000"));
            }
            else if (msg.Action == DamageAction.BailWater)
            {
                float before = _damage.waterLevel;
                _damage.waterLevel = Mathf.Clamp01(_damage.waterLevel - amount);
                RememberRepair("вх вода -" + (before - _damage.waterLevel).ToString("0.000"));
            }

            BroadcastSnapshot();
        }

        private void SendSnapshot(float dt)
        {
            if (_damage == null) return;

            float interval = 1f / Mathf.Max(0.5f, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            BroadcastSnapshot();
        }

        private void BroadcastSnapshot()
        {
            if (_damage == null) return;

            _net.Broadcast(new BoatDamageStateMsg
            {
                Tick = _net.Clock.ServerTick,
                WaterLevel = _damage.waterLevel,
                HullDamage = _damage.hullDamage,
                Oakum = _damage.oakum,
                WaterIntakeChunk = _damage.waterIntakeChunk,
                Sunk = _damage.sunk,
            }, LiteNetLib.DeliveryMethod.Unreliable);
        }

        private void ApplyRemotePumps(float dt)
        {
            if (_damage == null || _damage.sunk || _heldPumpsByActor.Count == 0) return;

            bool any = false;
            for (int i = 0; i < _pumps.Length; i++)
            {
                if (!IsPumpHeld((ushort)i)) continue;
                var pump = _pumps[i];
                if (pump == null || pump.damage == null) continue;

                float before = pump.damage.waterLevel;
                pump.damage.waterLevel = Mathf.Clamp01(pump.damage.waterLevel - dt * PumpInput * pump.drainRate);
                RotatePumpVisual(pump, dt);
                any = any || pump.damage.waterLevel != before;
            }
        }

        private static void RotatePumpVisual(BilgePump pump, float dt)
        {
            if (pump == null) return;
            float mult = pump.damage != null && pump.damage.waterLevel > 0f ? 0.75f : 2.25f;
            pump.transform.Rotate(Vector3.forward, PumpInput * dt * 1.4f * pump.rotationSpeed * mult, Space.Self);
        }

        private bool IsPumpHeld(ushort index)
        {
            foreach (var set in _heldPumpsByActor.Values)
                if (set.Contains(index)) return true;
            return false;
        }

        private int ActivePumpCount()
        {
            int n = 0;
            for (int i = 0; i < _pumps.Length; i++)
                if (IsPumpHeld((ushort)i)) n++;
            return n;
        }

        private void RefreshBoat()
        {
            if (_emb == null) _emb = Object.FindObjectOfType<PlayerEmbarkerNew>();
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat == _cachedBoat) return;

            _cachedBoat = boat;
            _heldPumpsByActor.Clear();
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

            Plugin.Logger.LogInfo("[BoatDamageSync] Корабль сменился: damage=" + (_damage != null) +
                                  ", помп=" + _pumps.Length + " ('" + boat.name + "')");
        }

        private void RememberPump(string text)
        {
            _lastPump = text;
            _lastPumpTick = _net.Clock.ServerTick;
        }

        private void RememberRepair(string text)
        {
            _lastRepair = text;
            _lastRepairTick = _net.Clock.ServerTick;
        }

        public void Clear()
        {
            _cachedBoat = null;
            _damage = null;
            _pumps = System.Array.Empty<BilgePump>();
            _heldPumpsByActor.Clear();
            _sendTimer = 0f;
            _lastPump = "—";
            _lastPumpTick = 0L;
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
        public static void Apply(Harmony harmony)
        {
            bool hull = TryPatch(harmony, typeof(HullDamageButton), "OnItemClick", new[] { typeof(PickupableItem) },
                nameof(PreHullOakum), nameof(PostHullOakum));
            bool water = TryPatch(harmony, typeof(BoatDamageWaterButton), "OnItemClick", new[] { typeof(PickupableItem) },
                nameof(PreWaterBail), nameof(PostWaterBail));
            bool oakumAlt = TryPatch(harmony, typeof(ShipItemOakum), "OnAltActivate", System.Type.EmptyTypes,
                nameof(PreOakumAlt), nameof(PostOakumAlt));

            Plugin.Logger.LogInfo("[BoatDamagePatches] Патчи повреждений: HullOakum=" + hull +
                                  ", WaterBail=" + water + ", OakumAlt=" + oakumAlt);
        }

        private static bool TryPatch(Harmony harmony, System.Type type, string method, System.Type[] args, string prefixName, string postfixName)
        {
            try
            {
                var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null)
                {
                    Plugin.Logger.LogWarning("[BoatDamagePatches] Не найден " + type.Name + "." + method);
                    return false;
                }
                var prefix = new HarmonyMethod(typeof(BoatDamagePatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = new HarmonyMethod(typeof(BoatDamagePatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[BoatDamagePatches] " + type.Name + "." + method + ": " + e.Message);
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
                    BoatDamageSync.Instance?.NotifyLocalDamageAction(DamageAction.AddOakum, delta);
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning("[BoatDamagePatches] PostHullOakum: " + e.Message); }
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
                    BoatDamageSync.Instance?.NotifyLocalDamageAction(DamageAction.BailWater, delta);
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
            catch (System.Exception e) { Plugin.Logger.LogWarning("[BoatDamagePatches] PostWaterBail: " + e.Message); }
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
                    BoatDamageSync.Instance?.NotifyLocalDamageAction(DamageAction.AddOakum, delta);
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning("[BoatDamagePatches] PostOakumAlt: " + e.Message); }
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
