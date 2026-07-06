using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P2 shared visual state for lanterns/lights. Full item pose/ownership remains P3; this
    /// layer only mirrors whether a known scene light is on and its fuel/health scalar.
    /// </summary>
    public sealed class LightSync
    {
        public static LightSync Instance { get; private set; }

        private readonly CoopNet _net;
        private readonly List<ShipItemLight> _lights = new List<ShipItemLight>();
        private readonly Dictionary<ShipItemLight, ushort> _index = new Dictionary<ShipItemLight, ushort>();
        private float _refreshTimer;
        private float _sendTimer;
        private string _last = "—";
        private long _lastTick;

        private static FieldInfo _fOn;
        private static MethodInfo _miSetLight;

        public float SnapshotHz = 2f;
        public int LightCount => _lights.Count;

        public string LightText
        {
            get
            {
                string last = "—";
                if (_lastTick != 0)
                {
                    long age = _net.Clock.ServerTick - _lastTick;
                    if (age < 0) age = 0;
                    last = _last + " " + age + "ms";
                }
                return _lights.Count + " pcs · " + last;
            }
        }

        public LightSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            RefreshLights(dt);
            if (_net.Role != Role.Host) return;

            float interval = 1f / Mathf.Max(0.5f, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            for (int i = 0; i < _lights.Count; i++)
            {
                var light = _lights[i];
                if (light == null) continue;
                _net.Broadcast(new LightStateMsg
                {
                    Index = (ushort)i,
                    On = IsOn(light),
                    Health = light.health,
                }, LiteNetLib.DeliveryMethod.Unreliable);
            }
        }

        public void NotifyLocalLightChanged(ShipItemLight light)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (light == null) return;
            RefreshLights(force: true);
            if (!_index.TryGetValue(light, out ushort idx)) return;

            var msg = new LightRequestMsg { Index = idx, On = IsOn(light), Health = light.health };
            _net.Broadcast(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("out #" + idx + " " + (msg.On ? "on" : "off"));
        }

        public void OnLightRequest(LightRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshLights(force: true);
            var light = GetLight(msg.Index);
            if (light == null) return;

            Apply(light, msg.On, msg.Health);
            Remember("in #" + msg.Index + " " + (msg.On ? "on" : "off"));

            _net.Broadcast(new LightStateMsg { Index = msg.Index, On = IsOn(light), Health = light.health },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        public void OnLightState(LightStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;
            RefreshLights(force: true);
            var light = GetLight(msg.Index);
            if (light == null) return;

            Apply(light, msg.On, msg.Health);
            Remember("in #" + msg.Index + " " + (msg.On ? "on" : "off"));
        }

        private void Apply(ShipItemLight light, bool on, float health)
        {
            if (light == null) return;
            light.health = Mathf.Max(0f, health);
            try
            {
                if (_miSetLight == null)
                    _miSetLight = typeof(ShipItemLight).GetMethod("SetLight", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_miSetLight != null) _miSetLight.Invoke(light, new object[] { on });
                else if (_fOn != null) _fOn.SetValue(light, on);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[LightSync] SetLight failed: " + e.Message);
            }
        }

        private static bool IsOn(ShipItemLight light)
        {
            try
            {
                if (_fOn == null)
                    _fOn = typeof(ShipItemLight).GetField("on", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fOn != null && (bool)_fOn.GetValue(light);
            }
            catch { return light != null && light.amount >= 1f; }
        }

        private ShipItemLight GetLight(ushort index)
        {
            return index < _lights.Count ? _lights[index] : null;
        }

        private void RefreshLights(float dt)
        {
            _refreshTimer += dt;
            if (_refreshTimer < 2f && _lights.Count > 0) return;
            _refreshTimer = 0f;
            RefreshLights(force: true);
        }

        private void RefreshLights(bool force)
        {
            _lights.Clear();
            _index.Clear();
            foreach (var light in UnityEngine.Object.FindObjectsOfType<ShipItemLight>())
            {
                if (light != null) _lights.Add(light);
            }
            _lights.Sort((a, b) => string.CompareOrdinal(BoatLocator.PathOf(a.transform), BoatLocator.PathOf(b.transform)));
            for (int i = 0; i < _lights.Count && i <= ushort.MaxValue; i++)
                if (_lights[i] != null) _index[_lights[i]] = (ushort)i;
        }

        private void Remember(string text)
        {
            _last = text;
            _lastTick = _net.Clock.ServerTick;
        }

        public void Clear()
        {
            _lights.Clear();
            _index.Clear();
            _refreshTimer = 0f;
            _sendTimer = 0f;
            _last = "—";
            _lastTick = 0L;
        }
    }

    public static class LightPatches
    {
        public static void Apply(Harmony harmony)
        {
            bool alt = TryPatch(harmony, "OnAltActivate", Type.EmptyTypes, nameof(PostLightChanged));
            bool item = TryPatch(harmony, "OnItemClick", new[] { typeof(PickupableItem) }, nameof(PostLightChanged));
            Plugin.Logger.LogInfo("[LightPatches] Light patches: Alt=" + alt + ", ItemClick=" + item);
        }

        private static bool TryPatch(Harmony harmony, string method, Type[] args, string postfixName)
        {
            try
            {
                var mi = typeof(ShipItemLight).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null) return false;
                var postfix = new HarmonyMethod(typeof(LightPatches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[LightPatches] " + method + ": " + e.Message);
                return false;
            }
        }

        private static void PostLightChanged(ShipItemLight __instance)
        {
            try { LightSync.Instance?.NotifyLocalLightChanged(__instance); }
            catch (Exception e) { Plugin.Logger.LogWarning("[LightPatches] PostLightChanged: " + e.Message); }
        }
    }
}
