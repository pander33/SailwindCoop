using System;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The arithmetic of a winch crank, with no Unity types so that RuntimeSmoke covers it.
    ///
    /// The crank has no authoritative state of its own: the rope's <c>currentLength</c> is the state,
    /// and the crank angle is its integral. The game (<c>GPButtonRopeWinch.DeltaToLength</c>) turns a
    /// turn into a length:
    /// <code>currentLength -= deltaRotation / 360 / gearRatio</code>, so
    /// <c>deltaRotation = -deltaLength * 360 * gearRatio</c> (<see cref="Turn"/>).
    /// </summary>
    internal static class WinchCrank
    {
        /// <summary>A length step bigger than this did not come from a crank: a load, a resync or a
        /// refit. (A carried anchor lengthens its rope in small steps; ControlsWinches.cs stops the
        /// crank for that by the anchor's holder, not by this threshold.) The length
        /// runs 0..1, so this is a quarter of the whole rope; a quick release at gear 5 moves about
        /// 0.05 per state packet.</summary>
        internal const float JumpLength = 0.25f;

        /// <summary>The crank turn (°) a rope length step stands for: <c>DeltaToLength</c> inverted.
        /// Pulling the rope in (a negative step) turns the crank positively, as the game does.</summary>
        internal static float Turn(float lengthStep, float gearRatio)
        {
            return -lengthStep * 360f * gearRatio;
        }

        /// <summary>The game's own reading of a crank angle: <c>localEulerAngles.z</c> folded to
        /// [-180, 180) the way <c>GPButtonRopeWinch.GetRotationDelta</c> folds it. A difference of two
        /// readings is not in [0, 360) any more, so both directions are wrapped here.</summary>
        internal static float Fold(float degrees)
        {
            while (degrees >= 180f) degrees -= 360f;
            while (degrees < -180f) degrees += 360f;
            return degrees;
        }

        /// <summary>The short way from one folded crank angle to another (what
        /// <c>Mathf.DeltaAngle</c> gives): one frame of a hand turning a crank never passes a half
        /// turn, so the sign is the turn's.</summary>
        internal static float Delta(float from, float to)
        {
            return Fold(to - from);
        }

        /// <summary>How fast the crank closes the gap to the angle its rope length implies (1/s):
        /// a first-order approach with a 50 ms time constant, which is about one length step at
        /// <c>StateHz</c> 20. A max-speed chase is not enough — a packet-sized step (10-25°) is then
        /// closed inside one frame and the handle still moves in 20 Hz jerks; this makes every frame
        /// advance a fraction of the gap instead. The price is a lag of roughly <c>speed x 50 ms</c>
        /// (18° at 360 °/s, 50° at 1050 °/s with quick release), which is the same order as the
        /// interpolation delay the boat and the hinges already play at.</summary>
        internal const float SmoothPerSecond = 20f;

        /// <summary>Below this gap the crank snaps onto the target: without it a float never closes
        /// the last fraction of a degree and the handle would be written every frame forever. 0.05° is
        /// far below anything visible on a winch handle.</summary>
        internal const float SnapDegrees = 0.05f;

        /// <summary>Move <paramref name="angle"/> towards <paramref name="target"/> for one frame of
        /// <paramref name="dt"/> seconds: a first-order approach, so a 12-20 Hz length step becomes
        /// motion on every frame rather than a step per packet.</summary>
        internal static float Chase(float angle, float target, float dt)
        {
            float delta = target - angle;
            if (delta == 0f || dt <= 0f) return angle;
            if (Math.Abs(delta) <= SnapDegrees) return target;
            return angle + delta * (1f - (float)Math.Exp(-SmoothPerSecond * dt));
        }
    }

    /// <summary>What one frame did to a crank, so the caller knows whether to write a pose.</summary>
    internal enum CrankStep
    {
        /// <summary>Nothing to write: the crank stands where it is (also a jump it must not animate).</summary>
        Idle,
        /// <summary>The local player's hand turned the crank: <c>Angle</c> was followed, not driven.</summary>
        Followed,
        /// <summary>The rope length implies a new pose (<c>PoseAngle</c>) to write.</summary>
        Moved
    }

    /// <summary>
    /// One crank's derived state. The caller binds a crank to the pose it stands in
    /// (<see cref="Bind"/>), then asks once a frame (<see cref="Advance"/>) and writes the pose the
    /// answer names. No Unity types, so RuntimeSmoke covers the rules:
    /// <list type="bullet">
    /// <item><see cref="Angle"/> is measured from the pose the crank was bound with, never from
    /// <c>localEulerAngles</c> — the two differ by a constant, and mixing them snaps the handle by that
    /// constant on the first turn after a jump;</item>
    /// <item>a crank in the local player's hand is followed through its own transform turns, so whole
    /// turns are kept and letting go continues from where the hand stopped;</item>
    /// <item>a length step too big for a crank, or the first length of a boat, moves nothing.</item>
    /// </list>
    /// </summary>
    internal struct CrankState
    {
        /// <summary>Degrees this machine has turned the crank, relative to the bound pose.</summary>
        internal float Angle;
        /// <summary>Degrees the rope length implies, same reference as <see cref="Angle"/>.</summary>
        internal float Target;
        /// <summary>The rope length both of them refer to.</summary>
        internal float Length;
        /// <summary>The crank angle read from the transform last frame (<c>localEulerAngles.z</c>, folded).</summary>
        internal float Seen;
        /// <summary>A first length has been taken as the reference.</summary>
        internal bool Ready;

        /// <summary>Above this many degrees the accumulator is folded by whole turns: a crank turned
        /// for hours reaches a magnitude where a float loses sub-degree precision, and the rotation is
        /// what matters, not the count of turns.</summary>
        private const float FoldAbove = 10000f;

        /// <summary>Take a crank as it stands: the caller captured its current pose as the reference,
        /// and <paramref name="seen"/> is that pose's folded <c>localEulerAngles.z</c>.</summary>
        internal void Bind(float length, float seen)
        {
            Angle = 0f;
            Target = 0f;
            Length = length;
            Seen = seen;
            Ready = false;
        }

        /// <summary>
        /// One frame for one crank. <paramref name="held"/> means the local player's hand is on it;
        /// <paramref name="seen"/> is the transform's angle this frame (folded). The caller keeps
        /// <see cref="Seen"/> up to date after handling the answer: with the value it read, or with the
        /// angle it wrote.
        /// </summary>
        internal CrankStep Advance(float length, bool held, float seen, float dt, float gearRatio,
            out float poseAngle)
        {
            poseAngle = Angle;

            if (held)
            {
                Angle += WinchCrank.Delta(Seen, seen);
                Target = Angle;
                Length = length;
                KeepSmall();
                // A hand on the crank keeps the length reference current too, so letting go is not a
                // first look at the boat: it carries on from the hand.
                Ready = true;
                return CrankStep.Followed;
            }

            float step = length - Length;
            Length = length;

            // The first length of a boat is a join or a resync (a saved length against the host's live
            // one), and a step this big is a load, a resync or a refit: neither
            // is a hand on the crank. Keep the handle where it stands and stop chasing.
            bool firstLook = !Ready;
            Ready = true;
            if (firstLook || Math.Abs(step) > WinchCrank.JumpLength)
            {
                Target = Angle;
                return CrankStep.Idle;
            }

            if (step == 0f && Angle == Target) return CrankStep.Idle;

            Target += WinchCrank.Turn(step, gearRatio);
            Angle = WinchCrank.Chase(Angle, Target, dt);
            KeepSmall();
            poseAngle = Angle;
            return CrankStep.Moved;
        }

        /// <summary>Fold whole turns out of both angles, keeping their difference: the pose and the
        /// chase never look at the absolute count, only at the difference.</summary>
        private void KeepSmall()
        {
            if (Angle <= FoldAbove && Angle >= -FoldAbove) return;
            float whole = (float)Math.Round(Angle / 360f) * 360f;
            Angle -= whole;
            Target -= whole;
        }
    }
}
