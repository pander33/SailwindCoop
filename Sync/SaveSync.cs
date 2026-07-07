using System;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// P4.4 — save policy. We play in the HOST's world (host's save). A connected guest must not write
    /// the world to disk: autosave, sleep-save and manual save would all clobber the guest's own local
    /// save with the host's world (or spawn junk saves). <see cref="SavePatches"/> blocks the single
    /// choke point <c>SaveLoadManager.SaveGame(bool)</c> on the client; the host saves normally. The guard
    /// is dynamic (role + link state) so single-player saving is restored the moment the guest disconnects.
    /// </summary>
    public static class SavePatches
    {
        public static void Apply(Harmony harmony)
        {
            bool save = TryPatch(harmony, typeof(SaveLoadManager), "SaveGame", new[] { typeof(bool) }, nameof(PreSaveGame));
            Plugin.Logger.LogInfo("[SavePatches] Save patch (guest does not write host world): SaveGame=" + save);
            SailwindCoop.Runtime.PatchHealth.Report("Save", save ? 1 : 0, 1);
        }

        /// <summary>True while we are a connected guest — saving the host's world locally is suppressed.</summary>
        private static bool GuestConnected()
        {
            var net = CoopBehaviour.Instance != null ? CoopBehaviour.Instance.Net : null;
            return net != null && net.Role == Role.Client && net.State == LinkState.Connected;
        }

        private static bool TryPatch(Harmony harmony, Type type, string method, Type[] args, string prefixName)
        {
            try
            {
                var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (mi == null) return false;
                var prefix = new HarmonyMethod(typeof(SavePatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(mi, prefix: prefix);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SavePatches] " + method + ": " + e.Message);
                return false;
            }
        }

        // Return false to skip vanilla SaveGame on a connected guest.
        private static bool PreSaveGame()
        {
            try
            {
                if (GuestConnected())
                {
                    // Don't write the host's world to the guest's disk; persist the guest's CHARACTER instead.
                    CoopProfile.SaveFromGame();
                    Plugin.Logger.LogInfo("[SavePatches] Host world not saved; guest character profile was written");
                    return false;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SavePatches] PreSaveGame: " + e.Message); }
            return true;
        }
    }
}
