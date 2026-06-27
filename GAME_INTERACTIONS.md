# GAME_INTERACTIONS.md

Справочник по логике взаимодействий Sailwind, извлечённый из `Sailwind_Data/Managed/Assembly-CSharp.dll`.

Цель файла: держать под рукой рабочую карту игровых классов, которые отвечают за взаимодействие игрока с миром, чтобы не декомпилировать DLL при каждом шаге кооп-мода.

Источник анализа:

- `D:\SteamLibrary\steamapps\common\Sailwind\Sailwind_Data\Managed\Assembly-CSharp.dll`
- Unity runtime игры: `2019.1.10f1`
- Декомпилятор: `ilspycmd 10.1.0`
- Проверенные типы: `GoPointer*`, `GPButton*`, `RopeController*`, `Pickupable*`, `ShipItem*`, `BoatDamage*`, `PlayerEmbarkerNew`, cargo/economy/shipyard/bed classes.

Это не копия исходников. Здесь зафиксирована логика поведения, точки входа и выводы для сетевой синхронизации.

## Главная Цепочка Ввода

### `GoPointer`

`GoPointer` - центральный raycast/input компонент игрока.

Ключевые private поля:

- `heldItem: PickupableItem` - предмет в руках.
- `pointedAtButton: GoPointerButton` - кнопка/предмет под прицелом.
- `clickedButton: GoPointerButton` - текущая зажатая кнопка.
- `stickyClickedButton: GoPointerButton` - "залипшее" удержание для winch/wheel/pump при mouse mode.
- `hit: RaycastHit` - последний raycast hit.
- `movement: GoPointerMovement` - источник delta rotation / keyboard delta.

Raycast:

- выполняется в `FixedUpdate`;
- дальность около `1.8`;
- игнорируется во сне, в кровати, в cursor menu и при `BoatCamera.on`;
- `ItemSubcollider` поднимается до родительского collider;
- если найден `GoPointerButton` и он не `unclickable`, вызывается `Look(this)`.

Main button down вызывает в таком порядке:

1. если есть `stickyClickedButton` - `UnStickyClick`;
2. иначе если есть `pointedAtButton`:
   - `clickedButton = pointedAtButton`;
   - `clickedButton.Click(movement)`;
   - `OnActivate()`;
   - `OnActivate(GoPointer)`;
   - `OnActivateHit(RaycastHit)`;
   - если в руках предмет: `pointedAtButton.OnItemClick(heldItem)`;
   - иначе если кнопка является `PickupableItem`: `PickUpItem(...)`.

Main button up:

1. `clickedButton.OnUnactivate()`;
2. `clickedButton.OnUnactivate(GoPointer)`;
3. `clickedButton.Unclick()`;
4. `clickedButton = null`.

Alt button down:

- если в руках предмет: `heldItem.OnAltActivate()` и `heldItem.OnAltActivate(GoPointer)`;
- иначе по наведённой кнопке: `OnAltActivate()` и `OnAltActivate(GoPointer)`;
- особый случай cargo loading: big `ShipItem` может уйти в `CargoStorageUI.instance.currentCarrier.InsertItem(...)`.

Alt button held:

- если в руках предмет: `heldItem.OnAltHeld()` и `heldItem.OnAltHeld(GoPointer)`;
- иначе по наведённой кнопке: `OnAltHeld()` и `OnAltHeld(GoPointer)`.

Scroll:

- если есть `heldItem`, scroll либо вращает предмет при `RotateH`, либо вызывает `heldItem.OnScroll(input)`.

Вывод для коопа:

- Нельзя считать `OnActivate(GoPointer)` единственным событием. Для полной синхронизации нужны виды событий: activate, activate-hit, unactivate, alt-activate, alt-held, item-click, pickup, drop, scroll.
- Текущий `InteractionSync` покрывает только дискретные `OnActivate(GoPointer)` / `OnAltActivate(GoPointer)`. Он не покрывает `OnUnactivate`, `OnAltHeld`, `OnItemClick`, pickup/drop, scroll.
- Для host-authority клиент должен отправлять intent, а хост должен применять его на своей копии мира.

### `GoPointerButton`

Базовый класс для почти всех interactable объектов.

Ключевые поля:

- `lookText`, `description`;
- `allowPlacingItems`;
- `isLookedAt`, `isClicked`;
- `pointedAtBy`, `stickyClickedBy`;
- `unclickable`;
- outline state.

Виртуальные методы:

- `AllowOnItemClick(GoPointerButton lookedAtButton)`;
- `OnActivate()`;
- `OnActivate(GoPointer activatingPointer)`;
- `OnActivateHit(RaycastHit activatingHit)`;
- `OnAltActivate()`;
- `OnAltActivate(GoPointer activatingPointer)`;
- `OnUnactivate()`;
- `OnUnactivate(GoPointer activatingPointer)`;
- `OnAltHeld()`;
- `OnAltHeld(GoPointer activatingPointer)`;
- `OnItemClick(PickupableItem heldItem)`;
- `ExtraLateUpdate()`;
- `ExtraFixedUpdate()`.

Sticky click:

- `StickyClick(GoPointer)` сохраняет pointer, выключает управление игроком через `Refs.SetPlayerControl(false)` и скрывает crosshair.
- `UnStickyClick()` возвращает управление и crosshair.

