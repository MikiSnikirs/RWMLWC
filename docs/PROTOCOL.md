# Протокол (v0, `ProtocolVersion = 1`)

Код: `Source/Shared/Protocol/`. Тесты: `Source/Tests/ProtocolRoundTripTests.cs` (TST-011).
Изменение любого сообщения повышает `ProtocolVersion` и идёт через запись КОНТРАКТ в `AGENT_LOG.md`.

## Кадр

```
int32  длина   — байты после этого поля (тип + тело), little-endian
uint16 тип     — MessageType
...    тело
```

- Все числа little-endian.
- Строка: `int32` длина в байтах + UTF-8. Блок данных: `int32` длина + байты.
- Лимиты (`ProtocolLimits`): кадр 4 МиБ, строка 64 КиБ, кусок снимка 256 КиБ, частей хеша 256.
- Любая ошибка формата (обрыв, лишние байты, неверная длина, неизвестный тип, не UTF-8) — `ProtocolException`. Получатель рвёт соединение.
- Потоковый транспорт: `ProtocolCodec.EncodeFrame` / `TryDecodeFrame` (собирает кадр из кусков). Транспорт с готовыми сообщениями: `EncodeBody` / `DecodeBody`.

## Сообщения

| № | Тип | Направление | Поля |
|---|---|---|---|
| 1 | `Hello` | клиент → сервер | `ProtocolVersion`, `ModId`, `GameVersion`, `PlayerName`, `ModListHash` (u64) |
| 2 | `Welcome` | сервер → клиент | `PlayerId`, `ServerTick` |
| 3 | `Reject` | сервер → клиент | `Reason` (`RejectReason`), `Detail` |
| 4 | `Command` | клиент → сервер | `ClientSeq`, `MapId`, `Kind`, `Payload` |
| 5 | `CommandOrdered` | сервер → все | `Seq` (i64), `Tick`, `PlayerId`, `ClientSeq`, `MapId`, `Kind`, `Payload` |
| 6 | `Tick` | сервер → все | `TickUpTo`, `Speed` (0 — пауза, 1..4 — `TimeSpeed`) |
| 7 | `SpeedRequest` | клиент → сервер | `Speed` |
| 8 | `SnapshotChunk` | в обе стороны | `SnapshotId`, `MapId`, `Tick`, `ChunkIndex`, `ChunkCount`, `Data` |
| 9 | `MapHash` | клиент → сервер | `MapId`, `Tick`, `Total` (u64), `Parts` — список (имя, u64) |

`MapId` — `Map.uniqueID`; `-1` (`ProtocolIds.WorldMapId`) — уровень мира.

## Сценарий

1. Клиент подключается и шлёт `Hello`. Сервер отвечает `Welcome` или `Reject` и закрывает соединение.
2. Игрок что-то делает → клиент шлёт `Command` и **не исполняет его сам**.
3. Сервер назначает команде `Seq` и тик исполнения, рассылает `CommandOrdered` всем, включая автора.
4. Сервер рассылает `Tick`. Гарантия: все команды для тиков `≤ TickUpTo` разосланы **раньше** этого `Tick`. Клиент тикает до `TickUpTo` и исполняет команды каждого тика по возрастанию `Seq`.
5. Клиент периодически шлёт `MapHash`; сервер сравнивает хеши игроков по `(MapId, Tick)` и при расхождении запускает пересылку карты (T-034) кусками `SnapshotChunk`.

Что решено **не в v0** и остаётся задачам: формат `Payload` команд и реестр `Kind` (A, T-020–T-023), формат и сжатие данных снимка (A, T-030), права (B, T-031), чат и пинг (B, T-025).
