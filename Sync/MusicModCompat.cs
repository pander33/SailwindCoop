using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// MusicMod (Viking-D-Mike, com.vikingdmike.musicmod) adds a lute, a xiao flute, nakers and a hurdy-gurdy. They are
    /// ordinary ShipItems, so item sync already carries them, but every note is read from this machine's keyboard and
    /// played from the item's own AudioSource, so a crewmate's playing was silent everywhere else.
    ///
    /// Lute and nakers: each keypress calls InstrumentoItem.Golpe(note), a one-shot. We send it and call Golpe on the
    /// crewmate's copy. Flute and hurdy-gurdy: FlautaItem.LeerTeclas(activo) polls the keys every frame for a held note,
    /// with crossfades and a pitch glide, and the hurdy-gurdy adds a drone while play mode is on. We send play mode and
    /// the held note when either changes. On the other machines the item is not in anyone's hand there (held is null),
    /// so MusicMod calls LeerTeclas(false); a prefix turns that into true for an instrument a crewmate is playing, and
    /// the key reads inside LeerTeclas are rerouted to answer from that crewmate's state. MusicMod's own sound code
    /// then plays it exactly as it does for the player holding it.
    ///
    /// Built against 1.1.2. All reflected, because the mod may be absent, and a miss turns this off with a warning.
    /// </summary>
    public static class MusicModCompat
    {
        public const string Guid = "com.vikingdmike.musicmod";
        private const string AssemblyName = "LaudMod";

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static Type _instrumentType;    // LaudMod.InstrumentoItem
        private static Type _fluteType;         // LaudMod.FlautaItem (the hurdy-gurdy derives from it)
        private static MethodInfo _strike;      // protected void Golpe(int)
        private static MethodInfo _fluteRead;   // protected override void LeerTeclas(bool), FlautaItem
        private static MethodInfo _gurdyRead;   // protected override void LeerTeclas(bool), ZanfonaItem (may be absent)
        private static FieldInfo _playing;      // protected bool tocando (InstrumentoItem)
        private static FieldInfo _note;         // private int notaActual (FlautaItem)
        private static PropertyInfo _data;      // public abstract Instrumento Datos
        private static FieldInfo _keys;         // public ConfigEntry<KeyCode>[] Teclas (Instrumento)

        private static readonly MethodInfo InputGetKeyDown = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) });
        private static readonly MethodInfo InputGetKey = AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) });

        // While this machine plays a crewmate's strike, Golpe must not send it back.
        private static bool _applying;

        /// <summary>A crewmate is playing this flute or hurdy-gurdy. Key is this machine's binding for their note.</summary>
        private class RemoteVoice
        {
            public uint Actor;
            public float Since;
            public int Note = -1;
            public KeyCode Key = KeyCode.None;
        }

        private static readonly Dictionary<ShipItem, RemoteVoice> _remote = new Dictionary<ShipItem, RemoteVoice>();

        /// <summary>What we last told the crew about each held-note instrument in our own hands.</summary>
        private struct SentState
        {
            public bool Playing;
            public int Note;
        }

        private static readonly Dictionary<ShipItem, SentState> _sent = new Dictionary<ShipItem, SentState>();
        private static readonly List<ShipItem> _scratch = new List<ShipItem>();

        // Instruments found by instance id. A lute strike arrives per keypress, and a scene-wide search for each one
        // would be far too slow.

        // A voice outlives the instrument leaving its player's hands by at most this long. A held note is sent after
        // the pickup, so this only covers a pickup that has not registered here yet.
        private const float VoiceGrace = 2f;
        private const float TickInterval = 0.5f;
        private static float _nextTick;

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Logger.LogInfo("[MusicCompat] MusicMod not installed.");
                    return;
                }
                IsInstalled = true;
                string version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);

                _instrumentType = asm?.GetType("LaudMod.InstrumentoItem");
                _fluteType = asm?.GetType("LaudMod.FlautaItem");
                var gurdyType = asm?.GetType("LaudMod.ZanfonaItem");
                var dataType = asm?.GetType("LaudMod.Instrumento");

                _strike = _instrumentType != null ? AccessTools.Method(_instrumentType, "Golpe", new[] { typeof(int) }) : null;
                _playing = _instrumentType != null ? AccessTools.Field(_instrumentType, "tocando") : null;
                _data = _instrumentType != null ? AccessTools.Property(_instrumentType, "Datos") : null;
                _fluteRead = _fluteType != null ? AccessTools.DeclaredMethod(_fluteType, "LeerTeclas", new[] { typeof(bool) }) : null;
                _note = _fluteType != null ? AccessTools.Field(_fluteType, "notaActual") : null;
                _gurdyRead = gurdyType != null ? AccessTools.DeclaredMethod(gurdyType, "LeerTeclas", new[] { typeof(bool) }) : null;
                _keys = dataType != null ? AccessTools.Field(dataType, "Teclas") : null;

                SyncEnabled = _strike != null && _playing != null && _data != null && _fluteRead != null && _note != null
                              && _keys != null && InputGetKeyDown != null && InputGetKey != null;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo($"[MusicCompat] MusicMod v{version} detected; instrument sync enabled" +
                                       (_gurdyRead == null ? " (no hurdy-gurdy in this version)." : "."));
                else
                    Plugin.Logger.LogWarning($"[MusicCompat] MusicMod v{version} detected, but its members did not resolve. " +
                        "Instrument sync disabled, so crewmates will not hear each other play. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[MusicCompat] Detection threw. Instrument sync disabled. " + e);
            }
        }

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                harmony.Patch(_strike, postfix: new HarmonyMethod(typeof(MusicModCompat), nameof(StrikePostfix)));
                harmony.Patch(_fluteRead,
                    prefix: new HarmonyMethod(typeof(MusicModCompat), nameof(ReadPrefix)),
                    postfix: new HarmonyMethod(typeof(MusicModCompat), nameof(FluteReadPostfix)),
                    transpiler: new HarmonyMethod(typeof(MusicModCompat), nameof(FluteReadTranspiler)));
                // The hurdy-gurdy's drone and crank run off its own activo before it calls the flute's LeerTeclas.
                if (_gurdyRead != null)
                    harmony.Patch(_gurdyRead, prefix: new HarmonyMethod(typeof(MusicModCompat), nameof(ReadPrefix)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[MusicCompat] Patching threw. Instrument sync disabled. " + e);
            }
        }

        public static void Reset()
        {
            _remote.Clear();
            _sent.Clear();
            _applying = false;
        }

        // === Sending ===

        private static void StrikePostfix(ShipItem __instance, int nota)
            => PatchGuard.Run(() => StrikePostfixCore(__instance, nota), Report);

        private static void StrikePostfixCore(ShipItem __instance, int nota)
        {
            if (!CrewMusic.Active || _applying || __instance == null || __instance.held == null) return;
            CrewMusic.Send(__instance, CrewNoteKind.Strike, nota, true);
        }

        private static void FluteReadPostfix(ShipItem __instance)
        {
            try { FluteReadPostfixCore(__instance); }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void FluteReadPostfixCore(ShipItem __instance)
        {
            if (!CrewMusic.Active || __instance == null) return;

            bool inOurHands = __instance.held != null;
            bool known = _sent.TryGetValue(__instance, out var last);
            if (!inOurHands && !known) return;

            var now = new SentState { Playing = false, Note = -1 };
            if (inOurHands)
            {
                now.Playing = (bool)_playing.GetValue(__instance);
                now.Note = now.Playing ? (int)_note.GetValue(__instance) : -1;
            }
            bool silent = !now.Playing && now.Note < 0;
            if (known ? (now.Playing == last.Playing && now.Note == last.Note) : silent) return;

            if (CrewMusic.Send(__instance, CrewNoteKind.Voice, now.Note, now.Playing))
            {
                if (silent) _sent.Remove(__instance);
                else _sent[__instance] = now;
            }
        }

        // === Receiving ===

        public static void OnRemoteNote(CrewNoteMsg packet)
        {
            if (packet.Kind == CrewNoteKind.Horn) { VikingHornCompat.OnRemoteHorn(packet); return; }
            if (!SyncEnabled) return;

            var item = ItemSync.Instance?.FindSharedItem(packet.ItemInstanceId, packet.PrefabIndex);
            if (item == null || !_instrumentType.IsInstanceOfType(item)) return;
            // Our own hands win: the player here picked it up, and their keys drive it.
            if (item.held != null) { _remote.Remove(item); return; }

            if (packet.Kind == CrewNoteKind.Strike)
            {
                _applying = true;
                try { _strike.Invoke(item, new object[] { (int)packet.Note }); }
                catch (Exception e) { Plugin.Logger.LogWarning($"[MusicCompat] Could not play a crewmate's note on {item.name}: {e.Message}"); }
                finally { _applying = false; }
                return;
            }

            if (!_fluteType.IsInstanceOfType(item)) return;
            if (!packet.Playing)
            {
                if (_remote.TryGetValue(item, out var ending) && ending.Actor == packet.Actor) _remote.Remove(item);
                return;
            }
            if (!_remote.TryGetValue(item, out var voice) || voice.Actor != packet.Actor)
            {
                voice = new RemoteVoice { Actor = packet.Actor, Since = Time.unscaledTime };
                _remote[item] = voice;
            }
            voice.Note = packet.Note;
            voice.Key = KeyFor(item, packet.Note);
        }

        /// <summary>
        /// Drops voices whose instrument is gone, has come into our own hands, or is no longer in any crewmate's hands
        /// (put down, or its player left). Called from the item tick.
        /// </summary>
        public static void Tick()
        {
            if (_remote.Count == 0 || Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + TickInterval;

            _scratch.Clear();
            foreach (var kvp in _remote)
            {
                if (kvp.Key == null || kvp.Key.held != null) { _scratch.Add(kvp.Key); continue; }
                bool carried = ItemSync.Instance?.HeldByRemotePlayer(kvp.Key, kvp.Value.Actor) == true;
                if (!carried && Time.unscaledTime - kvp.Value.Since > VoiceGrace) _scratch.Add(kvp.Key);
            }
            foreach (var item in _scratch) _remote.Remove(item);
            _scratch.Clear();
        }

        // A crewmate's flute or hurdy-gurdy plays as if it were in a hand here.
        private static void ReadPrefix(ShipItem __instance, ref bool activo)
        {
            if (_remote.Count == 0 || __instance == null || __instance.held != null) return;
            if (_remote.ContainsKey(__instance)) activo = true;
        }

        private static IEnumerable<CodeInstruction> FluteReadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var keyDown = AccessTools.Method(typeof(MusicModCompat), nameof(KeyDown));
            var key = AccessTools.Method(typeof(MusicModCompat), nameof(KeyHeld));
            int swapped = 0;
            foreach (var ci in instructions)
            {
                bool isDown = ci.Calls(InputGetKeyDown);
                if (isDown || ci.Calls(InputGetKey))
                {
                    // The KeyCode is already on the stack; add `this` after it and call our (KeyCode, ShipItem) version.
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    self.labels.AddRange(ci.labels);
                    self.blocks.AddRange(ci.blocks);
                    ci.labels.Clear();
                    ci.blocks.Clear();
                    yield return self;
                    yield return new CodeInstruction(OpCodes.Call, isDown ? keyDown : key) { labels = ci.labels, blocks = ci.blocks };
                    swapped++;
                    continue;
                }
                yield return ci;
            }
            if (swapped == 0)
                Plugin.Logger.LogWarning("[MusicCompat] Found no key reads in the flute's LeerTeclas; held notes from crewmates will be silent.");
        }

        // The flute's loop sets the last key reported down as the note, then keeps it while it is still held, so
        // answering "down" and "held" for the crewmate's note every frame plays exactly that note.
        public static bool KeyDown(KeyCode code, ShipItem self)
        {
            if (_remote.Count != 0 && self != null && self.held == null && _remote.TryGetValue(self, out var voice))
                return voice.Note >= 0 && code == voice.Key && code != KeyCode.None;
            return Input.GetKeyDown(code);
        }

        public static bool KeyHeld(KeyCode code, ShipItem self)
        {
            if (_remote.Count != 0 && self != null && self.held == null && _remote.TryGetValue(self, out var voice))
                return voice.Note >= 0 && code == voice.Key && code != KeyCode.None;
            return Input.GetKey(code);
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[MusicCompat] " + error.Message);

        // === Helpers ===

        // Notes travel as indices, because each player binds their own keys; the receiver plays it through whatever
        // key it has bound to that index. Two notes bound to one key here would play the higher of the two.
        private static KeyCode KeyFor(ShipItem item, int note)
        {
            if (note < 0) return KeyCode.None;
            try
            {
                var data = _data.GetValue(item, null);
                var keys = data != null ? _keys.GetValue(data) as ConfigEntry<KeyCode>[] : null;
                if (keys == null || note >= keys.Length || keys[note] == null) return KeyCode.None;
                return keys[note].Value;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[MusicCompat] Could not read the note keys of {item.name}: {e.Message}");
                return KeyCode.None;
            }
        }

    }
}