Вывод для коопа:

- Sticky-click состояние локальное и должно быть отдельно отражено в debug/intent.
- Remote replay не должен навсегда оставлять host pointer в sticky state.

## Непрерывные Судовые Контролы

### `GPButtonRopeWinch`

Кнопка лебёдки/ворота, наследник `GoPointerButton`.

Ключевые поля:

- `rope: RopeController`;
- `rotHandle: TouchRotateHandle`;
- `gearRatio`;
- `rotationSpeed`;
- `reverseWindResistance`;
- `currentInput`;
- `deltaRotation`;
- `lastAngle`.

Activate:

- при mouse winch mode выключает mouse look;
- при non-mouse winch mode делает `StickyClick`;
- проигрывает звук.

Update при удержании:

1. читает input из sticky keyboard delta, mouse/touch delta или `rotHandle`;
2. ограничивает input в диапазоне примерно `[-50, 50]`;
3. применяет сопротивление ветра из `rope.currentResistance`;
4. не даёт тянуть за пределы `rope.currentLength` `[0, 1]`;
5. `Run` при отпускании добавляет quick release;
6. вращает визуальный handle;
7. вычисляет `deltaRotation`;
8. переводит delta в `rope.currentLength -= deltaRotation / 360 / gearRatio`;
9. ставит `rope.changed = true`.

Вывод для коопа:

- Смысловое состояние - `RopeController.currentLength`, а не rotation самого winch.
- Визуальная ручка лебёдки косметическая, но её можно пересылать как feedback.
- Host-authority: клиент отправляет request длины/дельты, хост применяет к своему `RopeController`, затем рассылает `ControlState`.

### `RopeController`

База:

- `currentLength`;
- `currentResistance`;
- `changed`;
- `UpdateSailAttachment()`;
- `Pull(float)`;
- `Loosen(float)`;
- `CanPull()`.

### `RopeControllerSailAngle`

Переводит `currentLength` в ограничения `HingeJoint` паруса.

Поведение:

- при `changed` обновляет joint limits;
- вычисляет wind resistance от `sail.appliedWindForce`;
- обновляет rope visual slack;
- учитывает `limitMax`, `limitBoth`, `linkedLengthRope`;
- повторно применяет более строгие limits в `LateUpdate`.

Вывод для коопа:

- Синхронизировать `currentLength` и иногда hinge/узлы для визуальной сходимости.
- Не пытаться напрямую "крутить парус" вместо rope length: игра сама пересчитает hinge limits.

### `RopeControllerSailReef`

Переводит `currentLength` в `sail.currentUnroll`.

Поведение:

- `reverseReefing` инвертирует смысл длины;
- при изменении длины обновляет sail unroll, cloth scale, wind cloth;
- на loading/justStarted принудительно выставляет крайние значения.

Вывод для коопа:

- Для reef достаточно длины rope, но после load/scene transitions нужна повторная отправка состояния.

### `RopeControllerAnchor`

Управляет длиной anchor joint.

Ключевые поля:

- `joint: ConfigurableJoint`;
- `maxLength`;
- `canPull`.

Поведение:

- регистрируется в `BoatMooringRopes`;
- `currentLength` переводится в `joint.linearLimit.limit`;
- `OnAnchorRelease()` ставит joint limit в максимум и выключает controller;
- `ResetAnchor()` возвращает длину в 0.

Вывод для коопа:

- Anchor (тип `Anchor : PickupableItem`): поля `set`/`body`, методы `IsSet()`/`GetRopeLength()`/
  `SetAnchor()`/`ReleaseAnchor()`/`AnchorReleaseSequence()`.
- **РЕАЛИЗОВАНО (`AnchorSync`)**: длина троса — через `ControlsSync`; позу якоря — кинематик-кукла
  (boat-local на стоянке / real-space когда отдан), зеркало `Anchor.set`.

### `GPButtonSteeringWheel`

Кнопка штурвала.

Ключевые поля:

- `attachedRudder: HingeJoint`;
- private `rudder: Rudder`;
- private `rotator`;
- `currentInput`;
- `gearRatio`;
- private `locked`.

Поведение:

- activate включает mouse/sticky steering, unlock при locked;
- alt при удержании toggles lock;
- при input меняет `currentInput`, ограничивает по `rotationAngleLimit`;
- `ApplyRudderRotation()` меняет `attachedRudder.spring.targetPosition`;
- `ApplyWheelRotationFromRudder()` ставит visual wheel angle от `rudder.currentAngle`;
- без удержания wheel следует за rudder, если не locked.

Вывод для коопа:

- Лодка использует СТАРЫЙ `Rudder` (не `RudderNew`). Авторитет руления = public `currentInput`
  (команда) → `ApplyRudderRotation()` ставит `attachedRudder.spring.targetPosition`.
- **РЕАЛИЗОВАНО (`ControlsSync`)**: клиент при удержании штурвала форвардит `currentInput`
  (`SteerRequest`); хост ставит `currentInput`+вызывает `ApplyRudderRotation` → его лодка поворачивает →
  `BoatSync` разносит курс. Угол руля у клиента приходит как поворот узла (host→client), его НЕ трогать
  локально (COMMAND→PHYSICS, `INTERACTION_RULES.md` R2/R6).

