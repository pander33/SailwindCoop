using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Winch cranks (both directions).
    ///
    /// A crank has no authoritative state of its own: the rope's <c>currentLength</c> is the state,
    /// and the crank angle is its integral. In the game the angle only moves while somebody holds the
    /// winch (<c>GPButtonRopeWinch.Update</c> -> <c>ApplyRotation</c>), and <c>DeltaToLength</c> turns
    /// it into the length: <c>currentLength -= deltaRotation / 360 / gearRatio</c>, that is
    /// <c>deltaRotation = -deltaLength * 360 * gearRatio</c>. Every machine already receives that
    /// length (host -> client in <see cref="ControlStateMsg"/>, client -> host as the length of a
    /// <see cref="ControlRequestMsg"/>), so every machine can derive the same crank pose from it:
    /// no extra wire data, no second authority, and the two directions are the same code.
    ///
    /// The arithmetic and the three rules live in <see cref="CrankState"/>; this file is the Unity
    /// side of it. One rule is not arithmetic and belongs here: <c>GPButtonRopeWinch.lastAngle</c> is
    /// written after every pose this code sets. The game's own <c>GetRotationDelta</c> measures the
    /// next frame's input against that field, and only while the winch is held, so a pose left there
    /// would be booked as player input on the next grab: a rope length jump of up to 0.5 / gearRatio.
    /// The host's old raw pose copy had exactly that defect. If that field ever leaves the game, the
    /// cranks stop being driven instead: a still handle is better than a rope that moves by itself.
    /// </summary>
    public sealed partial class ControlsSync
    {
        // Per winch: its derived state and the pose it was bound with, so that `Angle` is a turn
        // from that pose. Fixed local axis (Space.Self forward, as both the game's own ApplyRotation
        // and TouchRotateHandle turn a crank), so the pose is always rest * AngleAxis(angle).
        private CrankState[] _crank = new CrankState[0];
        private Quaternion[] _crankRest = System.Array.Empty<Quaternion>();

        // Non-public on the game class: the crank angle the next GetRotationDelta counts from.
        private static readonly FieldInfo WinchAngle = typeof(GPButtonRopeWinch).GetField("lastAngle",
            BindingFlags.NonPublic | BindingFlags.Instance);
        /// <summary>False once that field cannot be kept in step: then the cranks are left alone,
        /// because a pose written without it would be read as player input as soon as someone grabs
        /// the winch (a rope length jump). A missing handle is better than a moved rope.</summary>
        private static bool _winchAngleUsable = true;

        /// <summary>Take the current crank poses as the reference (new boat, refit, connect).</summary>
        private void ResetCranks()
        {
            int n = _winches.Length;
            _crank = new CrankState[n];
            _crankRest = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                var winch = _winches[i];
                if (winch == null) continue;
                _crankRest[i] = winch.transform.localRotation;
                _crank[i].Bind(winch.rope != null ? winch.rope.currentLength : 0f,
                    WinchCrank.Fold(winch.transform.localEulerAngles.z));
            }
        }

        /// <summary>
        /// Drive every crank whose rope length somebody else changed. Host: from <see cref="Tick"/>,
        /// client: from <see cref="ApplyClient"/>; both fan out through the boat contexts.
        /// </summary>
        internal void DriveCranks(float dt)
        {
            if (!_winchAngleUsable) return;
            if (_crank.Length != _winches.Length) ResetCranks();
            if (_winches.Length == 0 || _net.State != LinkState.Connected) return;

            GoPointerButton held = HeldButton();
            for (int i = 0; i < _winches.Length; i++)
            {
                var winch = _winches[i];
                if (winch == null) continue;
                var rope = winch.rope;
                if (rope == null) continue;
                Transform tr = winch.transform;
                float z = WinchCrank.Fold(tr.localEulerAngles.z);

                // This winch itself first (the common case while the local player cranks), then the
                // rope: two winches may share one rope, and a crank in the hand must never be driven.
                // A carried anchor lengthens its rope as the player walks (Anchor.Update); the game does
                // not turn the winch for that, so the crank stands as if held and keeps its reference.
                bool heldHere = held == winch || Grabbed(winch.rotHandle) || IsLocalRopeHeld(rope, held) ||
                    (rope is RopeControllerAnchor anchorRope && (AnchorSync.Instance?.AnchorCarried(anchorRope) ?? false));

                CrankStep step = _crank[i].Advance(rope.currentLength, heldHere, z, dt,
                    winch.gearRatio, out float poseAngle);
                if (step == CrankStep.Moved)
                {
                    Quaternion pose = _crankRest[i] * Quaternion.AngleAxis(poseAngle, Vector3.forward);
                    float poseZ = WinchCrank.Fold(pose.eulerAngles.z);
                    // The game's delta reference first: a pose it does not know about would be read as
                    // player input on the next grab, so without the reference the pose is not written.
                    if (!SetWinchAngle(winch, poseZ)) continue;
                    tr.localRotation = pose;
                    z = poseZ;
                }
                _crank[i].Seen = z;
            }
        }

        /// <summary>Keep the game's delta reference on the pose we are about to set (see the class
        /// note). False once it cannot be kept: cranks are then left alone for the session.</summary>
        private static bool SetWinchAngle(GPButtonRopeWinch winch, float z)
        {
            if (!_winchAngleUsable) return false;
            if (WinchAngle == null) { DisableCranks("field not found"); return false; }
            try { WinchAngle.SetValue(winch, z); return true; }
            catch (System.Exception e) { DisableCranks(e.Message); return false; }
        }

        /// <summary>Stop driving cranks for the session, once, with the reason in the log.</summary>
        private static void DisableCranks(string why)
        {
            if (!_winchAngleUsable) return;
            _winchAngleUsable = false;
            if (Plugin.Logger != null)
                Plugin.Logger.LogWarning("[ControlsSync] GPButtonRopeWinch.lastAngle: " + why +
                    " - remote crank turns are left alone, because a pose the game does not know about" +
                    " would be read as player input on the next grab");
        }
    }
}
