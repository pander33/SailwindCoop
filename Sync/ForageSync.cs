using System;
using System.Collections.Generic;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Forage spots (game 0.40: <c>WorldItemSpawner</c>, mushrooms and coconuts on islands).
    /// <para>The game spawns the spot's item on every machine for itself (sold, not in the save,
    /// frozen) and keeps a respawn time per spot in <c>ItemSpawners.cooldowns</c>, which is saved.
    /// The item enters the save only when it is taken in hand: <c>WorldItemSpawner.Update</c> sees
    /// <c>item.held</c>, writes the cooldown, registers the item and forgets it. Until then the item
    /// has no instance id, so <see cref="ItemSync"/> does not see it; the copies on each machine
    /// stand at different places (the spot's randomizer is local) and that is left as it is.</para>
    /// <para>On a pickup the taker sends <see cref="ForageTakenMsg"/>: the others remove their item
    /// of that spot and take the taker's cooldown, so the spot respawns together. The picked item
    /// becomes shared the usual way — a client's through <see cref="ItemSync.NotifyClientAuthored"/>,
    /// as a bought good; the host's through its own item scan.</para>
    /// <para>A client that is still loading the host's save would lose a take made after that save
    /// was written (the load writes the saved cooldowns back), so a client keeps the takes it
    /// received and writes them again after <c>SaveLoadManager.LoadGame</c>.</para>
    /// <para>Two players picking the same spot within one round trip keep one item each.</para>
    /// </summary>
    public sealed class ForageSync
    {
        public static ForageSync Instance { get; private set; }

        private readonly CoopNet _net;
        /// <summary>Client: takes received in this session, by spot, until the next save load.</summary>
        private readonly Dictionary<int, float> _receivedTakes = new Dictionary<int, float>();

        public ForageSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>A session is up: forage spots are kept in step.</summary>
        internal bool Active => _net.Role != Role.None && _net.State == LinkState.Connected;

        /// <summary>The local player took the item of a forage spot (called after the game's Update).</summary>
        internal void OnLocalTaken(int spawner, ShipItem item)
        {
            if (!Active) return;
            if (!ForagePatches.TryGetCooldown(spawner, out float cooldown)) return;
            _net.Broadcast(new ForageTakenMsg { Spawner = spawner, Cooldown = cooldown },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            if (item != null) ItemSync.Instance?.NotifyClientAuthored(item);
            Log("took spot=" + spawner + " cooldown=" + cooldown.ToString("0") +
                (item != null ? " '" + item.name + "'" : ""));
        }

        public void OnForageTaken(ForageTakenMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            try
            {
                if (_net.Role == Role.Host) _net.RelayExcept(msg, fromPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
                if (_net.Role == Role.Client) _receivedTakes[msg.Spawner] = msg.Cooldown;
                if (!ForagePatches.SetCooldown(msg.Spawner, msg.Cooldown))
                {
                    Plugin.Logger.LogInfo("[ForageSync] role=" + _net.Role + " no spot " + msg.Spawner);
                    return;
                }
                string removed = ForagePatches.RemoveSpawned(msg.Spawner);
                Log("spot=" + msg.Spawner + " taken elsewhere, cooldown=" + msg.Cooldown.ToString("0") +
                    (removed != null ? ", " + removed : ""));
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ForageSync] OnForageTaken: " + e.Message); }
        }

        /// <summary>
        /// Client: the game has just loaded a save and written its cooldowns. A take received before
        /// that came after the host wrote the save; write it again. Held only until this load:
        /// a session loads the host's save once.
        /// </summary>
        internal void AfterSaveLoaded()
        {
            if (_net.Role != Role.Client || _receivedTakes.Count == 0) return;
            int applied = 0;
            foreach (var take in _receivedTakes)
                if (ForagePatches.SetCooldown(take.Key, take.Value)) applied++;
            Log("save loaded: wrote " + applied + " of " + _receivedTakes.Count + " takes received before it");
            _receivedTakes.Clear();
        }

        public void Clear()
        {
            _receivedTakes.Clear();
            ForagePatches.ClearDeferred();
        }

        private static void Log(string text) => Plugin.Logger.LogInfo("[ForageSync] " + text);
    }

    /// <summary>
    /// Harmony bridge for <see cref="ForageSync"/>: a prefix/postfix pair on
    /// <c>WorldItemSpawner.Update</c> notices the moment the game lets go of a held spot item, and
    /// a postfix of <c>SaveLoadManager.LoadGame</c> writes back takes a client received early.
    /// </summary>
    public static class ForagePatches
    {
        private static Type _spawnerType;
        private static AccessTools.FieldRef<object, ShipItem> _item;
        private static AccessTools.FieldRef<object, int> _index;

        /// <summary>
        /// Spots whose item was taken elsewhere in the frame the item appeared here. The game's own
        /// <c>FreezeItem</c> coroutine reads the item at the end of that frame, so it is removed on
        /// a later Update of the spot instead.
        /// </summary>
        private static readonly HashSet<int> _deferred = new HashSet<int>();

        public static void Apply(Harmony harmony)
        {
            _spawnerType = AccessTools.TypeByName("WorldItemSpawner");
            if (_spawnerType == null) throw new MissingMemberException("WorldItemSpawner (game 0.40 forage spots)");
            _item = AccessTools.FieldRefAccess<ShipItem>(_spawnerType, "item");
            _index = AccessTools.FieldRefAccess<int>(_spawnerType, "itemSpawnerIndex");
            var update = AccessTools.Method(_spawnerType, "Update");
            if (update == null) throw new MissingMethodException("WorldItemSpawner.Update");
            harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(ForagePatches), nameof(PreUpdate)),
                postfix: new HarmonyMethod(typeof(ForagePatches), nameof(PostUpdate)));

            bool load = false;
            try
            {
                var loadGame = AccessTools.Method(typeof(SaveLoadManager), "LoadGame", new[] { typeof(int) });
                if (loadGame != null)
                {
                    harmony.Patch(loadGame, postfix: new HarmonyMethod(typeof(ForagePatches), nameof(PostLoadGame)));
                    load = true;
                }
            }
            catch (Exception e) { Warn("LoadGame: " + e.Message); }

            Plugin.Logger.LogInfo("[ForagePatches] WorldItemSpawner.Update patched, LoadGame=" + load);
            SailwindCoop.Runtime.PatchHealth.Report("Forage", load ? 2 : 1, 2);
        }

        private static void PreUpdate(object __instance, out ShipItem __state)
        {
            __state = null;
            try
            {
                var sync = ForageSync.Instance;
                if (sync == null || !sync.Active) return;
                var item = _item(__instance);
                if (item == null) return;
                if (_deferred.Count > 0 && _deferred.Remove(_index(__instance)) && item.held == null)
                {
                    _item(__instance) = null;
                    UnityEngine.Object.Destroy(item.gameObject);
                    return;
                }
                if (item.held != null) __state = item;
            }
            catch (Exception e) { Warn("PreUpdate: " + e.Message); }
        }

        private static void PostUpdate(object __instance, ShipItem __state)
        {
            try
            {
                if (__state == null) return;
                if (_item(__instance) != null) return;   // the game did not let go
                ForageSync.Instance?.OnLocalTaken(_index(__instance), __state);
            }
            catch (Exception e) { Warn("PostUpdate: " + e.Message); }
        }

        private static void PostLoadGame()
        {
            try { ForageSync.Instance?.AfterSaveLoaded(); }
            catch (Exception e) { Warn("PostLoadGame: " + e.Message); }
        }

        internal static bool TryGetCooldown(int spawner, out float cooldown)
        {
            cooldown = 0f;
            var cooldowns = ItemSpawners.instance != null ? ItemSpawners.instance.cooldowns : null;
            if (cooldowns == null || spawner < 0 || spawner >= cooldowns.Length) return false;
            cooldown = cooldowns[spawner];
            return true;
        }

        internal static bool SetCooldown(int spawner, float cooldown)
        {
            var cooldowns = ItemSpawners.instance != null ? ItemSpawners.instance.cooldowns : null;
            if (cooldowns == null || spawner < 0 || spawner >= cooldowns.Length) return false;
            cooldowns[spawner] = cooldown;
            return true;
        }

        /// <summary>
        /// Removes this machine's item of the spot, unless the local player holds it (then both
        /// players picked it; each keeps one). Returns what was done, or null when nothing was there.
        /// </summary>
        internal static string RemoveSpawned(int spawner)
        {
            if (_spawnerType == null || _item == null) return null;
            foreach (var spot in UnityEngine.Object.FindObjectsOfType(_spawnerType))
            {
                if (_index(spot) != spawner) continue;
                var item = _item(spot);
                if (item == null || item.held != null) return null;
                // Not frozen yet: it appeared in this frame and the game's FreezeItem still reads it.
                var body = item.GetItemRigidbody();
                if (body != null && !body.debugForceKinematic)
                {
                    _deferred.Add(spawner);
                    return "removal of '" + item.name + "' deferred to the next frame";
                }
                _item(spot) = null;
                string name = item.name;
                UnityEngine.Object.Destroy(item.gameObject);
                return "removed '" + name + "'";
            }
            return null;
        }

        internal static void ClearDeferred() => _deferred.Clear();

        private static void Warn(string text) => Plugin.Logger.LogWarning("[ForagePatches] " + text);
    }
}