### `BilgePump`

Похожа на winch, но меняет `BoatDamage.waterLevel`.

Поведение:

- при удержании читает sticky/mouse delta;
- input ограничен `[0, 50]`;
- вращает ручку;
- если input > 0 и лодка не sunk:
  - уменьшает `damage.waterLevel`;
  - тратит `PlayerNeeds.water` и `PlayerNeeds.food`;
  - показывает minibar;
  - проигрывает звук.

Вывод для коопа:

- Это непрерывное host-authority действие, не одиночный клик.
- Нужны start/stop или input stream сообщения для помпы.
- `BoatDamage.waterLevel` должен быть состоянием хоста и рассылаться клиентам.

## Предметы И Pickup

### `PickupableItem`

Наследник `GoPointerButton`.

Поля:

- `big`;
- `holdDistance`, `holdHeight`, `furniturePlaceHeight`;
- `held: GoPointer`;
- `heldRotationOffset`;
- `colChecker`.

Методы:

- `OnPickup()`;
- `OnDrop()`;
- `OnScroll(float input)` - по умолчанию меняет `heldRotationOffset`.

### `GoPointer.PickUpItem` / `DropItem`

Pickup:

- сохраняет item в `heldItem`;
- ставит layer 2;
- `heldItem.held = this`;
- вызывает `item.OnPickup()`;
- для big item сохраняет local pos/rot относительно pointer.

Drop:

- сбрасывает layer 0;
- `heldItem.held = null`;
- очищает ссылку.

Throw/drop:

- для `ShipItem` main key up вызывает `OnDrop`, затем `DropItem`, затем может добавить force в Rigidbody.

Вывод для коопа:

- Pickup/drop нельзя корректно покрыть обычным button replay.
- Нужны item NetId, owner/holder NetId, pose stream для held/dropped item, и host validation.

### `ShipItem`

Главный класс физических предметов.

Важные поля:

- `sold`, `nailed`;
- `amount`, `health`, `value`, `mass`;
- `currentWalkCol`, `currentActualBoat`;
- `itemRigidbodyC`;
- `SaveablePrefab saveable`;
- `currentCargoCarrier`;
- `currentlyStayedEmbarkCol`;

Поведение:

- `Awake` делает collider trigger, rigidbody continuous speculative, запускает delayed load.
- delayed load создаёт отдельный `ItemRigidbody`, collision checker и LOD.
- `ExtraFixedUpdate` отслеживает вход/выход из boat embark collider и вызывает `EnterBoat` / `ExitBoat`.
- `EnterBoat`:
  - берёт `BoatEmbarkCollider.walkCollider`;
  - ставит parent предмета в actual boat;
  - вызывает `ItemRigidbody.EnterBoat`;
  - `SaveablePrefab.SetParentObject(sceneIndex лодки)`.
- `ExitBoat`:
  - parent в floating world;
  - `SaveablePrefab.SetParentObject(-1)`;
  - disconnect hangable joint если надо.
- `OnPickup` снимает wall attachment и inventory slot.
- `OnDrop`:
  - если не sold, возвращает в shop;
  - если wallAttachment и есть wall target, прикрепляет предмет.
- `OnAltActivate` для unsold item пытается продать через shopkeeper.
- `OnItemClick` разрешает placement маленьких items на `allowPlacingItems`.

Вывод для коопа:

- Host должен владеть saveable/item lifecycle.
- Клиентский pickup должен быть request: item NetId, action pickup/drop/place/throw/scroll.
- Для предметов на лодке позицию лучше слать boat-local; вне лодки - real-space.
- Нельзя отключать `ShipItem` физику без понимания `ItemRigidbody`: игра использует отдельный proxy rigidbody.

### `ShipItemCrate` / `CrateInventory`

Crate:

- `amount` - число содержимого до unseal;
- `containedPrefab`;
- `UnsealCrate()` spawn-ит contained prefab items, регистрирует их в save, вставляет в crate inventory;
- `OnAltActivate(GoPointer)`:
  - если unsold - обычная продажа;
  - если sold и amount > 0 - показывает `CrateSealUI`;
  - если amount <= 0 - открывает `CrateInventory`.

CrateInventory:

- `containedItems: List<ShipItem>`;
- `InsertItem`:
  - добавляет item в список;
  - пишет `currentCrateId`;
  - attached/disableCol/inStove;
  - уменьшает scale, ставит layer 26.
- `WithdrawItem` обратен insert.
- `LateUpdate` держит contained items в позиции crate, пока UI не открыт.

Вывод для коопа:

- Crate операции создают/прячут saveable items, это host-only до полноценного item sync.
- Для синхронизации нужен контейнерный state: crate id -> contained item ids / amount.

## Предметы — подклассы по доменам

Все `ShipItem` — saveable физические предметы (база: `sold`/`nailed`/`health`/`amount`/`value`/`mass`,
парентинг к лодке/миру через `EnterBoat`/`ExitBoat`, `OnPickup`/`OnDrop`). По умолчанию весь домен —
**ITEM** (репликация предметов, отдельный этап P3: NetId предмета + кадр позы boat-local/real + per-type
state + владелец-в-руках). Ниже — что синкать сверх базовой позы. Класс политики — по
`Sync/InteractionPolicy.cs`; правила — `INTERACTION_RULES.md` (R1–R16). Точки удержания (`OnAltHeld`)
и скролла (`OnScroll`) сейчас НЕ перехватываются (см. «Чего не хватает»).

