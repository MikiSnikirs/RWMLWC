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
        public Pawn? leftPawn; // уход (шаг 4): у владельца уведомления уже исполнены в тике — своё не повторять
        public bool leaveFreed; // освобождённый пленник/раб (goodwill за освобождение)
        public int removeTaleId = -1; // удаление сказания: все применяют по номеру (список сказаний общий)
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
    internal static void QueuePawnDeath(Map map, Pawn pawn, DamageInfo? dinfo)
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
        });
        TestAutomation.Note($"map events: pawn death {pawn.def.defName} #{pawn.thingIDNumber} on map {map.uniqueID} queued at tick {tick}.{seq}");
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
            int take = group.Any(p => p.kind == KindPawnDeath || p.kind == KindPawnLeave) ? 8 : MaxPerCommand;
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
                    }
                    else if (p.kind == KindPawnLeave)
                    {
                        w.WriteBytes(System.Text.Encoding.UTF8.GetBytes(p.xml ?? ""));
                        w.WriteBool(p.leaveFreed);
                    }
                    else if (p.kind == KindTaleRemove)
                    {
                        w.WriteInt32(p.removeTaleId);
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
            if (kind == KindPawnDeath)
            {
                xml = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
                dinfoDef = r.ReadString();
                dinfoAmount = BitConverter.Int32BitsToSingle(r.ReadInt32());
                dinfoInstigator = r.ReadInt32();
                dinfoCategory = r.ReadByte();
            }
            else if (kind == KindPawnLeave)
            {
                xml = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
                leaveFreed = r.ReadBool();
            }
            else if (kind == KindTaleRemove)
            {
                xml = "";
            }
            else
            {
                xml = r.ReadString();
            }
            int removeTaleId = kind == KindTaleRemove ? r.ReadInt32() : -1;
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
                ApplyPawnDeath(xml, dinfoDef, dinfoAmount, dinfoInstigator, dinfoCategory, mapId, tick, seq, own: mine != null);
            }
            else if (kind == KindPawnLeave)
            {
                ApplyPawnLeave(xml, leaveFreed, mapId, tick, seq, own: mine != null);
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
        }
    }

    private static readonly AccessTools.FieldRef<TaleManager, List<Tale>> TalesListRef =
        AccessTools.FieldRefAccess<TaleManager, List<Tale>>("tales");

    /// <summary>
    /// Спящий клиент: восстановить ушедшую пешку из XML в WorldPawns и исполнить мировые уведомления ухода.
    /// Goodwill за уход считается без карты (в отличие от смерти) — синхронизируется полностью.
    /// Владелец (own) уже исполнил уведомления в тике — не повторять.
    /// </summary>
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
    /// Спящий клиент: восстановить мёртвую пешку из XML в WorldPawns и исполнить мировые уведомления смерти.
    /// Владелец (own) уже исполнил их в тике карты — не повторять. Уведомления идут под изолированным Rand:
    /// у владельца они считались в Rand карты, общий Rand спящего не должен измениться.
    /// Известный пробел (записан в 11.4б): goodwill фракции при map=null не меняется (блок Notify_MemberDied
    /// требует карту), регенерация лидера фракции даёт другую пешку — синхронизировать данными в следующей итерации.
    /// </summary>
    private static void ApplyPawnDeath(string xml, string dinfoDef, float amount, int instigatorId, byte category, int mapId, int tick, int seq, bool own)
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
            Find.Storyteller.Notify_PawnEvent(pawn, AdaptationEvent.Died);
            pawn.HomeFaction?.Notify_MemberDied(pawn, dinfo, wasWorldPawn: false, wasGuilty: false, map: null);
            Find.QuestManager.Notify_PawnKilled(pawn, dinfo);
            Find.FactionManager.Notify_PawnKilled(pawn);
            Find.IdeoManager.Notify_PawnKilled(pawn);
            TestAutomation.Note($"map events: pawn death {pawn.def.defName} #{pawn.thingIDNumber} applied at tick {Find.TickManager.TicksGame} (restored, map {mapId} absent)");
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
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml("<root>" + xml + "</root>");
            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
            Pawn? pawn = null;
            Scribe_Deep.Look(ref pawn, "saveable");
            LoadedObjectDirectory dir = directoryRef(Scribe.loader.crossRefs);
            foreach (Pawn p in PawnsFinder.All_AliveOrDead)
            {
                dir.RegisterLoaded(p);
            }
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
            {
                dir.RegisterLoaded(f);
            }
            if (ModsConfig.IdeologyActive)
            {
                foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
                {
                    dir.RegisterLoaded(ideo);
                }
            }
            foreach (WorldObject w in Find.WorldObjects.AllWorldObjects)
            {
                dir.RegisterLoaded(w);
            }
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

    private static readonly AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> directoryRef =
        AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");

    /// <summary>Сказание из XML (<c>DebugOutputFor</c>); ссылки — на пешек, фракции и идеологии этой игры.</summary>
    private static Tale? LoadTale(string xml)
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
            foreach (Pawn p in PawnsFinder.All_AliveOrDead)
            {
                dir.RegisterLoaded(p);
            }
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
            {
                dir.RegisterLoaded(f);
            }
            if (ModsConfig.IdeologyActive)
            {
                foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
                {
                    dir.RegisterLoaded(ideo);
                }
            }
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
    private static void Postfix(Pawn __instance, DamageInfo? dinfo)
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
        MapEvents.QueuePawnDeath(map, __instance, dinfo);
    }
}

/// <summary>
/// Уход пешки с карты в тике карты (T-056, шаг 4): после исполнения поставить событие карты для спящих.
/// Только sleepremove. Уход из исполнения команды (караван и т.п.) идёт у всех одинаково — событие не нужно.
/// </summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
internal static class Patch_Pawn_ExitMap_MapEvent
{
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
        if (PerMapSim.SleepRemoveTest && !MapEvents.Applying)
        {
            if (LockstepClient.InSession)
            {
                MapEvents.QueueTaleRemove(tale.id);
                return false; // удаление применится командой у всех
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
