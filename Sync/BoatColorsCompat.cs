using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// BoatColors (com.nandbrew.boatcolors) repaints hull/cabin/trim materials via
    /// ShipColors.UpdateColor(string, Color) and ShipColors.ResetColor(string).
    /// Sync live repaints/resets across the crew and send the host's current palette to late joiners.
    /// </summary>
    public static class BoatColorsCompat
    {
        public const string Guid = "com.nandbrew.boatcolors";
        private const string AssemblyName = "BoatColors";

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Host => Net?.Role == Role.Host;
        private static bool Active => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true &&
            (Host || (Net.HostModFeatures & ModFeatures.BoatColors) != 0);

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static MethodInfo _updateColor;
        private static MethodInfo _resetColor;
        private static FieldInfo _boatMats;
        private static bool _applying;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _updateColor = null;
            _resetColor = null;
            _boatMats = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                    return;
                IsInstalled = true;
                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                var type = asm?.GetType("BoatColors.ShipColors");
                _updateColor = type != null ? AccessTools.Method(type, "UpdateColor", new[] { typeof(string), typeof(Color) }) : null;
                _resetColor = type != null ? AccessTools.Method(type, "ResetColor", new[] { typeof(string) }) : null;
                _boatMats = type != null ? AccessTools.Field(type, "boatMats") : null;
                SyncEnabled = _updateColor != null && _boatMats != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo("[BoatColorsCompat] BoatColors detected; live paint sync enabled.");
                else
                    Plugin.Logger.LogWarning("[BoatColorsCompat] BoatColors detected, but ShipColors members did not resolve.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BoatColorsCompat] Detection threw: " + e.Message);
            }
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled || _updateColor == null) return;
            try
            {
                harmony.Patch(_updateColor,
                    prefix: new HarmonyMethod(typeof(BoatColorsCompat), nameof(PreUpdateColor)),
                    postfix: new HarmonyMethod(typeof(BoatColorsCompat), nameof(PostUpdateColor)));
                if (_resetColor != null)
                    harmony.Patch(_resetColor,
                        prefix: new HarmonyMethod(typeof(BoatColorsCompat), nameof(PreResetColor)),
                        postfix: new HarmonyMethod(typeof(BoatColorsCompat), nameof(PostResetColor)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[BoatColorsCompat] Patching threw: " + e.Message);
            }
        }

        public static void Reset()
        {
            _applying = false;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static Material MaterialOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var dict = _boatMats.GetValue(null) as IDictionary;
            return dict != null && dict.Contains(key) ? dict[key] as Material : null;
        }

        private static bool Same(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

        private struct PaintState
        {
            public bool Known;
            public Color Before;
        }

        private static void PreUpdateColor(string __0, out PaintState __state)
        {
            __state = default;
            try
            {
                var mat = MaterialOf(__0);
                if (mat != null) __state = new PaintState { Known = true, Before = mat.color };
            }
            catch { __state = default; }
        }

        private static void PostUpdateColor(string __0, Color __1, PaintState __state)
        {
            try
            {
                if (_applying || !Active || string.IsNullOrEmpty(__0)) return;
                // Unknown key (boat not cached) changes nothing. Re-applying the current colour is an echo:
                // BoatColors' EnterBoat postfix loads the boat's colours into its config sliders, whose
                // SettingChanged calls UpdateColor with the colour already shown.
                if (!__state.Known || Same(__state.Before, __1)) return;
                Net.SendMod(new BoatColorStateMsg
                {
                    Key = __0,
                    R = __1.r,
                    G = __1.g,
                    B = __1.b,
                    A = __1.a,
                }, ModFeatures.BoatColors, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatColorsCompat] PostUpdateColor: " + e.Message);
            }
        }

        private static void PreResetColor(string __0, out PaintState __state) => PreUpdateColor(__0, out __state);

        private static void PostResetColor(string __0, PaintState __state)
        {
            try
            {
                if (_applying || !Active || string.IsNullOrEmpty(__0)) return;
                var mat = MaterialOf(__0);
                if (mat == null) return;
                Color c = mat.color;
                if (!__state.Known || Same(__state.Before, c)) return;   // already the default colour
                Net.SendMod(new BoatColorStateMsg { Key = __0, R = c.r, G = c.g, B = c.b, A = c.a },
                    ModFeatures.BoatColors, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatColorsCompat] PostResetColor: " + e.Message);
            }
        }

        public static void OnRemote(BoatColorStateMsg msg, NetPeer fromPeer)
        {
            if (!Active || msg == null || string.IsNullOrEmpty(msg.Key)) return;
            if (!IsFinite(msg.R) || !IsFinite(msg.G) || !IsFinite(msg.B) || !IsFinite(msg.A)) return;
            try
            {
                _applying = true;
                try
                {
                    _updateColor.Invoke(null, new object[] { msg.Key, new Color(msg.R, msg.G, msg.B, msg.A) });
                }
                finally { _applying = false; }

                if (Host)
                    Net.SendMod(msg, ModFeatures.BoatColors, DeliveryMethod.ReliableOrdered, except: fromPeer);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatColorsCompat] OnRemote: " + e.Message);
            }
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || !SyncEnabled || peer == null) return;
            if ((Net.FeaturesOf(peer) & ModFeatures.BoatColors) == 0) return;
            try
            {
                var dict = _boatMats.GetValue(null) as IDictionary;
                if (dict == null) return;
                foreach (DictionaryEntry entry in dict)
                {
                    string key = entry.Key as string;
                    var mat = entry.Value as Material;
                    if (string.IsNullOrEmpty(key) || mat == null) continue;
                    Color c = mat.color;
                    peer.Send(new BoatColorStateMsg { Key = key, R = c.r, G = c.g, B = c.b, A = c.a }, DeliveryMethod.ReliableOrdered);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[BoatColorsCompat] ResyncTo: " + e.Message);
            }
        }
    }
}
