using System;
using System.Collections.Generic;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Host-authoritative boat transform sync. P2 extends the original single-current-boat
    /// model to all embarkable boats in the loaded save (main ship + dinghies), addressed by
    /// <see cref="BoatLocator"/> index. This keeps the host boat moving for a guest who has
    /// disembarked and gives remote player poses an unambiguous boat-local frame.
    /// </summary>
    public sealed class BoatSync
    {
        private sealed class HostBoat
        {
            public ushort Index;
            public uint NetId;
            public Transform Boat;
            public Vector3 LastRealPos;
            public long LastRealTick;
            public bool HaveLast;
        }

        private sealed class ClientBoat
        {
            public ushort Index;
            public Transform Boat;
            public readonly NetTransform Net = new NetTransform();
            public Rigidbody Rb;
            public bool PrevKinematic;
            public RigidbodyInterpolation PrevInterp;
            public Component PhysSwitcher;
            public bool PrevPaused;
        }

        private readonly CoopNet _net;
        private readonly Dictionary<ushort, HostBoat> _hostBoats = new Dictionary<ushort, HostBoat>();
        private readonly Dictionary<ushort, ClientBoat> _clientBoats = new Dictionary<ushort, ClientBoat>();

        private float _sendTimer;
        private float _refreshTimer;
        private uint _firstBoatNetId;
        private static Type _physSwitcherType;

        public float InterpDelayMs = 100f;
        public int SnapshotHz = 20;

        public bool IsSlaving => _clientBoats.Count > 0;
        public uint BoatNetId => _firstBoatNetId;
        public int BoatCount => _net.Role == Role.Host ? _hostBoats.Count : _clientBoats.Count;

        public BoatSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: author all embarkable boats
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.Role != Role.Host) return;
            if (_net.State != LinkState.Connected) return;
            if (!CoordSpace.Ready) return;

            RefreshHostBoats(dt);
            if (_hostBoats.Count == 0) return;

            float interval = 1f / Mathf.Max(1, SnapshotHz);
            _sendTimer += dt;
            if (_sendTimer < interval) return;
            _sendTimer = 0f;

            long tick = _net.Clock.ServerTick;
            foreach (var hb in _hostBoats.Values)
            {
                if (hb.Boat == null) continue;

                Vector3 real = CoordSpace.LocalToReal(hb.Boat.position);
                Vector3 vel = Vector3.zero;
                if (hb.HaveLast)
                {
                    float secs = (tick - hb.LastRealTick) / 1000f;
                    if (secs > 0.0001f) vel = (real - hb.LastRealPos) / secs;
                }
                hb.LastRealPos = real;
                hb.LastRealTick = tick;
                hb.HaveLast = true;

                _net.Broadcast(new BoatStateMsg
                {
                    NetId = hb.NetId,
                    BoatIndex = hb.Index,
                    Tick = tick,
                    RealPos = real,
                    Rot = hb.Boat.rotation,
                    RealVel = vel,
                }, LiteNetLib.DeliveryMethod.Unreliable);
            }
        }

        // -----------------------------------------------------------------
        // Client: receive/apply boat poses
        // -----------------------------------------------------------------

        public void OnBoatState(BoatStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Client) return;

            Transform boat = BoatLocator.FindByIndex(msg.BoatIndex);
            if (boat == null) return;

            var cb = EnsureClientBoat(msg.BoatIndex, msg.NetId, boat);
            cb.Net.InterpDelayMs = InterpDelayMs;
            cb.Net.Push(msg.Tick, msg.RealPos, msg.Rot, msg.RealVel);
        }

        public void ApplyRemote()
        {
            if (_net.Role != Role.Client) return;
            if (!CoordSpace.Ready) return;
            if (Time.timeScale <= 0.0001f) return;

            foreach (var cb in _clientBoats.Values)
            {
                if (cb.Boat == null || !cb.Net.HasData) continue;
                cb.Net.Apply(cb.Boat, _net.Clock.ServerTick);
            }
        }

        public Transform GetBoatByIndex(ushort index)
        {
            if (_net.Role == Role.Client && _clientBoats.TryGetValue(index, out var cb) && cb.Boat != null)
                return cb.Boat;
            if (_net.Role == Role.Host && _hostBoats.TryGetValue(index, out var hb) && hb.Boat != null)
                return hb.Boat;
            return BoatLocator.FindByIndex(index);
        }

        // -----------------------------------------------------------------
        // Host boat discovery
        // -----------------------------------------------------------------

        private void RefreshHostBoats(float dt)
        {
            _refreshTimer += dt;
            if (_refreshTimer < 1f && _hostBoats.Count > 0) return;
            _refreshTimer = 0f;

            var boats = BoatLocator.FindBoats();
            var seen = new HashSet<ushort>();
            for (int i = 0; i < boats.Count && i <= ushort.MaxValue - 1; i++)
            {
                var boat = boats[i];
                if (boat == null) continue;
                ushort idx = (ushort)i;
                seen.Add(idx);

                if (!_hostBoats.TryGetValue(idx, out var hb))
                {
                    hb = new HostBoat
                    {
                        Index = idx,
                        NetId = _net.Registry.AllocateId(),
                        Boat = boat,
                    };
                    _hostBoats[idx] = hb;
                    _net.Registry.Register(hb.NetId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
                    if (_firstBoatNetId == 0) _firstBoatNetId = hb.NetId;
                    Plugin.Logger.LogInfo("[BoatSync] Лодка #" + idx + " зарегистрирована NetId=" + hb.NetId +
                                          " ('" + BoatLocator.PathOf(boat) + "')");
                }
                else if (hb.Boat != boat)
                {
                    hb.Boat = boat;
                    hb.HaveLast = false;
                    _net.Registry.Register(hb.NetId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
                    Plugin.Logger.LogInfo("[BoatSync] Лодка #" + idx + " перепривязана ('" + BoatLocator.PathOf(boat) + "')");
                }
            }

            var remove = new List<ushort>();
            foreach (var kv in _hostBoats)
                if (!seen.Contains(kv.Key)) remove.Add(kv.Key);
            foreach (ushort idx in remove)
            {
                _net.Registry.Remove(_hostBoats[idx].NetId);
                _hostBoats.Remove(idx);
            }
        }

        // -----------------------------------------------------------------
        // Client slave / restore
        // -----------------------------------------------------------------

        private ClientBoat EnsureClientBoat(ushort index, uint netId, Transform boat)
        {
            if (_clientBoats.TryGetValue(index, out var cb))
            {
                if (cb.Boat == boat) return cb;
                RestoreClientBoat(cb);
                _clientBoats.Remove(index);
            }

            cb = new ClientBoat { Index = index, Boat = boat };
            cb.Rb = boat.GetComponent<Rigidbody>();
            if (cb.Rb != null)
            {
                cb.PrevKinematic = cb.Rb.isKinematic;
                cb.PrevInterp = cb.Rb.interpolation;
                cb.Rb.isKinematic = true;
                cb.Rb.interpolation = RigidbodyInterpolation.None;
            }

            TrySetPhysicsPaused(cb, true);
            _clientBoats[index] = cb;
            _net.Registry.Register(netId, NetObjKind.Boat, NetRegistry.HostAuthority, boat);
            if (_firstBoatNetId == 0) _firstBoatNetId = netId;

            Plugin.Logger.LogInfo("[BoatSync] Лодка клиента #" + index + " в ведомом режиме: NetId=" + netId +
                                  " ('" + BoatLocator.PathOf(boat) + "'), rb=" + (cb.Rb != null) +
                                  ", physSwitcher=" + (cb.PhysSwitcher != null));
            return cb;
        }

        private void RestoreClientBoat(ClientBoat cb)
        {
            if (cb == null) return;

            if (cb.Rb != null)
            {
                cb.Rb.isKinematic = cb.PrevKinematic;
                cb.Rb.interpolation = cb.PrevInterp;
            }

            TrySetPhysicsPaused(cb, false, restore: true);
            cb.Net.Clear();
            cb.Rb = null;
            cb.PhysSwitcher = null;
            cb.Boat = null;
        }

        private void TrySetPhysicsPaused(ClientBoat cb, bool paused, bool restore = false)
        {
            try
            {
                if (cb == null || cb.Boat == null) return;
                if (_physSwitcherType == null)
                    _physSwitcherType = Type.GetType("BoatPhysicsSwitcher, Assembly-CSharp");
                if (_physSwitcherType == null) return;

                if (cb.PhysSwitcher == null)
                    cb.PhysSwitcher = cb.Boat.GetComponentInChildren(_physSwitcherType);
                if (cb.PhysSwitcher == null) return;

                var field = _physSwitcherType.GetField("paused");
                var prop = field == null ? _physSwitcherType.GetProperty("paused") : null;
                if (field == null && prop == null) return;

                if (!restore)
                    cb.PrevPaused = field != null
                        ? (bool)field.GetValue(cb.PhysSwitcher)
                        : (bool)prop.GetValue(cb.PhysSwitcher, null);

                bool target = restore ? cb.PrevPaused : paused;
                if (field != null) field.SetValue(cb.PhysSwitcher, target);
                else prop.SetValue(cb.PhysSwitcher, target, null);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatSync] BoatPhysicsSwitcher.paused недоступен: " + e.Message);
            }
        }

        public void Clear()
        {
            foreach (var cb in _clientBoats.Values)
                RestoreClientBoat(cb);
            _clientBoats.Clear();
            _hostBoats.Clear();
            _firstBoatNetId = 0;
            _sendTimer = 0f;
            _refreshTimer = 0f;
        }
    }
}
