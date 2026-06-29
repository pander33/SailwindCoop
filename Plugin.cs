using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.IO;
using UnityEngine;

namespace SailwindCoop
{
    /// <summary>
    /// BepInEx entry point. Reads config, then attaches the persistent
    /// <see cref="Runtime.CoopBehaviour"/> that owns the network loop.
    /// </summary>
    [BepInPlugin(Guid, "Sailwind LAN Co-op", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.sailwind.coop";
        public const string Version = "0.1.0";

        internal static Plugin Instance { get; private set; }
        internal new static ManualLogSource Logger { get; private set; }
        internal static CoopConfig Cfg { get; private set; }
        internal static string AvatarBundlePath => Path.Combine(Path.GetDirectoryName(Instance.Info.Location), "avatar.bundle");

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            Cfg = new CoopConfig(Config);

            Logger.LogInfo("Sailwind LAN Co-op " + Version + " загружается...");

            var go = new GameObject("SailwindCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runtime.CoopBehaviour>();

            Logger.LogInfo("Sailwind LAN Co-op готов. " +
                           "Host: " + Cfg.HostKey.Value + ", Join: " + Cfg.JoinKey.Value +
                           ", оверлей: " + Cfg.OverlayKey.Value);
        }
    }

    /// <summary>Typed wrapper over the BepInEx config file.</summary>
    public sealed class CoopConfig
    {
        public readonly ConfigEntry<string> ListenIp;
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<string> JoinIp;
        public readonly ConfigEntry<string> PlayerName;
        public readonly ConfigEntry<int> SnapshotHz;
        public readonly ConfigEntry<float> InterpDelayMs;
        public readonly ConfigEntry<float> AvatarVerticalOffset;
        public readonly ConfigEntry<float> HostAvatarVerticalOffset;

        // Server (host) tuning.
        public readonly ConfigEntry<int> MaxClients;
        public readonly ConfigEntry<int> DisconnectTimeoutMs;
        public readonly ConfigEntry<int> UpdateTimeMs;
        public readonly ConfigEntry<int> PingIntervalMs;

        // Debug.
        public readonly ConfigEntry<bool> EnableDebugPanel;

        public readonly ConfigEntry<KeyCode> HostKey;
        public readonly ConfigEntry<KeyCode> JoinKey;
        public readonly ConfigEntry<KeyCode> DisconnectKey;
        public readonly ConfigEntry<KeyCode> OverlayKey;
        public readonly ConfigEntry<KeyCode> DebugPanelKey;

        public CoopConfig(ConfigFile c)
        {
            Port = c.Bind("Network", "Port", 7777, "UDP-порт хоста.");
            ListenIp = c.Bind("Network", "ListenIp", "0.0.0.0", "IP, который слушает хост (0.0.0.0 = все интерфейсы).");
            JoinIp = c.Bind("Network", "JoinIp", "127.0.0.1", "IP хоста для подключения клиентом.");
            PlayerName = c.Bind("Network", "PlayerName", "Player", "Отображаемое имя игрока.");
            SnapshotHz = c.Bind("Network", "SnapshotHz", 20, "Частота отправки снапшотов состояния (Гц), этап 1+.");
            InterpDelayMs = c.Bind("Network", "InterpDelayMs", 100f, "Задержка буфера интерполяции (мс), этап 1+.");
            AvatarVerticalOffset = c.Bind("Avatar", "VerticalOffset", -0.65f, "Вертикальный сдвиг визуальной bundle-модели относительно сетевой позиции игрока. Отрицательное значение опускает модель.");
            HostAvatarVerticalOffset = c.Bind("Avatar", "HostVerticalOffset", -0.65f, "Вертикальный сдвиг визуальной bundle-модели хоста. Нужен отдельно, потому что root-поза хоста в Sailwind обычно выше клиентской.");

            MaxClients = c.Bind("Server", "MaxClients", 4, "Максимум одновременно подключённых клиентов к хосту (1 = только один гость). Применяется при входящем подключении.");
            DisconnectTimeoutMs = c.Bind("Server", "DisconnectTimeoutMs", 5000, "Таймаут (мс) без пакетов от пира, после которого он считается отключённым.");
            UpdateTimeMs = c.Bind("Server", "UpdateTimeMs", 15, "Интервал внутреннего обновления сетевого менеджера (мс). Меньше = чаще опрос/отправка, выше нагрузка на CPU.");
            PingIntervalMs = c.Bind("Server", "PingIntervalMs", 1000, "Интервал ping (мс) для оценки задержки и keepalive соединения.");

            EnableDebugPanel = c.Bind("Debug", "EnableDebugPanel", false, "Включить дебаг-панель тест-сценариев (золото/спавн/репутация/мир) по хоткею DebugPanel. Только для отладки — в обычной игре держать выключенной.");

            HostKey = c.Bind("Hotkeys", "Host", KeyCode.F9, "Запустить хост.");
            JoinKey = c.Bind("Hotkeys", "Join", KeyCode.F10, "Подключиться к JoinIp.");
            DisconnectKey = c.Bind("Hotkeys", "Disconnect", KeyCode.F11, "Разорвать соединение.");
            OverlayKey = c.Bind("Hotkeys", "Overlay", KeyCode.F8, "Показать/скрыть диагностический оверлей.");
            DebugPanelKey = c.Bind("Hotkeys", "DebugPanel", KeyCode.F7, "Показать/скрыть дебаг-панель тест-сценариев (золото/спавн/репутация/мир).");
        }
    }
}
