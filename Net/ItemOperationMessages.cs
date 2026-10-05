using System;
using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    public sealed class CreatedItemState
    {
        public int LocalId;
        public ItemStateMsg State = new ItemStateMsg();
    }
    /// <summary>Absolute world results; personal needs and UI are never replayed remotely.</summary>
    public abstract class ItemOperationBody : INetMessage
    {
        public uint OperationId, Actor;
        public ItemStateMsg[] Changed = Array.Empty<ItemStateMsg>();
        public CreatedItemState[] Created = Array.Empty<CreatedItemState>();
        public int[] Consumed = Array.Empty<int>();
        public DamageRequestMsg[] Damage = Array.Empty<DamageRequestMsg>();
        public byte WorldEffects;
        public float TotemAttraction;
        public uint WeatherRevision;
        public long WorldTick;
        public BoatDamageStateMsg[] Hull = Array.Empty<BoatDamageStateMsg>();
        internal const byte WaitingTarget = 3;
        internal const byte Applied = 0, MissingTarget = 1, PartialFault = 2;
        public byte Status;
        public abstract MsgType Type { get; }
        public void Serialize(NetDataWriter w)
        {
            w.Put(OperationId); w.Put(Actor); w.Put(Status); w.Put(WorldEffects);
            w.Put(TotemAttraction); w.Put(WeatherRevision); w.Put(WorldTick);
            PutCount(w, Changed.Length); foreach (var state in Changed) state.Serialize(w);
            PutCount(w, Created.Length); foreach (var item in Created) { w.Put(item.LocalId); item.State.Serialize(w); }
            PutCount(w, Consumed.Length); foreach (int id in Consumed) w.Put(id);
            PutCount(w, Hull.Length); foreach (var state in Hull) state.Serialize(w);
            PutCount(w, Damage.Length); foreach (var damage in Damage) damage.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            OperationId = r.GetUInt(); Actor = r.GetUInt(); Status = r.GetByte(); WorldEffects = r.GetByte();
            TotemAttraction = r.GetFloat(); WeatherRevision = r.GetUInt(); WorldTick = r.GetLong();
            Changed = new ItemStateMsg[Count(r, 100)];
            for (int i = 0; i < Changed.Length; i++) { Changed[i] = new ItemStateMsg(); Changed[i].Deserialize(r); }
            Created = new CreatedItemState[Count(r, 104)];
            for (int i = 0; i < Created.Length; i++) { Created[i] = new CreatedItemState { LocalId = r.GetInt() }; Created[i].State.Deserialize(r); }
            Consumed = new int[Count(r, 4)]; for (int i = 0; i < Consumed.Length; i++) Consumed[i] = r.GetInt();
            Hull = new BoatDamageStateMsg[Count(r, 35)];
            for (int i = 0; i < Hull.Length; i++) { Hull[i] = new BoatDamageStateMsg(); Hull[i].Deserialize(r); }
            Damage = new DamageRequestMsg[Count(r, 11)];
            for (int i = 0; i < Damage.Length; i++) { Damage[i] = new DamageRequestMsg(); Damage[i].Deserialize(r); }
        }
        private static void PutCount(NetDataWriter w, int count)
        { if (count > ushort.MaxValue) throw new InvalidDataException("operation count exceeds wire format"); w.Put((ushort)count); }
        private static int Count(NetDataReader r, int minimumBytes)
        {
            int count = r.GetUShort();
            if (count > r.AvailableBytes / minimumBytes) throw new InvalidDataException("truncated operation array");
            return count;
        }
    }
    public sealed class ItemOperationRequestMsg : ItemOperationBody { public override MsgType Type => MsgType.ItemOperationRequest; }
    public sealed class ItemOperationResultMsg : ItemOperationBody { public override MsgType Type => MsgType.ItemOperationResult; }
}
