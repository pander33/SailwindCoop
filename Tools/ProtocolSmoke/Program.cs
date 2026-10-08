using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;
using SailwindCoop.Net;

namespace ProtocolSmoke
{
    internal static class Program
    {
        private static int Main()
        {
            var failures = new List<string>();
            var messageTypes = typeof(INetMessage).Assembly.GetTypes()
                .Where(t => typeof(INetMessage).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => t.Name)
                .ToList();

            foreach (var type in messageTypes)
            {
                try
                {
                    var msg = (INetMessage)Activator.CreateInstance(type);
                    var writer = Protocol.Write(msg);
                    var reader = new NetDataReader(writer.Data, 0, writer.Length);
                    var wireType = (MsgType)reader.GetByte();
                    if (wireType != msg.Type)
                    {
                        failures.Add(type.Name + ": wrote " + wireType + ", expected " + msg.Type);
                        continue;
                    }

                    var clone = Protocol.ReadBody(wireType, reader);
                    if (clone == null)
                    {
                        failures.Add(type.Name + ": Protocol.ReadBody returned null for " + wireType);
                        continue;
                    }
                    if (clone.GetType() != type)
                        failures.Add(type.Name + ": decoded as " + clone.GetType().Name);
                }
                catch (Exception e)
                {
                    failures.Add(type.Name + ": " + e.GetType().Name + " " + e.Message);
                }
            }

            foreach (MsgType type in Enum.GetValues(typeof(MsgType)))
            {
                if (type == MsgType.InteractRequest)
                    continue; // reserved slot; intentionally no message body yet
                if (!messageTypes.Any(t => ((INetMessage)Activator.CreateInstance(t)).Type == type))
                    failures.Add("MsgType " + type + " has no INetMessage implementation");
            }

            TestSleepOrdering(failures);
            TestDirection(failures);
            TestOversizedCounts(failures);

            int populated = 0, truncated = 0;
            CheckPopulatedMessage(new HelloMsg { ProtocolVersion = Protocol.Version, PlayerGuid = Guid.NewGuid().ToString("N"), PlayerName = "Dice guest", SelectedAvatar = "avatar.bundle" }, failures, ref populated, ref truncated);
            CheckPopulatedMessage(new DiceRequestMsg { Action = DiceAction.Place, TableId = 38, Epoch = 14, RequestId = 77, Revision = 90, TurnId = 2, BoatIndex = 511, Stake = 15, MatchId = Guid.NewGuid().ToString("N"), Mask = 5, Flag = true, X = 2.5f, Y = 1.3f, Z = -4, Yaw = 120 }, failures, ref populated, ref truncated);
            CheckPopulatedMessage(new DiceStateMsg { TableId = 38, Epoch = 14, Revision = 99, TurnId = 3, RollId = 4, ClockRevision = 7, OwnerNetId = 3, Stake = 25, StakeCurrency = 2, StakesAllowed = true, BoatIndex = 511, MatchId = Guid.NewGuid().ToString("N"), ClockEpoch = "clock", Phase = 4, ResumePhase = 3, Faces = new byte[] { 4, 4, 2 }, HeldMask = 3, RollHeldMask = 3, Running = true, AnchorTick = 1000, ElapsedMs = 700, PresentationAtMs = 500, Players = Enumerable.Range(0, 5).Select(i => new DicePlayerData { Identity = Guid.NewGuid().ToString("N"), Name = "Player " + i, NetId = (uint) i + 1, Connected = true, Ready = true, Scores = new[] { 14, 21, 30 }, Scored = new[] { true, true, true } }).ToArray() }, failures, ref populated, ref truncated);
            CheckPopulatedMessage(new DiceResultMsg { RequestId = 7, Revision = 90, Result = DiceResultKind.ResyncNeeded, Detail = "changed" }, failures, ref populated, ref truncated);
            CheckPopulatedMessage(new DiceBaselineMsg { Revision = 100, Tables = new uint[] { 3, 4, 5 } }, failures, ref populated, ref truncated);
            CheckPopulatedMessage(new DiceJournalMsg { Text = "Player 1: 42 points" }, failures, ref populated, ref truncated);
            foreach (var type in messageTypes.Where(t => t.GetField("LayoutHash") != null || t == typeof(SpawnObjectMsg) ||
                t == typeof(FishCatchMsg) || t == typeof(ItemRequestMsg) || t == typeof(ItemStateMsg) || t == typeof(StormStateMsg) || t == typeof(ChartRequestMsg) || t == typeof(ChartStateMsg) || t == typeof(DirtRequestMsg) || t == typeof(DirtStateMsg) || t == typeof(OrbRequestMsg) || t == typeof(OrbStateMsg) || t == typeof(WindRequestMsg) || t == typeof(InstrumentRequestMsg) || t == typeof(InstrumentStateMsg) || typeof(ItemOperationBody).IsAssignableFrom(t) || typeof(RefitBody).IsAssignableFrom(t)))
            {
                foreach (ushort boat in new ushort[] { 1, 511, 65534 })
                {
                    try
                    {
                        var msg = (INetMessage)Activator.CreateInstance(type);
                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                            field.SetValue(msg, Sample(field.FieldType));
                        type.GetField("BoatIndex")?.SetValue(msg, boat);
                        type.GetField("LayoutHash")?.SetValue(msg, 0xFEDCBA98u);
                        if (msg is MooringStateMsg mooring)
                        {
                            mooring.StateAvailable = boat != 511;
                            mooring.IsInteraction = boat != 511;
                        }
                        if (msg is HatchSnapshotMsg hatch) hatch.Open = boat != 511;
                        if (msg is SpawnObjectMsg spawn) { spawn.IsSnapshot = boat != 511; spawn.IsBaselineItem = boat == 511; }
                        if (msg is AnchorRequestMsg anchorRequest) { anchorRequest.Held = boat != 511; anchorRequest.Frame = boat == 511 ? CoordFrame.World : CoordFrame.Boat; }
                        if (msg is AnchorStateMsg anchorState) anchorState.Set = boat != 511;
                        if (msg is ItemStateMsg itemState) itemState.Amount = boat == 511 ? 0f : 1f;
                        if (msg is ItemRequestMsg itemRequest) { itemRequest.Action = ItemAction.State; itemRequest.Amount = boat == 511 ? 0f : 1f; }
                        if (msg is DirtStateMsg dirt) { dirt.Chunk = 1; dirt.Chunks = 2; }
                        if (msg is ChartStateMsg chart) { chart.Chunk = 1; chart.Chunks = 2; }
                        var writer = Protocol.Write(msg);
                        var reader = new NetDataReader(writer.Data, 1, writer.Length);
                        var clone = Protocol.ReadBody(msg.Type, reader);
                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                            if (!Equal(field.GetValue(msg), field.GetValue(clone)))
                                throw new Exception("round-trip mismatch: " + field.Name);
                        if (reader.AvailableBytes != 0) throw new Exception("unconsumed payload");
                        populated++;
                        for (int length = 0; length < writer.Length - 1; length++)
                        {
                            byte[] cut = new byte[length];
                            Buffer.BlockCopy(writer.Data, 1, cut, 0, length);
                            if (Protocol.ReadBody(msg.Type, new NetDataReader(cut)) != null)
                                throw new Exception("accepted truncated body length " + length);
                            truncated++;
                            if (Protocol.ReadBody(msg.Type, new NetDataReader(writer.Data, 1, length + 1)) != null)
                                throw new Exception("accepted truncated pooled body length " + length);
                            truncated++;
                        }
                    }
                    catch (Exception e) { failures.Add(type.Name + " boat=" + boat + ": " + e.Message); }
                }
            }

            foreach (INetMessage msg in new INetMessage[] {
                new ItemOperationResultMsg { Actor = 10, OperationId = 41, Status = ItemOperationBody.WaitingTarget },
                new ItemOperationResultMsg { Actor = 10, OperationId = 41, Status = ItemOperationBody.PartialFault,
                    WorldEffects = 1, Consumed = new[] { 901 } },
                new SpawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = 902, PrefabIndex = 12,
                    AuthorRequester = 10, AuthorRequestId = 42, Revision = 1, Tick = 200 },
                new SpawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = 901, PrefabIndex = 12,
                    AuthorRequester = 10, AuthorRequestId = 41, Revision = 1, Tick = 201 },
                new SleepStateMsg { Revision = 23, CycleActor = 0x12345678, CycleId = 91, Requester = 0x87654321,
                    RequestId = 92, Phase = SleepPhase.Sleeping, Reply = SleepReply.Applied,
                    Address = new SleepAddress { Source = SleepSource.Tavern, Path = "island/tavern[2]", Timeskip = true },
                    EntranceCommitted = true, Entrance = new SleepAddress { Source = SleepSource.Onsen, Path = "island/onsen[1]" } },
                new SleepRequestMsg { RequestId = 104, CycleActor = 0x12345678, CycleId = 91, Phase = SleepPhase.Sleeping,
                    Address = new SleepAddress { Source = SleepSource.ItemBed, InstanceId = 55, PrefabIndex = 12, Timeskip = true } },
                new SleepPresenceMsg { Flags = SleepPresenceMsg.InBed | SleepPresenceMsg.Wants | SleepPresenceMsg.Timeskip },
                new ResyncRequestMsg { BoatIndex = 7, Domain = ResyncDomain.Damage },
                new HouseDoorMsg { Door = 0x2A03, Open = true },
                new MoneyTransferMsg { FromNetId = 0x12345678, ToNetId = 0x87654321, Currency = 3, Amount = 1250, Returned = true },
                new WalletStateMsg { Shared = true, AckSeq = 0x01020304, Currency = new[] { 120, 0, -5, 99999 } },
                new WalletDeltaMsg { Seq = 0x0A0B0C0D, Delta = new[] { -30, 0, 0, 12 } },
                new ShopTakenMsg { PrefabIndex = 137, Pos = new UnityEngine.Vector3(218.3f, 5.7f, -356f) },
                new MoneyOfferMsg { FromNetId = 0x12345678, ToNetId = 0x87654321, Kind = MoneyOfferKind.Take, Currency = 2, Amount = 75 },
                new ModManifestMsg { FilesOffered = true, Mods = new[] {
                    new ModEntry { Folder = "SeaLifeMod", Complete = true,
                        Plugins = new[] { new ModPlugin { Guid = "com.example.sealife", Name = "Sea Life", Version = "1.2.0" } },
                        Files = new[] { new ModListFile { Path = "SeaLifeMod.dll", Size = 14336, Hash = SampleHash(1) },
                                        new ModListFile { Path = "data/assets", Size = 8376605, Hash = SampleHash(2) } } },
                    new ModEntry { Folder = "", Complete = false,
                        Plugins = new[] { new ModPlugin { Guid = "a", Name = "A", Version = "0.1" }, new ModPlugin { Guid = "b", Name = "B", Version = "2" } },
                        Files = new[] { new ModListFile { Path = "Lone.dll", Size = 1, Hash = SampleHash(3) } } } } },
                new ModSyncResultMsg { Decision = ModSyncDecision.Abort, Missing = 3, Different = 1 },
                new ModFileRequestMsg { Mod = 2, File = 511 },
                new ModFileChunkMsg { Mod = 2, File = 511, Index = 77, Data = new byte[] { 1, 2, 3, 250 } },
                new ModFileEndMsg { Mod = 2, File = 511, Ok = true }
            })
                CheckPopulatedMessage(msg, failures, ref populated, ref truncated);

