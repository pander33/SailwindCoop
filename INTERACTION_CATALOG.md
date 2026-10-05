# Каталог входов взаимодействий — baseline T0 / текущий Protocol 75

Статус 2026-10-04: T0, основы T1–T5, T6, carry/dock T7 и T9 runtime paths реализованы в коде; игровые сценарии/Unity Mono не проверены. T8/T10–T13 и перечисленные в СОСТОЯНИЕ.md технические остатки открыты. Исходные сигнатуры T0 сохранены; кодовый статус ниже обновлён по фактическим adapters. Основание — установленная Assembly-CSharp, SHA256 `978A21A680F42C89EBCB3530F9A99EF074960BE6377DAF8E85893134A5E5CE23`. RuntimeSmoke сверяет **93 типа и 182 объявленные сигнатуры**, включая **19 no-arg OnAltActivate, 9 no-arg OnAltHeld и 1 pointer alt** у ShipItem.

## Правила чтения

- GoPointer вызывает OnActivate() → OnActivate(GoPointer) → OnActivateHit(RaycastHit) при клике; затем OnItemClick(PickupableItem), если держит предмет. Отпускание: OnUnactivate() → pointer-вариант. Alt: no-arg → pointer, затем аналогично held; scroll вызывает OnScroll(float).
- Унаследованный пустой метод GoPointerButton — запасная сигнатура, не реализация эффекта. Ниже отмечены содержательные inherited входы; остальные базовые no-op доступны по общему контракту выше. Они не делают relay готовым.
- Update/ExtraLateUpdate/ExtraFixedUpdate — драйверы состояния, не доказательство ввода. В таблице указаны собственные драйверы типа; private Update не считается наследуемым input.
- Pickup/drop идут через GoPointer.PickUpItem(PickupableItem)/DropItem(), независимо от блокировки sleep entry. Sticky/mouse release находится в GoPointer/GoPointerButton и controls.
- К — найден путь в коде, Ч — частично, Л — личный. Полные engine поля/эффекты — GAME_INTERACTIONS.md и INTERACTION_AUDIT.md. Проверка каждого общего результата: host/guest/observer, handover/drop, late join/save; конкретный сценарий задаёт соответствующий пакет T0–T13.

## Реестр всех 93 типов

