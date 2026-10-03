# Аудит взаимодействий клиента — 2026-10-03

**Не всё сделано для клиента.** Общая передача позы предмета работает отдельно от его использования; часть действий и расхода ресурсов до хоста не доходит. Покупка лодки также не означает поддержку всей верфи.

Проверено рабочее дерево `main`, HEAD `648705c`, Protocol **63**, включая незакоммиченные исправления якоря и складной карты. Основание: исходники мода, `CLAUDE.md`, `СОСТОЯНИЕ.md`, `DESIGN.md`, `GAME_INTERACTIONS.md`, `INTERACTION_RULES.md` и декомпиляция установленного `Assembly-CSharp.dll`. SHA256 игровой DLL: `978A21A680F42C89EBCB3530F9A99EF074960BE6377DAF8E85893134A5E5CE23`.

Инвентаризация: **93 типа** в иерархии `GoPointerButton`, включая базовые классы; **35** из них в иерархии `ShipItem`. Дополнительно проверены действия через `GoPointer`, `MouseButtonPointer`, `PlayerMouthCol`, `CookableFood*`, `StoveFuel*`, `FoodState`, `Shopkeeper`, `IslandMarket*`, `CargoCarrier`, `CrateInventory`, `PlayerMissions`, `PortDude`, `PurchasableBoat`, `Shipyard`, `SailInstaller`/parts installer, `Cleaner`, `MapChart`, `Sleep` и сохранения. Это охват типов/путей, а не процент готовности игрового поведения.

Ниже «реализовано» означает найденный путь захвата → применения → рассылки в коде. Игровое выполнение и Unity Mono этим аудитом не подтверждены. Исправления не вносились. Последние результаты предыдущего этапа: Release 0/0, ProtocolSmoke 52 типа / 48 round-trip / 4014 truncated, RuntimeSmoke 26/26. В рамках анализа они повторно не запускались.

Решение о полном доверии действиям клиента сохраняется. Предлагаемые исправления относятся к доставке результата, идентификации объектов и порядку событий; новые проверки допустимости действий или числовых значений не нужны.

## Подтверждённые разрывы в коде

### A1 · P2 · Общие hooks использования предметов почти ничего не охватывают

`ItemPatches.PatchShipItem` ищет только объявленные в `ShipItem`/подклассах методы с аргументом `GoPointer` (`Sync/ItemPatches.cs:98`). В установленной игре таких объявленных `OnAltHeld` — **0**, `OnAltActivate` — **1**, у `ShipItemCrate`. Одновременно есть **9** объявленных `OnAltHeld()` и **19** `OnAltActivate()` без аргументов. `GoPointer` вызывает обе перегрузки, но пустой унаследованный метод с `GoPointer` объявлен в `GoPointerButton`, который этот поиск не охватывает. Generic InteractionSync исключает ITEM, поэтому запасного пути нет.

Отдельные hooks спасают еду, бутылку, весло, молоток, паклю, свет, метлу и fold/unfold. Они не спасают нож, эликсиры, солнечный/штормовой `ShipItemTotem`, суп, трубку, крышку часов и внутренние части некоторых приборов. Не следует исправлять это слепым replay всех методов на хосте: личные эффекты `PlayerNeeds`, локальный прицел, окна и камеры должны остаться у действующего игрока. Нужна передача фактического результата нужного типа.

### A2 · P2 · Нарезка и эликсиры оставляют мир хоста без результата

`ShipItemKnife.CutFood` создаёт ломтики через Instantiate/RegisterToSave и уничтожает исходную еду. Нет hook на `CutFood`, авторинга этих ломтиков или расхода исходного предмета. Клиентские новые предметы не становятся общими автоматически: `RefreshItems` рассылает lifecycle только на хосте (`Sync/ItemSync.cs:2537`).

`ShipItemElixir` и `ShipItemRandomElixir.OnAltActivate()` меняют личные needs, затем `DestroyItem()`. Нет отдельного Consume-hook, аналогичного `ShipItemFood.EatFood` (`Sync/ItemPatches.cs:27`). Хост сохраняет расходник. В случае random elixir выбор случайного эффекта также нельзя повторять независимо на хосте.

