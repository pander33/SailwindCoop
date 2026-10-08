using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SailwindCoop.Sync
{
	// The journal belongs to a world, not to a slot or a file: its id is kept in the save's own
	// mod data (GameState.modData), so it follows the save, its backups and its copies. A world
	// without the id gets a new journal.
	public sealed class DiceJournal
	{
		public const string SaveKey = "SailwindCoop.DiceJournal";
		public Guid Identity = Guid.NewGuid();
		public readonly List<DiceMatchRecord> Matches = new List<DiceMatchRecord>();
		public static string FileName(Guid identity) => "coop_dice_" + identity.ToString("N") + ".dat";
		public static Guid Associate(IDictionary<string, string> saveData)
		{
			if (saveData.TryGetValue(SaveKey, out string text) && Guid.TryParse(text, out Guid identity) && identity != Guid.Empty) return identity;
			identity = Guid.NewGuid();
			saveData[SaveKey] = identity.ToString("N");
			return identity;
		}
		public static DiceJournal Load(string path, Guid identity)
		{
			if (!File.Exists(path) && !File.Exists(path + ".bak")) return new DiceJournal { Identity = identity };
			var journal = AtomicSaveFile.Read(path, Read, out _);
			journal.Identity = identity;
			return journal;
		}
		public bool Add(DiceGame game)
		{
			if (game.Phase != DicePhase.Completed || Matches.Any(m => m.MatchId == game.MatchId)) return false;
			Matches.Add(new DiceMatchRecord { MatchId = game.MatchId, Players = game.Seats.Select(p => p.Copy()).ToArray() });
			return true;
		}
		public void Save(string path) => AtomicSaveFile.Write(path, Write);
		private void Write(Stream stream)
		{
			using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
			{
				writer.Write(3); writer.Write(Identity.ToByteArray());
				writer.Write(Matches.Count);
				foreach (var match in Matches)
				{
					writer.Write(match.MatchId.ToByteArray()); writer.Write(match.Players.Length);
					foreach (var p in match.Players)
					{
						writer.Write(p.Identity.ToByteArray()); writer.Write(p.Name);
						writer.Write(p.Pairs); writer.Write(p.Triples);
						for (int i = 0; i < 3; i++) writer.Write(p.Scores[i]);
					}
				}
			}
		}
		private static DiceJournal Read(Stream stream)
		{
			using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
			{
				if (reader.ReadInt32() != 3) throw new InvalidDataException("Unknown dice journal schema");
				var journal = new DiceJournal { Identity = new Guid(reader.ReadBytes(16)) };
				int count = reader.ReadInt32(); if (count < 0 || count > 100000) throw new InvalidDataException("Dice match count");
				for (int m = 0; m < count; m++)
				{
					var match = new DiceMatchRecord { MatchId = new Guid(reader.ReadBytes(16)) };
					int seats = reader.ReadInt32(); if (seats < 2 || seats > 5) throw new InvalidDataException("Dice seats");
					match.Players = new DiceSeat[seats];
					for (int p = 0; p < seats; p++)
					{
						var player = new DiceSeat { Identity = new Guid(reader.ReadBytes(16)), Name = reader.ReadString() };
						player.Pairs = reader.ReadInt32(); player.Triples = reader.ReadInt32();
						for (int i = 0; i < 3; i++) { player.Scores[i] = reader.ReadInt32(); player.Scored[i] = true; }
						match.Players[p] = player;
					}
					journal.Matches.Add(match);
				}
				return journal;
			}
		}
		public string Summary()
		{
			var text = new StringBuilder("Completed matches: " + Matches.Count + "\n");
			foreach (var group in Matches.SelectMany(m => m.Players).GroupBy(p => p.Identity).Take(12))
			{
				int wins = 0, shared = 0;
				foreach (var match in Matches)
				{
					var player = match.Players.FirstOrDefault(p => p.Identity == group.Key);
					if (player == null || player.Total != match.Players.Max(p => p.Total)) continue;
					if (match.Players.Count(p => p.Total == player.Total) == 1) wins++; else shared++;
				}
				text.AppendLine(group.Last().Name + ": " + group.Count() + " games, " + wins + " wins, " + shared +
					" shared; best " + group.Max(p => p.Total) + ", turn " + group.Max(p => p.Scores.Max()) +
					", pairs " + group.Sum(p => p.Pairs) + ", triples " + group.Sum(p => p.Triples));
			}
			foreach (var match in Matches.Skip(Math.Max(0, Matches.Count - 5)))
				text.AppendLine(string.Join(" / ", match.Players.Select(p => p.Name + " " + p.Total)));
			return text.ToString();
		}
	}
	public sealed class DiceMatchRecord
	{
		public Guid MatchId;
		public DiceSeat[] Players;
	}
}
