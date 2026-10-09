using System;

namespace SailwindCoop.Sync
{
    /// <summary>What the sender stands on, as the game's own footsteps chose it. Wire values.</summary>
    public enum StepGround : byte
    {
        None = 0,   // in the air, swimming, lying, no character control
        Wood = 1,
        Sand = 2,
        Stone = 3,
        Grass = 4,
        Wet = 5,
    }

    /// <summary>
    /// The footstep signal of a player, packed into the free bits of <c>PlayerStateMsg.Hands</c>:
    /// bits 3-5 are the ground, bit 6 says the player is in water. A build that does not know these
    /// bits ignores them, so the wire format is the same.
    /// </summary>
    public static class StepSignal
    {
        public const byte GroundMask = 0x38;
        public const byte InWater = 0x40;
        private const int GroundShift = 3;

        public static byte Pack(StepGround ground, bool inWater)
            => (byte)((((int)ground << GroundShift) & GroundMask) | (inWater ? InWater : 0));

        public static StepGround Ground(byte hands)
        {
            int ground = (hands & GroundMask) >> GroundShift;
            return ground <= (int)StepGround.Wet ? (StepGround)ground : StepGround.None;
        }

        public static bool Swims(byte hands) => (hands & InWater) != 0;
    }

    /// <summary>
    /// When a remote player's foot comes down. The same count as the game's <c>FootstepsAudio</c>:
    /// distance walked on the ground, one step each <c>stepLength</c>, plus a step on landing.
    /// </summary>
    public sealed class StepCadence
    {
        private float _walked;
        private bool _grounded = true;   // an avatar that appears standing has not just landed

        public bool Advance(StepGround ground, float speed, float dt, float stepLength, float maxSpeed)
        {
            if (ground == StepGround.None)
            {
                _grounded = false;
                return false;
            }

            if (!_grounded)
            {
                _grounded = true;
                _walked = 0f;
                return true;
            }

            if (stepLength <= 0f || dt <= 0f) return false;
            _walked += Math.Max(0f, Math.Min(maxSpeed, speed)) * dt;
            if (_walked < stepLength) return false;
            // One step a frame at most, as in the game: a long frame does not play a burst.
            _walked = Math.Min(_walked - stepLength, stepLength);
            return true;
        }
    }
}
