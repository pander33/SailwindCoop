using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Boat ids shared by all peers. The id is <c>SaveableObject.sceneIndex</c> (low byte) plus the index
    /// of the embark hull under that root (high byte, always 0 for vanilla boats). Boats come from the
    /// <c>SaveLoadManager</c> registry, so boats the game has deactivated for distance are included.
    ///
    /// Not a position in a <c>FindObjectsOfType</c> list: that skips inactive boats, and
    /// <c>BoatHorizonPerformanceSwitcher</c> deactivates boats beyond ~10 km (except while loading), so host
    /// and a freshly loaded guest got different lists and different numbering.
    ///
    /// <see cref="FindBoats"/> returns owned boats (what the host streams); <see cref="FindByIndex"/> and
    /// <see cref="IndexOf"/> resolve any saveable boat.
    /// </summary>
    public static class BoatLocator
    {
        public const ushort NoBoat = ushort.MaxValue;

        /// <summary>Rescan interval; picks up ownership changes.</summary>
        public const float CacheSeconds = 1f;

        private struct Entry
        {
            public ushort Id;
            public Transform Boat;
            public SaveableObject Saveable;
            public bool Purchasable;
        }

        private static readonly Dictionary<ushort, Entry> _byId = new Dictionary<ushort, Entry>();
        private static readonly Dictionary<Transform, ushort> _idOf = new Dictionary<Transform, ushort>();
        private static readonly List<Transform> _owned = new List<Transform>();
        private static readonly List<Entry> _ownedScratch = new List<Entry>();
        private static readonly List<KeyValuePair<string, Transform>> _hullScratch =
            new List<KeyValuePair<string, Transform>>();
        private static float _cacheStamp = float.NegativeInfinity;
        private static bool _haveScan;
        private static int _setEpoch;
        private static string _lastSignature = "";

        /// <summary>Owned boats in the same order on every peer. Shared cache — do not modify.</summary>
        public static List<Transform> FindBoats()
        {
            EnsureScan();
            return _owned;
        }

        /// <summary>Fallback boat when the player is not on one.</summary>
        public static Transform FirstBoat()
        {
            var boats = FindBoats();
            return boats.Count > 0 ? boats[0] : null;
        }

        /// <summary>Force the next lookup to rescan. Call when the boat set changes (e.g. a purchase).</summary>
        public static void Invalidate()
        {
            _cacheStamp = float.NegativeInfinity;
        }

        /// <summary>False until an owned boat is known (purchase flags load after the registry exists).
        /// Callers treat false as "not known yet" rather than "no boat".</summary>
        public static bool IndicesAuthoritative
        {
            get { EnsureScan(); return _owned.Count > 0; }
        }

        /// <summary>Incremented when the owned set changes.</summary>
        public static int SetEpoch => _setEpoch;

        public static Transform FindByIndex(ushort index)
        {
            if (index == NoBoat) return null;
            EnsureScan();
            if (_byId.TryGetValue(index, out var e) && e.Boat != null) return e.Boat;
            return null;
        }

        public static ushort IndexOf(Transform boat)
        {
            if (boat == null) return NoBoat;
            EnsureScan();
            return _idOf.TryGetValue(boat, out var id) ? id : NoBoat;
        }

        public static int SceneIndexOf(ushort index) => index == NoBoat ? -1 : (index & 0xFF);

        private static void EnsureScan()
        {
            // Unscaled: timeScale is 0 on the host during a join.
            if (_haveScan && Time.unscaledTime - _cacheStamp < CacheSeconds && !HasDestroyed()) return;
            Rescan();
        }

        private static bool HasDestroyed()
        {
            foreach (var kv in _byId)
                if (kv.Value.Boat == null || kv.Value.Saveable == null) return true;
            return false;
        }

        private static void Rescan()
        {
            _byId.Clear();
            _idOf.Clear();
            _owned.Clear();
            _ownedScratch.Clear();

            SaveableObject[] registry = null;
            try
            {
                if (SaveLoadManager.instance != null) registry = SaveLoadManager.instance.GetCurrentObjects();
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[BoatLocator] SaveLoadManager registry unavailable: " + e.Message);
            }

            if (registry != null)
            {
                for (int si = 0; si < registry.Length && si <= 0xFF; si++)
                {
                    var so = registry[si];
                    if (so == null || so.GetComponent("BoatProbes") == null) continue;

                    // Include inactive: distant boats are deactivated by the game.
                    _hullScratch.Clear();
                    foreach (var col in so.GetComponentsInChildren<BoatEmbarkCollider>(true))
                    {
                        if (col == null || col.transform.parent == null) continue;
                        Transform hull = col.transform.parent;
                        bool dup = false;
                        for (int k = 0; k < _hullScratch.Count; k++)
                            if (_hullScratch[k].Value == hull) { dup = true; break; }
                        if (!dup) _hullScratch.Add(new KeyValuePair<string, Transform>(PathOf(hull), hull));
                    }
                    if (_hullScratch.Count == 0) continue;
                    _hullScratch.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

                    bool purchasable = so.GetComponent("PurchasableBoat") != null;
                    for (int sub = 0; sub < _hullScratch.Count && sub <= 0xFF; sub++)
                    {
                        ushort id = (ushort)((sub << 8) | si);
                        if (id == NoBoat) continue;
                        var e = new Entry
                        {
                            Id = id,
                            Boat = _hullScratch[sub].Value,
                            Saveable = so,
                            Purchasable = purchasable,
                        };
                        _byId[id] = e;
                        _idOf[e.Boat] = id;
                        // Same rule as SaveableObject.UnpurchasedBoatOrMooringRope.
                        if (so.extraSetting) _ownedScratch.Add(e);
                    }
                }
            }

            // Non-purchasable boats first, then by id.
            _ownedScratch.Sort((a, b) =>
            {
                if (a.Purchasable != b.Purchasable) return a.Purchasable ? 1 : -1;
                return a.Id.CompareTo(b.Id);
            });
            for (int i = 0; i < _ownedScratch.Count; i++) _owned.Add(_ownedScratch[i].Boat);

            // Empty registry = scene not loaded yet; do not cache.
            _haveScan = _byId.Count > 0;
            _cacheStamp = Time.unscaledTime;

            // Activity is left out so hiding a distant boat does not bump the epoch.
            string sig = Signature(withActivity: false);
            if (sig != _lastSignature)
            {
                _setEpoch++;
                _lastSignature = sig;
                if (_byId.Count > 0)
                    Plugin.Logger.LogInfo("[BoatLocator] Boats: " + _byId.Count + " saveable, owned " +
                                          Signature(withActivity: true));
            }
        }

        private static string Signature(bool withActivity)
        {
            if (_ownedScratch.Count == 0) return _byId.Count > 0 ? "[]" : "";
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < _ownedScratch.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('#').Append(_ownedScratch[i].Id).Append(' ').Append(_ownedScratch[i].Boat.name);
                if (withActivity && !_ownedScratch[i].Boat.gameObject.activeInHierarchy) sb.Append(" (hidden)");
            }
            return sb.Append(']').ToString();
        }

        public static string PathOf(Transform t)
        {
            if (t == null) return "";
            string path = t.name;
            Transform p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}
