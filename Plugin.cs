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
        public const string Version = "0.2.0";

        internal static Plugin Instance { get; private set; }
        internal new static ManualLogSource Logger { get; private set; }
        internal static CoopConfig Cfg { get; private set; }
        internal static string AvatarBundlePath => Path.Combine(Path.GetDirectoryName(Instance.Info.Location), "avatar.bundle");

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            Cfg = new CoopConfig(Config);
            Avatar.AvatarCatalog.Initialize();

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

        // Client connection tuning.
        public readonly ConfigEntry<int> ConnectAttempts;
        public readonly ConfigEntry<int> ReconnectDelayMs;

        // Save sharing (host streams its world to the joining client; client overlays its profile).
        public readonly ConfigEntry<int> CoopSaveSlot;
        public readonly ConfigEntry<bool> ForceHostSaveOnJoin;
        public readonly ConfigEntry<bool> PauseHostOnJoin;

        // Debug.
        public readonly ConfigEntry<bool> EnableDebugPanel;

        public readonly ConfigEntry<KeyCode> HostKey;
        public readonly ConfigEntry<KeyCode> JoinKey;
        public readonly ConfigEntry<KeyCode> DisconnectKey;
        public readonly ConfigEntry<KeyCode> OverlayKey;
        public readonly ConfigEntry<KeyCode> DebugPanelKey;
        public readonly ConfigEntry<KeyCode> AvatarSelectKey;

        public CoopConfig(ConfigFile c)
        {
            Port = c.Bind("Network", "Port", 7777, "UDP-порт хоста.");
            ListenIp = c.Bind("Network", "ListenIp", "0.0.0.0", "IP/интерфейс, который слушает хост (0.0.0.0 = все интерфейсы). Применяется при старте хоста: конкретный адрес = принимать подключения только на нём.");
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

            ConnectAttempts = c.Bind("Client", "ConnectAttempts", 10, "Сколько раз клиент пытается достучаться до хоста перед ошибкой подключения.");
            ReconnectDelayMs = c.Bind("Client", "ReconnectDelayMs", 500, "Задержка (мс) между попытками подключения клиента к хосту.");

            CoopSaveSlot = c.Bind("Save", "CoopSaveSlot", 5, "Слот сохранения (0..5), в который клиент пишет полученный мир хоста и из которого грузится. ВНИМАНИЕ: локальный сейв в этом слоте на клиенте перезаписывается. Подключаться нужно из главного меню.");
            ForceHostSaveOnJoin = c.Bind("Save", "ForceHostSaveOnJoin", true, "При подключении клиента хост делает свежее сохранение, чтобы клиент получил актуальный мир (экономика/объекты/позиция). Выключите, если хотите отдавать последний автосейв без принудительного сохранения.");
            PauseHostOnJoin = c.Bind("Save", "PauseHostOnJoin", true, "Пока клиент грузит мир хоста, мир хоста ставится на паузу (timeScale=0, как в меню настроек) — предметы/якорь/швартовы/волны у клиента совпадут со снапшотом. Пауза снимается, когда клиент отчитается о загрузке, отключится или по таймауту 120 с.");

            EnableDebugPanel = c.Bind("Debug", "EnableDebugPanel", true, "Дебаг-панель тест-сценариев (золото/спавн/репутация/мир) доступна и открывается по хоткою DebugPanel (стартует скрытой). Значение читается в рантайме — переключать доступность можно прямо в игре через BepInEx.ConfigurationManager (F1) без рестарта.");

            HostKey = c.Bind("Hotkeys", "Host", KeyCode.F9, "Запустить хост.");
            JoinKey = c.Bind("Hotkeys", "Join", KeyCode.F10, "Подключиться к JoinIp.");
            DisconnectKey = c.Bind("Hotkeys", "Disconnect", KeyCode.F11, "Разорвать соединение.");
            OverlayKey = c.Bind("Hotkeys", "Overlay", KeyCode.F8, "Показать/скрыть диагностический оверлей.");
            DebugPanelKey = c.Bind("Hotkeys", "DebugPanel", KeyCode.F7, "Показать/скрыть дебаг-панель тест-сценариев (золото/спавн/репутация/мир).");
            AvatarSelectKey = c.Bind("Hotkeys", "AvatarSelect", KeyCode.F6, "Показать/скрыть меню выбора модели персонажа.");
        }
    }
}