### Готовка / печь (ITEM + общее состояние)

- `ShipItemStove` (+`ShopStove`): `currentHeat`, `slots: StoveCookTrigger[]`, `fuelTrigger`;
  `OnItemClick` кладёт еду/топливо в слот; `ExtraLateUpdate` греет. Состояние: heat, занятость слотов.
- `StoveFuel` / `ShipItemStoveFuel` / `StoveFuelTrigger`: `lit`, `inserted`, запас топлива; `Update`
  жжёт по `Sun.sun.timescale`. **`lit`/огонь — общий визуал** (видно обоим).
- `CookableFood`/`CookableFoodKettle`/`CookableFoodSoup`: `foodState`, `currentHeat`, `Cook()` →
  cooked/burnt. Состояние: степень готовности.
- `ShipItemKettle`: `currentWater`/`currentTeaAmount`/`currentTeaType` (LiquidType). `ShipItemSoup`:
  `currentWater`/`currentEnergy`/`currentSpoiled`/`currentSalted`/… (содержимое).
- `ShipItemFood`: `foodState`, `amount`; `OnAltHeld`→`EatFood` (тратит `amount`, меняет mesh).
- `ShipItemKnife`: `OnAltActivate`→`CutFood` (режет `FoodState`). `ShipItemSalt`/`ShipItemTea` — ингредиенты.

Вывод: готовка — это **state контейнеров** (вода/тепло/топливо/готовность) + held-действия (есть/резать).
Синк = ITEM-state; `lit`-огонь и дым полезно показывать обоим как общий визуал.

### Расходники / курение (ITEM, в основном личное)

- `ShipItemBottle`: `capacity`, `health`=налито, `OnAltHeld`/`Drink`, `FillBottle(liquid)`. Состояние: жидкость.
- `ShipItemPipe`: `inhaling`/`drinking`, `OnAltHeld`→курить; `ShipItemTobacco` наполняет.
- `ShipItemElixir`/`ShipItemRandomElixir`: `OnAltActivate`→выпить (эффект игроку — личное/PlayerNeeds).

Вывод: эффект — на нужды конкретного игрока (PlayerNeeds, отдельный домен); сетево важна лишь поза/наличие.

### Рыбалка (ITEM + забрасываемая снасть)

- `ShipItemFishingRod`: `activated`/`holding`/`throwing`, `useAngular/Velocity/DeltaPos` (физика заброса),
  `UpdateHook`/`UpdateBend`, `OnItemClick`. Ловит `FishingRodFish`. Состояние: заброшена/натяжение/рыба.
- `ShipItemFishingHook`/`ShipItemFishingHook1`/`FishingRodFish` — крюк/рыба.

Вывод: ITEM + состояние снасти (заброс/рыба). Заброс — held-физика, нужен held-канал (P3).

### Навиг-приборы (ITEM позиция; ЧТЕНИЕ — LOCAL)

- `ShipItemCompass` (lockX/Y/Z), `ShipItemQuadrant` (секстант, `inspecting`/`rotating`),
  `ShipItemChipLog` (лаг скорости: `thrown`/`throwing`, `OnAltActivate`/`OnAltHeld` — за борт),
  `ShipItemClock` (`lidOpen`, `OnAltActivate` открыть), `ShipItemSpyglass` (`OnAltActivate` смотреть,
  `OnScroll`=**зум LOCAL**, `UpdateCam`), `ShipItemScroll`/`MapChart`/`ChartData`/`ShipItemInkSet` (карты).

Вывод: **чтение/зум/инспекция прибора — LOCAL** (личный обзор). Синкать только позицию предмета (ITEM).
Прокладка курса на общей карте (MapChart/Chart*) — потенциально SHARED, но отдельный поздний заход.

### Инструменты (ITEM; часть меняет общее состояние)

- `ShipItemHammer`: `OnAltActivate`/`OnAltHeld` → прибивает предметы (`ShipItem.nailed`), чинит корпус
  (см. `HullDamageButton`). **Меняет общее состояние** (nailed/ремонт) → нужен host-apply.
- `ShipItemOar`: `isRowing`/`isOverWater`, `OnAltActivate`/`OnAltHeld` → **гребля двигает лодку**.
  Курс/движение — авторитет хоста (как штурвал) → грести у клиента нужно форвардить хосту.
- `ShipItemBroom`: `OnAltActivate` подмести — косметика.

Вывод: молоток (nailed/ремонт) и весло (движение) меняют АВТОРИТЕТНОЕ состояние → host-apply, как руление.

### Свет (ITEM + общий «горит/потушен»)

- `ShipItemLight : ShipItemHangable`: `on`, `usesOil`, `fuelConsumptionRate`, `SetLight(state)`,
  `OnAltActivate`→toggle, `OnItemClick`→дозаправка от `ShipItemLanternFuel` (`RequestOil`).
- `ShipItemHangable`/`ShipItemLampHook` (`occupied`): повесить лампу на крюк.
- `ShipItemTotem`/`WindTotemOrb`: `casting` (ветряной тотем).

