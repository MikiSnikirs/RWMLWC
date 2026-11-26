using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// События карты (T-056, docs/ARCHITECTURE.md, 11.4а): запись в общий мир из тика карты не исполняется на месте,
/// а встаёт в очередь карты; докладчик карты отправляет очередь командой мира, и при её исполнении запись
/// применяют все — на одном тике, одинаково. Кто считал карту, применяет свой объект; у кого карта спит —
/// восстанавливает его из присланных данных (<c>Scribe</c>, ссылки по номерам).
///
/// Сейчас — сказания (<c>TaleManager.Add</c>): по ним <c>CompArt</c> выбирает сюжет с <c>Rand</c> на любой карте,
/// а при раздельном счёте они расходились первыми после журнала (прогоны sl1, sl2).
///
/// Ключ события — (карта, тик, номер в тике); у карты хранится последний применённый ключ
/// (<see cref="MapSimState"/>), поэтому повтор от второго докладчика не применится дважды.
/// Вне сессии и без раздельного счёта — как в игре.
/// </summary>
public static class MapEvents
{
    public const byte OpMapEvents = 40;
    private const byte KindTale = 1;
    private const byte KindPawnDeath = 2;
    private const byte KindPawnLeave = 3;
    private const byte KindTaleRemove = 4;
    private const byte KindPawnDiscard = 5;
    private const byte KindPawnEnter = 6; // мировая пешка пришла на карту: у игрока без карты её мировая копия уходит
    private const byte KindPawnToWorld = 7; // живая пешка ушла в мир не с карты (создана в тике карты: родственник и т.п.)
    private const int DropAfterTicks = 5000;
    private const int MaxPerCommand = 32;
    private const int WorldLevelMap = -1; // события мира (удаление сказаний): докладчик — младший игрок в сети

    private sealed class Pending
    {
        public int map;
        public int tick;
        public int seq;
        public byte kind;
        public Tale? tale;
        public Pawn? deadPawn; // смерть (шаг 4): у владельца уведомления уже исполнены в тике — своё не повторять
        public string? dinfoDef;
        public float dinfoAmount;
        public int dinfoInstigator = -1;
        public byte dinfoCategory;
        public int factionLoadId = -1; // смерть: дельта goodwill и новый лидер — данными (у спящего нет карты)
        public int goodwillDelta;
        public bool leaderChanged;
        public Pawn? leftPawn; // уход (шаг 4): у владельца уведомления уже исполнены в тике — своё не повторять
        public bool leaveFreed; // освобождённый пленник/раб (goodwill за освобождение)
        public int removeTaleId = -1; // удаление сказания: все применяют по номеру (список сказаний общий)
        public int discardPawnId = -1; // уборка мировой пешки сборщиком: все применяют по номеру
        public string? xml; // снимается при отправке (вызывающий может дописать сказание после RecordTale: customLabel)
        public bool sent;
    }

    private static readonly List<Pending> pending = new List<Pending>();
    private static int seqTick = -1;
    private static readonly Dictionary<int, int> seqByMap = new Dictionary<int, int>();
    private static bool applying;

    public static int Queued => pending.Count;

    internal static bool Applying => applying;

    /// <summary>Запись надо отложить: идёт тик карты в сессии (не исполнение события).</summary>
    internal static bool ShouldDefer(out Map? map)
    {
        map = null;
        if (applying || !LockstepClient.InSession || !PerMapSim.Enabled || !MapRandContext.InTick)
        {
            return false;
        }
        map = MapRandContext.Current;
        return map != null;
    }

    internal static void QueueTale(Map map, Tale tale)
    {
        int tick = Find.TickManager.TicksGame + StasisClock.Offset; // общие часы, не часы карты
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(map.uniqueID, out int seq);
        seqByMap[map.uniqueID] = seq + 1;
        pending.Add(new Pending { map = map.uniqueID, tick = tick, seq = seq, kind = KindTale, tale = tale });
        TestAutomation.Note($"map events: tale {tale.def.defName} #{tale.id} on map {map.uniqueID} queued at tick {tick}.{seq}");
    }

    /// <summary>
    /// Смерть пешки на карте (T-056, шаг 4 плана 11.4б): у спящего клиента карты нет, и мировые последствия
    /// смерти (мёртвая пешка в WorldPawns, адаптация, уведомления фракций и заданий) без события не наступят.
    /// Владелец исполняет их в тике как обычно; событие несёт XML пешки и контекст урона, спящий восстанавливает
    /// пешку и исполняет уведомления под изолированным Rand (общий Rand не трогается).
    /// </summary>
    internal static void QueuePawnDeath(Map map, Pawn pawn, DamageInfo? dinfo, int factionLoadId, int goodwillDelta, bool leaderChanged)
    {
        int tick = Find.TickManager.TicksGame + StasisClock.Offset;
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(map.uniqueID, out int seq);
        seqByMap[map.uniqueID] = seq + 1;
        pending.Add(new Pending
        {
            map = map.uniqueID,
            tick = tick,
            seq = seq,
            kind = KindPawnDeath,
            deadPawn = pawn,
            dinfoDef = dinfo?.Def?.defName,
            dinfoAmount = dinfo?.Amount ?? 0f,
            dinfoInstigator = dinfo?.Instigator?.thingIDNumber ?? -1,
            dinfoCategory = (byte)(dinfo?.Category ?? DamageInfo.SourceCategory.ThingOrUnknown),
            factionLoadId = factionLoadId,
            goodwillDelta = goodwillDelta,
            leaderChanged = leaderChanged,
        });
        TestAutomation.Note($"map events: pawn death {pawn.def.defName} #{pawn.thingIDNumber} on map {map.uniqueID} queued at tick {tick}.{seq} (goodwillDelta={goodwillDelta}, leaderChanged={leaderChanged})");
    }

