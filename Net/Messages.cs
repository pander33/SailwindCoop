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
        WindRequest = 63,      // client -> host : "I'm steering the wind with a held WindTotemOrb"
        ShopRequest = 64,      // client -> host : "buy this unsold item / sell this held item"
        ShopResult = 65,       // host -> client : buy/sell outcome (ok or reason) for notifications
        FishCatch = 66,        // client -> host : "I caught a fish (prefab P) here" — host authors the real item
        CargoResult = 67,      // host -> client : cargo load/unload outcome; withdraw tells the client to take the item
        StormState = 68,       // host -> client : wandering-storm positions + active flags (re-anchor drift)
        SleepState = 69,       // host -> client : host fell asleep / woke (shared blackout + time-skip)
        MissionJournal = 70,   // host -> client : the shared mission journal (read-only mirror)
        MissionReward = 71,    // host -> client : credit a mission delivery payout to the client's own wallet
        MissionAccept = 72,    // client -> host : "accept THIS mission" (full spec — offers differ per machine)
        MissionAbandon = 73,   // client -> host : "abandon the mission in slot N"
        BoatPurchase = 74,     // both : a purchasable boat was bought — mark it purchased on the other peer
        AvatarChange = 75,     // both : this player switched to a different avatar bundle mid-session

        // --- save streaming (ReliableOrdered) — host streams its world save to a joining client ---
        SaveSnapshotBegin = 76, // host -> client : a save transfer is starting (total size, chunk count, game version)
        SaveSnapshotChunk = 77, // host -> client : one chunk of the serialized SaveContainer bytes
        SaveSnapshotEnd = 78,   // host -> client : transfer complete (final signal; client merges + loads)
        ClientWorldLoaded = 79, // client -> host : "I finished (or failed) loading your world" — host lifts the join-pause

        RodState = 80,          // both : fishing-rod cast visual — bobber real-pos + line length + rod bend from the holder
    }

    /// <summary>Which shop transaction a <see cref="ShopRequestMsg"/> asks the host to perform.</summary>
    public enum ShopKind : byte
    {
        Buy = 0,   // client buys an unsold shop item (host gold pays; item handed to the client)
        Sell = 1,  // client sells a held item (host gold credited; item destroyed)
    }

    /// <summary>Why a <see cref="ShopResultMsg"/> reports failure.</summary>
    public enum ShopFailReason : byte
    {
        None = 0,
        NotEnoughMoney = 1,
        ItemNotFound = 2,
        ShopNotFound = 3,
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
        public string SelectedAvatar = ""; // avatar bundle file name, e.g. "avatar1.bundle"

        public MsgType Type => MsgType.Hello;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ProtocolVersion);
            w.Put(ModVersion);
            w.Put(WorldId);
            w.Put(PlayerName);
            w.Put(SelectedAvatar ?? "");
        }

        public void Deserialize(NetDataReader r)
        {
            ProtocolVersion = r.GetInt();
            ModVersion = r.GetString();
            WorldId = r.GetString();
            PlayerName = r.GetString();
            SelectedAvatar = r.GetString();
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
        public bool Crouch;       // local player crouch intent for remote avatar animation

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
            w.Put(Crouch);
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
            Crouch = r.GetBool();
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
        // waves (WavesInertia: transform.rotation / currentInertia / currentMagnitude) —
        // mismatched sea state makes the host-authoritative boat sit under/above the client's water.
        public bool HasWaves;
        public Quaternion WavesRot;
        public float WavesInertia;
        public float WavesMagnitude;
        // wave clock (Ocean FFT phase = sqrt(g·k)·Time.time·speed) — the client re-drives
        // Ocean.calcComplex with the HOST's Time.time so crests line up on both machines.
        // HostTimeScale = host Time.timeScale: 0 during JoinPause, so the client's water
        // freezes in the host's phase and unfreezes together with it.
        public float WaveTime;
        public float HostTimeScale;

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
            w.Put(HasWaves);
            w.PutQuaternion(WavesRot);
            w.Put(WavesInertia);
            w.Put(WavesMagnitude);
            w.Put(WaveTime);
            w.Put(HostTimeScale);
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
            HasWaves = r.GetBool();
            WavesRot = r.GetQuaternion();
            WavesInertia = r.GetFloat();
            WavesMagnitude = r.GetFloat();
            WaveTime = r.GetFloat();
            HostTimeScale = r.GetFloat();
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
        Consume = 6,      // client consumed the item (ate food) — host destroys its copy (no host PlayerNeeds)
        Nail = 7,         // client (un)nailed a TARGET item with a hammer — host sets target.nailed (InstanceId=target)
        Crate = 8,        // client moved a TARGET item in/out of a crate — host mirrors membership (InstanceId=item, CrateId=dest)
        Unseal = 9,       // client unsealed a crate (InstanceId=crate) — host authors the contained items
        Cargo = 10,       // client loaded/unloaded port cargo — host charges the wallet and moves the item
        Inventory = 11,   // client moved an item in/out of a personal belt slot
        RodHook = 12,     // крючок удочки появился/пропал (attach через OnItemClick / DetachHook при сходе рыбы) — хост ставит rod.health из Health и рассылает ItemState
        LampHook = 13,    // client hung a ShipItemHangable on a ShipItemLampHook; CrateId/CargoIndex carry hook id/prefab
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
        public int CrateId;       // SaveablePrefab.currentCrateId — which crate contains this item (0 = none)
        public int CargoPort = -1; // CargoCarrier.portIndex this item is stored in (-1 = not in cargo)
        public int InventorySlot = -1; // personal belt slot 0..4 (-1 = not in a belt slot)
        public bool Attached;      // ItemRigidbody.attached — предмет "положен"/повешен (F-place), физика заморожена

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
            w.Put(CrateId);
            w.Put(CargoPort);
            w.Put(InventorySlot);
            w.Put(Attached);
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
            CrateId = r.GetInt();
            CargoPort = r.GetInt();
            InventorySlot = r.GetInt();
            Attached = r.GetBool();
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
        public int CrateId;        // Crate action: the crate this item should belong to (0 = withdraw)
        public int CargoPort = -1; // Cargo action: target carrier portIndex
        public int CargoIndex = -1; // Cargo withdraw: index into the carrier's cargo list
        public int InventorySlot = -1; // Inventory action: personal belt slot 0..4 (-1 = withdraw)
        public bool Attached;      // Drop: предмет "положен"/повешен через F-place (ItemRigidbody.attached), Vel игнорируется

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
            w.Put(CrateId);
            w.Put(CargoPort);
            w.Put(CargoIndex);
            w.Put(InventorySlot);
            w.Put(Attached);
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
            CrateId = r.GetInt();
            CargoPort = r.GetInt();
            CargoIndex = r.GetInt();
            InventorySlot = r.GetInt();
            Attached = r.GetBool();
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
        public int CrateId;       // SaveablePrefab.currentCrateId (0 = not in a crate)
        public int CargoPort = -1; // CargoCarrier.portIndex (-1 = not in cargo)
        public int InventorySlot = -1; // personal belt slot 0..4 (-1 = not in a belt slot)

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
            w.Put(CrateId);
            w.Put(CargoPort);
            w.Put(InventorySlot);
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
            CrateId = r.GetInt();
            CargoPort = r.GetInt();
            InventorySlot = r.GetInt();
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
    // WindTotemOrb (Phase 1) — a non-ShipItem PickupableItem whose effect is
    // authoritative world state: while held it drives Wind.instance.ForceNewWind.
    // The client can't own the wind (EnvironmentSync makes it host-authoritative),
    // so it forwards the orb's computed wind vector; the host applies ForceNewWind
    // and EnvironmentSync distributes the result to everyone like any other wind.
    // ---------------------------------------------------------------------

    /// <summary>Client -> host: the wind vector the held WindTotemOrb wants to force.</summary>
    public sealed class WindRequestMsg : INetMessage
    {
        public Vector3 Wind;

        public MsgType Type => MsgType.WindRequest;

        public void Serialize(NetDataWriter w) { w.PutVector3(Wind); }
        public void Deserialize(NetDataReader r) { Wind = r.GetVector3(); }
    }

    // ---------------------------------------------------------------------
    // Fishing (Phase 1) — the rod/bobber/casting are physics-driven and stay local; only
    // the gameplay outcome is shared. A caught fish is authored on the client (RNG), but
    // ItemSync is host-authoritative, so the client asks the host to create the real fish
    // item; the host's SpawnObject then replicates it and the client remaps its local catch.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Host -> client: outcome of a cargo load/unload. On a successful withdraw the client locally
    /// takes its own copy of the item (by id) out of the carrier and into hand; on failure (no money /
    /// not found) it shows a notification. Insert success needs no result — membership replicates.
    /// </summary>
    public sealed class CargoResultMsg : INetMessage
    {
        public bool Ok;
        public bool IsWithdraw;
        public ShopFailReason Reason;
        public int InstanceId;     // withdraw ok: the item the client should take into hand

        public MsgType Type => MsgType.CargoResult;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Ok);
            w.Put(IsWithdraw);
            w.Put((byte)Reason);
            w.Put(InstanceId);
        }

        public void Deserialize(NetDataReader r)
        {
            Ok = r.GetBool();
            IsWithdraw = r.GetBool();
            Reason = (ShopFailReason)r.GetByte();
            InstanceId = r.GetInt();
        }
    }

    /// <summary>Client -> host: a fish (PrefabsDirectory prefab) was caught at this pose; author it.</summary>
    public sealed class FishCatchMsg : INetMessage
    {
        public int PrefabIndex;
        public CoordFrame Frame;
        public ushort BoatIndex;
        public Vector3 Pos;
        public Quaternion Rot;

        public MsgType Type => MsgType.FishCatch;

        public void Serialize(NetDataWriter w)
        {
            w.Put(PrefabIndex);
            w.Put((byte)Frame);
            w.Put(BoatIndex);
            w.PutVector3(Pos);
            w.PutQuaternion(Rot);
        }

        public void Deserialize(NetDataReader r)
        {
            PrefabIndex = r.GetInt();
            Frame = (CoordFrame)r.GetByte();
            BoatIndex = r.GetUShort();
            Pos = r.GetVector3();
            Rot = r.GetQuaternion();
        }
    }

    /// <summary>
    /// Держащий удочку игрок -> все остальные (клиент шлёт хосту, хост ретранслирует): косметика
    /// заброса — позиция боббера (real-space), длина лески (linearLimit) и изгиб удилища. Получатель
    /// кинематически ведёт боббер и подставляет currentTargetLength; леску/изгиб рисует ваниль
    /// (ExtraLateUpdate → UpdateRope). Unreliable, ~10 Гц; пропажа потока = таймаут и возврат физики.
    /// </summary>
    public sealed class RodStateMsg : INetMessage
    {
        public int InstanceId;
        public int PrefabIndex;
        public Vector3 RealPos;   // боббер в real-координатах (стабильных к floating origin)
        public float Limit;       // bobberJoint.linearLimit.limit — длина отпущенной лески
        public float Bend;        // currentRodBend — изгиб удилища (натяжение)

        public MsgType Type => MsgType.RodState;

        public void Serialize(NetDataWriter w)
        {
            w.Put(InstanceId);
            w.Put(PrefabIndex);
            w.PutVector3(RealPos);
            w.Put(Limit);
            w.Put(Bend);
        }

        public void Deserialize(NetDataReader r)
        {
            InstanceId = r.GetInt();
            PrefabIndex = r.GetInt();
            RealPos = r.GetVector3();
            Limit = r.GetFloat();
            Bend = r.GetFloat();
        }
    }

    // ---------------------------------------------------------------------
    // Shop economy (Phase 1) — buy/sell is host-authoritative (host's wallet,
    // host's PlayerGold). The client never runs the local transaction (its gold
    // isn't authoritative); it forwards a request, the host runs the vanilla
    // Shopkeeper method, and the result replicates via the normal item channel
    // (a bought item is handed to the client; a sold item despawns).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Client -> host: perform a shop transaction. The keeper is named by a stable
    /// <see cref="ShopLocator"/> index. For a Buy the unsold item is addressed by prefab +
    /// real-space position (unsold shop items aren't save-registered, but sit at fixed
    /// identical positions on both peers). For a Sell the held item carries its instanceId.
    /// </summary>
    public sealed class ShopRequestMsg : INetMessage
    {
        public ShopKind Kind;
        public ushort KeeperIndex;
        public int InstanceId;     // Sell: the held item's id (0 for Buy)
        public int PrefabIndex;    // Buy: which good to match in the keeper's stock
        public Vector3 RealPos;    // Buy: real-space position to disambiguate identical-prefab stock

        public MsgType Type => MsgType.ShopRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind);
            w.Put(KeeperIndex);
            w.Put(InstanceId);
            w.Put(PrefabIndex);
            w.PutVector3(RealPos);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (ShopKind)r.GetByte();
            KeeperIndex = r.GetUShort();
            InstanceId = r.GetInt();
            PrefabIndex = r.GetInt();
            RealPos = r.GetVector3();
        }
    }

    /// <summary>Host -> client: outcome of a shop transaction (for a client-side notification).</summary>
    public sealed class ShopResultMsg : INetMessage
    {
        public ShopKind Kind;
        public bool Ok;
        public ShopFailReason Reason;
        public int InstanceId;     // Buy ok: the host's stable id for the bought item, for the client to adopt

        public MsgType Type => MsgType.ShopResult;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind);
            w.Put(Ok);
            w.Put((byte)Reason);
            w.Put(InstanceId);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (ShopKind)r.GetByte();
            Ok = r.GetBool();
            Reason = (ShopFailReason)r.GetByte();
            InstanceId = r.GetInt();
        }
    }

    // ---------------------------------------------------------------------
    // Weather storms — wandering storms drift by the (synced) wind each frame, but each
    // machine integrates that drift over its own frame times, so positions slowly diverge.
    // The host periodically re-anchors every storm's real-space position + active flag; the
    // weather visuals derive from storm proximity, so syncing storms keeps weather aligned
    // without serializing the non-networkable WeatherSet scene objects.
    // ---------------------------------------------------------------------

    public sealed class StormStateMsg : INetMessage
    {
        public Vector3[] Pos = System.Array.Empty<Vector3>();
        public bool[] Active = System.Array.Empty<bool>();
        public float Distance;

        public MsgType Type => MsgType.StormState;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Pos.Length);
            for (int i = 0; i < Pos.Length; i++) { w.PutVector3(Pos[i]); w.Put(Active[i]); }
            w.Put(Distance);
        }

        public void Deserialize(NetDataReader r)
        {
            int n = r.GetByte();
            Pos = new Vector3[n];
            Active = new bool[n];
            for (int i = 0; i < n; i++) { Pos[i] = r.GetVector3(); Active[i] = r.GetBool(); }
            Distance = r.GetFloat();
        }
    }

    /// <summary>Host → client: the host is sleeping (true) or awake (false). Drives the client's shared
    /// blackout + control lock while the host authoritatively warps time (P4.2).</summary>
    public sealed class SleepStateMsg : INetMessage
    {
        public bool Sleeping;

        public MsgType Type => MsgType.SleepState;

        public void Serialize(NetDataWriter w) { w.Put(Sleeping); }
        public void Deserialize(NetDataReader r) { Sleeping = r.GetBool(); }
    }

    /// <summary>One active mission, mirroring the game's <c>SaveMissionData</c> (stable port/prefab
    /// indices). The slot is <see cref="MissionIndex"/> (0..4).</summary>
    public struct MissionEntry
    {
        public byte MissionIndex;
        public int OriginPort;
        public int DestinationPort;
        public int GoodPrefabIndex;
        public int GoodCount;
        public int TotalPrice;
        public float InsuranceLevel;
        public float Distance;
        public int DeliveredGoods;
        public int DueDay;
    }

    /// <summary>Host → client: the full shared mission journal (read-only mirror). Slots not listed are
    /// empty on the client. Built from <c>PlayerMissions.missions[i].PrepareSaveData()</c>.</summary>
    public sealed class MissionJournalMsg : INetMessage
    {
        public MissionEntry[] Missions = System.Array.Empty<MissionEntry>();

        public MsgType Type => MsgType.MissionJournal;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Missions.Length);
            for (int i = 0; i < Missions.Length; i++)
            {
                var m = Missions[i];
                w.Put(m.MissionIndex);
                w.Put(m.OriginPort); w.Put(m.DestinationPort); w.Put(m.GoodPrefabIndex);
                w.Put(m.GoodCount); w.Put(m.TotalPrice); w.Put(m.InsuranceLevel);
                w.Put(m.Distance); w.Put(m.DeliveredGoods); w.Put(m.DueDay);
            }
        }

        public void Deserialize(NetDataReader r)
        {
            int n = r.GetByte();
            Missions = new MissionEntry[n];
            for (int i = 0; i < n; i++)
            {
                Missions[i] = new MissionEntry
                {
                    MissionIndex = r.GetByte(),
                    OriginPort = r.GetInt(),
                    DestinationPort = r.GetInt(),
                    GoodPrefabIndex = r.GetInt(),
                    GoodCount = r.GetInt(),
                    TotalPrice = r.GetInt(),
                    InsuranceLevel = r.GetFloat(),
                    Distance = r.GetFloat(),
                    DeliveredGoods = r.GetInt(),
                    DueDay = r.GetInt(),
                };
            }
        }
    }

    /// <summary>Host → client: a mission delivery just paid out on the host; credit the same amount to the
    /// client's own wallet (separate-money model — the reward is duplicated to both players).</summary>
    public sealed class MissionRewardMsg : INetMessage
    {
        public int Region;
        public int Amount;
        public int OriginRegion = -1;
        public int DestinationRegion = -1;
        public int RepAmount;
        public int GoodPrefabIndex;
        public string DestinationName = "";
        public int ExpectedReward;

        public MsgType Type => MsgType.MissionReward;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Region);
            w.Put(Amount);
            w.Put(OriginRegion);
            w.Put(DestinationRegion);
            w.Put(RepAmount);
            w.Put(GoodPrefabIndex);
            w.Put(DestinationName ?? "");
            w.Put(ExpectedReward);
        }

        public void Deserialize(NetDataReader r)
        {
            Region = r.GetInt();
            Amount = r.GetInt();
            OriginRegion = r.GetInt();
            DestinationRegion = r.GetInt();
            RepAmount = r.GetInt();
            GoodPrefabIndex = r.GetInt();
            DestinationName = r.GetString();
            ExpectedReward = r.GetInt();
        }
    }

    /// <summary>Client → host: accept this exact mission. The mission OFFER list is generated from
    /// per-player state (reputation/prices) and can differ between machines, so we send the full spec
    /// (a <see cref="MissionEntry"/>) and the host accepts it directly rather than an offer-list index.</summary>
    public sealed class MissionAcceptMsg : INetMessage
    {
        public MissionEntry Mission;

        public MsgType Type => MsgType.MissionAccept;

        public void Serialize(NetDataWriter w)
        {
            var m = Mission;
            w.Put(m.OriginPort); w.Put(m.DestinationPort); w.Put(m.GoodPrefabIndex);
            w.Put(m.GoodCount); w.Put(m.TotalPrice); w.Put(m.InsuranceLevel);
            w.Put(m.Distance); w.Put(m.DueDay);
        }

        public void Deserialize(NetDataReader r)
        {
            Mission = new MissionEntry
            {
                OriginPort = r.GetInt(),
                DestinationPort = r.GetInt(),
                GoodPrefabIndex = r.GetInt(),
                GoodCount = r.GetInt(),
                TotalPrice = r.GetInt(),
                InsuranceLevel = r.GetFloat(),
                Distance = r.GetFloat(),
                DueDay = r.GetInt(),
            };
        }
    }

    /// <summary>Client → host: abandon the mission in the given shared journal slot.</summary>
    public sealed class MissionAbandonMsg : INetMessage
    {
        public byte MissionIndex;

        public MsgType Type => MsgType.MissionAbandon;

        public void Serialize(NetDataWriter w) { w.Put(MissionIndex); }
        public void Deserialize(NetDataReader r) { MissionIndex = r.GetByte(); }
    }

    /// <summary>Both directions: a purchasable boat was bought (its <c>SaveableObject.extraSetting</c> flipped
    /// to true). The buyer paid from their own wallet; the other peer just marks the same boat purchased (by
    /// stable <c>sceneIndex</c>) so both enumerate and sync it as a network boat. P4.5.</summary>
    public sealed class BoatPurchaseMsg : INetMessage
    {
        public int SceneIndex;

        public MsgType Type => MsgType.BoatPurchase;

        public void Serialize(NetDataWriter w) { w.Put(SceneIndex); }
        public void Deserialize(NetDataReader r) { SceneIndex = r.GetInt(); }
    }

    // ---------------------------------------------------------------------
    // Avatar selection
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sent when a player switches to a different avatar bundle file. The host
    /// rebroadcasts it to other clients so they can rebuild that player's
    /// remote avatar from the new bundle.
    /// </summary>
    public sealed class AvatarChangeMsg : INetMessage
    {
        public uint NetId;                  // the player whose avatar changed
        public string BundleFile = "";      // e.g. "avatar1.bundle"

        public MsgType Type => MsgType.AvatarChange;

        public void Serialize(NetDataWriter w)
        {
            w.Put(NetId);
            w.Put(BundleFile ?? "");
        }

        public void Deserialize(NetDataReader r)
        {
            NetId = r.GetUInt();
            BundleFile = r.GetString();
        }
    }

    // ---------------------------------------------------------------------
    // Save streaming — the host serializes its current world save (SaveContainer)
    // and streams the raw bytes to a joining client in chunks over the reliable
    // channel. The client reassembles them, overlays its own character profile
    // (money/reputation/needs/missions), writes the merged save to the coop slot
    // and loads it — so the guest enters the HOST's world but keeps its own progress.
    // ReliableOrdered guarantees in-order, complete delivery.
    // ---------------------------------------------------------------------

    /// <summary>Host -> client: announces a save transfer. <see cref="GameVersion"/> is the host's
    /// <c>SaveLoadManager.gameVersion</c> so the client can refuse a mismatched save format.</summary>
    public sealed class SaveSnapshotBeginMsg : INetMessage
    {
        public int TotalBytes;
        public int ChunkCount;
        public int GameVersion;

        public MsgType Type => MsgType.SaveSnapshotBegin;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TotalBytes);
            w.Put(ChunkCount);
            w.Put(GameVersion);
        }

        public void Deserialize(NetDataReader r)
        {
            TotalBytes = r.GetInt();
            ChunkCount = r.GetInt();
            GameVersion = r.GetInt();
        }
    }

    /// <summary>Host -> client: one ordered chunk of the serialized SaveContainer.</summary>
    public sealed class SaveSnapshotChunkMsg : INetMessage
    {
        public int Index;
        public byte[] Data = System.Array.Empty<byte>();

        public MsgType Type => MsgType.SaveSnapshotChunk;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.PutBytesWithLength(Data);
        }

        public void Deserialize(NetDataReader r)
        {
            Index = r.GetInt();
            Data = r.GetBytesWithLength();
        }
    }

    /// <summary>Host -> client: all chunks sent; the client now merges + loads.</summary>
    public sealed class SaveSnapshotEndMsg : INetMessage
    {
        public bool Ok;

        public MsgType Type => MsgType.SaveSnapshotEnd;

        public void Serialize(NetDataWriter w) { w.Put(Ok); }
        public void Deserialize(NetDataReader r) { Ok = r.GetBool(); }
    }

    /// <summary>Client -> host: the streamed world finished loading (<see cref="Ok"/> = true) or the
    /// load failed/was refused (false). Either way the host lifts its join-pause for this client.</summary>
    public sealed class ClientWorldLoadedMsg : INetMessage
    {
        public bool Ok;

        public MsgType Type => MsgType.ClientWorldLoaded;

        public void Serialize(NetDataWriter w) { w.Put(Ok); }
        public void Deserialize(NetDataReader r) { Ok = r.GetBool(); }
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
