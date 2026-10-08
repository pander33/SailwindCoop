using System;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	// The close view of a dice table: the game camera is shown above the table for the frame being
	// drawn and put back right after it, so the game itself never sees it moved. The player stays
	// where they stand, the cursor is free and the table is played with the mouse buttons. Nothing of
	// this is sent: the others see the same table and the same avatar as without it.
	public sealed class DiceCloseUp
	{
		private const float BlendSeconds = .3f, LeaveMetres = .35f;
		// The middle of what has to be seen, and where the eye is from it, in the frame of the table. The
		// top itself turns while the table is folded, so the view holds on to the table, not to the top.
		private static readonly Vector3 Middle = new Vector3(0, DiceTableView.TopHeight + .09f, .057f), Eye = new Vector3(0, .8f, -.6f);
		private const float HalfHeight = .34f, HalfWidth = .4f;
		private DiceTable table;
		private Transform anchor;
		private bool active, captured, hooked, moved;
		private float blend;
		private int leftFrame = -1;
		private Vector3 enteredAt, savedPosition;
		private Quaternion savedRotation;
		private Camera movedCamera;
		private bool previousVisible, previousLook, previousCursorMenu;
		private CursorLockMode previousLock;
		public bool Active => active;
		public DiceTable Table => table;
		public void Enter(DiceTable target)
		{
			if (active || target?.View == null || GameState.inCursorMenu || Time.frameCount == leftFrame) return;
			var player = CoopBehaviour.Instance.Players.LocalPlayer;
			table = target; anchor = target.View.Root.transform; active = true;
			enteredAt = Standing(player);
			previousVisible = Cursor.visible; previousLock = Cursor.lockState;
			previousLook = MouseLook.MouseLookIsEnabled(); previousCursorMenu = GameState.inCursorMenu;
			// The same capture as the gesture wheel: without inCursorMenu the game locks the cursor again.
			MouseLook.ToggleMouseLook(newState: false);
			Cursor.visible = true; Cursor.lockState = CursorLockMode.None; GameState.inCursorMenu = true;
			captured = true;
			if (!hooked) { Camera.onPreCull += Show; Camera.onPostRender += PutBack; hooked = true; }
		}
		public void Exit()
		{
			if (!active) return;
			active = false; table = null; leftFrame = Time.frameCount;
			// An open co-op menu holds the cursor now and gives the game its input back when it closes.
			if (captured && CoopBehaviour.Instance?.CoopMenuOpen != true)
			{
				Cursor.visible = previousVisible; Cursor.lockState = previousLock;
				GameState.inCursorMenu = previousCursorMenu; MouseLook.ToggleMouseLook(previousLook);
			}
			captured = false;
		}
		/// <summary>The world is going away: nothing of it is worth a blend back.</summary>
		public void Drop()
		{
			Exit(); PutBack(movedCamera); blend = 0; anchor = null;
			if (hooked) { Camera.onPreCull -= Show; Camera.onPostRender -= PutBack; hooked = false; }
		}
		// Runs in Update, before the game opens its own menu on Escape in LateUpdate: that menu
		// remembers the mouse look it finds and has to find the game's own.
		public void Tick()
		{
			PutBack(movedCamera);
			if (active)
			{
				var player = CoopBehaviour.Instance.Players.LocalPlayer;
				bool gone = table.View == null || anchor == null || !GameState.playing || GameState.currentlyLoading || GameState.inBed || GameState.sleeping || BoatCamera.on;
				if (gone || CoopBehaviour.Instance.CoopMenuOpen || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) ||
					Input.GetKeyDown(Plugin.Cfg.DiceViewKey.Value) ||
					Vector3.Distance(Standing(player), enteredAt) > LeaveMetres) Exit();
			}
			blend = anchor == null ? 0 : Mathf.MoveTowards(blend, active ? 1 : 0, Time.unscaledDeltaTime / BlendSeconds);
			if (blend <= 0 && !active) anchor = null;
		}
		// Where the player stands beside the table. Height is left out: sitting down and getting up is not a step aside.
		private Vector3 Standing(Transform player)
		{
			if (player == null || anchor == null) return enteredAt;
			Vector3 local = anchor.InverseTransformPoint(player.position);
			return new Vector3(local.x, 0, local.z);
		}
		private static void Pose(Transform top, Camera camera, out Vector3 position, out Quaternion rotation)
		{
			float half = Mathf.Tan(Mathf.Clamp(camera.fieldOfView, 20, 120) * .5f * Mathf.Deg2Rad);
			float distance = Mathf.Clamp(Mathf.Max(HalfHeight / half, HalfWidth / (half * Mathf.Max(.5f, camera.aspect))), .3f, 1.6f);
			Vector3 middle = top.TransformPoint(Middle);
			position = top.TransformPoint(Middle + Eye * distance);
			rotation = Quaternion.LookRotation(middle - position, top.up);
		}
		/// <summary>The ray under the cursor as the close view shows it.</summary>
		public bool CursorRay(out Ray ray)
		{
			ray = default;
			var camera = Camera.main;
			if (!active || anchor == null || camera == null || Screen.width <= 0 || Screen.height <= 0) return false;
			Pose(anchor, camera, out Vector3 position, out Quaternion rotation);
			float half = Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad);
			Vector3 mouse = Input.mousePosition;
			ray = new Ray(position, rotation * new Vector3((mouse.x / Screen.width * 2 - 1) * half * camera.aspect, (mouse.y / Screen.height * 2 - 1) * half, 1).normalized);
			return true;
		}
		private void Show(Camera camera)
		{
			try
			{
				if (moved || blend <= 0 || anchor == null || camera != Camera.main) return;
				var eye = camera.transform;
				savedPosition = eye.localPosition; savedRotation = eye.localRotation; movedCamera = camera; moved = true;
				Pose(anchor, camera, out Vector3 position, out Quaternion rotation);
				float k = Mathf.SmoothStep(0, 1, blend);
				eye.SetPositionAndRotation(Vector3.Lerp(eye.position, position, k), Quaternion.Slerp(eye.rotation, rotation, k));
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[Dice] close view: " + e.Message); }
		}
		private void PutBack(Camera camera)
		{
			if (!moved || camera != movedCamera) return;
			moved = false;
			if (movedCamera != null) { movedCamera.transform.localPosition = savedPosition; movedCamera.transform.localRotation = savedRotation; }
			movedCamera = null;
		}
	}
}
