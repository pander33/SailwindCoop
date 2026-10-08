using System;
using System.Collections.Generic;
using System.Linq;

namespace SailwindCoop.Sync
{
	public enum DicePhase : byte
	{
		Unfolding, Lobby, AwaitRoll, RollingInitial, Selecting, RollingReroll,
		Paused, Completed, Cancelled, Folding, Removed
	}

	public sealed class DiceSeat
	{
		public Guid Identity;
		public uint NetId;
		public string Name = "";
		public bool Connected = true, Ready, ResumeReady, CancelVote;
		public int Pairs, Triples;
		public int[] Scores = new int[3];
		public bool[] Scored = new bool[3];

		public int Total => Scores.Sum();
		public DiceSeat Copy() => new DiceSeat
		{
			Identity = Identity, NetId = NetId, Name = Name, Connected = Connected,
			Ready = Ready, ResumeReady = ResumeReady, CancelVote = CancelVote,
			Pairs = Pairs, Triples = Triples,
			Scores = (int[]) Scores.Clone(), Scored = (bool[]) Scored.Clone()
		};
	}

	public sealed class DiceGame
	{
		public const int MaxSeats = 5, Rounds = 3;
		public const long RollDurationMs = 1400, FoldDurationMs = 900;
		public Guid MatchId = Guid.NewGuid();
		public DicePhase Phase = DicePhase.Unfolding, ResumePhase = DicePhase.Lobby;
		public readonly List<DiceSeat> Seats = new List<DiceSeat>();
		public byte[] Faces = new byte[3];
		public byte HeldMask, RollHeldMask;
		public bool Rerolled;
		public int Round, Seat, FirstSeat;
		public uint TurnId, RollId;
		public long PresentationAtMs;
		public ushort Stake;
		public byte StakeCurrency;

		public static int Score(byte first, byte second, byte third)
			=> first + second + third + (first == second && second == third ? 12 :
				first == second || first == third || second == third ? 4 : 0);

		public int[] Winners()
		{
			if (Seats.Count == 0) return new int[0];
			int best = Seats.Max(player => player.Total);
			return Enumerable.Range(0, Seats.Count).Where(index => Seats[index].Total == best).ToArray();
		}

		public bool Join(Guid identity, uint netId, string name)
		{
			if (Phase != DicePhase.Lobby || Seats.Count >= MaxSeats || Seats.Any(player => player.Identity == identity)) return false;
			Seats.Add(new DiceSeat { Identity = identity, NetId = netId, Name = name });
			return true;
		}

		public bool Leave(int index)
		{
			if (Phase != DicePhase.Lobby || index < 0 || index >= Seats.Count) return false;
			Seats.RemoveAt(index);
			return true;
		}

		public bool SetReady(int index, bool ready)
		{
			if (Phase != DicePhase.Lobby || index < 0 || index >= Seats.Count) return false;
			Seats[index].Ready = ready;
			return true;
		}

		// A changed stake asks everyone to confirm again.
		public bool SetStake(ushort stake, byte currency)
		{
			if (Phase != DicePhase.Lobby || stake == Stake && currency == StakeCurrency) return false;
			Stake = stake; StakeCurrency = currency;
			foreach (var player in Seats) player.Ready = false;
			return true;
		}

		public bool Start()
		{
			if (Phase != DicePhase.Lobby || Seats.Count < 2) return false;
			Round = 0;
			Seat = FirstSeat % Seats.Count;
			BeginTurn();
			return true;
		}

		private void BeginTurn()
		{
			TurnId++;
			HeldMask = RollHeldMask = 0;
			Rerolled = false;
			Phase = DicePhase.AwaitRoll;
		}

		public bool Roll(byte[] receivedFaces, long elapsedMs)
		{
			bool initial = Phase == DicePhase.AwaitRoll;
			if (!initial && (Phase != DicePhase.Selecting || Rerolled || HeldMask == 7)) return false;
			RollHeldMask = initial ? (byte) 0 : HeldMask;
			for (int index = 0; index < 3; index++)
				if ((RollHeldMask & (1 << index)) == 0) Faces[index] = receivedFaces[index];
			Rerolled = !initial;
			RollId++;
			PresentationAtMs = elapsedMs;
			Phase = initial ? DicePhase.RollingInitial : DicePhase.RollingReroll;
			return true;
		}

		public bool SetHeld(byte mask)
		{
			if (Phase != DicePhase.Selecting) return false;
			HeldMask = mask;
			return true;
		}

		public bool Bank()
		{
			if (Phase != DicePhase.Selecting) return false;
			BankAcceptedRoll();
			return true;
		}