Вывод: **`on` (горит) — общий визуал** (свет виден обоим) → синкать состояние + позицию; топливо — ITEM-state.

### Мебель / прочее

- `ShipItemBed`: `OnAltActivate` → сон → **HOST-ONLY** (двигает время, см. политику).
- `ShipItemFoldable`: сложить/разложить мебель — ITEM-state (toggle).

### Пушек в игре НЕТ

В `Assembly-CSharp` нет типов с «Cannon»/орудиями — Sailwind мирный торговый симулятор. Не искать.

## Швартовка

### `PickupableBoatMooringRope`

Это `PickupableItem`, но с отдельной логикой mooring.

Ключевые поля:

- `boatRigidbody`;
- `mooredToSpring: SpringJoint`;
- `initialPos`, `initialRot`, `initialParent`;
- `currentRopeLengthSquared`;
- `lengthAdjuster`.

Поведение:

- `AllowOnItemClick` разрешает click по `GPButtonDockMooring`.
- `OnPickup` если moored - вызывает `Unmoor`.
- `OnAltActivate(GoPointer)` если moored - даёт в руки `MooringRopeLengthAdjuster`.
- `OnDrop` если не moored - возвращает rope к `initialPos` coroutine.
- `OnTriggerEnter` если rope брошен на `GPButtonDockMooring` - вызывает `MoorTo`.
- `MoorTo(GPButtonDockMooring)`:
  - переносит rope к dock;
  - вычисляет длину;
  - настраивает spring connected body/anchor/spring/damper/maxDistance;
  - выключает collider dock;
  - parent rope к spring transform;
  - пишет `SaveableObject.extraSetting = true`.
- `Unmoor()`:
  - сбрасывает spring connected body/anchor/spring;
  - включает collider dock;
  - parent обратно;
  - `extraSetting = false`.
- `ChangeRopeLength(float)` меняет `currentRopeLengthSquared` и `spring.maxDistance`.

### `BoatMooringRopes`

Координатор швартовов лодки.

Поля:

- `anchor`;
- `mooringSet`;
- `ropes: PickupableBoatMooringRope[]`;
- `mooringFront`, `mooringBack`;
- `anchorController`.

Поведение:

- на Start может moor closest rope к стартовым mooring points;
- `AnyRopeMoored()` учитывает и anchor, и mooring ropes;
- `UnmoorAllRopes()` вызывает `Unmoor` и `ResetRopePos` у всех ropes;
- `MoorClosestRope(Transform mooring)` выбирает ближайший rope в initial pos.

### `MooringRopeLengthAdjuster`

`PickupableItem` — катушка для регулировки длины пришвартованного каната.

- `mooringRope: PickupableBoatMooringRope`, `boatAttachment`, `returnSequencePlaying`, `pickedUpFromMooring`.
- `OnScroll(input)` → `mooringRope.ChangeRopeLength(input / 3f)` — крутит колесо, меняет длину.
- `OnAltActivate` берёт катушку в руки; `PickupFromMooring` снимает её с швартова; `OnDrop` →
  `ReturnRopeSequence` возвращает на место.
- Авторитет длины — `PickupableBoatMooringRope.currentRopeLengthSquared` (+ `mooredToSpring.maxDistance=√`).

Вывод для коопа:

- Длина — STATE (синкать абсолют `currentRopeLengthSquared`, не дельту — `ChangeRopeLength` нелинеен).

Вывод для коопа (швартовка целиком):

- **РЕАЛИЗОВАНО (`MooringSync`)**: Harmony-перехват `Unmoor()`/`MoorTo(dock)`/`ChangeRopeLength(x)`,
  реле-через-хост (см. `INTERACTION_RULES.md` Часть 5). Адресация: rope index в `BoatMooringRopes.ropes`
  (карта `rope→idx`, т.к. `MoorTo` перепарентит трос под dock), dock — по real-space позиции (ближайший
  `GPButtonDockMooring`), длина — абсолют. На клиенте швартовка косметична (его лодка кинематическая),
  но отвязка ДОЛЖНА дойти до хоста, чтобы авторитетная лодка освободилась.

## Повреждения, Вода, Ремонт

### `BoatDamage`

Главное состояние повреждений лодки.

Поля:

- `waterLevel`;
- `hullDamage`;
- `oakum`;
- `waterIntakeRate`, `waterDrainRate`;
- `waterUnitsCapacity`;
- `sunk`;
- `waterIntakeChunk`;
- `sinkRotation`.

Поведение:

- `DailyDamage()` увеличивает `hullDamage` по дням и уменьшает `oakum`.
- `Impact(Collider other, float force)` добавляет damage, если:
  - не cooldown;
  - не sleeping+moored;
  - не ocean bottom;
  - velocity выше threshold;
  - не ShipItem impact.
- `UpdateWaterAndDrag()`:
  - если не sunk и время не paused, вода стекает по `waterDrainRate`;
  - если это `GameState.lastBoat`, добавляет воду от дождя и hullDamage;
  - `oakum` уменьшает intake через `GetCaulkMult`;
  - waterLevel clamp `[0, 1]`;
  - hullDamage clamp `[0, 1]`;
  - меняет boat buoyancy/drag;
  - при `waterLevel >= 1` кэширует local items, ставит `sunk`, выключает boat collider.

