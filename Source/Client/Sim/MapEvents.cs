using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
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
    private const int DropAfterTicks = 5000;
    private const int MaxPerCommand = 32;

    private sealed class Pending
    {
        public int map;
        public int tick;
        public int seq;
        public byte kind;
        public Tale? tale;
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
            p.tale = null;
        }
        seqTick = -1;
        seqByMap.Clear();
        lastAppliedAbsent.Clear();
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
            List<Pending> items = group.OrderBy(p => p.tick).ThenBy(p => p.seq).Take(MaxPerCommand).ToList();
            foreach (Pending p in items)
            {
                p.xml ??= p.tale != null ? Scribe.saver.DebugOutputFor(p.tale) : "";
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
                    w.WriteString(p.xml ?? "");
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
            string xml = r.ReadString();
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

/// <summary>Диагностика sleepremove (T-056): кто и откуда убирает сказание — список входит в хеш мира.</summary>
[HarmonyPatch(typeof(TaleManager), "RemoveTale")]
internal static class Patch_TaleManager_RemoveTale_Diag
{
    private static void Prefix(Tale tale)
    {
        if (PerMapSim.SleepRemoveTest && tale != null)
        {
            string caller = new System.Diagnostics.StackTrace(1).ToString();
            if (caller.Length > 600)
            {
                caller = caller.Substring(0, 600);
            }
            TestAutomation.Note($"tale diag: removing #{tale.id} {tale.def?.defName} (uses={tale.Uses}, pawn={(tale.DominantPawn == null ? "null" : tale.DominantPawn.thingIDNumber.ToString())}) by {caller.Replace("\n", " | ")}");
        }
    }
}
