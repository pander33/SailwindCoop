using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    /// <summary>
    /// Single (de)serialization entry point (F1). Every packet on the wire is:
    ///   [ byte msgType ][ message payload ]
    /// Bump <see cref="Version"/> whenever the wire format of any message changes
    /// in a non-backward-compatible way; the handshake compares versions and a
    /// mismatch is rejected with a clear reason.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Wire protocol version. Increment on any breaking format change.</summary>
        public const int Version = 45;

        /// <summary>Writes [msgType][payload] into a fresh writer ready to send.</summary>
        public static NetDataWriter Write(INetMessage msg)
        {
            var w = new NetDataWriter();
            w.Put((byte)msg.Type);
            msg.Serialize(w);
            return w;
        }

        /// <summary>Reads the leading msgType byte without consuming the payload position semantics.</summary>
        public static MsgType PeekType(NetPacketReader reader)
        {
            return (MsgType)reader.GetByte();
        }

        /// <summary>
        /// Reads a message of the given type from a reader whose msgType byte has
        /// already been consumed (see <see cref="PeekType"/>). Returns null for
        /// unknown types so the caller can log and drop rather than crash.
        /// </summary>
        public static INetMessage ReadBody(MsgType type, NetDataReader r)
        {
            INetMessage msg = Create(type);
            if (msg == null) return null;
            msg.Deserialize(r);
            return msg;
        }

        private static INetMessage Create(MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello: return new HelloMsg();
                case MsgType.HelloAck: return new HelloAckMsg();
                case MsgType.Reject: return new RejectMsg();
                case MsgType.Disconnect: return new DisconnectMsg();
                case MsgType.TimeSync: return new TimeSyncMsg();
                case MsgType.PlayerState: return new PlayerStateMsg();
                case MsgType.BoatState: return new BoatStateMsg();
                case MsgType.EnvState: return new EnvStateMsg();
                case MsgType.ControlState: return new ControlStateMsg();
                case MsgType.AnchorState: return new AnchorStateMsg();
                case MsgType.MooringState: return new MooringStateMsg();
                case MsgType.BoatDamageState: return new BoatDamageStateMsg();
                case MsgType.ControlRequest: return new ControlRequestMsg();
                case MsgType.ControlEvent: return new ControlEventMsg();
                case MsgType.SteerRequest: return new SteerRequestMsg();
                case MsgType.MooringRequest: return new MooringRequestMsg();
                case MsgType.HoldRequest: return new HoldRequestMsg();
                case MsgType.DamageRequest: return new DamageRequestMsg();
                case MsgType.PushRequest: return new PushRequestMsg();
                case MsgType.LightState: return new LightStateMsg();
                case MsgType.LightRequest: return new LightRequestMsg();
                case MsgType.ItemState: return new ItemStateMsg();
                case MsgType.ItemRequest: return new ItemRequestMsg();
                case MsgType.SpawnObject: return new SpawnObjectMsg();
                case MsgType.DespawnObject: return new DespawnObjectMsg();
                case MsgType.ItemExtra: return new ItemExtraStateMsg();
                case MsgType.WindRequest: return new WindRequestMsg();
                case MsgType.ShopRequest: return new ShopRequestMsg();
                case MsgType.ShopResult: return new ShopResultMsg();
                case MsgType.FishCatch: return new FishCatchMsg();
                case MsgType.CargoResult: return new CargoResultMsg();
                case MsgType.StormState: return new StormStateMsg();
                case MsgType.SleepState: return new SleepStateMsg();
                case MsgType.MissionJournal: return new MissionJournalMsg();
                case MsgType.MissionReward: return new MissionRewardMsg();
                case MsgType.MissionAccept: return new MissionAcceptMsg();
                case MsgType.MissionAbandon: return new MissionAbandonMsg();
                case MsgType.BoatPurchase: return new BoatPurchaseMsg();
                case MsgType.AvatarChange: return new AvatarChangeMsg();
                case MsgType.SaveSnapshotBegin: return new SaveSnapshotBeginMsg();
                case MsgType.SaveSnapshotChunk: return new SaveSnapshotChunkMsg();
                case MsgType.SaveSnapshotEnd: return new SaveSnapshotEndMsg();
                case MsgType.ClientWorldLoaded: return new ClientWorldLoadedMsg();
                case MsgType.RodState: return new RodStateMsg();
                // Stage 1+ message bodies are registered here as they land.
                default: return null;
            }
        }
    }

    /// <summary>Convenience send helpers over a LiteNetLib peer.</summary>
    public static class PeerExt
    {
        public static void Send(this NetPeer peer, INetMessage msg, DeliveryMethod method)
        {
            peer.Send(Protocol.Write(msg), method);
        }
    }
}
