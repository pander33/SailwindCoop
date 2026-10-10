using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The game's own footstep and swim sounds: the clips, the step length and the audio settings,
    /// read once from the local player's <c>FootstepsAudio</c> and <c>SwimEffects</c>. Their fields
    /// are private, so everything goes through reflection; a field that is gone leaves that sound
    /// out and nothing else.
    /// </summary>
    internal static class StepSounds
    {
        private static readonly FieldInfo CurrentClips = Field("currentClips"), StepLengthField = Field("stepLength"),
            MaxSpeedField = Field("maxSpeed"), AudioField = Field("audio");
        private static readonly FieldInfo[] ClipFields =
        {
            null, Field("woodSteps"), Field("sandSteps"), Field("stoneSteps"), Field("grassSteps"), Field("wetSteps"),
        };

        private static FootstepsAudio _source;
        private static CharacterController _controller;   // the one the game's footsteps read
        private static StepGround _heldGround;
        private static float _retryAt;
        private static readonly AudioClip[][] Clips = new AudioClip[ClipFields.Length][];

        public static float StepLength { get; private set; }
        public static float MaxSpeed { get; private set; }
        public static AudioSource StepTemplate { get; private set; }
        public static AudioSource SwimTemplate { get; private set; }
        public static float SwimVolume { get; private set; }

        private static FieldInfo Field(string name) => AccessTools.Field(typeof(FootstepsAudio), name);

        /// <summary>False until a world with the local player is loaded.</summary>
        public static bool Ready()
        {
            if (_source != null) return true;
            if (Time.unscaledTime < _retryAt) return false;
            _retryAt = Time.unscaledTime + 2f;
            try
            {
                FootstepsAudio source = UnityEngine.Object.FindObjectOfType<FootstepsAudio>();
                CharacterController controller = source != null ? source.GetComponent<CharacterController>() : null;
                if (controller == null) return false;

                for (int i = 1; i < ClipFields.Length; i++)
                    Clips[i] = ClipFields[i] != null ? ClipFields[i].GetValue(source) as AudioClip[] : null;
                StepLength = StepLengthField != null ? (float)StepLengthField.GetValue(source) : 0f;
                MaxSpeed = MaxSpeedField != null ? (float)MaxSpeedField.GetValue(source) : 0f;
                StepTemplate = AudioField != null ? AudioField.GetValue(source) as AudioSource : null;
                if (StepLength <= 0f) StepLength = 1.6f;
                if (MaxSpeed <= 0f) MaxSpeed = 6f;

                SwimTemplate = null;
                SwimEffects swim = UnityEngine.Object.FindObjectOfType<SwimEffects>();
                if (swim != null)
                {
                    SwimTemplate = swim.GetComponent<AudioSource>();
                    FieldInfo volume = AccessTools.Field(typeof(SwimEffects), "swimAudioVolume");
                    SwimVolume = volume != null ? (float)volume.GetValue(swim) : 0.2f;
                }

                _source = source;
                _controller = controller;
                _heldGround = StepGround.None;
                Plugin.Logger.LogInfo("[PlayerSteps] game sounds: stepLength=" + StepLength + " maxSpeed=" + MaxSpeed +
                                      " wood=" + Count(StepGround.Wood) + " sand=" + Count(StepGround.Sand) +
                                      " stone=" + Count(StepGround.Stone) + " grass=" + Count(StepGround.Grass) +
                                      " wet=" + Count(StepGround.Wet) + " stepSource=" + (StepTemplate != null) +
                                      " swim=" + (SwimTemplate != null && SwimTemplate.clip != null) +
                                      " | steps " + Describe(StepTemplate) + " | swim " + Describe(SwimTemplate));
                return true;
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning("[PlayerSteps] game sounds not read: " + error.Message);
                return false;
            }
        }

        // How the player hears the game's own sound: the remote copies are matched against it.
        private static string Describe(AudioSource source)
        {
            if (source == null) return "none";
            Camera camera = Camera.main;
            return "volume=" + source.volume.ToString("0.00") + " spatial=" + source.spatialBlend.ToString("0.00") +
                   " rolloff=" + source.rolloffMode + " min=" + source.minDistance.ToString("0.0") +
                   " max=" + source.maxDistance.ToString("0.0") +
                   " toCamera=" + (camera != null ? Vector3.Distance(camera.transform.position, source.transform.position).ToString("0.00") : "?");
        }

        /// <summary>
        /// How much of its volume the game's own step source keeps on the way to the player's
        /// ears: that source is a 3D one some two metres from the camera, with its own rolloff.
        /// A crewmate's step played at full volume next to you is several times louder than that.
        /// </summary>
        public static float OwnStepGain()
        {
            try
            {
                AudioSource source = StepTemplate;
                AudioListener ears = UnityEngine.Object.FindObjectOfType<AudioListener>();
                if (source == null || ears == null || source.spatialBlend < 0.5f) return 1f;

                float distance = Vector3.Distance(ears.transform.position, source.transform.position);
                float min = Mathf.Max(0.01f, source.minDistance), max = Mathf.Max(min + 0.01f, source.maxDistance);
                float gain;
                switch (source.rolloffMode)
                {
                    case AudioRolloffMode.Custom:
                        gain = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff).Evaluate(distance / max);
                        break;
                    case AudioRolloffMode.Linear:
                        gain = 1f - Mathf.InverseLerp(min, max, distance);
                        break;
                    default:
                        gain = min / Mathf.Max(min, distance);
                        break;
                }

                return Mathf.Clamp(gain, 0.05f, 1f);
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        private static int Count(StepGround ground) => Clips[(int)ground] != null ? Clips[(int)ground].Length : 0;

        public static AudioClip Pick(StepGround ground)
        {
            AudioClip[] clips = Clips[(int)ground];
            // The game has no grass steps in use; fall back so a later game version still sounds.
            if ((clips == null || clips.Length == 0) && ground == StepGround.Grass) clips = Clips[(int)StepGround.Sand];
            return clips == null || clips.Length == 0 ? null : clips[UnityEngine.Random.Range(0, clips.Length)];
        }

        /// <summary>What the local player stands on and whether it is in water, as bits for
        /// <c>PlayerStateMsg.Hands</c>. The ground is the clip set the game itself chose.</summary>
        public static byte SampleLocal()
        {
            try
            {
                if (!Ready() || _controller == null) return 0;

                bool inWater = PlayerSwimming.swimming || PlayerSwimming.inShallowWater;
                // The game takes character control away in a shop, at a map table, in a bed. The
                // player has not left the ground then: keep the last ground, or the others would
                // hear a landing each time control comes back.
                if (!_controller.enabled) return StepSignal.Pack(_heldGround, inWater);

                StepGround ground = StepGround.None;
                if (_controller.isGrounded && CurrentClips != null)
                {
                    object current = CurrentClips.GetValue(_source);
                    for (int i = 1; current != null && i < Clips.Length; i++)
                        if (ReferenceEquals(current, Clips[i])) { ground = (StepGround)i; break; }
                }

                _heldGround = ground;
                return StepSignal.Pack(ground, inWater);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>The steps and the swimming of one remote player, heard from where its avatar is.</summary>
    internal sealed class AvatarStepAudio
    {
        private const float MinDistance = 1.5f, MaxDistance = 22f;
        // Against the level the player hears its own steps at (StepSounds.OwnStepGain): a
        // crewmate standing next to you stays below your own steps.
        private const float CrewVolume = 0.7f;
        // Whatever the game's rolloff says, a crewmate is never played above half the game's volume.
        private const float MaxOwnGain = 0.5f;

        private readonly StepCadence _cadence = new StepCadence();
        private AudioSource _steps, _swim;
        private bool _failed;

        public void Tick(Transform avatar, byte hands, float speed, float dt)
        {
            if (_failed || avatar == null) return;
            try
            {
                if (!StepSounds.Ready()) return;

                StepGround ground = StepSignal.Ground(hands);
                if (_cadence.Advance(ground, speed, dt, StepSounds.StepLength, StepSounds.MaxSpeed))
                {
                    AudioClip clip = StepSounds.Pick(ground);
                    if (clip != null)
                    {
                        if (_steps == null) _steps = Source(avatar, "CoopSteps", StepSounds.StepTemplate, false);
                        if (_steps.isActiveAndEnabled) _steps.PlayOneShot(clip);
                    }
                }

                TickSwim(avatar, StepSignal.Swims(hands) ? speed : 0f, dt);
            }
            catch (Exception error)
            {
                _failed = true;
                Plugin.Logger.LogWarning("[PlayerSteps] avatar sound stopped: " + error.Message);
            }
        }

        // The game keeps one looping swim sound and moves its volume with the speed in water.
        private void TickSwim(Transform avatar, float speed, float dt)
        {
            AudioSource template = StepSounds.SwimTemplate;
            if (template == null || template.clip == null) return;
            if (_swim == null)
            {
                if (speed <= 0f) return;
                _swim = Source(avatar, "CoopSwim", template, true);
                _swim.clip = template.clip;
                _swim.volume = 0f;
            }

            _swim.volume = Mathf.Lerp(_swim.volume, Mathf.Clamp01(speed * StepSounds.SwimVolume) * MaxOwnGain * CrewVolume, dt * 2.5f);
            if (_swim.volume <= 0.01f) { if (_swim.isPlaying) _swim.Stop(); }
            else if (!_swim.isPlaying && _swim.isActiveAndEnabled) _swim.Play();
        }

        private static AudioSource Source(Transform avatar, string name, AudioSource template, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(avatar, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = MinDistance;
            source.maxDistance = MaxDistance;
            source.dopplerLevel = 0f;
            if (template != null)
            {
                // The game's mixer group, so being indoors or under water changes these too.
                source.outputAudioMixerGroup = template.outputAudioMixerGroup;
                source.pitch = template.pitch;
                if (!loop)
                {
                    float own = Mathf.Min(StepSounds.OwnStepGain(), MaxOwnGain);
                    source.volume = template.volume * own * CrewVolume;
                    Plugin.Logger.LogInfo("[PlayerSteps] crew step volume=" + source.volume.ToString("0.000") +
                                          " (game " + template.volume.ToString("0.00") + ", own steps reach the ears at " +
                                          own.ToString("0.00") + ")");
                }
            }

            return source;
        }
    }
}
