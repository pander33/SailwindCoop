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
    /// TentandPlank (DogEggz.TentandPlank) adds hull-repair wood planks, unbundling/pallet cutting,
    /// and canvas tarps/tents. Relay client plank repairs, unbundles/pallet cuts, and live tarp snapshots.
    /// Tarp snapshots carry ONLY tarp records (no cargo condition, removed-cargo list or save stamp) and are
    /// applied by rebuilding tarps directly, never through World.Load (whose stale-save check would wipe the
    /// receiver's mod state). The host re-broadcasts its own tarp set after applying a client's snapshot so
    /// concurrent edits converge on the host's view; receivers skip snapshots equivalent to what they show.
    /// </summary>
    public static class TentandPlankCompat
    {
        public const string Guid = "DogEggz.TentandPlank";
        private const string AssemblyName = "TentandPlank";
        private const float ReconcileDelay = 2.5f;

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool Client => Net?.Role == Role.Client;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.TentandPlank) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }
        private static bool TarpSyncEnabled { get; set; }

        private static MethodInfo _finishPlank;
        private static MethodInfo _unbundle;
        private static MethodInfo _palletCut;
        private static FieldInfo _palletLastCut;
        private static MethodInfo _tarpMake;
        private static MethodInfo _tarpUntie;
        private static MethodInfo _tarpRepair;
        private static FieldInfo _tarpLastPack;
        private static FieldInfo _tarpLastTarp;
        private static FieldInfo _tarpLastRepaired;
        private static FieldInfo _tarpWaiting;
        private static MethodInfo _tarpRecords;
        private static MethodInfo _tarpFourSpots;
        private static MethodInfo _tarpLoaded;
        private static Type _tarpType;
        private static FieldInfo _tarpAll;
        private static Type _saveStateType;
        private static FieldInfo _stateTarps;
        private static FieldInfo _stateOtherTarps;
        private static MethodInfo _codecWrite;
        private static MethodInfo _codecRead;
        private static FieldInfo _worldState;
        private static FieldInfo _worldReady;
        private static FieldInfo _worldLoading;
        private static FieldInfo _recShip, _recCorners, _recTurns, _recWear;

        private static bool _applying;
        private static ShipItem _lastAuthoredPack;
        private static string _pendingRemote;
        private static float _reconcileAt = -1f;
        private static bool _broadcastQueued;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            TarpSyncEnabled = false;
            _finishPlank = _unbundle = _palletCut = _tarpMake = _tarpUntie = _tarpRepair = null;
            _tarpRecords = _tarpFourSpots = _tarpLoaded = _codecWrite = _codecRead = null;
            _palletLastCut = _tarpLastPack = _tarpLastTarp = _tarpLastRepaired = _tarpWaiting = _tarpAll = null;
            _stateTarps = _stateOtherTarps = _worldState = _worldReady = _worldLoading = null;
            _recShip = _recCorners = _recTurns = _recWear = null;
            _tarpType = _saveStateType = null;
            Reset();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                if (asm == null) return;

                var planksType = asm.GetType("TentandPlank.WoodPlanks");
                var palletCutType = asm.GetType("TentandPlank.PalletCut");
                var tarpsType = asm.GetType("TentandPlank.Tarps");
                var worldType = asm.GetType("TentandPlank.World");
                var codecType = asm.GetType("TentandPlank.StateCodec");
                var recordType = asm.GetType("TentandPlank.TarpState");
                _tarpType = asm.GetType("TentandPlank.Tarp");
                _saveStateType = asm.GetType("TentandPlank.SaveState");

                _finishPlank = planksType != null ? AccessTools.Method(planksType, "Finish", new[] { typeof(ShipItem), typeof(BoatDamage), typeof(bool) }) : null;
                _unbundle = planksType != null ? AccessTools.Method(planksType, "Unbundle", new[] { typeof(ShipItem) }) : null;
                _palletCut = palletCutType != null ? AccessTools.Method(palletCutType, "Cut", new[] { typeof(ShipItemKnife) }) : null;
                _palletLastCut = palletCutType != null ? AccessTools.Field(palletCutType, "LastCut") : null;

                if (tarpsType != null)
                {
                    _tarpMake = AccessTools.Method(tarpsType, "Make");
                    _tarpUntie = AccessTools.Method(tarpsType, "Untie");
                    _tarpRepair = AccessTools.Method(tarpsType, "Repair");
                    _tarpLastPack = AccessTools.Field(tarpsType, "LastPack");
                    _tarpLastTarp = AccessTools.Field(tarpsType, "LastTarp");
                    _tarpLastRepaired = AccessTools.Field(tarpsType, "LastRepaired");
                    _tarpWaiting = AccessTools.Field(tarpsType, "waiting");
                    _tarpRecords = AccessTools.Method(tarpsType, "Records");
                    _tarpFourSpots = AccessTools.Method(tarpsType, "FourSpots");
                    _tarpLoaded = AccessTools.Method(tarpsType, "Loaded");
                }
                _tarpAll = _tarpType != null ? AccessTools.Field(_tarpType, "All") : null;
                if (_saveStateType != null)
                {
                    _stateTarps = AccessTools.Field(_saveStateType, "tarps");
                    _stateOtherTarps = AccessTools.Field(_saveStateType, "otherTarps");
                }
                if (codecType != null)
                {
                    _codecWrite = AccessTools.Method(codecType, "Write");
                    _codecRead = AccessTools.Method(codecType, "Read");
                }
                if (worldType != null)
                {
                    _worldState = AccessTools.Field(worldType, "State");
                    _worldReady = AccessTools.Field(worldType, "Ready");
                    _worldLoading = AccessTools.Field(worldType, "Loading");
                }
                if (recordType != null)
                {
                    _recShip = AccessTools.Field(recordType, "ship");
                    _recCorners = AccessTools.Field(recordType, "corners");
                    _recTurns = AccessTools.Field(recordType, "turns");
                    _recWear = AccessTools.Field(recordType, "wear");
                }

                TarpSyncEnabled = _tarpRecords != null && _tarpFourSpots != null && _tarpLoaded != null && _tarpWaiting != null &&
                    _tarpAll != null && _saveStateType != null && _stateTarps != null && _stateOtherTarps != null &&
                    _codecWrite != null && _codecRead != null && _worldState != null && _worldReady != null && _worldLoading != null &&
                    _recShip != null && _recCorners != null && _recTurns != null && _recWear != null;
                SyncEnabled = _finishPlank != null || _unbundle != null || TarpSyncEnabled;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[TentandPlankCompat] TentandPlank detected; plank repair & unbundle sync enabled" +
                        (TarpSyncEnabled ? ", tarp sync enabled." : "; tarp sync members did not resolve."));
                else
                    Plugin.Logger.LogWarning("[TentandPlankCompat] TentandPlank detected, but target members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                TarpSyncEnabled = false;
                Plugin.Logger.LogWarning("[TentandPlankCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                if (_finishPlank != null)
                    harmony.Patch(_finishPlank,
                        prefix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PreFinishPlank)),
                        postfix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PostFinishPlank)));
                if (_unbundle != null)
                    harmony.Patch(_unbundle,
                        prefix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PreSpawnItems)),
                        postfix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PostUnbundle)));
                if (_palletCut != null)
                    harmony.Patch(_palletCut,
                        prefix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PreSpawnItems)),
                        postfix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PostPalletCut)));
                if (TarpSyncEnabled)
                {
                    if (_tarpMake != null && _tarpLastTarp != null)
                        harmony.Patch(_tarpMake,
                            prefix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PreTarpMake)),
                            postfix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PostTarpMake)));
                    if (_tarpRepair != null && _tarpLastRepaired != null)
                        harmony.Patch(_tarpRepair,
                            prefix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PreTarpRepair)),
                            postfix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PostTarpRepair)));
                    if (_tarpUntie != null)
                        harmony.Patch(_tarpUntie, postfix: new HarmonyMethod(typeof(TentandPlankCompat), nameof(PostTarpUntie)));
                }
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[TentandPlankCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _applying = false;
            _lastAuthoredPack = null;
            _pendingRemote = null;
            _reconcileAt = -1f;
            _broadcastQueued = false;
        }

        public static void Tick()
        {
            try
            {
                if (!Active) return;
                if (Client && _tarpLastPack != null)
                {
                    var pack = _tarpLastPack.GetValue(null) as ShipItem;
                    if (pack != null && pack != _lastAuthoredPack)
                    {
                        _lastAuthoredPack = pack;
                        ItemSync.Instance?.NotifyClientAuthored(pack);
                    }
                }
                if (!TarpSyncEnabled) return;
                if (_pendingRemote != null && WorldReady())
                {
                    string payload = _pendingRemote;
                    _pendingRemote = null;
                    ApplyPayload(payload);
                }
                if (Host && _reconcileAt > 0f && Time.unscaledTime >= _reconcileAt)
                {
                    // Wait until the applied snapshot has actually been rebuilt.
                    if (_tarpWaiting.GetValue(null) != null || !WorldReady())
                    {
                        _reconcileAt = Time.unscaledTime + 0.5f;
                    }
                    else
                    {
                        _reconcileAt = -1f;
                        BroadcastTarps();
                    }
                }
                if (_broadcastQueued) FlushBroadcast();
            }
            catch { }
        }

        private struct PlankState
        {
            public bool Tracked;
            public float HullDamage;
            public float Oakum;
        }

        private static void PreFinishPlank(ShipItem __0, BoatDamage __1, out PlankState __state)
        {
            __state = default;
            try
            {
                if (!Client || CoopBehaviour.Instance?.PlayingCoop != true || __1 == null) return;
                __state = new PlankState { Tracked = true, HullDamage = __1.hullDamage, Oakum = __1.oakum };
                if (__0 != null) ItemSync.Instance?.NotifyConsume(__0);
            }
            catch { __state = default; }
        }

        private static void PostFinishPlank(BoatDamage __1, PlankState __state)
        {
            try
            {
                if (!__state.Tracked || __1 == null) return;
                float repaired = __state.HullDamage - __1.hullDamage;
                float oakum = __state.Oakum - __1.oakum;
                var dmg = BoatDamageSync.Instance;
                if (dmg == null) return;
                if (oakum > 0f) dmg.NotifyLocalDamageAction(DamageAction.RemoveOakum, oakum, __1);
                if (repaired > 0f) dmg.NotifyLocalDamageAction(DamageAction.RepairHull, repaired, __1);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] PostFinishPlank: " + e.Message);
            }
        }

        private static void PreSpawnItems(out HashSet<ShipItem> __state)
        {
            __state = null;
            try
            {
                if (!Client || CoopBehaviour.Instance?.PlayingCoop != true) return;
                __state = new HashSet<ShipItem>(UnityEngine.Object.FindObjectsOfType<ShipItem>());
            }
            catch { __state = null; }
        }

        private static void PostUnbundle(ShipItem __0, HashSet<ShipItem> __state)
        {
            try
            {
                if (__state == null || !Client) return;
                var items = ItemSync.Instance;
                if (items == null) return;
                int spawned = AuthorNewPlankItems(__state, items);
                if (spawned > 0 && __0 != null) items.NotifyConsume(__0);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] PostUnbundle: " + e.Message);
            }
        }

        private static void PostPalletCut(HashSet<ShipItem> __state)
        {
            try
            {
                if (__state == null || !Client) return;
                var items = ItemSync.Instance;
                if (items == null) return;
                int spawned = AuthorNewPlankItems(__state, items);
                if (spawned > 0 && _palletLastCut != null)
                {
                    var cutPallet = _palletLastCut.GetValue(null) as ShipItem;
                    if (cutPallet != null) items.NotifyConsume(cutPallet);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] PostPalletCut: " + e.Message);
            }
        }

        private static int AuthorNewPlankItems(HashSet<ShipItem> before, ItemSync items)
        {
            int count = 0;
            foreach (var it in UnityEngine.Object.FindObjectsOfType<ShipItem>())
            {
                if (it == null || before.Contains(it)) continue;
                var sp = it.GetComponent<SaveablePrefab>();
                if (sp == null || (sp.prefabIndex != 641 && sp.prefabIndex != 643 && sp.prefabIndex != 645)) continue;
                items.NotifyClientAuthored(it);
                count++;
            }
            return count;
        }

        // ---------------- tarps ----------------

        private static void PreTarpMake(ShipItem __0, out object __state)
        {
            __state = null;
            try
            {
                __state = _tarpLastTarp.GetValue(null);
                if (_applying || !Active || !Client || __0 == null) return;
                // Make always destroys the pack (even when it then warns "Pole too tall").
                ItemSync.Instance?.NotifyConsume(__0);
            }
            catch { }
        }

        private static void PostTarpMake(object __state)
        {
            try
            {
                if (_applying || !Active) return;
                // The tarp is built whenever LastTarp changed, including the "Pole too tall" notice path.
                var made = _tarpLastTarp.GetValue(null) as UnityEngine.Object;
                if (made == null || ReferenceEquals(made, __state)) return;
                BroadcastTarps();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[TentandPlankCompat] PostTarpMake: " + e.Message); }
        }

        private struct RepairState
        {
            public object Before;
            public ShipItem Pack;
        }

        private static void PreTarpRepair(ShipItem __1, out RepairState __state)
        {
            __state = default;
            try { __state = new RepairState { Before = _tarpLastRepaired.GetValue(null), Pack = __1 }; }
            catch { __state = default; }
        }

        private static void PostTarpRepair(RepairState __state)
        {
            try
            {
                if (_applying || !Active) return;
                // Repair returns "Canvas repaired" on success and null on refusal; LastRepaired changes only on success.
                var repaired = _tarpLastRepaired.GetValue(null) as UnityEngine.Object;
                if (repaired == null || ReferenceEquals(repaired, __state.Before)) return;
                if (Client && __state.Pack != null) ItemSync.Instance?.NotifyConsume(__state.Pack);
                BroadcastTarps();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[TentandPlankCompat] PostTarpRepair: " + e.Message); }
        }

        private static void PostTarpUntie(object __0)
        {
            try
            {
                if (_applying || !Active || __0 == null) return;
                BroadcastTarps();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[TentandPlankCompat] PostTarpUntie: " + e.Message); }
        }

        private static bool WorldReady()
        {
            try
            {
                return (bool)_worldReady.GetValue(null) && !(bool)_worldLoading.GetValue(null) && !GameState.currentlyLoading;
            }
            catch { return false; }
        }

        /// <summary>Tarp-only snapshot encoded with TentandPlank's own codec (cargo lists empty, savedHour 0).</summary>
        private static string BuildPayload()
        {
            var records = _tarpRecords.Invoke(null, null) as IList;
            var state = Activator.CreateInstance(_saveStateType);
            var four = Activator.CreateInstance(_stateTarps.FieldType) as IList;
            var other = Activator.CreateInstance(_stateOtherTarps.FieldType) as IList;
            if (records != null)
            {
                foreach (var rec in records)
                {
                    if (rec == null) continue;
                    if ((bool)_tarpFourSpots.Invoke(null, new[] { rec })) four.Add(rec);
                    else other.Add(rec);
                }
            }
            _stateTarps.SetValue(state, four);
            _stateOtherTarps.SetValue(state, other);
            return _codecWrite.Invoke(null, new[] { state }) as string;
        }

        private static void BroadcastTarps()
        {
            // Coalesced: UntieFrom unties every knot on a picked-up item in one frame; send one snapshot from Tick.
            _broadcastQueued = true;
        }

        private static void FlushBroadcast()
        {
            _broadcastQueued = false;
            if (!TarpSyncEnabled || Net == null) return;
            string payload = BuildPayload();
            if (payload == null) return;
            if (!TarpStateMsg.Fits(payload))
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] Tarp snapshot is too large to send (" + payload.Length + " chars); skipped.");
                return;
            }
            Net.SendMod(new TarpStateMsg { Payload = payload }, ModFeatures.TentandPlank, DeliveryMethod.ReliableOrdered);
        }

        public static void OnRemote(TarpStateMsg msg, NetPeer fromPeer)
        {
            if (!Active || !TarpSyncEnabled || msg == null || string.IsNullOrEmpty(msg.Payload)) return;
            try
            {
                if (WorldReady())
                {
                    _pendingRemote = null;
                    ApplyPayload(msg.Payload);
                }
                else
                {
                    _pendingRemote = msg.Payload;   // latest wins; applied from Tick once the mod's world is ready
                }

                if (Host)
                {
                    Net.SendMod(msg, ModFeatures.TentandPlank, DeliveryMethod.ReliableOrdered, except: fromPeer);
                    _reconcileAt = Time.unscaledTime + ReconcileDelay;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] OnRemote: " + e.Message);
            }
        }

        private static void ApplyPayload(string payload)
        {
            object incoming;
            try { incoming = _codecRead.Invoke(null, new object[] { payload }); }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] Tarp snapshot rejected: " + (e.InnerException ?? e).Message);
                return;
            }
            if (incoming == null) return;
            var inFour = _stateTarps.GetValue(incoming) as IList;
            var inOther = _stateOtherTarps.GetValue(incoming) as IList;

            var current = _tarpRecords.Invoke(null, null) as IList;
            if (Equivalent(Concat(inFour, inOther), current)) return;

            _applying = true;
            try
            {
                var world = _worldState.GetValue(null);
                if (world == null) return;
                _stateTarps.SetValue(world, inFour ?? Activator.CreateInstance(_stateTarps.FieldType));
                _stateOtherTarps.SetValue(world, inOther ?? Activator.CreateInstance(_stateOtherTarps.FieldType));
                // Destroy live tarps WITHOUT Tarps.Clear(): Clear() also cancels this player's own
                // half-placed tie spots (Pending/PendingPack). Dropping `waiting` stops a rebuild in flight.
                DestroyLiveTarps();
                _tarpWaiting.SetValue(null, null);
                _tarpLoaded.Invoke(null, null);
            }
            finally { _applying = false; }
        }

        private static void DestroyLiveTarps()
        {
            var victims = new HashSet<UnityEngine.Object>();
            var all = _tarpAll.GetValue(null) as IList;
            if (all != null)
            {
                foreach (var t in all)
                    if (t is Component c && c != null) victims.Add(c.gameObject);
                // Destroy() runs at frame end; drop them from Tarp.All now (as the mod does in Untie/Repair)
                // so Records() in this frame no longer lists them.
                all.Clear();
            }
            foreach (var o in Resources.FindObjectsOfTypeAll(_tarpType))
            {
                if (!(o is Component c) || c == null) continue;
                if (!c.gameObject.scene.IsValid()) continue;   // skip prefabs/templates
                victims.Add(c.gameObject);
            }
            foreach (var go in victims)
                if (go != null) UnityEngine.Object.Destroy(go);
        }

        private static List<object> Concat(IList a, IList b)
        {
            var list = new List<object>();
            if (a != null) foreach (var x in a) if (x != null) list.Add(x);
            if (b != null) foreach (var x in b) if (x != null) list.Add(x);
            return list;
        }

        /// <summary>Same tarp set (order-free) within float tolerance; wear drifts slightly between machines.</summary>
        private static bool Equivalent(List<object> incoming, IList current)
        {
            var cur = new List<object>();
            if (current != null) foreach (var x in current) if (x != null) cur.Add(x);
            return TarpSnapshotMath.SameSet(incoming.ConvertAll(View), cur.ConvertAll(View));
        }

        private static TarpRecordView View(object rec) => new TarpRecordView
        {
            Ship = (int)_recShip.GetValue(rec),
            Wear = (float)_recWear.GetValue(rec),
            Corners = _recCorners.GetValue(rec) as float[],
            Turns = _recTurns.GetValue(rec) as float[],
        };

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !TarpSyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.TentandPlank) == 0) return;
            try
            {
                if (!WorldReady()) return;
                string payload = BuildPayload();
                if (string.IsNullOrEmpty(payload) || !TarpStateMsg.Fits(payload)) return;
                peer.Send(new TarpStateMsg { Payload = payload }, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[TentandPlankCompat] ResyncTo: " + e.Message);
            }
        }
    }
}
