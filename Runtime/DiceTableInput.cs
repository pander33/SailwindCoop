using System;
using System.Collections.Generic;
using System.Linq;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	// Everything a player does at a dice table is done at the table: the cup, the three dice and the
	// score board each take the pick-up button and the use button. Only setting the table up starts
	// elsewhere, from the gesture wheel.
	public sealed class DiceTableInput
	{
		private struct Choice
		{
			public string Label;
			public DiceAction Action;
			public byte Mask;
			public bool Flag, Page, Closer;
		}
		private const float ReachMetres = 2f, ShorePresenceMetres = 12f;
		private const int StakeStep = 5, StakeLimit = 1000;
		private const int CupTarget = 0, BoardTarget = 4;
		private readonly DiceSync sync;
		private DiceTableView preview;
		private readonly DiceCloseUp closeUp = new DiceCloseUp();
		public DiceCloseUp CloseUp => closeUp;
		private bool placing, valid;
		private readonly Dictionary<uint, bool> presence = new Dictionary<uint, bool>();
		private float yaw;
		private ushort boatIndex;
		private Vector3 position;
		private string placementFailure;
		private readonly Collider[] overlaps = new Collider[128];
		private static readonly System.Reflection.FieldInfo currentBoat = HarmonyLib.AccessTools.Field(typeof(PlayerEmbarkerNew), "currentBoat");
		private PlayerEmbarkerNew embarker;
		private PlayerEmbarkerNew Embarker()
		{
			if (embarker == null) embarker = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
			return embarker;
		}
		public string Hint = "";
		public DiceTableInput(DiceSync sync) { this.sync = sync; }
		public void Clear() { closeUp.Drop(); preview?.Dispose(); preview = null; placing = false; Hint = ""; presence.Clear(); }
		public void Forget(DiceTable table) { presence.Remove(table.State.TableId); if (closeUp.Table == table) closeUp.Exit(); }
		/// <summary>Called by the gesture wheel sector "Dice table".</summary>
		public void BeginPlacement()
		{
			// Setting up a table again moves the one the player already has; the host decides the same way.
			var mine = MyTable();
			if (mine != null && mine.State.Phase >= (byte) DicePhase.AwaitRoll && mine.State.Phase <= (byte) DicePhase.RollingReroll) { CoopBehaviour.Notice("Your dice table is in use. Finish or cancel the party first."); return; }
			if (sync.Tables.Count(t => t != mine) >= DiceTablePlace.MaxTables) { CoopBehaviour.Notice("There are already " + DiceTablePlace.MaxTables + " dice tables in this world."); return; }
			preview?.Dispose(); preview = null; placing = true;
		}
		private DiceTable MyTable()
		{
			uint me = CoopBehaviour.Instance.Net.MyNetId;
			return sync.Tables.FirstOrDefault(t => t.State.OwnerNetId == me);
		}
		private static bool Within(DiceTable table, float metres)
		{
			var local = CoopBehaviour.Instance.Players.LocalPlayer;
			return local != null && table?.View != null && table.View.Root != null && Vector3.Distance(local.position, table.View.Root.transform.position) < metres;
		}
		public void ObservePresence()
		{
			closeUp.Tick();
			var coop = CoopBehaviour.Instance;
			bool available = !GameState.inBed && !GameState.sleeping;
			foreach (var table in sync.Tables.Where(t => t.State.Players.Any(p => p.NetId == coop.Net.MyNetId)))
			{
				// On shore there is no deck to stay on: a player who walked a few steps away is still at the table.
				bool here = available && (table.State.BoatIndex == BoatLocator.NoBoat ? Within(table, ShorePresenceMetres) : coop.Players.LocalBoatIndex == table.State.BoatIndex);
				if (!here && (!presence.TryGetValue(table.State.TableId, out bool previous) || previous)) sync.Request(table, DiceAction.Presence, flag: false);
				presence[table.State.TableId] = here;
			}
		}
		public bool Handle(GoPointer pointer)
		{
			var coop = CoopBehaviour.Instance;
			if (pointer.type != GoPointer.PointerType.crosshairMouse) return false;
			Hint = "";
			if (coop != null && closeUp.Active) return HandleCloseUp(coop);
			if (coop == null || coop.Net.Role == Role.None ||
				!GameState.playing || GameState.currentlyLoading || GameState.inBed || GameState.sleeping || GameState.inCursorMenu || BoatCamera.on ||
				pointer.GetHeldItem() != null || Time.timeScale <= 0) return false;
			var camera = Camera.main; if (camera == null) return false;
			var ray = new Ray(camera.transform.position, camera.transform.forward);
			if (placing)
			{
				if (Input.GetKeyDown(KeyCode.Escape) || pointer.AltButtonDown()) { Clear(); return true; }
				yaw += Input.mouseScrollDelta.y * 15;
				valid = Placement(ray, out Transform boat, out position, out boatIndex);
				if (preview != null) preview.Root.SetActive(valid);
				if (valid && preview == null)
				{
					preview = new DiceTableView(boat, new DiceStateMsg { X = position.x, Y = position.y, Z = position.z, Yaw = yaw });
					preview.Apply(new DiceStateMsg { Phase = (byte) DicePhase.Lobby }, 0);
					preview.Root.SetActive(valid);
				}
				if (preview != null && valid)
				{
					preview.Root.transform.SetParent(boat, false);
					preview.Root.transform.localPosition = boat != null ? position : CoordSpace.RealToLocal(position);
					preview.Root.transform.localRotation = Quaternion.Euler(0, yaw, 0);
				}
				Hint = valid ? (MyTable() != null ? "Move your dice table here" : "Set up the dice table") + "\nPick up: place    Wheel: turn    Use: cancel" : placementFailure + "\nUse: cancel";
				if (pointer.MainButtonDown() && valid) { sync.Request(null, DiceAction.Place, boat: boatIndex, position: position, yaw: yaw); Clear(); }
				return true;
			}
			DiceTable nearest = null; int target = -1; float distance = ReachMetres;
			foreach (var table in sync.Tables)
			{
				if (table.View == null) continue;
				int candidate = table.View.Target(ray, out float d);
				if (candidate >= 0 && d < distance) { nearest = table; target = candidate; distance = d; }
			}
			if (nearest == null) return false;
			Ray physicsRay = PhysicsRay(ray, nearest.Boat, out _);
			if (Physics.Raycast(physicsRay, out var obstacle, distance, -604165, QueryTriggerInteraction.Ignore) && obstacle.distance + .02f < distance) return false;
			if (Input.GetKeyDown(Plugin.Cfg.DiceViewKey.Value)) { closeUp.Enter(nearest); return true; }
			// A seated player who stepped back from the table comes close again at the cup, before anything else.
			bool closer = Plugin.Cfg.DiceCloseUp.Value && target == CupTarget && nearest.State.Players.Any(p => p.NetId == coop.Net.MyNetId);
			bool acted = Offer(nearest, target, "Pick up", "Use", pointer.MainButtonDown(), pointer.AltButtonDown(), closer);
			Hint += "\n" + Plugin.Cfg.DiceViewKey.Value + ": close view";
			return acted;
		}
		// The close view: the cursor is free, the left button stands for pick-up and the right one for use.
		private bool HandleCloseUp(CoopBehaviour coop)
		{
			var table = closeUp.Table;
			string leave = Plugin.Cfg.DiceViewKey.Value + ", a step aside or a right click beside the table: step back";
			if (table?.View == null || !closeUp.CursorRay(out Ray ray) || Time.timeScale <= 0) { Hint = leave; return true; }
			int target = table.View.Target(ray, out _);
			bool right = Input.GetMouseButtonDown(1);
			if (target < 0) { Hint = leave; if (right) closeUp.Exit(); return true; }
			Offer(table, target, "Left click", "Right click", Input.GetMouseButtonDown(0), right);
			Hint += "\n" + leave;
			return true;
		}
		private bool Offer(DiceTable nearest, int target, string mainName, string altName, bool mainDown, bool altDown, bool closer = false)
		{
			uint me = CoopBehaviour.Instance.Net.MyNetId;
			bool main = Choose(nearest, target, false, me, out Choice primary), alt = Choose(nearest, target, true, me, out Choice secondary);
			if (closer) { main = true; primary = new Choice { Label = "move closer to the table", Closer = true }; }
			Hint = (target == CupTarget ? "Dice cup" : target == BoardTarget ? "Score board" : "Die") +
				(main ? "\n" + mainName + ": " + primary.Label : "") + (alt ? "\n" + altName + ": " + secondary.Label : "");
			var state = nearest.State;
			if (target == BoardTarget && state.Phase == (byte) DicePhase.Lobby && state.StakesAllowed && state.Players.Any(p => p.NetId == me))
			{
				Hint += "\nWheel: stake, now " + (state.Stake > 0 ? state.Stake + " " + DiceTableView.CurrencyName(state.StakeCurrency) + " each" : "none");
				int notches = Mathf.RoundToInt(Input.mouseScrollDelta.y);
				int stake = Mathf.Clamp(state.Stake + notches * StakeStep, 0, StakeLimit);
				if (notches != 0 && stake != state.Stake)
					sync.Request(nearest, DiceAction.SetStake, mask: (byte) (state.Stake > 0 ? state.StakeCurrency : TradeCurrency()), stake: (ushort) stake);
			}
			if (mainDown) { if (main) Do(nearest, primary); return true; }
			if (altDown && alt) { Do(nearest, secondary); return true; }
			return false;
		}
		// What the pick-up button (or the use button) does on this part of the table right now. The host
		// does not check turns or seats, so this is the only place that decides what a player is offered.
		private static bool Choose(DiceTable table, int target, bool alt, uint me, out Choice choice)
		{
			choice = new Choice { Flag = true };
			var state = table.State; var phase = (DicePhase) state.Phase;
			int seat = Array.FindIndex(state.Players, p => p.NetId == me);
			bool seated = seat >= 0, turn = seated && state.Seat == seat;
			bool playing = phase >= DicePhase.AwaitRoll && phase <= DicePhase.RollingReroll, over = phase == DicePhase.Completed || phase == DicePhase.Cancelled;
			if (target == CupTarget && !alt)
			{
				if (phase == DicePhase.Lobby && !seated && state.Players.Length < DiceGame.MaxSeats) return Set(ref choice, "sit down", DiceAction.Join);
				if (phase == DicePhase.Lobby && seated) return Set(ref choice, state.Players[seat].Ready ? "not ready after all" : "ready", DiceAction.Ready, flag: !state.Players[seat].Ready);
				if (phase == DicePhase.AwaitRoll && turn) return Set(ref choice, "roll", DiceAction.Roll);
				if (phase == DicePhase.Selecting && turn && !state.Rerolled && state.HeldMask != 7) return Set(ref choice, "reroll the dice left in the tray", DiceAction.Roll);
				if (phase == DicePhase.Paused && seated) return Set(ref choice, state.Players[seat].ResumeReady ? "not ready after all" : "ready to go on", DiceAction.ResumeReady, flag: !state.Players[seat].ResumeReady);
				// A passer-by does not wipe the final score of others.
				if (over && (seated || state.Players.Length == 0 || state.OwnerNetId == me)) return Set(ref choice, "play again", DiceAction.Rematch);
			}
			else if (target == CupTarget)
			{
				if (phase == DicePhase.Lobby && seated) return Set(ref choice, "leave the table", DiceAction.Leave);
				if (phase == DicePhase.Paused && seated) return Set(ref choice, "leave (cancels the party)", DiceAction.Leave);
			}
			else if (target == BoardTarget && !alt)
			{
				if (phase == DicePhase.Lobby && seated && state.Players.Length >= 2 && state.Players.All(p => p.Ready && p.Connected)) return Set(ref choice, "start the party", DiceAction.Start);
				if (phase == DicePhase.Selecting && turn)
					return Set(ref choice, "take " + DiceGame.Score(state.Faces[0], state.Faces[1], state.Faces[2]) + " points", DiceAction.Bank);
				choice.Page = true; choice.Label = table.View != null && table.View.ShowHistory ? "back to the score" : "rules and past parties";
				return true;
			}
			else if (target == BoardTarget)
			{
				// Only the owner folds the table away. A paused party goes with it; a running one is paused first.
				if (state.OwnerNetId == me && (phase == DicePhase.Lobby || over || phase == DicePhase.Paused))
					return Set(ref choice, phase == DicePhase.Paused ? "fold the table away (ends the party)" : "fold the table away", DiceAction.Remove);
				if (playing && seated) return Set(ref choice, "pause the party", DiceAction.Pause);
				if (phase == DicePhase.Paused && seated && !state.Players[seat].CancelVote) return Set(ref choice, "vote to cancel the party", DiceAction.CancelVote);
			}
			else if (!alt && phase == DicePhase.Selecting && turn)
			{
				byte bit = (byte) (1 << (target - 1));
				return Set(ref choice, (state.HeldMask & bit) != 0 ? "put back into the tray" : "keep", DiceAction.SetHeldMask, (byte) (state.HeldMask ^ bit));
			}
			return false;
		}
		private static bool Set(ref Choice choice, string label, DiceAction action, byte mask = 0, bool flag = true)
		{
			choice.Label = label; choice.Action = action; choice.Mask = mask; choice.Flag = flag;
			return true;
		}
		private void Do(DiceTable table, Choice choice)
		{
			if (choice.Closer) { closeUp.Enter(table); return; }
			if (choice.Page)
			{
				if (table.View == null) return;
				table.View.HistoryText = sync.JournalText; table.View.ShowHistory = !table.View.ShowHistory;
				return;
			}
			// The free hand of the avatar goes to the cup, so the others see who rolls.
			if (choice.Action == DiceAction.Roll && table.View != null)
				CoopBehaviour.Instance.Players.ReachFor(table.View.Cup, Vector3.up * .08f, 1.2f);
			sync.Request(table, choice.Action, choice.Mask, choice.Flag);
			// Sitting down brings the table close; getting up steps back from it.
			if (choice.Action == DiceAction.Join && Plugin.Cfg.DiceCloseUp.Value) closeUp.Enter(table);
			if (choice.Action == DiceAction.Leave) closeUp.Exit();
		}
		/// <summary>The currency of the region the player trades in; the stake is named in it.</summary>
		private static int TradeCurrency()
		{
			int current = 0;
			try { current = (int) GameState.currentCurrency; } catch { }
			return current >= 0 && current < WalletSync.CurrencyCount ? current : 0;
		}
		private bool Placement(Ray ray, out Transform boat, out Vector3 local, out ushort index)
		{
			placementFailure = "Look down at a supported surface within 3 metres.";
			var embarker = Embarker();
			boat = embarker != null ? embarker.debugOutCurrentBoat : null;
			if (boat == null) boat = CoopBehaviour.Instance.Players.ResolveLocalBoatNow();
			local = default; index = BoatLocator.NoBoat;
			Ray physicsRay = PhysicsRay(ray, boat, out Transform physicsBoat);
			bool found = Physics.Raycast(physicsRay, out var hit, 3f, -604165, QueryTriggerInteraction.Ignore);
			bool onBoat = found && physicsBoat != null && hit.collider.transform.IsChildOf(physicsBoat);
			if (!onBoat)
			{
				boat = physicsBoat = null;
				if (!Physics.Raycast(ray, out hit, 3f, -604165, QueryTriggerInteraction.Ignore)) return false;
			}
			if (onBoat && (index = BoatLocator.IndexOf(boat)) == BoatLocator.NoBoat)
			{
				placementFailure = "A dice table cannot be set up on this boat.";
				return false;
			}
			ushort placeBoat = index; var mine = MyTable();
			if (onBoat && sync.Tables.Any(t => t != mine && t.State.BoatIndex == placeBoat))
			{
				placementFailure = "This boat already has a dice table.";
				return false;
			}
			local = onBoat ? physicsBoat.InverseTransformPoint(hit.point) : CoordSpace.LocalToReal(hit.point);
			var up = onBoat ? physicsBoat.up : Vector3.up;
			placementFailure = "Choose a level area away from items and controls.";
			if (Vector3.Dot(hit.normal, up) < .94f || hit.collider.GetComponentInParent<ShipItem>() != null || hit.collider.GetComponentInParent<GoPointerButton>() != null) return false;
			var rotation = (onBoat ? physicsBoat.rotation : Quaternion.identity) * Quaternion.Euler(0, yaw, 0);
			placementFailure = "All four table legs need level support.";
			for (int i = 0; i < 4; i++)
			{
				Vector3 foot = hit.point + rotation * new Vector3((i % 2 == 0 ? -1 : 1) * .28f, .1f, (i < 2 ? -1 : 1) * .18f);
				if (!Physics.Raycast(foot, -up, out var support, .18f, -604165, QueryTriggerInteraction.Ignore) ||
					(onBoat && !support.collider.transform.IsChildOf(physicsBoat)) || Mathf.Abs(Vector3.Dot(support.point - hit.point, up)) > .035f || support.collider.GetComponentInParent<GoPointerButton>() != null || support.collider.GetComponentInParent<ShipItem>() != null) return false;
			}
			int count = Physics.OverlapBoxNonAlloc(hit.point + up * .3f, new Vector3(.4f, .24f, .36f), overlaps, rotation, -604165, QueryTriggerInteraction.Ignore);
			placementFailure = "Clear items and controls from the table area.";
			if (count == overlaps.Length) return false;
			for (int i = 0; i < count; i++)
				if (overlaps[i].GetComponentInParent<GoPointerButton>() != null || overlaps[i].GetComponentInParent<ShipItem>() != null) return false;
			return true;
		}
		private Ray PhysicsRay(Ray ray, Transform boat, out Transform physicsBoat)
		{
			physicsBoat = boat;
			if (boat == null) return ray;
			var embarker = Embarker();
			var embark = embarker != null ? currentBoat.GetValue(embarker) as EmbarkBoat : null;
			if (embark == null || embark.worldBoat != boat || embark.walkCol == null) return ray;
			physicsBoat = embark.walkCol;
			return new Ray(physicsBoat.TransformPoint(boat.InverseTransformPoint(ray.origin)),
				physicsBoat.TransformDirection(boat.InverseTransformDirection(ray.direction)));
		}
		public void DrawHint()
		{
			if (string.IsNullOrEmpty(Hint)) return;
			// In the close view the hint follows the cursor, as it follows the crosshair otherwise.
			if (closeUp.Active)
				GUI.Label(new Rect(Mathf.Clamp(UnityEngine.Input.mousePosition.x + 24, 0, Screen.width - 480), Mathf.Clamp(Screen.height - UnityEngine.Input.mousePosition.y + 20, 0, Screen.height - 130), 480, 130), Hint);
			else if (!GameState.inCursorMenu)
				GUI.Label(new Rect(Screen.width / 2f - 240, Screen.height / 2f + 35, 480, 130), Hint);
		}
	}
}
