using System;
using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    public sealed class NetSailConfiguration
    {
        public int Prefab, Mast, Color;
        public float Height, MinAngle, MaxAngle, Health, ScaleY, ScaleZ;
        internal void Write(NetDataWriter w)
        { w.Put(Prefab); w.Put(Mast); w.Put(Color); w.Put(Height); w.Put(MinAngle); w.Put(MaxAngle); w.Put(Health); w.Put(ScaleY); w.Put(ScaleZ); }
        internal void Read(NetDataReader r)
        { Prefab = r.GetInt(); Mast = r.GetInt(); Color = r.GetInt(); Height = r.GetFloat(); MinAngle = r.GetFloat(); MaxAngle = r.GetFloat(); Health = r.GetFloat(); ScaleY = r.GetFloat(); ScaleZ = r.GetFloat(); }
    }
    public enum RefitOutcome : byte { Applied, MissingObject, PartialFault }
    public abstract class RefitBody : INetMessage
    {
        public ushort BoatIndex = ushort.MaxValue;
        public uint Generation, Requester, RequestId;
        public bool Baseline, Repair, Clean;
        public RefitOutcome Outcome;
        public float HullDamage, WaterLevel;
        public int[] Parts = Array.Empty<int>();
        public NetSailConfiguration[] Sails = Array.Empty<NetSailConfiguration>();
        public byte[] Dirt = Array.Empty<byte>();
        public abstract MsgType Type { get; }
        public void Serialize(NetDataWriter w)
        {
            w.Put(BoatIndex); w.Put(Generation); w.Put(Requester); w.Put(RequestId);
            w.Put(Baseline); w.Put(Repair); w.Put(Clean); w.Put((byte)Outcome); w.Put(HullDamage); w.Put(WaterLevel);
            if (Parts.Length > ushort.MaxValue || Sails.Length > ushort.MaxValue) throw new InvalidDataException("refit array exceeds wire count");
            w.Put((ushort)Parts.Length); foreach (int part in Parts) w.Put(part);
            w.Put((ushort)Sails.Length); foreach (var sail in Sails) sail.Write(w);
            w.Put(Dirt.Length); w.Put(Dirt);
        }
        public void Deserialize(NetDataReader r)
        {
            BoatIndex = r.GetUShort(); Generation = r.GetUInt(); Requester = r.GetUInt(); RequestId = r.GetUInt();
            Baseline = r.GetBool(); Repair = r.GetBool(); Clean = r.GetBool(); Outcome = (RefitOutcome)r.GetByte(); HullDamage = r.GetFloat(); WaterLevel = r.GetFloat();
            int count = r.GetUShort(); if (count > r.AvailableBytes / 4) throw new InvalidDataException("truncated refit parts");
            Parts = new int[count]; for (int i = 0; i < count; i++) Parts[i] = r.GetInt();
            count = r.GetUShort(); if (count > r.AvailableBytes / 36) throw new InvalidDataException("truncated refit sails");
            Sails = new NetSailConfiguration[count]; for (int i = 0; i < count; i++) { Sails[i] = new NetSailConfiguration(); Sails[i].Read(r); }
            count = r.GetInt(); if (count < 0 || count > r.AvailableBytes) throw new InvalidDataException("truncated refit dirt");
            Dirt = new byte[count]; r.GetBytes(Dirt, count);
        }
    }
    public sealed class RefitRequestMsg : RefitBody { public override MsgType Type => MsgType.RefitRequest; }
    public sealed class RefitStateMsg : RefitBody { public override MsgType Type => MsgType.RefitState; }
}