Сценарии проверки: клиент режет рыбу/еду — исходная исчезает и все ломтики видны двум наблюдателям; клиент выпивает оба вида эликсира — предмет исчезает у всех, личный эффект получает только он.

### A3 · P2 · Item-click для ингредиентов и принадлежность к печи отсутствуют

Есть специальные hooks для бутылки, крючка удочки, lamp hook, пакли и вычерпывания. Общего relay `OnItemClick` для `ShipItem` нет. `ShipItemSoup.InsertFood/FillWater`, `ShipItemKettle.InsertDrink/FillWater`, `ShipItemTea.OnItemClick`, `ShipItemSalt.SaltFood` и `ShipItemPipe.LoadTobacco` выполняются на клиенте без передачи полного результата. При добавлении еды/табака расходуемый объект уничтожается локально, но не у хоста.

`ShipItemStove.OnItemClick` связывает еду с конкретным `StoveCookTrigger`. `Drop` передаёт позу/Attached, но не саму связь `currentTrigger ↔ currentFood`; `EnterAttachedStatic` не вызывает InsertIntoCookTrigger на хосте (`Sync/ItemSync.cs:683`). Ванильный StoveCookTrigger.OnTriggerEnter может восстановить связь при физическом попадании в слот; похожий fallback есть для топлива. Однако адресованного пути результата click нет, а повтор по коллайдерам не гарантирует тот же слот/момент. Объявлять любую готовку полностью сломанной без игры нельзя.

Обратный переход также неполон: после pickup с печи `CookableFood*.Update` снимает связь только если item.held != null. У host remote-held копии held остаётся null; Pickup лишь снимает Attached и меняет HolderNetId, но не вызывает TakeOutOfCooker. Слот/готовка и LateUpdate-позиционирование могут продолжаться на хосте после того, как гость забрал еду в руку.

Даже bottle-click не закрывает все направления: при наливании из чайника в кружку hook видит лишь поля бутылки, а изменение полей чайника не передаётся назад хосту. Обратное направление «вода из бутылки в чайник/суп» попадает в `OnItemClick` целевого чайника/супа, а не в hook бутылки.

### A4 · P2 · Ремонт и заправка передают пользу, но не весь расход

`PostHullOakum` и `PostOakumAlt` отправляют прирост `BoatDamage.oakum`, но не уменьшение `ShipItemOakum.amount` (`Sync/BoatDamageSync.cs:388`, `:452`). LightPatches отправляет On/Health фонаря, но не уменьшение Health масла и не Consume свечи (`Sync/LightSync.cs:211`). То же касается расхода табака и содержимого супа.

Частые Pose и Drop проблему не исправляют: хост принимает Amount/Health только для `ItemAction.State`, для остальных использует свои значения (`Sync/ItemSync.cs:675`). Поэтому ремонт/заправка может быть видна всем, а количество ресурса у хоста остаётся прежним и возвращается клиенту по state после передачи/отпускания.

Исправление: передавать оба изменившихся объекта и lifecycle расходника, как уже сделано в bottle-click/water-bail; личные потребности не воспроизводить на хосте.

### A5 · P2 · ItemExtra не является полноценной синхронизацией готовки

`ExtraFields` содержит ключи `CookableFood*`, `StoveFuel`, `ShopStove` и поле `foodState`, но lookup выполняется по `e.Item.GetType().Name`, а reflection читает поля самого **ShipItem**, не его компонентов (`Sync/ItemSync.cs:2430`, `:2457`, `:2478`). Ключи компонентов недостижимы из списка ShipItem. `ShipItemFood.foodState` — ссылка на компонент, которая преобразуется в 0 вместо `dried/smoked/salted/spoiled`; обратная установка float в поле-компонент вызывает пойманное/молча проглоченное исключение.

