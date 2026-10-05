using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Island house doors (game 0.39, <c>GPButtonHouseDoor</c>). A door is a toggle with a private
    /// <c>open</c> flag that the game neither saves nor exposes a setter for, so the absolute target
    /// travels and each machine toggles its copy until <c>IsOpen()</c> matches. Doors live in island
    /// scenes, not on a boat: the address is the scene index of the door's <c>SaveableObject</c> plus
    /// its ordinal among the doors sharing it. A far island may be inactive on one machine; its
    /// target is kept and applied once the door is active.
    /// </summary>
    public sealed class HouseDoorSync
    {
        public static HouseDoorSync Instance { get; private set; }

        private const float RescanSeconds = 2f;

        private readonly CoopNet _net;
        private readonly Dictionary<ushort, bool> _targets = new Dictionary<ushort, bool>();
        private readonly Dictionary<ushort, GPButtonHouseDoor> _doors = new Dictionary<ushort, GPButtonHouseDoor>();
        private readonly Dictionary<GPButtonHouseDoor, ushort> _keys = new Dictionary<GPButtonHouseDoor, ushort>();
        private float _scannedAt = float.NegativeInfinity;

        public HouseDoorSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        private bool Connected => _net.State == LinkState.Connected;

        public void Tick(float dt)
        {
            if (!Connected || _targets.Count == 0) return;
            if (!GameState.playing || GameState.currentlyLoading) return;
            foreach (var target in _targets)
            {
                var door = Find(target.Key);
                if (door == null || !door.gameObject.activeInHierarchy || door.IsOpen() == target.Value) continue;
                // A door in motion or a locked house ignores the call; the next tick asks again.
                using (InteractionContext.Begin(InteractionSource.RemoteApply))
                {
                    try { door.OnActivate(); }
                    catch (Exception e) { Plugin.Logger.LogWarning("[HouseDoorSync] door=" + target.Key + " apply: " + e.Message); }
                }
            }
        }

        /// <summary>The local player's click changed a door.</summary>
        internal void LocalChanged(GPButtonHouseDoor door, bool open)
        {
            if (!Connected || door == null) return;
            if (!_keys.TryGetValue(door, out ushort key))
            {
                Scan();
                if (!_keys.TryGetValue(door, out key)) return;
            }
            _targets[key] = open;
            _net.Broadcast(new HouseDoorMsg { Door = key, Open = open }, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[HouseDoorSync] role=" + _net.Role + " out door=" + key + " open=" + open);
        }

        public void OnHouseDoor(HouseDoorMsg msg, NetPeer fromPeer)
        {
            if (_net.Role == Role.Host)
            {
                if (_net.PlayerNetIdForPeer(fromPeer) == 0) return;
                _targets[msg.Door] = msg.Open;
                _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            }
            else if (_net.IsHostPeer(fromPeer)) _targets[msg.Door] = msg.Open;
        }

        /// <summary>Host: doors that differ from the closed state a freshly loaded world starts with.</summary>
        public void SendBaseline(NetPeer peer)
        {
            if (_net.Role != Role.Host || peer == null) return;
            Scan();
            foreach (var pair in _doors)
            {
                if (pair.Value == null) continue;
                bool open = _targets.TryGetValue(pair.Key, out bool target) ? target : pair.Value.IsOpen();
                if (open) peer.Send(new HouseDoorMsg { Door = pair.Key, Open = true }, DeliveryMethod.ReliableOrdered);
            }
        }

        public void Clear()
        {
            _targets.Clear();
            _doors.Clear();
            _keys.Clear();
            _scannedAt = float.NegativeInfinity;
        }

        private GPButtonHouseDoor Find(ushort key)
        {
            if (_doors.TryGetValue(key, out var door) && door != null) return door;
            if (Time.unscaledTime - _scannedAt < RescanSeconds) return null;
            Scan();
            return _doors.TryGetValue(key, out door) ? door : null;
        }

        private void Scan()
        {
            _scannedAt = Time.unscaledTime;
            _doors.Clear();
            _keys.Clear();
            var groups = new Dictionary<int, List<KeyValuePair<string, GPButtonHouseDoor>>>();
            // Inactive islands are included: FindObjectsOfType would skip their doors.
            foreach (var door in Resources.FindObjectsOfTypeAll<GPButtonHouseDoor>())
            {
                if (door == null || !door.gameObject.scene.IsValid()) continue;
                int scene = door.saveable != null ? door.saveable.sceneIndex : byte.MaxValue;
                if (scene < 0 || scene > byte.MaxValue) continue;
                if (!groups.TryGetValue(scene, out var list))
                    groups[scene] = list = new List<KeyValuePair<string, GPButtonHouseDoor>>();
                list.Add(new KeyValuePair<string, GPButtonHouseDoor>(OrderKey(door.transform), door));
            }
            foreach (var group in groups)
            {
                group.Value.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                for (int i = 0; i < group.Value.Count && i <= byte.MaxValue; i++)
                {
                    ushort key = (ushort)((group.Key << 8) | i);
                    _doors[key] = group.Value[i].Value;
                    _keys[group.Value[i].Value] = key;
                }
            }
        }

        // Scene data only, so both machines order the doors of one house identically.
        private static string OrderKey(Transform door)
        {
            var names = new StringBuilder();
            var siblings = new StringBuilder();
            for (var t = door; t != null; t = t.parent)
            {
                names.Insert(0, "/" + t.name);
                if (t.parent != null) siblings.Insert(0, "/" + t.GetSiblingIndex().ToString("D5"));
            }
            Vector3 p = door.localPosition;
            return names + "|" + p.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   p.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   p.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "|" + siblings;
        }
    }

    internal static class HouseDoorPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(GPButtonHouseDoor), "OnActivate", Type.EmptyTypes, m => harmony.Patch(m,
                prefix: Callback(nameof(PreActivate)), postfix: Callback(nameof(PostActivate))));
            PatchHealth.Report("House doors", hooks);
            Plugin.Logger.LogInfo("[HouseDoorPatches] role=initializing " + hooks.Detail);
        }
        private static HarmonyMethod Callback(string name)
            => new HarmonyMethod(typeof(HouseDoorPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error)
        {
            try { Plugin.Logger?.LogWarning("[HouseDoorPatches] " + error); } catch { }
        }
        private static void PreActivate(GPButtonHouseDoor __instance, out bool __state)
        {
            bool open = false;
            PatchGuard.Run(() => open = __instance != null && __instance.IsOpen(), Report);
            __state = open;
        }
        private static void PostActivate(GPButtonHouseDoor __instance, bool __state)
            => PatchGuard.Run(() => {
                // Only a real click that actually moved the door is an interaction.
                if (__instance == null || InteractionContext.Suppressed || !InteractionContext.HasInput ||
                    __instance.IsOpen() == __state) return;
                HouseDoorSync.Instance?.LocalChanged(__instance, __instance.IsOpen());
            }, Report);
    }
}