Вывод для коопа:

- Состояние damage/water должно быть host-authority snapshot.
- На клиенте не стоит независимо выполнять damage simulation для ведомой лодки.

### `HullDamageButton`

Интерактивная точка ремонта корпуса.

Поведение:

- `OnItemClick(PickupableItem heldItem)` принимает только `ShipItemOakum`;
- вычисляет недостающий oakum по `hullDamage * waterUnitsCapacity - oakum`;
- уменьшает `ShipItemOakum.amount`;
- увеличивает `BoatDamage.oakum`;
- возвращает `false`, то есть предмет не бросается автоматически.
- `ExtraLateUpdate` включает collider только когда игрок держит oakum и есть что чинить.

### `ShipItemOakum`

Поведение:

- `OnAltActivate` если sold и есть `GameState.currentBoat`, добавляет oakum сразу в текущую лодку;
- `UpdateLookText` показывает процент остатка.

Вывод для коопа:

- Ремонт через oakum - shared request к хосту: item id + boat damage target + amount.
- Нужно синхронизировать `oakum`, `hullDamage`, `waterLevel`, а также остаток `ShipItemOakum.amount`.

## Посадка На Лодку И Сход

### `PlayerEmbarkerNew`

Текущая важная система посадки игрока.

Поля:

- `playerObserver` - видимая/камерная transform-система;
- `playerController` - controller transform;
- `shiftingWorld`;
- private `currentBoat: EmbarkBoat`;
- private `embarked`;
- `debugOutCurrentBoat`.

Поведение:

- `LateUpdate` синхронизирует player collider либо к `currentBoat.walkCol`, либо к world.
- `ObserverTriggerEnter`:
  - при `EmbarkCol`/`EmbarkColPlayer` создаёт `EmbarkBoat(parent, walkCollider)`;
  - телепортирует collider к walk col;
  - при касании не-лодочного collider во время embarked может disembark.
- `OnTriggerEnter` если касается current boat walk col и ещё не embarked - вызывает `PlayerEmbark`.
- `PlayerEmbark`:
  - `playerController.parent = currentBoat.walkCol`;
  - `playerObserver.parent = currentBoat.worldBoat`;
  - `GameState.currentBoat = currentBoat.worldBoat`;
  - `GameState.lastBoat = currentBoat.worldBoat.parent`;
  - если boat saveable `extraSetting`, обновляет `GameState.lastOwnedBoat`;
  - `embarked = true`.
- `PlayerDisembark`:
  - оба player transforms parent к `shiftingWorld`;
  - `GameState.currentBoat = null`;
  - `embarked = false`.

Вывод для коопа:

- Для pose sync использовать `playerObserver`, не `playerController`.
- On-boat координаты должны быть boat-local относительно `debugOutCurrentBoat`.
- Embark/disembark гостя требует аккуратного обновления `ControlsSync`/`InteractionSync` индексов после смены current boat.

### `PlayerEmbarkDisembarkTrigger`

Старая/дополнительная система посадки.

Поведение:

- отслеживает `EmbarkCol`;
- raycast-ит ground on boat / land;
- parent player controller/observer между world и boat/walkCollider;
- использует static `embarked`.

Вывод для коопа:

- В текущем моде ориентироваться на `PlayerEmbarkerNew`, но знать, что в сценах могут быть старые trigger-компоненты.

### `BoatLadder`

Наследник `GoPointerButton`.

Поведение:

- collider включён только когда `!GameState.currentBoat`;
- `OnActivate()` телепортирует `PlayerController` к позиции лестницы + `Vector3.up * 1.25`.

Вывод для коопа:

- Ladder - локальное перемещение игрока, не состояние мира.
- Для guest нужно либо разрешить локально, либо отправлять embark intent, если ladder меняет current boat.

## Палубные контролы (дискретные)

Кнопки палубы — `GoPointerButton`-наследники с одиночным действием. SHARED-тогглы синкаются через
`InteractionSync` (replay обработчика на хосте по индексу кнопки); перемещения игрока — LOCAL; сон — HOST-ONLY.

- `GPButtonTrapdoor`: `open`/`inMotion`, `embarkCol: BoatEmbarkCollider`; `OnActivate()` →
  `OpenOrClose` (анимирует люк, переключает embark-collider). **SHARED toggle** (оба должны видеть
  открытый/закрытый люк) → replay через `InteractionSync`.
- `GPButtonRatlines`: `hardTeleportPos`; `OnActivateHit(hit)` телепортирует игрока вверх по вантам
  (`Refs.ovrController.transform.position = hardTeleportPos.position`). **LOCAL** — каждый лезет сам.
- `GPButtonSailPusher`, `GPButtonBoatPushCol`, `DockPushCol`: толкание паруса/отталкивание от
  причала. Толчок лодки меняет физику → авторитет хоста; для guest — request (как руление), либо
  оставить host-only до отдельной проверки.
- `GPButtonBed`: `OnActivate()` → сон. **HOST-ONLY** (двигает время, блокируется у клиента политикой).

## UI, Экономика, Cargo, Shipyard, Сон