Для чайника отсутствует `currentCookedTeaAmount`; для супа — `currentUncookedEnergy`, vitamins/protein; cooking heat находится на компонентах. ItemExtra идёт только host → client, поэтому изменения дополнительных полей от действий гостя не достигают хоста. Raw field write также не обновляет все материалы/описания/массу. Периодический ItemExtra не доказывает одинаковую готовность еды или консистенцию рецепта.

### A6 · P2 · Верфь клиента не синхронизирует переделку лодки

`ShipyardButton` и `ShipyardDocuments` классифицированы LOCAL (`Sync/InteractionPolicy.cs:65`). `ShipyardPatches` перехватывает только `PurchasableBoat.PurchaseBoat` (`Sync/ShipyardSync.cs:86`). Нет пути `Shipyard.ConfirmOrder` / InstallSails / ApplyCurrentOrder / CleanFully / repair result.

Клиент может оплатить заказ и локально изменить паруса/части/цвет/размер/чистоту/ремонт, а хост не меняет авторитетную лодку и save. Изменение набора rope/node может вызвать layout/count mismatch, но диагностический hash не доставляет новое устройство лодки. Покупка лодки работает отдельным путём, кастомизация — нет. Нужен самостоятельный результат заказа с адресом лодки и последующим переобнаружением её layout; UI/деньги действующего игрока остаются локальными.

### A7 · P2 · Фиксация старого штурвала не передаётся

В `GPButtonSteeringWheel` блокировка — private `locked`; AltButtonDown обрабатывается внутри ExtraLateUpdate и вызывает Lock/Unlock. ControlState передаёт WheelInputs/Length/Rotations, SteerRequest — лишь Input (`Sync/ControlsSync.cs:327`, `:347`). Нет lock hook/state. ForwardSteering работает только пока найден heldBtn; после Lock клавиатурный режим снимает StickyClick и запросы прекращаются.

Поворот клиентом реализован, фиксация/снятие фиксации — нет. Нельзя считать эту функцию общей только потому, что последний угол руля остался на хосте: поведение locked и последующее освобождение не совпадают. Проверить также доступные лодки с RopeControllerSteeringWheel отдельно — этот вывод относится к GPButtonSteeringWheel.

### A8 · P2 · Канат и катушка швартова в руках не видны как переносимые объекты

MooringSync передаёт Moor/Unmoor/Length; MooringState не несёт Holder/позицию/вращение (`Net/Messages.cs:709`). `ItemSync.NotifyPickup` принимает только ShipItem, а `PickupableBoatMooringRope` и `MooringRopeLengthAdjuster` — обычные PickupableItem. На удалённой машине Unmoor приводит канат на исходное место, а не в руку гостя.

Состояние привязки/длина реализованы, защита от старого снапшота и ожидания подтверждения есть. Они не добавляют видимость удержания. Нужен отдельный held-путь для каната и катушки, как для Anchor, с корректной real/boat системой координат.

### A9 · P2 · LightSync адресует свет нестабильным глобальным индексом

`RefreshLights` перечисляет все активные ShipItemLight, сортирует по `BoatLocator.PathOf(transform)` и заново выдаёт ushort индекс (`Sync/LightSync.cs:154`). В пакетах нет InstanceId/PrefabIndex/BoatIndex/layout. Путь меняется при перепривязке/переносе; активный набор расходится при локальном поясе, crate, загрузке/спавне. Тогда один Index у разных участников может обозначать разные фонари; изменение или снапшот применяется к чужому светильнику.

Это структурный дефект адресации; конкретное расположение, дающее перестановку индексов, требует игрового воспроизведения. ItemSync уже имеет стабильный ключ предмета — его нужно использовать для света. Одновременно нет pending/revision защиты от старого LightState поверх свежего клиентского включения.

### A10 · P2 · Старый ItemState откатывает владение и прочие скаляры после действия

`OnItemState` сразу присваивает HolderNetId/InventorySlot и применяет Health/Sold/Nailed/членство (`Sync/ItemSync.cs:704`). Общего отсева старого Tick нет; отдельная проверка защищает лишь Amount у ShipItemFoldable. NetTransform сортирует позу, но не эти метаданные.

