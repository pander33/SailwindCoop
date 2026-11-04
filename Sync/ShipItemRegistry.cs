using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Every <see cref="ShipItem"/> that has woken up, kept from a postfix of the private
    /// <c>ShipItem.Awake</c> (no subclass of <c>ShipItem</c> in game 0.40 declares its own <c>Awake</c>).
    /// It replaces the periodic <c>FindObjectsOfType&lt;ShipItem&gt;</c> scene search (~1.2 ms whatever
    /// it finds). <see cref="Snapshot"/> returns exactly what that search would: the items on active
    /// objects; an object that was never active has not woken up and that search does not see it
    /// either. Destroyed items are dropped by the Unity-null check, not from <c>OnDestroy</c>: two
    /// subclasses hide it without calling the base. Without the hook every call is the search.
    /// </summary>
    internal static class ShipItemRegistry
    {
        private static readonly HashSet<ShipItem> _items = new HashSet<ShipItem>();
        private static readonly Predicate<ShipItem> IsGone = item => item == null;
        private static bool _seeded;

        // Temporary check of the registry against the search it replaces; remove after the game check.
        private const float VerifySeconds = 30f;
        private static float _nextVerify;

        /// <summary>True when the Awake hook is installed.</summary>
        internal static bool Ready { get; private set; }

        internal static void Add(ShipItem item)
        {
            if (item != null) _items.Add(item);
        }

        /// <summary>Fills <paramref name="into"/> (cleared first) with the items on active objects.</summary>
        internal static void Snapshot(List<ShipItem> into)
        {
            into.Clear();
            if (!Ready)
            {
                into.AddRange(UnityEngine.Object.FindObjectsOfType<ShipItem>());
                return;
            }
            if (!_seeded)
            {
                // Items that woke before the hook was installed.
                _seeded = true;
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>()) Add(item);
            }
            _items.RemoveWhere(IsGone);
            foreach (var item in _items)
                if (item.gameObject.activeInHierarchy) into.Add(item);
            Verify(into);
        }

        /// <summary>A new list of the items on active objects, for a one-off search at a player's action
        /// (the caller may keep or change it).</summary>
        internal static List<ShipItem> Active()
        {
            var list = new List<ShipItem>();
            Snapshot(list);
            return list;
        }

        private static void Verify(List<ShipItem> snapshot)
        {
            if (Time.unscaledTime < _nextVerify) return;
            _nextVerify = Time.unscaledTime + VerifySeconds;
            try
            {
                var found = UnityEngine.Object.FindObjectsOfType<ShipItem>();
                var mine = new HashSet<ShipItem>(snapshot);
                var missing = new List<string>();
                foreach (var item in found)
                    if (item != null && !mine.Remove(item))
                    {
                        _items.Add(item);
                        if (missing.Count < 5) missing.Add(item.name);
                    }
                if (missing.Count == 0 && mine.Count == 0) return;
                var extra = new List<string>();
                foreach (var item in mine) if (extra.Count < 5) extra.Add(item != null ? item.name : "null");
                Plugin.Logger.LogWarning("[ShipItemRegistry] differs from the scene search: " + found.Length +
                    " found, " + snapshot.Count + " registered; missing [" + string.Join(", ", missing.ToArray()) +
                    "], extra [" + string.Join(", ", extra.ToArray()) + "]");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[ShipItemRegistry] check failed: " + e.Message); }
        }

        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(ShipItem), "Awake", Type.EmptyTypes, m =>
            {
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(ShipItemRegistry).GetMethod(nameof(PostAwake),
                    BindingFlags.Static | BindingFlags.NonPublic)));
                Ready = true;
            });
            PatchHealth.Report("Item registry", hooks);
        }

        private static void PostAwake(ShipItem __instance)
            => PatchGuard.Run(() => Add(__instance), Report);

        private static void Report(Exception error)
        {
            try { Plugin.Logger?.LogWarning("[ShipItemRegistry] " + error); } catch { }
        }
    }
}
