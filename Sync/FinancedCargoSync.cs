using System;
using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Option A: the host's current loan cargo, including loans made after a guest joined.</summary>
    public sealed class FinancedCargoSync
    {
        public static FinancedCargoSync Instance { get; private set; }
        private readonly CoopNet _net;
        private readonly HashSet<int> _ids = new HashSet<int>();
        private readonly HashSet<int> _scanned = new HashSet<int>();
        private bool _received, _enabled;
        private float _scanAt;
        public FinancedCargoSync(CoopNet net) { _net = net; Instance = this; }

        internal bool Blocks(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || !EconomyOverhaulCompat.Present ||
                (_net.HostModFeatures & ModFeatures.EconomyOverhaul) == 0 || item == null) return false;
            if (!_received) return EconomyOverhaulCompat.LocalFinanced(item);
            if (!_enabled || _ids.Count == 0) return false;
            var save = item.GetComponent<SaveablePrefab>();
            return save != null && _ids.Contains(save.instanceId);
        }

        public void Tick(bool force = false)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected || !EconomyOverhaulCompat.Present ||
                !GameState.playing || GameState.currentlyLoading) return;
            if (!force && Time.unscaledTime < _scanAt) return;
            _scanAt = Time.unscaledTime + .25f;
            var ready = _net.ReadyClientIds(ModFeatures.EconomyOverhaul);
            if (ready.Length == 0) return;
            if (!EconomyOverhaulCompat.ReadFinanced(_scanned)) return;
            if (!RememberScan()) return;
            var msg = Snapshot();
            foreach (uint id in ready) _net.SendToPlayer(id, msg, DeliveryMethod.ReliableOrdered);
        }

        public void SendBaseline(NetPeer peer)
        {
            if (_net.Role != Role.Host || !EconomyOverhaulCompat.Present ||
                (_net.FeaturesOf(peer) & ModFeatures.EconomyOverhaul) == 0 ||
                !EconomyOverhaulCompat.ReadFinanced(_scanned)) return;
            var msg = Snapshot();
            peer.Send(msg, DeliveryMethod.ReliableOrdered);
            // Keep the joining peer and existing peers on the same change-only base.
            if (!RememberScan()) return;
            uint joining = _net.PlayerNetIdForPeer(peer);
            foreach (uint id in _net.ReadyClientIds(ModFeatures.EconomyOverhaul))
                if (id != joining) _net.SendToPlayer(id, msg, DeliveryMethod.ReliableOrdered);
        }

        private bool RememberScan()
        {
            if (_received && _ids.SetEquals(_scanned)) return false;
            _received = true;
            _ids.Clear(); _ids.UnionWith(_scanned);
            return true;
        }

        private FinancedCargoMsg Snapshot()
        {
            var ids = new int[_scanned.Count];
            _scanned.CopyTo(ids);
            return new FinancedCargoMsg { Enabled = true, InstanceIds = ids };
        }

        public void Receive(FinancedCargoMsg msg)
        {
            if (_net.Role != Role.Client || !EconomyOverhaulCompat.Present ||
                (_net.HostModFeatures & ModFeatures.EconomyOverhaul) == 0) return;
            _received = true; _enabled = msg.Enabled;
            _ids.Clear(); _ids.UnionWith(msg.InstanceIds);
            EconomyOverhaulCompat.ClearSettledLoans(_ids);
        }

        public void Clear() { _ids.Clear(); _scanned.Clear(); _received = _enabled = false; _scanAt = 0; }
    }
}