Старый unreliable held Pose, полученный после reliable Drop, может снова записать клиенту HolderNetId=MyNetId. ApplyRemote тогда пропускает предмет, хотя GoPointer его уже не держит. Аналогично откатываются health, nails и container membership. Свободный предмет после финальной покоящейся позы больше постоянно не рассылается, поэтому восстановление нельзя гарантировать следующим tick. Нужен порядок состояния/подтверждений для полного ItemState, а не только mesh карты.

### A11 · P2 · Host-only bed обходится через неперехваченную перегрузку

`ShipItemBed` отмечен HOST-ONLY. Но блокирующий prefix ставится для OnActivate() и pointer-перегрузок; `OnAltActivate()` без аргументов не патчится (`Sync/InteractionSync.cs:773`). GoPointer вызывает no-arg первым; `ShipItemBed.OnAltActivate()` уже успевает EnterBed, прежде чем prefix на пустой pointer-перегрузке сообщает о блокировке.

Путь клиентского запроса сна отсутствует, а SleepPatches транслирует только сон хоста. Следовательно, фактическая локальная кровать не соответствует объявленной политике и может запустить клиентские sleeping/timeScale. GPButtonBed/таверна/онсэн идут через перехваченный OnActivate() и этим конкретным обходом не затронуты. Здесь нужна согласованная реализация принятой sleep policy, без пересмотра доверия запросам.

### A12 · P3 · Требование «событие только после взаимодействия/изменения» выполнено не везде

Люки имеют отдельный тихий baseline; mooring snapshots/manifest не выдаются за действие. Однако ForwardLocalRopeChanges считает любое расхождение local length с host length поводом начать local window, без связи с вводом (`Sync/ControlsSync.cs:405`). Локальная автореакция контроллера/инициализация может стать ControlRequest и Remember, даже если игрок канат не трогал. Quick-release нужно ловить по реальному вызову действия, а не угадывать его по произвольному расхождению.

Дополнительно `PostNailItem` отправляет Nail даже если ваниль отказала и флаг не изменился; `PostHammerAltActivate` отправляет его для любой pointed-at ShipItem. LightPatches отправляет LightRequest после любого OnItemClick, включая неподходящий предмет/no-op. Mooring `PostChangeLength` не смотрит результат/изменение (`Sync/MooringSync.cs:512`). Это реальные клики/вызовы, но не обязательно реальное изменение. Нужны сравнения before/after и отдельный захват намерения там, где повтор/удержание имеет смысл. Это дедупликация событий, не проверка допустимости клиента на хосте.

### A13 · P3 · PatchHealth может скрыть отсутствующий held hook

ItemPatches суммирует **25** возможных успехов, но ожидает **24** (`Sync/ItemPatches.cs:80`). При всех специальных hooks и alt=1, но held=0, получается «Items 24/24». Кроме того, единственный alt hook относится к crate и сразу исключается NotifyAltActivate. Зелёный статус не подтверждает покрытие использования предметов. Нужны отдельные ожидаемые signatures/пути и проверка, что они есть в установленной Assembly-CSharp, без тестов, просто повторяющих implementation.

## Остальные неполные области и принятые ограничения

