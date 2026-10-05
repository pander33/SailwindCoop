using System;
using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    public sealed class ChartMark
    {
        public ulong Id;
        public uint Order;
        public bool Point;
        public float X, Y, EndX, EndY;
        public int Color;
        public void Serialize(NetDataWriter w) { w.Put(Id); w.Put(Order); w.Put(Point); w.Put(X); w.Put(Y); w.Put(EndX); w.Put(EndY); w.Put(Color); }
        public void Deserialize(NetDataReader r) { Id = r.GetULong(); Order = r.GetUInt(); Point = r.GetBool(); X = r.GetFloat(); Y = r.GetFloat(); EndX = r.GetFloat(); EndY = r.GetFloat(); Color = r.GetInt(); }
    }
    public enum ChartAction : byte { Baseline, Upsert, Remove }
    public sealed class ChartRequestMsg : INetMessage
    {
        public int InstanceId, PrefabIndex;
        public uint RequestId;
        public ChartAction Action;
        public ChartMark Mark = new ChartMark();
        public MsgType Type => MsgType.ChartRequest;
        public void Serialize(NetDataWriter w) { w.Put(InstanceId); w.Put(PrefabIndex); w.Put(RequestId); w.Put((byte)Action); Mark.Serialize(w); }
        public void Deserialize(NetDataReader r) { InstanceId = r.GetInt(); PrefabIndex = r.GetInt(); RequestId = r.GetUInt(); Action = (ChartAction)r.GetByte(); Mark.Deserialize(r); }
    }
    /// <summary>Only a complete revision replaces committed marks; local preview is separate.</summary>
    public sealed class ChartStateMsg : INetMessage
    {
        public int InstanceId, PrefabIndex;
        public uint Revision, Requester, RequestId;
        public ushort Chunk, Chunks = 1;
        public bool Missing;
        public ChartMark[] Marks = Array.Empty<ChartMark>();
        public MsgType Type => MsgType.ChartState;
        public void Serialize(NetDataWriter w)
        {
            w.Put(InstanceId); w.Put(PrefabIndex); w.Put(Revision); w.Put(Requester); w.Put(RequestId); w.Put(Chunk); w.Put(Chunks); w.Put(Missing);
            if (Marks.Length > ushort.MaxValue) throw new InvalidDataException("chart chunk too large");
            w.Put((ushort)Marks.Length); foreach (var mark in Marks) mark.Serialize(w);
        }
        public void Deserialize(NetDataReader r)
        {
            InstanceId = r.GetInt(); PrefabIndex = r.GetInt(); Revision = r.GetUInt(); Requester = r.GetUInt(); RequestId = r.GetUInt();
            Chunk = r.GetUShort(); Chunks = r.GetUShort(); Missing = r.GetBool();
            if (Chunks == 0 || Chunk >= Chunks) throw new InvalidDataException("invalid chart chunk header");
            int count = r.GetUShort(); if (count > r.AvailableBytes / 33) throw new InvalidDataException("truncated chart marks");
            Marks = new ChartMark[count]; for (int i = 0; i < count; i++) { Marks[i] = new ChartMark(); Marks[i].Deserialize(r); }
        }
    }
}
