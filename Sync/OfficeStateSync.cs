using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Economy Overhaul 1.2.0's trade offices are the host's (<see cref="EconomyOverhaulCompat"/>): the
    /// host sends its office state to clients with EO when it changes, checked once a second, and to a
    /// client whose world has just loaded. Same shape as <see cref="FinancedCargoSync"/>.
    /// </summary>
    public sealed class OfficeStateSync
    {
        public static OfficeStateSync Instance { get; private set; }
        private const float CheckSeconds = 1f;
        private readonly CoopNet _net;
        private byte[] _sent;
        private float _checkAt;

        public OfficeStateSync(CoopNet net) { _net = net; Instance = this; }

        public void Tick()
        {
            if (_net.Role != Role.Host || _net.State != LinkState.Connected || !EconomyOverhaulCompat.Present ||
                !GameState.playing || GameState.currentlyLoading) return;
            if (Time.unscaledTime < _checkAt) return;
            _checkAt = Time.unscaledTime + CheckSeconds;
            var ready = _net.ReadyClientIds(ModFeatures.EconomyOverhaul);
            if (ready.Length == 0) return;
            var data = Read();
            if (data == null || Same(data, _sent)) return;
            _sent = data;
            var msg = new OfficeStateMsg { Data = data };
            foreach (uint id in ready) _net.SendToPlayer(id, msg, DeliveryMethod.ReliableOrdered);
        }

        public void SendBaseline(NetPeer peer)
        {
            if (_net.Role != Role.Host || peer == null || !EconomyOverhaulCompat.Present ||
                (_net.FeaturesOf(peer) & ModFeatures.EconomyOverhaul) == 0) return;
            var data = Read();
            if (data == null) return;
            peer.Send(new OfficeStateMsg { Data = data }, DeliveryMethod.ReliableOrdered);
            // The others get it from Tick if it changed; the joining peer now holds the current one.
            if (!Same(data, _sent))
            {
                _sent = data;
                uint joining = _net.PlayerNetIdForPeer(peer);
                var msg = new OfficeStateMsg { Data = data };
                foreach (uint id in _net.ReadyClientIds(ModFeatures.EconomyOverhaul))
                    if (id != joining) _net.SendToPlayer(id, msg, DeliveryMethod.ReliableOrdered);
            }
        }

        public void Receive(OfficeStateMsg msg)
        {
            if (_net.Role != Role.Client || msg == null || !EconomyOverhaulCompat.Present ||
                (_net.HostModFeatures & ModFeatures.EconomyOverhaul) == 0) return;
            EconomyOverhaulCompat.WriteOffice(msg.Data);
        }

        public void Clear() { _sent = null; _checkAt = 0; }

        private static byte[] Read()
        {
            var data = EconomyOverhaulCompat.ReadOffice();
            if (data != null && data.Length > OfficeStateMsg.MaxBytes)
            {
                Plugin.Logger.LogWarning("[OfficeStateSync] office state too large to send: " + data.Length + " bytes");
                return null;
            }
            return data;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
