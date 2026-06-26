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
        ControlRequest = 51,   // client -> host : "I adjusted rope #i to this length"
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
    // Boat replication (Stage 1) — Unreliable, host -> client only
    // ---------------------------------------------------------------------

    /// <summary>
    /// The host's controlled boat pose, in REAL (origin-stable) space. The client
    /// makes its own copy of the same ship kinematic and drives it from this through
    /// <see cref="Sync.NetTransform"/> (interpolation + bounded extrapolation by velocity).
    /// Rotation is identical in both spaces (the origin only translates).
    /// </summary>
    public sealed class BoatStateMsg : INetMessage
    {
        public uint NetId;
        public long Tick;
        public Vector3 RealPos;   // CoordSpace.LocalToReal(boat.position)
        public Quaternion Rot;    // boat world rotation
        public Vector3 RealVel;   // real-space velocity for extrapolation (may be zero)

        public MsgType Type => MsgType.BoatState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(Tick);
            w.PutVector3(RealPos);
            w.PutQuaternion(Rot);
            w.PutVector3(RealVel);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt();
            Tick = r.GetLong();
            RealPos = r.GetVector3();
            Rot = r.GetQuaternion();
            RealVel = r.GetVector3();
        }
    }

    // ---------------------------------------------------------------------
    // Environment (Stage 1) — Unreliable, host -> client only, a few Hz
    // ---------------------------------------------------------------------

    /// <summary>
    /// Host's environment snapshot. Wind, sun/time-of-day and moon are simple global
    /// state; the client mirrors them so the sky, lighting, flags and sails match.
    /// Weather/storms are NOT here yet — they hang off wandering storms and scene
    /// <c>WeatherSet</c> references that don't serialize, and need their own pass.
    /// </summary>
    public sealed class EnvStateMsg : INetMessage
    {
        public long Tick;
        // wind (Wind.currentWind / currentBaseWind / windRotation — static)
        public Vector3 Wind;
        public Vector3 BaseWind;
        public Quaternion WindRot;
        // time / sun (Sun.sun.globalTime / localTime / timescale ; GameState.day)
        public float GlobalTime;
        public float LocalTime;
        public float Timescale;
        public int Day;
        // moon (Moon.instance.currentPhase)
        public float MoonPhase;

        public MsgType Type => MsgType.EnvState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Tick);
            w.PutVector3(Wind);
            w.PutVector3(BaseWind);
            w.PutQuaternion(WindRot);
            w.Put(GlobalTime);
            w.Put(LocalTime);
            w.Put(Timescale);
            w.Put(Day);
            w.Put(MoonPhase);
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            Wind = r.GetVector3();
            BaseWind = r.GetVector3();
            WindRot = r.GetQuaternion();
            GlobalTime = r.GetFloat();
            LocalTime = r.GetFloat();
            Timescale = r.GetFloat();
            Day = r.GetInt();
            MoonPhase = r.GetFloat();
        }
    }

    // ---------------------------------------------------------------------
    // Controls (Stage 1) — Unreliable, host -> client only
    // ---------------------------------------------------------------------

    /// <summary>
    /// The host boat's control surface in one batch. Two parts:
    ///
    /// <para><b>Lengths</b> — every <c>RopeController.currentLength</c> in
    /// <c>GetComponentsInChildren&lt;RopeController&gt;</c> order. These drive the
    /// discrete/continuous rope state: sail reef (furl amount), anchor payout, etc.</para>
    ///
    /// <para><b>Rotations</b> — the local rotation of every moving mechanical part
    /// (sail booms on their <c>HingeJoint</c> and the rudder) in a fixed
    /// enumeration order. The rope length only sets a boom's swing <i>limits</i>; the
    /// actual angle is physics/wind-driven and diverges on the kinematic client, so we
    /// replicate the real rotation instead of hoping the simulation matches. Winch crank
    /// transforms are not included; rope length is the authoritative winch state.</para>
    ///
    /// Identity-by-index is safe because both machines load the same boat; a count
    /// mismatch on either array is detected and that part is skipped.
    /// </summary>
    public sealed class ControlStateMsg : INetMessage
    {
        public long Tick;
        public float[] Lengths = System.Array.Empty<float>();
        public Quaternion[] Rotations = System.Array.Empty<Quaternion>();

        public MsgType Type => MsgType.ControlState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Tick);
            w.Put((ushort)Lengths.Length);
            for (int i = 0; i < Lengths.Length; i++) w.Put(Lengths[i]);
            w.Put((ushort)Rotations.Length);
            for (int i = 0; i < Rotations.Length; i++) w.PutQuaternion(Rotations[i]);
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            int n = r.GetUShort();
            Lengths = new float[n];
            for (int i = 0; i < n; i++) Lengths[i] = r.GetFloat();
            int m = r.GetUShort();
            Rotations = new Quaternion[m];
            for (int i = 0; i < m; i++) Rotations[i] = r.GetQuaternion();
        }
    }

    // ---------------------------------------------------------------------
    // Shared control (Stage 2) — client -> host request, ReliableOrdered
    // ---------------------------------------------------------------------

    /// <summary>
    /// A client's request to set one rope's length (it operated a winch). Authority
    /// stays with the host: it applies the value to the real <c>RopeController</c>, its
    /// physics produces the result, and the normal <c>ControlState</c> broadcast carries
    /// the outcome back to everyone. Index matches the host's rope enumeration order
    /// (the same list <c>ControlState.Lengths</c> uses). Reliable so the final value on
    /// release is guaranteed to land. The optional winch rotation is cosmetic host-side
    /// feedback so the host sees the client's hand turning the handle.
    /// </summary>
    public sealed class ControlRequestMsg : INetMessage
    {
        public ushort Index;
        public float Length;
        public bool HasWinchRotation;
        public Quaternion WinchRotation;

        public MsgType Type => MsgType.ControlRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(Length);
            w.Put(HasWinchRotation);
            if (HasWinchRotation) w.PutQuaternion(WinchRotation);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            Length = r.GetFloat();
            HasWinchRotation = r.GetBool();
            WinchRotation = HasWinchRotation ? r.GetQuaternion() : Quaternion.identity;
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
