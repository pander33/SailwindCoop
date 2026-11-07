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
        private bool _received, _enabled;
        private float _scanAt;
        public FinancedCargoSync(CoopNet net) { _net = net; Instance = this; }

        internal bool Blocks(ShipItem item)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected || item == null) return false;
            var save = item.GetComponent<SaveablePrefab>();
            return _received ? _enabled && save != null && _ids.Contains(save.instanceId) : EconomyOverhaulCompat.LocalFinanced(item);
        }

        public void Tick(bool force = false)
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading) return;
            if (!force && Time.unscaledTime < _scanAt) return;
            _scanAt = Time.unscaledTime + .25f;
            if (!EconomyOverhaulCompat.ReadFinanced(out int[] ids)) return;
            if (_received && _ids.SetEquals(ids)) return;
            _received = true; _enabled = EconomyOverhaulCompat.Present;
            _ids.Clear(); _ids.UnionWith(ids);
            _net.Broadcast(new FinancedCargoMsg { Enabled = _enabled, InstanceIds = ids }, DeliveryMethod.ReliableOrdered);
        }

        public void SendBaseline(NetPeer peer)
        {
            if (_net.Role != Role.Host || !EconomyOverhaulCompat.ReadFinanced(out int[] ids)) return;
            peer.Send(new FinancedCargoMsg { Enabled = EconomyOverhaulCompat.Present, InstanceIds = ids }, DeliveryMethod.ReliableOrdered);
        }

        public void Receive(FinancedCargoMsg msg)
        {
            if (_net.Role != Role.Client) return;
            _received = true; _enabled = msg.Enabled;
            _ids.Clear(); _ids.UnionWith(msg.InstanceIds);
            EconomyOverhaulCompat.ClearSettledLoans(_ids);
        }

        public void Clear() { _ids.Clear(); _received = _enabled = false; _scanAt = 0; }
    }
}