- `ShipItemTotem.FinishCast` меняет `WeatherStorms.totemAttraction` и Health. Environment/weather идёт от хоста, пути клиентского cast нет. **WindTotemOrb — другой объект**: клиентский WindRequest реализован, визуал переносимой сферы — нет.
- Clock lid, scroll open/page, quadrant rotatingParent, chip-log bobber/rope не имеют отдельного substate. Поза корня передаётся, личное чтение и камеры работают локально. Это не полноценный общий визуал каждого инструмента. Spyglass zoom и управление личной камерой синхронизировать на экраны наблюдателей не требуется.
- MapChart/ChartData линии, стирание и пометки остаются локальными; общего протокола нет. Исправление fold/unfold карты этого не покрывает. Нужна явная продуктовая политика, если карта должна быть общим редактируемым объектом. Масштаб/камера/линейка интерфейса могут оставаться личными.
- Broom передаёт действие/позу, удалённый Cleaner повторяет чистку геометрически. Нет синхронизации authoritative текстуры CleanableObject; точное совпадение следов и late join после чистки нужно проверить отдельно.
- Сон инициируется хостом; гостю передаётся blackout/needs. Запроса гостя на общий сон нет. Запись мира остаётся у хоста, гостевой профиль/пояс — личные. Это ограничения принятой архитектуры, а не автоматически недостающие пакеты.
- Generic SHARED relay индексирует объекты только внутри принадлежащего флоту корпуса. Люки вне флота остаются локальными; для нового shore/NPC interaction одна default-policy Shared не гарантирует relay. TraderBoat/NPC presence не доказывает поддержку всех действий с ними.
- Якорь, map fold, dock push, люки, fleet/late join/reconnect остаются в игровой приёмке. AnchorSync сейчас связывает один первый RopeControllerAnchor на hull; если игровой prefab содержит несколько якорей, нужен отдельный инвентарь экземпляров. Наличие такого prefab этим аудитом не установлено.
- Сетевой аудит отправителя остаётся отдельной открытой работой: PlayerState NetId=0 до handshake; часть обработчиков (свет, миссии, wind, purchase, RodState) не проверяет authenticated sender, а общий receive dispatcher передаёт gameplay до handshake. Это разрешённая проверка личности отправителя, не gameplay validation.
- Уже существующие `ItemSync` reject non-owner и destination checks в MissionDeliver стоит отдельно сопоставить с решением о доверии. В этом анализе новые проверки не вводились и политика не пересматривалась.

## Матрица игровых сценариев

