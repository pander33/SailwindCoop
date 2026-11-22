using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using LiteNetLib;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Sail Damage (FluffyJesus / Кэп, kap.saildamage) tears sails from overload and swing hits, cuts holes in the canvas,
    /// costs thrust, and is mended with sailcloth or at the shipyard. Every machine tore its own copy of the sails from
    /// its own physics, so the crew saw different holes and the host's boat lost thrust nobody else could see.
    ///
    /// The host's copy is the real one. Guests never accrue damage (the three accrual methods are skipped there). Every
    /// machine compares each sail's six values (damage, patches, and both studding sail sides; DamageModel.Snapshot)
    /// against the last synced ones, keyed by Sail Damage's own save key (boat scene index, mast, sail prefab, ordinal),
    /// which names the same sail on every machine. The host broadcasts what changed; a guest's change (mending by hand,
    /// a paid repair, the test keys) goes to the host, which applies it and passes it on.
    ///
    /// A sail object seen for the first time takes the synced value for its key instead of being read as a change. The
    /// shipyard rebuilds every sail, and the machine that did not place the order rebuilds them from data that carries
    /// no damage: read as a change, that was a free repair for the whole crew. A paid repair (DamageModel.Mend) runs on
    /// the new objects right after the rebuild, so it is hooked and sent directly.
    ///
    /// Built against 1.0.5. All reflected, because the mod may be absent, and a miss turns this off with a warning.
    /// </summary>
    public static class SailDamageCompat
    {
        public const string Guid = "kap.saildamage";
        private const string AssemblyName = "SailDamage";

        private const float ScanInterval = 0.5f;
        private const float RefreshInterval = 1f;
        // Overload grows a little every physics step; one percent keeps a storm to a few packets a second per sail.
        private const float SendStep = 0.01f;
        private const float Same = 0.0001f;

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.SailDamage) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _modelType;
        private static FieldInfo _instance;      // internal static DamageModel Instance
        private static FieldInfo _pending;       // private Dictionary<string, float[]> pending
        private static MethodInfo _snapshot;     // internal float[] Snapshot(Sail), null for a whole sail
        private static MethodInfo _restore;      // internal void Restore(Sail, float[])
        private static MethodInfo _mend;         // internal void Mend(Sail)
        private static MethodInfo _deserialize;  // internal void Deserialize(string)
        private static MethodInfo _saveKey;      // internal static string SaveKey(Sail)
        private static readonly List<MethodInfo> _accrual = new List<MethodInfo>();
        private static MethodInfo _sew;          // SailMending: internal static float Sew(Sail, ShipItemSailcloth, float, string)

        // Bolts of sailcloth used up by sewing since the last scan. Sewing takes a little every frame, so the amount
        // goes out with the scan instead of on each change.
        private static readonly HashSet<ShipItem> _sewnBolts = new HashSet<ShipItem>();

        private static bool _applying;
        // A guest sends nothing until the host's state has arrived: before that, its sails carry its own save's damage.
        private static bool _baselined;

        // Last value synced for each sail key. A key with no entry is a whole sail on a guest, and unseen on the host.
        private static readonly Dictionary<string, float[]> _synced = new Dictionary<string, float[]>();
        private static readonly Dictionary<string, SailRepairQueue> _repairs = new Dictionary<string, SailRepairQueue>();
        private static readonly HashSet<Sail> _known = new HashSet<Sail>();
        // Each key's value at the previous scan: a change smaller than SendStep still goes out once it stops moving.
        private static readonly Dictionary<string, float[]> _lastScan = new Dictionary<string, float[]>();
        private static Sail[] _sails = new Sail[0];
        private static float _nextScan, _nextRefresh;
        private static readonly float[] Whole = new float[6];

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Logger.LogInfo("[SailDmgCompat] Sail Damage not installed.");
                    return;
                }
                IsInstalled = true;
                string version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                _modelType = asm?.GetType("SailDamage.DamageModel");
                var mendingType = asm?.GetType("SailDamage.SailMending");
                _sew = mendingType != null ? AccessTools.Method(mendingType, "Sew") : null;

                if (_modelType != null)
                {
                    _instance = AccessTools.Field(_modelType, "Instance");
                    _pending = AccessTools.Field(_modelType, "pending");
                    _snapshot = AccessTools.Method(_modelType, "Snapshot", new[] { typeof(Sail) });
                    _restore = AccessTools.Method(_modelType, "Restore", new[] { typeof(Sail), typeof(float[]) });
                    _mend = AccessTools.Method(_modelType, "Mend", new[] { typeof(Sail) });
                    _deserialize = AccessTools.Method(_modelType, "Deserialize", new[] { typeof(string) });
                    _saveKey = AccessTools.Method(_modelType, "SaveKey", new[] { typeof(Sail) });
                    _accrual.Clear();
                    foreach (var name in new[] { "CheckSwingHit", "ApplyOverload", "ApplyStuddingOverload" })
                    {
                        var m = AccessTools.Method(_modelType, name, new[] { typeof(Sail) });
                        if (m != null) _accrual.Add(m);
                    }
                }

                SyncEnabled = _instance != null && _pending != null && _snapshot != null && _restore != null && _mend != null
                              && _deserialize != null && _saveKey != null && _accrual.Count == 3
                              && _pending.FieldType == typeof(Dictionary<string, float[]>);
                if (SyncEnabled)
                    Plugin.Logger.LogInfo($"[SailDmgCompat] Sail Damage v{version} detected; the host's sail damage is shared with the crew.");
                else
                    Plugin.Logger.LogWarning($"[SailDmgCompat] Sail Damage v{version} detected, but its members did not resolve. " +
                        "Sail damage sync disabled, so each crewmate's sails will tear on their own. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[SailDmgCompat] Detection threw. Sail damage sync disabled. " + e);
            }
        }

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                foreach (var m in _accrual)
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(SailDamageCompat), nameof(AccrualPrefix)));
                harmony.Patch(_mend, postfix: new HarmonyMethod(typeof(SailDamageCompat), nameof(MendPostfix)));
                if (_sew != null)
                    harmony.Patch(_sew, postfix: new HarmonyMethod(typeof(SailDamageCompat), nameof(SewPostfix)));
                else
                    Plugin.Logger.LogWarning("[SailDmgCompat] SailMending.Sew not found; a bolt of sailcloth will not show what was used of it to the crew.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[SailDmgCompat] Patching threw. Sail damage sync disabled. " + e);
            }
        }

        public static void Reset()
        {
            _synced.Clear();
            _repairs.Clear();
            _known.Clear();
            _lastScan.Clear();
            _sewnBolts.Clear();
            _sails = new Sail[0];
            _baselined = false;
            _applying = false;
            _nextScan = _nextRefresh = 0f;
        }

        private static object Model => _instance.GetValue(null);

        // Only the host's physics tears sails.
        private static bool AccrualPrefix()
        {
            return !SyncEnabled || !Active || Host;
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[SailDmgCompat] " + error.Message);

        private static void MendPostfix(Sail sail)
        {
            if (Active) PatchGuard.Run(() => OnMend(sail), Report);
        }

        private static void OnMend(Sail sail)
        {
            if (!SyncEnabled || !Active || _applying || sail == null) return;
            if (!Host && !_baselined) return;
            string key = Key(sail);
            if (key == null) return;
            _known.Add(sail);
            if (Host)
            {
                _synced[key] = Whole;
                Send(false, new List<SailDamageEntry> { new SailDamageEntry { Key = key, Values = Whole } });
            }
            else
            {
                var repair = RepairFor(key);
                repair.Mend(Snap(sail));
                if (repair.TrySend(false, Time.unscaledTime, out var values, out bool mend))
                    Send(false, new List<SailDamageEntry> { new SailDamageEntry { Key = key, Values = values, Mend = mend } });
            }
        }

        private static void SewPostfix(ShipItem cloth)
        {
            if (Active && cloth != null) _sewnBolts.Add(cloth);
        }

        // === Scanning ===

        /// <summary>Called from the item tick.</summary>
        public static void Tick()
        {
            if (!SyncEnabled || !Active) return;
            try
            {
                if (Model == null) return;
                if (!Host && !_baselined) return;
                float now = Time.unscaledTime;
                if (now < _nextScan) return;
                _nextScan = now + ScanInterval;
                if (_sewnBolts.Count > 0)
                {
                    // A used-up bolt is destroyed through ShipItem.DestroyItem, which item sync already sends.
                    foreach (var bolt in _sewnBolts)
                        if (bolt != null) ItemSync.Instance?.NotifyItemStateChanged(bolt, "sailcloth used");
                    _sewnBolts.Clear();
                }
                if (now >= _nextRefresh)
                {
                    _nextRefresh = now + RefreshInterval;
                    _sails = UnityEngine.Object.FindObjectsOfType<Sail>();
                    _known.RemoveWhere(sail => sail == null);
                }

                List<SailDamageEntry> changed = null;
                foreach (var sail in _sails)
                {
                    if (sail == null) continue;
                    string key = Key(sail);
                    if (key == null) continue;
                    float[] snap = Snap(sail);
                    if (!Host)
                    {
                        var repair = RepairFor(key);
                        if (_known.Add(sail)) ShowRepair(sail, snap, repair);
                        else repair.Observe(snap);

                        var queued = repair.Queued;
                        bool repairSettled = _lastScan.TryGetValue(key, out var previousDelta) && !Differs(queued, previousDelta, Same);
                        _lastScan[key] = (float[])queued.Clone();
                        bool sendDelta = Differs(queued, Whole, SendStep) || (repairSettled && Differs(queued, Whole, Same));
                        if (repair.TrySend(sendDelta, now, out var values, out bool mend))
                            (changed ?? (changed = new List<SailDamageEntry>())).Add(
                                new SailDamageEntry { Key = key, Values = values, Mend = mend });
                        continue;
                    }
                    bool hasSynced = _synced.TryGetValue(key, out var synced);

                    if (_known.Add(sail))
                    {
                        // First sight. The host takes what it has as the starting point for a key it has never seen.
                        if (!hasSynced) { _synced[key] = snap; continue; }
                        if (Differs(snap, synced, Same)) Apply(sail, synced);
                        continue;
                    }

                    var reference = hasSynced ? synced : Whole;
                    bool settled = _lastScan.TryGetValue(key, out var previous) && !Differs(snap, previous, Same);
                    _lastScan[key] = snap;
                    if (!Worth(snap, reference) && !(settled && Differs(snap, reference, Same))) continue;
                    _synced[key] = snap;
                    (changed ?? (changed = new List<SailDamageEntry>())).Add(new SailDamageEntry
                    {
                        Key = key, Values = snap,
                    });
                }

                if (changed != null) Send(false, changed);
            }
            catch (Exception e)
            {
                PatchGuard.Fail(Report, e);
            }
        }

        /// <summary>Host: everything a joiner needs, including sails of boats the host has not come near yet.</summary>
        public static void ResyncTo(NetPeer peer)
        {
            if (!Active || !Host || Model == null || (Net.FeaturesOf(peer) & ModFeatures.SailDamage) == 0) return;
            var entries = new List<SailDamageEntry>();
            var seen = new HashSet<string>();
            foreach (var sail in UnityEngine.Object.FindObjectsOfType<Sail>())
            {
                string key = Key(sail);
                if (key == null || !seen.Add(key)) continue;
                float[] snap = Snap(sail);
                if (Differs(snap, Whole, Same)) entries.Add(new SailDamageEntry { Key = key, Values = snap });
            }
            if (_pending.GetValue(Model) is Dictionary<string, float[]> pending)
                foreach (var kvp in pending)
                    if (seen.Add(kvp.Key) && kvp.Value != null && kvp.Value.Length >= 6)
                        entries.Add(new SailDamageEntry { Key = kvp.Key, Values = kvp.Value });

            var packet = new SailDamageMsg { ReplaceAll = true, Entries = entries.ToArray() };
            peer.Send(packet, DeliveryMethod.ReliableOrdered);

        }

        // === Receiving ===

        public static void OnRemote(SailDamageMsg packet, NetPeer sender)
        {
            if (!Active || packet == null) return;
            try
            {
                if (Model == null) return;

                if (Host)
                {
                    if (packet.ReplaceAll || packet.RepairAck || (Net.FeaturesOf(sender) & ModFeatures.SailDamage) == 0) return;
                }
                else if (packet.ReplaceAll)
                {
                    // The guest's own save put damage on its sails; the host's state replaces all of it.
                    _applying = true;
                    try { _deserialize.Invoke(Model, new object[] { "" }); }
                    finally { _applying = false; }
                    _synced.Clear();
                    _repairs.Clear();
                    _known.Clear();
                    _lastScan.Clear();
                    _baselined = true;
                }

                if (packet.Entries == null) return;
                Dictionary<string, Sail> byKey = null;
                var applied = Host ? new List<SailDamageEntry>() : null;
                foreach (var entry in packet.Entries)
                {
                    if (entry == null || entry.Key == null || entry.Values == null || entry.Values.Length < 6) continue;
                    if (byKey == null) byKey = SailsByKey();
                    byKey.TryGetValue(entry.Key, out var sail);
                    if (Host)
                    {
                        float[] authoritative;
                        if (sail != null)
                        {
                            var current = Snap(sail);
                            var repaired = entry.Mend ? Whole : AddDelta(current, entry.Values);
                            if (entry.Mend || Differs(current, repaired, Same)) Apply(sail, repaired);
                            authoritative = Snap(sail);
                            _known.Add(sail);
                        }
                        else
                        {
                            var pending = (Dictionary<string, float[]>)_pending.GetValue(Model);
                            if (pending == null || !pending.TryGetValue(entry.Key, out var current))
                                current = _synced.TryGetValue(entry.Key, out var last) ? last : Whole;
                            authoritative = entry.Mend ? Whole : AddDelta(current, entry.Values);
                            if (pending != null) pending[entry.Key] = authoritative;
                        }
                        _synced[entry.Key] = authoritative;
                        applied.Add(new SailDamageEntry { Key = entry.Key, Values = authoritative });
                    }
                    else
                    {
                        var repair = RepairFor(entry.Key);
                        if (sail != null)
                        {
                            var current = Snap(sail);
                            if (!_known.Add(sail)) repair.Observe(current);
                            repair.UpdateAuthority(entry.Values, packet.RepairAck);
                            ShowRepair(sail, current, repair);
                        }
                        else
                        {
                            repair.UpdateAuthority(entry.Values, packet.RepairAck);
                            if (_pending.GetValue(Model) is Dictionary<string, float[]> pending)
                                pending[entry.Key] = entry.Values;
                        }
                    }
                }
                if (applied?.Count > 0)
                {
                    var entries = applied.ToArray();
                    sender.Send(new SailDamageMsg { RepairAck = true, Entries = entries }, DeliveryMethod.ReliableOrdered);
                    Net.SendMod(new SailDamageMsg { Entries = entries }, ModFeatures.SailDamage, DeliveryMethod.ReliableOrdered, sender);
                }
            }
            catch (Exception e)
            {
                PatchGuard.Fail(Report, e);
            }
        }

        // === Helpers ===

        private static void Apply(Sail sail, float[] values)
        {
            _applying = true;
            try
            {
                // Mend also hands the canvas back from the torn one; Restore with zeros would leave it torn.
                if (!Differs(values, Whole, Same)) _mend.Invoke(Model, new object[] { sail });
                else _restore.Invoke(Model, new object[] { sail, (float[])values.Clone() });
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[SailDmgCompat] Could not apply damage to {sail.sailName}: {e.Message}");
            }
            finally
            {
                _applying = false;
            }
        }

        private static void Send(bool replaceAll, List<SailDamageEntry> entries)
        {
            var packet = new SailDamageMsg { ReplaceAll = replaceAll, Entries = entries.ToArray() };
            Net.SendMod(packet, ModFeatures.SailDamage, DeliveryMethod.ReliableOrdered);
        }

        private static Dictionary<string, Sail> SailsByKey()
        {
            var map = new Dictionary<string, Sail>();
            foreach (var sail in UnityEngine.Object.FindObjectsOfType<Sail>())
            {
                string key = Key(sail);
                if (key != null && !map.ContainsKey(key)) map[key] = sail;
            }
            return map;
        }

        private static string Key(Sail sail)
        {
            try { return _saveKey.Invoke(null, new object[] { sail }) as string; }
            catch { return null; }
        }

        private static float[] Snap(Sail sail)
        {
            var v = _snapshot.Invoke(Model, new object[] { sail }) as float[];
            return v != null && v.Length >= 6 ? v : Whole;
        }

        private static SailRepairQueue RepairFor(string key)
        {
            if (!_repairs.TryGetValue(key, out var repair)) _repairs.Add(key, repair = new SailRepairQueue(Whole));
            return repair;
        }

        private static void ShowRepair(Sail sail, float[] current, SailRepairQueue repair)
        {
            var shown = repair.Preview();
            if (Differs(current, shown, Same))
            {
                Apply(sail, shown);
                current = Snap(sail);
            }
            repair.MarkDisplayed(current);
        }

        private static float[] AddDelta(float[] baseline, float[] delta)
        {
            var values = new float[SailDamageEntry.ValueCount];
            for (int i = 0; i < values.Length; i++) values[i] = Mathf.Clamp01(baseline[i] + delta[i]);
            return values;
        }

        private static bool Differs(float[] a, float[] b, float eps)
        {
            for (int i = 0; i < 6; i++)
                if (Mathf.Abs(a[i] - b[i]) > eps) return true;
            return false;
        }

        // Small steps wait until they add up, but reaching whole, or a sail tearing away, goes out at once.
        private static bool Worth(float[] now, float[] last)
        {
            if (Differs(now, last, SendStep)) return true;
            if (!Differs(now, last, Same)) return false;
            bool nowWhole = !Differs(now, Whole, Same), lastWhole = !Differs(last, Whole, Same);
            if (nowWhole != lastWhole) return true;
            for (int i = 0; i < 6; i += 2)
                if ((now[i] >= 0.999f) != (last[i] >= 0.999f)) return true;
            return false;
        }

    }
}