    /// <summary>
    /// Уход пешки с карты (T-056, шаг 4): пешка уходит в мировые пешки, фракция получает уведомление
    /// (goodwill за здорового гостя / освобождение), задания — сигнал LeftMap. У спящего карты нет —
    /// событие несёт XML пешки и флаг освобождения; спящий восстанавливает пешку и исполняет уведомления
    /// под изолированным Rand. Goodwill при уходе считается без карты — синхронизируется полностью.
    /// Пробел первой итерации: создание/вступление в караван при уходе (новый объект мира) не синхронизировано.
    /// </summary>
    internal static void QueuePawnLeave(Map map, Pawn pawn, bool freed)
    {
        int tick = Find.TickManager.TicksGame + StasisClock.Offset;
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(map.uniqueID, out int seq);
        seqByMap[map.uniqueID] = seq + 1;
        pending.Add(new Pending
        {
            map = map.uniqueID,
            tick = tick,
            seq = seq,
            kind = KindPawnLeave,
            leftPawn = pawn,
            leaveFreed = freed,
        });
        TestAutomation.Note($"map events: pawn leave {pawn.def.defName} #{pawn.thingIDNumber} from map {map.uniqueID} queued at tick {tick}.{seq} (freed={freed})");
    }

    /// <summary>
    /// Пешка передана в мир в тике карты, но не уходом с карты (T-056, sr34): создана при событии карты (родственники
    /// пешек набега или гостей, пешки заданий карты) и сразу ушла в мировые пешки. Она есть только у считающего карту:
    /// у второго её нет, а сборщик мусора у считающего убрать её не может (исполняются удаления докладчика) — состав
    /// мировых пешек расходился навсегда (sr34: c2 на 2 живые пешки больше со 150000). Событие несёт XML пешки,
    /// игрок без карты добавляет её в мировые пешки без уведомлений.
    /// </summary>
    internal static void QueuePawnToWorld(Map map, Pawn pawn)
    {
        int tick = Find.TickManager.TicksGame + StasisClock.Offset;
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(map.uniqueID, out int seq);
        seqByMap[map.uniqueID] = seq + 1;
        pending.Add(new Pending { map = map.uniqueID, tick = tick, seq = seq, kind = KindPawnToWorld, leftPawn = pawn });
        TestAutomation.Note($"map events: pawn to world {pawn.def.defName} #{pawn.thingIDNumber} from map {map.uniqueID} context queued at tick {tick}.{seq}");
    }

    /// <summary>Идёт Pawn.ExitMap: передачу в мир покрывает событие ухода.</summary>
    internal static int InExitMap;

    private static readonly HashSet<int> taleRemovalsQueued = new HashSet<int>();

    /// <summary>
    /// Удаление сказания (T-056, шаг 4): в режиме заглушки все удаления откладываются в команду мира и
    /// применяются у всех по номеру. Иначе владелец убирает сказание (протухло, пешка ушла), а спящий
    /// оставляет восстановленную копию с null-ссылками — список сказаний расходится (sr7/sr8/sr15).
    /// Решение «убрать» принимается у каждого локально, но исполняется только командой — у всех одинаково.
    /// </summary>
    internal static void QueueTaleRemove(int taleId)
    {
        if (!taleRemovalsQueued.Add(taleId))
        {
            return; // уже ставили (сборщик может звать RemoveTale повторно до применения команды)
        }
        int tick = Find.TickManager.TicksGame + StasisClock.Offset;
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(WorldLevelMap, out int seq);
        seqByMap[WorldLevelMap] = seq + 1;
        pending.Add(new Pending { map = WorldLevelMap, tick = tick, seq = seq, kind = KindTaleRemove, removeTaleId = taleId });
        TestAutomation.Note($"map events: tale remove #{taleId} queued at tick {tick}.{seq}");
    }

    /// <summary>
    /// Мировая пешка вошла на карту (T-056: принятый странник, гость, пленник): у владельца она ушла из мира
    /// (Pawn.SpawnSetup → WorldPawns.RemovePawn), у игрока без этой карты её мировая копия осталась бы (sr32: Nanami).
    /// Событие карты с номером пешки; у владельца — ничего, у остальных — копия убирается из мира.
    /// </summary>
    internal static void QueuePawnEnter(Map map, int thingId)
    {
        int tick = Find.TickManager.TicksGame + StasisClock.Offset;
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(map.uniqueID, out int seq);
        seqByMap[map.uniqueID] = seq + 1;
        pending.Add(new Pending { map = map.uniqueID, tick = tick, seq = seq, kind = KindPawnEnter, discardPawnId = thingId });
        TestAutomation.Note($"map events: pawn enter #{thingId} on map {map.uniqueID} queued at tick {tick}.{seq}");
    }

