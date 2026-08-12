using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stable-enough boat enumeration for save-identical peers. A Sailwind embark collider sits
    /// under the visible "world boat" transform used by <c>PlayerEmbarkerNew</c>; sorting by the
    /// transform hierarchy path gives both machines the same index for main boat / dinghies.
    ///
    /// The scan itself is expensive (<c>FindObjectsOfType</c> + a sort whose key is a full hierarchy
    /// path string), and <see cref="FindByIndex"/>/<see cref="IndexOf"/> sit on the per-snapshot hot
    /// path (PlayerSync/BoatSync/ItemSync at SnapshotHz, times every peer and every boat). Running it
    /// raw there cost >100 full scene scans per second plus the matching GC churn — a GC spike is a
    /// frame hitch, and a frame hitch while the deck is driven kinematically is exactly what drops a
    /// player through it. So the result is cached for <see cref="CacheSeconds"/> and invalidated
    /// explicitly when the boat set can change (a purchase) or when any cached boat is destroyed
    /// (scene unload).
    /// </summary>
    public static class BoatLocator
    {
        public const ushort NoBoat = ushort.MaxValue;

        /// <summary>How long a scan result stays valid. A new/removed boat is picked up within this.</summary>
        public const float CacheSeconds = 1f;

        /// <summary>Life of a scan taken while the boat set is still changing, where the result may be
        /// partial and a stale index-to-transform mapping is actively harmful. Also the interval at
        /// which stability is re-measured, so settling costs about
        /// <c>SettlingCacheSeconds * StableScansRequired</c>.</summary>
        public const float SettlingCacheSeconds = 0.1f;

        /// <summary>
        /// Consecutive identical scans required before positional indices are treated as authoritative.
        ///
        /// A boat's index is its position in this list, so an INCOMPLETE scan silently renumbers every
        /// boat after the missing one: with boat A not yet spawned, the already-spawned boat B answers
        /// to A's index, and <c>BoatSync</c> binds A's snapshot stream to B and teleports it to A's pose.
        ///
        /// The obvious guard — "don't trust indices while <c>GameState.currentlyLoading</c>" — was tried
        /// and rejected: it assumes the flag clears only after the last boat exists, which is exactly
        /// the assumption §5.6 of the journal records as false for this engine (objects arrive on
        /// deferred coroutines after the load formally ends). Worse, it made the short TTL below
        /// unreachable, so the first scan after the flag cleared — still possibly partial — was cached
        /// for a full second, which at SnapshotHz=20 is ~20 snapshots against the 2 that
        /// <c>BoatSync.SamplesBeforeEngage</c> needs. The gate widened the very window it was meant
        /// to close.
        ///
        /// Measuring the set instead needs no guess about engine timing: whatever the reason the set is
        /// still moving (a load, a deferred spawn, a purchase), indices are held back until it stops
        /// moving. The cost is that a genuine change — buying a boat — makes lookups return "unknown"
        /// for ~0.2 s, during which poses go out in world frame rather than boat-local. That is a
        /// glitch; binding a boat to the wrong hull is corruption.
        /// </summary>
        public const int StableScansRequired = 3;

        /// <summary>Complain if indices stay untrustworthy longer than this while boats DO exist —
        /// otherwise a set that never settles would silently disable boat sync for the whole session.</summary>
        private const float UnstableWarnSec = 10f;

        private static List<Transform> _cache;
        private static float _cacheStamp = float.NegativeInfinity;
        private static float _cacheTtl = CacheSeconds;
        private static int _stableScans;
        private static int _setEpoch;
        private static float _unstableSince = -1f;
        private static Runtime.CoopLog.Repeat _unstableReport;
        // Reused across scans so a steady state allocates nothing.
        private static readonly HashSet<Transform> _scanSet = new HashSet<Transform>();
        private static readonly List<Entry> _scanEntries = new List<Entry>();

        private struct Entry
        {
            public Transform Boat;
            public string Path;
            public int Purchasable;   // 0 = original, 1 = purchasable (sorted last)
        }

        /// <summary>
        /// The current boat set, ordered identically on every peer. The returned list is the shared
        /// cache — read it, never mutate or keep it across frames.
        /// </summary>
        public static List<Transform> FindBoats()
        {
            if (IsCacheUsable()) return _cache;
            return Rescan();
        }

        /// <summary>Force the next lookup to rescan. Call when the boat set changes (e.g. a purchase).</summary>
        public static void Invalidate()
        {
            _cacheStamp = float.NegativeInfinity;
        }

        /// <summary>
        /// True once the boat set has come back identical <see cref="StableScansRequired"/> times in a
        /// row, i.e. once a position in the list can be trusted to mean the same hull on every peer.
        /// While false, callers must treat every index as unknown rather than guess.
        /// </summary>
        public static bool IndicesAuthoritative => _stableScans >= StableScansRequired;

        /// <summary>
        /// Bumped every time a scan comes back different from the previous one, i.e. every time the
        /// numbering may have moved. Callers that cache an index across frames must cache this with it
        /// and drop the cached index when it no longer matches — the transform they resolved from is NOT
        /// enough evidence, because what changes is that transform's POSITION in the list, not its
        /// identity. A boat appearing or disappearing ahead of it renumbers it while it stays the very
        /// same object (see <c>PlayerSync.ResolveBoatIndex</c>).
        /// </summary>
        public static int SetEpoch => _setEpoch;

        private static bool IsCacheUsable()
        {
            if (_cache == null) return false;
            // An empty result is never worth trusting: the only way to get one is scanning before the
            // boats exist, and caching that serves "no such boat" for a whole second.
            if (_cache.Count == 0) return false;
            // Unscaled: the host freezes timeScale during a join, and boats still need resolving there.
            if (Time.unscaledTime - _cacheStamp >= _cacheTtl) return false;
            // A destroyed entry means the scene changed under us — never hand out dangling transforms.
            for (int i = 0; i < _cache.Count; i++)
                if (_cache[i] == null) return false;
            return true;
        }

        /// <summary>Element-wise equality against the previous scan. Both lists come out of the same
        /// deterministic sort, so this is a set comparison; a destroyed entry in the old list compares
        /// unequal, which is the wanted answer (the scene changed).</summary>
        private static bool MatchesCache(List<Transform> boats)
        {
            if (_cache == null || _cache.Count != boats.Count) return false;
            for (int i = 0; i < boats.Count; i++)
                if (_cache[i] != boats[i]) return false;
            return true;
        }

        private static List<Transform> Rescan()
        {
            _scanSet.Clear();
            foreach (var col in Object.FindObjectsOfType<BoatEmbarkCollider>())
            {
                if (col == null || col.transform == null || col.transform.parent == null) continue;
                Transform boat = col.transform.parent;
                if (!IsNetworkBoat(boat)) continue;
                _scanSet.Add(boat);
            }

            // Build the sort keys once instead of rebuilding two hierarchy paths per comparison.
            _scanEntries.Clear();
            foreach (var boat in _scanSet)
                _scanEntries.Add(new Entry
                {
                    Boat = boat,
                    Path = PathOf(boat),
                    Purchasable = IsPurchasable(boat) ? 1 : 0,
                });

            // Original boats first (stable order), purchasable boats last. A purchasable boat only joins
            // the network set once bought (extraSetting); sorting it after the originals means buying one
            // APPENDS its index instead of shifting the main ship's index (which items/players address).
            _scanEntries.Sort((a, b) =>
            {
                if (a.Purchasable != b.Purchasable) return a.Purchasable - b.Purchasable;
                return string.CompareOrdinal(a.Path, b.Path);
            });

            var boats = new List<Transform>(_scanEntries.Count);
            for (int i = 0; i < _scanEntries.Count; i++) boats.Add(_scanEntries[i].Boat);

            // An empty result is never stored (see IsCacheUsable) — it can only mean "scanned too early".
            // No boats at all is the main menu, not an unstable set: reset quietly and do not complain.
            if (boats.Count == 0)
            {
                if (_cache != null) _setEpoch++;   // boats -> none is a renumbering like any other
                _cache = null;
                _cacheStamp = float.NegativeInfinity;
                _stableScans = 0;
                _unstableSince = -1f;
                return boats;
            }

            // Compare BEFORE overwriting the cache. A run of identical scans is what makes positions
            // authoritative; anything else restarts the run at this scan.
            bool sameSet = MatchesCache(boats);
            if (!sameSet) _setEpoch++;
            _stableScans = sameSet ? _stableScans + 1 : 1;

            // A still-moving set gets a much shorter life — that is both the staleness bound and the
            // re-measure interval. Not caching at all there was the wrong trade: these lookups are on
            // the per-frame path, the client is connected and ticking throughout its load, and a raw
            // scan per call reinstates exactly the GC-churn frame hitches this class exists to remove.
            _cache = boats;
            _cacheStamp = Time.unscaledTime;
            _cacheTtl = IndicesAuthoritative ? CacheSeconds : SettlingCacheSeconds;

            if (IndicesAuthoritative)
            {
                _unstableSince = -1f;
                // Clear the occurrence count too, not just the timer: a later episode in the same
                // session would otherwise resume deep inside ReportError's throttle and stay silent
                // until the next multiple of 300.
                _unstableReport = default(Runtime.CoopLog.Repeat);
            }
            else
            {
                if (_unstableSince < 0f) _unstableSince = Time.unscaledTime;
                else if (Time.unscaledTime - _unstableSince > UnstableWarnSec)
                    Plugin.Logger.ReportError(
                        "[BoatLocator] Boat set still changing after " + UnstableWarnSec +
                        " s - boat/item/player indices stay unresolved until it settles",
                        "boats=" + boats.Count + ", stable run=" + _stableScans + "/" + StableScansRequired,
                        ref _unstableReport);
            }
            return _cache;
        }

        private static bool IsPurchasable(Transform worldBoat)
        {
            if (worldBoat == null) return false;
            Transform root = worldBoat.parent != null ? worldBoat.parent : worldBoat;
            return root.GetComponent("PurchasableBoat") != null;
        }

        private static bool IsNetworkBoat(Transform worldBoat)
        {
            if (worldBoat == null) return false;
            Transform root = worldBoat.parent != null ? worldBoat.parent : worldBoat;
            var saveable = root.GetComponent<SaveableObject>();
            var probes = root.GetComponent("BoatProbes");
            if (saveable != null && probes != null && !saveable.extraSetting)
                return false;
            return true;
        }

        public static Transform FindByIndex(ushort index)
        {
            if (index == NoBoat) return null;
            // FindBoats() FIRST, always: it is the only thing that advances the stability run. Checking
            // IndicesAuthoritative before it would mean no scans happen while unstable, so the set could
            // never be observed settling and every lookup would be refused forever.
            var boats = FindBoats();
            if (!IndicesAuthoritative) return null;
            return index < boats.Count ? boats[index] : null;
        }

        public static ushort IndexOf(Transform boat)
        {
            if (boat == null) return NoBoat;
            var boats = FindBoats();
            if (!IndicesAuthoritative) return NoBoat;
            for (int i = 0; i < boats.Count; i++)
                if (boats[i] == boat) return (ushort)i;
            return NoBoat;
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
