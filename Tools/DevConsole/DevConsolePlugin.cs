using BepInEx;
using UnityEngine;

namespace SailwindCoop.DevConsole
{
    /// <summary>
    /// Development-only: runs C# snippets inside the running game: devrun.sh compiles one and posts it to a loopback HTTP port, so a state
    /// question can be answered without a rebuild. See README.md. Never part of a public build.
    /// </summary>
    [BepInPlugin("com.sailwind.coop.devconsole", "Sailwind Co-op Dev Console", "1.0.0")]
    public sealed class DevConsolePlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            var enabled = Config.Bind("DevConsole", "Enabled", true,
                "Start the loopback HTTP endpoint that runs posted snippets in the game. Development only.");
            var port = Config.Bind("DevConsole", "Port", 7778,
                "First port to try on 127.0.0.1; the next free one is taken if it is busy (second game copy).");
            var timeout = Config.Bind("DevConsole", "TimeoutSeconds", 15,
                "How long a request waits for the game's main thread.");
            if (!enabled.Value) { Logger.LogInfo("Dev console disabled in config"); return; }

            // The BepInEx manager object can be destroyed by the game; the pump must outlive scenes.
            var go = new GameObject("SailwindCoopDevConsole");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var pump = go.AddComponent<Pump>();

            Json.Describe = Dev.Describe;
            pump.Server = new ConsoleServer(pump.Queue, m => Logger.LogWarning(m), timeout.Value * 1000);
            if (pump.Server.Start(port.Value))
                Logger.LogWarning("DEVELOPMENT TOOL: running posted code on http://127.0.0.1:" + pump.Server.Port +
                                  "/run (loopback only). Remove this plugin before sharing the game folder.");
            else
                Logger.LogError("No free port from " + port.Value + "; dev console is off");
        }

        private sealed class Pump : MonoBehaviour
        {
            public readonly MainThreadQueue Queue = new MainThreadQueue();
            public ConsoleServer Server;
            private void Update() => Queue.Pump();
            private void OnDestroy() => Server?.Dispose();
        }
    }
}
