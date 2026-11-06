using System;
using System.Linq;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	public sealed partial class DiceTableInput
	{
		private int bidQuantity = 1, bidFace = 2;
		private uint bidTable, bidTurn;
		private bool buttonPeek;
		private void PrepareBid(DiceStateMsg state)
		{
			if (bidTable == state.TableId && bidTurn == state.TurnId) return;
			bidTable = state.TableId; bidTurn = state.TurnId;
			bidQuantity = Math.Max(1, (int) state.LiarQuantity); bidFace = state.LiarFace + 1;
			if (bidFace > 6) { bidQuantity++; bidFace = 1; }
		}
		private bool CanBid(DiceStateMsg state) => LiarDiceGame.CanRaise(bidQuantity, bidFace,
			state.LiarQuantity, state.LiarFace, state.DiceLeft.Sum(n => (int) n));
		private void SendBid(DiceTable table) => sync.Request(table, DiceAction.LiarBid, mask: (byte) bidFace, stake: (ushort) bidQuantity);
		private bool HandleLiarActions(DiceTable table)
		{
			var state = table.State; PrepareBid(state);
			uint me = CoopBehaviour.Instance.Net.MyNetId;
			int seat = Array.FindIndex(state.Players, p => p.NetId == me);
			bool turn = seat >= 0 && state.Seat == seat;
			table.View.ShowLiarHand = seat >= 0 && (Input.GetKey(KeyCode.P) || Input.GetMouseButton(1) || buttonPeek);
			Hint = "";
			if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.H))
			{
				table.View.HistoryText = sync.JournalText; table.View.ShowHistory = !table.View.ShowHistory;
				return true;
			}
			if (state.Phase == (byte) DicePhase.LiarReveal)
			{
				if (seat >= 0 && Input.GetKeyDown(KeyCode.Space)) Do(table, new Choice { Action = DiceAction.LiarNext });
				return true;
			}
			// A click on the slate still opens the rules, as in Three Rounds.
			if (Input.mousePosition.y >= 172f && Input.GetMouseButtonDown(0) && closeUp.CursorRay(out Ray boardRay) && table.View.Target(boardRay, out _) == BoardTarget)
			{
				table.View.HistoryText = sync.JournalText; table.View.ShowHistory = !table.View.ShowHistory; return true;
			}
			if (turn)
			{
				if (Input.GetKeyDown(KeyCode.UpArrow)) bidQuantity = Math.Min(state.DiceLeft.Sum(n => (int) n), bidQuantity + 1);
				if (Input.GetKeyDown(KeyCode.DownArrow)) bidQuantity = Math.Max(1, bidQuantity - 1);
				if (Input.GetKeyDown(KeyCode.RightArrow)) bidFace = bidFace % 6 + 1;
				if (Input.GetKeyDown(KeyCode.LeftArrow)) bidFace = (bidFace + 4) % 6 + 1;
				if (Input.GetKeyDown(KeyCode.Return) && CanBid(state)) { SendBid(table); return true; }
				if (Input.GetKeyDown(KeyCode.L) && state.LiarQuantity > 0) { Do(table, new Choice { Action = DiceAction.LiarChallenge }); return true; }
			}
			// This panel is taller than the Three Rounds action bar; clicks belong to its GUI.
			if (Input.mousePosition.y < 172f) return true;
			if (closeUp.CursorRay(out Ray ray) && table.View.Target(ray, out float _) == CupTarget)
				Hint = "Your covered dice\nHold P or right mouse: peek at your own hand\nEnter: raise bid    L: call Liar!";
			return true;
		}
		private void DrawModeChoice(DiceTable table)
		{
			if (table.State.Phase != (byte) DicePhase.Lobby) return;
			float width = Mathf.Min(620f, Screen.width - 24f), x = (Screen.width - width) / 2;
			var panel = new Rect(x, Screen.height - 102f, width, 40f);
			DrawPanel(panel, _panelTex, _borderTex, _shadowTex);
			GUI.Label(new Rect(x + 10, panel.y + 6, 76, 28), "Game:", _statusBoldStyle);
			bool enabled = GUI.enabled;
			GUI.enabled = table.State.OwnerNetId == CoopBehaviour.Instance.Net.MyNetId;
			if (GUI.Button(new Rect(x + 85, panel.y + 6, 155, 28), "Three Rounds", table.State.Mode == 0 ? _accentBtnStyle : _btnStyle) && table.State.Mode != 0)
				sync.Request(table, DiceAction.SetMode, mask: 0);
			if (GUI.Button(new Rect(x + 246, panel.y + 6, 155, 28), "Liar's Dice", table.State.Mode == 1 ? _accentBtnStyle : _btnStyle) && table.State.Mode != 1)
				sync.Request(table, DiceAction.SetMode, mask: 1);
			GUI.enabled = enabled;
			GUI.Label(new Rect(x + 412, panel.y + 6, width - 422, 28), "Owner chooses", _statusStyle);
		}
		private void DrawLiarHUD(DiceTable table)
		{
			var state = table.State; PrepareBid(state);
			int seat = Array.FindIndex(state.Players, p => p.NetId == CoopBehaviour.Instance.Net.MyNetId);
			bool turn = seat >= 0 && seat == state.Seat;
			bool reveal = state.Phase == (byte) DicePhase.LiarReveal;
			float width = Mathf.Min(850, Screen.width - 20), x = (Screen.width - width) / 2, y = Screen.height - 164;
			DrawPanel(new Rect(x, y, width, 154), _panelTex, _borderTex, _shadowTex);
			string player = state.Players.Length > state.Seat ? state.Players[state.Seat].Name : "Crew";
			GUI.Label(new Rect(x + 12, y + 8, width - 280, 25), "LIAR'S DICE | Round " + state.LiarRound + " | " + (turn ? "Your turn" : player + "'s turn"), _statusBoldStyle);
			if (GUI.Button(new Rect(x + width - 256, y + 8, 72, 25), "[Tab] Rules", _btnStyle))
			{ table.View.HistoryText = sync.JournalText; table.View.ShowHistory = !table.View.ShowHistory; }
			bool mayPause = GUI.enabled; GUI.enabled = seat >= 0;
			if (GUI.Button(new Rect(x + width - 178, y + 8, 70, 25), "Pause", _btnStyle)) Do(table, new Choice { Action = DiceAction.Pause });
			GUI.enabled = mayPause;
			if (GUI.Button(new Rect(x + width - 102, y + 8, 90, 25), "Step back", _btnStyle)) closeUp.Exit();
			string bid = state.LiarQuantity > 0 ? state.Players[state.LiarSeat].Name + " bids " + state.LiarQuantity + " x " + state.LiarFace : "No bid yet. Raise the quantity, or the face at the same quantity.";
			GUI.Label(new Rect(x + 12, y + 38, width - 24, 26), bid, _statusStyle);
			bool enabled = GUI.enabled;
			if (reveal)
			{
				GUI.Label(new Rect(x + 12, y + 70, width - 190, 28), "Found " + state.LiarActual + ". " + state.Players[state.LiarLoser].Name + " loses one die.", _statusBoldStyle);
				GUI.enabled = seat >= 0;
				if (GUI.Button(new Rect(x + width - 176, y + 70, 164, 28), "[Space] Next round", _accentBtnStyle)) Do(table, new Choice { Action = DiceAction.LiarNext });
			}
			else
			{
				GUI.enabled = turn;
				GUI.Label(new Rect(x + 12, y + 71, 35, 28), "Bid:", _statusStyle);
				if (GUI.Button(new Rect(x + 50, y + 70, 28, 28), "-", _btnStyle)) bidQuantity = Math.Max(1, bidQuantity - 1);
				GUI.Label(new Rect(x + 83, y + 71, 42, 28), bidQuantity + " x", _statusBoldStyle);
				if (GUI.Button(new Rect(x + 126, y + 70, 28, 28), "+", _btnStyle)) bidQuantity = Math.Min(state.DiceLeft.Sum(n => (int) n), bidQuantity + 1);
				for (int face = 1; face <= 6; face++)
					if (GUI.Button(new Rect(x + 168 + (face - 1) * 34, y + 70, 30, 28), face.ToString(), bidFace == face ? _accentBtnStyle : _btnStyle)) bidFace = face;
				GUI.enabled = turn && CanBid(state);
				if (GUI.Button(new Rect(x + 380, y + 70, 114, 28), "[Enter] Raise", _accentBtnStyle)) SendBid(table);
				GUI.enabled = turn && state.LiarQuantity > 0;
				if (GUI.Button(new Rect(x + 502, y + 70, 100, 28), "[L] Liar!", _accentBtnStyle)) Do(table, new Choice { Action = DiceAction.LiarChallenge });
			}
			GUI.enabled = enabled;
			buttonPeek = !reveal && seat >= 0 && GUI.RepeatButton(new Rect(x + 12, y + 112, 156, 28), "Hold to peek [P]", _btnStyle);
			if (!reveal && seat >= 0 && table.View.ShowLiarHand)
			{
				for (int i = 0; i < 5; i++)
				{
					byte face = state.LiarHands[seat * 5 + i]; if (face == 0) continue;
					GUI.DrawTextureWithTexCoords(new Rect(x + 182 + i * 34, y + 111, 29, 29), DiceVisuals.Atlas(), new Rect((face - 1) / 8f, 0, 1 / 8f, 1));
				}
			}
			else GUI.Label(new Rect(x + 182, y + 113, width - 194, 26), reveal ? "All hands are revealed on the table. Ones are not wild." : "Your dice stay hidden. Ones are not wild.", _statusStyle);
		}
	}
}
