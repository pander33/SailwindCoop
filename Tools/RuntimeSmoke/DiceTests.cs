using System;
using System.IO;
using System.Linq;
using SailwindCoop.Sync;

internal static class DiceTests
{
	private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
	private static DiceGame Game(int players)
	{
		var game = new DiceGame { Phase = DicePhase.Lobby };
		for (int i = 0; i < players; i++) game.Join(Guid.NewGuid(), (uint) i + 1, "Player " + i);
		Check(game.Start(), "start"); return game;
	}
	public static void Run(Action<string, Action> test)
	{
		test("dice scores: all 216 outcomes and permutations", () =>
		{
			int pairs = 0, triples = 0, sum = 0, minimum = 30, maximum = 0;
			for (byte a = 1; a <= 6; a++) for (byte b = 1; b <= 6; b++) for (byte c = 1; c <= 6; c++)
			{
				int score = DiceGame.Score(a, b, c); sum += score; minimum = Math.Min(minimum, score); maximum = Math.Max(maximum, score);
				int bonus = a == b && b == c ? 12 : a == b || a == c || b == c ? 4 : 0;
				if (bonus == 4) pairs++; if (bonus == 12) triples++;
				Check(score == a + b + c + bonus && score == DiceGame.Score(c, a, b), "score/permutation");
			}
			Check(pairs == 90 && triples == 6 && sum == 2700 && minimum == 6 && maximum == 30, "distribution");
		});
		test("dice masks preserve held faces and reject keeping all / second reroll", () =>
		{
			for (byte mask = 0; mask < 8; mask++)
			{
				var game = Game(2); game.Roll(new byte[] { 1, 2, 3 }, 0); game.Advance(1400); game.SetHeld(mask);
				bool rolled = game.Roll(new byte[] { 6, 6, 6 }, 1500);
				Check(rolled == (mask != 7), "all held");
				for (int i = 0; i < 3; i++) Check(game.Faces[i] == ((mask & (1 << i)) != 0 || mask == 7 ? i + 1 : 6), "held face changed");
				Check(!game.Roll(new byte[] { 5, 5, 5 }, 1501), "second reroll");
			}
		});
		test("dice rounds rotate first seat and ties share victory for 2-5 seats", () =>
		{
			for (int players = 2; players <= 5; players++)
			{
				var game = Game(players);
				for (int round = 0; round < 3; round++) for (int turn = 0; turn < players; turn++)
				{
					Check(game.Seat == (round + turn) % players, "round order");
					game.Roll(new byte[] { 4, 4, 2 }, 0); game.Advance(1400); Check(game.Bank(), "bank");
					Check(game.Faces.SequenceEqual(new byte[] { 4, 4, 2 }), "last faces erased");
				}
				Check(game.Phase == DicePhase.Completed && game.Seats.All(p => p.Scored.All(v => v) && p.Total == 42), "completion");
				Check(game.Winners().Length == players, "tie"); Check(game.Rematch() && game.FirstSeat == 1 && game.Seats.All(p => !p.Ready), "rematch");
			}
		});
		test("dice party pause settles accepted reroll once without new RNG", () =>
		{
			var game = Game(2); game.Roll(new byte[] { 2, 2, 6 }, 0); game.Advance(1400);
			game.Roll(new byte[] { 6, 6, 6 }, 1500); game.Pause(); Check(game.Advance(2900), "settle");
			Check(game.Phase == DicePhase.Paused && game.ResumePhase == DicePhase.AwaitRoll && game.Seats[0].Scores[0] == 30, "resume state");
			Check(!game.Advance(4000) && !game.Roll(new byte[] { 1, 1, 1 }, 4000), "paused replay");
			game.SetResumeReady(0, true); game.SetResumeReady(1, true); Check(game.Seat == 1 && game.Phase == DicePhase.AwaitRoll, "resume");
		});
		test("dice clock freezes under world pause and dedup handles wrap", () =>
		{
			var clock = new DiceClock(); clock.SetRunning(100, true); clock.SetRunning(500, false);
			Check(clock.Elapsed(5000) == 400, "pause"); clock.SetRunning(5100, true); Check(clock.Elapsed(5200) == 500, "resume");
			var ledger = new DiceRequestLedger(); Check(ledger.Accept(1, uint.MaxValue) && ledger.Accept(1, 1) && !ledger.Accept(1, 1) && !ledger.Accept(1, uint.MaxValue), "dedup");
		});
		test("dice lobby is not paused, a leaver cancels a started party, rematch drops absent seats", () =>
		{
			var lobby = new DiceGame { Phase = DicePhase.Lobby }; lobby.Join(Guid.NewGuid(), 1, "A"); lobby.Join(Guid.NewGuid(), 2, "B");
			Check(!lobby.Pause() && !lobby.Cancel() && lobby.Leave(1) && lobby.Seats.Count == 1, "lobby");
			var game = Game(3); Check(!game.Leave(0) && game.Cancel() && game.Phase == DicePhase.Cancelled && !game.Cancel(), "cancel");
			game = Game(3);
			for (int i = 0; i < 9; i++) { game.Roll(new byte[] { 1, 2, 3 }, 0); game.Advance(1400); game.Bank(); }
			game.Seats[1].Connected = false;
			Check(game.Rematch() && game.Seats.Count == 2 && game.Seats.All(p => p.Connected) && game.FirstSeat < 2, "rematch seats");
		});
		test("dice stake asks for a new confirmation, a cancelled party can be played again, a paused table can be folded", () =>
		{
			var lobby = new DiceGame { Phase = DicePhase.Lobby }; lobby.Join(Guid.NewGuid(), 1, "A"); lobby.Join(Guid.NewGuid(), 2, "B");
			lobby.SetReady(0, true); lobby.SetReady(1, true);
			Check(lobby.SetStake(10, 2) && lobby.Stake == 10 && lobby.StakeCurrency == 2 && lobby.Seats.All(p => !p.Ready) && !lobby.SetStake(10, 2), "stake");
			var game = Game(2); Check(!game.SetStake(5, 0), "stake after the start");
			Guid match = game.MatchId; Check(game.Cancel() && game.Rematch() && game.Phase == DicePhase.Lobby && game.MatchId != match && game.Seats.Count == 2, "rematch after cancel");
			game = Game(2); Check(!game.Fold(0) && game.Pause() && game.Fold(0) && game.Phase == DicePhase.Folding, "fold");
		});
		test("dice journal follows the id kept in the save and survives a failed write", () =>
		{
			string dir = Path.Combine(Path.GetTempPath(), "dice-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
			try
			{
				var save = new System.Collections.Generic.Dictionary<string, string>();
				Guid id = DiceJournal.Associate(save);
				Check(id != Guid.Empty && DiceJournal.Associate(save) == id, "the id of a save is stable");
				var copy = new System.Collections.Generic.Dictionary<string, string>(save);
				Check(DiceJournal.Associate(copy) == id, "a copied save lost its journal");
				Check(DiceJournal.Associate(new System.Collections.Generic.Dictionary<string, string>()) != id, "a new world took another journal");
				save[DiceJournal.SaveKey] = "not a guid"; Check(DiceJournal.Associate(save) != id, "unreadable id");
				string path = Path.Combine(dir, DiceJournal.FileName(id)); var game = Game(2);
				for (int i = 0; i < 6; i++) { game.Roll(new byte[] { 6, 6, 6 }, 0); game.Advance(1400); game.Bank(); }
				Check(DiceJournal.Load(path, id).Matches.Count == 0 && DiceJournal.Load(path, id).Identity == id, "missing file");
				var journal = DiceJournal.Load(path, id); Check(journal.Add(game) && !journal.Add(game), "dedup"); journal.Save(path);
				var loaded = DiceJournal.Load(path, id);
				Check(loaded.Matches.Count == 1 && loaded.Identity == id, "reload");
				Check(loaded.Matches[0].Players.All(p => p.Triples == 3 && p.Pairs == 0 && p.Total == 90), "persisted combination statistics");
				Check(game.Rematch() && game.Seats.All(p => p.Triples == 0 && p.Pairs == 0), "rematch combination statistics");
				Check(loaded.Matches[0].Players.All(p => p.Triples == 3), "history mutated by rematch");
				bool failed = false; try { journal.Save(Path.Combine(dir, "missing", "file")); } catch (IOException) { failed = true; }
				Check(failed && DiceJournal.Load(path, id).Matches.Count == 1, "failed write damaged committed file");
				journal.Save(path); File.Delete(path);
				Check(DiceJournal.Load(path, id).Matches.Count == 1, "missing primary did not recover backup");
				File.WriteAllText(path, "damaged"); File.WriteAllText(path + ".bak", "damaged");
				bool unreadable = false; try { DiceJournal.Load(path, id); } catch (AggregateException) { unreadable = true; }
				Check(unreadable, "damaged journal was accepted");
			}
			finally { Directory.Delete(dir, true); }
		});
	}
}
