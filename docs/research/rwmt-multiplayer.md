# RimWorld Multiplayer и Multiplayer Compatibility: что у них лучше и подойдёт ли мост API

27.11.2026, агент A. Разбор исходников, игра не запускалась. Исходники взяты с GitHub, все три под лицензией MIT:
- `rwmt/Multiplayer`, ветка `dev`, коммит `ddfccdb` от 06.11.2026, переход на .NET 10;
- `rwmt/Multiplayer-Compatibility`, коммит `1a050f0` от 11.09.2026;
- `rwmt/MultiplayerAPI`.

В сборке стоит временная копия Multiplayer для 1.7 от стороннего автора (Steam 3820003322) с тем же `packageId`.

## Масштаб

| | Multiplayer | RWMLWC |
|---|---|---|
| Код C# | около 62 000 строк, из них синхронизация 7 500, патчи игры 5 300, общие окна 5 200 | около 29 000 строк |
| Синхронизация ввода | поимённо: около 530 записей в коде мода (`SyncMethod`, `SyncDelegate`, `SyncField`) | общим механизмом: кнопки, разметка, меню по щелчку, выбор цели — без перечисления; окна — поимённо (`MethodCommands`, `FieldWatch`) |
| Совместимость с модами | отдельный мод Compatibility: 228 файлов. Для 27 модов нашей сборки — 5 500 строк | общий механизм; для чужих окон пока ничего |

## Где у них лучше

1. **Поиск расхождений.**
   - Как у них: Multiplayer снимает стек на каждый вызов `Rand.Value` и `Rand.Int`, на каждый новый номер вещи и на каждое появление вещи. Для этого он обходит кадры стека сам (`DeferredStackTracing`, только x64/x86), а имена методов подставляет только при разборе. Это дёшево, поэтому включено всегда, в кольцевом буфере. При расхождении оба клиента сохраняют отчёт, и он сразу показывает код, который разошёлся.
   - Как у нас: `RandTracer` снимает стек обычным `StackTrace`. Это дорого, поэтому включается только на окно тиков и только по ключу. В остальное время у нас есть хеш и деление сборки пополам.
   - Для сборки из 141 мода их способ сэкономит большую часть прогонов этапа 2: виновник виден из одного отчёта.
   - Есть ещё «арбитр»: невидимый третий экземпляр игры решает, кто прав при расхождении.
2. **Детерминизм ванили — опыт нескольких лет.** Из 81 метода игры, который они патчат ради совпадения, у нас встречаются около 23 (поиск по имени класса и метода — грубый). Многие остальные нужны их модели времени или интерфейсу. Но есть и настоящие исправления, которых у нас, похоже, нет (предположение, у нас не проверено):
   - **кэши, которые обновляет интерфейс:**
     - `WealthWatcher.ForceRecount` из интерфейса;
     - `DangerWatcher.DangerRating`;
     - `Caravan.ImmobilizedByMass`;
     - `StoryWatcher_PopAdaptation.Notify_PawnEvent`;
     - `AutoSlaughterManager.Notify_ConfigChanged`;
     - `SituationalThoughtHandler.CheckRecalculateSocialThoughts`;
     - кэш `PawnCapacitiesHandler.GetLevel`;
   - **сохранение меняет состояние:** `Pawn_RecordsTracker.ExposeData` пишет `battleActive`;
   - **зависит от камеры:** `FloodFillerFog.FloodUnfog` (`allOnScreen`);
   - **случайность без своего зерна:**
     - поворот при `GenSpawn.Spawn`;
     - `PreceptComp_UnwillingToDo_Chance`;
     - обрушения `UndercaveMapComponent` (Anomaly).

   На ванили мы этого не поймали, вероятно, потому что сценарии не трогали эти места. Каждый пункт стоит проверить у нас.
3. **Общие окна.** Сессиями сделаны:
   - формирование и разделение каравана;
   - ритуалы и психические ритуалы;
   - порталы карт;
   - полёт гравикорабля;
   - «момент роста» Biotech;
   - пауза по требованию окна.

   У нас есть `SharedWindows`. Список стоит сверить, когда дойдём до этих окон.

## Где лучше у нас

