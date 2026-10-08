using System;
using System.Collections.Generic;
using System.Linq;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	public sealed class DiceTableView : IDisposable
	{
		public readonly GameObject Root;
		private readonly Transform top, cup;
		private readonly Transform[] dice = new Transform[3], legs = new Transform[4], coins = new Transform[DiceGame.MaxSeats];
		private readonly NetTransform[] poses = new NetTransform[3];
		private readonly Material wood, ivory, ink, felt, gold;
		private readonly TextMesh board;
		private readonly AudioSource audio;
		private readonly AudioClip knock;
		private uint knockRoll;
		private int knocks;
		private bool initial = true;
		private DiceStateMsg presentation;
		private readonly Queue<DiceStateMsg> pendingPresentations = new Queue<DiceStateMsg>();
		private string observedMatch;
		private uint observedRoll;
		private int layer;
		private uint boardRevision = uint.MaxValue;
		private bool boardRolling, boardHistory;
		/// <summary>The local reader turned the board to the rules and the match history.</summary>
		public bool ShowHistory;
		public string HistoryText = "";
		public Transform Cup => cup;
		private const int BoardLines = 9, BoardColumns = 46;
		private const float TrayX = -.045f, CupX = -.305f;
		private byte shownHeld;
		// The table is low: it is played sitting on the deck. The board leans back, so it is read from above.
		public const float TopHeight = .38f;
		private const float BoardLean = 25f;
		private static readonly Vector3 BoardHinge = new Vector3(0, .018f, .2f), BoardCenter = new Vector3(0, .136f, .255f), BoardSize = new Vector3(.62f, .24f, .13f);
		private static Font boardFont;
		private static Material boardMaterial, litSource;
		private static float litRetryAt;
		// A TextMesh made in code has no font material. The game's own labels have one that is hidden
		// by what stands in front of it, so the board borrows the most used pair of font and material.
		private static void FindBoardFont()
		{
			if (boardFont != null && boardMaterial != null) return;
			try
			{
				var counts = new Dictionary<Material, int>(); int best = 0;
				foreach (TextMesh mesh in Resources.FindObjectsOfTypeAll<TextMesh>())
				{
					var renderer = mesh != null && mesh.font != null ? mesh.GetComponent<MeshRenderer>() : null;
					Material material = renderer != null ? renderer.sharedMaterial : null;
					if (material == null) continue;
					counts.TryGetValue(material, out int count); counts[material] = ++count;
					if (count > best) { best = count; boardFont = mesh.font; boardMaterial = material; }
				}
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[Dice] game font search failed: " + e.Message); }
			if (boardFont != null && boardMaterial != null) return;
			boardFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
			boardMaterial = boardFont != null ? boardFont.material : null;
		}
		// A material made from a shader name is not reliable in the shipped game, and an unlit one glows
		// at night. A copy of an opaque material of some item of the game is lit like everything around it.
		private static Material LitSource()
		{
			if (litSource != null || Time.unscaledTime < litRetryAt) return litSource;
			litRetryAt = Time.unscaledTime + 30;
			try
			{
				int looked = 0;
				foreach (ShipItem item in Resources.FindObjectsOfTypeAll<ShipItem>())
				{
					// Any suitable material will do; the search for a better one is not worth a stall.
					if (++looked > 400 && litSource != null) break;
					var renderer = item != null ? item.GetComponentInChildren<MeshRenderer>(true) : null;
					Material material = renderer != null ? renderer.sharedMaterial : null;
					if (material == null || material.renderQueue > 2450 || !material.HasProperty("_Color") || !material.HasProperty("_MainTex") ||
						material.IsKeywordEnabled("_EMISSION")) continue;
					litSource = material;
					if (material.shader.name == "Standard") break;
				}
				Plugin.Logger.LogInfo("[Dice] table material: " + (litSource != null ? litSource.name + " (" + litSource.shader.name + ")" : "plain, no item material found"));
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[Dice] item material search failed: " + e.Message); }
			return litSource;
		}
		public DiceTableView(Transform boat, DiceStateMsg state)
		{
			Root = new GameObject("Coop dice table"); Root.transform.SetParent(boat, false);
			Root.transform.localPosition = boat != null ? new Vector3(state.X, state.Y, state.Z) : CoordSpace.RealToLocal(new Vector3(state.X, state.Y, state.Z));
			Root.transform.localRotation = Quaternion.Euler(0, state.Yaw, 0);
			var camera = Camera.main;
			for (int i = 0; i < 32; i++) if (camera == null || (camera.cullingMask & (1 << i)) != 0) { layer = i; break; }
			wood = Material(new Color(.37f, .23f, .12f)); ivory = Material(new Color(.96f, .9f, .75f));
			ink = Material(new Color(.06f, .04f, .02f)); felt = Material(new Color(.1f, .2f, .16f)); gold = Material(new Color(.83f, .62f, .2f));
			top = new GameObject("Folding top").transform; top.SetParent(Root.transform, false); top.localPosition = new Vector3(0, TopHeight, 0);
			Block(top, "Tabletop", Vector3.zero, new Vector3(.7f, .035f, .5f), wood);
			Block(top, "Tray", new Vector3(TrayX, .021f, 0), new Vector3(.4f, .012f, .3f), felt);
			for (int side = -1; side <= 1; side += 2)
			{
				Block(top, "Tray rail", new Vector3(TrayX, .04f, side * .16f), new Vector3(.44f, .035f, .015f), wood);
				Block(top, "Tray rail", new Vector3(TrayX + side * .215f, .04f, 0), new Vector3(.015f, .035f, .32f), wood);
			}
			for (int i = 0; i < 4; i++)
			{
				legs[i] = new GameObject("Leg hinge").transform; legs[i].SetParent(Root.transform, false);
				legs[i].localPosition = new Vector3((i % 2 == 0 ? -1 : 1) * .28f, TopHeight - .02f, (i < 2 ? -1 : 1) * .18f);
				Block(legs[i], "Leg", new Vector3(0, -(TopHeight - .02f) / 2, 0), new Vector3(.035f, TopHeight - .02f, .035f), wood);
			}
			cup = new GameObject("Dice cup").transform; cup.SetParent(top, false); cup.localPosition = new Vector3(CupX, .025f, .1f);
			for (int i = 0; i < 16; i++)
			{
				float angle = i * Mathf.PI / 8;
				var wall = Block(cup, "Cup wall", new Vector3(Mathf.Cos(angle) * .033f, .047f, Mathf.Sin(angle) * .033f), new Vector3(.012f, .094f, .015f), wood);
				wall.localRotation = Quaternion.Euler(0, -i * 22.5f, 0);
			}
			Block(cup, "Cup base", Vector3.zero, new Vector3(.07f, .01f, .07f), wood);
			for (int i = 0; i < 3; i++)
			{
				Block(top, "Keep slot", Slot(i, true) - new Vector3(0, .022f, 0), new Vector3(.06f, .008f, .065f), felt);
				dice[i] = new GameObject("Die " + i).transform; dice[i].SetParent(top, false);
				Block(dice[i], "Bone", Vector3.zero, Vector3.one * .04f, ivory);
				Pips(dice[i], 1, Vector3.up, Vector3.right, Vector3.forward);
				Pips(dice[i], 6, Vector3.down, Vector3.right, Vector3.forward);
				Pips(dice[i], 2, Vector3.forward, Vector3.right, Vector3.up);
				Pips(dice[i], 5, Vector3.back, Vector3.right, Vector3.up);
				Pips(dice[i], 3, Vector3.right, Vector3.forward, Vector3.up);
				Pips(dice[i], 4, Vector3.left, Vector3.forward, Vector3.up);
				// The pose is computed for the current tick on this machine, so there is nothing to wait for.
				poses[i] = new NetTransform { InterpDelayMs = 0, ToWorldPos = p => top.TransformPoint(p), ToWorldRot = q => top.rotation * q };
			}
			// The stake: one coin for each seated player, stacked in the near corner.
			for (int i = 0; i < coins.Length; i++)
			{
				coins[i] = Shape(PrimitiveType.Cylinder, top, "Stake coin", new Vector3(.3f, .0195f + i * .0045f, -.2f), new Vector3(.026f, .002f, .026f), gold);
				coins[i].gameObject.SetActive(false);
			}
			// The board stands behind the tray on the far long side and is read from the tray side.
			var hinge = new GameObject("Board hinge").transform; hinge.SetParent(top, false);
			hinge.localPosition = BoardHinge; hinge.localRotation = Quaternion.Euler(BoardLean, 0, 0);
			Block(hinge, "Score board", new Vector3(0, .13f, .006f), new Vector3(.62f, .26f, .012f), wood);
			var boardObject = new GameObject("Scoreboard"); boardObject.layer = layer;
			boardObject.transform.SetParent(hinge, false); boardObject.transform.localPosition = new Vector3(-.295f, .252f, -.004f);
			board = boardObject.AddComponent<TextMesh>(); board.fontSize = 48; board.characterSize = .005f;
			board.color = Color.white; board.anchor = TextAnchor.UpperLeft;
			FindBoardFont();
			if (boardFont != null) board.font = boardFont;
			if (boardMaterial != null) boardObject.GetComponent<MeshRenderer>().sharedMaterial = boardMaterial;
			audio = Root.AddComponent<AudioSource>(); audio.spatialBlend = 1; audio.minDistance = .5f; audio.maxDistance = 8;
			knock = AudioClip.Create("Dice knock", 2205, 1, 22050, false);
			var samples = new float[2205]; for (int i = 0; i < samples.Length; i++) samples[i] = Mathf.Sin(i * 1.8f) * Mathf.Exp(-i / 180f) * .15f;
			knock.SetData(samples, 0);
		}
		private Material Material(Color color)
		{
			Material source = Plugin.Cfg.DiceLitMaterials.Value ? LitSource() : null;
			if (source == null) return new Material(Shader.Find("Sprites/Default")) { color = color };
			var material = new Material(source) { mainTexture = null, color = color };
			if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .15f);
			if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0);
			return material;
		}
		private Transform Block(Transform parent, string name, Vector3 pos, Vector3 scale, Material material)
			=> Shape(PrimitiveType.Cube, parent, name, pos, scale, material);
		private Transform Shape(PrimitiveType type, Transform parent, string name, Vector3 pos, Vector3 scale, Material material)
		{
			var obj = GameObject.CreatePrimitive(type); obj.name = name; obj.layer = layer;
			var collider = obj.GetComponent<Collider>(); collider.enabled = false; UnityEngine.Object.Destroy(collider);
			obj.transform.SetParent(parent, false); obj.transform.localPosition = pos; obj.transform.localScale = scale;
			obj.GetComponent<Renderer>().sharedMaterial = material; return obj.transform;
		}
		private void Pips(Transform die, int count, Vector3 normal, Vector3 horizontal, Vector3 vertical)
		{
			for (int i = 0; i < count; i++)
			{
				float x, y;
				if (count == 6) { x = i < 3 ? -.01f : .01f; y = (i % 3 - 1) * .01f; }
				else if (i == count - 1 && (count & 1) == 1) x = y = 0;
				else { x = (i % 2 == 0 ? -1 : 1) * .01f; y = (i < 2 ? -1 : 1) * .01f; if (count == 2 || count == 3) y = x; }
				var pip = Block(die, "Pip", normal * .0203f + horizontal * x + vertical * y, new Vector3(.0045f, .001f, .0045f), ink);
				pip.localRotation = Quaternion.FromToRotation(Vector3.up, normal);
			}
		}
		public static Vector3 Slot(int index, bool held) => held ? new Vector3(.255f, .047f, (index - 1) * .085f) : new Vector3(TrayX - .11f + index * .11f, .047f, 0);
		// Where a die comes to rest is decoration, but every machine has to show the same picture, so it
		// is computed from the table and the roll instead of being sent.
		private static float Scatter(uint table, uint roll, int die, uint salt)
		{
			uint hash = unchecked(table * 73856093u ^ roll * 19349663u ^ (uint) (die + 1) * 83492791u ^ salt * 2654435761u);
			hash ^= hash >> 13; hash = unchecked(hash * 0x5bd1e995u); hash ^= hash >> 15;
			return (hash & 0xFFFFFF) / (float) 0x1000000;
		}
		private static Vector3 Rest(uint table, uint roll, int die)
			=> roll == 0 ? Slot(die, false) : new Vector3(TrayX - .11f + die * .11f + (Scatter(table, roll, die, 0) - .5f) * .05f, .047f, (Scatter(table, roll, die, 1) - .5f) * .2f);
		private static Quaternion RestTurn(uint table, uint roll, int die)
			=> roll == 0 ? Quaternion.identity : Quaternion.Euler(0, Scatter(table, roll, die, 2) * 360, 0);
		public static Quaternion FaceRotation(byte face)
		{
			switch (face)
			{
				case 2: return Quaternion.Euler(-90, 0, 0);
				case 3: return Quaternion.Euler(0, 0, 90);
				case 4: return Quaternion.Euler(0, 0, -90);
				case 5: return Quaternion.Euler(90, 0, 0);
				case 6: return Quaternion.Euler(180, 0, 0);
				default: return Quaternion.identity;
			}
		}
		public void Apply(DiceStateMsg state, long tick)
		{
			if (state.BoatIndex == BoatLocator.NoBoat)
				Root.transform.position = CoordSpace.RealToLocal(new Vector3(state.X, state.Y, state.Z));
			long elapsed = state.ElapsedMs + (state.Running ? Math.Max(0, tick - state.AnchorTick) : 0);
			ObservePresentation(state);
			if (presentation == null || state.MatchId != presentation.MatchId)
			{
				presentation = pendingPresentations.Dequeue();
				foreach (var pose in poses) pose.Clear();
				shownHeld = 0;
			}
			while (pendingPresentations.Count > 0 && elapsed >= presentation.PresentationAtMs + DiceGame.RollDurationMs)
				presentation = pendingPresentations.Dequeue();
			float phase = Mathf.Clamp01((elapsed - presentation.PresentationAtMs) / (float) DiceGame.RollDurationMs);
			bool rolling = presentation.RollId > 0 && presentation.Faces.All(f => f > 0) && phase < 1;
			float fold = Mathf.Clamp01((elapsed - state.PresentationAtMs) / (float) DiceGame.FoldDurationMs);
			float open = state.Phase == (byte) DicePhase.Unfolding ? fold : state.Phase == (byte) DicePhase.Folding ? 1 - fold : 1;
			top.localRotation = Quaternion.Euler((1 - open) * 85, 0, 0);
			for (int i = 0; i < 4; i++) legs[i].localRotation = Quaternion.Euler((1 - open) * (i < 2 ? 85 : -85), 0, 0);
			cup.localRotation = Quaternion.Euler(0, 0, rolling ? Mathf.Sin(phase * Mathf.PI) * -70 : 0);
			uint roll = rolling ? presentation.RollId : state.RollId;
			// A kept die stays in its slot after the turn is over, until the next roll collects it.
			if (rolling) shownHeld = presentation.RollHeldMask;
			else if (state.Phase == (byte) DicePhase.Selecting) shownHeld = state.HeldMask;
			for (int i = 0; i < 3; i++)
			{
				byte face = rolling ? presentation.Faces[i] : state.Faces[i];
				bool held = (shownHeld & (1 << i)) != 0;
				Vector3 position = held ? Slot(i, true) : Rest(state.TableId, roll, i);
				Quaternion rotation = (held ? Quaternion.identity : RestTurn(state.TableId, roll, i)) * FaceRotation(face);
				if (rolling && !held)
				{
					position = Vector3.Lerp(new Vector3(CupX, .15f, .1f), position, phase);
					position.y += Mathf.Abs(Mathf.Sin(phase * Mathf.PI * 4)) * .12f * (1 - phase);
					rotation = Quaternion.Euler((1 - phase) * (720 + i * 90), (1 - phase) * 540, (1 - phase) * 360) * rotation;
				}
				poses[i].Push(tick, position, rotation); poses[i].Apply(dice[i], tick);
				dice[i].gameObject.SetActive(face > 0);
			}
			// A knock for every time the dice touch the tray. A roll that was already under way when this
			// view appeared stays silent up to where it is now.
			int touches = rolling ? Mathf.FloorToInt(phase * 4) : 0;
			if (rolling && (initial || knockRoll != presentation.RollId)) { knockRoll = presentation.RollId; knocks = initial ? touches : 0; }
			for (; rolling && knocks < touches; knocks++)
			{
				audio.pitch = .85f + Scatter(state.TableId, presentation.RollId, knocks, 3) * .4f;
				audio.PlayOneShot(knock, 1f - knocks * .22f);
			}
			initial = false;
			bool staked = state.Stake > 0 && state.Phase >= (byte) DicePhase.Lobby && state.Phase <= (byte) DicePhase.Completed;
			for (int i = 0; i < coins.Length; i++) coins[i].gameObject.SetActive(staked && i < state.Players.Length);
			if (state.Revision != boardRevision) ShowHistory = false;
			if (state.Revision == boardRevision && rolling == boardRolling && ShowHistory == boardHistory) return;
			boardRevision = state.Revision; boardRolling = rolling; boardHistory = ShowHistory;
			board.text = Fit(ShowHistory ? "THREE ROUNDS\nThree dice, one reroll of any of them.\nPair +4, triple +12. Highest total wins.\n" + HistoryText : ScoreText(state, rolling));
		}
		/// <summary>The index comes from another machine and is not checked there.</summary>
		public static string CurrencyName(byte currency)
		{
			try { if (currency < WalletSync.CurrencyCount) return PlayerGold.GetCurrencyName(currency); } catch { }
			return "coins";
		}
		private static string Fit(string text)
			=> string.Join("\n", text.Split('\n').Where(line => line.Trim().Length > 0).Take(BoardLines).Select(line => line.Length > BoardColumns ? line.Substring(0, BoardColumns - 2) + ".." : line.TrimEnd()));
		private static string Short(string name) => name.Length > 14 ? name.Substring(0, 12) + ".." : name;
		// The faces are already in the state while the dice are still in the air, so a roll in progress hides them.
		private static string ScoreText(DiceStateMsg state, bool rolling)
		{
			var phase = (DicePhase) state.Phase;
			string turn = state.Players.Length > state.Seat ? Short(state.Players[state.Seat].Name) : "";
			string headline;
			switch (phase)
			{
				case DicePhase.Unfolding: headline = "Setting up"; break;
				case DicePhase.Lobby:
					headline = state.Players.Length == 0 ? "Sit down at the cup" : state.Players.Length < 2 ? "Waiting for a second player" :
						state.Players.All(p => p.Ready) ? "All ready: start at this board" : "Waiting for everyone to be ready";
					break;
				case DicePhase.AwaitRoll: headline = turn + " to roll"; break;
				case DicePhase.Selecting: headline = rolling ? turn + " rolls" : turn + ": keep, reroll or take"; break;
				case DicePhase.RollingInitial: case DicePhase.RollingReroll: headline = turn + " rolls"; break;
				case DicePhase.Paused: headline = "Paused"; break;
				case DicePhase.Completed:
					int best = state.Players.Max(p => p.Scores.Sum());
					var winners = state.Players.Where(p => p.Scores.Sum() == best).Select(p => Short(p.Name)).ToArray();
					headline = (winners.Length == 1 ? "Winner: " : "Shared victory: ") + string.Join(", ", winners);
					break;
				case DicePhase.Cancelled: headline = "Party cancelled"; break;
				default: headline = ""; break;
			}
			bool playing = phase >= DicePhase.AwaitRoll && phase <= DicePhase.Paused;
			string faces = "";
			if (rolling) faces = "Rolling...";
			else if (phase >= DicePhase.AwaitRoll && state.Faces.All(f => f > 0))
			{
				int score = DiceGame.Score(state.Faces[0], state.Faces[1], state.Faces[2]), sum = state.Faces[0] + state.Faces[1] + state.Faces[2];
				faces = string.Join("  ", state.Faces.Select(f => f.ToString())) + "  = " + score + (score - sum == 12 ? "  (triple +12)" : score - sum == 4 ? "  (pair +4)" : "");
			}
			string stake = state.Stake > 0 ? "Stake: " + state.Stake + " " + CurrencyName(state.StakeCurrency) + " each" : "";
			if (state.Stake > 0 && phase == DicePhase.Completed)
			{
				// The same rule as DiceSync.Settle: nobody pays when everyone shares the best score.
				int top = state.Players.Max(p => p.Scores.Sum()), winning = state.Players.Count(p => p.Scores.Sum() == top);
				stake = winning == state.Players.Length ? "Draw: nobody pays the stake"
					: "Each loser pays " + state.Stake + " " + CurrencyName(state.StakeCurrency) + (winning == 1 ? " to the winner" : ", shared by the winners");
			}
			return "THREE ROUNDS" + (playing ? "   round " + (state.Round + 1) + " of 3" : "") + "\n" + headline + "\n" + faces + "\n" + stake + "\n" +
				string.Join("\n", state.Players.Select(p => Short(p.Name) + "  " + string.Join(" / ", p.Scores.Select((s, i) => p.Scored[i] ? s.ToString() : "-")) + "  = " + p.Scores.Sum() +
					(!p.Connected ? "  (away)" : phase == DicePhase.Lobby ? (p.Ready ? "  ready" : "  ...") : phase == DicePhase.Paused ? (p.CancelVote ? "  cancel" : p.ResumeReady ? "  ready" : "  ...") : "")));
		}
		public void ObservePresentation(DiceStateMsg state)
		{
			if (observedMatch == state.MatchId && observedRoll == state.RollId) return;
			if (observedMatch != state.MatchId) pendingPresentations.Clear();
			observedMatch = state.MatchId; observedRoll = state.RollId;
			pendingPresentations.Enqueue(new DiceStateMsg { MatchId = state.MatchId, RollId = state.RollId,
				PresentationAtMs = state.PresentationAtMs, Faces = (byte[]) state.Faces.Clone(), RollHeldMask = state.RollHeldMask });
		}
		public int Target(Ray ray, out float distance)
		{
			distance = float.MaxValue; int target = -1;
			Vector3 origin = top.InverseTransformPoint(ray.origin), direction = top.InverseTransformDirection(ray.direction);
			for (int i = 0; i < 5; i++)
			{
				if (i >= 1 && i <= 3 && !dice[i - 1].gameObject.activeSelf) continue;
				Vector3 center = i == 0 ? cup.localPosition + Vector3.up * .05f : i == 4 ? BoardCenter : dice[i - 1].localPosition;
				var bounds = new Bounds(center, i == 4 ? BoardSize : Vector3.one * (i == 0 ? .12f : .075f));
				if (bounds.IntersectRay(new Ray(origin, direction), out float d) && d < distance) { distance = d; target = i; }
			}
			return target;
		}
		public void Dispose()
		{
			UnityEngine.Object.Destroy(Root); UnityEngine.Object.Destroy(wood); UnityEngine.Object.Destroy(ivory);
			UnityEngine.Object.Destroy(ink); UnityEngine.Object.Destroy(felt); UnityEngine.Object.Destroy(gold); UnityEngine.Object.Destroy(knock);
		}
	}
}
