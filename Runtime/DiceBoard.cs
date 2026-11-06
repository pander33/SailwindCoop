using System.Linq;
using System.Text;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	// The score board. It is not one block of text: every column is a TextMesh of its own, so the
	// rounds stand under their headings whatever the borrowed font does with its metrics, and the
	// marker in front of the active row is a piece of geometry instead of a character the font may
	// not have. The rules and the match history stay one block, on the same slate.
	internal sealed class DiceBoard
	{
		// Line height follows from characterSize in DiceVisuals.BoardText: nine lines fill the slate.
		private const float TextTop = .246f, Line = .026f, Depth = -.004f;
		private const float Left = -.288f, Right = .298f, NameX = -.281f, MarkerX = -.300f;
		private const float ColRound1 = -.058f, ColRound2 = .000f, ColRound3 = .058f, ColTotal = .128f, ColStatus = .152f;
		private const float BadgeX = .050f;
		private const int PageLines = 9, PageColumns = 46, Columns = 6;
		private static readonly Color Chalk = new Color(.905f, .890f, .835f), Gold = new Color(.86f, .70f, .31f);
		private readonly TextMesh title, aside, headline, faces, badge, page;
		private readonly TextMesh[] columns = new TextMesh[Columns];
		private readonly Transform marker;

		public DiceBoard(Transform hinge, int layer, Material gold)
		{
			title = Make(hinge, layer, "Board title", NameX, TextTop, TextAnchor.UpperLeft, Chalk);
			aside = Make(hinge, layer, "Board round", Right, TextTop, TextAnchor.UpperRight, Chalk);
			headline = Make(hinge, layer, "Board turn", NameX, TextTop - Line, TextAnchor.UpperLeft, Chalk);
			faces = Make(hinge, layer, "Board dice", NameX, TextTop - 2f * Line, TextAnchor.UpperLeft, Chalk);
			badge = Make(hinge, layer, "Board bonus", BadgeX, TextTop - 2f * Line, TextAnchor.UpperLeft, Gold);
			float top = TextTop - 3f * Line;
			columns[0] = Make(hinge, layer, "Board names", NameX, top, TextAnchor.UpperLeft, Chalk);
			columns[1] = Make(hinge, layer, "Board round 1", ColRound1, top, TextAnchor.UpperRight, Chalk);
			columns[2] = Make(hinge, layer, "Board round 2", ColRound2, top, TextAnchor.UpperRight, Chalk);
			columns[3] = Make(hinge, layer, "Board round 3", ColRound3, top, TextAnchor.UpperRight, Chalk);
			columns[4] = Make(hinge, layer, "Board total", ColTotal, top, TextAnchor.UpperRight, Chalk);
			columns[5] = Make(hinge, layer, "Board state", ColStatus, top, TextAnchor.UpperLeft, Chalk);
			// The rules and the history use the whole slate: they are free text, not a table.
			page = Make(hinge, layer, "Board page", Left, .252f, TextAnchor.UpperLeft, Chalk);
			page.gameObject.SetActive(false);
			var diamond = GameObject.CreatePrimitive(PrimitiveType.Cube);
			diamond.name = "Board marker"; diamond.layer = layer;
			var collider = diamond.GetComponent<Collider>();
			if (collider != null) { collider.enabled = false; Object.Destroy(collider); }
			diamond.transform.SetParent(hinge, false);
			diamond.transform.localPosition = new Vector3(MarkerX, TextTop, Depth);
			diamond.transform.localRotation = Quaternion.Euler(0, 0, 45f);
			diamond.transform.localScale = new Vector3(.011f, .011f, .003f);
			diamond.GetComponent<MeshRenderer>().sharedMaterial = gold;
			marker = diamond.transform;
			marker.gameObject.SetActive(false);
		}

		private static TextMesh Make(Transform parent, int layer, string name, float x, float y, TextAnchor anchor, Color color)
		{
			var obj = new GameObject(name); obj.layer = layer;
			obj.transform.SetParent(parent, false);
			obj.transform.localPosition = new Vector3(x, y, Depth);
			var text = obj.AddComponent<TextMesh>();
			DiceVisuals.BoardText(text, anchor, color);
			return text;
		}

		/// <summary>The score of a party, in play or waiting for one.</summary>
		public void Score(DiceStateMsg state, bool rolling)
		{
			var phase = (DicePhase) state.Phase;
			bool playing = phase >= DicePhase.AwaitRoll && phase <= DicePhase.Paused;
			string turn = state.Players.Length > state.Seat ? Short(state.Players[state.Seat].Name, 14) : "";
			title.text = "THREE ROUNDS";
			aside.text = Aside(state, phase, playing);
			headline.text = Headline(state, phase, turn);
			faces.text = Faces(state, phase, rolling);
			badge.text = Bonus(state, phase, rolling);
			for (int column = 0; column < Columns; column++)
			{
				var text = new StringBuilder(Header(column));
				for (int seat = 0; seat < state.Players.Length; seat++)
					text.Append('\n').Append(Cell(column, state, phase, state.Players[seat]));
				columns[column].text = text.ToString();
			}
			page.gameObject.SetActive(false);
			SetTable(true);
			marker.gameObject.SetActive(playing && state.Players.Length > 0);
			marker.localPosition = new Vector3(MarkerX, TextTop - (4.5f + state.Seat) * Line, Depth);
		}

		/// <summary>The rules and the completed matches, in place of the table.</summary>
		public void Page(string text)
		{
			page.text = Fit(text);
			page.gameObject.SetActive(true);
			SetTable(false);
			marker.gameObject.SetActive(false);
		}

		private void SetTable(bool visible)
		{
			title.gameObject.SetActive(visible); aside.gameObject.SetActive(visible);
			headline.gameObject.SetActive(visible); faces.gameObject.SetActive(visible);
			badge.gameObject.SetActive(visible && badge.text.Length > 0);
			for (int column = 0; column < Columns; column++) columns[column].gameObject.SetActive(visible);
		}

		private static string Aside(DiceStateMsg state, DicePhase phase, bool playing)
		{
			string round = playing ? "round " + (state.Round + 1) + " of 3" : "";
			if (state.Stake <= 0) return round;
			string money = state.Stake + " " + Short(DiceTableView.CurrencyName(state.StakeCurrency), 10);
			if (phase == DicePhase.Completed && state.Players.Length > 0)
			{
				// The same rule as DiceSync.Settle: nobody pays when everyone shares the best score.
				int best = state.Players.Max(p => p.Scores.Sum()), winning = state.Players.Count(p => p.Scores.Sum() == best);
				if (winning == state.Players.Length) return "draw: nobody pays";
				return "losers pay " + money + (winning > 1 ? " to the winners" : "");
			}
			return round.Length > 0 ? round + "  |  " + money + " each" : money + " each";
		}

		private static string Headline(DiceStateMsg state, DicePhase phase, string turn)
		{
			switch (phase)
			{
				case DicePhase.Unfolding: return "Setting up";
				case DicePhase.Lobby:
					if (state.Players.Length == 0) return "Sit down at the cup";
					if (state.Players.Length < 2) return "Waiting for a second player";
					return state.Players.All(p => p.Ready) ? "All ready: start at this board" : "Waiting for everyone to be ready";
				case DicePhase.AwaitRoll: return turn + " to roll";
				case DicePhase.Selecting: return turn + ": keep, reroll or take";
				case DicePhase.RollingInitial:
				case DicePhase.RollingReroll: return turn + " rolls";
				case DicePhase.Paused: return "Paused";
				case DicePhase.Completed:
					if (state.Players.Length == 0) return "";
					int best = state.Players.Max(p => p.Scores.Sum());
					var winners = state.Players.Where(p => p.Scores.Sum() == best).Select(p => Short(p.Name, 14)).ToArray();
					return (winners.Length == 1 ? "Winner: " : "Shared victory: ") + string.Join(", ", winners);
				case DicePhase.Cancelled: return "Party cancelled";
				default: return "";
			}
		}

		// The faces are already in the state while the dice are still in the air, so a roll in
		// progress hides them.
		private static string Faces(DiceStateMsg state, DicePhase phase, bool rolling)
		{
			if (rolling) return "Rolling...";
			if (phase < DicePhase.AwaitRoll || !state.Faces.All(f => f > 0)) return "";
			int score = DiceGame.Score(state.Faces[0], state.Faces[1], state.Faces[2]);
			return string.Join("  ", state.Faces.Select(f => f.ToString())) + "  =  " + score;
		}

		private static string Bonus(DiceStateMsg state, DicePhase phase, bool rolling)
		{
			if (rolling || phase < DicePhase.AwaitRoll || !state.Faces.All(f => f > 0)) return "";
			int score = DiceGame.Score(state.Faces[0], state.Faces[1], state.Faces[2]);
			int sum = state.Faces[0] + state.Faces[1] + state.Faces[2];
			return score - sum == 12 ? "TRIPLE +12" : score - sum == 4 ? "PAIR +4" : "";
		}

		private static string Header(int column)
		{
			switch (column)
			{
				case 0: return "CREW";
				case 1: return "R-1";
				case 2: return "R-2";
				case 3: return "R-3";
				case 4: return "TOTAL";
				default: return "";
			}
		}

		private static string Cell(int column, DiceStateMsg state, DicePhase phase, DicePlayerData player)
		{
			switch (column)
			{
				case 0: return Short(player.Name, 14);
				case 1: case 2: case 3:
					int round = column - 1;
					return player.Scored[round] ? player.Scores[round].ToString() : "-";
				case 4: return player.Scores.Sum().ToString();
				default:
					if (!player.Connected) return "away";
					if (phase == DicePhase.Lobby) return player.Ready ? "ready" : "";
					if (phase == DicePhase.Paused) return player.CancelVote ? "cancel" : player.ResumeReady ? "ready" : "";
					return "";
			}
		}

		private static string Fit(string text)
			=> string.Join("\n", text.Split('\n').Where(line => line.Trim().Length > 0).Take(PageLines)
				.Select(line => line.Length > PageColumns ? line.Substring(0, PageColumns - 2) + ".." : line.TrimEnd()));

		private static string Short(string text, int limit) => text.Length > limit ? text.Substring(0, limit - 2) + ".." : text;
	}
}
