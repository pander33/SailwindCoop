using System;
using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// One set of markets for the crew when Economy Overhaul runs on the host and the client (stage 7 of
    /// <c>ECONOMY_OVERHAUL_PLAN.md</c>). Without it every machine runs EO's market simulation with its
    /// own random rolls, so stock, prices, demand events and currency rates drift apart within hours.
    ///
    /// <para>The host's markets are the crew's. The host sends each ready client the stock of every
    /// port's market (<c>IslandMarket.currentSupply</c>, from which the game and EO compute prices),
    /// EO's capacity bonus per port, its currency rates and its demand events: what changed since the
    /// last state sent to that client, checked once a second, and everything every 20 s. A client that
    /// has had one stops EO's market cycle, its demand rolls and its currency day (<see cref="Mirrors"/>)
    /// and shows the host's values.</para>
    ///
    /// <para>A client's own trades (the trade book, shops, the currency booth) change its copy at once,
    /// so the price it pays is the one it saw. What a trade changed is measured around the trade
    /// (<see cref="BeginTrade"/>/<see cref="EndTrade"/>) and sent as <see cref="EconomyDeltaMsg"/>; the
    /// host adds it to its markets and acknowledges it in the next state (<c>AckSeq</c>). Until then the
    /// client keeps showing its change on top of the host's values, as the shared wallet does.</para>
    ///
    /// <para>Left per machine: price reports of other ports (the trade book's rumours), EO's trader
    /// fleet and its visible hulls, cargo condition, mission warehouses. The fleet's own trades on a
    /// client change that client's copy only until the next state.</para>
    /// </summary>
    public sealed class MarketSync
    {
        public static MarketSync Instance { get; private set; }

        private const float ScanSecs = 1f;
        private const float FullSecs = 20f;

        private readonly CoopNet _net;
        private Runtime.CoopLog.Repeat _failures;

        public MarketSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>Client: the host shares its markets, so this machine's own market simulation stays off.</summary>
        public bool Mirrors => _mirrors && _net.Role == Role.Client && _net.State == LinkState.Connected;


        // ---- Markets of this world ------------------------------------------------

        private Port[] _portsSeen;
        private IslandMarket[] _markets = new IslandMarket[0];

        private IslandMarket[] Markets()
        {
            var ports = Port.ports;
            if (ports == null) return _markets = new IslandMarket[0];
            bool stale = !ReferenceEquals(ports, _portsSeen) || _markets.Length != ports.Length;
            if (!stale)
                for (int i = 0; i < _markets.Length; i++)
                    if (ports[i] != null && _markets[i] == null) { stale = true; break; }
            if (stale)
            {
                _portsSeen = ports;
                _markets = new IslandMarket[ports.Length];
                for (int i = 0; i < ports.Length; i++)
                    _markets[i] = ports[i] != null ? ports[i].GetComponent<IslandMarket>() : null;
            }
            return _markets;
        }

        private IslandMarket Market(int port)
        {
            var markets = Markets();
            return port >= 0 && port < markets.Length && markets[port] != null && markets[port].currentSupply != null ? markets[port] : null;
        }

        public void Tick()
        {
            try
            {
                if (_net.State != LinkState.Connected || !EconomyOverhaulCompat.MarketsOn) return;
                if (_net.Role == Role.Host) TickHost();
            }
            catch (Exception e) { Plugin.Logger.ReportError("[Markets] Tick failed", e, ref _failures); }
        }

        // ---- Host --------------------------------------------------------------------

        private sealed class PeerView
        {
            public readonly Dictionary<int, float[]> Ports = new Dictionary<int, float[]>();
            public double[] Fx;
            public double[] Demand;
            public float FullAt;
        }

        private readonly Dictionary<uint, PeerView> _peers = new Dictionary<uint, PeerView>();
        private readonly Dictionary<uint, uint> _ackByClient = new Dictionary<uint, uint>();
        private readonly HashSet<uint> _owesAck = new HashSet<uint>();
        private readonly List<MarketPort> _portBuffer = new List<MarketPort>();
        private float _scanAt;

        private void TickHost()
        {
            if (!GameState.playing || GameState.currentlyLoading) return;
            float now = Time.unscaledTime;
            if (now < _scanAt && _owesAck.Count == 0) return;
            _scanAt = now + ScanSecs;

            uint[] ready = _net.ReadyClientIds();
            // A client that left takes its view and its acknowledgement along: one that comes back
            // starts with everything and a fresh count.
            var gone = new List<uint>();
            foreach (uint id in _peers.Keys) if (Array.IndexOf(ready, id) < 0) gone.Add(id);
            foreach (uint id in _ackByClient.Keys) if (Array.IndexOf(ready, id) < 0 && !gone.Contains(id)) gone.Add(id);
            foreach (uint id in gone) { _peers.Remove(id); _ackByClient.Remove(id); }
            if (ready.Length == 0) { _owesAck.Clear(); return; }

            double[] fx = EconomyOverhaulCompat.ReadFx(out int fxDay, out double[] rates, out double[] basis, out double[] smooth, out double[] closes);
            MarketEvent[] events = EconomyOverhaulCompat.ReadDemand(out double[] demandView);
            var markets = Markets();

            foreach (uint id in ready)
            {
                if (!_peers.TryGetValue(id, out PeerView view)) { view = new PeerView(); _peers[id] = view; }
                bool full = now >= view.FullAt;
                if (full) view.FullAt = now + FullSecs;

                _portBuffer.Clear();
                for (int port = 0; port < markets.Length; port++)
                {
                    var m = markets[port];
                    if (m == null || m.currentSupply == null) continue;
                    float bonus = EconomyOverhaulCompat.MarketBonus(port);
                    if (!Changed(view, port, m.currentSupply, bonus) && !full) continue;
                    _portBuffer.Add(new MarketPort { Port = (short)port, Bonus = bonus, Supply = (float[])m.currentSupply.Clone() });
                }
                bool fxChanged = fx != null && (full || !Same(view.Fx, fx));
                bool demandChanged = demandView != null && (full || !Same(view.Demand, demandView));
                bool owes = _owesAck.Remove(id);
                if (_portBuffer.Count == 0 && !fxChanged && !demandChanged && !owes) continue;

                _ackByClient.TryGetValue(id, out uint ack);
                var msg = new EconomyStateMsg { AckSeq = ack, Ports = _portBuffer.ToArray() };
                if (fxChanged)
                {
                    msg.HasFx = true; msg.FxDay = fxDay; msg.Rates = rates; msg.Basis = basis; msg.Smooth = smooth; msg.Closes = closes;
                    view.Fx = fx;
                }
                if (demandChanged) { msg.HasDemand = true; msg.Events = events; view.Demand = demandView; }
                _net.SendToPlayer(id, msg, DeliveryMethod.ReliableOrdered);
            }
            _owesAck.Clear();
        }

        /// <summary>Whether the port differs from what this client was last sent; remembers the new value.</summary>
        private static bool Changed(PeerView view, int port, float[] supply, float bonus)
        {
            if (view.Ports.TryGetValue(port, out float[] seen) && seen.Length == supply.Length + 1)
            {
                bool same = SameBits(seen[supply.Length], bonus);
                for (int g = 0; same && g < supply.Length; g++) same = SameBits(seen[g], supply[g]);
                if (same) return false;
            }
            else
            {
                seen = new float[supply.Length + 1];
                view.Ports[port] = seen;
            }
            Array.Copy(supply, seen, supply.Length);
            seen[supply.Length] = bonus;
            return true;
        }

        private static bool SameBits(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

        private static bool Same(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void OnEconomyDelta(EconomyDeltaMsg msg, NetPeer fromPeer)
        {
            try
            {
                if (_net.Role != Role.Host) return;
                uint from = _net.PlayerNetIdForPeer(fromPeer);
                if (from == 0) return;
                _ackByClient[from] = msg.Seq;
                _owesAck.Add(from);
                if (!EconomyOverhaulCompat.MarketsOn) return;

                var touched = new HashSet<int>();
                foreach (var c in msg.Changes)
                {
                    var m = Market(c.Port);
                    if (m == null || c.Good < 0 || c.Good >= m.currentSupply.Length) continue;
                    m.currentSupply[c.Good] += c.Delta;
                    touched.Add(c.Port);
                    EconomyOverhaulCompat.CheckDemand(c.Port, c.Good);
                }
                foreach (int port in touched) EconomyOverhaulCompat.RefreshMarket(Market(port));
                EconomyOverhaulCompat.AddFx(msg.Cargo, msg.Shops, msg.Rates);
                EconomyOverhaulCompat.RefreshTradeBook();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Markets] OnEconomyDelta: " + e.Message); }
        }

        // ---- Client -------------------------------------------------------------------

        private bool _mirrors;
        private uint _seq;
        private readonly List<KeyValuePair<uint, EconomyDeltaMsg>> _pending = new List<KeyValuePair<uint, EconomyDeltaMsg>>();
        private readonly Dictionary<long, float> _pendingStock = new Dictionary<long, float>();
        private readonly Dictionary<int, MarketPort> _hostPorts = new Dictionary<int, MarketPort>();
        private readonly HashSet<int> _repaint = new HashSet<int>();
        private EconomyStateMsg _hostFx;

        public void OnEconomyState(EconomyStateMsg msg)
        {
            try
            {
                if (_net.Role != Role.Client || !EconomyOverhaulCompat.MarketsOn) return;
                if (!_mirrors)
                {
                    _mirrors = true;
                    Plugin.Logger.LogInfo("[Markets] the host shares Economy Overhaul's markets; this machine's market simulation is off");
                }
                _repaint.Clear();
                bool ratesAcknowledged = false;
                foreach (var p in _pending)
                    if (p.Key <= msg.AckSeq)
                    {
                        foreach (var c in p.Value.Changes) _repaint.Add(c.Port);
                        ratesAcknowledged |= p.Value.Rates.Length > 0;
                    }
                _pending.RemoveAll(p => p.Key <= msg.AckSeq);
                foreach (var p in msg.Ports)
                {
                    _hostPorts[p.Port] = p;
                    _repaint.Add(p.Port);
                }
                if (msg.HasFx) _hostFx = msg;

                // Changes the host has not counted yet are still ours to show.
                _pendingStock.Clear();
                var pendingRates = new double[4];
                foreach (var p in _pending)
                {
                    foreach (var c in p.Value.Changes)
                    {
                        long key = ((long)c.Port << 16) | (ushort)c.Good;
                        _pendingStock.TryGetValue(key, out float sum);
                        _pendingStock[key] = sum + c.Delta;
                    }
                    for (int i = 0; i < pendingRates.Length && i < p.Value.Rates.Length; i++) pendingRates[i] += p.Value.Rates[i];
                }

                // Even an ack-only reply changes the overlay. The host may be back at its last
                // transmitted stock after another player's compensating trade, so no port was sent.
                foreach (int port in _repaint)
                {
                    if (!_hostPorts.TryGetValue(port, out var p)) continue;
                    var m = Market(p.Port);
                    if (m == null || p.Supply == null) continue;
                    int count = Math.Min(m.currentSupply.Length, p.Supply.Length);
                    for (int g = 0; g < count; g++)
                    {
                        float value = p.Supply[g];
                        if (_pendingStock.Count > 0 && _pendingStock.TryGetValue(((long)p.Port << 16) | (ushort)g, out float mine)) value += mine;
                        m.currentSupply[g] = value;
                    }
                    if (!float.IsNaN(p.Bonus)) EconomyOverhaulCompat.SetMarketBonus(p.Port, p.Bonus);
                    EconomyOverhaulCompat.RefreshMarket(m);
                }
                if (_hostFx != null && (msg.HasFx || ratesAcknowledged))
                {
                    var rates = (double[])_hostFx.Rates.Clone();
                    for (int i = 0; i < rates.Length && i < pendingRates.Length; i++) rates[i] += pendingRates[i];
                    EconomyOverhaulCompat.WriteFx(_hostFx.FxDay, rates, (double[])_hostFx.Basis.Clone(),
                        (double[])_hostFx.Smooth.Clone(), (double[])_hostFx.Closes.Clone());
                }
                if (msg.HasDemand) EconomyOverhaulCompat.WriteDemand(msg.Events);
                if (_repaint.Count > 0 || msg.HasFx || ratesAcknowledged) EconomyOverhaulCompat.RefreshTradeBook();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Markets] OnEconomyState: " + e.Message); }
        }

        // A client's trade: what it changes is measured from before to after. Nested trades (a shop
        // trade inside a book action) count once, at the outermost end.
        private int _tradeDepth;
        private readonly Dictionary<int, float[]> _before = new Dictionary<int, float[]>();
        private double[] _cargoBefore, _shopsBefore, _ratesBefore;

        internal void BeginTrade()
        {
            if (_tradeDepth > 0) { _tradeDepth++; return; }
            if (!Mirrors) return;
            var markets = Markets();
            for (int port = 0; port < markets.Length; port++)
            {
                var m = markets[port];
                if (m == null || m.currentSupply == null) continue;
                if (!_before.TryGetValue(port, out float[] copy) || copy.Length != m.currentSupply.Length)
                    _before[port] = copy = new float[m.currentSupply.Length];
                Array.Copy(m.currentSupply, copy, copy.Length);
            }
            EconomyOverhaulCompat.ReadFxQueues(out _cargoBefore, out _shopsBefore, out _ratesBefore);
            // Only once everything is copied: a failed start leaves no trade open.
            _tradeDepth = 1;
        }

        internal void EndTrade()
        {
            if (_tradeDepth == 0 || --_tradeDepth > 0) return;
            if (!Mirrors) return;
            var changes = new List<MarketChange>();
            var markets = Markets();
            for (int port = 0; port < markets.Length; port++)
            {
                var m = markets[port];
                if (m == null || m.currentSupply == null || !_before.TryGetValue(port, out float[] before)) continue;
                int count = Math.Min(before.Length, m.currentSupply.Length);
                for (int g = 0; g < count; g++)
                {
                    float delta = m.currentSupply[g] - before[g];
                    if (delta != 0f) changes.Add(new MarketChange { Port = (short)port, Good = (short)g, Delta = delta });
                }
            }
            EconomyOverhaulCompat.ReadFxQueues(out double[] cargo, out double[] shops, out double[] rates);
            var msg = new EconomyDeltaMsg
            {
                Changes = changes.ToArray(),
                Cargo = Difference(cargo, _cargoBefore),
                Shops = Difference(shops, _shopsBefore),
                Rates = Difference(rates, _ratesBefore),
            };
            if (msg.Changes.Length == 0 && msg.Cargo.Length == 0 && msg.Shops.Length == 0 && msg.Rates.Length == 0) return;
            msg.Seq = ++_seq;
            _pending.Add(new KeyValuePair<uint, EconomyDeltaMsg>(msg.Seq, msg));
            _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>after − before, or empty when nothing moved.</summary>
        private static double[] Difference(double[] after, double[] before)
        {
            if (after == null || before == null || after.Length != before.Length) return new double[0];
            var delta = new double[after.Length];
            bool any = false;
            for (int i = 0; i < after.Length; i++) { delta[i] = after[i] - before[i]; any |= delta[i] != 0; }
            return any ? delta : new double[0];
        }

        public void Clear()
        {
            _peers.Clear();
            _ackByClient.Clear();
            _owesAck.Clear();
            _scanAt = 0f;
            if (_mirrors) Plugin.Logger.LogInfo("[Markets] session over; this machine's market simulation is back on");
            _mirrors = false;
            _seq = 0;
            _pending.Clear();
            _pendingStock.Clear();
            _hostPorts.Clear();
            _hostFx = null;
            _repaint.Clear();
            _tradeDepth = 0;
            _before.Clear();
            _portsSeen = null;
            _markets = new IslandMarket[0];
        }
    }
}
