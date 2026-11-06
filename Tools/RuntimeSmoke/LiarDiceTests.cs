using System;
using System.IO;
using System.Linq;
using System.Text;
using SailwindCoop.Sync;

internal static class LiarDiceTests
{
	private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
	private static DiceGame Game(int count)
	{
		var game = new DiceGame { Phase = DicePhase.Lobby };
		for (int i = 0; i < count; i++) game.Join(Guid.NewGuid(), (uint) i + 1, "Crew " + i);
		game.SetMode(1); game.Start(() => 2); return game;
	}
	public static void Run(Action<string, Action> test)
	{
		test("liar bid ordering, face boundaries and maximum table dice", () =>
		{
			Check(LiarDiceGame.CanRaise(1, 1, 0, 0, 10), "opening one");
			Check(LiarDiceGame.CanRaise(3, 1, 2, 6, 10) && LiarDiceGame.CanRaise(2, 6, 2, 5, 10), "raise");
			Check(!LiarDiceGame.CanRaise(2, 5, 2, 5, 10) && !LiarDiceGame.CanRaise(1, 6, 2, 1, 10), "lower/equal");
			Check(!LiarDiceGame.CanRaise(11, 1, 2, 1, 10) && !LiarDiceGame.CanRaise(0, 1, 0, 0, 10) &&
				!LiarDiceGame.CanRaise(1, 7, 0, 0, 10), "range");
		});
		test("liar 2-5 seats: each viewer receives only their hand; spectators receive no faces", () =>
		{
			for (int count = 2; count <= 5; count++)
			{
				var game = Game(count);
				for (int viewer = -1; viewer < count; viewer++)
				{
					byte[] faces = game.Liar.VisibleHands(viewer);
					for (int i = 0; i < 25; i++) Check(faces[i] == (i / 5 == viewer ? 2 : 0), "hidden face leaked");
					faces[0] = 6; Check(game.Liar.Hands[0][0] == 2, "snapshot mutated host hand");
				}
			}
		});
		test("liar true bid: caller loses once; reveal survives until next round", () =>
		{
			var game = Game(3); game.LiarBid(15, 2);
			Check(game.Seat == 1 && game.LiarChallenge(), "challenge");
			Check(game.Liar.Actual == 15 && game.Liar.Loser == 1 && game.Liar.DiceLeft[1] == 4, "caller loss");
			Check(game.Phase == DicePhase.LiarReveal && !game.LiarChallenge() && !game.Advance(100000), "duplicate/automatic reveal");
			Check(game.Liar.VisibleHands(-1).Count(n => n > 0) == 15, "revealed hand lost removed die");
			Check(game.LiarNext(() => 3) && game.Seat == 1 && game.Liar.TotalDice == 14 && game.Liar.Round == 2, "next round");
			Check(game.Liar.VisibleHands(-1).All(n => n == 0) && game.Liar.Quantity == 0, "next round leaked reveal");
		});
		test("liar false bid: bidder loses, ones do not act as wilds", () =>
		{
			var game = Game(2); game.Liar.Hands[0] = new byte[] { 1, 1, 1, 1, 1 };
			game.LiarBid(6, 2); game.LiarChallenge();
			Check(game.Liar.Actual == 5 && game.Liar.Loser == 0 && game.Liar.DiceLeft[0] == 4 && game.Seat == 0, "ones treated as wild");
		});
		test("liar elimination skips an empty seat, last survivor wins, rematch clears secrets", () =>
		{
			var game = Game(3); game.Liar.DiceLeft[0] = 1; game.LiarBid(15, 6); game.LiarChallenge();
			Check(game.Liar.DiceLeft[0] == 0 && game.Seat == 1, "eliminated starter");
			game.LiarNext(() => 2); game.LiarBid(1, 2); Check(game.Seat == 2, "next active seat");
			game.LiarChallenge(); game.LiarNext(() => 2);
			game.Liar.DiceLeft[game.Seat] = 1; game.LiarBid(1, 6); game.LiarChallenge();
			Check(game.Phase == DicePhase.Completed && game.Winners().Length == 1 && game.Seats.Count(p => p.Total == 1) == 1, "winner");
			Check(game.Rematch() && game.Mode == 1 && game.Liar.Hands.Length == 0 && game.Liar.VisibleHands(-1).All(n => n == 0), "rematch secrets");
		});
		test("liar pause/resume does not reroll hidden hands or advance bids", () =>
		{
			var game = Game(2); var before = game.Liar.VisibleHands(0); game.LiarBid(1, 2);
			Check(game.Pause() && !game.LiarBid(2, 2) && !game.LiarNext(() => 6) && !game.Advance(100000), "paused mutation");
			game.SetResumeReady(0, true); game.SetResumeReady(1, true);
			Check(game.Phase == DicePhase.LiarBidding && before.SequenceEqual(game.Liar.VisibleHands(0)) && game.Liar.Quantity == 1, "resume changed hand");
		});
		test("liar complete matches for every crew size preserve exactly one winner and decreasing dice", () =>
		{
			for (int players = 2; players <= 5; players++)
			{
				var game = Game(players); int losses = 0;
				while (game.Phase != DicePhase.Completed && losses < 25)
				{
					int before = game.Liar.TotalDice;
					game.LiarBid((ushort) before, 6); game.LiarChallenge(); losses++;
					Check(game.Liar.TotalDice == before - 1, "one die per loss");
					if (game.Phase != DicePhase.Completed) game.LiarNext(() => 2);
				}
				Check(game.Phase == DicePhase.Completed && losses == (players - 1) * 5 && game.Winners().Single() == players - 1, "complete match");
			}
		});
		test("liar mode changes reset readiness; active game cannot change mode", () =>
		{
			var game = new DiceGame { Phase = DicePhase.Lobby }; game.Join(Guid.NewGuid(), 1, "A"); game.Join(Guid.NewGuid(), 2, "B");
			game.SetReady(0, true); Check(game.SetMode(1) && !game.Seats[0].Ready, "readiness");
			game.Start(() => 2); Check(!game.SetMode(0) && game.Cancel() && game.Rematch() && game.SetMode(0), "mode lifecycle");
		});
		test("dice journal reads schema 3 and saves both modes with schema 4", () =>
		{
			string dir = Path.Combine(Path.GetTempPath(), "liar-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "journal.dat"); Guid id = Guid.NewGuid();
			try
			{
				using (var stream = File.Create(path)) using (var writer = new BinaryWriter(stream, Encoding.UTF8))
				{
					writer.Write(3); writer.Write(id.ToByteArray()); writer.Write(1); writer.Write(Guid.NewGuid().ToByteArray()); writer.Write(2);
					for (int i = 0; i < 2; i++) { writer.Write(Guid.NewGuid().ToByteArray()); writer.Write("Old crew"); writer.Write(0); writer.Write(0); for (int j = 0; j < 3; j++) writer.Write(12); }
				}
				var journal = DiceJournal.Load(path, id); Check(journal.Matches[0].Mode == 0, "legacy mode");
				var game = Game(2); game.Liar.DiceLeft[0] = 1; game.LiarBid(10, 6); game.LiarChallenge();
				Check(journal.Add(game), "add liar"); journal.Save(path);
				var loaded = DiceJournal.Load(path, id); Check(loaded.Matches.Count == 2 && loaded.Matches[1].Mode == 1 && loaded.Summary().Contains("Liar's Dice"), "journal roundtrip");
			}
			finally { Directory.Delete(dir, true); }
		});
	}
}
