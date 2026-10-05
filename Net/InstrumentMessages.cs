using LiteNetLib.Utils;
using UnityEngine;

namespace SailwindCoop.Net
{
    public abstract class InstrumentBody
    {
        public int InstanceId, PrefabIndex;
        public uint RequestId, ParentRevision;
        public long Tick;
        public bool Final, Kinematic, Thrown, Throwing, BodyActive, StaticBobberActive;
        public Vector3 RealPos, Velocity, RotatorPos;
        public Quaternion Rot = Quaternion.identity, RotatorRot = Quaternion.identity;
        public float Limit, TargetLength, Bend, ReelVolume;
        public int FishPrefab;
        public bool FishDead, Splash, TensionSound;
        public float FishEnergy, FishTension;
        protected void Write(NetDataWriter w)
        {
            w.Put(InstanceId); w.Put(PrefabIndex); w.Put(RequestId); w.Put(ParentRevision); w.Put(Tick); w.Put(Final); w.Put(Kinematic); w.Put(Thrown); w.Put(Throwing);
            w.PutVector3(RealPos); w.PutQuaternion(Rot); w.PutVector3(Velocity); w.PutVector3(RotatorPos); w.PutQuaternion(RotatorRot);
            w.Put(Limit); w.Put(TargetLength); w.Put(Bend); w.Put(ReelVolume); w.Put(FishPrefab); w.Put(FishDead); w.Put(Splash); w.Put(TensionSound); w.Put(FishEnergy); w.Put(FishTension);
            w.Put(BodyActive); w.Put(StaticBobberActive);
        }
        protected void Read(NetDataReader r)
        {
            InstanceId = r.GetInt(); PrefabIndex = r.GetInt(); RequestId = r.GetUInt(); ParentRevision = r.GetUInt(); Tick = r.GetLong(); Final = r.GetBool(); Kinematic = r.GetBool(); Thrown = r.GetBool(); Throwing = r.GetBool();
            RealPos = r.GetVector3(); Rot = r.GetQuaternion(); Velocity = r.GetVector3(); RotatorPos = r.GetVector3(); RotatorRot = r.GetQuaternion();
            Limit = r.GetFloat(); TargetLength = r.GetFloat(); Bend = r.GetFloat(); ReelVolume = r.GetFloat(); FishPrefab = r.GetInt(); FishDead = r.GetBool(); Splash = r.GetBool(); TensionSound = r.GetBool(); FishEnergy = r.GetFloat(); FishTension = r.GetFloat();
            BodyActive = r.GetBool(); StaticBobberActive = r.GetBool();
        }
    }
    public sealed class InstrumentRequestMsg : InstrumentBody, INetMessage
    {
        public MsgType Type => MsgType.InstrumentRequest;
        public void Serialize(NetDataWriter w) => Write(w);
        public void Deserialize(NetDataReader r) => Read(r);
    }
    public sealed class InstrumentStateMsg : InstrumentBody, INetMessage
    {
        public uint Revision;
        public MsgType Type => MsgType.InstrumentState;
        public void Serialize(NetDataWriter w) { Write(w); w.Put(Revision); }
        public void Deserialize(NetDataReader r) { Read(r); Revision = r.GetUInt(); }
    }
}