		private void BankAcceptedRoll()
		{
			Seats[Seat].Scores[Round] = Score(Faces[0], Faces[1], Faces[2]);
			Seats[Seat].Scored[Round] = true;
			if (Faces[0] == Faces[1] && Faces[1] == Faces[2]) Seats[Seat].Triples++;
			else if (Faces[0] == Faces[1] || Faces[0] == Faces[2] || Faces[1] == Faces[2]) Seats[Seat].Pairs++;
			int played = Seats.Count(player => player.Scored[Round]);
			if (played == Seats.Count)
			{
				if (++Round == Rounds) { Round = Rounds - 1; Phase = DicePhase.Completed; return; }
				Seat = (FirstSeat + Round) % Seats.Count;
			}
			else Seat = (Seat + 1) % Seats.Count;
			BeginTurn();
		}

		public bool Pause()
		{
			if (Phase == DicePhase.Paused || Phase >= DicePhase.Completed || Phase <= DicePhase.Lobby) return false;
			ResumePhase = Phase;
			Phase = DicePhase.Paused;
			foreach (var player in Seats) { player.ResumeReady = false; player.CancelVote = false; }
			return true;
		}

		public bool Cancel()
		{
			if (Phase >= DicePhase.Completed || Phase <= DicePhase.Lobby) return false;
			Phase = DicePhase.Cancelled;
			return true;
		}

		public bool SetResumeReady(int index, bool ready)
		{
			if (Phase != DicePhase.Paused || index < 0 || index >= Seats.Count) return false;
			Seats[index].ResumeReady = ready;
			if (Seats.All(player => player.Connected && player.ResumeReady)) Phase = ResumePhase;
			return true;
		}

		public bool VoteCancel(int index)
		{
			if (Phase != DicePhase.Paused || index < 0 || index >= Seats.Count) return false;
			Seats[index].CancelVote = true;
			if (Seats.Where(player => player.Connected).All(player => player.CancelVote)) Phase = DicePhase.Cancelled;
			return true;
		}

		public bool Fold(long elapsedMs)
		{
			// A paused party may be folded away with its table; one that is being played may not.
			if (Phase != DicePhase.Lobby && Phase != DicePhase.Completed && Phase != DicePhase.Cancelled && Phase != DicePhase.Paused) return false;
			Phase = DicePhase.Folding;
			PresentationAtMs = elapsedMs;
			return true;
		}

		public bool Rematch()
		{
			if (Phase != DicePhase.Completed && Phase != DicePhase.Cancelled) return false;
			MatchId = Guid.NewGuid();
			Seats.RemoveAll(player => !player.Connected);
			FirstSeat = Seats.Count > 0 ? (FirstSeat + 1) % Seats.Count : 0;
			foreach (var player in Seats)
			{
				player.Scores = new int[3]; player.Scored = new bool[3];
				player.Ready = player.ResumeReady = player.CancelVote = false;
				player.Pairs = player.Triples = 0;
			}
			Phase = DicePhase.Lobby;
			Round = 0; Seat = FirstSeat; HeldMask = 0; Rerolled = false;
			Faces = new byte[3];
			return true;
		}

		public bool Advance(long elapsedMs)
		{
			bool paused = Phase == DicePhase.Paused;
			DicePhase active = paused ? ResumePhase : Phase;
			long duration = active == DicePhase.Unfolding || active == DicePhase.Folding ? FoldDurationMs : RollDurationMs;
			if (elapsedMs - PresentationAtMs < duration) return false;
			switch (active)
			{
				case DicePhase.Unfolding: Phase = DicePhase.Lobby; break;
				case DicePhase.Folding: Phase = DicePhase.Removed; break;
				case DicePhase.RollingInitial: Phase = DicePhase.Selecting; break;
				case DicePhase.RollingReroll: BankAcceptedRoll(); break;
				default: return false;
			}
			if (paused && Phase != DicePhase.Completed) { ResumePhase = Phase; Phase = DicePhase.Paused; }
			return true;
		}
	}

	// Where a table stands and whose it is.
	public struct DiceTablePlace
	{
		public const int MaxTables = 5;
		public ushort Boat;
		public float X, Y, Z, Yaw;
		public Guid Owner;
	}

	public sealed class DiceClock
	{
		public Guid Epoch = Guid.NewGuid();
		public long AnchorServerTick, ElapsedAtAnchorMs;
		public bool Running;
		public uint Revision;
		public long Elapsed(long serverTick) => ElapsedAtAnchorMs + (Running ? Math.Max(0, serverTick - AnchorServerTick) : 0);
		public bool SetRunning(long tick, bool running)
		{
			if (Running == running) return false;
			ElapsedAtAnchorMs = Elapsed(tick); AnchorServerTick = tick; Running = running; Revision++;
			return true;
		}
	}

	public sealed class DiceRequestLedger
	{
		private readonly Dictionary<uint, uint> last = new Dictionary<uint, uint>();
		public bool Accept(uint actor, uint request)
		{
			if (last.TryGetValue(actor, out uint previous) && unchecked((int) (request - previous)) <= 0) return false;
			last[actor] = request;
			return true;
		}
		public void Clear() => last.Clear();
	}
}
