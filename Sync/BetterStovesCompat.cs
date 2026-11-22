using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// BetterStoves (com.dixiecapt.sailwind.betterstoves) attaches a custom SoupMug component
    /// (foodPerUnit, vitPerUnit, protPerUnit, heat, spoil) when ladling soup into a mug
    /// (ShipItemBottle, amount == 16). Sync those 5 floats so every peer gets full nutrition,
    /// temperature, steam, and spoilage on shared soup mugs.
    /// </summary>
    public static class BetterStovesCompat
    {
        public const string Guid = "com.dixiecapt.sailwind.betterstoves";
        private const string AssemblyName = "BetterStoves";
        private const int MaxPending = 64;
        private const float PendingTimeout = 15f;

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.BetterStoves) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _soupMugType;
        private static MethodInfo _fillMethod;
        private static FieldInfo _fFood, _fVit, _fProt, _fHeat, _fSpoil;
        private static bool _applying;

        private struct PendingMug
        {
            public SoupMugStateMsg Msg;
            public float Time;
        }

        private static readonly Dictionary<int, PendingMug> _pending = new Dictionary<int, PendingMug>();
        private static readonly List<int> _pendingScratch = new List<int>();
        private static float _nextPendingPoll;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _soupMugType = null;
            _fillMethod = null;
            _fFood = _fVit = _fProt = _fHeat = _fSpoil = null;
            _pending.Clear();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                _soupMugType = asm?.GetType("BetterStoves.SoupMug");
                if (_soupMugType != null)
                {
                    _fillMethod = AccessTools.Method(_soupMugType, "Fill", new[] { typeof(ShipItemSoup), typeof(ShipItemBottle) });
                    _fFood = AccessTools.Field(_soupMugType, "foodPerUnit");
                    _fVit = AccessTools.Field(_soupMugType, "vitPerUnit");
                    _fProt = AccessTools.Field(_soupMugType, "protPerUnit");
                    _fHeat = AccessTools.Field(_soupMugType, "heat");
                    _fSpoil = AccessTools.Field(_soupMugType, "spoil");
                }
                SyncEnabled = _fillMethod != null && _fFood != null && _fVit != null && _fProt != null && _fHeat != null && _fSpoil != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[BetterStovesCompat] BetterStoves detected; SoupMug sync enabled.");
                else
                    Plugin.Logger.LogWarning("[BetterStovesCompat] BetterStoves detected, but SoupMug members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BetterStovesCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled || _fillMethod == null) return;
            try
            {
                harmony.Patch(_fillMethod, postfix: new HarmonyMethod(typeof(BetterStovesCompat), nameof(PostFill)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BetterStovesCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _applying = false;
            _pending.Clear();
            _nextPendingPoll = 0f;
        }

        public static void Tick()
        {
            if (_pending.Count == 0 || !Active) return;
            float now = Time.unscaledTime;
            if (now < _nextPendingPoll) return;
            _nextPendingPoll = now + 0.25f;
            _pendingScratch.Clear();
            foreach (var kv in _pending)
            {
                if (now - kv.Value.Time > PendingTimeout || TryApply(kv.Value.Msg))
                    _pendingScratch.Add(kv.Key);
            }
            for (int i = 0; i < _pendingScratch.Count; i++) _pending.Remove(_pendingScratch[i]);
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static void PostFill(ShipItemBottle __1)
        {
            try
            {
                if (_applying || !Active || __1 == null) return;
                var msg = Capture(__1);
                if (msg != null)
                    Net.SendMod(msg, ModFeatures.BetterStoves, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BetterStovesCompat] PostFill: " + e.Message);
            }
        }

        private static SoupMugStateMsg Capture(ShipItemBottle mug)
        {
            var sp = mug.GetComponent<SaveablePrefab>();
            if (sp == null || sp.instanceId == 0) return null;
            var comp = mug.GetComponent(_soupMugType);
            if (comp == null) return null;
            return new SoupMugStateMsg
            {
                InstanceId = sp.instanceId,
                FoodPerUnit = (float)_fFood.GetValue(comp),
                VitPerUnit = (float)_fVit.GetValue(comp),
                ProtPerUnit = (float)_fProt.GetValue(comp),
                Heat = (float)_fHeat.GetValue(comp),
                Spoil = (float)_fSpoil.GetValue(comp),
            };
        }

        private static bool TryApply(SoupMugStateMsg msg)
        {
            var item = ItemSync.Instance?.FindByInstanceId(msg.InstanceId) as ShipItemBottle;
            if (item == null) return false;
            _applying = true;
            try
            {
                var comp = item.GetComponent(_soupMugType) ?? item.gameObject.AddComponent(_soupMugType);
                _fFood.SetValue(comp, msg.FoodPerUnit);
                _fVit.SetValue(comp, msg.VitPerUnit);
                _fProt.SetValue(comp, msg.ProtPerUnit);
                _fHeat.SetValue(comp, msg.Heat);
                _fSpoil.SetValue(comp, msg.Spoil);
                try { item.UpdateLookText(); } catch { }
            }
            finally { _applying = false; }
            return true;
        }

        public static void OnRemote(SoupMugStateMsg msg, NetPeer fromPeer)
        {
            if (!Active || msg == null || msg.InstanceId == 0) return;
            if (!IsFinite(msg.FoodPerUnit) || !IsFinite(msg.VitPerUnit) || !IsFinite(msg.ProtPerUnit) ||
                !IsFinite(msg.Heat) || !IsFinite(msg.Spoil)) return;
            try
            {
                if (!TryApply(msg) && (_pending.ContainsKey(msg.InstanceId) || _pending.Count < MaxPending))
                    _pending[msg.InstanceId] = new PendingMug { Msg = msg, Time = Time.unscaledTime };
                if (Host)
                    Net.SendMod(msg, ModFeatures.BetterStoves, DeliveryMethod.ReliableOrdered, except: fromPeer);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BetterStovesCompat] OnRemote: " + e.Message);
            }
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !SyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.BetterStoves) == 0) return;
            try
            {
                foreach (var comp in UnityEngine.Object.FindObjectsOfType(_soupMugType))
                {
                    var mb = comp as MonoBehaviour;
                    var mug = mb != null ? mb.GetComponent<ShipItemBottle>() : null;
                    if (mug == null) continue;
                    var msg = Capture(mug);
                    if (msg != null) peer.Send(msg, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BetterStovesCompat] ResyncTo: " + e.Message);
            }
        }
    }
}
