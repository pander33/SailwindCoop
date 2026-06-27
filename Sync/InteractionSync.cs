using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Stage 2 — general shared interaction. Everything a player can click on the boat is a
    /// <see cref="GoPointerButton"/>: cleats, quick-release levers, mooring lines, hatches, etc.
    /// Continuous drags (winch/wheel) already flow as values through <see cref="ControlsSync"/>;
    /// this handles the <i>discrete</i> press/alt-press interactions that don't.
    ///
    /// <para>The flow follows F3 (request → host applies → state syncs back): when the local
    /// client triggers a button, a Harmony postfix forwards a <see cref="ControlEventMsg"/> with
    /// the button's index (its position in the boat's <c>GetComponentsInChildren&lt;GoPointerButton&gt;</c>
    /// order — identical on both machines). The host looks the button up and <b>replays the same
    /// handler</b>, so the authoritative game logic runs host-side and the result reaches everyone
    /// through the normal control/state broadcasts.</para>
    ///
    /// <para>Two safety exclusions on the <c>Activate</c> (primary) path, because replaying it on
    /// the host with the host's own pointer would hijack the host: continuous grabs
    /// (<c>GPButtonRopeWinch</c>/<c>GPButtonSteeringWheel</c>, handled by the value channel) and
    /// pickups (<c>PickupableItem</c>, which would put the item in the host's hands). Their
    /// <c>AltActivate</c> (untie/unmoor/quick-release) is still forwarded.</para>
    /// </summary>
    public sealed class InteractionSync
    {
        public static InteractionSync Instance { get; private set; }

        private readonly CoopNet _net;
        private PlayerEmbarkerNew _emb;

        private Transform _cachedBoat;
        private GoPointerButton[] _buttons = Array.Empty<GoPointerButton>();
        private readonly Dictionary<GoPointerButton, int> _index = new Dictionary<GoPointerButton, int>();

        private GoPointer _hostPointer;
        private bool _replaying;          // guard so the host's replay doesn't re-forward

        // Overlay diagnostics.
        private string _lastEvent = "—";
        private long _lastEventTick;
        public string LastEventText
        {
            get
            {
                if (_lastEventTick == 0) return "—";
                long age = _net.Clock.ServerTick - _lastEventTick;
                if (age < 0) age = 0;
                return _lastEvent + " " + age + "мс";
            }
        }
        public int ButtonCount => _buttons.Length;

        public InteractionSync(CoopNet net) { _net = net; Instance = this; }

        /// <summary>
        /// True if this interaction must NOT run on the local machine: a connected client
        /// triggering a HOST-ONLY action (sleep/time, economy, missions, save). The Harmony
        /// prefix skips the original handler so the guest can't affect the host's world.
        /// </summary>
        public static bool ShouldBlockLocally(GoPointerButton btn)
        {
            var self = Instance;
            if (self == null || btn == null) return false;
            if (self._net.Role != Role.Client || self._net.State != LinkState.Connected) return false;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.HostOnly) return false;

            self.Remember("блок(host-only) '" + ButtonLabel(btn) + "'");
            return true;
        }

        // -----------------------------------------------------------------
        // Boat binding (both roles need a consistent button index)
        // -----------------------------------------------------------------

        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected) return;
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            if (_emb == null) _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            Transform boat = _emb != null ? _emb.debugOutCurrentBoat : null;
            if (boat == _cachedBoat) return;

            _cachedBoat = boat;
            _index.Clear();
            if (boat == null)
            {
                _buttons = Array.Empty<GoPointerButton>();
                return;
            }

            _buttons = boat.GetComponentsInChildren<GoPointerButton>(true);
            for (int i = 0; i < _buttons.Length; i++)
                if (_buttons[i] != null) _index[_buttons[i]] = i;

            Plugin.Logger.LogInfo("[InteractionSync] Корабль сменился: кнопок=" + _buttons.Length +
                                  (boat != null ? " ('" + boat.name + "')" : ""));
        }

        // -----------------------------------------------------------------
        // Client: a local interaction happened (called from the Harmony postfix)
        // -----------------------------------------------------------------

        /// <summary>
        /// Forward the local player's discrete interaction to the host. No-op unless we're a
        /// connected client doing this for real (not while replaying a remote event).
        /// </summary>
        public void NotifyLocalInteract(GoPointerButton btn, InteractKind kind)
        {
            if (_replaying) return;
            if (_net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (btn == null) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;  // only SHARED is forwarded
            if (IsExcluded(btn, kind)) return;

            RefreshButtons();
            if (!_index.TryGetValue(btn, out int idx)) return;   // not a button on the shared boat

            _net.Broadcast(new ControlEventMsg { Index = (ushort)idx, Kind = kind },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
            Remember("исх " + kind + " #" + idx + " '" + ButtonLabel(btn) + "'");
        }

        // -----------------------------------------------------------------
        // Host: replay a client's interaction authoritatively
        // -----------------------------------------------------------------

        public void OnControlEvent(ControlEventMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            if (_net.Role != Role.Host) return;
            RefreshButtons();

            int i = msg.Index;
            if (i < 0 || i >= _buttons.Length) return;
            var btn = _buttons[i];
            if (btn == null) return;
            if (InteractionPolicy.Classify(btn) != InteractPolicy.Shared) return;
            if (IsExcluded(btn, msg.Kind)) return;   // never replay something we'd never forward

            string method = msg.Kind == InteractKind.AltActivate ? "OnAltActivate" : "OnActivate";
            Remember("вх " + msg.Kind + " #" + i + " '" + ButtonLabel(btn) + "'");

            try
            {
                if (_hostPointer == null) _hostPointer = UnityEngine.Object.FindObjectOfType<GoPointer>();
                var mi = btn.GetType().GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(GoPointer) }, null);
                if (mi == null)
                {
                    Plugin.Logger.LogWarning("[InteractionSync] У '" + btn.GetType().Name + "' нет " + method + "(GoPointer)");
                    return;
                }
                _replaying = true;
                mi.Invoke(btn, new object[] { _hostPointer });
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[InteractionSync] Ошибка воспроизведения " + method + " на '" +
                                         btn.GetType().Name + "': " + e.Message);
            }
            finally
            {
                _replaying = false;
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Whether this interaction is handled elsewhere or is unsafe to replay host-side.
        /// Primary-click grabs of winch/wheel go through the value channel; primary-click pickups
        /// would put the item in the host's hands. Their alt action is still allowed.
        /// </summary>
        private static bool IsExcluded(GoPointerButton btn, InteractKind kind)
        {
            if (kind != InteractKind.Activate) return false;
            return btn is GPButtonRopeWinch
                || btn is GPButtonSteeringWheel
                || btn is PickupableItem;
        }

        private static string ButtonLabel(GoPointerButton btn)
        {
            if (btn == null) return "—";
            return string.IsNullOrEmpty(btn.lookText) ? btn.GetType().Name : btn.lookText;
        }

        private void Remember(string text)
        {
            _lastEvent = text;
            _lastEventTick = _net.Clock.ServerTick;
        }

        public void Clear()
        {
            _cachedBoat = null;
            _buttons = Array.Empty<GoPointerButton>();
            _index.Clear();
            _hostPointer = null;
            _replaying = false;
            _lastEvent = "—";
            _lastEventTick = 0L;
        }
    }

    /// <summary>
    /// Harmony postfixes that turn every local <see cref="GoPointerButton"/> press into a forwarded
    /// event. Patching is done across the base type and all overrides so subclassed handlers are
    /// caught too. We only hook the discrete single-shot handlers (Activate / AltActivate); the
    /// held/continuous paths are covered by <see cref="ControlsSync"/>.
    /// </summary>
    public static class InteractionPatches
    {
        public static void Apply(Harmony harmony)
        {
            PatchAll(harmony, "OnActivate", nameof(PostActivate));
            PatchAll(harmony, "OnAltActivate", nameof(PostAltActivate));
        }

        private static void PatchAll(Harmony harmony, string gameMethod, string postfixName)
        {
            var postfix = new HarmonyMethod(typeof(InteractionPatches).GetMethod(
                postfixName, BindingFlags.Static | BindingFlags.NonPublic));
            var prefix = new HarmonyMethod(typeof(InteractionPatches).GetMethod(
                nameof(PreBlock), BindingFlags.Static | BindingFlags.NonPublic));

            int patched = 0;
            var baseType = typeof(GoPointerButton);
            foreach (var t in baseType.Assembly.GetTypes())
            {
                if (!baseType.IsAssignableFrom(t)) continue;
                MethodInfo mi;
                try
                {
                    mi = t.GetMethod(gameMethod,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                        null, new[] { typeof(GoPointer) }, null);
                }
                catch { continue; }
                if (mi == null || mi.IsAbstract) continue;
                try
                {
                    harmony.Patch(mi, prefix: prefix, postfix: postfix);
                    patched++;
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[InteractionPatches] Не удалось пропатчить " +
                                             t.Name + "." + gameMethod + ": " + e.Message);
                }
            }
            Plugin.Logger.LogInfo("[InteractionPatches] " + gameMethod + "(GoPointer): пропатчено " + patched + " методов");
        }

        // The first GoPointer parameter is __0 (its name varies across overrides).
        private static void PostActivate(GoPointerButton __instance)
            => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.Activate);

        private static void PostAltActivate(GoPointerButton __instance)
            => InteractionSync.Instance?.NotifyLocalInteract(__instance, InteractKind.AltActivate);

        /// <summary>Block HOST-ONLY actions on the client: returning false skips the original handler.</summary>
        private static bool PreBlock(GoPointerButton __instance)
            => !InteractionSync.ShouldBlockLocally(__instance);
    }
}
