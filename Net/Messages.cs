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
        AnchorState = 34,
        MooringState = 35,
        BoatDamageState = 36,

        // --- discrete control events (ReliableOrdered) — Stage 1+ ---
        ControlEvent = 40,

        // --- interaction (ReliableOrdered) — Stage 2 ---
        InteractRequest = 50,
        ControlRequest = 51,   // client -> host : "I adjusted rope #i to this length"
        SteerRequest = 52,     // client -> host : "I turned wheel #i to this input"
        MooringRequest = 53,   // client -> host : "I unmoored rope #i"
        HoldRequest = 54,      // client -> host : "I started/stopped holding button #i"
        DamageRequest = 55,    // client -> host : "I repaired/bailed the boat damage state"
        PushRequest = 56,      // client -> host : "I pushed a boat/sail/dock collider this frame"
        LightState = 57,       // host -> client : shared lantern/light state
        LightRequest = 58,     // client -> host : requested lantern/light state change
        ItemState = 59,        // host -> client : replicated physical item pose/state
        ItemRequest = 60,      // client -> host : pickup/drop/held-pose/held-action item intent
        ItemExtra = 62,        // host -> client : per-type item state (cooking/consumption)
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
        public ushort BoatIndex;  // valid when Frame == Boat, else NoBoat/ushort.MaxValue
        public Vector3 Pos;       // meaning depends on Frame (real space, or boat-local)
        public Quaternion Rot;    // world rotation, or boat-local rotation
        public Quaternion HeadRot; // world rotation, or boat-local head/camera rotation
        public Vector3 Vel;       // velocity in the same frame, for extrapolation (may be zero)

        public MsgType Type => MsgType.PlayerState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(Tick);
            w.Put((byte)Frame);
            w.Put(BoatIndex);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
            w.PutQuaternion(HeadRot);
            w.PutVector3(Vel);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt();
            Tick = r.GetLong();
            Frame = (CoordFrame)r.GetByte();
            BoatIndex = r.GetUShort();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
            HeadRot = r.GetQuaternion();
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
        public ushort BoatIndex;
        public long Tick;
        public Vector3 RealPos;   // CoordSpace.LocalToReal(boat.position)
        public Quaternion Rot;    // boat world rotation
        public Vector3 RealVel;   // real-space velocity for extrapolation (may be zero)

        public MsgType Type => MsgType.BoatState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(BoatIndex);
            w.Put(Tick);
            w.PutVector3(RealPos);
            w.PutQuaternion(Rot);
            w.PutVector3(RealVel);
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt();
            BoatIndex = r.GetUShort();
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
    // Anchor (Stage 2) — Unreliable, host -> client only
    // ---------------------------------------------------------------------

    /// <summary>
    /// The boat anchor's pose + set-state. Anchor payout is already carried by the anchor
    /// rope's length (ControlState), but on the client the anchor is a free rigidbody jointed
    /// to a now-kinematic boat, so it drifts from the host. We slave it like the boat.
    ///
    /// <para>Frame matters: <b>stowed</b> the anchor is fixed to the deck → <see cref="CoordFrame.Boat"/>
    /// (boat-local, immune to the floating origin and to interp lag while sailing); <b>deployed/set</b>
    /// it sits at a world seabed point the boat swings around → <see cref="CoordFrame.World"/>
    /// (origin-stable real space). The host picks the frame; the client switches converters on change.</para>
    /// </summary>
    public sealed class AnchorStateMsg : INetMessage
    {
        public long Tick;
        public CoordFrame Frame;
        public Vector3 Pos;       // real space (World) or boat-local (Boat), per Frame
        public Quaternion Rot;    // world rotation (World) or boat-local rotation (Boat)
        public Vector3 Vel;       // real-space velocity for extrapolation (World only; else zero)
        public bool Set;          // Anchor.IsSet() — dug into the seabed

        public MsgType Type => MsgType.AnchorState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Tick);
            w.Put((byte)Frame);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
            w.PutVector3(Vel);
            w.Put(Set);
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            Frame = (CoordFrame)r.GetByte();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
            Vel = r.GetVector3();
            Set = r.GetBool();
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

    /// <summary>
    /// A client's request to set one steering wheel's input (it turned the wheel). The boat
    /// steers off the old <c>Rudder</c>: the wheel's <c>currentInput</c> drives the rudder
    /// hinge's spring target. The host sets the same wheel's <c>currentInput</c> and re-applies
    /// the rudder rotation, so the host's boat actually turns and BoatSync carries the new
    /// heading back to everyone. Index = position in the boat's steering-wheel enumeration.
    /// </summary>
    public sealed class SteerRequestMsg : INetMessage
    {
        public ushort Index;
        public float Input;

        public MsgType Type => MsgType.SteerRequest;

        public void Serialize(NetDataWriter w) { w.Put(Index); w.Put(Input); }
        public void Deserialize(NetDataReader r) { Index = r.GetUShort(); Input = r.GetFloat(); }
    }

    /// <summary>Mooring action on one rope.</summary>
    public enum MooringKind : byte
    {
        Unmoor = 0,   // PickupableBoatMooringRope.Unmoor()
        Moor = 1,     // MoorTo(dock) — dock found by DockReal position
        Length = 2,   // adjusted rope length — LengthSq is the new currentRopeLengthSquared
    }

    /// <summary>
    /// One mooring action, mirrored both ways and relayed by the host. The rope is referenced by
    /// its index in <c>BoatMooringRopes.ropes</c> (identical on both machines — same ship).
    /// For <see cref="MooringKind.Moor"/> the target dock is given by its real-space position;
    /// the receiver finds the nearest <c>GPButtonDockMooring</c> (docks are static world objects).
    ///
    /// <para>Host→client uses <see cref="MooringStateMsg"/>; client→host uses
    /// <see cref="MooringRequestMsg"/>. Same payload, two directions, so the host stays the
    /// authority (the only spring that holds the authoritative boat is the host's).</para>
    /// </summary>
    public sealed class MooringStateMsg : INetMessage
    {
        public ushort Index;
        public MooringKind Kind;
        public Vector3 DockReal;   // real-space dock position (Moor only)
        public float LengthSq;     // new currentRopeLengthSquared (Length only)

        public MsgType Type => MsgType.MooringState;

        public void Serialize(NetDataWriter w) { w.Put(Index); w.Put((byte)Kind); w.PutVector3(DockReal); w.Put(LengthSq); }
        public void Deserialize(NetDataReader r) { Index = r.GetUShort(); Kind = (MooringKind)r.GetByte(); DockReal = r.GetVector3(); LengthSq = r.GetFloat(); }
    }

    /// <summary>Client -> host mooring action (see <see cref="MooringStateMsg"/>). Host applies + relays.</summary>
    public sealed class MooringRequestMsg : INetMessage
    {
        public ushort Index;
        public MooringKind Kind;
        public Vector3 DockReal;
        public float LengthSq;

        public MsgType Type => MsgType.MooringRequest;

        public void Serialize(NetDataWriter w) { w.Put(Index); w.Put((byte)Kind); w.PutVector3(DockReal); w.Put(LengthSq); }
        public void Deserialize(NetDataReader r) { Index = r.GetUShort(); Kind = (MooringKind)r.GetByte(); DockReal = r.GetVector3(); LengthSq = r.GetFloat(); }
    }

    // ---------------------------------------------------------------------
    // Boat damage / bilge water (Stage 2) — host -> client snapshot
    // ---------------------------------------------------------------------

    /// <summary>
    /// Host-authoritative boat damage state. The host owns flooding/sinking and bilge pump
    /// effects; clients mirror these scalar fields so water visuals and drag/sink state converge.
    /// </summary>
    public sealed class BoatDamageStateMsg : INetMessage
    {
        public long Tick;
        public float WaterLevel;
        public float HullDamage;
        public float Oakum;
        public float WaterIntakeChunk;
        public bool Sunk;

        public MsgType Type => MsgType.BoatDamageState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Tick);
            w.Put(WaterLevel);
            w.Put(HullDamage);
            w.Put(Oakum);
            w.Put(WaterIntakeChunk);
            w.Put(Sunk);
        }

        public void Deserialize(NetDataReader r)
        {
            Tick = r.GetLong();
            WaterLevel = r.GetFloat();
            HullDamage = r.GetFloat();
            Oakum = r.GetFloat();
            WaterIntakeChunk = r.GetFloat();
            Sunk = r.GetBool();
        }
    }

    /// <summary>Discrete interaction kind, mirrored from the game's <c>GoPointerButton</c> handlers.</summary>
    public enum InteractKind : byte
    {
        Activate = 0,      // OnActivate(GoPointer) — primary click (toggles, presses)
        AltActivate = 1,   // OnAltActivate(GoPointer) — secondary action (untie, quick-release, unmoor)
        ActivateNoArg = 2, // OnActivate() — simple toggles such as trapdoors
    }

    /// <summary>
    /// A client's discrete interaction with one of the boat's <c>GoPointerButton</c>s
    /// (anything that isn't a continuous winch/wheel drag): toggling a cleat, quick-release,
    /// untying a mooring line, etc. The button is referenced by its index in the boat's
    /// <c>GetComponentsInChildren&lt;GoPointerButton&gt;</c> order — identical on both machines
    /// because they load the same ship. The host replays the same handler authoritatively (F3),
    /// so its game logic (and the resulting state sync) is the source of truth. ReliableOrdered:
    /// these are one-shot events that must not be dropped.
    /// </summary>
    public sealed class ControlEventMsg : INetMessage
    {
        public ushort Index;
        public InteractKind Kind;

        public MsgType Type => MsgType.ControlEvent;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put((byte)Kind);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            Kind = (InteractKind)r.GetByte();
        }
    }

    /// <summary>
    /// A client's held-button transition. This is for interactions whose meaning is not a
    /// one-shot click but "keep doing this until released" (currently BilgePump). The button
    /// index uses the same boat-local <c>GoPointerButton</c> order as <see cref="ControlEventMsg"/>.
    /// </summary>
    public sealed class HoldRequestMsg : INetMessage
    {
        public ushort Index;
        public InteractKind Kind;
        public bool Down;

        public MsgType Type => MsgType.HoldRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put((byte)Kind);
            w.Put(Down);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            Kind = (InteractKind)r.GetByte();
            Down = r.GetBool();
        }
    }

    public enum DamageAction : byte
    {
        AddOakum = 0,
        BailWater = 1,
    }

    /// <summary>
    /// A client's damage-domain item action. Until full item replication exists, the client
    /// applies vanilla local item logic and forwards only the authoritative scalar delta.
    /// Host clamps the result against its own <c>BoatDamage</c>.
    /// </summary>
    public sealed class DamageRequestMsg : INetMessage
    {
        public DamageAction Action;
        public float Amount;

        public MsgType Type => MsgType.DamageRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Action);
            w.Put(Amount);
        }

        public void Deserialize(NetDataReader r)
        {
            Action = (DamageAction)r.GetByte();
            Amount = r.GetFloat();
        }
    }

    /// <summary>
    /// A client's continuous push against a shared physics object. The client computes the
    /// same force vector Sailwind would apply locally while the push collider is clicked;
    /// the host applies it to the authoritative rigidbody at the real-space contact point.
    /// </summary>
    public sealed class PushRequestMsg : INetMessage
    {
        public ushort Index;
        public Vector3 RealPos;
        public Vector3 Force;
        public float DeltaTime;

        public MsgType Type => MsgType.PushRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.PutVector3(RealPos);
            w.PutVector3(Force);
            w.Put(DeltaTime);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            RealPos = r.GetVector3();
            Force = r.GetVector3();
            DeltaTime = r.GetFloat();
        }
    }

    /// <summary>Shared lantern/light state, indexed by stable scene hierarchy order.</summary>
    public sealed class LightStateMsg : INetMessage
    {
        public ushort Index;
        public bool On;
        public float Health;

        public MsgType Type => MsgType.LightState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(On);
            w.Put(Health);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            On = r.GetBool();
            Health = r.GetFloat();
        }
    }

    /// <summary>Client -> host light change request; same payload as <see cref="LightStateMsg"/>.</summary>
    public sealed class LightRequestMsg : INetMessage
    {
        public ushort Index;
        public bool On;
        public float Health;

        public MsgType Type => MsgType.LightRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(On);
            w.Put(Health);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            On = r.GetBool();
            Health = r.GetFloat();
        }
    }

    // ---------------------------------------------------------------------
    // Physical items (P3) — existing save-loaded ShipItem replication
    // ---------------------------------------------------------------------

    public enum ItemAction : byte
    {
        Pose = 0,
        Pickup = 1,
        Drop = 2,
        AltHeld = 3,      // client holds alt with the item in hand (continuous, throttled) — host replays OnAltHeld
        AltActivate = 4,  // client alt-clicked the held item (discrete) — host replays OnAltActivate
        State = 5,        // client changed held item scalars locally (water in mug, etc.) — host adopts amount/health
    }

    public sealed class ItemStateMsg : INetMessage
    {
        public ushort Index;
        public int InstanceId;
        public int PrefabIndex;
        public long Tick;
        public CoordFrame Frame;
        public ushort BoatIndex;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;
        public uint HolderNetId;
        public float Amount;
        public float Health;
        public bool Sold;
        public bool Nailed;

        public MsgType Type => MsgType.ItemState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(InstanceId);
            w.Put(PrefabIndex);
            w.Put(Tick);
            w.Put((byte)Frame);
            w.Put(BoatIndex);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
            w.PutVector3(Vel);
            w.Put(HolderNetId);
            w.Put(Amount);
            w.Put(Health);
            w.Put(Sold);
            w.Put(Nailed);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetUShort();
            InstanceId = r.GetInt();
            PrefabIndex = r.GetInt();
            Tick = r.GetLong();
            Frame = (CoordFrame)r.GetByte();
            BoatIndex = r.GetUShort();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
            Vel = r.GetVector3();
            HolderNetId = r.GetUInt();
            Amount = r.GetFloat();
            Health = r.GetFloat();
            Sold = r.GetBool();
            Nailed = r.GetBool();
        }
    }

    public sealed class ItemRequestMsg : INetMessage
    {
        public ItemAction Action;
        public ushort Index;
        public int InstanceId;
        public int PrefabIndex;
        public long Tick;
        public CoordFrame Frame;
        public ushort BoatIndex;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;
        public float Amount;
        public float Health;
        public bool Sold;
        public bool Nailed;

        public MsgType Type => MsgType.ItemRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Action);
            w.Put(Index);
            w.Put(InstanceId);
            w.Put(PrefabIndex);
            w.Put(Tick);
            w.Put((byte)Frame);
            w.Put(BoatIndex);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
            w.PutVector3(Vel);
            w.Put(Amount);
            w.Put(Health);
            w.Put(Sold);
            w.Put(Nailed);
        }

        public void Deserialize(NetDataReader r)
        {
            Action = (ItemAction)r.GetByte();
            Index = r.GetUShort();
            InstanceId = r.GetInt();
            PrefabIndex = r.GetInt();
            Tick = r.GetLong();
            Frame = (CoordFrame)r.GetByte();
            BoatIndex = r.GetUShort();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
            Vel = r.GetVector3();
            Amount = r.GetFloat();
            Health = r.GetFloat();
            Sold = r.GetBool();
            Nailed = r.GetBool();
        }
    }

    // ---------------------------------------------------------------------
    // Item lifecycle (P3) — host-authoritative spawn/despawn of runtime items
    // (caught fish, cooked food, crate contents, …) that aren't in the base save.
    // ReliableOrdered: a missed spawn/despawn leaves the world permanently diverged.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Host -> client: create a physical item that appeared in the host's world after load.
    /// Carries the same pose/scalar payload as <see cref="ItemStateMsg"/> plus a
    /// <see cref="NetObjKind"/> tag (only <c>Item</c> is used today; the field keeps the
    /// channel open for non-item spawns later).
    /// </summary>
    public sealed class SpawnObjectMsg : INetMessage
    {
        public byte Kind;          // NetObjKind (Item)
        public int InstanceId;
        public int PrefabIndex;
        public CoordFrame Frame;
        public ushort BoatIndex;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;
        public uint HolderNetId;
        public float Amount;
        public float Health;
        public bool Sold;
        public bool Nailed;

        public MsgType Type => MsgType.SpawnObject;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Kind);
            w.Put(InstanceId);
            w.Put(PrefabIndex);
            w.Put((byte)Frame);
            w.Put(BoatIndex);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
            w.PutVector3(Vel);
            w.Put(HolderNetId);
            w.Put(Amount);
            w.Put(Health);
            w.Put(Sold);
            w.Put(Nailed);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = r.GetByte();
            InstanceId = r.GetInt();
            PrefabIndex = r.GetInt();
            Frame = (CoordFrame)r.GetByte();
            BoatIndex = r.GetUShort();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
            Vel = r.GetVector3();
            HolderNetId = r.GetUInt();
            Amount = r.GetFloat();
            Health = r.GetFloat();
            Sold = r.GetBool();
            Nailed = r.GetBool();
        }
    }

    /// <summary>Host -> client: an item was destroyed/consumed; remove the client's copy.</summary>
    public sealed class DespawnObjectMsg : INetMessage
    {
        public byte Kind;          // NetObjKind (Item)
        public int InstanceId;

        public MsgType Type => MsgType.DespawnObject;

        public void Serialize(NetDataWriter w) { w.Put(Kind); w.Put(InstanceId); }
        public void Deserialize(NetDataReader r) { Kind = r.GetByte(); InstanceId = r.GetInt(); }
    }

    /// <summary>
    /// Host -> client: per-type item state beyond the generic pose/scalars — cooking heat,
    /// fuel lit, food doneness, liquid contents, etc. The field set per type is fixed by an
    /// agreed table on both peers (see <c>ItemSync.ExtraFields</c>), so <see cref="Values"/> is
    /// positional. Floats carry bools (0/1) and enums (cast to int) too; the client converts
    /// back per the real field type via reflection.
    /// </summary>
    public sealed class ItemExtraStateMsg : INetMessage
    {
        public int InstanceId;
        public int PrefabIndex;
        public float[] Values = System.Array.Empty<float>();

        public MsgType Type => MsgType.ItemExtra;

        public void Serialize(NetDataWriter w)
        {
            w.Put(InstanceId);
            w.Put(PrefabIndex);
            w.Put((byte)Values.Length);
            for (int i = 0; i < Values.Length; i++) w.Put(Values[i]);
        }

        public void Deserialize(NetDataReader r)
        {
            InstanceId = r.GetInt();
            PrefabIndex = r.GetInt();
            int n = r.GetByte();
            Values = new float[n];
            for (int i = 0; i < n; i++) Values[i] = r.GetFloat();
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
