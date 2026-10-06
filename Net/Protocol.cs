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
        public const int Version = 83;

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
        /// unknown types — and for truncated/corrupt payloads, which would otherwise
        /// throw out of the receive callback and take down the whole frame's tick —
        /// so the caller can log and drop rather than crash.
        /// </summary>
        public static INetMessage ReadBody(MsgType type, NetDataReader r)
        {
            INetMessage msg = Create(type);
            if (msg == null) return null;
            try
            {
                msg.Deserialize(r);
                // LiteNetLib getters can read beyond the logical packet length when the
                // pooled backing array is larger; do not accept those stale buffer bytes.
                if (r.AvailableBytes < 0) throw new System.IO.InvalidDataException("Read past packet boundary");
            }
            catch (Exception e)
            {
                // Throttled: a corrupt or spoofed UDP stream on the port would otherwise write a line
                // per datagram. ShouldReport increments first, so nothing is built when suppressed.
#if PROTOCOL_SMOKE
                _malformedCount++;
                _ = e;
#else
                if (Plugin.Logger.ShouldReport(ref _malformedCount))
                    Plugin.Logger.LogWarning("[Protocol] Malformed " + type + " payload dropped " +
                                             "(occurrence #" + _malformedCount + "): " + e.Message);
#endif
                return null;
            }
            return msg;
        }

        private static int _malformedCount;

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
                case MsgType.AnchorRequest: return new AnchorRequestMsg();
                case MsgType.MooringState: return new MooringStateMsg();
                case MsgType.BoatDamageState: return new BoatDamageStateMsg();
                case MsgType.ControlRequest: return new ControlRequestMsg();
                case MsgType.ControlEvent: return new ControlEventMsg();
                case MsgType.HatchSnapshot: return new HatchSnapshotMsg();
                case MsgType.SteerRequest: return new SteerRequestMsg();
                case MsgType.MooringRequest: return new MooringRequestMsg();
                case MsgType.HoldRequest: return new HoldRequestMsg();
                case MsgType.DamageRequest: return new DamageRequestMsg();
                case MsgType.PushRequest: return new PushRequestMsg();
                case MsgType.LightState: return new LightStateMsg();
                case MsgType.LightRequest: return new LightRequestMsg();
                case MsgType.ItemOperationRequest: return new ItemOperationRequestMsg();
                case MsgType.ItemOperationResult: return new ItemOperationResultMsg();
                case MsgType.WheelLockRequest: return new WheelLockRequestMsg();
                case MsgType.MooringCarryRequest: return new MooringCarryRequestMsg();
                case MsgType.MooringCarryState: return new MooringCarryStateMsg();
                case MsgType.ChartRequest: return new ChartRequestMsg();
                case MsgType.ChartState: return new ChartStateMsg();
                case MsgType.DirtRequest: return new DirtRequestMsg();
                case MsgType.DirtState: return new DirtStateMsg();
                case MsgType.OrbRequest: return new OrbRequestMsg();
                case MsgType.OrbState: return new OrbStateMsg();
                case MsgType.InstrumentRequest: return new InstrumentRequestMsg();
                case MsgType.RefitRequest: return new RefitRequestMsg();
                case MsgType.RefitState: return new RefitStateMsg();
                case MsgType.InstrumentState: return new InstrumentStateMsg();
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
                case MsgType.SleepRequest: return new SleepRequestMsg();
                case MsgType.ResyncRequest: return new ResyncRequestMsg();
                case MsgType.HouseDoor: return new HouseDoorMsg();
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
                case MsgType.WavePhases: return new WavePhasesMsg();
                case MsgType.NpcBoatState: return new NpcBoatStateMsg();
                case MsgType.SessionRoster: return new SessionRosterMsg();
                case MsgType.GameplayNotice: return new GameplayNoticeMsg();
                case MsgType.MissionDeliver: return new MissionDeliverMsg();
                case MsgType.MissionDeliverResult: return new MissionDeliverResultMsg();
                case MsgType.ModManifest: return new ModManifestMsg();
                case MsgType.ModSyncResult: return new ModSyncResultMsg();
                case MsgType.ModFileRequest: return new ModFileRequestMsg();
                case MsgType.ModFileChunk: return new ModFileChunkMsg();
                case MsgType.ModFileEnd: return new ModFileEndMsg();
                // Stage 1+ message bodies are registered here as they land.
                default: return null;
            }
        }
    }

    /// <summary>Convenience send helpers over a LiteNetLib peer.</summary>
    public static class PeerExt
    {
        /// <summary>Diagnostics: (type, bytes) of an Unreliable packet that did not fit one datagram.</summary>
        public static System.Action<MsgType, int> Oversized;

        public static void Send(this NetPeer peer, INetMessage msg, DeliveryMethod method)
        {
            var writer = Protocol.Write(msg);
            // LiteNetLib throws on an Unreliable payload above the peer MTU and only fragments reliable
            // channels. A snapshot that outgrew one datagram (a large hull's ControlState) is still
            // delivered, in order, instead of failing every send.
            if (method == DeliveryMethod.Unreliable && writer.Length > peer.GetMaxSinglePacketSize(method))
            {
                method = DeliveryMethod.ReliableOrdered;
                try { Oversized?.Invoke(msg.Type, writer.Length); } catch { }
            }
            peer.Send(writer, method);
        }
    }
}
