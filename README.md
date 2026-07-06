# Sailwind LAN Co-op Mod

[English](#english) | [Русский](#русский)

---

## English

### 📖 Description

Sailwind LAN Co-op is a mod that adds multiplayer functionality to the game Sailwind. It allows you to play with friends over LAN (Local Area Network) or through VPN/tunneling services.

**Current Version:** 0.1.1  
**Requirements:** BepInEx 5.x, Sailwind (Steam version)

### ✨ Features

- **LAN Multiplayer:** Play with friends on the same network
- **Cross-Internet Play:** Use with VPN services (Hamachi, ZeroTier, etc.)
- **Configurable Settings:** Adjust network parameters, player name, and more
- **In-game Co-op Menu:** Press F8 to host, join, disconnect, choose avatar, show diagnostics, and open debug tools
- **Default Avatar Included:** The release includes `avatar.bundle` for remote player models
- **Avatar Customization:** Replace `avatar.bundle` with your own compatible bundle if desired
- **Up to 4 Players:** Host a game with up to 4 clients simultaneously

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
5. Once connected, the host can invite you to their game

#### Disconnecting
- Press **F8** and click **Disconnect**

#### Overlay/Debug Info
- Press **F8** and use **Show Status** / **Hide Status**
- The **Debug** button opens the test panel on the left side of the screen

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
| **Avatar** |
| `VerticalOffset` | -0.65 | Vertical offset for client avatar model |
| `HostVerticalOffset` | -0.65 | Vertical offset for host avatar model |
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

**Issue: High latency/lag**
- Reduce `SnapshotHz` in config (lower = less network traffic)
- Increase `InterpDelayMs` for smoother interpolation
- Check your network connection quality

### 📝 Notes

- This mod is in early development (v0.1.1). Expect bugs!
- Only works with players who have the mod installed
- The client loads the host's streamed world save into a dedicated co-op slot, while guest character progress is kept in a local co-op profile
- The host's game state is authoritative
- The default avatar bundle is included with release 0.1.0 and should be installed next to the plugin DLL
- For best performance, play on a wired network connection

### 🤝 Contributing

Found a bug? Have a suggestion?  
Visit: https://github.com/ruslan120p/_coop_src

---

## Русский

### 📖 Описание

Sailwind LAN Co-op — это мод, добавляющий мультиплеер в игру Sailwind. Позволяет играть с друзьями по локальной сети (LAN) или через VPN/туннелирование.

**Текущая версия:** 0.1.1  
**Требования:** BepInEx 5.x, Sailwind (Steam версия)

### ✨ Особенности

- **LAN мультиплеер:** Игра с друзьями в одной сети
- **Игра через интернет:** Работает с VPN сервисами (Hamachi, ZeroTier и др.)
- **Настраиваемые параметры:** Настройка сети, имени игрока и др.
- **Меню кооператива в игре:** F8 открывает меню для хоста, подключения, отключения, выбора аватара, диагностики и отладки
- **Аватар по умолчанию в комплекте:** Релиз содержит `avatar.bundle` для моделей удаленных игроков
- **Кастомизация аватаров:** При желании можно заменить `avatar.bundle` на совместимый свой bundle
- **До 4 игроков:** Хост может принять до 4 клиентов одновременно

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
5. После подключения хост может пригласить вас в свою игру

#### Отключение
- Нажмите **F8** и кнопку **Disconnect**

#### Оверлей с информацией
- Нажмите **F8** и используйте **Show Status** / **Hide Status**
- Кнопка **Debug** открывает тестовую панель слева

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
| **Аватар** |
| `VerticalOffset` | -0.65 | Вертикальное смещение модели клиента |
| `HostVerticalOffset` | -0.65 | Вертикальное смещение модели хоста |
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

**Проблема: Высокая задержка/лаги**
- Уменьшите `SnapshotHz` в конфиге (меньше = меньше сетевого трафика)
- Увеличьте `InterpDelayMs` для более плавной интерполяции
- Проверьте качество вашего сетевого соединения

### 📝 Примечания

- Мод в ранней разработке (v0.1.1). Возможны баги!
- Работает только с игроками, у которых установлен мод
- Клиент загружает полученный от хоста сейв мира в отдельный co-op слот, а прогресс персонажа гостя хранится в локальном co-op профиле
- Состояние игры хоста является авторитетным
- Аватар по умолчанию входит в релиз 0.1.0 и должен лежать рядом с DLL мода
- Для лучшей производительности играйте по проводному соединению

### 🤝 Участие в разработке

Нашли баг? Есть предложения?  
Посетите: https://github.com/ruslan120p/_coop_src

---

### 📜 License

This project is licensed under the MIT License - see the LICENSE file for details.

Этот проект лицензирован под MIT License - подробности в файле LICENSE.
