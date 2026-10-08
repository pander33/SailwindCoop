using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
	public enum DiceAction : byte
	{
		Place, Join, Leave, Ready, Start, Roll, SetHeldMask, Bank, Pause,
		ResumeReady, CancelVote, Remove, Rematch, RequestBaseline, Presence, SetStake
	}

	public sealed class DiceRequestMsg : INetMessage
	{
		public DiceAction Action;
		public uint TableId, Epoch, RequestId, Revision, TurnId;
		public ushort BoatIndex, Stake;
		public string MatchId = "";
		public byte Mask;
		public bool Flag;
		public float X, Y, Z, Yaw;
		public MsgType Type => MsgType.DiceRequest;
		public void Serialize(NetDataWriter w)
		{
			w.Put((byte) Action); w.Put(TableId); w.Put(Epoch); w.Put(RequestId); w.Put(Revision);
			w.Put(TurnId); w.Put(BoatIndex); w.Put(MatchId); w.Put(Mask); w.Put(Flag);
			w.Put(X); w.Put(Y); w.Put(Z); w.Put(Yaw); w.Put(Stake);
		}
		public void Deserialize(NetDataReader r)
		{
			Action = (DiceAction) r.GetByte(); TableId = r.GetUInt(); Epoch = r.GetUInt(); RequestId = r.GetUInt();
			Revision = r.GetUInt(); TurnId = r.GetUInt(); BoatIndex = r.GetUShort(); MatchId = r.GetString(64);
			Mask = r.GetByte(); Flag = r.GetBool(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); Yaw = r.GetFloat(); Stake = r.GetUShort();
			if (Action > DiceAction.SetStake || Mask > 7) throw new InvalidDataException("Dice request format");
		}
	}

	public sealed class DicePlayerData
	{
		public string Identity = "", Name = "";
		public uint NetId;
		public bool Connected, Ready, ResumeReady, CancelVote;
		public int[] Scores = new int[3];
		public bool[] Scored = new bool[3];
		internal void Write(NetDataWriter w)
		{
			w.Put(Identity); w.Put(Name); w.Put(NetId); w.Put(Connected); w.Put(Ready); w.Put(ResumeReady); w.Put(CancelVote);
			for (int i = 0; i < 3; i++) { w.Put(Scores[i]); w.Put(Scored[i]); }
		}
		internal void Read(NetDataReader r)
		{
			Identity = r.GetString(64); Name = r.GetString(256); NetId = r.GetUInt();
			Connected = r.GetBool(); Ready = r.GetBool(); ResumeReady = r.GetBool(); CancelVote = r.GetBool();
			for (int i = 0; i < 3; i++) { Scores[i] = r.GetInt(); Scored[i] = r.GetBool(); }
		}
	}

		public sealed class DiceStateMsg : INetMessage
		{
		// BoatIndex=ushort.MaxValue denotes a shore table; X/Y/Z then use origin-stable world coordinates.
		public uint TableId, Epoch, Revision, TurnId, RollId, ClockRevision, OwnerNetId;
		public ushort BoatIndex, Stake;
		public byte StakeCurrency;
		public bool StakesAllowed;
		public string MatchId = "", ClockEpoch = "";
		public float X, Y, Z, Yaw;
		public byte Phase, ResumePhase, HeldMask, RollHeldMask, Round, Seat, FirstSeat;
		public bool Rerolled, Running;
		public long AnchorTick, ElapsedMs, PresentationAtMs;
		public byte[] Faces = new byte[3];
		public DicePlayerData[] Players = new DicePlayerData[0];
		public MsgType Type => MsgType.DiceState;
		public void Serialize(NetDataWriter w)
		{
			w.Put(TableId); w.Put(Epoch); w.Put(Revision); w.Put(TurnId); w.Put(RollId); w.Put(ClockRevision);
			w.Put(BoatIndex); w.Put(MatchId); w.Put(ClockEpoch); w.Put(X); w.Put(Y); w.Put(Z); w.Put(Yaw);
			w.Put(Phase); w.Put(ResumePhase); w.Put(HeldMask); w.Put(RollHeldMask); w.Put(Round); w.Put(Seat); w.Put(FirstSeat);
			w.Put(Rerolled); w.Put(Running); w.Put(AnchorTick); w.Put(ElapsedMs); w.Put(PresentationAtMs);
			for (int i = 0; i < 3; i++) w.Put(Faces[i]);
			w.Put(OwnerNetId); w.Put(Stake); w.Put(StakeCurrency); w.Put(StakesAllowed);
			w.Put((byte) Players.Length); foreach (var p in Players) p.Write(w);
		}
		public void Deserialize(NetDataReader r)
		{
			TableId = r.GetUInt(); Epoch = r.GetUInt(); Revision = r.GetUInt(); TurnId = r.GetUInt(); RollId = r.GetUInt(); ClockRevision = r.GetUInt();
			BoatIndex = r.GetUShort(); MatchId = r.GetString(64); ClockEpoch = r.GetString(64);
			X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); Yaw = r.GetFloat();
			Phase = r.GetByte(); ResumePhase = r.GetByte(); HeldMask = r.GetByte(); RollHeldMask = r.GetByte();
			Round = r.GetByte(); Seat = r.GetByte(); FirstSeat = r.GetByte(); Rerolled = r.GetBool(); Running = r.GetBool();
			AnchorTick = r.GetLong(); ElapsedMs = r.GetLong(); PresentationAtMs = r.GetLong();
			for (int i = 0; i < 3; i++) Faces[i] = r.GetByte();
			OwnerNetId = r.GetUInt(); Stake = r.GetUShort(); StakeCurrency = r.GetByte(); StakesAllowed = r.GetBool();
			int count = r.GetByte(); if (count > 5) throw new InvalidDataException("Dice seat count");
			Players = new DicePlayerData[count]; for (int i = 0; i < count; i++) { Players[i] = new DicePlayerData(); Players[i].Read(r); }
			if (Phase > 10 || ResumePhase > 10 || HeldMask > 7 || RollHeldMask > 7 || Round > 2 || (count > 0 && Seat >= count))
				throw new InvalidDataException("Dice state format");
			foreach (byte face in Faces) if (face > 6) throw new InvalidDataException("Dice face");
		}
	}

	public enum DiceResultKind : byte { Applied, AlreadyApplied, ResyncNeeded }
	public sealed class DiceResultMsg : INetMessage
	{
		public uint RequestId, Revision;
		public DiceResultKind Result;
		public string Detail = "";
		public MsgType Type => MsgType.DiceResult;
		public void Serialize(NetDataWriter w) { w.Put(RequestId); w.Put(Revision); w.Put((byte) Result); w.Put(Detail); }
		public void Deserialize(NetDataReader r) { RequestId = r.GetUInt(); Revision = r.GetUInt(); Result = (DiceResultKind) r.GetByte(); Detail = r.GetString(512); }
	}

	public sealed class DiceBaselineMsg : INetMessage
	{
		public uint Revision;
		public uint[] Tables = new uint[0];
		public MsgType Type => MsgType.DiceBaseline;
		public void Serialize(NetDataWriter w) { w.Put(Revision); w.Put((ushort) Tables.Length); foreach (uint id in Tables) w.Put(id); }
		public void Deserialize(NetDataReader r)
		{
			Revision = r.GetUInt(); int count = r.GetUShort(); if (count > 255) throw new InvalidDataException("Dice table count");
			Tables = new uint[count]; for (int i = 0; i < count; i++) Tables[i] = r.GetUInt();
		}
	}

	public sealed class DiceJournalMsg : INetMessage
	{
		public string Text = "";
		public MsgType Type => MsgType.DiceJournal;
		public void Serialize(NetDataWriter w) => w.Put(Text);
		public void Deserialize(NetDataReader r) => Text = r.GetString(12000);
	}
}
