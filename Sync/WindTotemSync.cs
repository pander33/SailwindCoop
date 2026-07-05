using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Phase 1 — the wind totem orb (<c>WindTotemOrb</c>, a non-ShipItem PickupableItem).
    /// While a player holds the orb the vanilla orb drives <c>Wind.instance.ForceNewWind</c>
    /// every frame, steering the global wind. Wind is host-authoritative
    /// (<see cref="EnvironmentSync"/> disables the client's local Wind sim), so a client
    /// holding the orb has no effect locally. We fix that by forwarding the orb's computed
    /// wind vector to the host; the host applies <c>ForceNewWind</c> and EnvironmentSync
    /// distributes the result to everyone like any other wind snapshot.
    ///
    /// The host's own orb use already works through vanilla + EnvironmentSync, so this sync
    /// only sends from the client side.
    /// </summary>
    public sealed class WindTotemSync
    {
        private readonly CoopNet _net;
        private GoPointer _gp;
        private FieldInfo _fHeldItem;
        private float _sendTimer;
        private bool _active;          // overlay: client currently steering the wind
        private Vector3 _lastWind;

        /// <summary>Forward rate while holding the orb (Hz). The orb updates wind every frame.</summary>
        public float SendHz = 15f;

        public bool Active => _active;
        public Vector3 LastWind => _lastWind;

        public WindTotemSync(CoopNet net) { _net = net; }

        public void Tick(float dt)
        {
            if (_net.Role != Role.Client || _net.State != LinkState.Connected)
            {
                _active = false;
                return;
            }

            var orb = HeldOrb();
            if (orb == null || orb.totem == null)
            {
                _active = false;
                return;
            }

            float interval = 1f / Mathf.Max(1f, SendHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            Vector3 wind = ComputeOrbWind(orb);
            _lastWind = wind;
            _active = true;
            _net.Broadcast(new WindRequestMsg { Wind = wind }, LiteNetLib.DeliveryMethod.Unreliable);
        }

        public void OnWindRequest(WindRequestMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            var wind = global::Wind.instance;
            if (wind == null) return;
            // Apply authoritatively; EnvironmentSync.Tick will broadcast the new currentWind.
            wind.ForceNewWind(msg.Wind);
            _lastWind = msg.Wind;
        }

        public void Clear()
        {
            _gp = null;
            _fHeldItem = null;
            _sendTimer = 0f;
            _active = false;
            _lastWind = Vector3.zero;
        }

        /// <summary>Mirror of <c>WindTotemOrb.Update</c>'s wind computation (vanilla formula).</summary>
        private static Vector3 ComputeOrbWind(WindTotemOrb orb)
        {
            var wind = global::Wind.instance;
            float minMag = wind != null ? wind.minimumMagnitude : 0f;
            float maxMag = wind != null ? wind.maximumMagnitude : 1f;
            float dist = Vector3.Distance(orb.transform.position, orb.totem.position);
            float t = orb.maxCarryDistance > 0.0001f ? dist / orb.maxCarryDistance : 0f;
            float magnitude = Mathf.Lerp(minMag, maxMag, t);
            return orb.totem.forward * magnitude;
        }

        private WindTotemOrb HeldOrb()
        {
            var held = HeldItem();
            return held as WindTotemOrb;
        }

        private PickupableItem HeldItem()
        {
            try
            {
                if (_gp == null) _gp = Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return null;
                if (_fHeldItem == null)
                    _fHeldItem = typeof(GoPointer).GetField("heldItem", BindingFlags.NonPublic | BindingFlags.Instance);
                return _fHeldItem != null ? _fHeldItem.GetValue(_gp) as PickupableItem : null;
            }
            catch { return null; }
        }
    }
}