    private static readonly HashSet<int> pawnDiscardsQueued = new HashSet<int>();

    /// <summary>
    /// Уборка мировой пешки сборщиком (T-056, шаг 5): в режиме заглушки удаления откладываются в событие
    /// мира и применяются у всех по номеру. Иначе сборщик убирает пешек по-разному (несимметричные причины
    /// и разные списки), состав мировых пешек расходится (sr16: 25 против 16 удалений; sr18: мёртвых 4/8).
    /// Модель докладчика: исполняются только команды младшего в сети — список эволюционирует одинаково.
    /// </summary>
    internal static void QueuePawnDiscard(int thingId)
    {
        if (!pawnDiscardsQueued.Add(thingId))
        {
            return;
        }
        int tick = Find.TickManager.TicksGame + StasisClock.Offset;
        if (tick != seqTick)
        {
            seqTick = tick;
            seqByMap.Clear();
        }
        seqByMap.TryGetValue(WorldLevelMap, out int seq);
        seqByMap[WorldLevelMap] = seq + 1;
        pending.Add(new Pending { map = WorldLevelMap, tick = tick, seq = seq, kind = KindPawnDiscard, discardPawnId = thingId });
        TestAutomation.Note($"map events: pawn discard #{thingId} queued at tick {tick}.{seq}");
    }

    /// <summary>
    /// После загрузки (снимок, вход): объекты в очереди — из прежней игры. Данные остаются: неотправленное докладчик
    /// отправит, а при исполнении все восстановят объект из данных (отложенного нет в сейве, его не было в мире).
    /// </summary>
    public static void OnGameLoaded()
    {
        foreach (Pending p in pending)
        {
            if (p.xml == null && p.tale != null)
            {
                p.xml = Scribe.saver.DebugOutputFor(p.tale); // ссылки — по номерам, годятся и для новой игры
            }
            if (p.xml == null && p.deadPawn != null)
            {
                p.xml = Scribe.saver.DebugOutputFor(p.deadPawn);
            }
            if (p.xml == null && p.leftPawn != null)
            {
                p.xml = Scribe.saver.DebugOutputFor(p.leftPawn);
            }
            p.tale = null;
            p.deadPawn = null;
            p.leftPawn = null;
        }
        seqTick = -1;
        seqByMap.Clear();
        lastAppliedAbsent.Clear();
        taleRemovalsQueued.Clear();
        pawnDiscardsQueued.Clear();
    }

    /// <summary>Докладчик карты: владелец колонии в сети, иначе младший игрок в сети.</summary>
    private static int ReporterOf(int mapId)
    {
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        if (map?.Parent is RimWorld.Planet.Settlement st)
        {
            int owner = RWMLWC.Client.World.Colonies.Component?.OwnerOf(st) ?? 0;
            if (owner != 0 && RWMLWC.Client.Maps.PlayerPresence.IsOnline(owner))
            {
                return owner;
            }
        }
        IEnumerable<int> players = RWMLWC.Client.SessionView.SessionUi.Players.Select(p => p.PlayerId).Where(RWMLWC.Client.Maps.PlayerPresence.IsOnline);
        return players.DefaultIfEmpty(LockstepClient.PlayerId).Min();
    }

