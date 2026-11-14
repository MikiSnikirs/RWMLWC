# Исследование ввода-вывода: снимок карты, инъекция полей, транспорт (T-004)

Игра: RimWorld 1.7.5005 rev1082 (Waylith, .NET 10.0.15). Машина B, 14.11.2026.

Пометки: **[код]** — прочитано в декомпилированном коде игры (ilspycmd 11.2, код в репозиторий не попадает); **[запуск]** — проверено запуском в игре; **[док]** — из документации, здесь не проверялось; **[предположение]** — не подтверждено.

Временные проверки, давшие результаты ниже, лежат в коммите `c2cdb1f` ветки `b/T-004-io-research` (`Source/Client/Net/NetProbe.cs`, `SnapshotProbe.cs`) и удалены следующим коммитом.

## 1. Снимок карты (Scribe)

### Как устроено

**[код]** `ScribeSaver.InitSaving(filePath, rootName)` умеет писать только в файл: создаёт `FileStream`, на нём `XmlWriter` (с отступами) и входит в корневой узел. `FinalizeSaving()` закрывает `writer` и `saveStream` и проверяет ссылки (`loadIDsErrorsChecker`). Поля `saveStream` и `writer` приватные.

**[код]** `ScribeLoader.InitLoading(filePath)` читает файл в `XmlDocument` и ставит `curXmlParent = DocumentElement` (поле публичное), режим `LoadingVars`. `FinalizeLoading()` — разрешение перекрёстных ссылок (`crossRefs.ResolveAllCrossReferences`) и `PostLoadInit`.

**[код]** `Game.LoadGame()` грузит все карты одной коллекцией (`Scribe_Collections.Look(ref maps, "maps", LookMode.Deep)`), затем `Scribe.loader.FinalizeLoading()`, затем `map.FinalizeLoading()` для каждой карты (спавн вещей, затем `FinalizeInit`). Отдельного API «загрузить одну карту в идущую игру» нет. `Game.AddMap(map)` просто добавляет карту в список (лимит 127). `Game.DeinitAndRemoveMap` / `MapDeiniter.Deinit` — снятие карты.

### Снять снимок в память — работает

Способ: повторить `InitSaving`, подставив `MemoryStream` через рефлексию в `saveStream` и `writer`, затем `Scribe_Deep.Look(ref map, "map")` и штатный `FinalizeSaving()`.

**[запуск]** `RimWorld.exe -quicktest` (новая карта «Аварийная посадка», ваниль + все DLC), карта 250×250, 8 268 вещей, 11 пешек:

| Что | Результат |
|---|---|
| XML снимка (без отступов) | 1 500 393 байт за 234 мс |
| gzip, Fastest | 139 233 байт (9,3 %) за 7 мс |
| Brotli, Optimal | 84 776 байт (5,6 %) за 7 мс |
| Разбор XML в `XmlDocument` | 23 мс |
| Два снимка подряд без тиков | побайтно одинаковы (SHA-256 совпал) |
| Ошибки от мода в логе | нет |

Вывод: снимок недорогой, по сети пойдёт за 1 кусок `SnapshotChunk` (≤ 256 КиБ) после сжатия. Повторяемость снимка позволяет сравнивать снимки по хешу.

**Не проверено:** большая обжитая карта (в прогонах A было 33 926 вещей — ожидаю ×3–4 по размеру, **[предположение]**); снимок посреди тика; загрузка снимка обратно.

### Загрузить снимок — что известно и что надо решить в T-030

**[код]** Ссылки (`Scribe_References`) разрешаются через `LoadedObjectDirectory.ObjectWithLoadID`, а в каталог попадают только объекты, загруженные **в этом же сеансе** Scribe (`RegisterLoaded`). Карта ссылается на объекты вне себя — например, `MapInfo.parent` (объект карты мира) через `Scribe_References`.

Следствие: при загрузке одной карты в идущую игру нужно до `FinalizeLoading()` зарегистрировать в `Scribe.loader.crossRefs.loadedObjectDirectory` уже существующие объекты мира (фракции, идеологии, пешки мира, объекты карты мира и т. п.). Иначе ссылки станут `null`. Полный список типов **[предположение]** — уточнять в T-030 на реальном снимке (искать в XML все узлы-ссылки).

Порядок загрузки по аналогии с `Game.LoadGame` **[код, но собран вручную, не запускался]**: `curXmlParent` из байтов → режим `LoadingVars` → регистрация объектов мира → `Scribe_Deep.Look(ref map, "map")` → `Scribe.loader.FinalizeLoading()` → `Game.AddMap(map)` → `map.FinalizeLoading()`. Старую копию карты перед этим снять через `Game.DeinitAndRemoveMap`. Вопрос, при каком `uniqueID` и как это сочетается с раздельными списками тиков (T-005), — к A.

## 2. Инъекция полей без Prepatcher

