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
	// elsewhere, from the gesture wheel. In close view, players have dedicated hotkeys and a bottom
	// HUD action bar for smooth, rapid play.
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

		// Reusable textures and styles for translucent nautical HUD & tooltips
		private static Texture2D _shadowTex, _borderTex, _panelTex, _btnTex, _btnHoverTex, _btnActiveTex;
		private static Texture2D _accentTex, _accentHoverTex, _heldTex, _heldHoverTex;
		private static GUIStyle _hintTitleStyle, _hintBodyStyle, _statusStyle, _statusBoldStyle;
		private static GUIStyle _btnStyle, _accentBtnStyle, _heldBtnStyle;
		private static bool _stylesReady;

		public DiceTableInput(DiceSync sync) { this.sync = sync; }

		public void Clear() { closeUp.Drop(); preview?.Dispose(); preview = null; placing = false; Hint = ""; presence.Clear(); }

		public void Forget(DiceTable table) { presence.Remove(table.State.TableId); if (closeUp.Table == table) closeUp.Exit(); }

		/// <summary>Called by the gesture wheel sector "Dice table".</summary>
		public void BeginPlacement()
		{
			// Setting up a table again moves the one the player already has; the host decides the same way.
			var mine = MyTable();
			if (mine != null && mine.State.Phase >= (byte) DicePhase.AwaitRoll && mine.State.Phase <= (byte) DicePhase.RollingReroll)
			{
				CoopBehaviour.Notice("Your dice table is in use. Finish or cancel the party first.");
				return;
			}
			if (sync.Tables.Count(t => t != mine) >= DiceTablePlace.MaxTables)
			{
				CoopBehaviour.Notice("There are already " + DiceTablePlace.MaxTables + " dice tables in this world.");
				return;
			}
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
			foreach (var table in sync.Tables)
			{
				bool visible = available && !placing && GameState.playing && !GameState.currentlyLoading &&
					!coop.CoopMenuOpen && !BoatCamera.on && Time.timeScale > 0 &&
					(closeUp.Active ? closeUp.Table == table : !GameState.inCursorMenu && Within(table, 2.5f));
				table.View?.ShowActionCues(visible && SuggestAction(table, CupTarget, coop.Net.MyNetId),
					visible && SuggestAction(table, BoardTarget, coop.Net.MyNetId),
					visible && SuggestAction(table, DiceTableView.TableTarget, coop.Net.MyNetId));
			}
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

		// The close view: mouse pointer is free, hotkeys allow quick rolling and holding, and the bottom HUD provides clear actions.
		private bool HandleCloseUp(CoopBehaviour coop)
		{
			var table = closeUp.Table;
			if (table?.View == null || Time.timeScale <= 0) return true;

			uint me = coop.Net.MyNetId;
			var state = table.State;
			var phase = (DicePhase) state.Phase;
			int seat = Array.FindIndex(state.Players, p => p.NetId == me);
			bool seated = seat >= 0, turn = seated && state.Seat == seat;

			// Keyboard shortcuts for seamless rapid play
			if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.R))
			{
				if (phase == DicePhase.AwaitRoll && turn)
				{
					Do(table, new Choice { Action = DiceAction.Roll });
					return true;
				}
				if (phase == DicePhase.Selecting && turn && !state.Rerolled && state.HeldMask != 7)
				{
					Do(table, new Choice { Action = DiceAction.Roll });
					return true;
				}
				if (phase == DicePhase.Lobby)
				{
					if (seated)
					{
						bool ready = !state.Players[seat].Ready;
						Do(table, new Choice { Action = DiceAction.Ready, Flag = ready });
						return true;
					}
					if (state.Players.Length < DiceGame.MaxSeats)
					{
						Do(table, new Choice { Action = DiceAction.Join });
						return true;
					}
				}
				if (phase == DicePhase.Completed || phase == DicePhase.Cancelled)
				{
					if (seated || state.Players.Length == 0 || state.OwnerNetId == me)
					{
						Do(table, new Choice { Action = DiceAction.Rematch });
						return true;
					}
				}
			}

			if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E))
			{
				if (phase == DicePhase.Selecting && turn)
				{
					Do(table, new Choice { Action = DiceAction.Bank });
					return true;
				}
				if (phase == DicePhase.Lobby && seated && state.Players.Length >= 2 && state.Players.All(p => p.Ready && p.Connected))
				{
					Do(table, new Choice { Action = DiceAction.Start });
					return true;
				}
			}

			if (phase == DicePhase.Selecting && turn)
			{
				if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
				{
					Do(table, new Choice { Action = DiceAction.SetHeldMask, Mask = (byte) (state.HeldMask ^ 1) });
					return true;
				}
				if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
				{
					Do(table, new Choice { Action = DiceAction.SetHeldMask, Mask = (byte) (state.HeldMask ^ 2) });
					return true;
				}
				if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
				{
					Do(table, new Choice { Action = DiceAction.SetHeldMask, Mask = (byte) (state.HeldMask ^ 4) });
					return true;
				}
			}

			if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.H))
			{
				table.View.HistoryText = sync.JournalText;
				table.View.ShowHistory = !table.View.ShowHistory;
				try { UISoundPlayer.instance?.PlayParchmentSound(); } catch { }
				return true;
			}

			// In lobby, mouse wheel adjusts stake anywhere in close view
			if (phase == DicePhase.Lobby && state.StakesAllowed && seated && Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f)
			{
				int notches = Mathf.RoundToInt(Input.mouseScrollDelta.y);
				int stake = Mathf.Clamp(state.Stake + notches * StakeStep, 0, StakeLimit);
				if (notches != 0 && stake != state.Stake)
				{
					sync.Request(table, DiceAction.SetStake, mask: (byte) (state.Stake > 0 ? state.StakeCurrency : TradeCurrency()), stake: (ushort) stake);
					PlayFeedbackClick();
				}
			}

			// If cursor is over the bottom HUD action bar, suppress 3D raycasting so clicks don't hit background items
			if (Input.mousePosition.y < 64f)
			{
				Hint = "";
				return true;
			}

			if (!closeUp.CursorRay(out Ray ray)) { Hint = ""; return true; }
			int target = table.View.Target(ray, out _);
			bool left = Input.GetMouseButtonDown(0);
			bool right = Input.GetMouseButtonDown(1);

			// Accidental right-click outside targets does NOT kick the player out; exit is intentional via Esc, T, or HUD button.
			if (target < 0)
			{
				Hint = "";
				return true;
			}

			Offer(table, target, "Left click", "Right click", left, right);
			return true;
		}

		private bool Offer(DiceTable nearest, int target, string mainName, string altName, bool mainDown, bool altDown, bool closer = false)
		{
			uint me = CoopBehaviour.Instance.Net.MyNetId;
			bool main = Choose(nearest, target, false, me, out Choice primary), alt = Choose(nearest, target, true, me, out Choice secondary);
			if (closer) { main = true; primary = new Choice { Label = "move closer to the table", Closer = true }; }

			string header = "Dice Table";
			if (main && primary.Action == DiceAction.Join && !primary.Closer) header = "Dice Table — " + nearest.State.Players.Length + "/" + DiceGame.MaxSeats + " players";
			else if (target == CupTarget) header = "Dice Cup";
			else if (target == BoardTarget) header = "Score Board";
			else if (target >= 1 && target <= 3)
			{
				int dIdx = target - 1;
				byte val = nearest.State.Faces != null && dIdx < nearest.State.Faces.Length ? nearest.State.Faces[dIdx] : (byte) 0;
				bool held = (nearest.State.HeldMask & (1 << dIdx)) != 0;
				header = "Die #" + target + (val > 0 ? " [" + val + "]" : "") + (held ? " (Kept)" : " (In tray)");
			}

			Hint = header + (main ? "\n" + mainName + ": " + primary.Label : "") + (alt ? "\n" + altName + ": " + secondary.Label : "");
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
			// Also queried every frame for the glints; avoid a captured predicate per target.
			int seat = -1;
			for (int i = 0; i < state.Players.Length; i++)
				if (state.Players[i].NetId == me) { seat = i; break; }
			bool seated = seat >= 0, turn = seated && state.Seat == seat;
			bool playing = phase >= DicePhase.AwaitRoll && phase <= DicePhase.RollingReroll, over = phase == DicePhase.Completed || phase == DicePhase.Cancelled;
			// Joining belongs to the table, regardless of which piece caught the player's eye.
			if (!alt && phase == DicePhase.Lobby && !seated && state.Players.Length < DiceGame.MaxSeats)
				return Set(ref choice, "Join game", DiceAction.Join);
			if (target == DiceTableView.TableTarget)
			{
				if (alt) return false;
				choice.Closer = true; choice.Label = seated ? "return to the game" : "watch the game";
				return true;
			}
			if (target == CupTarget && !alt)
			{
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

		private static bool SuggestAction(DiceTable table, int target, uint me)
		{
			if (!Choose(table, target, false, me, out Choice choice) || choice.Page || choice.Closer) return false;
			switch (choice.Action)
			{
				case DiceAction.Join: return target == DiceTableView.TableTarget;
				case DiceAction.Ready:
				case DiceAction.ResumeReady: return choice.Flag;
				case DiceAction.Roll:
				case DiceAction.Start:
				case DiceAction.Bank:
				case DiceAction.Rematch: return true;
				default: return false;
			}
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
				try { UISoundPlayer.instance?.PlayParchmentSound(); } catch { }
				return;
			}
			// The free hand of the avatar goes to the cup, so the others see who rolls.
			if (choice.Action == DiceAction.Roll && table.View != null)
				CoopBehaviour.Instance.Players.ReachFor(table.View.Cup, Vector3.up * .08f, 1.2f);

			if (choice.Action == DiceAction.SetHeldMask)
				PlayDieToggleSound();
			else
				PlayFeedbackClick();

			sync.Request(table, choice.Action, choice.Mask, choice.Flag);
			// Sitting down brings the table close; getting up steps back from it.
			if (choice.Action == DiceAction.Join && Plugin.Cfg.DiceCloseUp.Value) closeUp.Enter(table);
			if (choice.Action == DiceAction.Leave) closeUp.Exit();
		}

		private static void PlayFeedbackClick()
		{
			try { UISoundPlayer.instance?.PlayUIClickSound(); } catch { }
		}

		private static void PlayDieToggleSound()
		{
			try
			{
				if (UISoundPlayer.instance != null)
					UISoundPlayer.instance.PlaySmallItemDropSound();
			}
			catch { }
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
			if (Vector3.Dot(hit.normal, up) < .90f || hit.collider.GetComponentInParent<ShipItem>() != null || hit.collider.GetComponentInParent<GoPointerButton>() != null) return false;
			var rotation = (onBoat ? physicsBoat.rotation : Quaternion.identity) * Quaternion.Euler(0, yaw, 0);
			placementFailure = "All four table legs need level support.";
			for (int i = 0; i < 4; i++)
			{
				Vector3 foot = hit.point + rotation * new Vector3((i % 2 == 0 ? -1 : 1) * .28f, .12f, (i < 2 ? -1 : 1) * .18f);
				if (!Physics.Raycast(foot, -up, out var support, .24f, -604165, QueryTriggerInteraction.Ignore) ||
					(onBoat && !support.collider.transform.IsChildOf(physicsBoat)) || Mathf.Abs(Vector3.Dot(support.point - hit.point, up)) > .065f || support.collider.GetComponentInParent<GoPointerButton>() != null || support.collider.GetComponentInParent<ShipItem>() != null) return false;
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

		private static Texture2D MakeSolidTex(Color color)
		{
			var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
			tex.SetPixel(0, 0, color);
			tex.Apply();
			return tex;
		}

		private static void EnsureStyles()
		{
			if (_stylesReady && _panelTex != null) return;

			_shadowTex = MakeSolidTex(new Color(0f, 0f, 0f, 0.52f));
			_borderTex = MakeSolidTex(new Color(0.58f, 0.46f, 0.28f, 0.88f));
			_panelTex = MakeSolidTex(new Color(0.065f, 0.055f, 0.045f, 0.94f));
			_btnTex = MakeSolidTex(new Color(0.20f, 0.16f, 0.12f, 0.96f));
			_btnHoverTex = MakeSolidTex(new Color(0.32f, 0.25f, 0.18f, 0.98f));
			_btnActiveTex = MakeSolidTex(new Color(0.42f, 0.32f, 0.20f, 1f));
			_accentTex = MakeSolidTex(new Color(0.52f, 0.38f, 0.16f, 0.96f));
			_accentHoverTex = MakeSolidTex(new Color(0.66f, 0.50f, 0.22f, 0.98f));
			_heldTex = MakeSolidTex(new Color(0.18f, 0.30f, 0.18f, 0.96f));
			_heldHoverTex = MakeSolidTex(new Color(0.26f, 0.40f, 0.24f, 0.98f));

			_hintTitleStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 13,
				fontStyle = FontStyle.Bold,
				normal = { textColor = new Color(0.98f, 0.82f, 0.42f) },
				alignment = TextAnchor.MiddleLeft
			};

			_hintBodyStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 12,
				fontStyle = FontStyle.Normal,
				wordWrap = true,
				normal = { textColor = new Color(0.92f, 0.88f, 0.80f) },
				alignment = TextAnchor.MiddleLeft
			};

			_statusStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 13,
				fontStyle = FontStyle.Normal,
				alignment = TextAnchor.MiddleLeft,
				normal = { textColor = new Color(0.88f, 0.85f, 0.78f) }
			};

			_statusBoldStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 13,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleLeft,
				normal = { textColor = new Color(1f, 0.86f, 0.45f) }
			};

			_btnStyle = new GUIStyle(GUI.skin.button)
			{
				fontSize = 12,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter,
				normal = { background = _btnTex, textColor = new Color(0.92f, 0.88f, 0.80f) },
				hover = { background = _btnHoverTex, textColor = Color.white },
				active = { background = _btnActiveTex, textColor = Color.white },
				border = new RectOffset(0, 0, 0, 0)
			};

			_accentBtnStyle = new GUIStyle(_btnStyle)
			{
				normal = { background = _accentTex, textColor = Color.white },
				hover = { background = _accentHoverTex, textColor = Color.white }
			};

			_heldBtnStyle = new GUIStyle(_btnStyle)
			{
				normal = { background = _heldTex, textColor = new Color(0.92f, 1f, 0.92f) },
				hover = { background = _heldHoverTex, textColor = Color.white }
			};

			_stylesReady = true;
		}

		private static void DrawPanel(Rect rect, Texture2D bg, Texture2D border, Texture2D shadow)
		{
			if (shadow != null)
				GUI.DrawTexture(new Rect(rect.x + 3f, rect.y + 3f, rect.width, rect.height), shadow, ScaleMode.StretchToFill);
			if (border != null)
				GUI.DrawTexture(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), border, ScaleMode.StretchToFill);
			if (bg != null)
				GUI.DrawTexture(rect, bg, ScaleMode.StretchToFill);
		}

		private static void DrawTooltip(Vector2 pos, string text)
		{
			if (string.IsNullOrEmpty(text)) return;
			string[] lines = text.Split('\n');
			string title = lines[0];
			string body = lines.Length > 1 ? string.Join("\n", lines.Skip(1)) : "";

			float titleWidth = _hintTitleStyle.CalcSize(new GUIContent(title)).x;
			float bodyWidth = string.IsNullOrEmpty(body) ? 0 : _hintBodyStyle.CalcSize(new GUIContent(body)).x;
			float w = Mathf.Clamp(Mathf.Max(titleWidth, bodyWidth) + 24f, 180f, 380f);

			float titleH = _hintTitleStyle.CalcHeight(new GUIContent(title), w - 24f);
			float bodyH = string.IsNullOrEmpty(body) ? 0 : _hintBodyStyle.CalcHeight(new GUIContent(body), w - 24f);
			float h = titleH + (bodyH > 0 ? bodyH + 6f : 0f) + 14f;

			float x = Mathf.Clamp(pos.x, 10f, Screen.width - w - 10f);
			float y = Mathf.Clamp(pos.y, 10f, Screen.height - h - 68f);
			var rect = new Rect(x, y, w, h);

			DrawPanel(rect, _panelTex, _borderTex, _shadowTex);
			GUI.Label(new Rect(rect.x + 12f, rect.y + 7f, w - 24f, titleH), title, _hintTitleStyle);
			if (bodyH > 0)
				GUI.Label(new Rect(rect.x + 12f, rect.y + 7f + titleH + 3f, w - 24f, bodyH), body, _hintBodyStyle);
		}

		private static void DrawCrosshairHint(string text)
		{
			if (string.IsNullOrEmpty(text)) return;
			string[] lines = text.Split('\n');
			string title = lines[0];
			string body = lines.Length > 1 ? string.Join("\n", lines.Skip(1)) : "";

			float titleWidth = _hintTitleStyle.CalcSize(new GUIContent(title)).x;
			float bodyWidth = string.IsNullOrEmpty(body) ? 0 : _hintBodyStyle.CalcSize(new GUIContent(body)).x;
			float w = Mathf.Clamp(Mathf.Max(titleWidth, bodyWidth) + 28f, 220f, 400f);

			float titleH = _hintTitleStyle.CalcHeight(new GUIContent(title), w - 28f);
			float bodyH = string.IsNullOrEmpty(body) ? 0 : _hintBodyStyle.CalcHeight(new GUIContent(body), w - 28f);
			float h = titleH + (bodyH > 0 ? bodyH + 6f : 0f) + 16f;

			float x = (Screen.width - w) * 0.5f;
			float y = Screen.height * 0.5f + 32f;
			var rect = new Rect(x, y, w, h);

			DrawPanel(rect, _panelTex, _borderTex, _shadowTex);
			GUI.Label(new Rect(rect.x + 14f, rect.y + 8f, w - 28f, titleH), title, _hintTitleStyle);
			if (bodyH > 0)
				GUI.Label(new Rect(rect.x + 14f, rect.y + 8f + titleH + 3f, w - 28f, bodyH), body, _hintBodyStyle);
		}

		public void DrawHint()
		{
			EnsureStyles();

			if (closeUp.Active && closeUp.Table != null)
				DrawCloseUpHUD(closeUp.Table);

			if (string.IsNullOrEmpty(Hint)) return;

			if (closeUp.Active)
				DrawTooltip(new Vector2(Input.mousePosition.x + 20, Screen.height - Input.mousePosition.y + 20), Hint);
			else if (!GameState.inCursorMenu)
				DrawCrosshairHint(Hint);
		}

		private void DrawCloseUpHUD(DiceTable table)
		{
			float barW = Mathf.Clamp(Screen.width * 0.84f, 680f, 1020f);
			float barH = 46f;
			float barX = (Screen.width - barW) * 0.5f;
			float barY = Screen.height - 54f;
			var barRect = new Rect(barX, barY, barW, barH);

			DrawPanel(barRect, _panelTex, _borderTex, _shadowTex);

			uint me = CoopBehaviour.Instance.Net.MyNetId;
			var state = table.State;
			var phase = (DicePhase) state.Phase;
			int seat = Array.FindIndex(state.Players, p => p.NetId == me);
			bool seated = seat >= 0, turn = seated && state.Seat == seat;

			float btnY = barY + 8f;
			float btnH = 30f;
			float curX = barX + barW - 12f;

			bool DrawBtn(string label, float width, GUIStyle style, Action onClick)
			{
				curX -= width;
				var r = new Rect(curX, btnY, width, btnH);
				curX -= 6f;
				if (GUI.Button(r, label, style))
				{
					onClick();
					return true;
				}
				return false;
			}

			// Rightmost fixed button: Step back
			DrawBtn("[Esc] Step back", 102f, _btnStyle, () => closeUp.Exit());

			// Slate rules / score history toggle
			DrawBtn("[Tab] Slate", 82f, _btnStyle, () =>
			{
				if (table.View != null)
				{
					table.View.HistoryText = sync.JournalText;
					table.View.ShowHistory = !table.View.ShowHistory;
					try { UISoundPlayer.instance?.PlayParchmentSound(); } catch { }
				}
			});

			string statusText = "";

			if (phase == DicePhase.Selecting)
			{
				byte f1 = (state.Faces != null && state.Faces.Length > 0) ? state.Faces[0] : (byte) 1;
				byte f2 = (state.Faces != null && state.Faces.Length > 1) ? state.Faces[1] : (byte) 1;
				byte f3 = (state.Faces != null && state.Faces.Length > 2) ? state.Faces[2] : (byte) 1;
				int pts = DiceGame.Score(f1, f2, f3);

				string bonus = "";
				if (f1 == f2 && f2 == f3) bonus = " (Triple +12!)";
				else if (f1 == f2 || f2 == f3 || f1 == f3) bonus = " (Pair +4!)";

				if (turn)
				{
					statusText = pts + " pts" + bonus + (state.Rerolled ? " [Final roll]" : "");

					DrawBtn("[Enter] Bank " + pts, 118f, _accentBtnStyle, () => Do(table, new Choice { Action = DiceAction.Bank }));

					if (!state.Rerolled && state.HeldMask != 7)
						DrawBtn("[Space] Reroll", 106f, _accentBtnStyle, () => Do(table, new Choice { Action = DiceAction.Roll }));

					bool held3 = (state.HeldMask & 4) != 0;
					DrawBtn("[3] " + f3 + (held3 ? " [Kept]" : " [Tray]"), 88f, held3 ? _heldBtnStyle : _btnStyle, () =>
						Do(table, new Choice { Action = DiceAction.SetHeldMask, Mask = (byte) (state.HeldMask ^ 4) }));

					bool held2 = (state.HeldMask & 2) != 0;
					DrawBtn("[2] " + f2 + (held2 ? " [Kept]" : " [Tray]"), 88f, held2 ? _heldBtnStyle : _btnStyle, () =>
						Do(table, new Choice { Action = DiceAction.SetHeldMask, Mask = (byte) (state.HeldMask ^ 2) }));

					bool held1 = (state.HeldMask & 1) != 0;
					DrawBtn("[1] " + f1 + (held1 ? " [Kept]" : " [Tray]"), 88f, held1 ? _heldBtnStyle : _btnStyle, () =>
						Do(table, new Choice { Action = DiceAction.SetHeldMask, Mask = (byte) (state.HeldMask ^ 1) }));
				}
				else
				{
					string pName = (state.Players != null && state.Seat < state.Players.Length) ? state.Players[state.Seat].Name : "Player";
					statusText = pName + " choosing dice: " + pts + " pts" + bonus;
				}
			}
			else if (phase == DicePhase.AwaitRoll)
			{
				if (turn)
				{
					statusText = "Your turn — Round " + (state.Round + 1) + "/3";
					DrawBtn("[Space] Roll Cup", 124f, _accentBtnStyle, () => Do(table, new Choice { Action = DiceAction.Roll }));
				}
				else
				{
					string pName = (state.Players != null && state.Seat < state.Players.Length) ? state.Players[state.Seat].Name : "Player";
					statusText = pName + "'s turn — Round " + (state.Round + 1) + "/3";
				}
			}
			else if (phase == DicePhase.RollingInitial || phase == DicePhase.RollingReroll)
			{
				statusText = "Rolling dice in cup...";
			}
			else if (phase == DicePhase.Lobby)
			{
				statusText = "Lobby • " + state.Players.Length + "/" + DiceGame.MaxSeats + " seated" +
					(state.Stake > 0 ? " • Stake: " + state.Stake + " " + DiceTableView.CurrencyName(state.StakeCurrency) : "");

				if (seated)
				{
					if (state.Players.Length >= 2 && state.Players.All(p => p.Ready && p.Connected))
						DrawBtn("[Enter] Start Party", 126f, _accentBtnStyle, () => Do(table, new Choice { Action = DiceAction.Start }));

					bool ready = state.Players[seat].Ready;
					DrawBtn(ready ? "[Space] Not Ready" : "[Space] Ready", 120f, ready ? _btnStyle : _accentBtnStyle, () =>
						Do(table, new Choice { Action = DiceAction.Ready, Flag = !ready }));

					DrawBtn("Leave seat", 84f, _btnStyle, () => Do(table, new Choice { Action = DiceAction.Leave }));
				}
				else if (state.Players.Length < DiceGame.MaxSeats)
				{
					DrawBtn("[Space] Join game", 128f, _accentBtnStyle, () => Do(table, new Choice { Action = DiceAction.Join }));
				}
			}
			else if (phase == DicePhase.Completed || phase == DicePhase.Cancelled)
			{
				statusText = phase == DicePhase.Completed ? "Party completed!" : "Party cancelled";
				if (seated || state.Players.Length == 0 || state.OwnerNetId == me)
					DrawBtn("[Space] Play Again", 124f, _accentBtnStyle, () => Do(table, new Choice { Action = DiceAction.Rematch }));
			}
			else if (phase == DicePhase.Paused)
			{
				statusText = "Party paused";
				if (seated)
				{
					bool resume = state.Players[seat].ResumeReady;
					DrawBtn(resume ? "Wait for crew" : "Resume Ready", 120f, resume ? _btnStyle : _accentBtnStyle, () =>
						Do(table, new Choice { Action = DiceAction.ResumeReady, Flag = !resume }));
				}
			}

			// Render status text in the remaining left space of the action bar
			float statusW = curX - (barX + 14f);
			if (statusW > 40f)
				GUI.Label(new Rect(barX + 14f, btnY, statusW, btnH), statusText, _statusBoldStyle);
		}
	}
}
