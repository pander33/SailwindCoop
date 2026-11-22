using System;
using System.Collections;
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
    /// BetterNPCBoats (com.raddude.betternpcboats) replaces NPCBoatController on 30 pooled regional
    /// fishing boats with BetterNPCFishingBoat, driven by independent RNG timeAdjustment and local
    /// Rigidbody physics.
    /// In co-op:
    /// - Host simulates all active fishing boats and broadcasts batched FishingBoatStateMsg at 5 Hz
    ///   (with 1 s keepalive) keyed by deterministic (SceneIndex, SlotIndex).
    /// - Client suppresses BetterNPCFishingBoat.FixedUpdate/Update, keeps slaved bodies kinematic
    ///   (cooperating with NpcBoatPatches.PreUpdateKinematic), interpolates poses via NetTransform +
    ///   PlayerPlaybackClock, and mirrors lanterns, pilot/fisherman visibility and sail animations.
    /// </summary>
    public static class BetterNpcBoatsCompat
    {
        public const string Guid = "com.raddude.betternpcboats";
        private const string AssemblyName = "BetterNPCBoats";

        private const byte FlagGoingFishing = 1 << 0;
        private const byte FlagAtFishingSpot = 1 << 1;
        private const byte FlagAtParkingSpot = 1 << 2;
        private const byte FlagLanternsOn = 1 << 3;
        private const byte FlagHasTarget = 1 << 4;

        private const float SendInterval = 0.2f;
        private const float KeepaliveSeconds = 1.0f;
        private const float MinPosDeltaSq = 0.04f;
        private const float MinRotDeltaDeg = 0.5f;

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool ClientActive => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            !Host && (Net.HostModFeatures & ModFeatures.BetterNpcBoats) != 0;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.BetterNpcBoats) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _boatType;
        private static FieldInfo _fActiveBoats;
        private static FieldInfo _fSceneDict;
        private static FieldInfo _fSceneIndex;
        private static FieldInfo _fParkedPos;
        private static FieldInfo _fGoingFishing;
        private static FieldInfo _fAtFishingSpot;
        private static FieldInfo _fAtParkingSpot;
        private static FieldInfo _fLanternsOn;
        private static FieldInfo _fCurrentTarget;
        private static FieldInfo _fPilot;
        private static FieldInfo _fFisherman;
        private static FieldInfo _fSailSpeed;
        private static FieldInfo _fSailAngle;
        private static FieldInfo _fSailReef;
        private static FieldInfo _fDataParkedPos;
        private static MethodInfo _mFixedUpdate;
        private static MethodInfo _mUpdate;
        private static MethodInfo _mToggleLanterns;

        private struct HostSent
        {
            public Vector3 RealPos;
            public Quaternion Rot;
            public byte Flags;
            public float SentAt;
        }

        private sealed class ClientBoat
        {
            public Component Boat;
            public Transform Transform;
            public Rigidbody Body;
            public bool PrevKinematic;
            public RigidbodyInterpolation PrevInterp;
            public NetTransform Net = new NetTransform();
            public PlayerPlaybackClock Playback = new PlayerPlaybackClock();
            public long LatestTick = long.MinValue;
            public byte Flags;
            public bool FlagsApplied;
        }

        private static readonly Dictionary<int, HostSent> _hostSent = new Dictionary<int, HostSent>();
        private static readonly Dictionary<int, ClientBoat> _clientBoats = new Dictionary<int, ClientBoat>();
        private static readonly HashSet<Transform> _slavedBodies = new HashSet<Transform>();
        private static readonly List<FishingBoatEntry> _sendScratch = new List<FishingBoatEntry>();
        private static readonly List<int> _removeScratch = new List<int>();
        private static float _sendAccum;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _boatType = null;
            _fActiveBoats = _fSceneDict = _fSceneIndex = _fParkedPos = null;
            _fGoingFishing = _fAtFishingSpot = _fAtParkingSpot = _fLanternsOn = _fCurrentTarget = null;
            _fPilot = _fFisherman = _fSailSpeed = _fSailAngle = _fSailReef = _fDataParkedPos = null;
            _mFixedUpdate = _mUpdate = _mToggleLanterns = null;

            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                if (asm == null) return;

                _boatType = asm.GetType("BetterNPCBoats.BetterNPCFishingBoat");
                var scenesType = asm.GetType("BetterNPCBoats.FishingBoatScenes");
                var dataType = asm.GetType("BetterNPCBoats.ActiveFBData");
                if (_boatType == null || scenesType == null || dataType == null) return;

                _fActiveBoats = AccessTools.Field(scenesType, "activeFishingBoats");
                _fSceneDict = AccessTools.Field(scenesType, "fishingBoatScenes");
                _fSceneIndex = AccessTools.Field(_boatType, "sceneIndex");
                _fParkedPos = AccessTools.Field(_boatType, "parkedPos");
                _fGoingFishing = AccessTools.Field(_boatType, "goingFishing");
                _fAtFishingSpot = AccessTools.Field(_boatType, "atFishingSpot");
                _fAtParkingSpot = AccessTools.Field(_boatType, "atParkingSpot");
                _fLanternsOn = AccessTools.Field(_boatType, "lanternsOn");
                _fCurrentTarget = AccessTools.Field(_boatType, "currentTarget");
                _fPilot = AccessTools.Field(_boatType, "pilot");
                _fFisherman = AccessTools.Field(_boatType, "fisherman");
                _fSailSpeed = AccessTools.Field(_boatType, "sailSpeed");
                _fSailAngle = AccessTools.Field(_boatType, "sailAngleControllers");
                _fSailReef = AccessTools.Field(_boatType, "sailReefControllers");
                _fDataParkedPos = AccessTools.Field(dataType, "position");
                _mFixedUpdate = AccessTools.Method(_boatType, "FixedUpdate", Type.EmptyTypes);
                _mUpdate = AccessTools.Method(_boatType, "Update", Type.EmptyTypes);
                _mToggleLanterns = AccessTools.Method(_boatType, "ToggleLanterns", new[] { typeof(bool) });

                SyncEnabled = _fActiveBoats != null && _fSceneDict != null && _fSceneIndex != null &&
                              _fParkedPos != null && _fDataParkedPos != null && _mFixedUpdate != null && _mUpdate != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[BetterNpcBoatsCompat] BetterNPCBoats detected; fishing boat sync enabled.");
                else
                    Plugin.Logger.LogWarning("[BetterNpcBoatsCompat] BetterNPCBoats detected, but required members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BetterNpcBoatsCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                harmony.Patch(_mFixedUpdate, prefix: new HarmonyMethod(typeof(BetterNpcBoatsCompat), nameof(PreClientSkip)));
                harmony.Patch(_mUpdate, prefix: new HarmonyMethod(typeof(BetterNpcBoatsCompat), nameof(PreClientSkip)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BetterNpcBoatsCompat] Patching threw: " + e.Message);
            }
        }

        private static bool PreClientSkip() => !ClientActive;

        public static bool IsSlaved(Transform body) => body != null && _slavedBodies.Contains(body);

        public static void Reset()
        {
            try
            {
                foreach (var cb in _clientBoats.Values)
                {
                    if (cb.Body != null)
                    {
                        cb.Body.isKinematic = cb.PrevKinematic;
                        cb.Body.interpolation = cb.PrevInterp;
                    }
                }
            }
            catch { }
            _clientBoats.Clear();
            _slavedBodies.Clear();
            _hostSent.Clear();
            _sendAccum = 0f;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        private static bool IsFinite(Quaternion q) => IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w);

        private static int MakeKey(ushort sceneIndex, byte slotIndex) => (sceneIndex << 8) | slotIndex;

        private static bool TryResolveSlot(int sceneIndex, Vector3 parkedPos, out byte slotIndex)
        {
            slotIndex = 0;
            var dict = _fSceneDict.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(sceneIndex)) return false;
            var list = dict[sceneIndex] as IList;
            if (list == null) return false;
            float bestSq = float.MaxValue;
            int best = -1;
            for (int i = 0; i < list.Count && i < 256; i++)
            {
                var item = list[i];
                if (item == null || _fDataParkedPos == null) continue;
                // ActiveFBData.position packs (x, yawDegrees, z); BetterNPCFishingBoat.parkedPos is (x, 0, z).
                Vector3 p = (Vector3)_fDataParkedPos.GetValue(item);
                float dx = p.x - parkedPos.x;
                float dz = p.z - parkedPos.z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            if (best < 0 || bestSq > 1f) return false;
            slotIndex = (byte)best;
            return true;
        }

        public static void Tick(float dt)
        {
            if (!Active || !GameState.playing || GameState.currentlyLoading) return;
            if (Host) TickHost(dt);
            else ApplyRemote(dt);
        }

        private static void TickHost(float dt)
        {
            _sendAccum += dt;
            if (_sendAccum < SendInterval) return;
            _sendAccum = 0f;
            try
            {
                var active = _fActiveBoats.GetValue(null) as IList;
                if (active == null || active.Count == 0) return;
                float now = Time.unscaledTime;
                _sendScratch.Clear();
                for (int i = 0; i < active.Count && _sendScratch.Count < FishingBoatStateMsg.MaxBoats; i++)
                {
                    var go = active[i] as GameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    var comp = go.GetComponent(_boatType);
                    if (comp == null) continue;
                    int scene = (int)_fSceneIndex.GetValue(comp);
                    if (scene < 0 || scene > ushort.MaxValue) continue;
                    Vector3 parked = (Vector3)_fParkedPos.GetValue(comp);
                    if (!TryResolveSlot(scene, parked, out byte slot)) continue;

                    Vector3 realPos = CoordSpace.LocalToReal(go.transform.position);
                    Quaternion rot = go.transform.rotation;
                    var rb = go.GetComponent<Rigidbody>();
                    Vector3 vel = rb != null ? rb.velocity * Mathf.Max(0f, Time.timeScale) : Vector3.zero;
                    byte flags = PackFlags(comp);

                    int key = MakeKey((ushort)scene, slot);
                    if (_hostSent.TryGetValue(key, out var prev) &&
                        now - prev.SentAt < KeepaliveSeconds &&
                        prev.Flags == flags &&
                        (prev.RealPos - realPos).sqrMagnitude < MinPosDeltaSq &&
                        Quaternion.Angle(prev.Rot, rot) < MinRotDeltaDeg)
                        continue;

                    _hostSent[key] = new HostSent { RealPos = realPos, Rot = rot, Flags = flags, SentAt = now };
                    _sendScratch.Add(new FishingBoatEntry
                    {
                        SceneIndex = (ushort)scene,
                        SlotIndex = slot,
                        RealPos = realPos,
                        Rot = rot,
                        RealVel = vel,
                        Flags = flags,
                    });
                }
                if (_sendScratch.Count == 0) return;
                Net.SendMod(new FishingBoatStateMsg
                {
                    Tick = Net.Clock.ServerTick,
                    Boats = _sendScratch.ToArray(),
                }, ModFeatures.BetterNpcBoats, DeliveryMethod.Unreliable);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BetterNpcBoatsCompat] TickHost: " + e.Message);
            }
        }

        private static byte PackFlags(Component comp)
        {
            byte f = 0;
            if (_fGoingFishing != null && (bool)_fGoingFishing.GetValue(comp)) f |= FlagGoingFishing;
            if (_fAtFishingSpot != null && (bool)_fAtFishingSpot.GetValue(comp)) f |= FlagAtFishingSpot;
            if (_fAtParkingSpot != null && (bool)_fAtParkingSpot.GetValue(comp)) f |= FlagAtParkingSpot;
            if (_fLanternsOn != null && (bool)_fLanternsOn.GetValue(comp)) f |= FlagLanternsOn;
            if (_fCurrentTarget != null && (_fCurrentTarget.GetValue(comp) as UnityEngine.Object) != null) f |= FlagHasTarget;
            return f;
        }

        public static void OnRemote(FishingBoatStateMsg msg)
        {
            if (!ClientActive || msg == null || msg.Boats == null || !GameState.playing || GameState.currentlyLoading) return;
            try
            {
                double localNow = Net.Clock.LocalTimeMs;
                double serverNow = localNow + Net.Clock.OffsetMs;
                for (int i = 0; i < msg.Boats.Length; i++)
                {
                    var e = msg.Boats[i];
                    if (e == null || !IsFinite(e.RealPos) || !IsFinite(e.RealVel) || !IsFinite(e.Rot)) continue;
                    var cb = Engage(e.SceneIndex, e.SlotIndex);
                    if (cb == null) continue;
                    cb.Net.Push(msg.Tick, e.RealPos, e.Rot, e.RealVel);
                    if (msg.Tick <= cb.LatestTick) continue;
                    cb.LatestTick = msg.Tick;
                    cb.Playback.Observe(msg.Tick, serverNow, localNow);
                    if (!cb.FlagsApplied || cb.Flags != e.Flags)
                        ApplyFlags(cb, e.Flags);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BetterNpcBoatsCompat] OnRemote: " + e.Message);
            }
        }

        private static ClientBoat Engage(ushort sceneIndex, byte slotIndex)
        {
            int key = MakeKey(sceneIndex, slotIndex);
            if (_clientBoats.TryGetValue(key, out var existing))
            {
                if (existing.Boat != null && existing.Transform != null && existing.Transform.gameObject.activeInHierarchy &&
                    (int)_fSceneIndex.GetValue(existing.Boat) == sceneIndex)
                    return existing;
                if (existing.Transform != null) _slavedBodies.Remove(existing.Transform);
                if (existing.Body != null)
                {
                    existing.Body.isKinematic = existing.PrevKinematic;
                    existing.Body.interpolation = existing.PrevInterp;
                }
                _clientBoats.Remove(key);
            }

            var active = _fActiveBoats.GetValue(null) as IList;
            if (active == null) return null;
            for (int i = 0; i < active.Count; i++)
            {
                var go = active[i] as GameObject;
                if (go == null || !go.activeInHierarchy) continue;
                var comp = go.GetComponent(_boatType);
                if (comp == null || (int)_fSceneIndex.GetValue(comp) != sceneIndex) continue;
                Vector3 parked = (Vector3)_fParkedPos.GetValue(comp);
                if (!TryResolveSlot(sceneIndex, parked, out byte slot) || slot != slotIndex) continue;

                var rb = go.GetComponent<Rigidbody>();
                var cb = new ClientBoat
                {
                    Boat = comp,
                    Transform = go.transform,
                    Body = rb,
                    PrevKinematic = rb != null && rb.isKinematic,
                    PrevInterp = rb != null ? rb.interpolation : RigidbodyInterpolation.None,
                };
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.interpolation = RigidbodyInterpolation.None;
                }
                _clientBoats[key] = cb;
                _slavedBodies.Add(go.transform);
                return cb;
            }
            return null;
        }

        private static void ApplyFlags(ClientBoat cb, byte flags)
        {
            cb.Flags = flags;
            cb.FlagsApplied = true;
            var comp = cb.Boat;
            bool atFishing = (flags & FlagAtFishingSpot) != 0;
            bool atParking = (flags & FlagAtParkingSpot) != 0;
            bool lanterns = (flags & FlagLanternsOn) != 0;
            if (_mToggleLanterns != null) _mToggleLanterns.Invoke(comp, new object[] { lanterns });
            var pilot = _fPilot?.GetValue(comp) as GameObject;
            var fisherman = _fFisherman?.GetValue(comp) as GameObject;
            if (pilot != null) pilot.SetActive(!atFishing && !atParking);
            if (fisherman != null) fisherman.SetActive(atFishing);
        }

        private static void ApplyRemote(float dt)
        {
            if (_clientBoats.Count == 0 || Time.timeScale <= 0.0001f) return;
            try
            {
                double localNow = Net.Clock.LocalTimeMs;
                double serverNow = localNow + Net.Clock.OffsetMs;
                float windMag = Wind.currentWind.magnitude;
                _removeScratch.Clear();
                foreach (var kv in _clientBoats)
                {
                    var cb = kv.Value;
                    if (cb.Boat == null || cb.Transform == null || !cb.Transform.gameObject.activeInHierarchy)
                    {
                        _removeScratch.Add(kv.Key);
                        continue;
                    }
                    if (cb.Body != null && !cb.Body.isKinematic) cb.Body.isKinematic = true;
                    if (cb.Net.HasData)
                    {
                        double renderTick = cb.Playback.AdvanceExact(serverNow, localNow, 225f);
                        cb.Net.ApplyAt(cb.Transform, renderTick);
                    }
                    AnimateSails(cb, dt, windMag);
                }
                for (int i = 0; i < _removeScratch.Count; i++)
                {
                    int k = _removeScratch[i];
                    if (_clientBoats.TryGetValue(k, out var dead))
                    {
                        if (dead.Transform != null) _slavedBodies.Remove(dead.Transform);
                        if (dead.Body != null)
                        {
                            dead.Body.isKinematic = dead.PrevKinematic;
                            dead.Body.interpolation = dead.PrevInterp;
                        }
                    }
                    _clientBoats.Remove(k);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BetterNpcBoatsCompat] ApplyRemote: " + e.Message);
            }
        }

        private static void AnimateSails(ClientBoat cb, float dt, float windMag)
        {
            bool hasTarget = (cb.Flags & FlagHasTarget) != 0;
            var reefs = _fSailReef?.GetValue(cb.Boat) as RopeControllerSailReef[];
            if (reefs != null)
            {
                float step = (hasTarget ? 0.15f : -0.15f) * dt;
                for (int i = 0; i < reefs.Length; i++)
                {
                    var r = reefs[i];
                    if (r == null) continue;
                    r.currentLength = Mathf.Clamp01(r.currentLength + step);
                }
            }
            if (!hasTarget) return;
            var angles = _fSailAngle?.GetValue(cb.Boat) as RopeControllerSailAngle[];
            if (angles == null) return;
            float speed = _fSailSpeed != null ? (float)_fSailSpeed.GetValue(cb.Boat) : 1f;
            float delta = speed * dt * 0.05f;
            for (int i = 0; i < angles.Length; i++)
            {
                var a = angles[i];
                if (a == null) continue;
                a.changed = true;
                a.currentLength = Mathf.Clamp01(a.currentLength + (a.currentResistance > windMag ? delta : -delta));
            }
        }
    }
}