            if (failures.Count == 0)
            {
                Console.WriteLine("Protocol smoke OK: " + messageTypes.Count + " message types, " + populated +
                    " populated round-trips, " + truncated + " truncated packets rejected, protocol " + Protocol.Version);
                return 0;
            }

            Console.Error.WriteLine("Protocol smoke FAILED:");
            foreach (var failure in failures)
                Console.Error.WriteLine(" - " + failure);
            return 1;
        }

        private static byte[] SampleHash(byte seed)
        {
            var hash = new byte[ModLimits.HashBytes];
            for (int i = 0; i < hash.Length; i++) hash[i] = (byte)(seed * 31 + i);
            return hash;
        }

        private static object Sample(Type type)
        {
            if (type == typeof(byte)) return (byte)193;
            if (type == typeof(int)) return 123456;
            if (type == typeof(ushort)) return (ushort)65534;
            if (type == typeof(uint)) return 0xFEDCBA98u;
            if (type == typeof(ulong)) return 0xFEDCBA9812345678ul;
            if (type == typeof(long)) return 9876543210L;
            if (type == typeof(float)) return 0.375f;
            if (type == typeof(bool)) return true;
            if (type == typeof(string)) return "Island/dock[1]/bollard[2]";
            if (type == typeof(UnityEngine.Vector3)) return new UnityEngine.Vector3(12.5f, -8.25f, 100.75f);
            if (type == typeof(UnityEngine.Quaternion)) return new UnityEngine.Quaternion(0f, 0.6f, 0f, 0.8f);
            if (type.IsEnum) { var values = Enum.GetValues(type); return values.GetValue(values.Length - 1); }
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType(), 2);
                for (int i = 0; i < 2; i++) array.SetValue(Sample(type.GetElementType()), i);
                return array;
            }
            if (type == typeof(ItemDetails) || type == typeof(CreatedItemState) || type == typeof(ControlEpoch) || type == typeof(ChartMark) || type == typeof(NetSailConfiguration) || typeof(INetMessage).IsAssignableFrom(type))
            {
                var value = Activator.CreateInstance(type);
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(value, Sample(field.FieldType));
                return value;
            }
            throw new Exception("no sample for " + type.Name);
        }

        private static void TestDirection(List<string> failures)
        {
            foreach (MsgType type in Enum.GetValues(typeof(MsgType)))
            {
                MsgFlow flow = MessageDirection.Of(type);
                if (type == MsgType.InteractRequest)
                {
                    if (flow != MsgFlow.None) failures.Add("MessageDirection: reserved InteractRequest must stay closed");
                    continue;
                }
                if (flow == MsgFlow.None) { failures.Add("MessageDirection: " + type + " has no direction"); continue; }
                // Nothing but the handshake itself may be read from a peer that has not finished it.
                bool session = type == MsgType.Hello || type == MsgType.HelloAck || type == MsgType.Reject || type == MsgType.Disconnect;
                if (!session && (MessageDirection.Accept(type, true, false) || MessageDirection.Accept(type, false, false)))
                    failures.Add("MessageDirection: " + type + " accepted before handshake");
                if (!session && flow == MsgFlow.ToHost && (!MessageDirection.Accept(type, true, true) || MessageDirection.Accept(type, false, true)))
                    failures.Add("MessageDirection: " + type + " must reach only the host");
                if (!session && flow == MsgFlow.ToClient && (MessageDirection.Accept(type, true, true) || !MessageDirection.Accept(type, false, true)))
                    failures.Add("MessageDirection: " + type + " must reach only a client");
                if (!session && flow == MsgFlow.Both && (!MessageDirection.Accept(type, true, true) || !MessageDirection.Accept(type, false, true)))
                    failures.Add("MessageDirection: " + type + " must reach both roles");
            }
            if (!MessageDirection.Accept(MsgType.Hello, true, false) || MessageDirection.Accept(MsgType.Hello, true, true) ||
                MessageDirection.Accept(MsgType.Hello, false, false))
                failures.Add("MessageDirection: Hello is a single host-side handshake step");
            if (MessageDirection.Accept(MsgType.HelloAck, true, true) || !MessageDirection.Accept(MsgType.HelloAck, false, false) ||
                MessageDirection.Accept(MsgType.Reject, true, false) || !MessageDirection.Accept(MsgType.Reject, false, false))
                failures.Add("MessageDirection: HelloAck/Reject are host replies");
            if (MessageDirection.Accept((MsgType)250, true, true) || MessageDirection.Accept((MsgType)250, false, true))
                failures.Add("MessageDirection: unknown type accepted");
        }

        /// <summary>A count field larger than the remaining payload must be refused before the
        /// array is allocated, not after reading stale pooled bytes.</summary>
        private static void TestOversizedCounts(List<string> failures)
        {
            var control = new NetDataWriter();
            control.Put((ushort)1); control.Put(0u); control.Put(0u); control.Put(0L); control.Put(false);
            control.Put(ushort.MaxValue);
            if (Protocol.ReadBody(MsgType.ControlState, new NetDataReader(control.Data, 0, control.Length)) != null)
                failures.Add("ControlStateMsg: oversized wheel count accepted");
            var waves = new NetDataWriter();
            waves.Put((byte)1); waves.Put(ushort.MaxValue);
            if (Protocol.ReadBody(MsgType.WavePhases, new NetDataReader(waves.Data, 0, waves.Length)) != null)
                failures.Add("WavePhasesMsg: oversized phase count accepted");
            var mods = new NetDataWriter();
            mods.Put(true); mods.Put((byte)(ModLimits.MaxMods + 1));
            if (Protocol.ReadBody(MsgType.ModManifest, new NetDataReader(mods.Data, 0, mods.Length)) != null)
                failures.Add("ModManifestMsg: oversized mod count accepted");
            var files = new NetDataWriter();
            files.Put(true); files.Put((byte)1); files.Put("Mod"); files.Put(true); files.Put((byte)0); files.Put((ushort)ModLimits.MaxFilesPerMod);
            if (Protocol.ReadBody(MsgType.ModManifest, new NetDataReader(files.Data, 0, files.Length)) != null)
                failures.Add("ModManifestMsg: file count beyond the payload accepted");
            var chunk = new NetDataWriter();
            chunk.Put((ushort)0); chunk.Put((ushort)0); chunk.Put(0); chunk.Put(ushort.MaxValue);
            if (Protocol.ReadBody(MsgType.ModFileChunk, new NetDataReader(chunk.Data, 0, chunk.Length)) != null)
                failures.Add("ModFileChunkMsg: oversized chunk accepted");
        }

        private static void TestSleepOrdering(List<string> failures)
        {
            var order = new SailwindCoop.Sync.SleepTransitionState();
            if (!order.AcceptRequest(10, uint.MaxValue) || !order.AcceptRequest(10, 1) || order.AcceptRequest(10, uint.MaxValue))
                failures.Add("SleepTransitionState: request wrap/duplicate order");
            if (!order.AcceptRequest(10, 2) || !order.Transition(10, 2, SleepPhase.Begin, 0, 0))
                failures.Add("SleepTransitionState: begin request");
            uint revision = order.Revision;
            if (order.AcceptRequest(10, 2) && order.Transition(10, 2, SleepPhase.Begin, 0, 0) || order.Revision != revision)
                failures.Add("SleepTransitionState: duplicate begin");
            if (!order.AcceptRequest(10, 3) || !order.Transition(10, 3, SleepPhase.Sleeping, 10, 2) ||
                order.Transition(10, 3, SleepPhase.Wake, 11, 2)) failures.Add("SleepTransitionState: competing/stale cycle");
            if (!order.ShouldPreservePresentationForRejectedAck(true, SleepReply.ObsoleteCycle, 1, 11) ||
                order.ShouldPreservePresentationForRejectedAck(false, SleepReply.ObsoleteCycle, 1, 11) ||
                order.ShouldPreservePresentationForRejectedAck(true, SleepReply.Applied, 1, 11) ||
                order.ShouldPreservePresentationForRejectedAck(true, SleepReply.ObsoleteCycle, 0, 11) ||
                order.ShouldPreservePresentationForRejectedAck(true, SleepReply.ObsoleteCycle, 1, 10))
                failures.Add("SleepTransitionState: rejected local intent must preserve foreign sleeping presentation");
            order.BeginPending(8); order.BeginEntrance(9);
            if (order.ReceiveEntrance(10, 9, 11) || !order.ReceiveEntrance(11, 9, 11) || order.Pending != 8 ||
                order.ReceiveEntrance(11, 9, 11)) failures.Add("SleepTransitionState: entrance acknowledgement correlation");
            if (!order.Receive(uint.MaxValue, 11, 8, 11, out var ack) || !ack || order.Pending != 0 ||
                !order.Receive(1, 0, 0, 11, out ack) || ack || order.Receive(1, 0, 0, 11, out ack))
                failures.Add("SleepTransitionState: revision wrap/ack ordering");
            order.BeginPending(10); order.Expire(11, 2);
            if (order.Expired) failures.Add("SleepTransitionState: wrong actor expiry");
            order.Expire(10, 2);
            if (!order.Expired || order.Pending != 0) failures.Add("SleepTransitionState: expiry");
            order.BeginEntrance(12);
            if (!order.ExpireEntrance(12) || order.PendingEntrance != 0 || order.ExpireEntrance(12))
                failures.Add("SleepTransitionState: entrance expiry");
            order.Clear();
            if (order.Active || order.Pending != 0 || order.PendingEntrance != 0 || order.Expired || order.Revision != 0)
                failures.Add("SleepTransitionState: reset");
        }

        private static void CheckPopulatedMessage(INetMessage msg, List<string> failures, ref int populated, ref int truncated)
        {
            try
            {
                var writer = Protocol.Write(msg);
                var reader = new NetDataReader(writer.Data, 1, writer.Length);
                var clone = Protocol.ReadBody(msg.Type, reader);
                foreach (var field in msg.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (!Equal(field.GetValue(msg), field.GetValue(clone))) throw new Exception("round-trip mismatch: " + field.Name);
                if (reader.AvailableBytes != 0) throw new Exception("unconsumed payload");
                populated++;
                for (int length = 0; length < writer.Length - 1; length++)
                {
					byte[] cut = new byte[length];
					Buffer.BlockCopy(writer.Data, 1, cut, 0, length);
					if (Protocol.ReadBody(msg.Type, new NetDataReader(cut)) != null)
						throw new Exception("accepted truncated copied payload " + length);
					truncated++;
                    if (Protocol.ReadBody(msg.Type, new NetDataReader(writer.Data, 1, length + 1)) != null)
                        throw new Exception("accepted truncated pooled payload " + length);
                    truncated++;
                }
            }
            catch (Exception e) { failures.Add(msg.GetType().Name + " populated: " + e.Message); }
        }

        private static bool Equal(object a, object b)
        {
            if (a is Array aa && b is Array bb)
            {
                if (aa.Length != bb.Length) return false;
                for (int i = 0; i < aa.Length; i++) if (!Equal(aa.GetValue(i), bb.GetValue(i))) return false;
                return true;
            }
            if (a != null && b != null && a.GetType() == b.GetType() &&
                (a is ItemDetails || a is CreatedItemState || a is ControlEpoch || a is ChartMark || a is NetSailConfiguration || a is SleepAddress || a is INetMessage ||
                  a is ModEntry || a is ModPlugin || a is ModListFile || a is DicePlayerData))
                return a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).All(f => Equal(f.GetValue(a), f.GetValue(b)));
            return object.Equals(a, b);
        }
    }
}
