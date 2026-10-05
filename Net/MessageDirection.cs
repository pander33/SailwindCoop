namespace SailwindCoop.Net
{
    public enum MsgFlow : byte { None, ToHost, ToClient, Both }

    /// <summary>
    /// Central post-handshake sender/direction gate. A packet is checked here before its body is
    /// parsed, so a peer that never completed Hello cannot reach a gameplay handler (where an
    /// unauthenticated sender resolves to NetId 0), and a wrong-direction type is dropped once
    /// instead of relying on every handler's own role check. This is a transport check only: it
    /// never looks at the payload and adds no gameplay eligibility.
    /// </summary>
    public static class MessageDirection
    {
        public static MsgFlow Of(MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello:
                case MsgType.ControlRequest:
                case MsgType.SteerRequest:
                case MsgType.MooringRequest:
                case MsgType.HoldRequest:
                case MsgType.DamageRequest:
                case MsgType.PushRequest:
                case MsgType.LightRequest:
                case MsgType.WindRequest:
                case MsgType.ShopRequest:
                case MsgType.FishCatch:
                case MsgType.MissionAccept:
                case MsgType.MissionAbandon:
                case MsgType.MissionDeliver:
                case MsgType.ClientWorldLoaded:
                case MsgType.ItemOperationRequest:
                case MsgType.WheelLockRequest:
                case MsgType.MooringCarryRequest:
                case MsgType.ChartRequest:
                case MsgType.DirtRequest:
                case MsgType.OrbRequest:
                case MsgType.InstrumentRequest:
                case MsgType.RefitRequest:
                case MsgType.SleepRequest:
                case MsgType.ResyncRequest:
                case MsgType.AnchorRequest:
                    return MsgFlow.ToHost;

                case MsgType.HelloAck:
                case MsgType.Reject:
                case MsgType.SpawnObject:
                case MsgType.DespawnObject:
                case MsgType.BoatState:
                case MsgType.EnvState:
                case MsgType.ControlState:
                case MsgType.AnchorState:
                case MsgType.MooringState:
                case MsgType.BoatDamageState:
                case MsgType.LightState:
                case MsgType.ItemState:
                case MsgType.ItemExtra:
                case MsgType.ShopResult:
                case MsgType.CargoResult:
                case MsgType.StormState:
                case MsgType.SleepState:
                case MsgType.MissionJournal:
                case MsgType.MissionReward:
                case MsgType.MissionDeliverResult:
                case MsgType.SaveSnapshotBegin:
                case MsgType.SaveSnapshotChunk:
                case MsgType.SaveSnapshotEnd:
                case MsgType.WavePhases:
                case MsgType.NpcBoatState:
                case MsgType.SessionRoster:
                case MsgType.GameplayNotice:
                case MsgType.HatchSnapshot:
                case MsgType.ItemOperationResult:
                case MsgType.MooringCarryState:
                case MsgType.ChartState:
                case MsgType.DirtState:
                case MsgType.OrbState:
                case MsgType.InstrumentState:
                case MsgType.RefitState:
                    return MsgFlow.ToClient;

                // Relayed by the host to the other clients, or genuinely sent by either side.
                case MsgType.Disconnect:
                case MsgType.TimeSync:
                case MsgType.PlayerState:
                case MsgType.ControlEvent:
                case MsgType.ItemRequest:
                case MsgType.BoatPurchase:
                case MsgType.AvatarChange:
                case MsgType.RodState:
                    return MsgFlow.Both;

                default:
                    return MsgFlow.None;
            }
        }

        /// <param name="receiverIsHost">Role of the machine that received the packet.</param>
        /// <param name="senderHandshaked">Host: the peer finished Hello. Client: the packet came
        /// from the host peer and HelloAck has already been applied.</param>
        public static bool Accept(MsgType type, bool receiverIsHost, bool senderHandshaked)
        {
            MsgFlow flow = Of(type);
            switch (type)
            {
                // A second Hello would allocate a second NetId for the same peer.
                case MsgType.Hello: return receiverIsHost && !senderHandshaked;
                case MsgType.HelloAck:
                case MsgType.Reject: return !receiverIsHost;
                case MsgType.Disconnect: return true;
            }
            if (!senderHandshaked || flow == MsgFlow.None) return false;
            return flow == MsgFlow.Both || flow == (receiverIsHost ? MsgFlow.ToHost : MsgFlow.ToClient);
        }
    }
}
