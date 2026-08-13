# Sailwind LAN Co-op Mod

[English](#english) | [Русский](#русский)

---

## English

### 📖 Description

Sailwind LAN Co-op is a mod that adds multiplayer functionality to the game Sailwind. It allows you to play with friends over LAN (Local Area Network) or through VPN/tunneling services.

**Current Version:** 0.1.5  
**Requirements:** BepInEx 5.x, Sailwind

### ✨ Features

- **LAN Multiplayer:** Play with friends on the same network
- **Cross-Internet Play:** Use with VPN services (Hamachi, ZeroTier, etc.)
- **Configurable Settings:** Adjust network parameters, player name, and more
- **In-game Co-op Menu:** Press F8 to host, join, disconnect, choose avatar, show diagnostics, and open debug tools
- **Default Avatar Included:** The release includes `avatar.bundle` for remote player models
- **Avatar Customization:** Replace `avatar.bundle` with your own compatible bundle if desired
- **Up to 4 Players:** Host a game with up to 4 clients simultaneously
- **Shared Sea:** Waves, weather and time of day run on the host's clock, so every player sees the same water under the same hull
- **Quiet by Default:** The mod writes nothing to the log unless you switch logging on from the menu

### 📥 Installation

#### Prerequisites
1. **Sailwind** installed via Steam
2. **BepInEx 5.x** installed for Sailwind
   - If not installed, download from: https://github.com/BepInEx/BepInEx/releases
   - Extract to your Sailwind game folder

#### Mod Installation
1. Download the latest release archive
2. Extract `SailwindCoop.dll`, `LiteNetLib.dll`, and `avatar.bundle` to: `Sailwind/BepInEx/plugins/SailwindCoop/`
3. If you use a custom avatar, replace the included `avatar.bundle` with your compatible bundle
4. Launch the game

### 🎮 How to Play

For gameplay details such as economy, missions, cargo, items, damage, mooring, anchor, sleep, and guest progress, read [MULTIPLAYER_GUIDE.md](MULTIPLAYER_GUIDE.md).

#### Hosting a Game (You will be the server)
1. Launch Sailwind
2. Load or start a save game
3. Press **F8** to open the **Sailwind Co-op** menu
4. Click **Host**
5. Share your IP address with friends (see "Finding Your IP" below)
6. Wait for friends to connect

#### Joining a Game
1. Launch Sailwind
2. **Important:** Do NOT load a save game (stay at main menu)
3. Press **F8** to open the **Sailwind Co-op** menu
4. Enter the host IP and click **Join**
   - Default IP is `127.0.0.1` (localhost)
   - The menu writes the value to `BepInEx/config/com.sailwind.coop.cfg`
5. The host's world is sent to you automatically and loaded into the co-op save slot — wait for it to finish

#### Disconnecting
- Press **F8** and click **Disconnect**

#### Overlay/Debug Info
- Press **F8** and use **Show Status** / **Hide Status**
- **Logging** switches the log file on and off without restarting the game. It is off by default; turn it on *before* reproducing a problem, otherwise the log will hold nothing useful
- **Dump water state** writes `debug/water-*.txt`. Press it on both machines at the same moment if the sea ever looks different on one of them
- The **Debug** button opens the developer panel, and only works if `EnableDebugPanel` is set in the config

#### Skin Selection
- Press **F8** and click **Avatar** to open the skin selection menu
- Skin changes are visible to other players in real-time

#### Menu Input
- While the co-op menu is open, the mouse cursor is captured by the menu and does not interact with the world.
- Closing the co-op menu closes companion panels such as Avatar and Debug, then returns cursor control to the game.

### ⚙️ Configuration

Configuration file location: `Sailwind/BepInEx/config/com.sailwind.coop.cfg`

| Setting | Default | Description |
|---------|---------|-------------|
| **Network** |
| `Port` | 7777 | UDP port for hosting (must be forwarded if playing over internet) |
| `ListenIp` | 0.0.0.0 | IP address to listen on (0.0.0.0 = all interfaces) |
| `JoinIp` | 127.0.0.1 | IP address of the host to connect to |
| `PlayerName` | Player | Your display name in-game |
| `MaxClients` | 4 | Maximum number of players (1-4) |
| `SnapshotHz` | 20 | State snapshot send rate |
| `InterpDelayMs` | 100 | Interpolation buffer delay, in ms |
| **Avatar** |
| `VerticalOffset` | -0.6 | Vertical offset for client avatar model |
| `HostVerticalOffset` | -0.6 | Vertical offset for host avatar model |
| **Save** |
| `CoopSaveSlot` | 5 | Slot the client writes the received host world into. **The local save in this slot is overwritten.** |
| `ForceHostSaveOnJoin` | true | Host makes a fresh save on join so the client gets the current world |
| `PauseHostOnJoin` | true | Host world is paused while a client loads it, so nothing drifts during the join |
| **Debug** |
| `EnableLogging` | false | Write diagnostics to `BepInEx/LogOutput.log`. Also toggleable in-game (F8 → Logging) |
| `EnableDebugPanel` | false | Developer/test panel. The **Debug** button in the menu does nothing until this is on |
| **UI** |
| `MenuKey` | F8 | Open/close the Sailwind Co-op menu |

### 🔍 Finding Your IP Address

#### For LAN Play (same network):
- **Windows:** Open Command Prompt and type `ipconfig`
- Look for "IPv4 Address" under your network adapter (usually starts with 192.168.x.x)

#### For Internet Play (with VPN):
- **Hamachi:** Use the Hamachi IP (5.x.x.x)
- **ZeroTier:** Use the ZeroTier-assigned IP
- **Other VPN:** Use the VPN-provided IP address

### 🛠️ Troubleshooting

**Issue: Friends can't connect**
- Ensure port 7777 (or your custom port) is open in your firewall
- For internet play: Set up port forwarding on your router
- Try disabling antivirus/firewall temporarily
- Make sure all players are using the same mod version

**Issue: Game crashes on startup**
- Verify BepInEx is installed correctly
- Check that `SailwindCoop.dll` is in the right folder
- Look at `BepInEx/LogOutput.log` for error details

**Issue: Avatars appear incorrectly**
- Adjust `VerticalOffset` and `HostVerticalOffset` in the config file
- Ensure `avatar.bundle` exists in `Sailwind/BepInEx/plugins/SailwindCoop/`

**Issue: Reporting a bug**
- Press F8 → **Logging** to switch logging on, reproduce the problem, then attach `BepInEx/LogOutput.log`
- Say which version both machines were running

**Issue: High latency/lag**
- Reduce `SnapshotHz` in config (lower = less network traffic)
- Increase `InterpDelayMs` for smoother interpolation
- Check your network connection quality

### 📝 Notes

- This mod is in early development (v0.1.5). Expect bugs!
- Only works with players who have the mod installed, and **every machine must run the same version** — the network protocol changes between releases, so mismatched builds refuse to connect
- The client loads the host's streamed world save into a dedicated co-op slot, while guest character progress is kept in a local co-op profile
- The host's game state is authoritative
- The default avatar bundle ships with the release and must sit next to the plugin DLL
- For best performance, play on a wired network connection

### 🤝 Contributing

Found a bug? Have a suggestion?  
Visit: https://github.com/pander33/SailwindCoop

---

## Русский

### 📖 Описание

Sailwind LAN Co-op — это мод, добавляющий мультиплеер в игру Sailwind. Позволяет играть с друзьями по локальной сети (LAN) или через VPN/туннелирование.

**Текущая версия:** 0.1.5  
**Требования:** BepInEx 5.x, Sailwind (Steam версия)

### ✨ Особенности

- **LAN мультиплеер:** Игра с друзьями в одной сети
- **Игра через интернет:** Работает с VPN сервисами (Hamachi, ZeroTier и др.)
- **Настраиваемые параметры:** Настройка сети, имени игрока и др.
- **Меню кооператива в игре:** F8 открывает меню для хоста, подключения, отключения, выбора аватара, диагностики и отладки
- **Аватар по умолчанию в комплекте:** Релиз содержит `avatar.bundle` для моделей удаленных игроков
- **Кастомизация аватаров:** При желании можно заменить `avatar.bundle` на совместимый свой bundle
- **До 4 игроков:** Хост может принять до 4 клиентов одновременно
- **Общее море:** Волны, погода и время суток идут по часам хоста — вода под лодкой одинакова у всех
- **Тишина по умолчанию:** Мод ничего не пишет в лог, пока логирование не включено из меню

### 📥 Установка

#### Необходимые условия
1. **Sailwind** установлен через Steam
2. **BepInEx 5.x** установлен для Sailwind
   - Если не установлен, скачайте: https://github.com/BepInEx/BepInEx/releases
   - Распакуйте в папку с игрой Sailwind

#### Установка мода
1. Скачайте последний архив релиза
2. Распакуйте `SailwindCoop.dll`, `LiteNetLib.dll` и `avatar.bundle` в: `Sailwind/BepInEx/plugins/SailwindCoop/`
3. Если используете свой аватар, замените комплектный `avatar.bundle` на совместимый bundle
4. Запустите игру

### 🎮 Как играть

Подробное английское описание работы экономики, миссий, карго, предметов, повреждений, швартовки, якоря, сна и прогресса гостя: [MULTIPLAYER_GUIDE.md](MULTIPLAYER_GUIDE.md).

#### Создание сервера (Вы будете хостом)
1. Запустите Sailwind
2. Загрузите или начните новую игру
3. Нажмите **F8**, чтобы открыть меню **Sailwind Co-op**
4. Нажмите **Host**
5. Сообщите друзьям свой IP адрес (см. "Как узнать свой IP" ниже)
6. Ждите подключения друзей

#### Подключение к игре
1. Запустите Sailwind
2. **Важно:** НЕ загружайте сохранение (останьтесь в главном меню)
3. Нажмите **F8**, чтобы открыть меню **Sailwind Co-op**
4. Введите IP хоста и нажмите **Join**
   - IP по умолчанию: `127.0.0.1` (локальный)
   - Меню сохраняет значение в `BepInEx/config/com.sailwind.coop.cfg`
5. Мир хоста передаётся автоматически и загружается в co-op слот сохранения — дождитесь окончания

#### Отключение
- Нажмите **F8** и кнопку **Disconnect**

#### Оверлей с информацией
- Нажмите **F8** и используйте **Show Status** / **Hide Status**
- **Logging** включает и выключает лог-файл без перезапуска игры. По умолчанию выключено; включайте *до* воспроизведения проблемы, иначе в логе не будет ничего полезного
- **Dump water state** пишет `debug/water-*.txt`. Нажмите на обеих машинах одновременно, если море где-то выглядит иначе
- Кнопка **Debug** открывает панель разработчика и работает только при включённом `EnableDebugPanel` в конфиге

#### Выбор скина
- Нажмите **F8** и кнопку **Avatar**, чтобы открыть меню выбора скина

#### Управление курсором
- Пока co-op меню открыто, курсор работает только с меню и не взаимодействует с миром.
- При закрытии co-op меню закрываются сопутствующие панели Avatar/Debug, затем управление курсором возвращается игре.

### ⚙️ Настройка

Файл конфигурации: `Sailwind/BepInEx/config/com.sailwind.coop.cfg`

| Настройка | По умолчанию | Описание |
|-----------|--------------|----------|
| **Сеть** |
| `Port` | 7777 | UDP порт для хостинга (нужно открыть для интернета) |
| `ListenIp` | 0.0.0.0 | IP адрес для прослушивания (0.0.0.0 = все интерфейсы) |
| `JoinIp` | 127.0.0.1 | IP адрес хоста для подключения |
| `PlayerName` | Player | Ваше отображаемое имя в игре |
| `MaxClients` | 4 | Максимум игроков (1-4) |
| `SnapshotHz` | 20 | Частота отправки снапшотов состояния |
| `InterpDelayMs` | 100 | Задержка буфера интерполяции, мс |
| **Аватар** |
| `VerticalOffset` | -0.6 | Вертикальное смещение модели клиента |
| `HostVerticalOffset` | -0.6 | Вертикальное смещение модели хоста |
| **Сохранения** |
| `CoopSaveSlot` | 5 | Слот, куда клиент пишет полученный мир хоста. **Локальное сохранение в этом слоте перезаписывается.** |
| `ForceHostSaveOnJoin` | true | Хост делает свежее сохранение при подключении, чтобы клиент получил актуальный мир |
| `PauseHostOnJoin` | true | Мир хоста стоит на паузе, пока клиент его грузит — иначе состояние успевает разойтись |
| **Отладка** |
| `EnableLogging` | false | Писать диагностику в `BepInEx/LogOutput.log`. Переключается и в игре (F8 → Logging) |
| `EnableDebugPanel` | false | Панель разработчика. Кнопка **Debug** в меню не работает, пока это выключено |
| **UI** |
| `MenuKey` | F8 | Открыть/закрыть меню Sailwind Co-op |

### 🔍 Как узнать свой IP адрес

#### Для игры по LAN (в одной сети):
- **Windows:** Откройте командную строку и введите `ipconfig`
- Найдите "IPv4 адрес" вашей сетевой карты (обычно начинается с 192.168.x.x)

#### Для игры через интернет (с VPN):
- **Hamachi:** Используйте IP Hamachi (5.x.x.x)
- **ZeroTier:** Используйте назначенный ZeroTier IP
- **Другой VPN:** Используйте IP, предоставленный VPN

### 🛠️ Решение проблем

**Проблема: Друзья не могут подключиться**
- Убедитесь, что порт 7777 (или ваш порт) открыт в брандмауэре
- Для интернета: настройте проброс портов на роутере
- Попробуйте временно отключить антивирус/брандмауэр
- Убедитесь, что все используют одинаковую версию мода

**Проблема: Игра вылетает при запуске**
- Проверьте, что BepInEx установлен правильно
- Проверьте, что `SailwindCoop.dll` в нужной папке
- Посмотрите `BepInEx/LogOutput.log` для деталей ошибки

**Проблема: Аватары отображаются неправильно**
- Настройте `VerticalOffset` и `HostVerticalOffset` в конфиге
- Убедитесь, что `avatar.bundle` лежит в `Sailwind/BepInEx/plugins/SailwindCoop/`

**Как сообщить о баге**
- Нажмите F8 → **Logging**, чтобы включить логирование, воспроизведите проблему и приложите `BepInEx/LogOutput.log`
- Укажите версию мода на обеих машинах

**Проблема: Высокая задержка/лаги**
- Уменьшите `SnapshotHz` в конфиге (меньше = меньше сетевого трафика)
- Увеличьте `InterpDelayMs` для более плавной интерполяции
- Проверьте качество вашего сетевого соединения

### 📝 Примечания

- Мод в ранней разработке (v0.1.5). Возможны баги!
- Работает только с игроками, у которых установлен мод, и **у всех должна быть одна и та же версия** — сетевой протокол меняется между релизами, разные сборки не соединятся
- Клиент загружает полученный от хоста сейв мира в отдельный co-op слот, а прогресс персонажа гостя хранится в локальном co-op профиле
- Состояние игры хоста является авторитетным
- Аватар по умолчанию входит в релиз и должен лежать рядом с DLL мода
- Для лучшей производительности играйте по проводному соединению

### 🤝 Участие в разработке

Нашли баг? Есть предложения?  
Посетите: https://github.com/pander33/SailwindCoop

---

### 📜 License

This project is licensed under the MIT License - see the LICENSE file for details.

Этот проект лицензирован под MIT License - подробности в файле LICENSE.