| Действие клиента | Захват → хост → наблюдатели | Вывод |
|---|---|---|
| Взять/нести/вращать/положить/бросить ShipItem | Pickup/Pose/Drop → ApplyWirePose → ItemState | Реализовано; A10, специальные состояния отдельно |
| Лебёдка, reef/furl, длина якорного троса | ControlRequest → rc.currentLength → ControlState | Реализовано; A12, игровой прогон |
| Старый штурвал: поворот | SteerRequest → ApplyRudderRotation → ControlState | Реализовано; Lock/Unlock отсутствует, A7 |
| Помпа | HoldRequest + renewal/timeout → BoatDamage → DamageState | Реализовано |
| Черпать воду кружкой | delta BailWater + item State → Damage/ItemState | Реализовано, расход/содержимое связаны |
| Пакля в корпус | AddOakum → DamageState | Польза есть, Amount расходника отсутствует, A4 |
| Толкнуть лодку/парус/от причала | PushRequest с адресом boat → AddForce → boat/node states | Реализовано, последние dock changes требуют игры |
| Люк лодки | intent → host target state → deferred HatchState; HatchSnapshot для join | Реализовано, без периодических кликовых событий |
| Швартовка/снятие/длина | MooringRequest + ack/pending → MooringState | Реализовано; held визуал отсутствует, A8 |
| Якорь: перенос/отпускание/трение/трос | AnchorRequest/State + revision/ack + NetTransform | Добавлено в Protocol 63, игра ожидается |
| Свет: включить/выключить | LightRequest → SetLight → LightState | Есть, но A9 (identity/order) |
| Масло/свеча в фонарь | LightRequest лишь для цели | Расход масла/свечи неполон, A4 |
| Карта/мебель: fold/unfold | actual-form → ItemAction.State → FoldableState.Apply | Добавлено; карта рисования не покрыта |
| Прибить/снять предмет | NailItem/hammer hook → ItemAction.Nail → ItemState | Реализовано; no-op A12 |
| Грести | oar OnAltHeld hook → boat-addressed impulse → BoatState | Реализовано |
| Подметать | broom action/pose → Cleaner pulse → relay | Визуал есть; точная грязь/late join не доказаны |
| Есть обычную еду | EatFood → Consume → host DestroyItem → Despawn | Реализовано, needs остаются личными |
| Пить из бутылки/кружки | Drink hook → ItemAction.State → ItemState | Реализовано |
| Перелить бутылка ↔ бутылка | before/after обоих → State → ItemState | Реализовано; не распространяется на все другие ёмкости |
| Выпить эликсир/случайный эликсир | no-arg local → missing Consume | Отсутствует общий расход, A2 |
| Резать/солить еду | local CutFood/SaltFood → missing result | Отсутствует, A2/A3 |
| Чай/суп/вода/ингредиенты | local OnItemClick → missing result/extra request | Неполно, A3/A5 |
| Поставить еду на печь/коптильню | local trigger membership → pose/Attached only | Неполно, A3/A5 |
| Топливо для печи | local click/physics → нет адресованной связи | Требует отдельного пути и игры; физический drop может сработать |
| Трубка/табак | local LoadTobacco/smoking → missing consumption | Неполно, A3/A4 |
| Рыбалка: заброс/леска/изгиб | RodState → host relay → remote rod state | Реализовано; fish fight animation отдельно не доказана |
| Крючок/потеря/пойманная рыба | RodHook + Consume; FishCatch → author → Spawn | Реализовано |
| Crate: окно/insert/withdraw/unseal | UI local; Crate/Unseal → host membership/author → ItemState/Spawn | Реализовано |
| Cargo: load/withdraw/хранение | local wallet + Cargo → membership → ItemState | Реализовано; транспортные дни/fee требуют игры |
| Пояс: положить/вынуть | claim Consume / client author + remap/pickup | Реализовано, личный профиль |
| Купить/продать в магазине/на рынке | wallet local; author / Consume → Spawn/Despawn | Реализовано |
| Валюта/receipt/торговый UI | local; EconomyState передаёт world market state | Личный UI/кошелёк намеренно локальны |
| Миссии: принять/отменить/доставить | dedicated requests → host journal/lifecycle/reward → clients | Реализовано; dropped-goods/port/reconnect acceptance открыта |
| Купить лодку | BoatPurchase → LoadAsPurchased → invalidate BoatLocator/stream | Реализовано |
| Переделать лодку в верфи | local ConfirmOrder → missing shared order result | Отсутствует, A6 |
| WindTotemOrb | held poll → WindRequest → EnvironmentSync | Эффект реализован; held sphere visual нет |
| ShipItemTotem | local cast → missing host attraction/consume state | Отсутствует, A1 |
| Сон/автосохранение/ручной save | host-only world + local CoopProfile | Принятое ограничение; bed bypass A11 |
| Лестница/ванты/ходьба/плавание | vanilla local movement → PlayerState | Действия личные, присутствие передаётся |
| Настройки/меню/zoom/дневник | vanilla local | Намеренно локально |

## Реестр всех 93 типов

Обозначения: **К** — сетевой путь найден; **Ч** — частичный/разрыв; **Л** — личное действие/UI, не требует replay; **Х** — принятое host-only ограничение; **Б** — базовый класс. Любой К требует игровой приёмки. Для ShipItem К в колонке «перенос» не означает К для его использования. Общий риск A10 относится ко всем сетевым предметам.

