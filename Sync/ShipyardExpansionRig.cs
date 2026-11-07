using System;
using System.Reflection;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The per-sail data of the Shipyard Expansion mod (sail angle, jib flip, texture), which lives
    /// outside the game's customization data: in <c>GameState.modData["SEboatSails.{sceneIndex}"]</c>.
    /// SE applies it only from its postfix on <c>SaveableBoatCustomization.LoadData</c>; a refit
    /// rebuilds sails with <c>Mast.LoadSail</c> and never runs that postfix, so the entry is carried in
    /// <c>RefitBody.ModRig</c> and applied here. Everything goes through SE's own public
    /// <c>ShipyardExpansion.SailDataManager.SaveSailConfig/LoadSailConfig(BoatRefs)</c>, found by
    /// reflection; without SE (or when its API moved) every call is a no-op.
    /// </summary>
    internal static class ShipyardExpansionRig
    {
        private const string Guid = "com.nandbrew.shipyardexpansion";
        private const string KeyPrefix = "SEboatSails.";
        /// <summary>Wire ceiling for one boat's entry.</summary>
        internal const int MaxChars = 16 * 1024;

        private static bool _resolved;
        private static MethodInfo _save, _load;
        private static CoopLog.Repeat _faults;

        internal static bool Present => Resolve();

        private static bool Resolve()
        {
            if (_resolved) return _save != null && _load != null;
            _resolved = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out BepInEx.PluginInfo info) || info?.Instance == null)
                    return false;
                Type manager = info.Instance.GetType().Assembly.GetType("ShipyardExpansion.SailDataManager");
                var args = new[] { typeof(BoatRefs) };
                _save = manager?.GetMethod("SaveSailConfig", BindingFlags.Public | BindingFlags.Static, null, args, null);
                _load = manager?.GetMethod("LoadSailConfig", BindingFlags.Public | BindingFlags.Static, null, args, null);
                if (_save == null || _load == null)
                    Plugin.Logger.LogWarning("[Shipyard] Shipyard Expansion is loaded but SailDataManager.SaveSailConfig/LoadSailConfig " +
                                             "were not found; sail angle, flip and texture will not follow refits");
                else Plugin.Logger.LogInfo("[Shipyard] Shipyard Expansion found: its sail data follows refits");
            }
            catch (Exception e)
            {
                _save = _load = null;
                Plugin.Logger.LogWarning("[Shipyard] Shipyard Expansion lookup failed: " + e.Message);
            }
            return _save != null && _load != null;
        }

        private static bool Parts(SaveableBoatCustomization custom, out BoatRefs refs, out string key)
        {
            refs = null; key = null;
            if (custom == null || GameState.modData == null) return false;
            refs = custom.GetComponent<BoatRefs>();
            var saveable = custom.GetComponent<SaveableObject>();
            if (refs == null || saveable == null) return false;
            key = KeyPrefix + saveable.sceneIndex;
            return true;
        }

        /// <summary>SE's entry for the boat as it stands now; empty without SE. SE rewrites the entry
        /// from the live sails; when it gives up (a sail without its scaler, or its own debug switch),
        /// the previous entry is put back and nothing is returned.</summary>
        internal static string Capture(SaveableBoatCustomization custom)
        {
            if (!Resolve() || !Parts(custom, out BoatRefs refs, out string key)) return "";
            GameState.modData.TryGetValue(key, out string before);
            GameState.modData.Remove(key);
            try { _save.Invoke(null, new object[] { refs }); }
            catch (Exception e) { Fault("capture", e); }
            if (GameState.modData.TryGetValue(key, out string now) && !string.IsNullOrEmpty(now) && now.Length <= MaxChars) return now;
            if (before != null) GameState.modData[key] = before;
            else GameState.modData.Remove(key);
            return "";
        }

        /// <summary>Writes the sender's entry under this machine's key for the boat and lets SE apply it
        /// to the sails just rebuilt. Without an entry (the sender has no SE) SE's stored entry is
        /// rewritten from the rebuilt sails instead, so the saved data never describes older sails.</summary>
        internal static void Apply(SaveableBoatCustomization custom, string rig)
        {
            if (!Resolve() || !Parts(custom, out BoatRefs refs, out string key)) return;
            if (string.IsNullOrEmpty(rig)) { Capture(custom); return; }
            GameState.modData[key] = rig;
            try { _load.Invoke(null, new object[] { refs }); }
            catch (Exception e) { Fault("apply", e); }
        }

        /// <summary>SE's <c>LoadSailConfig</c> can flip a jib but never flips one back, so it can bring
        /// existing sails to an entry only when no sail goes from flipped to not flipped. True when the
        /// two entries differ in any sail's flip: the sails must then be rebuilt. Entry format:
        /// <c>mast(prefab,scaleZ,scaleY,angle,flipped,texture]...)...|version</c>.</summary>
        internal static bool FlipsDiffer(string current, string wanted)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(wanted)) return false;
            var a = Flips(current); var b = Flips(wanted);
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return true;
            return false;
        }

        private static System.Collections.Generic.List<bool> Flips(string rig)
        {
            var flips = new System.Collections.Generic.List<bool>();
            int bar = rig.IndexOf('|');
            string body = bar >= 0 ? rig.Substring(0, bar) : rig;
            foreach (string token in body.Split(']'))
            {
                string sail = token.Substring(token.LastIndexOf('(') + 1);
                string[] fields = sail.Split(',');
                if (fields.Length < 4) continue; // ")" between masts
                flips.Add(fields.Length >= 5 && string.Equals(fields[4].Trim(), "true", StringComparison.OrdinalIgnoreCase));
            }
            return flips;
        }

        /// <summary>Puts a known entry back without touching the sails: after SE's own <c>GetData</c>
        /// postfix wrote the preview of a boat being edited.</summary>
        internal static void Restore(SaveableBoatCustomization custom, string rig)
        {
            if (string.IsNullOrEmpty(rig) || !Resolve() || !Parts(custom, out BoatRefs refs, out string key)) return;
            GameState.modData[key] = rig;
        }

        private static void Fault(string stage, Exception e)
        {
            Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            Plugin.Logger?.ReportError("[Shipyard] Shipyard Expansion " + stage, inner.Message, ref _faults);
        }
    }
}
