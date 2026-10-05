using LiteNetLib.Utils;
using UnityEngine;

namespace SailwindCoop.Net
{
    public enum OrbAction : byte { Baseline, Pickup, Pose, Drop }
    public abstract class OrbBody
    {
        public string OrbId = "", ParentId = "";
        public uint RequestId;
        public long Tick;
        public bool Held;
        public Vector3 RealPos, ParentReal;
        public Quaternion Rot = Quaternion.identity, ParentRot = Quaternion.identity;
        protected void Write(NetDataWriter w)
        { w.Put(OrbId); w.Put(ParentId); w.Put(RequestId); w.Put(Tick); w.Put(Held); w.PutVector3(RealPos); w.PutQuaternion(Rot); w.PutVector3(ParentReal); w.PutQuaternion(ParentRot); }
        protected void Read(NetDataReader r)
        { OrbId = r.GetString(); ParentId = r.GetString(); RequestId = r.GetUInt(); Tick = r.GetLong(); Held = r.GetBool(); RealPos = r.GetVector3(); Rot = r.GetQuaternion(); ParentReal = r.GetVector3(); ParentRot = r.GetQuaternion(); }
    }
    public sealed class OrbRequestMsg : OrbBody, INetMessage
    {
        public OrbAction Action;
        public MsgType Type => MsgType.OrbRequest;
        public void Serialize(NetDataWriter w) { Write(w); w.Put((byte)Action); }
        public void Deserialize(NetDataReader r) { Read(r); Action = (OrbAction)r.GetByte(); }
    }
    public sealed class OrbStateMsg : OrbBody, INetMessage
    {
        public uint Holder, Revision, Requester;
        public bool Missing;
        public MsgType Type => MsgType.OrbState;
        public void Serialize(NetDataWriter w) { Write(w); w.Put(Holder); w.Put(Revision); w.Put(Requester); w.Put(Missing); }
        public void Deserialize(NetDataReader r) { Read(r); Holder = r.GetUInt(); Revision = r.GetUInt(); Requester = r.GetUInt(); Missing = r.GetBool(); }
    }
}
