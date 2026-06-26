using LiteNetLib.Utils;
using UnityEngine;

namespace SailwindCoop.Net
{
    /// <summary>
    /// Wire message type. One byte, prepended to every payload (F1).
    /// NEVER renumber existing values — only append. Renumbering breaks the
    /// version contract for anyone on an older build.
    /// </summary>
    public enum MsgType : byte
    {
        // --- handshake / session (ReliableOrdered) ---
        Hello = 1,        // client -> host : version + worldId + name
        HelloAck = 2,     // host -> client : accepted, assigned NetId, server tick
        Reject = 3,       // host -> client : refused, with reason
        Disconnect = 4,   // both          : graceful leave, reason

        // --- clock (Unreliable) ---
        TimeSync = 10,    // both          : round-trip clock estimation (F4)

        // --- object lifecycle (ReliableOrdered) — Stage 1+ ---
        SpawnObject = 20,
        DespawnObject = 21,

        // --- state snapshots (Unreliable) — Stage 1+ ---
        PlayerState = 30,
        BoatState = 31,
        EnvState = 32,
        ControlState = 33,

        // --- discrete control events (ReliableOrdered) — Stage 1+ ---
        ControlEvent = 40,

        // --- interaction (ReliableOrdered) — Stage 2 ---
        InteractRequest = 50,
    }

    /// <summary>Why the host refused a client (sent in Reject).</summary>
    public enum RejectReason : byte
    {
        None = 0,
        ProtocolMismatch = 1,
        ModVersionMismatch = 2,
        WorldMismatch = 3,
        ServerFull = 4,
        AlreadyConnected = 5,
    }

    /// <summary>
    /// Every message can write/read its own payload (the MsgType byte is handled
    /// by <see cref="Protocol"/>, not here).
    /// </summary>
    public interface INetMessage
    {
        MsgType Type { get; }
        void Serialize(NetDataWriter w);
        void Deserialize(NetDataReader r);
    }

    // ---------------------------------------------------------------------
    // Handshake
    // ---------------------------------------------------------------------

    public sealed class HelloMsg : INetMessage
    {
        public int ProtocolVersion;
        public string ModVersion = "";
        public string WorldId = "";      // host save identity; must match
        public string PlayerName = "";

        public MsgType Type => MsgType.Hello;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ProtocolVersion);
            w.Put(ModVersion);
            w.Put(WorldId);
            w.Put(PlayerName);
        }

        public void Deserialize(NetDataReader r)
        {
            ProtocolVersion = r.GetInt();
            ModVersion = r.GetString();
            WorldId = r.GetString();
            PlayerName = r.GetString();
        }
    }

    public sealed class HelloAckMsg : INetMessage
    {
        public uint AssignedNetId;       // the joining player's NetId
        public long ServerTick;          // host clock at send (ms)
        public string HostPlayerName = "";

        public MsgType Type => MsgType.HelloAck;

        public void Serialize(NetDataWriter w)
        {
            w.Put(AssignedNetId);
            w.Put(ServerTick);
            w.Put(HostPlayerName);
        }

        public void Deserialize(NetDataReader r)
        {
            AssignedNetId = r.GetUInt();
            ServerTick = r.GetLong();
            HostPlayerName = r.GetString();
        }
    }

    public sealed class RejectMsg : INetMessage
    {
        public RejectReason Reason;
        public string Detail = "";

        public MsgType Type => MsgType.Reject;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Reason);
            w.Put(Detail);
        }

        public void Deserialize(NetDataReader r)
        {
            Reason = (RejectReason)r.GetByte();
            Detail = r.GetString();
        }
    }

    public sealed class DisconnectMsg : INetMessage
    {
        public string Reason = "";

        public MsgType Type => MsgType.Disconnect;

        public void Serialize(NetDataWriter w) => w.Put(Reason);
        public void Deserialize(NetDataReader r) => Reason = r.GetString();
    }

    // ---------------------------------------------------------------------
    // Clock (F4)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Ping-pong for clock offset/RTT. The originator stamps <see cref="ClientSendTick"/>;
    /// the responder echoes it and adds its own <see cref="ServerTick"/>.
    /// </summary>
    public sealed class TimeSyncMsg : INetMessage
    {
        public bool IsReply;
        public long ClientSendTick;      // echoed unchanged on reply
        public long ServerTick;          // responder's clock (ms), valid on reply

        public MsgType Type => MsgType.TimeSync;

        public void Serialize(NetDataWriter w)
        {
            w.Put(IsReply);
            w.Put(ClientSendTick);
            w.Put(ServerTick);
        }

        public void Deserialize(NetDataReader r)
        {
            IsReply = r.GetBool();
            ClientSendTick = r.GetLong();
            ServerTick = r.GetLong();
        }
    }

    // ---------------------------------------------------------------------
    // Player presence (Stage 1) — Unreliable
    // ---------------------------------------------------------------------

    /// <summary>
    /// One player's pose, in REAL (origin-stable) space. Sender stamps its own
    /// <see cref="NetId"/>; the host relays clients' states to other clients.
    /// </summary>
    /// <summary>Coordinate frame a <see cref="PlayerStateMsg"/> is expressed in.</summary>
    public enum CoordFrame : byte
    {
        World = 0,   // Pos = origin-stable real position
        Boat = 1,    // Pos = position local to the player's current boat (shared ship)
    }

    public sealed class PlayerStateMsg : INetMessage
    {
        public uint NetId;
        public long Tick;
        public CoordFrame Frame;
        public Vector3 Pos;       // meaning depends on Frame (real space, or boat-local)
        public Quaternion Rot;    // world rotation, or boat-local rotation
        public Vector3 Vel;       // velocity in the same frame, for extrapolation (may be zero)

        public MsgType Type => MsgType.PlayerState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(Tick);
            w.Put((byte)Frame);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
            w.PutVector3(Vel);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt();
            Tick = r.GetLong();
            Frame = (CoordFrame)r.GetByte();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
            Vel = r.GetVector3();
        }
    }

    // ---------------------------------------------------------------------
    // Shared wire helpers for Vector3/Quaternion (used from Stage 1 on).
    // ---------------------------------------------------------------------

    public static class WireExt
    {
        public static void PutVector3(this NetDataWriter w, Vector3 v)
        {
            w.Put(v.x); w.Put(v.y); w.Put(v.z);
        }

        public static Vector3 GetVector3(this NetDataReader r)
            => new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

        public static void PutQuaternion(this NetDataWriter w, Quaternion q)
        {
            w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
        }

        public static Quaternion GetQuaternion(this NetDataReader r)
            => new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
    }
}