    /// <summary>Каждый кадр сессии: докладчик отправляет очередь; устаревшее у остальных отбрасывается.</summary>
    public static void Frame()
    {
        if (pending.Count == 0 || Current.Game == null)
        {
            return;
        }
        int now = Find.TickManager.TicksGame + StasisClock.Offset;
        pending.RemoveAll(p => now - p.tick > DropAfterTicks);
        foreach (IGrouping<int, Pending> group in pending.Where(p => !p.sent).GroupBy(p => p.map).ToList())
        {
            if (ReporterOf(group.Key) != LockstepClient.PlayerId)
            {
                continue;
            }
            // XML пешки велик (десятки КБ) — события пешек шлём меньшими пачками, чтобы команда влезла в кадр (4 МБ).
            int take = group.Any(p => p.kind == KindPawnDeath || p.kind == KindPawnLeave || p.kind == KindPawnToWorld) ? 8 : MaxPerCommand;
            List<Pending> items = group.OrderBy(p => p.tick).ThenBy(p => p.seq).Take(take).ToList();
            foreach (Pending p in items)
            {
                p.xml ??= p.tale != null ? Scribe.saver.DebugOutputFor(p.tale)
                    : p.deadPawn != null ? Scribe.saver.DebugOutputFor(p.deadPawn)
                    : p.leftPawn != null ? Scribe.saver.DebugOutputFor(p.leftPawn) : "";
            }
            RWMLWC.Client.Input.WorldCommands.SendMapEvents(w =>
            {
                w.WriteInt32(group.Key);
                w.WriteInt32(items.Count);
                foreach (Pending p in items)
                {
                    w.WriteInt32(p.tick);
                    w.WriteInt32(p.seq);
                    w.WriteByte(p.kind);
                    if (p.kind == KindPawnDeath)
                    {
                        w.WriteBytes(System.Text.Encoding.UTF8.GetBytes(p.xml ?? ""));
                        w.WriteString(p.dinfoDef ?? "");
                        w.WriteInt32(BitConverter.SingleToInt32Bits(p.dinfoAmount));
                        w.WriteInt32(p.dinfoInstigator);
                        w.WriteByte(p.dinfoCategory);
                        w.WriteInt32(p.factionLoadId);
                        w.WriteInt32(p.goodwillDelta);
                        w.WriteBool(p.leaderChanged);
                        string leaderXml = "";
                        if (p.leaderChanged && p.factionLoadId >= 0)
                        {
                            Faction? f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.loadID == p.factionLoadId);
                            if (f?.leader != null)
                            {
                                leaderXml = Scribe.saver.DebugOutputFor(f.leader);
                            }
                        }
                        w.WriteBytes(System.Text.Encoding.UTF8.GetBytes(leaderXml));
                    }
                    else if (p.kind == KindPawnLeave || p.kind == KindPawnToWorld)
                    {
                        w.WriteBytes(System.Text.Encoding.UTF8.GetBytes(p.xml ?? ""));
                        w.WriteBool(p.leaveFreed);
                    }
                    else if (p.kind == KindTaleRemove)
                    {
                        w.WriteInt32(p.removeTaleId);
                    }
                    else if (p.kind == KindPawnDiscard || p.kind == KindPawnEnter)
                    {
                        w.WriteInt32(p.discardPawnId);
                    }
                    else
                    {
                        w.WriteString(p.xml ?? "");
                    }
                }
            });
            foreach (Pending p in items)
            {
                p.sent = true;
            }
            TestAutomation.Note($"map events: sent {items.Count} for map {group.Key}");
        }
    }

    // Ключ последнего применённого события для карт, которых у этого игрока нет (sleepremove, 11.4б):
    // MapSimState у такой карты нет, а событие применить надо — мир общий. В сейве не хранится;
    // от повтора после загрузки защищает проверка номера сказания ниже.
    private static readonly Dictionary<int, (int tick, int seq)> lastAppliedAbsent = new Dictionary<int, (int, int)>();

    /// <summary>Исполнение команды (у всех, в общем порядке).</summary>
    public static void Execute(ref PacketReader r)
    {
        int mapId = r.ReadInt32();
        int count = r.ReadCount(MaxPerCommand, 13);
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        MapSimState? state = map == null ? null : MapSimState.For(map);
        for (int i = 0; i < count; i++)
        {
            int tick = r.ReadInt32();
            int seq = r.ReadInt32();
            byte kind = r.ReadByte();
            string xml;
            string dinfoDef = "";
            float dinfoAmount = 0f;
            int dinfoInstigator = -1;
            byte dinfoCategory = 0;
            bool leaveFreed = false;
            int factionLoadId = -1;
            int goodwillDelta = 0;
            bool leaderChanged = false;
            string leaderXml = "";
            if (kind == KindPawnDeath)
            {
                xml = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
                dinfoDef = r.ReadString();
                dinfoAmount = BitConverter.Int32BitsToSingle(r.ReadInt32());
                dinfoInstigator = r.ReadInt32();
                dinfoCategory = r.ReadByte();
                factionLoadId = r.ReadInt32();
                goodwillDelta = r.ReadInt32();
                leaderChanged = r.ReadBool();
                leaderXml = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
            }
            else if (kind == KindPawnLeave || kind == KindPawnToWorld)
            {
                xml = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
                leaveFreed = r.ReadBool();
            }
            else if (kind == KindTaleRemove || kind == KindPawnDiscard || kind == KindPawnEnter)
            {
                xml = "";
            }
            else
            {
                xml = r.ReadString();
            }
            int removeTaleId = kind == KindTaleRemove ? r.ReadInt32() : -1;
            int discardPawnId = kind == KindPawnDiscard || kind == KindPawnEnter ? r.ReadInt32() : -1;
            int lastTick;
            int lastSeq;
            if (state != null)
            {
                lastTick = state.lastEventTick;
                lastSeq = state.lastEventSeq;
            }
            else
            {
                lastAppliedAbsent.TryGetValue(mapId, out (int tick, int seq) last);
                lastTick = last.tick;
                lastSeq = last.seq;
            }
            if (tick < lastTick || tick == lastTick && seq <= lastSeq)
            {
                continue; // уже применено (второй докладчик)
            }
            if (state != null)
            {
                state.lastEventTick = tick;
                state.lastEventSeq = seq;
            }
            else
            {
                lastAppliedAbsent[mapId] = (tick, seq);
            }
            Pending? mine = pending.FirstOrDefault(p => p.map == mapId && p.tick == tick && p.seq == seq);
            if (mine != null)
            {
                pending.Remove(mine);
            }
            if (kind == KindTale)
            {
                Tale? tale = mine?.tale ?? LoadTale(xml);
                if (tale != null && !Find.TaleManager.AllTalesListForReading.Any(t => t.id == tale.id))
                {
                    applying = true;
                    try
                    {
                        Find.TaleManager.Add(tale);
                    }
                    finally
                    {
                        applying = false;
                    }
                    TestAutomation.Note($"map events: tale #{tale.id} {tale.def.defName} applied at tick {Find.TickManager.TicksGame} ({(mine != null ? "own" : "restored")}, map {mapId}{(map == null ? " absent" : "")})");
                }
            }
            else if (kind == KindPawnDeath)
            {
                ApplyPawnDeath(xml, dinfoDef, dinfoAmount, dinfoInstigator, dinfoCategory, factionLoadId, goodwillDelta, leaderChanged, leaderXml, mapId, tick, seq, own: mine != null);
            }
            else if (kind == KindPawnLeave)
            {
                ApplyPawnLeave(xml, leaveFreed, mapId, tick, seq, own: mine != null);
            }
            else if (kind == KindPawnToWorld)
            {
                ApplyPawnToWorld(xml, mapId, own: mine != null);
            }
            else if (kind == KindTaleRemove)
            {
                // Применяют все, включая постановщика: локальное удаление было отложено префиксом RemoveTale.
                applying = true;
                try
                {
                    int removed = TalesListRef(Find.TaleManager).RemoveAll(t => t.id == removeTaleId);
                    if (removed > 0)
                    {
                        TestAutomation.Note($"map events: tale remove #{removeTaleId} applied at tick {Find.TickManager.TicksGame} ({(mine != null ? "own" : "remote")})");
                    }
                }
                finally
                {
                    applying = false;
                }
            }
            else if (kind == KindPawnEnter)
            {
                Pawn? copy = Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(p => p.thingIDNumber == discardPawnId);
                if (copy != null && !copy.Spawned)
                {
                    Find.WorldPawns.RemovePawn(copy);
                    TestAutomation.Note($"map events: pawn enter #{discardPawnId} applied at tick {Find.TickManager.TicksGame} — world copy removed (map {mapId} absent)");
                }
            }
            else if (kind == KindPawnDiscard)
            {
                // Применяют все, включая постановщика: локальная уборка была отложена префиксом.
                Pawn? pawn = Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(p => p.thingIDNumber == discardPawnId);
                if (pawn != null)
                {
                    applying = true;
                    try
                    {
                        Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
                        TestAutomation.Note($"map events: pawn discard {pawn.def.defName} #{discardPawnId} applied at tick {Find.TickManager.TicksGame} ({(mine != null ? "own" : "remote")})");
                    }
                    finally
                    {
                        applying = false;
                    }
                }
            }
        }
    }

    private static readonly AccessTools.FieldRef<TaleManager, List<Tale>> TalesListRef =
        AccessTools.FieldRefAccess<TaleManager, List<Tale>>("tales");

    /// <summary>
    /// Спящий клиент: восстановить ушедшую пешку из XML в WorldPawns и исполнить мировые уведомления ухода.
    /// Goodwill за уход считается без карты (в отличие от смерти) — синхронизируется полностью.
    /// Владелец (own) уже исполнил уведомления в тике — не повторять.
    /// </summary>
    private static void ApplyPawnToWorld(string xml, int mapId, bool own)
    {
        if (own)
        {
            return;
        }
        Pawn? pawn = LoadPawn(xml);
        if (pawn == null || Find.WorldPawns.AllPawnsAliveOrDead.Any(p => p.thingIDNumber == pawn.thingIDNumber))
        {
            return;
        }
        applying = true;
        try
        {
            Find.WorldPawns.PassToWorld(pawn);
            TestAutomation.Note($"map events: pawn to world {pawn.def.defName} #{pawn.thingIDNumber} applied at tick {Find.TickManager.TicksGame} (restored, map {mapId} absent)");
        }
        catch (Exception ex)
        {
            TestAutomation.Note("map events: pawn to world apply FAILED: " + ex.Message);
            Log.Error("[RWMLWC] событие передачи пешки в мир: " + ex);
        }
        finally
        {
            applying = false;
        }
    }

    private static void ApplyPawnLeave(string xml, bool freed, int mapId, int tick, int seq, bool own)
    {
        if (own)
        {
            return;
        }
        Pawn? pawn = LoadPawn(xml);
        if (pawn == null || Find.WorldPawns.AllPawnsAliveOrDead.Any(p => p.thingIDNumber == pawn.thingIDNumber))
        {
            return;
        }
        applying = true;
        Rand.PushState(Gen.HashCombineInt(mapId, tick, seq, pawn.thingIDNumber));
        try
        {
            Find.WorldPawns.PassToWorld(pawn);
            pawn.Ideo?.Notify_MemberLost(pawn, null);
            pawn.Faction?.Notify_MemberExitedMap(pawn, freed);
            QuestUtility.SendQuestTargetSignals(pawn.questTags, "LeftMap", pawn.Named("SUBJECT"));
            Find.FactionManager.Notify_PawnLeftMap(pawn);
            Find.IdeoManager.Notify_PawnLeftMap(pawn);
            TestAutomation.Note($"map events: pawn leave {pawn.def.defName} #{pawn.thingIDNumber} applied at tick {Find.TickManager.TicksGame} (restored, map {mapId} absent, freed={freed})");
        }
        catch (Exception ex)
        {
            TestAutomation.Note("map events: pawn leave apply FAILED: " + ex.Message);
            Log.Error("[RWMLWC] событие ухода пешки: " + ex);
        }
        finally
        {
            Rand.PopState();
            applying = false;
        }
    }

    /// <summary>
    /// Спящий клиент: восстановить мёртвую пешку из XML в WorldPawns и исполнить мировые последствия смерти.
    /// Владелец (own) уже исполнил их в тике карты — не повторять. Goodwill и новый лидер фракции приходят
    /// данными события (дельта и XML): блок Notify_MemberDied требует карту, которой у спящего нет, а
    /// регенерация лидера под другим Rand дала бы другую пешку (sr20: goodwill 35, задание 1/0 на 164000).
    /// Остальные уведомления идут под изолированным Rand — общий Rand спящего не меняется.
    /// </summary>
    private static void ApplyPawnDeath(string xml, string dinfoDef, float amount, int instigatorId, byte category, int factionLoadId, int goodwillDelta, bool leaderChanged, string leaderXml, int mapId, int tick, int seq, bool own)
    {
        if (own)
        {
            return;
        }
        Pawn? pawn = LoadPawn(xml);
        if (pawn == null || Find.WorldPawns.AllPawnsAliveOrDead.Any(p => p.thingIDNumber == pawn.thingIDNumber))
        {
            return;
        }
        DamageInfo? dinfo = null;
        if (dinfoDef.Length > 0)
        {
            DamageDef? def = DefDatabase<DamageDef>.GetNamedSilentFail(dinfoDef);
            if (def != null)
            {
                Thing? instigator = instigatorId >= 0 ? PawnsFinder.All_AliveOrDead.FirstOrDefault(p => p.thingIDNumber == instigatorId) : null;
                dinfo = new DamageInfo(def, amount, instigator: instigator, category: (DamageInfo.SourceCategory)category);
            }
        }
        applying = true;
        Rand.PushState(Gen.HashCombineInt(mapId, tick, seq, pawn.thingIDNumber));
        try
        {
            Find.WorldPawns.PassToWorld(pawn);
            // Общая адаптация в режиме заглушки из событий карты не меняется (StorytellerPerMap.cs) — у владельца тоже.
            Faction? f = factionLoadId >= 0 ? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.loadID == factionLoadId) : null;
            if (f != null)
            {
                if (goodwillDelta != 0)
                {
                    Faction.OfPlayer.TryAffectGoodwillWith(f, goodwillDelta, canSendMessage: false, canSendHostilityLetter: false);
                }
                if (leaderChanged && leaderXml.Length > 0)
                {
                    Pawn? leader = LoadPawn(leaderXml);
                    if (leader != null)
                    {
                        if (!Find.WorldPawns.AllPawnsAliveOrDead.Any(p => p.thingIDNumber == leader.thingIDNumber))
                        {
                            Find.WorldPawns.PassToWorld(leader, PawnDiscardDecideMode.KeepForever);
                        }
                        f.leader = leader;
                        QuestUtility.SendQuestTargetSignals(pawn.questTags, "NoLongerFactionLeader", pawn.Named("SUBJECT"), leader.Named("NEWFACTIONLEADER"));
                    }
                }
            }
            Find.QuestManager.Notify_PawnKilled(pawn, dinfo);
            Find.FactionManager.Notify_PawnKilled(pawn);
            Find.IdeoManager.Notify_PawnKilled(pawn);
            TestAutomation.Note($"map events: pawn death {pawn.def.defName} #{pawn.thingIDNumber} applied at tick {Find.TickManager.TicksGame} (restored, map {mapId} absent, goodwillDelta={goodwillDelta}, leaderChanged={leaderChanged})");
        }
        catch (Exception ex)
        {
            TestAutomation.Note("map events: pawn death apply FAILED: " + ex.Message);
            Log.Error("[RWMLWC] событие смерти пешки: " + ex);
        }
        finally
        {
            Rand.PopState();
            applying = false;
        }
    }

    private static Pawn? LoadPawn(string xml)
    {
        // Прежняя копия этой пешки у этого игрока (мировая: задание восстановило её в мир, а у владельца она ушла на
        // карту, которой здесь нет) — убрать, иначе два объекта с одним номером (sr32: Nanami).
        int id = -1;
        System.Text.RegularExpressions.Match mt = System.Text.RegularExpressions.Regex.Match(xml, "<id>[^<0-9]*([0-9]+)</id>");
        if (mt.Success)
        {
            int.TryParse(mt.Groups[1].Value, out id);
        }
        Pawn? stale = id < 0 ? null : Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(p => p.thingIDNumber == id);
        if (stale != null && !stale.Spawned)
        {
            Find.WorldPawns.RemovePawn(stale);
            TestAutomation.Note($"map events: stale world copy of pawn {id} removed before restore");
        }
        skipPawnId = id;
        try
        {
            return AsLoading(() => LoadPawnInner(xml));
        }
        finally
        {
            skipPawnId = -1;
        }
    }

    private static int skipPawnId = -1;

    private static Pawn? LoadPawnInner(string xml)
    {
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml("<root>" + xml + "</root>");
            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
            Pawn? pawn = null;
            Scribe_Deep.Look(ref pawn, "saveable");
            LoadedObjectDirectory dir = directoryRef(Scribe.loader.crossRefs);
            RegisterGame(dir, skipPawnId >= 0 ? new[] { skipPawnId } : null, null);
            Scribe.loader.FinalizeLoading();
            return pawn;
        }
        catch (Exception ex)
        {
            Scribe.ForceStop();
            TestAutomation.Note("map events: pawn restore FAILED: " + ex.Message);
            Log.Error("[RWMLWC] событие карты: не удалось восстановить пешку: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Загрузка объектов из данных события посреди игры — в том же состоянии программы, что и обычная загрузка сейва:
    /// часть кода игры при <c>ProgramState.Playing</c> идёт дальше и падает на недогруженной пешке
    /// (LifeStageWorker_HumanlikeAdult.Notify_LifeStageStarted в PostLoadInit, sr31).
    /// </summary>
    internal static T AsLoading<T>(Func<T> load)
    {
        ProgramState prev = Current.ProgramState;
        Current.ProgramState = ProgramState.MapInitializing;
        try
        {
            return load();
        }
        finally
        {
            Current.ProgramState = prev;
        }
    }

    /// <summary>
    /// Всё, на что могут ссылаться пешки, сказания и задания из данных события: пешки, фракции, идеологии с заповедями,
    /// объекты мира, карты, задания, политики колонии (еда, лекарства, одежда, чтение). Без политик и заповедей
    /// восстановленная пешка падала в PostLoadInit (sr27, sr30).
    /// </summary>
    internal static void RegisterGame(LoadedObjectDirectory dir, ICollection<int>? skipPawns, ICollection<int>? skipFactions)
    {
        foreach (Pawn p in PawnsFinder.All_AliveOrDead)
        {
            if (skipPawns == null || !skipPawns.Contains(p.thingIDNumber)) dir.RegisterLoaded(p);
        }
        foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
        {
            if (skipFactions == null || !skipFactions.Contains(f.loadID)) dir.RegisterLoaded(f);
        }
        if (ModsConfig.IdeologyActive)
        {
            foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
            {
                dir.RegisterLoaded(ideo);
                foreach (Precept pr in ideo.PreceptsListForReading) dir.RegisterLoaded(pr);
            }
        }
        foreach (WorldObject w in Find.WorldObjects.AllWorldObjects) dir.RegisterLoaded(w);
        foreach (Map m in Find.Maps) dir.RegisterLoaded(m);
        foreach (Quest q in Find.QuestManager.QuestsListForReading) dir.RegisterLoaded(q);
        Game g = Current.Game;
        foreach (DrugPolicy x in g.drugPolicyDatabase.AllPolicies) dir.RegisterLoaded(x);
        foreach (FoodPolicy x in g.foodRestrictionDatabase.AllFoodRestrictions) dir.RegisterLoaded(x);
        foreach (ApparelPolicy x in g.outfitDatabase.AllOutfits) dir.RegisterLoaded(x);
        foreach (ReadingPolicy x in g.readingPolicyDatabase.AllReadingPolicies) dir.RegisterLoaded(x);
    }

    private static readonly AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> directoryRef =
        AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");

    /// <summary>Сказание из XML (<c>DebugOutputFor</c>); ссылки — на пешек, фракции и идеологии этой игры.</summary>
    private static Tale? LoadTale(string xml) => AsLoading(() => LoadTaleInner(xml));

    private static Tale? LoadTaleInner(string xml)
    {
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml("<root>" + xml + "</root>");
            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
            Tale? tale = null;
            Scribe_Deep.Look(ref tale, "saveable");
            LoadedObjectDirectory dir = directoryRef(Scribe.loader.crossRefs);
            RegisterGame(dir, null, null);
            Scribe.loader.FinalizeLoading();
            return tale;
        }
        catch (Exception ex)
        {
            Scribe.ForceStop();
            TestAutomation.Note("map events: tale restore FAILED: " + ex.Message);
            Log.Error("[RWMLWC] событие карты: не удалось восстановить сказание: " + ex);
            return null;
        }
    }
}

