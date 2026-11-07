using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The reflection side of <see cref="MarketSync"/>: Economy Overhaul's market bonus, currencies and
    /// demand events, the parts of its simulation a mirroring client stops, and the trades whose effect
    /// a client measures.
    /// </summary>
    internal static partial class EconomyOverhaulCompat
    {
        private static FieldInfo _worldMarkets, _bonus, _eventsField;
        private static MethodInfo _marketData, _refresh, _award, _demandCheck, _fxEnsure, _fxSync;
        private static PropertyInfo _demandState;
        private static Type _demandEventType;
        private static FieldInfo _fxDay, _fxRates, _fxBasis, _fxSmooth, _fxCloses, _fxCargo, _fxShops, _fxNpc;
        private static FieldInfo _evId, _evDemand, _evProducer, _evGood, _evStart, _evAmount, _evAdded;

        /// <summary>Every member the shared markets need was found.</summary>
        internal static bool MarketsOn { get; private set; }

        private static void ApplyMarkets(Harmony harmony, ref int ok, ref int total)
        {
            var world = T("World");
            var economy = T("Economy");
            var demand = T("Demand");
            var fx = T("Fx");
            var fxState = T("FxState");
            _demandEventType = T("DemandEvent");
            var marketState = T("MarketState");
            var demandState = T("DemandState");
            try
            {
                _worldMarkets = world != null ? AccessTools.Field(world, "Markets") : null;
                _marketData = world != null ? AccessTools.Method(world, "MarketData", new[] { typeof(int) }) : null;
                _bonus = marketState != null ? AccessTools.Field(marketState, "bonus") : null;
                _refresh = economy != null ? AccessTools.Method(economy, "Refresh", new[] { typeof(IslandMarket) }) : null;
                _award = economy != null ? AccessTools.Method(economy, "Award", new[] { typeof(int), typeof(float) }) : null;
                _demandCheck = demand != null ? AccessTools.Method(demand, "Check", new[] { typeof(int), typeof(int) }) : null;
                _demandState = demand != null ? AccessTools.Property(demand, "State") : null;
                _eventsField = demandState != null ? AccessTools.Field(demandState, "events") : null;
                _fxEnsure = fx != null ? AccessTools.Method(fx, "Ensure") : null;
                _fxSync = fx != null ? AccessTools.Method(fx, "Sync") : null;
                if (fxState != null)
                {
                    _fxDay = AccessTools.Field(fxState, "day");
                    _fxRates = AccessTools.Field(fxState, "rates");
                    _fxBasis = AccessTools.Field(fxState, "basis");
                    _fxSmooth = AccessTools.Field(fxState, "smooth");
                    _fxCloses = AccessTools.Field(fxState, "closes");
                    _fxCargo = AccessTools.Field(fxState, "cargo");
                    _fxShops = AccessTools.Field(fxState, "shops");
                    _fxNpc = AccessTools.Field(fxState, "npc");
                }
                if (_demandEventType != null)
                {
                    _evId = AccessTools.Field(_demandEventType, "id");
                    _evDemand = AccessTools.Field(_demandEventType, "demand");
                    _evProducer = AccessTools.Field(_demandEventType, "producer");
                    _evGood = AccessTools.Field(_demandEventType, "good");
                    _evStart = AccessTools.Field(_demandEventType, "startDay");
                    _evAmount = AccessTools.Field(_demandEventType, "amount");
                    _evAdded = AccessTools.Field(_demandEventType, "added");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[EconomyOverhaul] markets: " + e.Message); }

            bool members = _worldMarkets != null && _marketData != null && _bonus != null && _refresh != null && _award != null &&
                           _fxEnsure != null && _fxSync != null && _fxDay != null && _fxRates != null && _fxBasis != null &&
                           _fxSmooth != null && _fxCloses != null && _fxCargo != null && _fxShops != null && _fxNpc != null &&
                           _demandState != null && _eventsField != null && _evId != null && _evDemand != null &&
                           _evProducer != null && _evGood != null && _evStart != null && _evAmount != null && _evAdded != null;

            // A mirroring client stops its own market cycle, demand rolls and currency day. All three or
            // none: half a simulation would drift as much as a whole one.
            int stops = 0;
            total += 3;
            if (members && Patch(harmony, "Economy", "Cycle", prefix: nameof(SkipWhenMirrored))) stops++;
            if (members && Patch(harmony, "Demand", "Midnight", prefix: nameof(SkipWhenMirrored))) stops++;
            if (members && Patch(harmony, "Fx", "AdvanceDay", prefix: nameof(SkipWhenMirrored))) stops++;
            ok += stops;
            _stopsComplete = stops == 3;

            // A client's trades. The trade book is measured in TradeBegin/TradeEnd.
            total++; if (members && Patch(harmony, "FxBooth", "Confirm", prefix: nameof(MarketTradeBegin), finalizer: nameof(MarketTradeEnd))) ok++;
            total++; if (members && PatchFirst(harmony, typeof(Shopkeeper), "TryToSellItem")) ok++;
            total++; if (members && PatchFirst(harmony, typeof(Shopkeeper), "TryToBuyItem")) ok++;

            MarketsOn = members && _stopsComplete;
            Plugin.Logger.LogInfo("[EconomyOverhaul] shared markets " + (MarketsOn ? "on" : "off (members missing)"));
        }

        private static bool _stopsComplete;

        // Before EO's own prefix, which does the shop trade and skips the game's method.
        private static bool PatchFirst(Harmony harmony, Type type, string method)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) { Plugin.Logger.LogWarning("[EconomyOverhaul] not found: " + type.Name + "." + method); return false; }
                var begin = Hook(nameof(MarketTradeBegin));
                begin.priority = Priority.First;
                harmony.Patch(target, prefix: begin, finalizer: Hook(nameof(MarketTradeEnd)), ilmanipulator: null);
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[EconomyOverhaul] " + type.Name + "." + method + ": " + e.Message); return false; }
        }

        private static bool SkipWhenMirrored() => !(MarketSync.Instance?.Mirrors ?? false);

        private static void MarketTradeBegin()
        {
            try { MarketSync.Instance?.BeginTrade(); }
            catch (Exception e) { Fault("market trade start", e); }
        }

        private static Exception MarketTradeEnd(Exception __exception)
        {
            try { MarketSync.Instance?.EndTrade(); }
            catch (Exception e) { Fault("market trade end", e); }
            return __exception;
        }

        // ---- Market bonus ------------------------------------------------------------

        /// <summary>EO's capacity bonus of a port, NaN when it keeps no record for it.</summary>
        internal static float MarketBonus(int port)
        {
            try
            {
                if (_worldMarkets?.GetValue(null) is IDictionary markets && markets.Contains(port))
                {
                    var state = markets[port];
                    if (state != null) return (float)_bonus.GetValue(state);
                }
            }
            catch (Exception e) { Fault("read bonus", e); }
            return float.NaN;
        }

        /// <summary>Client: the host's bonus; the port's capacity and buy limit follow it.</summary>
        internal static void SetMarketBonus(int port, float bonus)
        {
            if (MarketBonus(port) == bonus) return;
            try
            {
                var state = _marketData.Invoke(null, new object[] { port });
                if (state == null) return;
                _bonus.SetValue(state, bonus);
                // Award with nothing: the mod's own way to apply a bonus to the market.
                _award.Invoke(null, new object[] { port, 0f });
            }
            catch (Exception e) { Fault("write bonus", e); }
        }

        /// <summary>The market's own price report after its stock changed, as EO does after a cycle.</summary>
        internal static void RefreshMarket(IslandMarket market)
        {
            if (market == null || _refresh == null) return;
            try { _refresh.Invoke(null, new object[] { market }); }
            catch (Exception e) { Fault("refresh market", e); }
        }

        /// <summary>Host: a client's sale may end a demand event, as a sale on the host does.</summary>
        internal static void CheckDemand(int port, int good)
        {
            if (_demandCheck == null) return;
            try { _demandCheck.Invoke(null, new object[] { port, good }); }
            catch (Exception e) { Fault("demand check", e); }
        }

        internal static void RefreshTradeBook()
        {
            try { if (EconomyUI.instance && EconomyUI.instance.uiActive) EconomyUI.instance.RefreshPage(); }
            catch (Exception e) { Fault("trade book refresh", e); }
        }

        // ---- Currencies --------------------------------------------------------------

        private static object FxState()
        {
            try { return _fxEnsure?.Invoke(null, null); }
            catch (Exception e) { Fault("currency state", e); return null; }
        }

        private static double[] Doubles(FieldInfo field, object state) => (double[])field.GetValue(state) ?? new double[0];

        /// <summary>Host: the currency state, and all of it as one array to compare with the last one sent.</summary>
        internal static double[] ReadFx(out int day, out double[] rates, out double[] basis, out double[] smooth, out double[] closes)
        {
            day = 0; rates = basis = smooth = closes = null;
            var state = FxState();
            if (state == null) return null;
            try
            {
                day = (int)_fxDay.GetValue(state);
                rates = (double[])Doubles(_fxRates, state).Clone();
                basis = (double[])Doubles(_fxBasis, state).Clone();
                smooth = (double[])Doubles(_fxSmooth, state).Clone();
                closes = (double[])Doubles(_fxCloses, state).Clone();
                var view = new double[1 + rates.Length + basis.Length + smooth.Length + closes.Length];
                view[0] = day;
                int at = 1;
                foreach (var part in new[] { rates, basis, smooth, closes }) { Array.Copy(part, 0, view, at, part.Length); at += part.Length; }
                return view;
            }
            catch (Exception e) { Fault("read currencies", e); return null; }
        }

        /// <summary>Client: the host's currencies. The pressure this client queued is cleared: its trades
        /// went to the host, and the host settles the day.</summary>
        internal static void WriteFx(int day, double[] rates, double[] basis, double[] smooth, double[] closes)
        {
            var state = FxState();
            if (state == null) return;
            try
            {
                if (!Usable(rates) || !Usable(basis) || !Usable(smooth)) return;
                _fxDay.SetValue(state, day);
                _fxRates.SetValue(state, rates);
                _fxBasis.SetValue(state, basis);
                _fxSmooth.SetValue(state, smooth);
                _fxCloses.SetValue(state, closes ?? new double[0]);
                foreach (var queue in new[] { _fxCargo, _fxShops, _fxNpc })
                {
                    var values = (double[])queue.GetValue(state);
                    if (values != null) Array.Clear(values, 0, values.Length);
                }
                _fxSync.Invoke(null, null);
            }
            catch (Exception e) { Fault("write currencies", e); }
        }

        // EO refuses to save a currency state that is not four positive rates (Fx.Validate).
        private static bool Usable(double[] values)
        {
            if (values == null || values.Length != 4) return false;
            foreach (double v in values) if (!(v > 0) || double.IsInfinity(v)) return false;
            return true;
        }

        /// <summary>Client: the queued pressure and the live rates, copied, around a trade.</summary>
        internal static void ReadFxQueues(out double[] cargo, out double[] shops, out double[] rates)
        {
            cargo = shops = rates = null;
            var state = FxState();
            if (state == null) return;
            try
            {
                cargo = (double[])Doubles(_fxCargo, state).Clone();
                shops = (double[])Doubles(_fxShops, state).Clone();
                rates = (double[])Doubles(_fxRates, state).Clone();
            }
            catch (Exception e) { Fault("read currency queues", e); }
        }

        /// <summary>Host: a client's trade pressure, settled with the host's at midnight, and a client's
        /// booth exchange applied to the live rates (a move that would leave a rate not positive is left out).</summary>
        internal static void AddFx(double[] cargo, double[] shops, double[] rates)
        {
            if ((cargo == null || cargo.Length == 0) && (shops == null || shops.Length == 0) && (rates == null || rates.Length == 0)) return;
            var state = FxState();
            if (state == null) return;
            try
            {
                Add(Doubles(_fxCargo, state), cargo, false);
                Add(Doubles(_fxShops, state), shops, false);
                if (rates != null && rates.Length > 0)
                {
                    Add(Doubles(_fxRates, state), rates, true);
                    _fxSync.Invoke(null, null);
                }
            }
            catch (Exception e) { Fault("add currencies", e); }
        }

        private static void Add(double[] target, double[] delta, bool positive)
        {
            if (target == null || delta == null) return;
            // Gold (3) is fixed: EO refuses a state where it moved.
            for (int i = 0; i < Math.Min(3, Math.Min(target.Length, delta.Length)); i++)
            {
                double value = target[i] + delta[i];
                if (double.IsNaN(value) || double.IsInfinity(value) || (positive && !(value > 0))) continue;
                target[i] = value;
            }
        }

        // ---- Demand events -------------------------------------------------------------

        private static IList Events()
        {
            var state = _demandState?.GetValue(null, null);
            return state != null ? _eventsField.GetValue(state) as IList : null;
        }

        /// <summary>Host: the demand events, and the same as one array to compare with the last one sent.</summary>
        internal static MarketEvent[] ReadDemand(out double[] view)
        {
            view = null;
            try
            {
                var events = Events();
                if (events == null) return new MarketEvent[0];
                var result = new List<MarketEvent>();
                foreach (var e in events)
                {
                    if (e == null) continue;
                    result.Add(new MarketEvent
                    {
                        Id = (int)_evId.GetValue(e),
                        Demand = (short)(int)_evDemand.GetValue(e),
                        Producer = (short)(int)_evProducer.GetValue(e),
                        Good = (short)(int)_evGood.GetValue(e),
                        StartDay = (int)_evStart.GetValue(e),
                        Amount = (float)_evAmount.GetValue(e),
                        Added = (float)_evAdded.GetValue(e),
                    });
                }
                view = new double[result.Count * 7];
                for (int i = 0; i < result.Count; i++)
                {
                    var e = result[i];
                    view[i * 7] = e.Id; view[i * 7 + 1] = e.Demand; view[i * 7 + 2] = e.Producer; view[i * 7 + 3] = e.Good;
                    view[i * 7 + 4] = e.StartDay; view[i * 7 + 5] = e.Amount; view[i * 7 + 6] = e.Added;
                }
                return result.ToArray();
            }
            catch (Exception e) { Fault("read demand", e); view = null; return new MarketEvent[0]; }
        }

        /// <summary>Client: the host's demand events (the rumours and the trade book's notes read them).</summary>
        internal static void WriteDemand(MarketEvent[] events)
        {
            try
            {
                var list = Events();
                if (list == null || events == null) return;
                list.Clear();
                foreach (var e in events)
                {
                    var record = Activator.CreateInstance(_demandEventType);
                    _evId.SetValue(record, e.Id);
                    _evDemand.SetValue(record, (int)e.Demand);
                    _evProducer.SetValue(record, (int)e.Producer);
                    _evGood.SetValue(record, (int)e.Good);
                    _evStart.SetValue(record, e.StartDay);
                    _evAmount.SetValue(record, e.Amount);
                    _evAdded.SetValue(record, e.Added);
                    list.Add(record);
                }
            }
            catch (Exception e) { Fault("write demand", e); }
        }
    }
}
