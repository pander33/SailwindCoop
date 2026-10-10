# Sailwind LAN Co-op Mod

[English](#english) | [Русский](#русский)

---

## English

### 📖 Description

Sailwind LAN Co-op is a mod that adds multiplayer functionality to the game Sailwind. It allows you to play with friends over LAN (Local Area Network) or through VPN/tunneling services.

**Current Version:** 0.5.0
**Wire Protocol:** 92. Every machine needs a build with protocol 92; 0.4.3 and earlier (protocol 89) cannot connect to it. Some new features still await end-to-end in-game validation; see the release notes.
**Requirements:** BepInEx 5.x, Sailwind 0.39

### ✨ Features

- **LAN Multiplayer:** Play with friends on the same network
- **Cross-Internet Play:** Use with VPN services (Hamachi, ZeroTier, etc.)
- **Configurable Settings:** Adjust network parameters, player name, and more
- **In-game Co-op Menu:** Press F8 to host, join, disconnect, choose avatar, show diagnostics, and open debug tools
- **Default Avatar Included:** The release includes `avatar.bundle` for remote player models
- **Avatar Customization:** Replace `avatar.bundle` with your own compatible bundle if desired
- **Up to 5 Players:** One host with up to 4 clients simultaneously
- **Shared Sea:** Waves, weather and time of day run on the host's clock, so every player sees the same water under the same hull
- **Quiet by Default:** Routine diagnostics are off; serious errors still use a limited log budget
- **Crew Status and Host Controls:** See who is loading or ready, their ping and boat; hosts can lock the session or remove a guest
- **Gesture Wheel:** Hold G to wave, point, salute or shout "Land ho!" to your crew
- **Hands on the Ship:** Other players' avatars reach for the item they carry and the winch, wheel or rope they hold

### 📥 Installation

#### Prerequisites
1. **Sailwind 0.39** installed via Steam
<!--full-->
2. The GitHub Full archive includes **BepInEx 5 for Windows x64**. No separate loader download is needed.
<!--/full-->
<!--thunderstore:
2. Install the **BepInEx 5** dependency through your Thunderstore mod manager.
-->

<!--full-->
#### Editions
The mod comes in two editions. They use the same network protocol and can play together. The Thunderstore edition is published at <https://thunderstore.io/c/sailwind/p/pander33/SailwindCoop/> and installs through a mod manager.

| | Thunderstore edition | Full edition |
|---|---|---|
| LAN / VPN play | yes | yes |
| Play over Steam without an IP address | yes | yes |
| Other mods compared when joining | yes | yes |
| Missing mods downloaded from the host | no | yes |
| Files | `SailwindCoop.dll`, `LiteNetLib.dll`, `Facepunch.Steamworks.Win64.dll`, `steam_api64.dll`, `avatar.bundle`, `sounds/` | the same |

The title of the status overlay (F8 -> Settings -> Show Status) shows which edition is running, for example `Sailwind Co-op 0.5.0 (Thunderstore)`.

<!--/full-->
#### Mod Installation
<!--full-->
1. Close the game. Download `SailwindCoop-<version>.zip` from [GitHub Releases](https://github.com/pander33/SailwindCoop/releases). Choose the ZIP asset, not **Source code**. This is the complete manual-install bundle, including BepInEx 5.4.23.5 for Windows x64.
2. In Steam, right-click **Sailwind** -> **Manage** -> **Browse local files**. This opens the folder containing `Sailwind.exe`.
3. Extract the **contents** of the ZIP directly into that folder. Merge folders and replace included files when asked. Do not extract into `BepInEx/plugins/` or an extra folder named after the ZIP.
4. Check that `winhttp.dll` and `doorstop_config.ini` are next to `Sailwind.exe`, and the mod is at `BepInEx/plugins/SailwindCoop/SailwindCoop.dll`. The archive includes the loader, mod dependencies, avatar and sounds; it does not include saves or personal configs.
5. Launch the game normally and press **F8** to open the co-op menu. To change your avatar, open **Settings** -> **Avatar**.

**Updating:** close the game and repeat the extraction into the same game folder. Your saves and existing configs are not replaced. Back up a custom `avatar.bundle` before updating, since the included default replaces it. If BepInEx is already installed and you want to keep it as it is (another version, your own `doorstop_config.ini`), do not extract the whole archive: copy only the `BepInEx/plugins/SailwindCoop` folder from it into your `BepInEx/plugins/`. Otherwise back up the loader files before replacing them.

**Thunderstore alternative:** install SailwindCoop and its dependencies through the mod manager and launch the modded profile. Do not mix that profile with this manual installation.
<!--/full-->
<!--thunderstore:
1. Install **SailwindCoop** and its dependencies through your Thunderstore mod manager.
2. Launch Sailwind from the modded profile and press **F8** to open the co-op menu.
3. For manual installation, first install BepInEx 5 for Windows x64 into the folder containing `Sailwind.exe`, then extract this package into `BepInEx/plugins/SailwindCoop/`.
4. To change your avatar, open **Settings** -> **Avatar** in the F8 menu.
-->

### 🎮 How to Play

For gameplay details such as economy, missions, cargo, items, damage, mooring, anchor, sleep, and guest progress, read [MULTIPLAYER_GUIDE.md](MULTIPLAYER_GUIDE.md).

#### Hosting a Game (You will be the server)
1. Launch Sailwind
2. Load or start a save game
3. Press **F8** to open the **Sailwind Co-op** menu
4. Click **Host a game**: the session opens at once. Use **Copy address** or **Copy Steam ID** in the session card.
5. Share your IP address with friends (see "Finding Your IP" below), or switch the menu to **Steam** before pressing **Host** so Steam friends can join without an IP address (see [MULTIPLAYER_GUIDE.md](MULTIPLAYER_GUIDE.md#playing-over-steam))
6. Wait for friends to connect

#### Joining a Game
1. Launch Sailwind
2. Stay at the main menu, or join from a loaded world: the game then saves it, returns to the title screen by itself and joins
3. Press **F8** to open the **Sailwind Co-op** menu
4. Games hosted on the same local network appear by themselves under **Games on this network**: press **Join** next to one. Otherwise enter the host address in the **Join** field (an IP address or a host name, optionally `address:port`) and click **Join** or press Enter. From a loaded world, confirm **Save and join** (**Leave and join** when the world is a host's copy); **Cancel** leaves it open. A failed save stops the transition. A world of your own kept in the co-op save slot is replaced by the host's world: the confirmation says so.
   - Default IP is `127.0.0.1` (localhost)
   - The menu writes the value to `BepInEx/config/com.sailwind.coop.cfg`
5. The host's world is sent to you automatically and loaded into the co-op save slot — wait for it to finish

#### Disconnecting
- Press **F8** and click **Disconnect**
- After a client reaches Ready, **Join again** remembers that successful host across restarts. Failed attempts do not replace it. Connection errors offer **Retry** for the attempted host and **Back**.

#### Crew And Host Controls
- The **Crew** section shows each player's loading state, ping, and current boat.
- The host can use **Lock session** / **Open session** without disconnecting existing players.
- The host can remove a guest with the two-step **Kick** / **Sure?** action.
- **Teleport to boat** puts you back on the deck if you fell overboard or the boat left without you.

#### Gestures
- Hold **G** during a session to open the gesture wheel, move the mouse to a gesture and release. Release in the middle, or press **Esc**, to cancel.
- Gestures: Wave, Land ho!, Point, Come here, Applause, Shrug, Salute, Hooray.
- "Land ho!" and "Point" aim where you are looking. "Land ho!" also shouts, and other players hear it from where you stand.
- Walking ends a gesture. You do not see your own gesture, because the game is first person.
- The first time the wheel becomes available, a short hint appears on screen. The key is also shown in the F8 menu and can be changed with `EmoteKey` in the config.

#### Overlay/Debug Info
The buttons below sit under **Settings** in the F8 menu, which is collapsed until you click it.
- Press **F8**, open **Settings** and use **Show Status** / **Hide Status**
- **Logging** switches the log file on and off without restarting the game. It is off by default; turn it on *before* reproducing a problem, otherwise the log will hold nothing useful
- **Dump water state** writes `debug/water-*.txt`. Press it on both machines at the same moment if the sea ever looks different on one of them
- **Export report** writes `debug/coop-report-*.txt` with the session state, your character's needs, the installed plugins and the last few hundred lines the mod would have logged, kept in memory even when logging was off
- The **Debug** button opens the developer panel, and only works if `EnableDebugPanel` is set in the config

#### Skin Selection
- Press **F8**, open **Settings** and click **Avatar** to open the skin selection menu
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
| `MaxClients` | 4 | Maximum number of guests (1-4), in addition to the host |
| `AnnounceOnLan` | true | The host appears in the co-op menu of players on the same local network |
| `SnapshotHz` | 20 | State snapshot send rate |
| `InterpDelayMs` | 100 | Interpolation buffer delay, in ms |
| **Save** |
| `CoopSaveSlot` | 5 | Slot the client writes the received host world into. **The local save in this slot is overwritten.** |
| `ForceHostSaveOnJoin` | true | Host makes a fresh save on join so the client gets the current world |
| `PauseHostOnJoin` | true | Host world is paused while a client loads it, so nothing drifts during the join |
| **Dice** |
| `AllowStakes` | true | Host: allow optional dice stakes with personal wallets. Existing configs retain their saved value; set `AllowStakes = true` under `[Dice]` if previously disabled |
| `CloseUp` | true | Sitting down at a dice table opens the close view of it (free cursor, mouse buttons). The key `UI.DiceViewKey` (`T`) opens and closes it by hand |
| **Debug** |
| `EnableLogging` | false | Write diagnostics to `BepInEx/LogOutput.log`. Also toggleable in-game (F8 → Logging) |
| `EnableDebugPanel` | false | Developer/test panel. The **Debug** button in the menu does nothing until this is on |
| **UI** |
| `MenuKey` | F8 | Open/close the Sailwind Co-op menu |
| `EmoteKey` | G | Hold to open the gesture wheel. Change it if G is bound to something else in the game |
| `EmoteHintShown` | false | Becomes true after the one-time gesture hint was shown. Set to false to see it again |

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
- Ensure `avatar.bundle` exists in `Sailwind/BepInEx/plugins/SailwindCoop/`

**Issue: Reporting a bug**
- Press F8 → **Logging** to switch logging on, reproduce the problem, then attach `BepInEx/LogOutput.log`
- Say which version both machines were running

**Issue: High latency/lag**
- Reduce `SnapshotHz` in config (lower = less network traffic)
- Increase `InterpDelayMs` for smoother interpolation
- Check your network connection quality

### 📝 Notes

- This mod is in early development (v0.5.0). Expect bugs!
- Only works with players who have the mod installed, and **every machine must use the same wire protocol** — builds with different protocol numbers refuse to connect. Since 0.4.3 the mod version itself may differ; a host on 0.4.2 or earlier still requires the exact same version
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

**Текущая версия:** 0.5.0
**Сетевой протокол:** 92. На всех компьютерах нужна сборка с протоколом 92; 0.4.3 и более ранние (протокол 89) к ней не подключатся. Часть новых функций ещё ожидает проверки в игре; см. описание релиза.
**Требования:** BepInEx 5.x, Sailwind 0.39 (Steam версия)

### ✨ Особенности

- **LAN мультиплеер:** Игра с друзьями в одной сети
- **Игра через интернет:** Работает с VPN сервисами (Hamachi, ZeroTier и др.)
- **Настраиваемые параметры:** Настройка сети, имени игрока и др.
- **Меню кооператива в игре:** F8 открывает меню для хоста, подключения, отключения, выбора аватара, диагностики и отладки
- **Аватар по умолчанию в комплекте:** Релиз содержит `avatar.bundle` для моделей удаленных игроков
- **Кастомизация аватаров:** При желании можно заменить `avatar.bundle` на совместимый свой bundle
- **До 5 игроков:** Один хост и до 4 клиентов одновременно
- **Общее море:** Волны, погода и время суток идут по часам хоста — вода под лодкой одинакова у всех
- **Тишина по умолчанию:** Обычная диагностика выключена; серьёзные ошибки пишутся с ограничением частоты
- **Колесо жестов:** Удерживайте G, чтобы помахать, указать, отдать честь или крикнуть команде «Land ho!»
- **Руки на снастях:** Аватары других игроков тянутся к предмету в руке и к лебёдке, штурвалу или верёвке, которую держит игрок

### 📥 Установка

#### Необходимые условия
1. **Sailwind 0.39** установлен через Steam
<!--full-->
2. Архив полной редакции с GitHub уже включает **BepInEx 5 для Windows x64**. Отдельно скачивать загрузчик не нужно.
<!--/full-->
<!--thunderstore:
2. Установите зависимость **BepInEx 5** через менеджер модов Thunderstore.
-->

<!--full-->
#### Редакции
Мод выходит в двух редакциях. Сетевой протокол у них один, играть вместе можно. Редакция Thunderstore опубликована на <https://thunderstore.io/c/sailwind/p/pander33/SailwindCoop/> и ставится через менеджер модов.

| | Редакция Thunderstore | Полная редакция |
|---|---|---|
| Игра по LAN / VPN | да | да |
| Игра через Steam без IP-адреса | да | да |
| Сверка других модов при входе | да | да |
| Скачивание недостающих модов с хоста | нет | да |
| Файлы | `SailwindCoop.dll`, `LiteNetLib.dll`, `Facepunch.Steamworks.Win64.dll`, `steam_api64.dll`, `avatar.bundle`, `sounds/` | те же |

Редакция показана в заголовке оверлея статуса (F8 -> Settings -> Show Status), например `Sailwind Co-op 0.5.0 (Thunderstore)`.

<!--/full-->
#### Установка мода
<!--full-->
1. Закройте игру. Скачайте `SailwindCoop-<version>.zip` со страницы [GitHub Releases](https://github.com/pander33/SailwindCoop/releases). Нужен ZIP из **Assets**, не **Source code**. Это полный комплект для ручной установки, включая BepInEx 5.4.23.5 для Windows x64.
2. В Steam нажмите правой кнопкой на **Sailwind** -> **Управление** -> **Просмотреть локальные файлы**. Откроется папка с `Sailwind.exe`.
3. Распакуйте **содержимое** ZIP прямо в эту папку. При запросе объедините папки и замените включённые файлы. Не распаковывайте в `BepInEx/plugins/` или дополнительную папку с названием архива.
4. Проверьте: `winhttp.dll` и `doorstop_config.ini` лежат рядом с `Sailwind.exe`, а мод находится по пути `BepInEx/plugins/SailwindCoop/SailwindCoop.dll`. В архиве есть загрузчик, зависимости мода, аватар и звуки; сохранений и личных конфигов нет.
5. Запустите игру обычным способом и нажмите **F8**, чтобы открыть меню кооператива. Для смены аватара откройте **Settings** -> **Avatar**.

**Обновление:** закройте игру и повторите распаковку в ту же папку. Сохранения и существующие конфиги не заменяются. Если используете свой `avatar.bundle`, сделайте его копию: обновление заменит его стандартным. Если BepInEx уже установлен и вы хотите оставить его как есть (другая версия, свой `doorstop_config.ini`), не распаковывайте архив целиком: скопируйте из него только папку `BepInEx/plugins/SailwindCoop` в свою `BepInEx/plugins/`. Иначе сделайте резервную копию файлов загрузчика перед заменой.

**Альтернатива Thunderstore:** установите SailwindCoop и зависимости через менеджер модов и запускайте модифицированный профиль. Не смешивайте этот профиль с ручной установкой.
<!--/full-->
<!--thunderstore:
1. Установите **SailwindCoop** и зависимости через менеджер модов Thunderstore.
2. Запустите Sailwind из модифицированного профиля и нажмите **F8**, чтобы открыть меню кооператива.
3. Для ручной установки сначала установите BepInEx 5 для Windows x64 в папку с `Sailwind.exe`, затем распакуйте этот пакет в `BepInEx/plugins/SailwindCoop/`.
4. Для смены аватара откройте **Settings** -> **Avatar** в меню F8.
-->

### 🎮 Как играть

Подробное английское описание работы экономики, миссий, карго, предметов, повреждений, швартовки, якоря, сна и прогресса гостя: [MULTIPLAYER_GUIDE.md](MULTIPLAYER_GUIDE.md).

#### Создание сервера (Вы будете хостом)
1. Запустите Sailwind
2. Загрузите или начните новую игру
3. Нажмите **F8**, чтобы открыть меню **Sailwind Co-op**
4. Нажмите **Host a game** — сессия откроется сразу. В карточке сессии доступны **Copy address** или **Copy Steam ID**.
5. Сообщите друзьям свой IP адрес (см. "Как узнать свой IP" ниже)
6. Ждите подключения друзей

#### Подключение к игре
1. Запустите Sailwind
2. Останьтесь в главном меню или подключайтесь из загруженного мира: игра сама сохранит его, вернётся на титульный экран и подключится
3. Нажмите **F8**, чтобы открыть меню **Sailwind Co-op**
4. Игры в той же локальной сети появляются сами в списке **Games on this network**: нажмите **Join** рядом с нужной. Иначе введите адрес хоста в поле **Join** (IP-адрес или имя, можно `адрес:порт`) и нажмите **Join** или Enter. Из загруженного мира подтвердите **Save and join** (**Leave and join**, если это копия мира хоста); **Cancel** оставляет мир открытым. Ошибка сохранения останавливает переход. Собственный мир в слоте кооператива будет заменён миром хоста — окно подтверждения об этом предупреждает. **Join again** запоминает только готовое успешное подключение и работает после перезапуска.
   - IP по умолчанию: `127.0.0.1` (локальный)
   - Меню сохраняет значение в `BepInEx/config/com.sailwind.coop.cfg`
5. Мир хоста передаётся автоматически и загружается в co-op слот сохранения — дождитесь окончания

#### Отключение
- Нажмите **F8** и кнопку **Disconnect**

#### Жесты
- Во время сессии удерживайте **G**: откроется колесо жестов. Наведите мышь на жест и отпустите клавишу. Чтобы отменить, отпустите её в центре или нажмите **Esc**.
- Жесты: Wave, Land ho!, Point, Come here, Applause, Shrug, Salute, Hooray.
- «Land ho!» и «Point» направлены туда, куда вы смотрите. «Land ho!» ещё и звучит: другие игроки слышат выкрик с вашего места.
- Шаг прерывает жест. Свой жест вы не видите, потому что игра от первого лица.
- Когда колесо впервые становится доступным, на экране появляется короткая подсказка. Клавиша также указана в меню F8 и меняется настройкой `EmoteKey`.

#### Оверлей с информацией
Кнопки ниже находятся в разделе **Settings** меню F8; он свёрнут, пока по нему не щёлкнуть.
- Нажмите **F8**, откройте **Settings** и используйте **Show Status** / **Hide Status**
- **Logging** включает и выключает лог-файл без перезапуска игры. По умолчанию выключено; включайте *до* воспроизведения проблемы, иначе в логе не будет ничего полезного
- **Dump water state** пишет `debug/water-*.txt`. Нажмите на обеих машинах одновременно, если море где-то выглядит иначе
- Кнопка **Debug** открывает панель разработчика и работает только при включённом `EnableDebugPanel` в конфиге

#### Выбор скина
- Нажмите **F8**, откройте **Settings** и нажмите **Avatar**, чтобы открыть меню выбора скина

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
| `MaxClients` | 4 | Максимум гостей (1-4), дополнительно к хосту |
| `AnnounceOnLan` | true | Хост виден в меню кооператива у игроков той же локальной сети |
| `SnapshotHz` | 20 | Частота отправки снапшотов состояния |
| `InterpDelayMs` | 100 | Задержка буфера интерполяции, мс |
| **Сохранения** |
| `CoopSaveSlot` | 5 | Слот, куда клиент пишет полученный мир хоста. **Локальное сохранение в этом слоте перезаписывается.** |
| `ForceHostSaveOnJoin` | true | Хост делает свежее сохранение при подключении, чтобы клиент получил актуальный мир |
| `PauseHostOnJoin` | true | Мир хоста стоит на паузе, пока клиент его грузит — иначе состояние успевает разойтись |
| **Кости** |
| `AllowStakes` | true | Хост: разрешить добровольные ставки при личных кошельках. Старый конфиг сохраняет записанное значение; если ставки были выключены, задайте `AllowStakes = true` в секции `[Dice]` |
| `CloseUp` | true | Сев за столик для костей, игрок видит его крупным планом (свободный курсор, кнопки мыши). Клавиша `UI.DiceViewKey` (`T`) открывает и закрывает крупный план вручную |
| **Отладка** |
| `EnableLogging` | false | Писать диагностику в `BepInEx/LogOutput.log`. Переключается и в игре (F8 → Logging) |
| `EnableDebugPanel` | false | Панель разработчика. Кнопка **Debug** в меню не работает, пока это выключено |
| **UI** |
| `MenuKey` | F8 | Открыть/закрыть меню Sailwind Co-op |
| `EmoteKey` | G | Удерживать, чтобы открыть колесо жестов. Смените, если G занята в игре |
| `EmoteHintShown` | false | Становится true после разовой подсказки о жестах. Верните false, чтобы увидеть её снова |

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
- Убедитесь, что у всех один и тот же номер протокола (Wire Protocol). С 0.4.3 сама версия мода может отличаться; хост на 0.4.2 и старше требует точно такую же версию

**Проблема: Игра вылетает при запуске**
- Проверьте, что BepInEx установлен правильно
- Проверьте, что `SailwindCoop.dll` в нужной папке
- Посмотрите `BepInEx/LogOutput.log` для деталей ошибки

**Проблема: Аватары отображаются неправильно**
- Убедитесь, что `avatar.bundle` лежит в `Sailwind/BepInEx/plugins/SailwindCoop/`

**Как сообщить о баге**
- Нажмите F8 → **Logging**, чтобы включить логирование, воспроизведите проблему и приложите `BepInEx/LogOutput.log`
- Укажите версию мода на обеих машинах

**Проблема: Высокая задержка/лаги**
- Уменьшите `SnapshotHz` в конфиге (меньше = меньше сетевого трафика)
- Увеличьте `InterpDelayMs` для более плавной интерполяции
- Проверьте качество вашего сетевого соединения

### 📝 Примечания

- Мод в ранней разработке (v0.5.0). Возможны баги!
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

The "Land ho!" shout (`sounds/landho.wav`) is the sound "LandHo" by SilverDubloons from
freesound.org, released under Creative Commons 0.

Этот проект лицензирован под MIT License - подробности в файле LICENSE.
