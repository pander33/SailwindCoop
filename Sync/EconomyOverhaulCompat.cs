using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Co-op with the Economy Overhaul mod (<c>DogEggz.EconomyOverhaul</c>, 1.1.5). It keeps one economy
    /// state per machine (<c>EconomyOverhaul.World.State</c>, loaded from the host's save on a guest)
    /// and is written for one player with one loan book. Analysis and the choices: <c>ECONOMY_OVERHAUL_PLAN.md</c>.
    ///
    /// <para>What this does, all by reflection; without the mod, or when a member moved, that part is
    /// left out and logged once:</para>
    /// <list type="bullet">
    /// <item>Finance belongs to the host (option A). On a client the daily loan and rental processing
    /// does not run (it would charge the host's loans to the guest), the loan, rental, repayment and
    /// bankruptcy actions are refused with a notice, nothing EO debits reaches the client's wallet, and
    /// financed cargo cannot be used there.</item>
    /// <item>EO's trade book creates and destroys goods itself (<c>BulkTrade.Execute</c>), past the
    /// game's market calls that <see cref="ItemPatches"/> hook. On a client the goods it creates are
    /// handed to the host as client-authored items (and put into the cart once the host's id lands), and
    /// the goods it destroys are consumed on the host.</item>
    /// <item>A plank repair on a client (<c>WoodPlanks.Finish</c> writes the hull directly) goes to the
    /// host as damage requests, and the plank is consumed there.</item>
    /// <item>Pole bracing on a client (<c>PoleBrace.Push</c> pushes the bodies directly) goes to the host
    /// through the deck force path.</item>
    /// <item>Mission modifiers, kept by EO per offer object, travel with a client's accept and with
    /// the journal (<see cref="MissionSync"/>).</item>
    /// </list>
    /// </summary>
    internal static partial class EconomyOverhaulCompat
    {
        internal const string Guid = "DogEggz.EconomyOverhaul";
        private const string HostOnlyText = "In co-op, loans and ship rentals are the host's.";
        private const string FinancedText = "In co-op, financed cargo can only be used by the host.";

        private static bool _resolved;
        private static Assembly _assembly;
        private static MethodInfo _boughtBack;
        private static CoopLog.Repeat _faults;

        internal static bool Present => Resolve();

        private static bool Resolve()
        {
            if (_resolved) return _assembly != null;
            _resolved = true;
            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out BepInEx.PluginInfo info) && info?.Instance != null)
                    _assembly = info.Instance.GetType().Assembly;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[EconomyOverhaul] lookup failed: " + e.Message); }
            return _assembly != null;
        }

        private static Type T(string name) => _assembly?.GetType("EconomyOverhaul." + name);

        private static bool IsClient
        {
            get
            {
                var net = CoopBehaviour.Instance?.Net;
                return net != null && net.Role == Role.Client && net.State == LinkState.Connected;
            }
        }

        private static void Notify(string text)
        {
            try { if (NotificationUi.instance) NotificationUi.instance.ShowNotification(text); } catch { }
        }

        private static void Fault(string where, Exception e) =>
            Plugin.Logger?.ReportError("[EconomyOverhaul] " + where, e.Message, ref _faults);

        // -----------------------------------------------------------------
        // Patches. Installed from CoopBehaviour.Start: the loader may start this plugin before EO,
        // so its assembly is looked up once every plugin has loaded.
        // -----------------------------------------------------------------

        internal static void Apply(Harmony harmony)
        {
            if (!Resolve()) return;
            int ok = 0, total = 0;
            void Prefix(string type, string method, string handler) { total++; if (Patch(harmony, type, method, prefix: handler)) ok++; }

            // Finance on the host only.
            Prefix("Bottomry", "AdvanceDay", nameof(SkipOnClient));
            Prefix("Rentals", "AdvanceDay", nameof(SkipOnClient));
            Prefix("Loans", "Debit", nameof(SkipOnClient));
            Prefix("Loans", "BlockReason", nameof(RefuseText));
            Prefix("Loans", "BankruptcyRefusal", nameof(RefuseText));
            Prefix("Bottomry", "Refusal", nameof(RefuseText));
            Prefix("Rentals", "Refusal", nameof(RefuseText));
            Prefix("Rentals", "ReturnRefusal", nameof(RefuseText));
            Prefix("Bottomry", "RepayRemote", nameof(RefuseBool));
            Prefix("Bottomry", "RepayAtBox", nameof(RefuseVoid));
            Prefix("Loans", "Use", nameof(RefuseFinancedUse));
            try
            {
                var bottomry = T("Bottomry");
                _boughtBack = bottomry != null ? AccessTools.Method(bottomry, "BoughtBack", new[] { typeof(PurchasableBoat) }) : null;
                if (_boughtBack == null) Plugin.Logger.LogWarning("[EconomyOverhaul] not found: Bottomry.BoughtBack");
            }
            catch (Exception e) { Fault("find buyback", e); }
            total += 3;
            try
            {
                var guard = Hook(nameof(RefuseFinancedUnseal)); guard.priority = Priority.First;
                harmony.Patch(AccessTools.Method(typeof(ShipItemCrate), "UnsealCrate"), prefix: guard);
                ok++;
                guard = Hook(nameof(RefuseFinancedSale)); guard.priority = Priority.First;
                harmony.Patch(AccessTools.Method(typeof(Shopkeeper), "TryToBuyItem"), prefix: guard);
                ok++;
                guard = Hook(nameof(RefuseFinancedSacrifice)); guard.priority = Priority.First;
                harmony.Patch(AccessTools.Method(typeof(KiciaAltar), "OnTriggerEnter"), prefix: guard);
                ok++;
            }
            catch (Exception e) { Fault("finance action guards", e); }
            _tradeQuote = AccessTools.Method(T("BulkTrade"), "Quote");
            _tradeUnits = Field("TradeBatch", "Units");
            _tradeCargo = Field("TradeUnit", "Cargo");

            // The trade book's own goods.
            total++; if (Patch(harmony, "BulkTrade", "Execute", prefix: nameof(TradeBegin), finalizer: nameof(TradeEnd))) ok++;
            total++;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(ShipItem), nameof(ShipItem.DestroyItem)),
                    prefix: new HarmonyMethod(typeof(EconomyOverhaulCompat), nameof(TradeDestroy)));
                ok++;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[EconomyOverhaul] ShipItem.DestroyItem: " + e.Message); }

            // Planks and poles.
            total++; if (Patch(harmony, "WoodPlanks", "Finish", prefix: nameof(PlankBefore), postfix: nameof(PlankAfter))) ok++;
            total++; if (Patch(harmony, "PoleBrace", "Push", transpiler: nameof(PoleForces))) ok++;

            // One set of markets (MarketSync).
            ApplyMarkets(harmony, ref ok, ref total);
            total++;
            if (ShopPatches.PatchPurchaseDebit(harmony, T("FxShops"), "Trade",
                new[] { typeof(Shopkeeper), typeof(ShipItem), typeof(bool) })) ok++;

            _offers = Field("Missions", "offerModifiers");
            _sources = Field("Missions", "sources");
            _category = AccessTools.Method(T("Missions"), "Category");
            _state = Field("World", "State");
            _modifierType = T("MissionModifier");
            _missionStateType = T("MissionState");

            Plugin.Logger.LogInfo("[EconomyOverhaul] found: " + ok + "/" + total + " patches; missions " +
                                  (_offers != null && _state != null ? "on" : "off"));
            PatchHealth.Report("Economy Overhaul", ok, total);
        }

        private static FieldInfo Field(string type, string name)
        {
            var t = T(type);
            return t != null ? AccessTools.Field(t, name) : null;
        }

        private static bool Patch(Harmony harmony, string type, string method, string prefix = null, string postfix = null,
                                  string finalizer = null, string transpiler = null)
        {
            try
            {
                var t = T(type);
                var target = t != null ? AccessTools.Method(t, method) : null;
                if (target == null) { Plugin.Logger.LogWarning("[EconomyOverhaul] not found: " + type + "." + method); return false; }
                harmony.Patch(target, prefix: Hook(prefix), postfix: Hook(postfix), transpiler: Hook(transpiler),
                              finalizer: Hook(finalizer), ilmanipulator: null);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[EconomyOverhaul] " + type + "." + method + ": " + e.Message); return false; }
        }

        private static HarmonyMethod Hook(string name) => name == null ? null
            : new HarmonyMethod(typeof(EconomyOverhaulCompat).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // ---- Finance ---------------------------------------------------------

        // A guest purchase bypasses EO's local purchase-button finalizer on the host.
        internal static void CompleteRemoteBuyback(PurchasableBoat boat)
        {
            try { _boughtBack?.Invoke(null, new object[] { boat }); }
            catch (Exception e) { Fault("complete remote buyback", e); }
        }

        private static bool SkipOnClient() => !IsClient;

        private static bool RefuseText(ref string __result)
        {
            if (!IsClient) return true;
            __result = HostOnlyText;
            return false;
        }

        private static bool RefuseBool(ref bool __result)
        {
            if (!IsClient) return true;
            Notify(HostOnlyText);
            __result = false;
            return false;
        }

        private static bool RefuseVoid()
        {
            if (!IsClient) return true;
            Notify(HostOnlyText);
            return false;
        }

        private static MethodInfo _find;
        private static FieldInfo _creditCargo, _creditLoan;
        internal static bool LocalFinanced(ShipItem item)
        {
            if (!Present || item == null) return false;
            try
            {
                if (_find == null) _find = AccessTools.Method(T("Loans"), "Find", new[] { typeof(ShipItem) });
                return _find?.Invoke(null, new object[] { item }) != null;
            }
            catch (Exception e) { Fault("financed cargo", e); return false; }
        }

        internal static bool ReadFinanced(out int[] ids)
        {
            ids = new int[0];
            if (!Present) return true;
            try
            {
                if (_creditCargo == null) _creditCargo = Field("World", "Cargo");
                if (_creditLoan == null) _creditLoan = Field("CargoState", "loan");
                var cargo = _creditCargo?.GetValue(null) as IDictionary;
                if (cargo == null || _creditLoan == null) return false;
                var found = new List<int>();
                foreach (DictionaryEntry entry in cargo)
                    if (entry.Value != null && (bool)_creditLoan.GetValue(entry.Value)) found.Add((int)entry.Key);
                ids = found.ToArray();
                return true;
            }
            catch (Exception e) { Fault("read financed cargo", e); return false; }
        }

        internal static void ClearSettledLoans(HashSet<int> financed)
        {
            if (!IsClient || !Present) return;
            try
            {
                if (_creditCargo == null) _creditCargo = Field("World", "Cargo");
                if (_creditLoan == null) _creditLoan = Field("CargoState", "loan");
                if (!(_creditCargo?.GetValue(null) is IDictionary cargo) || _creditLoan == null) return;
                // The host may have repaid a loan since this guest loaded the save. Leaving the
                // old local marker makes EO withhold the principal again when the guest sells it.
                // Do not call Loans.Close: its reputation bookkeeping belongs to the host too.
                foreach (DictionaryEntry entry in cargo)
                    if (entry.Value != null && !financed.Contains((int)entry.Key) && (bool)_creditLoan.GetValue(entry.Value))
                        _creditLoan.SetValue(entry.Value, false);
            }
            catch (Exception e) { Fault("clear settled cargo loans", e); }
        }

        private static bool RefuseFinancedUnseal(ShipItemCrate __instance)
        {
            try
            {
                if (!(FinancedCargoSync.Instance?.Blocks(__instance) ?? false)) return true;
                Notify(FinancedText);
                return false;
            }
            catch (Exception e) { Fault("financed unseal", e); return true; }
        }

        private static bool RefuseFinancedSacrifice(Collider other)
        {
            try
            {
                var item = other != null ? other.GetComponent<ShipItem>() : null;
                if (!(FinancedCargoSync.Instance?.Blocks(item) ?? false)) return true;
                // EO replaces destruction inside the original trigger with Loans.Sacrifice.
                // Stop the whole trigger before its effects; Loans.Use is not called on this path.
                Notify(FinancedText);
                return false;
            }
            catch (Exception e) { Fault("financed sacrifice", e); return true; }
        }

        // HarmonyX still runs EO's prefix after false. EO ignores __runOriginal, but accepts null.
        private static void RefuseFinancedSale(ref ShipItem item)
        {
            try
            {
                if (!(FinancedCargoSync.Instance?.Blocks(item) ?? false)) return;
                Notify(FinancedText);
                item = null;
            }
            catch (Exception e) { Fault("financed sale", e); }
        }

        private static bool RefuseFinancedUse(ShipItem item, ref bool __result)
        {
            if (!IsClient) return true;
            try
            {
                if (!(FinancedCargoSync.Instance?.Blocks(item) ?? false)) return true;
            }
            catch (Exception e) { Fault("financed cargo", e); return true; }
            Notify(FinancedText);
            __result = false;
            return false;
        }

        // ---- Trade book goods -------------------------------------------------

        private const int TradeLoan = 1; // EconomyOverhaul.TradeAction: Buy, Loan, Sell
        private static MethodInfo _tradeQuote;
        private static FieldInfo _tradeUnits, _tradeCargo;
        private static bool _inTrade;
        private static HashSet<int> _tradeIds;

        private static bool TradeBegin(object[] __args)
        {
            if (!IsClient) return true;
            try
            {
                if (__args != null && __args.Length > 1 && Convert.ToInt32(__args[1]) == TradeLoan)
                {
                    Notify(HostOnlyText);
                    return false;
                }
                if (__args != null && __args.Length == 3 && Convert.ToInt32(__args[1]) == 2 && _tradeQuote != null && _tradeUnits != null && _tradeCargo != null)
                {
                    var batch = _tradeQuote.Invoke(null, __args);
                    if (_tradeUnits.GetValue(batch) is IEnumerable units)
                        foreach (var unit in units)
                        {
                            var cargo = _tradeCargo.GetValue(unit) as Good;
                            if (cargo == null || !(FinancedCargoSync.Instance?.Blocks(cargo.GetComponent<ShipItem>()) ?? false)) continue;
                            Notify(FinancedText);
                            return false;
                        }
                }
                _tradeIds = new HashSet<int>();
                foreach (var item in ShipItemRegistry.Active())
                {
                    var save = item != null ? item.GetComponent<SaveablePrefab>() : null;
                    if (save != null && save.instanceId > 0) _tradeIds.Add(save.instanceId);
                }
                _inTrade = true;
                MarketSync.Instance?.BeginTrade();
            }
            catch (Exception e) { Fault("trade start", e); _inTrade = false; }
            return true;
        }

        private static Exception TradeEnd(Exception __exception)
        {
            // A host loan is published before its newly created items enter the ordinary item stream.
            try { if (!IsClient) FinancedCargoSync.Instance?.Tick(force: true); }
            catch (Exception e) { Fault("publish financed cargo", e); }
            if (!_inTrade) return __exception;
            _inTrade = false;
            try { MarketSync.Instance?.EndTrade(); }
            catch (Exception e) { Fault("trade book market", e); }
            try
            {
                var sync = ItemSync.Instance;
                if (sync == null || _tradeIds == null) return __exception;
                foreach (var item in ShipItemRegistry.Active())
                {
                    if (item == null || !item.sold || item.GetComponent<Good>() == null) continue;
                    var save = item.GetComponent<SaveablePrefab>();
                    if (save == null || save.instanceId <= 0 || _tradeIds.Contains(save.instanceId)) continue;
                    sync.NotifyClientAuthored(item);
                    if (InCart(item)) sync.QueueCargoAfterAuthor(item);
                }
            }
            catch (Exception e) { Fault("trade end", e); }
            finally { _tradeIds = null; }
            return __exception;
        }

        private static bool InCart(ShipItem item)
        {
            if (CargoCarrier.carriers == null) return false;
            foreach (var carrier in CargoCarrier.carriers)
                if (carrier != null && carrier.cargo != null && carrier.cargo.Contains(item)) return true;
            return false;
        }

        // Runs for every DestroyItem; only a client inside a trade book action does anything.
        private static void TradeDestroy(ShipItem __instance)
        {
            if (!_inTrade || __instance == null) return;
            try
            {
                var save = __instance.GetComponent<SaveablePrefab>();
                if (save != null) ItemSync.Instance?.NotifySold(save.instanceId, save.prefabIndex);
            }
            catch (Exception e) { Fault("trade sale", e); }
        }

        // ---- Planks ------------------------------------------------------------

        private struct PlankState
        {
            internal bool Active;
            internal float Damage, Oakum;
            internal int Id, Prefab;
        }

        private static void PlankBefore(ShipItem plank, BoatDamage hull, out PlankState __state)
        {
            __state = default(PlankState);
            if (!IsClient || hull == null) return;
            try
            {
                var save = plank != null ? plank.GetComponent<SaveablePrefab>() : null;
                __state = new PlankState { Active = true, Damage = hull.hullDamage, Oakum = hull.oakum,
                                           Id = save != null ? save.instanceId : 0, Prefab = save != null ? save.prefabIndex : 0 };
                // Before the mod destroys it: the host consumes its copy.
                ItemSync.Instance?.NotifySold(__state.Id, __state.Prefab);
            }
            catch (Exception e) { Fault("plank start", e); }
        }

        private static void PlankAfter(BoatDamage hull, PlankState __state)
        {
            if (!__state.Active || hull == null) return;
            try
            {
                var damage = BoatDamageSync.Instance;
                if (damage == null) return;
                // Oakum first: the host clamps oakum to what the remaining damage holds after each step,
                // and only this order gives the mod's own result.
                float oakum = __state.Oakum - hull.oakum, repaired = __state.Damage - hull.hullDamage;
                if (oakum > 0f) damage.NotifyLocalDamageAction(DamageAction.RemoveOakum, oakum, hull);
                if (repaired > 0f) damage.NotifyLocalDamageAction(DamageAction.RepairHull, repaired, hull);
            }
            catch (Exception e) { Fault("plank repair", e); }
        }

        // ---- Poles -------------------------------------------------------------

        private static IEnumerable<CodeInstruction> PoleForces(IEnumerable<CodeInstruction> instructions)
        {
            var force = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition), new[] { typeof(Vector3), typeof(Vector3) });
            var replacement = AccessTools.Method(typeof(EconomyOverhaulCompat), nameof(PoleForce));
            int calls = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(force))
                {
                    var call = new CodeInstruction(OpCodes.Call, replacement);
                    call.labels.AddRange(instruction.labels);
                    call.blocks.AddRange(instruction.blocks);
                    yield return call;
                    calls++;
                    continue;
                }
                yield return instruction;
            }
            if (calls == 0) throw new InvalidOperationException("no AddForceAtPosition in PoleBrace.Push");
        }

        // Every physics step while a pole pushes: no allocation.
        private static void PoleForce(Rigidbody body, Vector3 force, Vector3 point)
        {
            body.AddForceAtPosition(force, point);
            if (!IsClient) return;
            try { InteractionSync.Instance?.CaptureBodyPush(body, force, point); }
            catch (Exception e) { Fault("pole push", e); }
        }

        // ---- Missions ------------------------------------------------------------

        private static FieldInfo _offers, _sources, _state;
        private static MethodInfo _category;
        private static Type _modifierType, _missionStateType;

        /// <summary>Client: the modifiers and the world-tab flag EO rolled for this offer.</summary>
        internal static bool ReadOffer(Mission mission, out int modifiers, out bool world)
        {
            modifiers = 0; world = false;
            if (mission == null || _offers == null) return false;
            try
            {
                if (_offers.GetValue(null) is IDictionary offers && offers.Contains(mission)) modifiers = Convert.ToInt32(offers[mission]);
                if (_sources?.GetValue(null) is IDictionary sources && sources.Contains(mission)) world = (bool)sources[mission];
                else world = mission.distance >= 140f;
                return true;
            }
            catch (Exception e) { Fault("read offer", e); return false; }
        }

        /// <summary>Host: give the mission built from a client's accept the client's offer data, so
        /// EO's own accept records it.</summary>
        internal static void WriteOffer(Mission mission, int modifiers, bool world)
        {
            if (mission == null || _offers == null || _modifierType == null) return;
            try
            {
                if (_offers.GetValue(null) is IDictionary offers) offers[mission] = Enum.ToObject(_modifierType, modifiers);
                if (_sources?.GetValue(null) is IDictionary sources) sources[mission] = world;
            }
            catch (Exception e) { Fault("write offer", e); }
        }

        private static IList Records()
        {
            var state = _state?.GetValue(null);
            return state != null ? AccessTools.Field(state.GetType(), "missions")?.GetValue(state) as IList : null;
        }

        /// <summary>Host: EO's record of the mission in a journal slot.</summary>
        internal static bool ReadRecord(int slot, out int modifiers, out bool world)
        {
            modifiers = 0; world = false;
            if (_missionStateType == null) return false;
            try
            {
                var records = Records();
                if (records == null) return false;
                foreach (var record in records)
                {
                    if (record == null || (int)Get(record, "slot") != slot) continue;
                    modifiers = (int)Get(record, "modifiers");
                    world = (bool)Get(record, "world");
                    return true;
                }
            }
            catch (Exception e) { Fault("read record", e); }
            return false;
        }

        /// <summary>Client: EO's records follow the journal the host sent, so the mission screens show
        /// the host's modifiers. Missions are delivered and paid on the host; these records are display.</summary>
        internal static void MirrorRecords(MissionEntry[] entries)
        {
            if (_missionStateType == null || entries == null) return;
            try
            {
                var records = Records();
                if (records == null) return;
                records.Clear();
                foreach (var e in entries)
                {
                    if ((e.ModFlags & 1) == 0 || PlayerMissions.missions == null || e.MissionIndex >= PlayerMissions.missions.Length) continue;
                    var mission = PlayerMissions.missions[e.MissionIndex];
                    var record = Activator.CreateInstance(_missionStateType);
                    Set(record, "slot", (int)e.MissionIndex);
                    Set(record, "origin", e.OriginPort);
                    Set(record, "destination", e.DestinationPort);
                    Set(record, "prefab", e.GoodPrefabIndex);
                    Set(record, "dueDay", e.DueDay);
                    Set(record, "totalPrice", e.TotalPrice);
                    Set(record, "world", (e.ModFlags & 2) != 0);
                    Set(record, "creditedDeliveries", e.DeliveredGoods);
                    Set(record, "modifiers", e.ModModifiers);
                    if (_category != null && mission != null) Set(record, "category", Convert.ToInt32(_category.Invoke(null, new object[] { mission })));
                    records.Add(record);
                }
            }
            catch (Exception e) { Fault("mirror records", e); }
        }

        private static object Get(object target, string field) => AccessTools.Field(target.GetType(), field).GetValue(target);
        private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field)?.SetValue(target, value);
    }
}