[HarmonyPatch(typeof(TaleManager), nameof(TaleManager.Add))]
internal static class Patch_TaleManager_Add_MapEvent
{
    private static bool Prefix(Tale tale)
    {
        if (!MapEvents.ShouldDefer(out Map? map) || map == null)
        {
            if (PerMapSim.SleepRemoveTest && !MapEvents.Applying && LockstepClient.InSession && tale != null)
            {
                TestAutomation.Note($"tale diag: added #{tale.id} {tale.def?.defName} outside map context (tick {Find.TickManager.TicksGame})");
            }
            return true;
        }
        MapEvents.QueueTale(map, tale);
        return false;
    }
}

/// <summary>
/// Смерть пешки в тике карты (T-056, шаг 4): после исполнения поставить событие карты для спящих клиентов.
/// Только sleepremove: в остальных режимах карту считают все, и мировые последствия наступают у всех сами.
/// </summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
internal static class Patch_Pawn_Kill_MapEvent
{
    // Снимок до смерти: goodwill и лидер фракции. После смерти дельта и новый лидер уходят данными события —
    // у спящего нет карты, и блок Notify_MemberDied (требует карту) у него не исполнился бы.
    private static void Prefix(Pawn __instance, ref (int loadId, int goodwill, int leaderId) __state)
    {
        __state = (-1, 0, -1);
        if (!PerMapSim.SleepRemoveTest || !LockstepClient.InSession || !MapRandContext.InTick || __instance == null)
        {
            return;
        }
        Faction? f = __instance.HomeFaction;
        if (f != null && !f.IsPlayer)
        {
            __state = (f.loadID, f.GoodwillWith(Faction.OfPlayer), f.leader?.thingIDNumber ?? -1);
        }
    }