| Тип / policy аудита | Собственные входы и драйверы | Содержательные inherited входы | Изменяемая область, личное/общее, relay и ограничение |
|---|---|---|---|
| Anchor / К, отдельный AnchorSync | ExtraFixedUpdate() | PickupableItem.OnPickup(); PickupableItem.OnDrop(); PickupableItem.OnScroll(float) | Protocol 63, carry/drop/set/rope; игра открыта |
| BilgePump / К | Update(); OnActivate(GoPointer); OnUnactivate(GoPointer) | Базовые no-op | HoldRequest/renewal, DamageState |
| BoatDamageWaterButton / К | ExtraLateUpdate(); OnItemClick(PickupableItem) | Базовые no-op | BailWater + State бутылки |
| BoatLadder / Л | Update(); OnActivate() | Базовые no-op | Перемещение через PlayerState |
| CargoCarrierButton / Л + К | OnActivate() | Базовые no-op | Окно личное, cargo membership отдельный |
| CargoStorageUIButton / Л + К | ExtraLateUpdate(); OnActivate(GoPointer) | Базовые no-op | Пагинация/режим local, Insert/Withdraw relay |
| CrateInventoryButton / Л + К | OnActivate(GoPointer) | Базовые no-op | Окно local, Crate membership relay |
| CurrencyExchangeUIButton / Л | OnActivate() | Базовые no-op | Личный кошелёк/обмен |
| CurrencySwitchButton / Л | Update(); OnActivate() | Базовые no-op | Отображение валюты |
| DockPushCol / К | ExtraFixedUpdate() | Базовые no-op | Адресованный dock PushRequest |
| EconomyUIButton / Л + К | OnActivate() | Базовые no-op | Личные buy/sell UI, физические goods отдельно |
| GoPointerButton / Б | OnActivate(); OnActivate(GoPointer); OnActivateHit(RaycastHit); OnAltActivate(); OnAltActivate(GoPointer); OnUnactivate(); OnUnactivate(GoPointer); OnAltHeld(); OnAltHeld(GoPointer); OnItemClick(PickupableItem); LateUpdate(); FixedUpdate(); ExtraLateUpdate(); ExtraFixedUpdate() | Базовые no-op | Generic replay только SHARED на принадлежащей лодке |
| GPButtonAntiAliasing / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonAutosaveToggle / Х | OnActivate() | Базовые no-op | Мир хоста, SavePatches/Profile |
| GPButtonBed / Х | OnActivate() | Базовые no-op | Гость не инициирует общий сон |
| GPButtonBoatPushCol / К | ExtraFixedUpdate() | Базовые no-op | Force на boat хоста |
| GPButtonBuyItem / Л + К | OnActivate(); OnItemClick(PickupableItem) | Базовые no-op | Покупка личная, purchased object author |
| GPButtonControlToggle / Л | OnActivate() | Базовые no-op | Настройка ввода |
| GPButtonDayLogDay / Л | OnActivate() | Базовые no-op | Просмотр дневника |
| GPButtonDockMooring / К | OnItemClick(PickupableItem) | Базовые no-op | T7 endpoint/adjuster carry/throw/host return; unloaded dock WaitingForDock |
| GPButtonExtraMenus / Л | OnActivate() | Базовые no-op | UI |
| GPButtonInterface / Л | OnActivate() | Базовые no-op | UI |
| GPButtonInventorySlot / Л + К | OnActivate(); OnActivate(GoPointer); OnItemClick(PickupableItem) | Базовые no-op | Личный пояс, claim/author transitions |
| GPButtonKeybinding / Л | OnActivate(); Update() | Базовые no-op | Настройка |
| GPButtonLightQuality / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonListedMission / Л + К | OnActivate() | Базовые no-op | Просмотр local, accept/abandon dedicated |
| GPButtonLogMode / Л | OnActivate() | Базовые no-op | Дневник |
| GPButtonMapZoom / Л | OnActivate() | Базовые no-op | Zoom карты |
| GPButtonMissionListBack / Л | OnActivate() | Базовые no-op | Навигация |
| GPButtonMissionListPage / Л | OnActivate() | Базовые no-op | Навигация |
| GPButtonMissionListWorld / Л | OnActivate() | Базовые no-op | Навигация |
| GPButtonOnsenEntrance / Х | OnActivate() | Базовые no-op | Общий сон от хоста |
| GPButtonPortMissions / Л + К | OnActivate() | Базовые no-op | Просмотр local, world journal отдельный |
| GPButtonPurchaseBoat / Л + К | OnActivate() | Базовые no-op | BoatPurchase |
| GPButtonRatlines / Л | OnActivateHit(RaycastHit) | Базовые no-op | OnActivateHit двигает игрока, PlayerState |
| GPButtonResetKeybindings / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonResolutionUI / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonRopeWinch / К | OnActivate(GoPointer); OnUnactivate(GoPointer); Update() | Базовые no-op | Длина/вращение рукоятки → ControlsSync |
| GPButtonSailPusher / К | ExtraFixedUpdate() | Базовые no-op | PushRequest |
| GPButtonScreenResolution / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonSetMission / Л + К | OnActivate() | Базовые no-op | PlayerMissions method hooks |
| GPButtonSettingsCheckbo / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonSliderVolume / Л | OnActivateHit(RaycastHit) | Базовые no-op | OnActivateHit, настройка |
| GPButtonSteeringWheel / К / Ч | OnActivate(GoPointer); OnUnactivate(GoPointer); ExtraLateUpdate(); ExtraFixedUpdate() | Базовые no-op | Input есть; Lock/Unlock A7 |
| GPButtonTargetFramerate / Л | OnActivate() | Базовые no-op | Настройка |
| GPButtonTavernSleep / Х | OnActivate() | Базовые no-op | Общий сон от хоста |
| GPButtonTrapdoor / К / Л вне флота | OnActivate() | Базовые no-op | Intent/target/HatchSnapshot |
| GPButtonWindowMode / Л | OnActivate() | Базовые no-op | Настройка |
| HullDamageButton / К / Ч | OnItemClick(PickupableItem); ExtraLateUpdate() | Базовые no-op | Oakum delta есть, resource Amount A4 |
| MooringRopeLengthAdjuster / Ч | OnAltActivate(GoPointer); OnPickup(); OnDrop(); Update(); OnScroll(float) | Базовые no-op | Длина есть; carry/coil visual A8 |
| MouseoverTextTrigger / Л | ExtraLateUpdate() | Базовые no-op | Подсказка |
| PickupableBoatMooringRope / Ч | Update(); ExtraLateUpdate(); OnPickup(); OnAltActivate(GoPointer); OnDrop() | PickupableItem.OnScroll(float) | Moor/Unmoor есть, hold/throw pose A8 |
| PickupableItem / Б | OnPickup(); OnDrop(); OnScroll(float) | Базовые no-op | Не всякий Pickupable входит в ItemSync |
| ShipItem / Б / К | ExtraFixedUpdate(); Update(); OnPickup(); OnDrop(); OnItemClick(PickupableItem); OnAltActivate() | PickupableItem.OnScroll(float) | Shared identity/pose/lifecycle; type-specific use отдельно |
| ShipItemBed / К + Х / Ч | OnAltActivate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Перенос есть; host-only bypass A11 |
| ShipItemBottle / К / Ч | OnItemClick(PickupableItem); OnAltActivate(); OnAltHeld(); OnDrop(); ExtraLateUpdate() | ShipItem.OnPickup(); PickupableItem.OnScroll(float) | Drink/bottle↔bottle есть; kettle/soup не полностью A3 |
| ShipItemBroom / К / Ч | OnAltActivate(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Broom pulse + DirtRequest фактических UV; full PNG DirtState/Revision/baseline/save |
| ShipItemChipLog / К / Ч | Update(); ExtraLateUpdate(); OnAltActivate(); OnAltHeld() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Instrument poses: buffered bobber/rope/thrown, reliable final; личное чтение |
| ShipItemClock / К / Ч | ExtraLateUpdate(); OnAltActivate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Typed ClockOpen + RotateLid absolute target; время общее |
| ShipItemCompass / К + Л | OnScroll(float); OnDrop(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate() | Общая поза; личная calibration/reading |
| ShipItemCrate / К | OnPickup(); OnAltActivate(GoPointer) | ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Окно local; crate/unseal/contents shared |
| ShipItemElixir / К / Ч | OnAltActivate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Личные needs да; shared Consume нет A2 |
| ShipItemFishingHook / К | — | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Attach/Consume через rod hook |
| ShipItemFishingRod / К | OnItemClick(PickupableItem); Update(); ExtraLateUpdate(); OnAltActivate(); OnAltHeld(); OnScroll(float) | ShipItem.OnPickup(); ShipItem.OnDrop() | Pose/RodState/Hook/FishCatch; локальный процесс лова |
| ShipItemFoldable / К + Л | OnItemClick(PickupableItem); OnAltActivate() | ShipItem.OnPickup(); ShipItem.OnDrop(); PickupableItem.OnScroll(float) | Fold/unfold есть; chart editor личный, lines отсутствуют |
| ShipItemFood / К / Ч | OnItemClick(PickupableItem); OnAltHeld(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Eat/Consume есть; salt/cook/recipe A3/A5 |
| ShipItemHammer / К | OnAltActivate(); OnAltHeld() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Target Nail отдельный, no-op A12 |
| ShipItemHangable / К | OnPickup() | ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Disconnect/Hangable/lamp attach; общий риск A10 |
| ShipItemInkSet / К + Л / Ч | — | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Перенос; charting local, общие lines отсутствуют |
| ShipItemKettle / К / Ч | OnItemClick(PickupableItem); OnAltActivate(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnDrop(); PickupableItem.OnScroll(float) | Ingredients/water/cooking extra неполны A3/A5 |
| ShipItemKnife / К / Ч | OnAltActivate(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | CutFood result/lifecycle отсутствует A2 |
| ShipItemLampHook / К | OnPickup(); OnItemClick(PickupableItem) | ShipItem.OnDrop(); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Отдельный адресованный LampHook |
| ShipItemLanternFuel / К / Ч | — | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Oil/candle расход отсутствует A4 |
| ShipItemLight / К / Ч | OnPickup(); OnDrop(); OnItemClick(PickupableItem); OnAltActivate(); ExtraLateUpdate() | PickupableItem.OnScroll(float) | On/Health есть; unstable ID/order/refuel A4/A9 |
| ShipItemOakum / К / Ч | OnAltActivate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Damage effect есть; расход A4 |
| ShipItemOar / К | ExtraLateUpdate(); OnAltActivate(); OnAltHeld() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Отдельный boat-addressed row impulse |
| ShipItemPipe / К / Ч | ExtraLateUpdate(); OnAltHeld(); OnItemClick(PickupableItem) | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Shared tobacco/consumption/smoke неполны |
| ShipItemQuadrant / К + Л / Ч | OnAltActivate(); OnDrop(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Typed QuadrantInspect + SmoothlyRotate target; камера/измерение личные |
| ShipItemRandomElixir / К / Ч | OnAltActivate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); PickupableItem.OnScroll(float) | Consume/result отсутствует A2 |
| ShipItemSalt / К / Ч | OnItemClick(PickupableItem) | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | SaltFood/food component/Amount A3/A5 |
| ShipItemScroll / К + Л / Ч | OnScroll(float); OnPickup(); OnDrop() | ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate() | Typed ScrollOpen/ScrollPage/mesh; GameState reading UI личный |
| ShipItemSoup / К / Ч | OnItemClick(PickupableItem); OnAltActivate(); OnAltHeld(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnDrop(); PickupableItem.OnScroll(float) | Recipe/water/drinking/spill A3/A5 |
| ShipItemSpyglass / К + Л | OnPickup(); OnDrop(); OnAltActivate(); OnScroll(float) | ShipItem.OnItemClick(PickupableItem) | Поза корня; камера/zoom личные |
| ShipItemStove / К / Ч | OnPickup(); OnItemClick(PickupableItem); ExtraLateUpdate() | ShipItem.OnDrop(); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Food/fuel slots/heat A3/A5 |
| ShipItemStoveFuel / К / Ч | — | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Поза/lifecycle; clicked membership/components A3/A5 |
| ShipItemTea / К / Ч | OnItemClick(PickupableItem) | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | InsertDrink/remaining Amount A3 |
| ShipItemTobacco / К / Ч | — | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | Pipe ingredient Consume отсутствует A3/A4 |
| ShipItemTotem / К / Ч | OnAltHeld(); ExtraLateUpdate() | ShipItem.OnPickup(); ShipItem.OnDrop(); ShipItem.OnItemClick(PickupableItem); ShipItem.OnAltActivate(); PickupableItem.OnScroll(float) | FinishCast compound weather/health result + attraction Revision; persistence проверка T12 |
| ShipyardButton / Л / Ч | ExtraLateUpdate(); OnActivate() | Базовые no-op | Preview local; ConfirmOrder world result отсутствует A6 |
| ShipyardDocuments / Л / Ч | OnActivate(); Update() | Базовые no-op | Enter shipyard local; весь refit не становится общим |
| StartMenuButton / Л | OnActivate(); ExtraLateUpdate() | Базовые no-op | UI; world save отдельно перехвачен |
| TradeReceiptsUIButton / Л | OnActivate() | Базовые no-op | Личный просмотр/печать; economy world state отдельно |
| WindTotemOrb / Ч / К эффект | OnDrop(); Update() | PickupableItem.OnPickup(); PickupableItem.OnScroll(float) | Orb carry + totem parent buffered pose/particles/audio; causal WindRequest |

## Назначение каждого item alt/held входа

| Точная сигнатура | Источник | Результат и механизм | Текущий статус |
|---|---|---|---|
| ShipItem.OnAltActivate() | Alt input | Shopkeeper.TryToSellItem; actor wallet/UI | Dedicated / Shop; не generic replay |
| ShipItemBed.OnAltActivate() | Alt input | Host-only sleep entry; T10 later | Dedicated / Interactions; не generic replay |
| ShipItemBottle.OnAltActivate() | Alt input | Shopkeeper.TryToSellItem | Dedicated / Shop; не generic replay |
| ShipItemBottle.OnAltHeld() | Active alt hold | Drink result; personal needs stay local | Dedicated / Items; не generic replay |
| ShipItemBroom.OnAltActivate() | Alt input | Broom pulse + UV/Dirt texture domain | Dedicated / Items; не generic replay |
| ShipItemChipLog.OnAltActivate() | Alt input | Buffered instrument bobber/rope/final state | Dedicated / Instrument poses; не generic replay |
| ShipItemChipLog.OnAltHeld() | Active alt hold | Buffered instrument bobber/rope/final state | Dedicated / Instrument poses; не generic replay |
| ShipItemClock.OnAltActivate() | Alt input | Absolute typed clock lid target | Dedicated / Special item visuals; не generic replay |
| ShipItemCrate.OnAltActivate(GoPointer) | Alt input | Local crate UI; membership/unseal use dedicated hooks | **Local**; не generic replay |
| ShipItemElixir.OnAltActivate() | Alt input | T4: consume after personal effect | **Pending**; не generic replay |
| ShipItemFishingRod.OnAltActivate() | Alt input | RodState + CollectFish | Dedicated / Items; не generic replay |
| ShipItemFishingRod.OnAltHeld() | Active alt hold | RodState holding/bobber/line | Dedicated / Items; не generic replay |
| ShipItemFoldable.OnAltActivate() | Alt input | Absolute FoldableState | Dedicated / Items; не generic replay |
| ShipItemFood.OnAltHeld() | Active alt hold | EatFood consume; personal needs stay local | Dedicated / Items; не generic replay |
| ShipItemHammer.OnAltActivate() | Alt input | Target NailItem result | Dedicated / Items; не generic replay |
| ShipItemHammer.OnAltHeld() | Active alt hold | Target NailItem result | Dedicated / Items; не generic replay |
| ShipItemKettle.OnAltActivate() | Alt input | Only unsold shop branch; recipe is T5 | Dedicated / Shop; не generic replay |
| ShipItemKnife.OnAltActivate() | Alt input | T4: CutFood lifecycle | **Pending**; не generic replay |
| ShipItemLight.OnAltActivate() | Alt input | Light target; stable ID remains T2/T4 | Dedicated / Lights; не generic replay |
| ShipItemOakum.OnAltActivate() | Alt input | T4: repair result lacks resource consumption | **Pending**; не generic replay |
| ShipItemOar.OnAltActivate() | Alt input | Local stroke reset; force captured by no-arg held hook | Dedicated / Items; не generic replay |
| ShipItemOar.OnAltHeld() | Active alt hold | Actual rowing impulse | Dedicated / Items; не generic replay |
| ShipItemPipe.OnAltHeld() | Active alt hold | T5: tobacco/content consumption | **Pending**; не generic replay |
| ShipItemQuadrant.OnAltActivate() | Alt input | Absolute typed quadrant target | Dedicated / Special item visuals; не generic replay |
| ShipItemRandomElixir.OnAltActivate() | Alt input | T4: consume; random personal choice stays local | **Pending**; не generic replay |
| ShipItemSoup.OnAltActivate() | Alt input | Only unsold shop branch; recipe/drink is T5 | Dedicated / Shop; не generic replay |
| ShipItemSoup.OnAltHeld() | Active alt hold | T5: soup content/consumption | **Pending**; не generic replay |
| ShipItemSpyglass.OnAltActivate() | Alt input | Observer root pose; actor camera/zoom | **Local**; не generic replay |
| ShipItemTotem.OnAltHeld() | Active alt hold | FinishCast compound shared effect/resource | Dedicated / Item results; не generic replay |

## Доменный capture вне общего input

| Домен | Capture / результат | Личное и проверка |
|---|---|---|
| Item lifecycle | PickUpItem/DropItem: holder/pose; EatFood: Consume; CollectFish: host author | PlayerNeeds автора; old-state/drop/lifecycle |
| Scalars | Bottle.OnItemClick/Drink: Amount/Health; Foldable.OnAltActivate: absolute mesh/details/collider | Питьё/камера личные; два наблюдателя |
| Target state | Hammer.NailItem/alt: target.nailed; FishingRod.DetachHook/itemclick: health/consume; LampHook.itemclick: attachment | Pointer aim не повторяется хостом; no-op/order |
| Inventory | Crate.Insert/Withdraw/Unseal; Cargo.Insert/Withdraw; InventorySlot.Insert/Withdraw | Membership/lifecycle общие, UI/кошелёк личные; handover/late join/save |
| Economy | IslandMarket.SpawnGood/Warehouse.SellGood; Shopkeeper domain hooks | Author/despawn общие, кошелёк автора; duplicates |
| Controls | Winch Update/input, wheel input, push fixed handlers, pump activate/release | Length/input/impulse/hold; физика хоста; T1 capture, T6 lock |
| Mooring/anchor | Unmoor/MoorTo/ChangeRopeLength; Anchor pickup/drop/held | Связь/длина/ack/anchor pose; T7 rope/adjuster carry |
| Cooking/resources | CutFood/InsertFood/FillWater/InsertDrink/SaltFood/LoadTobacco, cook trigger insert/takeout, FinishCast | Составной результат/components Pending T3–T5/T9 |
| World UI | Shipyard.ConfirmOrder, MapChart/ChartData edit, Cleaner/PaintObject, sleep entry | UI личный; world result T8/T9/T10; blind replay запрещён |

## Диагностика и приёмка

Items проверяет **23 реально устанавливаемые сигнатуры**. Input signatures отдельно проверяет **93 типа/182 объявленных метода**. Item actions назначает **29 alt/held входов** и показывает Pending с этапом: наличие пустого overload не означает готовую поддержку. Неполадки обязательного hook содержат точную сигнатуру и Missing/Failed, незавершённый relay — Pending. Метод/сигнатура не учитывается дважды.

Пять HOST-ONLY входов: GPButtonBed.OnActivate(), GPButtonTavernSleep.OnActivate(), GPButtonOnsenEntrance.OnActivate(), GPButtonAutosaveToggle.OnActivate(), ShipItemBed.OnAltActivate(). Последний получает prefix до EnterBed без нового event-postfix. Host/offline не блокируются, pickup кровати работает отдельно. Policy сохраняется до T10.

T0 закрывает выбор/назначение входов A1, обход A11 и false-green A13. Недостающие результаты A2–A10 не реализованы этим каталогом. Полнота body-эффектов, расход, extras и internal visuals закрываются T4/T5/T9. RuntimeSmoke 31/31 и ProtocolSmoke 52/48/4014 прошли на Windows .NET Framework; gameplay/Unity Mono ещё не проверены.

Проверки Protocol 76: см. СОСТОЯНИЕ.md §21. Catalog code paths не являются доказательством игровой приёмки; первоначальные findings A1–A13/B1–B12 сохранены в аудите и плане.

Верфь T8 (76): ConfirmOrder paid marker — InstallSails после local payment; UI не replay. Refit result/layout Generation и адресное context invalidation; preview публикация/save разделены, отдельная clone geometry не введена. Требуется игровая приёмка collision/VR/init/rejoin.
