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
		private readonly Material[] materials;
		private readonly DiceBoard board;
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
		private bool presentingRoll;
		private DiceActionSparkles actionSparkles;
		/// <summary>The local reader turned the board to the rules and the match history.</summary>
		public bool ShowHistory;
		public string HistoryText = "";
		public Transform Cup => cup;
		public const int TableTarget = 5;
		private const float TrayX = -.045f, CupX = -.305f;
		private byte shownHeld;
		// The table is low: it is played sitting on the deck. The board leans back, so it is read from above.
		public const float TopHeight = .38f;
		private const float BoardLean = 25f;
		private static readonly Vector3 BoardHinge = new Vector3(0, .018f, .2f), BoardCenter = new Vector3(0, .136f, .255f), BoardSize = new Vector3(.66f, .3f, .14f);
		// A material borrowed from the game brings its own maps with it; the table only keeps the shape
		// of its shader, its light and the one texture it is given.
		private static readonly string[] Maps = { "_BumpMap", "_OcclusionMap", "_DetailMask", "_EmissionMap", "_ParallaxMap" };
		private static Material litSource;
		private static float litRetryAt;
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
			Material wood = DiceGameMaterials.Copy("wood", Color.white) ?? Material(Color.white, DiceVisuals.Wood());
			Material beam = new Material(wood) { name = "Dice dark wood", color = wood.color * new Color(.72f, .65f, .55f) };
			Material bone = Material(Color.white, DiceVisuals.Atlas());
			Material felt = DiceGameMaterials.Copy("cloth", new Color(.16f, .30f, .23f)) ?? Material(Color.white, DiceVisuals.Cloth());
			Material slate = Material(Color.white, DiceVisuals.Slate()), gold = Material(new Color(.88f, .70f, .30f), null);
			Material leather = DiceGameMaterials.Copy("leather", Color.white) ?? Material(Color.white, DiceVisuals.Leather());
			Material brass = Material(new Color(.82f, .65f, .27f), null);
			Material chalkMat = Material(new Color(.95f, .94f, .90f), null);
			if (gold.HasProperty("_Glossiness")) gold.SetFloat("_Glossiness", .65f);
			if (gold.HasProperty("_Metallic")) gold.SetFloat("_Metallic", .88f);
			if (brass.HasProperty("_Glossiness")) brass.SetFloat("_Glossiness", .58f);
			if (brass.HasProperty("_Metallic")) brass.SetFloat("_Metallic", .82f);
			if (chalkMat.HasProperty("_Glossiness")) chalkMat.SetFloat("_Glossiness", .05f);
			materials = new[] { wood, beam, bone, felt, slate, gold, leather, brass, chalkMat };
			top = new GameObject("Folding top").transform; top.SetParent(Root.transform, false); top.localPosition = new Vector3(0, TopHeight, 0);
			Block(top, "Tabletop", Vector3.zero, new Vector3(.7f, .035f, .5f), wood);
			Block(top, "Tray", new Vector3(TrayX, .021f, 0), new Vector3(.4f, .012f, .3f), felt);
			for (int side = -1; side <= 1; side += 2)
			{
				Block(top, "Tray rail", new Vector3(TrayX, .04f, side * .16f), new Vector3(.44f, .035f, .015f), beam);
				Block(top, "Tray rail", new Vector3(TrayX + side * .215f, .04f, 0), new Vector3(.015f, .035f, .32f), beam);
			}
			for (int sx = -1; sx <= 1; sx += 2)
				for (int sz = -1; sz <= 1; sz += 2)
					Block(top, "Tray corner", new Vector3(TrayX + sx * .215f, .041f, sz * .158f), new Vector3(.022f, .032f, .022f), brass);
			for (int i = 0; i < 4; i++)
			{
				legs[i] = new GameObject("Leg hinge").transform; legs[i].SetParent(Root.transform, false);
				legs[i].localPosition = new Vector3((i % 2 == 0 ? -1 : 1) * .28f, TopHeight - .02f, (i < 2 ? -1 : 1) * .18f);
				Block(legs[i], "Leg", new Vector3(0, -(TopHeight - .02f) / 2, 0), new Vector3(.035f, TopHeight - .02f, .035f), beam);
			}
			// A cup turned from one profile: it stands beside the tray and its rim clears the rail.
			cup = Sculpt(top, "Dice cup", DiceVisuals.CupMesh(), new Vector3(CupX, .025f, .1f), leather);
			// Raised metal rims keep the cup detailed even when its leather comes from the game.
			Sculpt(cup, "Cup brass bands", DiceVisuals.CupBands(), Vector3.zero, brass);
			for (int i = 0; i < 3; i++)
			{
				Vector3 slotPos = Slot(i, true);
				Block(top, "Keep slot trim", slotPos - new Vector3(0, .023f, 0), new Vector3(.068f, .006f, .072f), beam);
				Block(top, "Keep slot", slotPos - new Vector3(0, .020f, 0), new Vector3(.058f, .007f, .062f), felt);
				dice[i] = Sculpt(top, "Die " + i, DiceVisuals.DieMesh(), Vector3.zero, bone);
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
			Block(top, "Hinge L", BoardHinge + new Vector3(-.18f, .010f, 0), new Vector3(.036f, .008f, .024f), brass);
			Block(top, "Hinge R", BoardHinge + new Vector3(.18f, .010f, 0), new Vector3(.036f, .008f, .024f), brass);
			Block(hinge, "Board frame", new Vector3(0, .13f, .01f), new Vector3(.665f, .295f, .012f), beam);
			Block(hinge, "Board slate", new Vector3(0, .13f, .0055f), new Vector3(.625f, .262f, .011f), slate);
			Block(hinge, "Chalk ledge", new Vector3(0, .003f, -.002f), new Vector3(.625f, .007f, .016f), beam);
			var chalkStick = Shape(PrimitiveType.Cylinder, hinge, "Chalk stick", new Vector3(.18f, .010f, -.004f), new Vector3(.007f, .024f, .007f), chalkMat);
			chalkStick.localRotation = Quaternion.Euler(0, 0, 85f);
			board = new DiceBoard(hinge, layer, gold);
			audio = Root.AddComponent<AudioSource>(); audio.spatialBlend = 1; audio.minDistance = .5f; audio.maxDistance = 8;
			knock = AudioClip.Create("Dice knock", 2205, 1, 22050, false);
			var samples = new float[2205];
			for (int i = 0; i < samples.Length; i++)
			{
				float t = i / 22050f;
				float click = (DiceVisuals.Noise(i, 42) * 2f - 1f) * Mathf.Exp(-t / .003f) * .35f;
				float boneClick = Mathf.Sin(t * 1520f * Mathf.PI * 2f) * Mathf.Exp(-t / .0075f) * .32f;
				float woodRes = (Mathf.Sin(t * 260f * Mathf.PI * 2f) + Mathf.Sin(t * 520f * Mathf.PI * 2f) * .45f) * Mathf.Exp(-t / .032f) * .45f;
				samples[i] = (click + boneClick + woodRes) * .55f;
			}
			knock.SetData(samples, 0);
		}
		private void PlayKnock(float volume, float pitch)
		{
			try
			{
				var col = ItemCollisionSoundPlayer.instance;
				if (col != null && col.audios != null && col.audios.Length > 0 && col.audios[0].clip != null)
				{
					audio.pitch = pitch;
					audio.PlayOneShot(col.audios[0].clip, volume * .32f);
					return;
				}
			}
			catch { }
			audio.pitch = pitch;
			audio.PlayOneShot(knock, volume);
		}
		// A material for the table: the texture says what it is, the borrowed material says how it takes
		// the light. The maps of the item it came from are dropped, or the table would wear its bark.
		private Material Material(Color color, Texture texture)
		{
			Material source = Plugin.Cfg.DiceLitMaterials.Value ? LitSource() : null;
			if (source == null)
			{
				var plain = new Material(Shader.Find("Sprites/Default")) { color = color };
				if (texture != null) plain.mainTexture = texture;
				return plain;
			}
			var material = new Material(source);
			foreach (string map in Maps) if (material.HasProperty(map)) material.SetTexture(map, null);
			material.DisableKeyword("_EMISSION");
			if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .12f);
			if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
			material.color = color;
			// The texture of the table is its own; a part without one keeps none, not the atlas of the
			// item the material was borrowed from.
			material.mainTexture = texture;
			material.mainTextureScale = Vector2.one; material.mainTextureOffset = Vector2.zero;
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
		private Transform Sculpt(Transform parent, string name, Mesh mesh, Vector3 pos, Material material)
		{
			var obj = new GameObject(name); obj.layer = layer;
			obj.AddComponent<MeshFilter>().sharedMesh = mesh;
			obj.AddComponent<MeshRenderer>().sharedMaterial = material;
			obj.transform.SetParent(parent, false); obj.transform.localPosition = pos;
			return obj.transform;
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
			presentingRoll = rolling;
			float fold = Mathf.Clamp01((elapsed - state.PresentationAtMs) / (float) DiceGame.FoldDurationMs);
			float open = state.Phase == (byte) DicePhase.Unfolding ? fold : state.Phase == (byte) DicePhase.Folding ? 1 - fold : 1;
			top.localRotation = Quaternion.Euler((1 - open) * 85, 0, 0);
			for (int i = 0; i < 4; i++) legs[i].localRotation = Quaternion.Euler((1 - open) * (i < 2 ? 85 : -85), 0, 0);

			if (rolling)
			{
				if (phase < .22f)
				{
					float shakePhase = phase / .22f;
					float shakeZ = Mathf.Sin(shakePhase * Mathf.PI * 6f) * 6f;
					float shakeX = Mathf.Cos(shakePhase * Mathf.PI * 6f) * 4f;
					cup.localRotation = Quaternion.Euler(shakeX, 0, 10f + shakeZ);
				}
				else
				{
					float pour = (phase - .22f) / .78f;
					cup.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(pour * Mathf.PI) * -75f);
				}
			}
			else cup.localRotation = Quaternion.identity;

			uint roll = rolling ? presentation.RollId : state.RollId;
			// A kept die stays in its slot after the turn is over, until the next roll collects it.
			if (rolling) shownHeld = presentation.RollHeldMask;
			else if (state.Phase == (byte) DicePhase.Selecting) shownHeld = state.HeldMask;

			Vector3 insideCup = new Vector3(CupX, .025f + DiceVisuals.CupHeight * .5f, .1f);
			Vector3 cupMouth = new Vector3(CupX + .03f, .025f + DiceVisuals.CupHeight + .01f, .1f);
			for (int i = 0; i < 3; i++)
			{
				byte face = rolling ? presentation.Faces[i] : state.Faces[i];
				bool held = (shownHeld & (1 << i)) != 0;
				Vector3 position = held ? Slot(i, true) : Rest(state.TableId, roll, i);
				Quaternion rotation = (held ? Quaternion.identity : RestTurn(state.TableId, roll, i)) * FaceRotation(face);
				if (rolling && !held)
				{
					if (phase < .22f)
					{
						float rattle = Mathf.Sin((phase / .22f) * Mathf.PI * 8f + i * 2f) * .007f;
						position = insideCup + new Vector3(rattle, (i - 1) * .014f + rattle, rattle);
						rotation = Quaternion.Euler(rattle * 400f, i * 60f, rattle * 300f);
					}
					else
					{
						float t = (phase - .22f) / .78f;
						position = Vector3.Lerp(cupMouth, position, t);
						position.y += Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3f)) * .12f * (1f - t);
						rotation = Quaternion.Euler((1f - t) * (720f + i * 90f), (1f - t) * 540f, (1f - t) * 360f) * rotation;
					}
				}
				poses[i].Push(tick, position, rotation); poses[i].Apply(dice[i], tick);
				dice[i].gameObject.SetActive(face > 0);
			}

			// Rattle sounds inside cup during shake, then landing knocks in tray:
			int touches = 0;
			if (rolling)
			{
				if (phase < .22f) touches = Mathf.FloorToInt((phase / .22f) * 2);
				else touches = 2 + Mathf.FloorToInt(((phase - .22f) / .78f) * 3);
			}
			if (rolling && (initial || knockRoll != presentation.RollId)) { knockRoll = presentation.RollId; knocks = initial ? touches : 0; }
			for (; rolling && knocks < touches; knocks++)
			{
				float p = .9f + Scatter(state.TableId, presentation.RollId, knocks, 3) * .35f;
				if (knocks < 2) PlayKnock(.25f, p * 1.35f);
				else PlayKnock(1f - (knocks - 2) * .22f, p);
			}

			bool staked = state.Stake > 0 && state.Phase >= (byte) DicePhase.Lobby && state.Phase <= (byte) DicePhase.Completed;
			for (int i = 0; i < coins.Length; i++) coins[i].gameObject.SetActive(staked && i < state.Players.Length);

			bool revisionChanged = state.Revision != boardRevision;
			if (revisionChanged) ShowHistory = false;
			if (state.Revision == boardRevision && rolling == boardRolling && ShowHistory == boardHistory) return;

			if (revisionChanged && !initial && !rolling)
			{
				var phaseEnum = (DicePhase) state.Phase;
				if (phaseEnum == DicePhase.Selecting || phaseEnum == DicePhase.AwaitRoll)
				{
					try { UISoundPlayer.instance?.PlayWritingSound(); } catch { }
				}
				else if (phaseEnum == DicePhase.Completed && state.Stake > 0)
				{
					try { UISoundPlayer.instance?.PlayGoldSound(); } catch { }
				}
			}

			initial = false;
			boardRevision = state.Revision; boardRolling = rolling; boardHistory = ShowHistory;
			if (ShowHistory) board.Page(HistoryText); else board.Score(state, rolling);
		}
		/// <summary>The index comes from another machine and is not checked there.</summary>
		public static string CurrencyName(byte currency)
		{
			try { if (currency < WalletSync.CurrencyCount) return PlayerGold.GetCurrencyName(currency); } catch { }
			return "coins";
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
				Vector3 center = i == 0 ? cup.localPosition + Vector3.up * (DiceVisuals.CupHeight * .5f) : i == 4 ? BoardCenter : dice[i - 1].localPosition;
				var bounds = new Bounds(center, i == 4 ? BoardSize : i == 0 ? new Vector3(.1f, DiceVisuals.CupHeight + .02f, .1f) : Vector3.one * .075f);
				if (bounds.IntersectRay(new Ray(origin, direction), out float d) && d < distance) { distance = d; target = i; }
			}
			// The whole table is an entry point; small game pieces keep their precise targets.
			if (target < 0 && new Bounds(new Vector3(0, -TopHeight / 2, 0),
				new Vector3(.72f, TopHeight + .05f, .52f)).IntersectRay(new Ray(origin, direction), out float tableDistance))
			{
				distance = tableDistance; target = TableTarget;
			}
			return target;
		}
		public void ShowActionCues(bool cupCue, bool boardCue, bool joinCue)
		{
			if (actionSparkles == null && !cupCue && !boardCue && !joinCue) return;
			if (actionSparkles == null) actionSparkles = new DiceActionSparkles(top, layer);
			actionSparkles.Show(0, cupCue && !presentingRoll, new Vector3(CupX, .025f + DiceVisuals.CupHeight, .1f));
			actionSparkles.Show(1, boardCue && !presentingRoll, BoardCenter + new Vector3(-.24f, .1f, -.09f));
			actionSparkles.Show(2, joinCue && !presentingRoll, new Vector3(0, .08f, 0));
		}
		public void Dispose()
		{
			actionSparkles?.Dispose();
			UnityEngine.Object.Destroy(Root);
			foreach (var material in materials) UnityEngine.Object.Destroy(material);
			UnityEngine.Object.Destroy(knock);
		}
	}
}