    private static void Postfix(Pawn __instance, DamageInfo? dinfo, (int loadId, int goodwill, int leaderId) __state)
    {
        if (!PerMapSim.SleepRemoveTest || !LockstepClient.InSession || !MapRandContext.InTick || __instance == null)
        {
            return;
        }
        Map? map = MapRandContext.Current;
        if (map == null)
        {
            return;
        }
        int delta = 0;
        bool leaderChanged = false;
        Faction? f = __state.loadId >= 0 ? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.loadID == __state.loadId) : null;
        if (f != null)
        {
            delta = f.GoodwillWith(Faction.OfPlayer) - __state.goodwill;
            leaderChanged = (f.leader?.thingIDNumber ?? -1) != __state.leaderId;
        }
        MapEvents.QueuePawnDeath(map, __instance, dinfo, __state.loadId, delta, leaderChanged);
    }
}

/// <summary>
/// Уход пешки с карты в тике карты (T-056, шаг 4): после исполнения поставить событие карты для спящих.
/// Только sleepremove. Уход из исполнения команды (караван и т.п.) идёт у всех одинаково — событие не нужно.
/// </summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
internal static class Patch_Pawn_ExitMap_MapEvent
{
    private static void Prefix() => MapEvents.InExitMap++;

    private static void Finalizer() => MapEvents.InExitMap--;

