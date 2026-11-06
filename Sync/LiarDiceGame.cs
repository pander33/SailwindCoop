using System;
using System.Linq;

namespace SailwindCoop.Sync
{
	// Host-only hands. Public snapshots are projected for one viewer; ones are not wild.
	public sealed class LiarDiceGame
	{
		public const int StartingDice = 5;
		public byte[] DiceLeft = new byte[0];
		public byte[][] Hands = new byte[0][];
		public int Seat, BidSeat = -1, Loser = -1, Actual, Round;
		public ushort Quantity;
		public byte Face;
		public bool Revealed;
		public int Winner => DiceLeft.Count(n => n > 0) == 1 ? Array.FindIndex(DiceLeft, n => n > 0) : -1;
		public int TotalDice => DiceLeft.Sum(n => (int) n);

		public void Start(int players, int first, Func<byte> roll)
		{
			DiceLeft = Enumerable.Repeat((byte) StartingDice, players).ToArray();
			Seat = first; Round = 0;
			RollRound(roll);
		}
		private void RollRound(Func<byte> roll)
		{
			Hands = DiceLeft.Select(n => Enumerable.Range(0, n).Select(_ => roll()).ToArray()).ToArray();
			Quantity = 0; Face = 0; BidSeat = Loser = -1; Actual = 0; Revealed = false; Round++;
		}
		public static bool CanRaise(int quantity, int face, int previousQuantity, int previousFace, int total)
			=> quantity >= 1 && quantity <= total && face >= 1 && face <= 6 &&
				(quantity > previousQuantity || quantity == previousQuantity && face > previousFace);
		public void Bid(ushort quantity, byte face)
		{
			// Legality and whose turn it is are offered by local input, as in Three Rounds.
			Quantity = quantity; Face = face; BidSeat = Seat; Seat = Next(Seat);
		}
		private int Next(int seat)
		{
			for (int i = 1; i <= DiceLeft.Length; i++)
				if (DiceLeft[(seat + i) % DiceLeft.Length] > 0) return (seat + i) % DiceLeft.Length;
			return seat;
		}
		public bool Challenge()
		{
			if (BidSeat < 0 || Revealed) return false;
			Actual = Hands.Sum(hand => hand.Count(value => value == Face));
			Loser = Actual >= Quantity ? Seat : BidSeat;
			DiceLeft[Loser]--; Revealed = true;
			Seat = DiceLeft[Loser] > 0 ? Loser : Next(Loser);
			return true;
		}
		public void NextRound(Func<byte> roll) => RollRound(roll);
		public byte[] VisibleHands(int viewer)
		{
			var result = new byte[DiceGame.MaxSeats * StartingDice];
			for (int seat = 0; seat < Hands.Length; seat++)
				if (Revealed || seat == viewer) Array.Copy(Hands[seat], 0, result, seat * StartingDice, Hands[seat].Length);
			return result;
		}
	}
}