- Кнопки объектов, разметка, меню по правому щелчку и выбор цели из любых модов синхронизируются **без записей под каждый мод**. Multiplayer требует записи на каждую такую кнопку, поэтому Compatibility такой большой. Для сборки из 141 мода это наше главное преимущество.
- Выделенный сервер, права на карты, отряды, раздельный счёт колоний, режимы отсутствия — у Multiplayer этого нет (у них другая модель: отдельные фракции игроков и своё время у каждой карты).

## Мост Multiplayer API: проверка

**Как мод находит Multiplayer** (`MP.cs` в MultiplayerAPI): среди загруженных сборок модов ищется сборка с именем `Multiplayer`, в ней — поле `Multiplayer.Common.MultiplayerAPIBridge.Instance` типа `IAPI`. Если оно есть, `MP.enabled = true`, и все вызовы `MP.*` идут в него.

Значит, мост технически возможен: мы кладём в свой мод маленькую сборку `Multiplayer.dll` с этим типом, и она передаёт вызовы в RWMLWC. Включать её вместе с настоящим Multiplayer нельзя — так же, как и сам мод.

**Что Compatibility вызывает и чем это у нас закрывается.** Счёт по всему Compatibility:

| Вызов API | Сколько раз | У нас |
|---|---|---|
| `RegisterSyncMethod` (+ `Lambda`) | 512 | `MethodCommands`: «метод M у объекта X с аргументами» и `ValueCodec`. Совпадает по смыслу |
| `IsInMultiplayer`, `InInterface`, `IsExecutingSyncCommand`, `…IssuedBySelf` | 366 | состояние `CommandBus` и тика; просто |
| `RegisterSyncWorker` | 168 | сериализаторы своих типов. Нужен переходник: класс `SyncWorker` (`Bind`, `Read`, `Write`) поверх нашего буфера `ValueCodec` |
| `RegisterSyncField`, `WatchBegin`/`WatchEnd` | 135 | `FieldWatch`: сравнение полей до и после. Совпадает по смыслу, нужен ручной вызов вместо списка методов |
| `RegisterSyncDelegate` (+ `Lambda`, `LocalFunc`) | 31 | замыкание с перечисленными полями. Передавать поля объекта-замыкания — `ValueCodec` умеет поля простых типов и ссылки на объекты игры, остальное — через `SyncWorker` |
| `SetContext` (карта и выделение, клетка под мышью) | 46 | у щелчка по карте уже передаётся выделение; нужно для методов |
| `SetDebugOnly` | 125 | отладочные действия — можно не поддерживать |
| сессии, `RegisterDialogNodeTree`, `RegisterPauseLock`, письма | около 25 | заглушки на первом шаге; письма — `LetterChoices` |

**Двойная отправка не грозит.** Кнопка мода, которую Compatibility описал через `RegisterSyncMethod`, у нас уже уходит общим механизмом. При исполнении нашей команды мост отвечает `IsExecutingSyncCommand = true`, и описанный метод исполняется на месте, без второй отправки. Это надо проверить первым тестом моста.

**Препятствия:**
1. В `About.xml` Compatibility требует мод `rwmt.Multiplayer`. Без него игра покажет предупреждение в списке модов. Что она сделает дальше, не проверено. Лицензия MIT позволяет положить `Multiplayer_Compat.dll` в наш мод с текстом лицензии — тогда зависимости нет.
2. 14 файлов Compatibility лезут во внутренности Multiplayer через reflection (`Multiplayer.Client.*`). Из нашей сборки это Humanoid Alien Races (окно причёски), Alpha Animals и Dubs Mint Menus. Эти части у нас не заработают, их надо делать самим или обходить.
3. Compatibility включает **свои исправления детерминизма модов** (около 100 патчей для нашей сборки, например Rand для VEF, ReGrowth, Fortifications) только при `MP.enabled`. Это плюс: малый мост сразу даёт эти исправления.

**Что даёт мост для нашей сборки:**
- описания 27 модов из Compatibility: около 100 записей синхронизации, 40 сериализаторов, около 100 патчей детерминизма;
- моды, которые сами вызывают API — Fortified Features Framework и YART, а через них моды на Fortified.

Оценка: малый мост (`enabled`, состояние, методы, сериализаторы, поля) — 3–4 сессии; полный (замыкания, контексты, сессии) — ещё 3–4.

## Вывод

