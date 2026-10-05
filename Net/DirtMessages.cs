using System;
using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    public enum DirtAction : byte { Baseline, Stroke, CleanFully }
    public enum DirtOutcome : byte { Applied, MissingObject, Fault }
    public sealed class DirtRequestMsg : INetMessage
    {
        public int SceneIndex;
        public uint RequestId;
        public DirtAction Action;
        public float U, V;
        public bool ColorPass;
        public MsgType Type => MsgType.DirtRequest;
        public void Serialize(NetDataWriter w) { w.Put(SceneIndex); w.Put(RequestId); w.Put((byte)Action); w.Put(U); w.Put(V); w.Put(ColorPass); }
        public void Deserialize(NetDataReader r) { SceneIndex = r.GetInt(); RequestId = r.GetUInt(); Action = (DirtAction)r.GetByte(); U = r.GetFloat(); V = r.GetFloat(); ColorPass = r.GetBool(); }
    }
    public sealed class DirtStateMsg : INetMessage
    {
        public int SceneIndex;
        public uint Revision, Requester, RequestId;
        public ushort Chunk, Chunks = 1;
        public DirtOutcome Outcome;
        public byte[] Png = Array.Empty<byte>();
        public MsgType Type => MsgType.DirtState;
        public void Serialize(NetDataWriter w)
        {
            w.Put(SceneIndex); w.Put(Revision); w.Put(Requester); w.Put(RequestId); w.Put(Chunk); w.Put(Chunks); w.Put((byte)Outcome);
            if (Png.Length > ushort.MaxValue) throw new InvalidDataException("dirt chunk too large");
            w.Put((ushort)Png.Length); w.Put(Png);
        }
        public void Deserialize(NetDataReader r)
        {
            SceneIndex = r.GetInt(); Revision = r.GetUInt(); Requester = r.GetUInt(); RequestId = r.GetUInt();
            Chunk = r.GetUShort(); Chunks = r.GetUShort(); Outcome = (DirtOutcome)r.GetByte();
            if (Chunks == 0 || Chunk >= Chunks) throw new InvalidDataException("invalid dirt chunk header");
            int size = r.GetUShort(); if (size > r.AvailableBytes) throw new InvalidDataException("truncated dirt texture");
            Png = new byte[size]; r.GetBytes(Png, size);
        }
    }
}
