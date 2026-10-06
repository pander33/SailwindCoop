# Реализация и приёмка Sailwind LAN Co-op

Текущий плагин 0.2.1, Protocol 80 (актуально на 2026-10-06). Ниже — описание реализации по состоянию на Protocol 63 и хроника до Protocol 76; дальнейшие изменения и открытые задачи — `СОСТОЯНИЕ.md` §22–§41 и §5.

Подробный будущий план по A1–A13 и остальным областям полного аудита —
[INTERACTION_REMEDIATION_PLAN.md](INTERACTION_REMEDIATION_PLAN.md): T0–T13,
зависимости, изменения протокола, целевые расширения и критерии приёмки.
В коде реализованы T0–T10; T11 и T12 — частично (остаток — `СОСТОЯНИЕ.md` §5, пп. 5–7); игровая приёмка T13 не проводилась. Ограничения — `СОСТОЯНИЕ.md` §12–§27.

2026-10-03: T0 реализован — [INTERACTION_CATALOG.md](INTERACTION_CATALOG.md),
93 типа/182 signatures, 23 item hooks, guard no-arg bed и честный PatchHealth.
RuntimeSmoke 31/31, ProtocolSmoke 52/48/4014, Release 0/0; gameplay открыт.

## A. Обработка исключений

Interaction Harmony callbacks защищены PatchGuard. Ошибки обработчика и диагностики
логируются с ограничением повторов и не прерывают vanilla input.

## B. Сохранения

AtomicSaveFile пишет уникальный temp рядом с целью, выполняет flush, закрывает файл и
заменяет цель с `.bak`. Читаемый backup используется для восстановления профиля;
повреждённая основная копия не затирает читаемый backup. Полученный мир проверяется по
GameVersion до записи и загрузки.

## C. Независимые лодки и взаимодействия

BoatContexts хранит отдельный контекст на каждую купленную лодку. Controls, Anchor,
Mooring, Damage и Interaction передают BoatIndex и индекс объекта. LayoutHash — диагностика.
Контекст привязан к конкретной лодке; смена состава объектов обновляет его, disconnect
восстанавливает исходную физику. Активность дальних лодок учитывает позу гостя.

Хост полностью доверяет действиям клиента и рассылает результат. Проверки допустимости
действий и числовых значений запросов не вводятся (решение 2026-10-03, риски приняты).
Остаются handshake отправителя, совпадение NetId в PlayerState, границы индекса/null объекта
и формат пакета. LayoutHash остаётся диагностикой.

Mouse/sticky grab и quick-release поддерживаются. Удерживаемый канат не перезаписывается
входящим snapshot. Помпа использует обновляемый held-канал с timeout 1 с. Люки передают
целевой HatchState/HatchOpen, который применяется после завершения анимации;
события возникают только после взаимодействия. Начальное состояние передаётся разовым
HatchSnapshot при join/новом контексте; во время загрузки данные ждут контекста лодки.

Швартовы используют RequestId + подтверждение с NetId отправителя. До ответа на последний
запрос и отпускания каната/катушки клиент не применяет старое состояние. Недоступный dock
диагностируется; клиент повторяет применение после загрузки цели, хост подтверждает запрос
с StateAvailable=false, если не может передать moored dock. Одинаковые снапшоты не пишут
лог/Remember. IsInteraction разделяет действия и snapshots швартовов; item manifest адресный
с IsSnapshot. State/pose/echo/initial sync не затирают последнее действие; polling якоря не
создаёт уведомление об игроке. Отказ join из загруженного мира выполняется до записи co-op слота.

AnchorSync получает якорь через joint своего RopeControllerAnchor (Awake меняет parent).
AnchorRequest (88): pickup/held pose/drop, ReliableOrdered (Boat на своей палубе, World на берегу/drop); AnchorState передаёт holder,
длину троса и RequestId/RequesterNetId/Revision. Локальная рука и ожидающий drop защищены;
удалённый ExtraFixedUpdate подавлен, свободная физика — на хосте. Игровой перенос пока не проверен.

ShipItemFoldable (карты/мебель): amount передаётся существующим ItemRequest(State)/ItemState,
применяется абсолютным Fold/Unfold (mesh/details/collider); capture учитывает OnLoad, местная
рука защищена. Postfix отправляет только фактическую смену формы, дублирующий alt relay исключён.

### C4. Игровая матрица приёмки

- Wheel, sail reef/furl/angle, quick-release и anchor у хоста и клиента.
- Якорь в руке: хост/гость, наблюдение второго гостя, перенос/вращение/drop, берег/лодка,
  floating origin, длина троса, задержка/быстрый pickup/drop, late join/уход держащего/disconnect.
- Moor/unmoor/rope length с палубы и берега; push лодки и паруса.
- Швартовы с задержкой/несколькими запросами: старый snapshot, удержание каната/катушки,
  отпускание, разные гости/лодки, отсутствующий/поздно загруженный причал и тихий no-op snapshot.
- Карта/мебель: fold/unfold хоста/гостя, drop, поздний join, повторный и старый packet; камера локальна.
- Pump, bail, oakum; отпускание удержания и disconnect.
- Hatch: повторные клики, задержка и позднее подключение.
- Минуту без взаимодействий: нет ControlEvent таймера, последнее действие не заменяется snapshot;
  затем реальный клик/hold и late join с отдельным начальным состоянием.
- Два/три игрока на разных лодках; смена палубы и выход на берег.
- Покупка/верфь, лодка дальше 10 км, reconnect и восстановление физики.
- Профиль/слот: backup/recovery и File.Replace в Unity Mono.
- Join из загруженного мира: точный отказ без изменения co-op слота/backup.

## Проверка

2026-10-03: установлен Release, 0 ошибок/предупреждений; RuntimeSmoke 26/26;
ProtocolSmoke 52 типа / 48 round-trip / 4014 truncated cases, Protocol 63. Это проверки на Windows
.NET Framework; игровая матрица и Unity Mono остаются открытыми.
Один промежуточный RuntimeSmoke дал 22/1 на прежнем File.Replace recovery; повтор — 23/23.
Причина нестабильности пока не установлена, код AtomicSaveFile не менялся.


Текущее продолжение remediation (2026-10-04): T7 carry/dock wait — Protocol 70, T9 приборы/weather result — 71, committed chart marks — 72, UV/точная текстура грязи/CleanFully — 73. Последние проверки Release 0/0, ProtocolSmoke 61/78/21246, RuntimeSmoke 49/49. Остальные пункты и игровая приёмка открыты; журнал — СОСТОЯНИЕ.md §15–18.


Продолжение T9: Protocol 74 — WindTotemOrb carry/parent visual и causal WindRequest, touch/VR-wheel capture; Protocol 75 — buffered Instrument child stream для rod/ChipLog/fish, root epoch/parent revision и reliable final drop. T9 кодовые пути реализованы, его игровая приёмка открыта. Следующие этапы T8, T10–T13; актуальные технические ограничения и проверки — СОСТОЯНИЕ.md §19–20.


Продолжение 2026-10-04: Protocol 76 — T8 committed refit (sails/parts/repair/clean), local payment only, independent hull Generation и deferred indexed packets/context rebuild. Native preview/save publication isolated, clone preview отсутствует. Release 0/0, ProtocolSmoke 67/99/26604, RuntimeSmoke 58/58; игровая приёмка/Unity Mono, T10–T13 и прежние остатки открыты. См. СОСТОЯНИЕ.md §21.
