using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Numeric payload helpers and guest poses for distant boat activity.</summary>
    internal sealed class BoatAuthority
    {
        public static BoatAuthority Instance { get; private set; }
        private readonly CoopNet _net;
        private sealed class Pose { public ushort Boat; public CoordFrame Frame; public Vector3 Pos; public float Received; }
        private readonly Dictionary<uint, Pose> _poses = new Dictionary<uint, Pose>();
        public BoatAuthority(CoopNet net) { _net = net; Instance = this; }

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        public static bool Rotation(Quaternion value)
        {
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
            float norm = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return norm > 0.5f && norm < 1.5f;
        }

        public void Record(PlayerStateMsg msg, NetPeer peer)
        {
            uint actor = _net.PlayerNetIdForPeer(peer);
            if (actor == 0 || actor != msg.NetId || !Finite(msg.Pos)) return;
            if (msg.Frame != CoordFrame.Boat && msg.Frame != CoordFrame.World) return;
            _poses[actor] = new Pose { Boat = msg.BoatIndex, Frame = msg.Frame, Pos = msg.Pos, Received = Time.unscaledTime };
        }

        public bool AnyActorNear(Transform boat, float radius)
        {
            if (_net.Role != Role.Host || boat == null || !Finite(radius) || !CoordSpace.Ready) return false;
            foreach (var pose in _poses.Values)
            {
                if (Time.unscaledTime - pose.Received > 5f) continue; // Smooth activity through brief packet loss.
                Vector3 position;
                if (pose.Frame == CoordFrame.Boat)
                {
                    var deck = BoatLocator.FindByIndex(pose.Boat);
                    if (deck == null) continue;
                    if (deck == boat) return true;
                    position = deck.TransformPoint(pose.Pos);
                }
                else position = CoordSpace.RealToLocal(pose.Pos);
                if ((position - boat.position).sqrMagnitude <= radius * radius) return true;
            }
            return false;
        }

        public void ClearActor(uint actor) { _poses.Remove(actor); }
        public void Clear() { _poses.Clear(); }
    }
}
