using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Transfers completed work in finite batches, independently of who holds the handle.</summary>
    internal sealed class BilgePumpSync
    {
        private sealed class Pump
        {
            public BilgePump Handle;
            public ushort BoatIndex, Index;
            public float OutRotation, OutEffort, InRotation, InEffort;
            public PumpVisualMotion GuestMotion;
        }

        private sealed class Hull
        {
            public Transform Boat;
            public Pump[] Pumps;
        }

        private const float SendInterval = 0.05f;
        private readonly CoopNet _net;
        private readonly Dictionary<ushort, Hull> _hulls = new Dictionary<ushort, Hull>();
        private readonly Dictionary<BilgePump, Pump> _bindings = new Dictionary<BilgePump, Pump>();
        private float _sendTimer;

        internal BilgePumpSync(CoopNet net) { _net = net; }

        internal void Record(BilgePump handle, float input)
        {
            if (input <= 0f || Time.deltaTime <= 0f || !Ready || InteractionContext.Suppressed) return;
            if (!_bindings.TryGetValue(handle, out var pump))
            {
                for (var t = handle.transform; t != null; t = t.parent)
                {
                    ushort id = BoatLocator.IndexOf(t);
                    if (id == BoatLocator.NoBoat) continue;
                    ResolveHull(id, t);
                    break;
                }
                if (!_bindings.TryGetValue(handle, out pump)) return;
            }
            if (ShipyardSync.Suspended(pump.BoatIndex)) return;

            // ApplyRotation uses this same effort and wet/dry multiplier. The hook
            // runs before vanilla applies this frame's change to the water level.
            float effort = input * Time.deltaTime;
            float multiplier = handle.damage.waterLevel > 0f ? 0.75f : 2.25f;
            pump.OutRotation += effort * 1.4f * handle.rotationSpeed * multiplier;
            if (_net.Role == Role.Client) pump.OutEffort += effort;
        }

        internal void Receive(PumpStrokeMsg msg, NetPeer peer)
        {
            if (!Ready) return;
            bool host = _net.Role == Role.Host;
            if (host ? _net.PlayerNetIdForPeer(peer) == 0 : !_net.IsHostPeer(peer)) return;

            var boat = BoatLocator.FindByIndex(msg.BoatIndex);
            if (boat == null) return;
            var hull = ResolveHull(msg.BoatIndex, boat);
            if (msg.PumpIndex >= hull.Pumps.Length) return;
            var pump = hull.Pumps[msg.PumpIndex];
            if (pump.Handle == null) return;
            if (!host)
            {
                pump.GuestMotion.Add(msg.Rotation);
                return;
            }

            pump.InRotation += msg.Rotation;
            if (BoatLocator.FindBoats().Contains(boat)) pump.InEffort += msg.Effort;
            msg.Effort = 0f;
            _net.RelayExcept(msg, peer, DeliveryMethod.ReliableOrdered);
        }

        internal void Tick(float dt)
        {
            if (!Ready || dt <= 0f) return;
            _sendTimer += dt;
            bool send = _sendTimer >= SendInterval;
            if (send) _sendTimer %= SendInterval;
            bool guest = _net.Role == Role.Client;
            float blend = guest ? 0f : 1f - Mathf.Exp(-dt / SendInterval);
            foreach (var pump in _bindings.Values)
            {
                var handle = pump.Handle;
                if (handle == null || ShipyardSync.Suspended(pump.BoatIndex)) continue;
                float step;
                if (guest) step = pump.GuestMotion.Advance(dt);
                else
                {
                    step = Mathf.Abs(pump.InRotation) < 0.05f ? pump.InRotation : pump.InRotation * blend;
                    pump.InRotation -= step;
                }
                if (step != 0f) handle.transform.Rotate(Vector3.forward, step, Space.Self);
                if (pump.InEffort != 0f)
                {
                    var damage = handle.damage;
                    if (damage != null && !damage.sunk)
                        damage.waterLevel = Mathf.Max(0f, damage.waterLevel - pump.InEffort * handle.drainRate);
                    pump.InEffort = 0f;
                }
                if (!send || (pump.OutRotation == 0f && pump.OutEffort == 0f)) continue;
                _net.Broadcast(new PumpStrokeMsg
                {
                    BoatIndex = pump.BoatIndex, Generation = BoatGenerationBook.Session.Get(pump.BoatIndex),
                    PumpIndex = pump.Index, Rotation = pump.OutRotation, Effort = pump.OutEffort
                }, DeliveryMethod.ReliableOrdered);
                pump.OutRotation = pump.OutEffort = 0f;
            }
        }

        private bool Ready => _net.State == LinkState.Connected && GameState.playing && !GameState.currentlyLoading;

        private Hull ResolveHull(ushort id, Transform boat)
        {
            if (_hulls.TryGetValue(id, out var hull) && hull.Boat == boat) return hull;
            Invalidate(id);
            var handles = boat.GetComponentsInChildren<BilgePump>(true);
            hull = new Hull { Boat = boat, Pumps = new Pump[handles.Length] };
            for (int i = 0; i < handles.Length; i++)
            {
                var pump = new Pump { Handle = handles[i], BoatIndex = id, Index = (ushort)i };
                hull.Pumps[i] = pump;
                _bindings.Add(pump.Handle, pump);
            }
            _hulls.Add(id, hull);
            return hull;
        }

        internal void Invalidate(ushort id)
        {
            if (!_hulls.TryGetValue(id, out var hull)) return;
            foreach (var pump in hull.Pumps) _bindings.Remove(pump.Handle);
            _hulls.Remove(id);
        }

        internal void Clear()
        {
            _hulls.Clear();
            _bindings.Clear();
            _sendTimer = 0f;
        }
    }
}
