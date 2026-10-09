using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Says on screen that the mod did not start. <see cref="CoopBehaviour"/> names game classes in
    /// its fields; on a game version that lacks one of them Unity cannot create the component, logs
    /// a TypeLoadException to Player.log only and goes on. BepInEx still reports the plugin as
    /// loaded, the co-op menu key does nothing and LogOutput.log is clean. This component stands in
    /// for the missing one. It must not name any game class itself.
    /// </summary>
    public sealed class StartupNotice : MonoBehaviour
    {
        private const float ShownAtStart = 30f, ShownOnKey = 12f;

        private string _text;
        private float _until;
        private GUIStyle _style;

        /// <summary>Adds the co-op component to <paramref name="go"/>; when that fails, adds the
        /// notice instead and writes the reason to the log whether logging is on or not.</summary>
        internal static bool Start(GameObject go, ManualLogSource log)
        {
            string fault = null;
            Application.LogCallback capture = (text, stack, type) =>
            {
                if (type == LogType.Exception && fault == null) fault = text;
            };

            bool started;
            Application.logMessageReceived += capture;
            try { started = AddBehaviour(go); }
            catch (Exception error)
            {
                started = false;
                if (fault == null) fault = error.GetType().Name + ": " + error.Message;
            }
            finally { Application.logMessageReceived -= capture; }

            if (started) return true;

            string text = StartupText.Describe(Application.version, fault);
            try
            {
                log.LogError(text.Replace("\n", " ") + " Reason: " + (fault ?? "the co-op component was not created"));
                go.AddComponent<StartupNotice>()._text = text;
            }
            catch (Exception error)
            {
                log.LogError("Startup notice failed: " + error);
            }

            return false;
        }

        // Kept out of Start: touching CoopBehaviour may itself throw where its fields cannot load.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AddBehaviour(GameObject go)
            => go.AddComponent<CoopBehaviour>() != null && CoopBehaviour.Instance != null;

        private void Awake() => _until = Time.unscaledTime + ShownAtStart;

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(Plugin.Cfg.MenuKey.Value)) _until = Time.unscaledTime + ShownOnKey;
            }
            catch (Exception) { }
        }

        private void OnGUI()
        {
            if (_text == null || Time.unscaledTime > _until) return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.box) { fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = true, alignment = TextAnchor.MiddleCenter };

            float width = Mathf.Min(720f, Screen.width - 40f);
            float height = _style.CalcHeight(new GUIContent(_text), width) + 16f;
            GUI.Box(new Rect((Screen.width - width) * 0.5f, 24f, width, height), _text, _style);
        }
    }
}
