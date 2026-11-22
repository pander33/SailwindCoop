using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// FreeCameraAndWaterCollision (com.DogEggz.sailwind.freecameraandcollision) lets a player
    /// preview time-of-day in Photo Mode (mutating Sun.sun.globalTime) and detach the camera
    /// (disabling Refs.ovrController). Keep Photo Mode time local and defer HostPauseSync's
    /// controller restore until free camera exits.
    /// </summary>
    public static class FreeCamCompat
    {
        public const string Guid = "com.DogEggz.sailwind.freecameraandcollision";

        private static bool _probed;
        private static PropertyInfo _photoModeIsOpen;
        private static PropertyInfo _freeCamIsOn;

        public static bool IsPhotoModeOpen
        {
            get
            {
                EnsureProbed();
                if (_photoModeIsOpen == null) return false;
                try { return (bool)_photoModeIsOpen.GetValue(null, null); }
                catch { return false; }
            }
        }

        public static bool IsFreeCameraOn
        {
            get
            {
                EnsureProbed();
                if (_freeCamIsOn == null) return false;
                try { return (bool)_freeCamIsOn.GetValue(null, null); }
                catch { return false; }
            }
        }

        public static void Init()
        {
            _probed = false;
            _photoModeIsOpen = null;
            _freeCamIsOn = null;
            EnsureProbed();
        }

        private static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "FreeCameraAndCollision");
                if (asm == null) return;

                var photoType = asm.GetType("FreeCameraAndCollision.PhotoMode");
                var camType = asm.GetType("FreeCameraAndCollision.FreeCameraController");
                _photoModeIsOpen = photoType != null ? AccessTools.Property(photoType, "IsOpen") : null;
                _freeCamIsOn = camType != null ? AccessTools.Property(camType, "IsFreeCameraOn") : null;

                if (_photoModeIsOpen != null || _freeCamIsOn != null)
                    Plugin.Logger.LogInfo("[FreeCamCompat] FreeCameraAndWaterCollision detected; PhotoMode/FreeCam guards enabled.");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[FreeCamCompat] Detection threw: " + e.Message);
            }
        }
    }
}
