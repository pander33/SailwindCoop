using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Viking Horn (Viking-D-Mike, com.hornmod.vikinghorn) adds a horn sold at Siren Song. It is an ordinary ShipItem,
    /// so item sync already carries it, and the raise to the mouth is a change of hold offsets that the held item pose
    /// stream already shows. The sound is not: HornItem plays its own AudioSource while the local player holds the
    /// activate key, and on every other machine the horn is in no hand there, so it stays silent.
    ///
    /// We send "blowing" and "stopped" on the MusicMod instrument packet (kind Horn). The receiver plays the horn's
    /// AudioSource once per blow, like HornItem does, and skips HornItem.ExtraLateUpdate while the crewmate blows,
    /// because that method fades out any horn that is not in a hand here. On "stopped" it runs again and does its own
    /// fade out.
    ///
    /// Built against 1.0.1. All reflected, because the mod may be absent, and a miss turns this off with a warning.
    /// </summary>
    public static class VikingHornCompat
    {
        public const string Guid = "com.hornmod.vikinghorn";
        private const string AssemblyName = "HornMod";

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _hornType;          // HornItem (global namespace)
        private static MethodInfo _lateUpdate;  // public override void ExtraLateUpdate()
        private static FieldInfo _using;        // private bool usando: true for a frame the player is blowing
        private static FieldInfo _baseVolume;   // private float volumenOriginal

        // Horns a crewmate is blowing, with the time we last heard about it.
        private class RemoteBlow
        {
            public uint Actor;
            public float Since;
        }

        private static readonly Dictionary<ShipItem, RemoteBlow> _remote = new Dictionary<ShipItem, RemoteBlow>();
        private static readonly HashSet<ShipItem> _sentBlowing = new HashSet<ShipItem>();
        private static readonly List<ShipItem> _scratch = new List<ShipItem>();

        // Same grace as MusicMod: a blow outlives the horn leaving its player's hands by at most this long.
        private const float BlowGrace = 2f;
        private const float TickInterval = 0.5f;
        private static float _nextTick;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Logger.LogInfo("[HornCompat] Viking Horn not installed.");
                    return;
                }
                IsInstalled = true;
                string version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);

                _hornType = asm?.GetType("HornItem");
                _lateUpdate = _hornType != null ? AccessTools.DeclaredMethod(_hornType, "ExtraLateUpdate", Type.EmptyTypes) : null;
                _using = _hornType != null ? AccessTools.Field(_hornType, "usando") : null;
                _baseVolume = _hornType != null ? AccessTools.Field(_hornType, "volumenOriginal") : null;

                SyncEnabled = _lateUpdate != null && _using != null && _baseVolume != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo($"[HornCompat] Viking Horn v{version} detected; horn sound sync enabled.");
                else
                    Plugin.Logger.LogWarning($"[HornCompat] Viking Horn v{version} detected, but its members did not resolve. " +
                        "Horn sound sync disabled, so crewmates will not hear each other's horn. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[HornCompat] Detection threw. Horn sound sync disabled. " + e);
            }
        }

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                harmony.Patch(_lateUpdate, prefix: new HarmonyMethod(typeof(VikingHornCompat), nameof(LateUpdatePrefix)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[HornCompat] Patching threw. Horn sound sync disabled. " + e);
            }
        }

        public static void Reset()
        {
            _remote.Clear();
            _sentBlowing.Clear();
        }

        // Runs before HornItem clears usando at the end of ExtraLateUpdate, so it sees this frame's blowing.
        // Returning false keeps a crewmate's horn sounding here.
        private static bool LateUpdatePrefix(ShipItem __instance)
        {
            try { return ReadHorn(__instance); }
            catch (Exception error) { PatchGuard.Fail(Report, error); return true; }
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[HornCompat] " + error.Message);

        private static bool ReadHorn(ShipItem __instance)
        {
            if (__instance == null || !CrewMusic.Active) return true;

            if (__instance.held != null)
            {
                _remote.Remove(__instance);
                bool blowing = false;
                try { blowing = (bool)_using.GetValue(__instance); } catch { }
                if (blowing != _sentBlowing.Contains(__instance) && Send(__instance, blowing))
                {
                    if (blowing) _sentBlowing.Add(__instance); else _sentBlowing.Remove(__instance);
                }
                return true;
            }

            // Left our hands while blowing (dropped, stowed): tell the crew it stopped.
            if (_sentBlowing.Remove(__instance)) Send(__instance, false);

            return !_remote.ContainsKey(__instance);
        }

        private static bool Send(ShipItem horn, bool blowing)
            => CrewMusic.Send(horn, CrewNoteKind.Horn, -1, blowing);

        /// <summary>Called by MusicModCompat.OnRemoteNote, which has already relayed the packet on the host.</summary>
        public static void OnRemoteHorn(CrewNoteMsg packet)
        {
            if (!SyncEnabled) return;
            var item = ItemSync.Instance?.FindSharedItem(packet.ItemInstanceId, packet.PrefabIndex);
            if (item == null || !_hornType.IsInstanceOfType(item)) return;
            if (item.held != null) { _remote.Remove(item); return; }

            if (!packet.Playing)
            {
                if (_remote.TryGetValue(item, out var ending) && ending.Actor == packet.Actor) _remote.Remove(item);
                return;
            }

            if (_remote.TryGetValue(item, out var current) && current.Actor == packet.Actor) return;
            _remote[item] = new RemoteBlow { Actor = packet.Actor, Since = Time.unscaledTime };
            var sound = item.GetComponent<AudioSource>();
            if (sound == null) return;
            try { sound.volume = (float)_baseVolume.GetValue(item); } catch { }
            if (!sound.isPlaying) sound.Play();
        }

        /// <summary>Drops blows whose horn is gone, came into our hands, or is no longer in a crewmate's hands.</summary>
        public static void Tick()
        {
            if (_remote.Count == 0 || Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + TickInterval;

            _scratch.Clear();
            foreach (var kvp in _remote)
            {
                if (kvp.Key == null || kvp.Key.held != null) { _scratch.Add(kvp.Key); continue; }
                bool carried = ItemSync.Instance?.HeldByRemotePlayer(kvp.Key, kvp.Value.Actor) == true;
                if (!carried && Time.unscaledTime - kvp.Value.Since > BlowGrace) _scratch.Add(kvp.Key);
            }
            foreach (var item in _scratch) _remote.Remove(item);
            _scratch.Clear();
        }

    }
}