**[код]** В игре есть собственный механизм `Waylith.Injection` и совместимый слой `RimWorldWaylith.PrepatcherCompat.dll` (оба поставляются с игрой). При запуске `RimWorld.dll` выполняет:

```csharp
PrepatcherCompat.Install();
ModLoadingHooks.AfterAssembliesLoaded = PrepatcherCompat.ProcessModAssemblies; // → Injection.ProcessAssemblies(сборки модов)
```

То есть **все сборки модов** проходят через инъекцию автоматически, мод Prepatcher не нужен.

Как объявить поле: статический метод-аксессор без тела (`static extern`) с атрибутом `[Prepatcher.PrepatcherField]` или `[Waylith.Injection.InjectedField]`. Допустимые формы **[код]**:

```csharp
[PrepatcherField] public static extern ref int MyCounter(this Map target);      // ref-геттер
[PrepatcherField] public static extern int MyCounter2(this Map target);          // геттер
[PrepatcherField] public static extern void SetMyCounter2(this Map t, int v);   // сеттер
```

Есть `[DefaultValue]`, `[ValueInitializer]`, `[InjectComponent]`. Метод не должен быть `InternalCall`/`PInvoke`. Поля применяются на границе кадра (`ApplyPendingAtFrameBoundary`), пока не применены — аксессоры бросают исключение, после 40 попыток пишется предупреждение.

Нужна ссылка сборки клиента на `RimWorldWaylith.PrepatcherCompat.dll` (или `Waylith.Injection.dll`) — в `RWMLWC.Client.csproj` её пока нет.

**Не проверено запуском:** ни одно поле ещё не внедрялось. Первое внедрение — проверить в той задаче, где поле понадобится (вероятно T-005/T-043 для локальных часов карты).

## 3. Транспорт

### Что есть

| Вариант | Что проверено |
|---|---|
| QUIC (`System.Net.Quic` + `msquic.dll`, оба в папке игры) | **[запуск]** в игре: `QuicListener.IsSupported = True`, локальное соединение с самоподписанным сертификатом (проверка по отпечатку SHA-256) — 58–85 мс, эхо дошло. Вне игры на этой машине — то же, 46 мс. |
| Steam Networking (Steamworks.NET в папке игры) | **[запуск]** `SteamManager.Initialized = True` в игре. **[код]** есть `SteamNetworkingSockets.CreateListenSocketP2P/ConnectP2P` и `SteamNetworkingMessages` — P2P по SteamID через релей Valve, без проброса портов. Соединение между двумя машинами **не проверялось** (нужны два аккаунта). |
| TCP + TLS (`System.Net.Sockets`, `SslStream`) | Не проверялось — заведомо работает в .NET. |

Ограничения:
- QUIC на Windows работает только на Windows 11 / Server 2022 и новее **[док]**. На Windows 10 `IsSupported` будет `false`.
- QUIC и TCP — это UDP/TCP-порт у того, кто держит сервер: дома нужен проброс порта, VPN (Radmin, ZeroTier, Tailscale) или VPS.
- Steam P2P не работает в отдельном консольном сервере без Steam (VPS) — только в процессе, где запущен Steam-клиент.

### Решение

1. **Сервер говорит только по QUIC.** Один и тот же код в консольном сервере и на VPS. Шифрование из коробки, несколько потоков: снимок карты идёт своим потоком и не задерживает команды и тики. Сертификат — самоподписанный, клиент проверяет отпечаток (передаётся вместе с адресом в «коде приглашения»).
2. **Транспорт за интерфейсом** (`ITransport`: кадры туда/обратно, события подключения/обрыва). Плюс транспорт в памяти для юнит-тестов (TST-012, TST-013 без сети).
3. **TCP + TLS — запасной вариант** для Windows 10, если у кого-то из игроков QUIC не поддерживается. Кадры уже с длиной (`ProtocolCodec.EncodeFrame`), так что для TCP почти ничего добавлять не нужно.
4. **Steam — мост, не транспорт сервера.** Хост поднимает сервер у себя; его игра принимает Steam P2P-подключения друга и перекладывает кадры в локальный сервер. Друг подключается по Steam без проброса портов. Отдельная задача после T-012.
5. **Переподключение** (T-012): при обрыве клиент переподключается и шлёт `Hello` с токеном сессии; сервер досылает команды и тики с последнего подтверждённого. Для этого протокол получит поле токена и номер последней полученной команды — это изменение `Shared`, пойдёт через КОНТРАКТ.

## 4. Что это меняет в задачах

- T-012: QUIC + транспорт в памяти + переподключение; TCP + TLS — если игроку нужен (см. вопрос людям).
- Новая задача T-016 (B): мост Steam P2P → локальный сервер.
- T-030 (A): способ снятия снимка — раздел 1; главная работа — регистрация объектов мира при загрузке.
- Клиенту нужна ссылка на `RimWorldWaylith.PrepatcherCompat.dll`, когда понадобится первое внедрённое поле.
