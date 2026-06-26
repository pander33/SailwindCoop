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
        public const int Version = 7;

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
                case MsgType.ControlRequest: return new ControlRequestMsg();
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