| Тип | Перенос / политика | Использование и ограничение |
|---|---|---|
| Anchor | К, отдельный AnchorSync | Protocol 63, carry/drop/set/rope; игра открыта |
| BilgePump | К | HoldRequest/renewal, DamageState |
| BoatDamageWaterButton | К | BailWater + State бутылки |
| BoatLadder | Л | Перемещение через PlayerState |
| CargoCarrierButton | Л + К | Окно личное, cargo membership отдельный |
| CargoStorageUIButton | Л + К | Пагинация/режим local, Insert/Withdraw relay |
| CrateInventoryButton | Л + К | Окно local, Crate membership relay |
| CurrencyExchangeUIButton | Л | Личный кошелёк/обмен |
| CurrencySwitchButton | Л | Отображение валюты |
| DockPushCol | К | Адресованный dock PushRequest |
| EconomyUIButton | Л + К | Личные buy/sell UI, физические goods отдельно |
| GPButtonAntiAliasing | Л | Настройка |
| GPButtonAutosaveToggle | Х | Мир хоста, SavePatches/Profile |
| GPButtonBed | Х | Гость не инициирует общий сон |
| GPButtonBoatPushCol | К | Force на boat хоста |
| GPButtonBuyItem | Л + К | Покупка личная, purchased object author |
| GPButtonControlToggle | Л | Настройка ввода |
| GPButtonDayLogDay | Л | Просмотр дневника |
| GPButtonDockMooring | К | ThrowRope → MoorTo hook; визуал полёта/hold неполон |
| GPButtonExtraMenus | Л | UI |
| GPButtonInterface | Л | UI |
| GPButtonInventorySlot | Л + К | Личный пояс, claim/author transitions |
| GPButtonKeybinding | Л | Настройка |
| GPButtonLightQuality | Л | Настройка |
| GPButtonListedMission | Л + К | Просмотр local, accept/abandon dedicated |
| GPButtonLogMode | Л | Дневник |
| GPButtonMapZoom | Л | Zoom карты |
| GPButtonMissionListBack | Л | Навигация |
| GPButtonMissionListPage | Л | Навигация |
| GPButtonMissionListWorld | Л | Навигация |
| GPButtonOnsenEntrance | Х | Общий сон от хоста |
| GPButtonPortMissions | Л + К | Просмотр local, world journal отдельный |
| GPButtonPurchaseBoat | Л + К | BoatPurchase |
| GPButtonRatlines | Л | OnActivateHit двигает игрока, PlayerState |
| GPButtonResetKeybindings | Л | Настройка |
| GPButtonResolutionUI | Л | Настройка |
| GPButtonRopeWinch | К | Длина/вращение рукоятки → ControlsSync |
| GPButtonSailPusher | К | PushRequest |
| GPButtonScreenResolution | Л | Настройка |
| GPButtonSetMission | Л + К | PlayerMissions method hooks |
| GPButtonSettingsCheckbo | Л | Настройка |
| GPButtonSliderVolume | Л | OnActivateHit, настройка |
| GPButtonSteeringWheel | К / Ч | Input есть; Lock/Unlock A7 |
| GPButtonTargetFramerate | Л | Настройка |
| GPButtonTavernSleep | Х | Общий сон от хоста |
| GPButtonTrapdoor | К / Л вне флота | Intent/target/HatchSnapshot |
| GPButtonWindowMode | Л | Настройка |
| GoPointerButton | Б | Generic replay только SHARED на принадлежащей лодке |
| HullDamageButton | К / Ч | Oakum delta есть, resource Amount A4 |
| MooringRopeLengthAdjuster | Ч | Длина есть; carry/coil visual A8 |
| MouseoverTextTrigger | Л | Подсказка |
| PickupableBoatMooringRope | Ч | Moor/Unmoor есть, hold/throw pose A8 |
| PickupableItem | Б | Не всякий Pickupable входит в ItemSync |
| ShipItem | Б / К | Shared identity/pose/lifecycle; type-specific use отдельно |
| ShipItemBed | К + Х / Ч | Перенос есть; host-only bypass A11 |
| ShipItemBottle | К / Ч | Drink/bottle↔bottle есть; kettle/soup не полностью A3 |
| ShipItemBroom | К / Ч | Pulse/pose есть, authoritative dirt отсутствует |
| ShipItemChipLog | К / Ч | Корень есть; bobber/rope и cast не реплицируются |
| ShipItemClock | К / Ч | Time общий; lidOpen/animation нет |
| ShipItemCompass | К + Л | Общая поза; личная calibration/reading |
| ShipItemCrate | К | Окно local; crate/unseal/contents shared |
| ShipItemElixir | К / Ч | Личные needs да; shared Consume нет A2 |
| ShipItemFishingHook | К | Attach/Consume через rod hook |
| ShipItemFishingRod | К | Pose/RodState/Hook/FishCatch; локальный процесс лова |
| ShipItemFoldable | К + Л | Fold/unfold есть; chart editor личный, lines отсутствуют |
| ShipItemFood | К / Ч | Eat/Consume есть; salt/cook/recipe A3/A5 |
| ShipItemHammer | К | Target Nail отдельный, no-op A12 |
| ShipItemHangable | К | Disconnect/Hangable/lamp attach; общий риск A10 |
| ShipItemInkSet | К + Л / Ч | Перенос; charting local, общие lines отсутствуют |
| ShipItemKettle | К / Ч | Ingredients/water/cooking extra неполны A3/A5 |
| ShipItemKnife | К / Ч | CutFood result/lifecycle отсутствует A2 |
| ShipItemLampHook | К | Отдельный адресованный LampHook |
| ShipItemLanternFuel | К / Ч | Oil/candle расход отсутствует A4 |
| ShipItemLight | К / Ч | On/Health есть; unstable ID/order/refuel A4/A9 |
| ShipItemOakum | К / Ч | Damage effect есть; расход A4 |
| ShipItemOar | К | Отдельный boat-addressed row impulse |
| ShipItemPipe | К / Ч | Shared tobacco/consumption/smoke неполны |
| ShipItemQuadrant | К + Л / Ч | Корень/чтение; child inspection transform нет |
| ShipItemRandomElixir | К / Ч | Consume/result отсутствует A2 |
| ShipItemSalt | К / Ч | SaltFood/food component/Amount A3/A5 |
| ShipItemScroll | К + Л / Ч | Личное чтение; remote open mesh/page отсутствуют |
| ShipItemSoup | К / Ч | Recipe/water/drinking/spill A3/A5 |
| ShipItemSpyglass | К + Л | Поза корня; камера/zoom личные |
| ShipItemStove | К / Ч | Food/fuel slots/heat A3/A5 |
| ShipItemStoveFuel | К / Ч | Поза/lifecycle; clicked membership/components A3/A5 |
| ShipItemTea | К / Ч | InsertDrink/remaining Amount A3 |
| ShipItemTobacco | К / Ч | Pipe ingredient Consume отсутствует A3/A4 |
| ShipItemTotem | К / Ч | FinishCast/attraction/health отсутствуют |
| ShipyardButton | Л / Ч | Preview local; ConfirmOrder world result отсутствует A6 |
| ShipyardDocuments | Л / Ч | Enter shipyard local; весь refit не становится общим |
| StartMenuButton | Л | UI; world save отдельно перехвачен |
| TradeReceiptsUIButton | Л | Личный просмотр/печать; economy world state отдельно |
| WindTotemOrb | Ч / К эффект | WindRequest есть, sphere held visual нет |

