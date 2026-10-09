using System;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Keeps the game's wind sound alive. <c>WindSound.Update</c> divides the distance moved by
    /// <c>Time.deltaTime</c> and blends the result into <c>apparentWind</c>. In a frame where the
    /// clock was started again after a pause, <c>timeScale</c> is already above zero while
    /// <c>deltaTime</c> is still zero: the division gives infinity, the blend turns it into NaN, and
    /// a NaN never leaves that blend. From then on the sound stops following the wind and the game
    /// writes "Attempt to set pitch to infinite value" to its log every frame. The co-op join pause
    /// starts the clock from <c>Update</c>, which is where this can happen.
    /// </summary>
    public static class WindSoundPatches
    {
        private static bool _zeroStepSeen, _brokenSeen;

        public static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(WindSound), "Update", Type.EmptyTypes, target => harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(WindSoundPatches), nameof(PreUpdate))));
            PatchHealth.Report("Wind sound", hooks);
            Plugin.Logger.LogInfo("[WindSoundPatches] " + hooks.Detail);
        }

        private static bool PreUpdate(ref Vector3 ___apparentWind)
        {
            try
            {
                if (Broken(___apparentWind))
                {
                    // Reached only if something other than the zero step below broke it; say what
                    // the game looked like, once, and let the sound follow the wind again.
                    if (!_brokenSeen)
                    {
                        _brokenSeen = true;
                        Plugin.Logger.LogWarning("[WindSoundPatches] wind sound state was not a number, reset: " + Context() +
                                                 " wind=" + Wind.currentWind);
                    }

                    ___apparentWind = Vector3.zero;
                }

                if (Time.timeScale > 0f && Time.deltaTime <= 0f)
                {
                    if (!_zeroStepSeen)
                    {
                        _zeroStepSeen = true;
                        Plugin.Logger.LogInfo("[WindSoundPatches] skipped a frame with no time step: " + Context());
                    }

                    return false;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning("[WindSoundPatches] " + error);
            }

            return true;
        }

        private static bool Broken(Vector3 v)
            => float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
               float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);

        private static string Context()
            => "role=" + CoopBehaviour.Instance?.Net?.Role + " timeScale=" + Time.timeScale + " deltaTime=" + Time.deltaTime +
               " frame=" + Time.frameCount + " joinPause=" + (CoopBehaviour.Instance?.Pause?.Active == true) +
               " hostPause=" + (CoopBehaviour.Instance?.HostPause?.Frozen == true);
    }
}
