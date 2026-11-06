using System;
using System.Linq;
using System.Text;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	public sealed partial class DiceTableView
	{
		public bool ShowLiarHand;
		private Transform[] liarDice, liarCups;
		private bool showingLiar;
		private uint liarBoardRevision = uint.MaxValue;
		private bool liarHistory;
		internal const string LiarRules = "LIAR'S DICE - ones are NOT wild\nFive dice each. Raise quantity, or face at\nthe same quantity. Call Liar! to reveal.\nWrong bidder/caller loses one die. Last wins.";

		private void HideLiar()
		{
			if (!showingLiar) return;
			showingLiar = false; ShowLiarHand = false;
			foreach (var die in liarDice) die.gameObject.SetActive(false);
			for (int i = 1; i < liarCups.Length; i++) liarCups[i].gameObject.SetActive(false);
			cup.localPosition = new Vector3(CupX, .025f, .1f); cup.gameObject.SetActive(true);
			boardRevision = uint.MaxValue;
		}
		private void ApplyLiar(DiceStateMsg state, long tick)
		{
			if (liarDice == null)
			{
				liarDice = new Transform[25]; liarCups = new Transform[5]; liarCups[0] = cup;
				for (int i = 0; i < liarDice.Length; i++)
					liarDice[i] = Sculpt(top, "Liar die", DiceVisuals.DieMesh(), Vector3.zero, materials[2]);
				for (int i = 1; i < liarCups.Length; i++)
				{
					liarCups[i] = Sculpt(top, "Crew dice cup", DiceVisuals.CupMesh(), Vector3.zero, materials[6]);
					Sculpt(liarCups[i], "Cup brass bands", DiceVisuals.CupBands(), Vector3.zero, materials[7]);
				}
			}
			showingLiar = true; presentingRoll = false;
			if (state.BoatIndex == BoatLocator.NoBoat) Root.transform.position = CoordSpace.RealToLocal(new Vector3(state.X, state.Y, state.Z));
			long elapsed = state.ElapsedMs + (state.Running ? Math.Max(0, tick - state.AnchorTick) : 0);
			float fold = Mathf.Clamp01((elapsed - state.PresentationAtMs) / (float) DiceGame.FoldDurationMs);
			float open = state.Phase == (byte) DicePhase.Unfolding ? fold : state.Phase == (byte) DicePhase.Folding ? 1 - fold : 1;
			top.localRotation = Quaternion.Euler((1 - open) * 85, 0, 0);
			for (int i = 0; i < legs.Length; i++) legs[i].localRotation = Quaternion.Euler((1 - open) * (i < 2 ? 85 : -85), 0, 0);
			foreach (var die in dice) die.gameObject.SetActive(false);
			int local = Array.FindIndex(state.Players, p => p.NetId == CoopBehaviour.Instance.Net.MyNetId);
			bool peek = ShowLiarHand && state.Phase == (byte) DicePhase.LiarBidding;
			for (int seat = 0; seat < 5; seat++)
			{
				bool reveal = state.LiarRevealed && state.Phase != (byte) DicePhase.Lobby && state.Phase != (byte) DicePhase.Unfolding;
				var c = liarCups[seat]; c.gameObject.SetActive(seat < Math.Max(1, state.Players.Length));
				c.localPosition = new Vector3(-.24f + seat * .12f, .025f + DiceVisuals.CupHeight + (reveal || peek && seat == local ? .055f : 0), -.205f);
				c.localRotation = Quaternion.Euler(180, 0, 0);
				for (int die = 0; die < 5; die++)
				{
					int index = seat * 5 + die; byte face = state.LiarHands[index]; var visual = liarDice[index];
					visual.gameObject.SetActive(face > 0 && (reveal || peek && seat == local));
					visual.localPosition = new Vector3(TrayX + (die - 2) * .07f, .047f, reveal ? -.11f + seat * .055f : 0);
					visual.localRotation = FaceRotation(face);
				}
				coins[seat].gameObject.SetActive(state.Stake > 0 && seat < state.Players.Length);
			}
			if (state.Revision != liarBoardRevision) ShowHistory = false;
			if (state.Revision == liarBoardRevision && liarHistory == ShowHistory) return;
			liarBoardRevision = state.Revision; liarHistory = ShowHistory;
			board.Page(ShowHistory ? LiarRules + "\n" + HistoryText : LiarBoard(state));
		}
		internal static string LiarBoard(DiceStateMsg state)
		{
			var text = new StringBuilder("LIAR'S DICE | round " + state.LiarRound + "\n");
			text.AppendLine(state.LiarQuantity == 0 ? "Raise a bid. Ones are not wild." : "Bid: " + state.LiarQuantity + " x " + state.LiarFace + " (" + state.Players[state.LiarSeat].Name + ")");
			if (state.Phase == (byte) DicePhase.Completed)
				text.AppendLine("Winner: " + state.Players.First(p => p.Scores[0] == 1).Name);
			else if (state.LiarRevealed)
				text.AppendLine("Found " + state.LiarActual + "; " + state.Players[state.LiarLoser].Name + " loses a die");
			else if (state.Phase == (byte) DicePhase.Paused) text.AppendLine("Paused - confirm ready to resume");
			else if (state.Phase == (byte) DicePhase.Lobby) text.AppendLine("Join, then click Ready to play");
			else text.AppendLine(state.Players.Length > state.Seat ? "Turn: " + state.Players[state.Seat].Name : "Waiting for crew");
			for (int i = 0; i < state.Players.Length; i++)
			{
				var p = state.Players[i];
				text.AppendLine((i + 1) + ". " + (p.Name.Length > 16 ? p.Name.Substring(0, 16) : p.Name) + " | " +
					(state.Phase == (byte) DicePhase.Lobby ? (p.Ready ? "ready" : "not ready") : state.DiceLeft[i] + " dice"));
			}
			text.Append(state.LiarRevealed ? "Read the reveal, then Next round" : "Hold P in close view: peek at your dice");
			return text.ToString();
		}
	}
}