## Порядок закрытия и приёмка

1. Закрыть lifecycle/ресурсы A2–A5: нож, эликсиры, пакля/топливо, ингредиенты и печь. Передавать реальные изменения цели и расходуемых предметов, сохраняя личные эффекты на клиенте.
2. Закрыть A10: общий порядок item metadata; затем A9: стабильные ID света и порядок его state.
3. Согласовать все signatures/блокировки A1/A11/A13, убрать ложные события A12. Не включать blindly все no-arg методы в host replay.
4. Добавить Lock/Unlock и перенос каната/катушки A7/A8.
5. Отдельно реализовать результат заказа верфи A6, после него пересобрать bound contexts/layout. Решить продуктовый scope общих chart lines и инструментальных визуалов.
6. Игровая матрица: каждый результат поочерёдно создают хост и гость; смотрят хост и второй гость; проверяют расход/target/lifecycle, передачу другому игроку, drop/throw, save/reload, late join, берег/лодку/floating origin и задержку/перестановку unreliable snapshot относительно reliable action. Отдельно минуту без ввода: нет выдуманных requests/Remember, snapshots не выдаются за действие.

Открытый вопрос отсутствующего причала у хоста сохраняется: путь диагностики/ack/retry есть, сетевого dock representation нет; игровое воспроизведение требуется. Ничего из перечисленных новых замечаний в этом аудите не исправлено.