1. Мост подходит к нашей шине команд. Делать его **раньше** этапа 2 (тишина): исправления детерминизма из Compatibility включаются только с мостом, без него мы будем искать уже найденное.
2. Перенять их дешёвый стек на каждый `Rand` (MIT, с указанием авторства) — инструмент для этапа 2.
3. Проверить у нас список исправлений ванили из раздела «Где у них лучше», п. 2.
4. Свою модель ввода не менять: для модов она сильнее.

## Сверка исправлений ванили (T-088, 27.11.2026)

Проверено по коду игры **1.7.5021** (игра обновилась 27.11 утром; декомпиляция `ilspycmd` 11.2 в `%USERPROFILE%\rw-decompiled-5021`). Multiplayer (ветка `dev`) уже рассчитан на 1.7, но каждое место смотрелось в нашей версии игры заново. Сверялись все их патчи, которые касаются совпадения симуляции: `Determinism.cs`, `Seeds.cs`, `Patches.cs`, `HashCodes.cs`, `Odyssey.cs`, `Anomaly.cs`, `PathFinderPatch.cs`, `VTRSyncPatch.cs`, `TileTemperatures.cs` и др.

### Применено у нас (`Source/Client/Sim/VanillaDeterminismFixes.cs`)

| Место в игре | Что не так в 1.7.5021 | Правка в сессии |
|---|---|---|
| `Zone.Cells`, `Plan.Cells` | клетки перемешиваются через `Rand` при первом обращении; первым часто обращается подсказка огорода (`Zone_Growing.GetInspectString`) — на состоянии `Rand` показа, у каждого своём. По порядку клеток носят на склад и сеют | вне симуляции клетки без перемешивания |
| `WealthWatcher.ForceRecount` | вкладка «История» и геттеры богатства пересчитывают его в кадре; богатство идёт в очки угроз | вне симуляции пересчёт пропускается (кроме создания карты) |
| `DangerWatcher.DangerRating` | кэш «значение + тик», каждый кадр его спрашивает музыка | вне симуляции — последнее значение |
| `Caravan.ImmobilizedByMass` | кэш на 60 тиков от первого спросившего | вне симуляции — без записи в кэш |
| `Pawn_AbilityTracker.AllAbilitiesForReading` | список собирается тем, кто спросил первым (кнопки пешки) | вне симуляции — во временный список, кэш не трогается |
| `PriorityWork.Clear` | геттер `IsPrioritized` из кнопок пешки сбрасывает приоритетную работу | вне симуляции сброс пропускается |
| `SituationalThoughtHandler` (4 метода) | окно потребностей и вкладка отношений пересчитывают мысли и сдвигают сроки кэша | вне симуляции — только посчитанное, без пересчёта |
| `PawnCapacitiesHandler.GetLevel` | способности (сознание, движение…) кэшируются тем, кто спросил первым, а боль меняется плавно, без сброса кэша | значение интерфейса помечается особо, симуляция его пересчитывает |
| `CompDissolutionEffect_Goodwill/Pollution.WorldUpdate` | загрязнение мира и потеря отношений от растворения отходов применяются **раз в кадр** (`World.WorldUpdate`) | применяются перед каждым тиком, в кадре — нет |
| `UndercaveMapComponent.MapComponentTick` (Anomaly) | вся логика обрушения, включая падение камней и `Rand`, идёт только у смотрящего на карту (`Find.CurrentMap`) — `SimCamera` этого не закрывал | камни и `Rand` у всех, тряска, звук и фоновые эффекты — у смотрящего |
| `PawnsArrivalModeWorker_EmergeFromWater.Arrive` | ошибка игры: появление пешек ставится на `Find.CurrentMap`, а не на карту события | карта события |
| `FastTileFinder.Query` | параллельный поиск клеток мира, 50 мест «кто первый успел» | один пакет — по порядку |
| `PathFinder.PathFinderTick` | задание `PathGridDoorsBlockedJob` идёт в другом потоке **одновременно с тиком** и читает живые позиции и задания пешек. Наш разбор 6а считал поиск пути безопасным — это было неверно | задания завершаются сразу после постановки, до тика пешек |
| `ThingGrid.Register` | мошки (не сохраняемые вещи) попадают в сетку вещей только у смотрящего | не сохраняемые вещи в сетку не ставятся |
| `PawnBioAndNameGenerator.TryGetRandomUnusedSolidName` | общий список имён перемешивается на месте и хранит порядок всю жизнь процесса | перед выбором списки приводятся к одному порядку |
| `QuestNode_TradeRequest_GetRequestedThing.RandomRequestCount` | **не из их списка**: зерно от `thingDef.GetHashCode()`, а хеш `Def` — хеш строки имени, в .NET он свой в каждом процессе | `shortHash` |

