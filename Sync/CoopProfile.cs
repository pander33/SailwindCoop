using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The guest's persistent CHARACTER profile, independent of any world save.
    ///
    /// <para>Co-op plays in the host's world (host's <see cref="SaveContainer"/>), but the guest must
    /// keep its own progress: money, reputation, needs, known prices, missions/quests, trade receipts.
    /// Those live in cleanly separated fields of <see cref="SaveContainer"/>; everything else
    /// (world objects, items, economy, NPCs, time/weather) belongs to the host.</para>
    ///
    /// <para>On disconnect the guest writes these fields (read from the live game) to
    /// <c>coop_profile.dat</c>. On join the host's streamed save is merged with this profile via
    /// <see cref="MergeInto"/> before it is loaded, so the guest enters the host's world with its
    /// own wallet/reputation.</para>
    ///
    /// <para>Phase 2 adds the guest's PERSONAL BELT inventory (the 5 <c>GPButtonInventorySlot</c> slots).
    /// Belt items are captured into the profile's <c>savedPrefabs</c> (each carries <c>inventorySlot</c>
    /// 0..4, <c>itemParentObject==0</c>). The merge <see cref="StripPersonalBelt"/> removes the HOST's
    /// belt items from the streamed world (so they don't load into the guest's belt) and injects the
    /// guest's own belt items with fresh instance ids. Belt items are player-local at runtime — see the
    /// <c>HasStableIdentity</c> exclusion + transition handling in <see cref="ItemSync"/>.</para>
    /// </summary>
    public static class CoopProfile
    {
        public static string ProfilePath => Path.Combine(Application.persistentDataPath, "coop_profile.dat");

        public static bool Exists()
        {
            try { return File.Exists(ProfilePath); }
            catch { return false; }
        }

        // -----------------------------------------------------------------
        // Save (guest -> disk): gather character fields from the live game.
        // -----------------------------------------------------------------

        /// <summary>Reads the local player's character state into a profile <see cref="SaveContainer"/>
        /// and writes it to disk. Each field is guarded so a single missing engine member degrades
        /// gracefully instead of losing the whole profile.</summary>
        public static bool SaveFromGame()
        {
            try
            {
                var c = new SaveContainer();
                FillCharacterFromGame(c);

                using (var fs = File.Create(ProfilePath))
                {
                    new BinaryFormatter().Serialize(fs, c);
                }
                Plugin.Logger.LogInfo("[CoopProfile] Character profile saved: " + ProfilePath);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[CoopProfile] Failed to save profile: " + e);
                return false;
            }
        }

        private static void FillCharacterFromGame(SaveContainer c)
        {
            // Use the currency-array path (playerGold==0 makes LoadGame read playerCurrency).
            c.playerGold = 0;
            Try("currency", () => { c.playerCurrency = PlayerGold.currency; c.currentCurrency = GameState.currentCurrency; });
            Try("reputation", () => c.playerReputation = PlayerReputation.GetSaveData());
            Try("knownPrices", () => c.playerKnownPrices = GameState.playerKnownPrices);
            Try("tradeReceipts", () => c.tradeReceipts = TradeReceiptsUI.instance.GetData());
            Try("quests", () => c.quests = Quests.instance.currentQuests);

            Try("needs", () =>
            {
                c.food = PlayerNeeds.food; c.foodDebt = PlayerNeeds.foodDebt;
                c.water = PlayerNeeds.water;
                c.sleep = PlayerNeeds.sleep; c.sleepDebt = PlayerNeeds.sleepDebt;
                c.vitamins = PlayerNeeds.vitamins; c.protein = PlayerNeeds.protein;
                c.alcohol = PlayerNeeds.alcohol;
            });

            Try("tobacco", () =>
            {
                var t = PlayerTobacco.instance;
                c.tobaccoWhite = t.white; c.tobaccoGreen = t.green;
                c.tobaccoBlack = t.black; c.tobaccoBrown = t.brown;
            });

            Try("missions", () =>
            {
                var src = PlayerMissions.missions;
                c.savedMissions = new SaveMissionData[src.Length];
                for (int i = 0; i < src.Length; i++)
                    if (src[i] != null) c.savedMissions[i] = src[i].PrepareSaveData();
            });
            Try("loggedMissions", () => c.loggedMissions = MissionLog.instance.GetLogSaveData());

            Try("dayLogs", () =>
            {
                int n = PlayerGold.currency != null ? PlayerGold.currency.Length : 0;
                c.currencyDayLogs = new DaySheet[n][];
                for (int i = 0; i < n; i++)
                    c.currencyDayLogs[i] = DayLogs.instance.dayLogs[i].GetSaveData();
            });

            // Personal belt inventory (slots 0..4): capture each carried item as SavePrefabData so it can be
            // re-injected into the guest's belt on the next join. PrepareSaveData() stamps inventorySlot 0..4
            // and itemParentObject==0 for belt items.
            Try("belt", () =>
            {
                var list = new List<SavePrefabData>();
                var slots = GPButtonInventorySlot.inventorySlots;
                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot == null || slot.currentItem == null) continue;
                        var sp = slot.currentItem.GetComponent<SaveablePrefab>();
                        if (sp == null) continue;
                        var data = sp.PrepareSaveData();
                        if (data != null) list.Add(data);
                    }
                }
                c.savedPrefabs = list;
            });
        }

        /// <summary>True for a SavePrefabData that represents a personal belt item (slot 0..4, not contained
        /// in a boat/crate). An item that was ever carried off a boat has itemParentObject == -1 (ExitBoat);
        /// an item never on a boat has 0. Both load as free items (LoadGame caches only itemParentObject > 0),
        /// so a belt item is anything with itemParentObject &lt;= 0 in a personal slot 0..4.</summary>
        private static bool IsPersonalBelt(SavePrefabData p)
        {
            return p != null && p.itemParentObject <= 0 && p.inventorySlot >= 0 && p.inventorySlot < 100;
        }

        // -----------------------------------------------------------------
        // Merge (disk -> host's streamed save): overlay character fields.
        // -----------------------------------------------------------------

        /// <summary>If a profile exists, copies its character fields onto <paramref name="host"/>
        /// (the host's streamed world save) so the merged save loads the host's world with the
        /// guest's own progress. No-op (host's values kept) on the very first join with no profile.</summary>
        public static void MergeInto(SaveContainer host)
        {
            if (host == null) return;

            // Always remove the HOST's personal belt items from the streamed world, even on the first session
            // with no profile — otherwise they'd load into the guest's own belt slots.
            int stripped = StripPersonalBelt(host);

            if (!Exists())
            {
                Plugin.Logger.LogInfo("[CoopProfile] Profile missing - first session (host belt stripped: " +
                                      stripped + ", own belt empty)");
                return;
            }

            try
            {
                SaveContainer profile;
                using (var fs = File.Open(ProfilePath, FileMode.Open))
                {
                    profile = (SaveContainer)new BinaryFormatter().Deserialize(fs);
                }
                CopyCharacterFields(profile, host);
                int injected = InjectPersonalBelt(profile, host);
                Plugin.Logger.LogInfo("[CoopProfile] Profile applied (host belt stripped: " + stripped +
                                      ", own belt injected: " + injected + ")");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[CoopProfile] Failed to apply profile (using host character): " + e);
            }
        }

        /// <summary>Removes the host's personal belt items (slots 0..4) from a world container. Returns count.</summary>
        private static int StripPersonalBelt(SaveContainer host)
        {
            if (host == null || host.savedPrefabs == null) return 0;
            return host.savedPrefabs.RemoveAll(IsPersonalBelt);
        }

        /// <summary>Appends the profile's personal belt items to the host world with fresh, collision-free
        /// instance ids. Returns the number injected.</summary>
        private static int InjectPersonalBelt(SaveContainer profile, SaveContainer host)
        {
            if (profile == null || profile.savedPrefabs == null || host == null) return 0;
            if (host.savedPrefabs == null) host.savedPrefabs = new List<SavePrefabData>();

            int nextId = MaxInstanceId(host) + 1000;   // wide buffer above any host-world id
            int n = 0;
            foreach (var p in profile.savedPrefabs)
            {
                if (!IsPersonalBelt(p)) continue;
                p.instanceId = nextId++;   // belt items have no internal references (crateId/parent == 0)
                host.savedPrefabs.Add(p);
                n++;
            }
            return n;
        }

        private static int MaxInstanceId(SaveContainer host)
        {
            int max = 0;
            if (host != null && host.savedPrefabs != null)
                foreach (var p in host.savedPrefabs)
                    if (p != null && p.instanceId > max) max = p.instanceId;
            return max;
        }

        /// <summary>Copies the CHARACTER subset of a <see cref="SaveContainer"/> from src to dst,
        /// leaving all WORLD fields of dst untouched. Null source fields are skipped so we never
        /// clobber the host's world data with a partially-populated profile.</summary>
        private static void CopyCharacterFields(SaveContainer src, SaveContainer dst)
        {
            dst.playerGold = src.playerGold;          // 0 -> use the currency array on load
            if (src.playerCurrency != null) dst.playerCurrency = src.playerCurrency;
            dst.currentCurrency = src.currentCurrency;
            if (src.playerReputation != null) dst.playerReputation = src.playerReputation;
            if (src.playerKnownPrices != null) dst.playerKnownPrices = src.playerKnownPrices;
            if (src.tradeReceipts != null) dst.tradeReceipts = src.tradeReceipts;
            if (src.quests != null) dst.quests = src.quests;

            dst.food = src.food; dst.foodDebt = src.foodDebt;
            dst.water = src.water;
            dst.sleep = src.sleep; dst.sleepDebt = src.sleepDebt;
            dst.vitamins = src.vitamins; dst.protein = src.protein;
            dst.alcohol = src.alcohol;

            dst.tobaccoWhite = src.tobaccoWhite; dst.tobaccoGreen = src.tobaccoGreen;
            dst.tobaccoBlack = src.tobaccoBlack; dst.tobaccoBrown = src.tobaccoBrown;

            if (src.savedMissions != null) dst.savedMissions = src.savedMissions;
            if (src.loggedMissions != null) dst.loggedMissions = src.loggedMissions;
            if (src.currencyDayLogs != null) dst.currencyDayLogs = src.currencyDayLogs;
        }

        private static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Plugin.Logger.LogWarning("[CoopProfile] field '" + what + "' skipped: " + e.Message); }
        }
    }
}