    private static void Postfix(Pawn __instance)
    {
        if (!PerMapSim.SleepRemoveTest || !LockstepClient.InSession || !MapRandContext.InTick || __instance == null)
        {
            return;
        }
        Map? map = MapRandContext.Current;
        if (map == null)
        {
            return;
        }
        MapEvents.QueuePawnLeave(map, __instance, __instance.guest?.Released ?? false);
    }
}

/// <summary>
/// Удаление сказания в режиме заглушки (T-056, шаг 4): не исполнять на месте, а поставить событием мира —
/// применят все по номеру одинаково. Иначе владелец убирает, а спящий оставляет копию с null-ссылками
/// (сказания вне хеша, но их удаление двигало общий Rand через сборщик мировых пешек, sr15).
/// Вне режима — как в игре; диагностика удаления сохраняется.
/// </summary>
[HarmonyPatch(typeof(TaleManager), "RemoveTale")]
internal static class Patch_TaleManager_RemoveTale_Diag
{
    private static bool Prefix(Tale tale)
    {
        if (tale == null)
        {
            return true;
        }
        if (PerMapSim.SleepRemoveTest)
        {
            if (LockstepClient.InSession)
            {
                MapEvents.QueueTaleRemove(tale.id);
                return false; // удаление применится командой у всех (в т.ч. при уборке пешки — тайминг общий)
            }
            string caller = new System.Diagnostics.StackTrace(1).ToString();
            if (caller.Length > 600)
            {
                caller = caller.Substring(0, 600);
            }
            TestAutomation.Note($"tale diag: removing #{tale.id} {tale.def?.defName} (uses={tale.Uses}, pawn={(tale.DominantPawn == null ? "null" : tale.DominantPawn.thingIDNumber.ToString())}) by {caller.Replace("\n", " | ")}");
        }
        return true;
    }
}