Эти домены меняют прогресс/save/economy и должны быть host-only до отдельной сетевой модели.

### Economy

`EconomyUIButton.OnActivate()` вызывает методы `EconomyUI`:

- page left/right;
- select good;
- buy/sell good;
- set region/currency;
- close UI;
- print receipt.

Вывод:

- Покупка/продажа меняют деньги, товары, receipts, missions. Для v1 блокировать у клиента.

### Cargo

`CargoCarrier`:

- `InsertItem(ShipItem)` списывает деньги, кладёт item в carrier inventory, скрывает scale, меняет `daysInStorage`.
- `WithdrawItem(GoPointer, index)` списывает storage fee, отдаёт item в руки pointer.
- `RegisterDayPassed` увеличивает storage days.

Вывод:

- Cargo - host-only до item sync + economy sync.

### Shipyard

`ShipyardButton` и `ShipyardDocuments` управляют установкой частей, парусов, ремонтом, заказом/покупкой.

Вывод:

- Shipyard - host-only.
- Любые действия меняют boat prefab/customization/save и требуют отдельного протокола.

### Sleep

Классы: `GPButtonBed`, `GPButtonTavernSleep`, `GPButtonOnsenEntrance`, `ShipItemBed`.

Вывод:

- Сон/пропуск времени - host-only, потому что общий `EnvironmentSync` уже ведёт время от хоста.

## Сетевая Классификация

### Shared сейчас или ближайший этап

| Домен | Классы | Как синхронизировать |
|---|---|---|
| Парусные лебёдки / reef / anchor length | `GPButtonRopeWinch`, `RopeController*` | Client request -> host applies `currentLength` -> `ControlState` |
| Косметика winch handle | `GPButtonRopeWinch.transform`, `rotHandle` | Optional request field only, not authoritative state |
| Штурвал | `GPButtonSteeringWheel`, `Rudder*` | Отдельный steering request, host authority |
| Швартовы | `PickupableBoatMooringRope`, `GPButtonDockMooring`, `BoatMooringRopes` | Rope index + dock id/pos + moored flag + length |
| Помпа | `BilgePump`, `BoatDamage` | Held/input request + host water snapshot |
| Damage/water/repair | `BoatDamage`, `HullDamageButton`, `ShipItemOakum` | Host damage snapshot + repair request |
| Лестницы/embark | `PlayerEmbarkerNew`, `BoatLadder` | Mostly local player state, but current boat must be stable |
| Простые boat-local toggles | `GPButtonTrapdoor`, push/ratlines if safe | Discrete interaction replay after testing |

### Item domain - отдельный большой этап

Классы: `PickupableItem`, `ShipItem` + ~37 подклассов (см. раздел «Предметы — подклассы по доменам»),
`ItemRigidbody`, `SaveablePrefab`, `CrateInventory`, `CargoCarrier`.

Нужная модель:

- stable item NetId; prefab/save id; owner/holder NetId;
- pose frame: boat-local/world-real;
- item state: sold, nailed, amount, health, current crate/cargo/inventory slot;
- host validation для pickup/drop/place/throw/scroll.

Подсказки по доменам (что синкать сверх позы) — источник истины классификации `Sync/InteractionPolicy.cs`:

| Под-домен | Классы | Доп. к синку | Нюанс |
|---|---|---|---|
| Готовка/печь | `ShipItemStove`/`Kettle`/`Soup`/`Food`/`Knife`, `StoveFuel`, `CookableFood*` | heat/вода/топливо/готовность | `lit`/огонь — общий визуал |
| Свет | `ShipItemLight`/`Hangable`/`LampHook`/`LanternFuel` | `on` (горит) + топливо | `on` — общий визуал |
| Рыбалка | `ShipItemFishingRod`/`FishingHook*`, `FishingRodFish` | заброс/натяжение/рыба | held-канал (P3) |
| Навиг-приборы | `ShipItemCompass`/`Quadrant`/`ChipLog`/`Clock`/`Spyglass`/`Scroll`, `MapChart` | только позиция | **чтение/зум — LOCAL** |
| Инструменты (общее состояние) | `ShipItemHammer` (nailed/ремонт), `ShipItemOar` (гребля) | host-apply действия | как руление — авторитет хоста |
| Расходники | `ShipItemBottle`/`Pipe`/`Tobacco`/`Elixir*` | наличие/жидкость | эффект → PlayerNeeds (личное) |
| Мебель | `ShipItemFoldable` (toggle), `ShipItemBed` | state | `ShipItemBed` сон = HOST-ONLY |

### Host-only v1

| Домен | Классы |
|---|---|
| Экономика и торговля | `EconomyUI*`, `CurrencyExchange*`, `GPButtonBuyItem`, `TradeReceiptsUI*` |
| Cargo storage/transport | `CargoCarrier*`, `CargoStorageUI*`, `CrateInventory*` |
| Миссии | `GPButtonListedMission`, `GPButtonSetMission`, `GPButtonPortMissions`, `MissionListUI*` |
| Shipyard/покупка лодок | `Shipyard*`, `GPButtonPurchaseBoat` |
| Сон/время | `GPButtonBed`, `GPButtonTavernSleep`, `GPButtonOnsenEntrance`, `ShipItemBed` |
| Save/autosave | `SaveLoadManager`, `SaveableObject`, `GPButtonAutosaveToggle` |
| Settings/UI only | `GPButtonSettings*`, resolution/window/volume/keybind/map UI |