### Уже было у нас

- `StatWorker.GetValue` → `StatCacheIsolation`;
- `Pawn_MeleeVerbs.TryGetMeleeVerb` → `MeleeVerbUiIsolation`;
- `MapBrightnessTracker.Tick` (`deltaTime`) и `Pawn_JobTracker.StartJob` (номер кадра) → `FrameIndependentState`;
- `WindManager.BaseWindSpeedAt` → `DeterministicGenSeed`;
- `FishShadowComponent.MapComponentTick` → `VisualRandIsolation`;
- мошки, `MoteAttachLink`, `MoteMaker.MakeStaticMote` → `VisualRandIsolation`;
- `FloodFillerFog.FloodUnfog` (`allOnScreen`), `AnomalyUtility.TryGetNearbyUnseenCell` → `SimCamera` (кадр камеры в тике — вне карты);
- `Pawn.ProcessPostTickVisuals`, `PawnTweener` → `TickStableDrawPos`, `TickDrivenRotation`;
- `GenTicks.GetCameraUpdateRate` (их `VTRSyncPatch`) → 6з;
- долгие события из тика → `SessionLongEvents`;
- тексты и имена (`GrammarResolver`, `NameGenerator`, `GeneratePawnName`) → `TextRandIsolation`;
- номера объектов, созданных интерфейсом → `FieldWatch` (`idgive`).

### Не нужно в нашей модели или в 1.7.5021

- **`Pawn_RecordsTracker.ExposeData` пишет `battleActive`.** Геттер `BattleActive` делает ту же запись при каждом чтении, результат одинаков.
- **`PreceptComp_UnwillingToDo_Chance`, `RitualBehaviorWorker_GravshipLaunch.CanReachGravship`.** Вне тика у нас и так `Rand` показа. Кэш «может дойти» от `Rand` не зависит.
- **`GenSpawn.Spawn` (`Rot4.Random`), `CaravanEnterMapUtility.Enter`, загрузка (`Game.LoadGame`, `Map.ExposeData`).** Команды и тик идут на общем `Rand`, загружают все один снимок.
- **`PawnRenderer.SetAllGraphicsDirty`.** В 1.7 лямбда только ставит пометки, `Rand` нет.
- **`Building_Electroharvester` / `Building_BioferriteHarvester`** (инициализация при отрисовке). Подписки перестраивают только провода — вид, `Rand` не берут.
- **`Pawn_GeneTracker.Notify_GenesChanged` (`BiotechInstalled`).** Без включённого Biotech генов нет. Разный состав включённых DLC и так не допускается.
- **`WorldObjectSelectionUtility.VisibleToCameraNow`, `Archive.Add`, сортировка одежды.** Первые два — только сообщение и архив писем. Сортировка одежды детерминирована.
- **Хеши `GlowGrid.litGlowers`, `TargetInfo`, `Tradeable`.** В .NET порядок обхода `HashSet`/`Dictionary` не зависит от значений хешей, в зёрна эти хеши не идут.
- **Их собственная модель:** своё время у каждой карты (`TileTemperatures`, `IncidentDef.TargetAllowed`, `IncidentWorker_Aurora`), фракции игроков (`FogGrid`, предупреждения), синхронизация и окна (`DiaOption`, `Designator`, `WorkSettings`, `MainTabWindow`, `ScreenFader`, `TickManager.Pause`, `DirectXmlSaver` и др.).

### Найдено попутно, в этой задаче не сделано

- **Окно автозабоя (`Dialog_AutoSlaughter`) в сессии не синхронизируется:** меняет настройки прямо в поле и зовёт `Notify_ConfigChanged` только у автора. Это пробел ввода, а не детерминизма; расхождение ловит сверка хешей.
- **Окно имени гравикорабля** при первой постройке открывается у всех; имя не синхронизируется.
- В .NET хеш строки свой в каждом процессе, поэтому любой код, который берёт `Def.GetHashCode()` или хеш строки в зерно `Rand`, расходится. В игре 1.7.5021 нашлось одно такое место (выше). **В модах их надо искать отдельно** (этап T-083).