[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
internal static class Patch_Pawn_SpawnSetup_EnterEvent
{
    private static void Prefix(Pawn __instance, bool respawningAfterLoad, out bool __state)
    {
        __state = !respawningAfterLoad && PerMapSim.SleepRemoveTest && LockstepClient.InSession && Find.WorldPawns.Contains(__instance);
    }

    private static void Postfix(Pawn __instance, Map map, bool __state)
    {
        if (__state && map != null)
        {
            MapEvents.QueuePawnEnter(map, __instance.thingIDNumber);
        }
    }
}

/// <summary>Передача живой пешки в мир в тике карты, не уходом с карты — событие для игроков без карты (sr34).</summary>
[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.PassToWorld))]
internal static class Patch_WorldPawns_PassToWorld_Event
{
    private static void Prefix(Pawn pawn, out Map? __state)
    {
        __state = null;
        if (PerMapSim.SleepRemoveTest && MapEvents.InExitMap == 0 && pawn != null && !pawn.Dead && !pawn.Spawned
            && MapEvents.ShouldDefer(out Map? map) && !Find.WorldPawns.Contains(pawn))
        {
            __state = map;
        }
    }

    private static void Postfix(Pawn pawn, Map? __state)
    {
        if (__state != null && Find.WorldPawns.Contains(pawn))
        {
            MapEvents.QueuePawnToWorld(__state, pawn);
        }
    }
}