## Практические Patch Points

### Что уже делает мод

- Harmony patches на `GoPointerButton.OnActivate(GoPointer)` и `OnAltActivate(GoPointer)`.
- `InteractionSync` replay по индексу `GoPointerButton` на текущей лодке.
- `ControlsSync` синхронизирует `RopeController.currentLength` и moving nodes.
- `MooringSync` уже использует `PickupableBoatMooringRope.MoorTo(...)` / `Unmoor`.

### Чего не хватает для полного interaction layer

1. `OnUnactivate(GoPointer)` - нужен для помпы/удерживаемых объектов.
2. `OnAltHeld(GoPointer)` - нужен для hammer/held tools.
3. `OnItemClick(PickupableItem)` - нужен для oakum, water bottle, placing items.
4. Pickup/drop/throw messages - нужны для `PickupableItem`/`ShipItem`.
5. Scroll messages - нужны для item rotation/spyglass/tools.
6. Host-only block list должен оставаться жёстким для economy/save/shipyard/sleep.

## Быстрый Индекс Типов

| Тип | Роль |
|---|---|
| `GoPointer` | Raycast, input dispatch, held item owner |
| `GoPointerButton` | База interactable объектов |
| `GPButtonRopeWinch` | Winch input -> `RopeController.currentLength` |
| `RopeControllerSailAngle` | Rope length -> sail hinge limits |
| `RopeControllerSailReef` | Rope length -> sail unroll |
| `RopeControllerAnchor` | Rope length -> anchor joint limit |
| `GPButtonSteeringWheel` | Wheel input -> rudder spring target |
| `BilgePump` | Held input -> drain `BoatDamage.waterLevel` |
| `BoatDamage` | Hull/water/sink simulation |
| `HullDamageButton` | Oakum item click repair target |
| `ShipItemOakum` | Held oakum repair shortcut |
| `PickupableItem` | Base pickup/drop/scroll item |
| `ShipItem` | Saveable physical item with boat/world parenting |
| `ShipItemCrate` | Crate amount/unseal/open inventory |
| `CrateInventory` | Hidden contained items |
| `PickupableBoatMooringRope` | Physical mooring rope + spring joint (`MoorTo`/`Unmoor`/`ChangeRopeLength`) |
| `BoatMooringRopes` | Boat mooring coordinator (`ropes[]`) |
| `GPButtonDockMooring` | Dock mooring target with spring |
| `MooringRopeLengthAdjuster` | Scroll -> `ChangeRopeLength` (mooring rope length) |
| `Anchor` | `set`/`body`, `SetAnchor`/`ReleaseAnchor`, rope length |
| `ShipItemStove` | Cooking heat/slots/fuel |
| `ShipItemLight` | Lantern `on`/oil (общий визуал) |
| `ShipItemFishingRod` | Cast/hook/fish state |
| `ShipItemCompass`/`Quadrant`/`ChipLog`/`Spyglass` | Nav instruments (чтение/зум = LOCAL) |
| `ShipItemHammer` | Nail items / repair hull (общее состояние) |
| `ShipItemOar` | Rowing -> двигает лодку (host-apply) |
| `ShipItemBottle`/`Pipe`/`Elixir` | Consumables -> PlayerNeeds |
| `ShipItemBed` | Sleep -> HOST-ONLY |
| `GPButtonTrapdoor` | Hatch toggle (SHARED replay) |
| `GPButtonRatlines` | Climb rigging (LOCAL teleport) |
| `PlayerEmbarkerNew` | Current player boat/world parenting |
| `BoatLadder` | Local ladder teleport/embark helper |
| `CargoCarrier` | Cargo transport/storage economy |
| `EconomyUIButton` | Economy UI command buttons |
| `ShipyardButton` | Shipyard command buttons |

> Сетевые правила и паттерны — `INTERACTION_RULES.md` (R1–R16; части 2 «STATE vs COMMAND→PHYSICS» и
> 5 «реле-через-хост по перехвату методов»). Источник истины классификации SHARED/ITEM/LOCAL/HOST-ONLY —
> `Sync/InteractionPolicy.cs`.

## Обновление Справочника

Если игра обновилась и interaction logic изменилась:

```powershell
ilspycmd -l c D:\SteamLibrary\steamapps\common\Sailwind\Sailwind_Data\Managed\Assembly-CSharp.dll
ilspycmd -t GoPointer D:\SteamLibrary\steamapps\common\Sailwind\Sailwind_Data\Managed\Assembly-CSharp.dll
ilspycmd -t GoPointerButton D:\SteamLibrary\steamapps\common\Sailwind\Sailwind_Data\Managed\Assembly-CSharp.dll
ilspycmd -t GPButtonRopeWinch D:\SteamLibrary\steamapps\common\Sailwind\Sailwind_Data\Managed\Assembly-CSharp.dll
```

Для новых доменов сначала ищи типы по именам `GPButton*`, `Pickupable*`, `ShipItem*`, `RopeController*`, затем добавляй только поведенческое резюме и сетевые выводы, не вставляя полный декомпилированный код.
