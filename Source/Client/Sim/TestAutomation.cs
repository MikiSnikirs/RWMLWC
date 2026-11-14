using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Автоматизация тестовых прогонов (T-008): чтобы прогон не требовал действий человека в игре.
///
/// Управляется файлом-меткой HashRecorder.ArmFilePath, строки вида ключ=значение:
///   armed=0            — метка выключена (то же, что отсутствие файла);
///   save=Имя           — сразу после запуска игры загрузить этот сейв;
///   scenario=none|raid|twomaps|twomaps-freeze — что сделать в первом тике прогона;
///   scenarios=a,b,c    — очередь сценариев: прогоны идут подряд в одном запуске игры, сейв
///                        перезагружается между ними; пройденное число хранится в automation-progress.txt,
///                        когда очередь пройдена, метка считается выключенной;
///   ticks=10000        — длительность прогона;
///   trace=0|1          — писать ли трассировку Rand (большой файл);
///   densefrom=0, denseto=300 — окно (в тиках от старта), где хеш пишется каждый тик и идёт трассировка;
///   perprocess=1       — между прогонами очереди перезапускать игру, а не перезагружать сейв;
///   snapat=2000        — тик от старта, на котором сценарии snap-save и snap-reload снимают снимок игры;
///   quit=1             — после прогона закрыть игру;
///   server=адрес:порт, thumbprint=отпечаток, name=имя — после загрузки сейва подключиться к серверу
///                        сессии; тики идут под управлением сервера, прогон только пишет хеши.
/// Файл без строк ключ=значение означает прежнее поведение: автостарт при загрузке, без сценария.
/// </summary>
public static class TestAutomation
{
    public sealed class Config
    {
        public bool armed;
        public string? save;
        public string scenario = "none";
        public List<string>? queue;
        public int queueIndex;
        public int ticks = HashRecorder.AutoRunTicks;
        public bool trace = true;
        public int denseFrom;
        public int denseTo = HashRecorder.DenseTicks;
        public bool perProcess;
        public int snapAt = 2000;
        public string? serverHost;
        public int serverPort;
        public string thumbprint = "";
        public string playerName = "player";
        public int holdSec;
        public bool issuer;
        public bool snapSender;
        public bool desync;
        public int connectDelaySec;
        public bool quit;
    }

    private static Config? activeConfig;
    private static string? pendingScenario;
    private static bool quitWhenDone;
    private static bool automationActive;
    // Шаги сценария, которые действуют как игрок: (тик, действие), по возрастанию тика.
    private static readonly Queue<(int tick, string name, Action act)> sessionSteps = new Queue<(int, string, Action)>();
    private static byte[]? snapshotToApply;
    private static bool snapshotDone;
    private static bool reloadPending;
    private static string? reloadSave;

    public static string ProgressPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "automation-progress.txt");

    private static int ReadProgress()
    {
        try
        {
            string path = ProgressPath;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int value) && value >= 0)
            {
                return value;
            }
        }
        catch (Exception)
        {
        }
        return 0;
    }

    private static void WriteProgress(int value)
    {
        try
        {
            File.WriteAllText(ProgressPath, value.ToString());
        }
        catch (Exception ex)
        {
            Note("cannot write progress: " + ex.Message);
        }
    }

    /// <summary>Идёт автоматический прогон: игра сама снимает паузу и сама закроется.</summary>
    public static bool AutomationActive => automationActive;

    public static string LogPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "automation-log.txt");

    public static Config Read()
    {
        Config cfg = new Config();
        string path = HashRecorder.ArmFilePath;
        if (!File.Exists(path))
        {
            return cfg;
        }
        cfg.armed = true;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "armed":
                    cfg.armed = value != "0";
                    break;
                case "save":
                    cfg.save = value.Length > 0 ? value : null;
                    break;
                case "scenario":
                    cfg.scenario = value.Length > 0 ? value.ToLowerInvariant() : "none";
                    break;
                case "scenarios":
                    cfg.queue = new List<string>();
                    foreach (string part in value.Split(','))
                    {
                        string name = part.Trim().ToLowerInvariant();
                        if (name.Length > 0)
                        {
                            cfg.queue.Add(name);
                        }
                    }
                    break;
                case "ticks":
                    if (int.TryParse(value, out int ticks) && ticks > 0)
                    {
                        cfg.ticks = ticks;
                    }
                    break;
                case "densefrom":
                    if (int.TryParse(value, out int from) && from >= 0)
                    {
                        cfg.denseFrom = from;
                    }
                    break;
                case "denseto":
                    if (int.TryParse(value, out int to) && to >= 0)
                    {
                        cfg.denseTo = to;
                    }
                    break;
                case "server":
                    {
                        int colon = value.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(value.Substring(colon + 1), out int port))
                        {
                            cfg.serverHost = value.Substring(0, colon);
                            cfg.serverPort = port;
                        }
                    }
                    break;
                case "thumbprint":
                    cfg.thumbprint = value;
                    break;
                case "desync":
                    cfg.desync = value == "1";
                    break;
                case "snapsender":
                    cfg.snapSender = value == "1";
                    break;
                case "connectdelay":
                    if (int.TryParse(value, out int connectDelay) && connectDelay >= 0)
                    {
                        cfg.connectDelaySec = connectDelay;
                    }
                    break;
                case "issuer":
                    cfg.issuer = value == "1";
                    break;
                case "holdsec":
                    if (int.TryParse(value, out int holdSec) && holdSec >= 0)
                    {
                        cfg.holdSec = holdSec;
                    }
                    break;
                case "name":
                    if (value == "auto")
                    {
                        // Два экземпляра игры на одной машине читают один файл-метку: имя по номеру процесса.
                        cfg.playerName = "p" + Environment.ProcessId;
                    }
                    else if (value.Length > 0)
                    {
                        cfg.playerName = value;
                    }
                    break;
                case "snapat":
                    if (int.TryParse(value, out int snap) && snap > 0)
                    {
                        cfg.snapAt = snap;
                    }
                    break;
                case "perprocess":
                    cfg.perProcess = value == "1";
                    break;
                case "trace":
                    cfg.trace = value != "0";
                    break;
                case "quit":
                    cfg.quit = value == "1";
                    break;
            }
        }
        if (cfg.queue != null)
        {
            cfg.queueIndex = ReadProgress();
            if (cfg.queueIndex >= cfg.queue.Count)
            {
                cfg.armed = false; // очередь пройдена
            }
            else
            {
                cfg.scenario = cfg.queue[cfg.queueIndex];
            }
        }
        return cfg;
    }

    /// <summary>Строка в журнал автоматизации: агент читает его вместо лога игры.</summary>
    public static void Note(string text)
    {
        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + "\n");
        }
        catch (Exception)
        {
            // Журнал вспомогательный; его сбой не должен ломать игру.
        }
    }

    public static void OnRunStarted(Config cfg, int startTick, string? hashFile)
    {
        pendingScenario = cfg.scenario == "none" ? null : cfg.scenario;
        quitWhenDone = cfg.quit;
        automationActive = cfg.quit;
        if (cfg.quit)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }
        activeConfig = cfg;
        sessionSteps.Clear();
        if (cfg.issuer && cfg.scenario == "tabs")
        {
            sessionSteps.Enqueue((startTick + 300, "tabs: work priority", TabsWorkPriority));
            sessionSteps.Enqueue((startTick + 400, "tabs: schedule", TabsSchedule));
            sessionSteps.Enqueue((startTick + 500, "tabs: outfit policy", TabsOutfit));
            sessionSteps.Enqueue((startTick + 600, "tabs: allowed area", TabsArea));
        }
        if (cfg.desync && cfg.scenario == "desync")
        {
            // T-034: этот клиент нарочно портит своё состояние.
            sessionSteps.Enqueue((startTick + 600, "desync: break map rand", DesyncBreak));
        }
        if (cfg.scenario == "ui")
        {
            // T-024: интерфейс во время прогона не должен менять симуляцию. Без сессии, один процесс.
            sessionSteps.Enqueue((startTick + 200, "ui: select pawn", UiSelectPawn));
            sessionSteps.Enqueue((startTick + 500, "ui: tab Architect", () => UiTab("Architect")));
            sessionSteps.Enqueue((startTick + 800, "ui: tab Work", () => UiTab("Work")));
            sessionSteps.Enqueue((startTick + 1100, "ui: tab Research", () => UiTab("Research")));
            sessionSteps.Enqueue((startTick + 1400, "ui: tab Quests", () => UiTab("Quests")));
            sessionSteps.Enqueue((startTick + 1700, "ui: tab Factions", () => UiTab("Factions")));
            sessionSteps.Enqueue((startTick + 2000, "ui: info card", UiInfoCard));
            sessionSteps.Enqueue((startTick + 2300, "ui: select item", UiSelectItem));
            sessionSteps.Enqueue((startTick + 2600, "ui: close all", UiCloseAll));
        }
        if (cfg.issuer && cfg.scenario == "rightclick")
        {
            sessionSteps.Enqueue((startTick + 300, "rightclick: draft", GizmoDraft));
            sessionSteps.Enqueue((startTick + 400, "rightclick: move", RightClickMove));
            sessionSteps.Enqueue((startTick + 600, "rightclick: undraft", GizmoDraft));
            sessionSteps.Enqueue((startTick + 700, "rightclick: item", RightClickItem));
            sessionSteps.Enqueue((startTick + 800, "rightclick: menu option", GizmoMenuOption));
        }
        if (cfg.issuer && cfg.scenario == "designate")
        {
            sessionSteps.Enqueue((startTick + 300, "designate", ScenarioDesignate));
        }
        if (cfg.issuer && cfg.scenario == "gizmo")
        {
            sessionSteps.Enqueue((startTick + 300, "gizmo: draft", GizmoDraft));
            sessionSteps.Enqueue((startTick + 400, "gizmo: forbid", GizmoForbid));
            sessionSteps.Enqueue((startTick + 500, "gizmo: attack", GizmoAttack));
            sessionSteps.Enqueue((startTick + 600, "gizmo: plant menu", GizmoPlantMenu));
            sessionSteps.Enqueue((startTick + 700, "gizmo: menu option", GizmoMenuOption));
        }
        snapshotDone = false;
        snapshotToApply = null;
        Note($"run started: #{cfg.queueIndex}, tick={startTick}, scenario={cfg.scenario}, ticks={cfg.ticks}, trace={cfg.trace}, quit={cfg.quit}, file={Path.GetFileName(hashFile)}");
    }

    /// <summary>Сейв загружен при включённой метке. Если задан сервер — подключаемся до первого тика.</summary>
    public static void OnGameLoaded(Config cfg)
    {
        if (cfg.quit)
        {
            // Автоматический прогон: снимать паузу с самого начала (в сессии это просьба серверу).
            automationActive = true;
        }
        if (cfg.serverHost != null && LockstepClient.CurrentState == LockstepClient.State.Off)
        {
            LockstepClient.HoldSeconds = cfg.holdSec;
            LockstepClient.ForceSnapshotSender = cfg.snapSender;
            LockstepClient.Connect(cfg.serverHost, cfg.serverPort, cfg.thumbprint, cfg.playerName);
        }
    }

    /// <summary>Вызывается в первом тике прогона, уже в контексте симуляции (после MapRandContext.BeginTick).</summary>
    public static void ApplyScenarioIfPending()
    {
        string? scenario = pendingScenario;
        if (scenario == null)
        {
            return;
        }
        pendingScenario = null;
        try
        {
            switch (scenario)
            {
                case "raid":
                    ScenarioRaid();
                    break;
                case "twomaps":
                    ScenarioSecondMap(false);
                    break;
                case "twomaps-freeze":
                    ScenarioSecondMap(true);
                    break;
                case "ui":
                case "tabs":
                case "desync":
                case "designate":
                case "gizmo":
                case "rightclick":
                    break; // срабатывают позже, из кадра (SessionFrame): приказы идут как ввод игрока
                case "snap-save":
                case "snap-reload":
                    break; // срабатывают позже, на тике snapat (BeforeTickDuringRun)
                default:
                    Note("unknown scenario: " + scenario);
                    break;
            }
        }
        catch (Exception ex)
        {
            Note("scenario '" + scenario + "' FAILED: " + ex);
            Log.Error("[RWMLWC] сценарий " + scenario + " упал: " + ex);
        }
    }

    /// <summary>Раз в кадр в сессии, между тиками: сценарии, которые действуют как игрок.</summary>
    public static void SessionFrame()
    {
        if (sessionSteps.Count == 0 || Current.Game == null || Find.TickManager.TicksGame < sessionSteps.Peek().tick)
        {
            return;
        }
        (int _, string name, Action act) = sessionSteps.Dequeue();
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Note("step '" + name + "' FAILED: " + ex);
            Log.Error("[RWMLWC] шаг сценария " + name + " упал: " + ex);
        }
    }

    // --- сценарий ui (T-024): окна и выделение во время прогона ---

    private static void UiSelectPawn()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
        if (pawn != null)
        {
            SelectOnly(pawn);
        }
        Note("ui: selected " + (pawn != null ? pawn.LabelShort : "nobody"));
    }

    private static void UiTab(string defName)
    {
        MainButtonDef? tab = DefDatabase<MainButtonDef>.GetNamedSilentFail(defName);
        if (tab == null)
        {
            Note("ui: no tab " + defName);
            return;
        }
        Find.MainTabsRoot.SetCurrentTab(tab, false);
        Note("ui: opened tab " + defName);
    }

    private static void UiInfoCard()
    {
        Find.MainTabsRoot.EscapeCurrentTab(false);
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
        if (pawn != null)
        {
            Find.WindowStack.Add(new Dialog_InfoCard(pawn));
        }
        Note("ui: info card " + (pawn != null ? pawn.LabelShort : "nobody"));
    }

    private static void UiSelectItem()
    {
        Find.WindowStack.TryRemove(typeof(Dialog_InfoCard), false);
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Thing? item = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
            .Where(t => t.Spawned && !t.Fogged()).OrderBy(t => t.thingIDNumber).FirstOrDefault();
        if (item != null)
        {
            SelectOnly(item);
        }
        Note("ui: selected item " + (item != null ? item.LabelShort : "none"));
    }

    private static void UiCloseAll()
    {
        Find.MainTabsRoot.EscapeCurrentTab(false);
        Find.Selector.ClearSelection();
        Note("ui: closed");
    }

    // --- сценарий tabs (T-023): те же вызовы, что делают вкладки «Работа», «Расписание», «Назначения» ---

    private static Pawn? FirstColonist()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        return map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
    }

    private static void TabsWorkPriority()
    {
        Pawn? pawn = FirstColonist();
        if (pawn == null || pawn.workSettings == null || !pawn.workSettings.EverWork)
        {
            Note("tabs: work: no colonist");
            return;
        }
        WorkTypeDef? work = DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderBy(w => w.defName).FirstOrDefault(w => !pawn.WorkTypeIsDisabled(w));
        if (work == null)
        {
            Note("tabs: work: no work type");
            return;
        }
        int before = pawn.workSettings.GetPriority(work);
        int after = before == 0 ? 3 : 0;
        int sentBefore = LockstepClient.CommandsSent;
        pawn.workSettings.SetPriority(work, after);
        Note($"tabs: work {work.defName} of {pawn.LabelShort}: {before} -> {after}, sent={LockstepClient.CommandsSent - sentBefore}, value now={pawn.workSettings.GetPriority(work)}");
    }

    private static void TabsSchedule()
    {
        Pawn? pawn = FirstColonist();
        if (pawn == null || pawn.timetable == null)
        {
            Note("tabs: schedule: no colonist");
            return;
        }
        TimeAssignmentDef before = pawn.timetable.GetAssignment(5);
        TimeAssignmentDef after = before == TimeAssignmentDefOf.Work ? TimeAssignmentDefOf.Joy : TimeAssignmentDefOf.Work;
        int sentBefore = LockstepClient.CommandsSent;
        pawn.timetable.SetAssignment(5, after);
        Note($"tabs: schedule hour 5 of {pawn.LabelShort}: {before.defName} -> {after.defName}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static void TabsOutfit()
    {
        Pawn? pawn = FirstColonist();
        if (pawn == null || pawn.outfits == null)
        {
            Note("tabs: outfit: no colonist");
            return;
        }
        ApparelPolicy? other = Current.Game.outfitDatabase.AllOutfits.OrderBy(o => o.id).FirstOrDefault(o => o != pawn.outfits.CurrentApparelPolicy);
        if (other == null)
        {
            Note("tabs: outfit: only one policy");
            return;
        }
        int sentBefore = LockstepClient.CommandsSent;
        pawn.outfits.CurrentApparelPolicy = other;
        Note($"tabs: outfit of {pawn.LabelShort} -> policy {other.id}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static void TabsArea()
    {
        Pawn? pawn = FirstColonist();
        if (pawn == null || pawn.playerSettings == null)
        {
            Note("tabs: area: no colonist");
            return;
        }
        Area? area = pawn.Map.areaManager.AllAreas.Where(a => a is Area_Allowed).OrderBy(a => a.ID).FirstOrDefault();
        Area? target = pawn.playerSettings.AreaRestrictionInPawnCurrentMap == area ? null : area;
        int sentBefore = LockstepClient.CommandsSent;
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = target;
        Note($"tabs: allowed area of {pawn.LabelShort} -> {(target != null ? target.ID.ToString() : "none")}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static string SettingsDigest(Map map)
    {
        Fnv64 hash = Fnv64.Create();
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber))
        {
            hash.Add(pawn.thingIDNumber);
            if (pawn.workSettings != null && pawn.workSettings.EverWork)
            {
                foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderBy(w => w.defName))
                {
                    hash.Add(pawn.workSettings.GetPriority(work));
                }
            }
            if (pawn.timetable != null)
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    hash.Add((int)pawn.timetable.GetAssignment(hour).shortHash);
                }
            }
            hash.Add(pawn.outfits?.CurrentApparelPolicy?.id ?? -1);
            hash.Add(pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.ID ?? -1);
        }
        return hash.Value.ToString("X16");
    }

    // --- сценарий desync (T-034): искусственное расхождение ---

    private static void DesyncBreak()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        // Одно лишнее число из Rand карты: со следующего хеша поле rand у этого клиента другое.
        MapRandContext.BeginTick();
        try
        {
            MapRandContext.Enter(map);
            float wasted = Rand.Value;
            Note($"desync: took one extra Rand value on map {map.uniqueID} at tick {Find.TickManager.TicksGame} ({wasted})");
        }
        finally
        {
            MapRandContext.EndTick();
        }
    }

    // --- сценарий gizmo (T-021): нажатия кнопок выделенных объектов, приказ атаки, меню ---

    private static Pawn? gizmoPawn;

    private static void SelectOnly(object obj)
    {
        Find.Selector.ClearSelection();
        Find.Selector.Select(obj, false, false);
    }

    private static void GizmoDraft()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        gizmoPawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
        if (gizmoPawn == null)
        {
            Note("gizmo: draft: no colonist");
            return;
        }
        SelectOnly(gizmoPawn);
        bool sent = GizmoCommands.SendClick(map, gizmoPawn, g => g is Command_Toggle toggle && toggle.hotKey == KeyBindingDefOf.Command_ColonistDraft);
        Note($"gizmo: draft {gizmoPawn.LabelShort} (id {gizmoPawn.thingIDNumber}): sent={sent}");
    }

    private static void GizmoForbid()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Thing? item = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
            .Where(t => t.Spawned && !t.Fogged() && t.TryGetComp<CompForbiddable>() != null)
            .OrderBy(t => t.thingIDNumber).FirstOrDefault();
        if (item == null)
        {
            Note("gizmo: forbid: no item");
            return;
        }
        SelectOnly(item);
        bool sent = GizmoCommands.SendClick(map, item, g => g is Command_Toggle toggle && toggle.hotKey == KeyBindingDefOf.Command_ItemForbid);
        Note($"gizmo: forbid {item.LabelShort} (id {item.thingIDNumber}, forbidden before={item.IsForbidden(Faction.OfPlayer)}): sent={sent}");
    }

    private static void GizmoAttack()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = gizmoPawn;
        if (pawn == null || !pawn.Spawned)
        {
            Note("gizmo: attack: no pawn");
            return;
        }
        Pawn? prey = map.mapPawns.AllPawnsSpawned
            .Where(p => p.RaceProps.Animal && p.Faction == null && !p.Fogged())
            .OrderBy(p => p.Position.DistanceToSquared(pawn.Position)).ThenBy(p => p.thingIDNumber).FirstOrDefault();
        Verb? verb = pawn.TryGetAttackVerb(prey);
        if (prey == null || verb == null)
        {
            Note("gizmo: attack: no target or verb");
            return;
        }
        SelectOnly(pawn);
        int before = LockstepClient.CommandsSent;
        // Тот же вызов, что делает прицел после щелчка по цели; в сессии он перехватывается.
        verb.OrderForceTarget(new LocalTargetInfo(prey));
        Note($"gizmo: attack {prey.LabelShort} (id {prey.thingIDNumber}) by {pawn.LabelShort}, drafted={pawn.Drafted}: sent={LockstepClient.CommandsSent - before}");
    }

    private static void GizmoPlantMenu()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Zone? zone = map.zoneManager.AllZones.Where(z => z is Zone_Growing).OrderBy(z => z.ID).FirstOrDefault();
        if (zone == null)
        {
            Note("gizmo: plant menu: no growing zone in this save");
            return;
        }
        SelectOnly(zone);
        bool sent = GizmoCommands.SendClick(map, zone, g => g.GetType().Name.Contains("SetPlantToGrow"));
        Note($"gizmo: plant menu of zone {zone.ID} ({((Zone_Growing)zone).GetPlantDefToGrow()?.defName}): sent={sent}");
    }

    // --- сценарий rightclick (T-022): правый щелчок по карте ---

    private static void RightClickMove()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = gizmoPawn;
        if (pawn == null || !pawn.Spawned)
        {
            Note("rightclick: move: no pawn");
            return;
        }
        SelectOnly(pawn);
        IntVec3 cell = CellFinder.StandableCellNear(pawn.Position + new IntVec3(4, 0, 4), map, 5f);
        if (!cell.IsValid)
        {
            Note("rightclick: move: no standable cell");
            return;
        }
        MapClickCommands.Send(map, cell.ToVector3Shifted());
        Note($"rightclick: move {pawn.LabelShort} from {pawn.Position} to {cell}, drafted={pawn.Drafted}: sent");
    }

    private static void RightClickItem()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = gizmoPawn;
        if (pawn == null || !pawn.Spawned)
        {
            Note("rightclick: item: no pawn");
            return;
        }
        Thing? item = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
            .Where(t => t.Spawned && !t.Fogged() && !t.IsForbidden(Faction.OfPlayer))
            .OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).ThenBy(t => t.thingIDNumber).FirstOrDefault();
        if (item == null)
        {
            Note("rightclick: item: no item");
            return;
        }
        SelectOnly(pawn);
        MapClickCommands.Send(map, item.Position.ToVector3Shifted());
        Note($"rightclick: item {item.LabelShort} (id {item.thingIDNumber}) at {item.Position} for {pawn.LabelShort}, drafted={pawn.Drafted}: sent");
    }

    private static void GizmoMenuOption()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        bool sent = VirtualUi.TestChooseFirstOption(map);
        Note($"gizmo: first menu option: sent={sent}");
    }

    // Те же вызовы, что делает интерфейс: в сессии они перехватываются и уходят командами (T-020).
    private static void ScenarioDesignate()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        IntVec3 center = map.Center;

        Designator_PlantsCut cut = new Designator_PlantsCut();
        List<IntVec3> cutCells = CellRect.CenteredOn(center, 15).ClipInsideMap(map).Cells.ToList();
        cut.DesignateMultiCell(cutCells);

        Designator_Build wall = new Designator_Build(ThingDefOf.Wall);
        wall.SetStuffDef(ThingDefOf.WoodLog);
        List<IntVec3> wallCells = new List<IntVec3>();
        for (int i = -5; i < 5; i++)
        {
            IntVec3 cell = center + new IntVec3(i, 0, 20);
            if (cell.InBounds(map))
            {
                wallCells.Add(cell);
            }
        }
        wall.DesignateMultiCell(wallCells);
        if (wallCells.Count > 0)
        {
            wall.DesignateSingleCell(wallCells[0] + new IntVec3(0, 0, 2));
        }

        Designator_ZoneAddStockpile_Resources stockpile = new Designator_ZoneAddStockpile_Resources();
        List<IntVec3> zoneCells = CellRect.CenteredOn(center + new IntVec3(25, 0, 0), 3).ClipInsideMap(map).Cells
            .Where(c => stockpile.CanDesignateCell(c).Accepted).ToList();
        stockpile.DesignateMultiCell(zoneCells);

        Designator_Hunt hunt = new Designator_Hunt();
        Pawn? prey = map.mapPawns.AllPawnsSpawned.Where(p => hunt.CanDesignateThing(p).Accepted).OrderBy(p => p.thingIDNumber).FirstOrDefault();
        if (prey != null)
        {
            hunt.DesignateThing(prey);
        }

        Note($"designate: tick={Find.TickManager.TicksGame}, cut={cutCells.Count} cells, wall={wallCells.Count}+1, zone={zoneCells.Count}, hunt={(prey != null ? prey.LabelShort : "none")}; captured={DesignatorCommands.Captured}, sent={LockstepClient.CommandsSent}");
    }

    private static void NoteMapSummary()
    {
        if (Current.Game == null)
        {
            return;
        }
        foreach (Map map in Find.Maps)
        {
            string plants = string.Join(",", map.zoneManager.AllZones.Where(z => z is Zone_Growing).OrderBy(z => z.ID)
                .Select(z => ((Zone_Growing)z).GetPlantDefToGrow()?.defName ?? "-"));
            Note($"summary: map{map.uniqueID} drafted={map.mapPawns.FreeColonistsSpawned.Count(p => p.Drafted)} forbidden={map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Count(t => t.IsForbidden(Faction.OfPlayer))} plants=[{plants}]");
            string jobs = string.Join(",", map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                .Select(p => p.thingIDNumber + ":" + (p.CurJobDef != null ? p.CurJobDef.defName : "-") + "@" + p.Position));
            Note($"summary: map{map.uniqueID} jobs=[{jobs}]");
            Note($"summary: map{map.uniqueID} settings={SettingsDigest(map)}");
            Note($"summary: map{map.uniqueID} designations={map.designationManager.AllDesignations.Count} blueprints={map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count} zones={map.zoneManager.AllZones.Count}; commands sent={LockstepClient.CommandsSent} executed={CommandBus.Executed} failed={CommandBus.Failed}");
        }
    }

    public static void OnRunFinished(string? hashFile)
    {
        NoteMapSummary();
        Note("run finished: file=" + Path.GetFileName(hashFile));
        Config? cfg = activeConfig;
        activeConfig = null;
        if (cfg != null && cfg.queue != null)
        {
            int next = cfg.queueIndex + 1;
            WriteProgress(next);
            if (next < cfg.queue.Count && cfg.perProcess)
            {
                // Следующий прогон — в новом процессе игры: штатный перезапуск, как после смены модов.
                Note("queue: restarting game for run #" + next + " (" + cfg.queue[next] + ")");
                automationActive = false;
                quitWhenDone = false;
                GenCommandLine.Restart();
                return;
            }
            if (next < cfg.queue.Count && cfg.save != null)
            {
                // Следующий прогон очереди: перезагрузить сейв. Делается из кадра, не из тика.
                PerMapSim.SetEnabled(false);
                reloadSave = cfg.save;
                reloadPending = true;
                Note("queue: reloading save for run #" + next + " (" + cfg.queue[next] + ")");
                return;
            }
            Note("queue: finished");
        }
        automationActive = false;
        if (quitWhenDone)
        {
            quitWhenDone = false;
            LockstepClient.Disconnect("прогон завершён");
            Note("quitting game");
            Root.Shutdown();
        }
    }

    /// <summary>Вызывается раз в кадр во время автоматического прогона: снять автопаузу (например, от письма о рейде).</summary>
    /// <summary>
    /// Вызывается перед каждым тиком идущего прогона, вне тика (Rand симуляции убран).
    /// Сценарии снимка: snap-save — снять снимок и идти дальше; snap-reload — снять снимок и
    /// загрузить его же. Оба прогона должны совпасть с обычным прогоном без снимка.
    /// </summary>
    public static void BeforeTickDuringRun(HashRecorder recorder, int elapsedTicks)
    {
        Config? cfg = activeConfig;
        if (cfg == null || snapshotDone || elapsedTicks != cfg.snapAt)
        {
            return;
        }
        bool reload = cfg.scenario == "snap-reload";
        if (!reload && cfg.scenario != "snap-save")
        {
            return;
        }
        snapshotDone = true;
        try
        {
            byte[] data = Maps.GameSnapshot.Take(out Maps.GameSnapshot.Stats stats);
            Note($"snapshot taken at tick {Find.TickManager.TicksGame}: {stats}");
            if (reload)
            {
                // Загрузка уничтожает текущую игру, поэтому не отсюда (мы внутри цикла тиков),
                // а из ближайшего кадра. Тики, которые успеют пройти до этого, отбрасываются вместе с игрой.
                recorder.SuspendForSnapshotReload();
                snapshotToApply = data;
            }
        }
        catch (Exception ex)
        {
            Note("snapshot FAILED: " + ex);
            Log.Error("[RWMLWC] снимок не удался: " + ex);
        }
    }

    // --- вход без мира (T-045): подключение из главного меню ---

    private static Config? entryConnect;
    private static readonly System.Diagnostics.Stopwatch entryClock = new System.Diagnostics.Stopwatch();

    /// <summary>В метке задан сервер, но не задан сейв: подключиться из главного меню через connectdelay секунд.</summary>
    public static void ScheduleEntryConnect(Config cfg)
    {
        entryConnect = cfg;
        entryClock.Restart();
        Note($"entry: will connect to {cfg.serverHost}:{cfg.serverPort} in {cfg.connectDelaySec} s without a loaded game");
    }

    /// <summary>Раз в кадр в главном меню.</summary>
    public static void EntryFrame()
    {
        Config? cfg = entryConnect;
        if (cfg == null || cfg.serverHost == null || entryClock.ElapsedMilliseconds < cfg.connectDelaySec * 1000L)
        {
            return;
        }
        entryConnect = null;
        if (cfg.quit)
        {
            automationActive = true;
        }
        LockstepClient.Connect(cfg.serverHost, cfg.serverPort, cfg.thumbprint, cfg.playerName);
    }

    public static void KeepRunning()
    {
        if (!LockstepClient.OwnsTicking)
        {
            SessionFrame(); // шаги сценариев без сессии (ui)
        }
        if (snapshotToApply != null)
        {
            byte[] data = snapshotToApply;
            snapshotToApply = null;
            try
            {
                Note("applying snapshot (" + data.Length + " bytes)");
                Maps.GameSnapshot.Apply(data);
            }
            catch (Exception ex)
            {
                Note("snapshot apply FAILED: " + ex);
            }
            return;
        }
        if (reloadPending)
        {
            reloadPending = false;
            string? save = reloadSave;
            reloadSave = null;
            if (save != null)
            {
                try
                {
                    GameDataSaveLoader.CheckVersionAndLoadGame(save);
                }
                catch (Exception ex)
                {
                    Note("queue: reload FAILED: " + ex);
                }
            }
            return;
        }
        if (automationActive && Current.Game != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }
    }

    // Вражеский рейд на первую карту: бой, генерация пешек, выстрелы.
    private static void ScenarioRaid()
    {
        Map map = Find.Maps[0];
        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
        parms.points = 500f;
        parms.forced = true;
        bool ok = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
        Note("scenario raid: executed=" + ok + ", faction=" + (parms.faction != null ? parms.faction.Name : "null"));
    }

    // Второе поселение игрока на случайной клетке: вторая карта с дикой природой, без колонистов.
    private static void ScenarioSecondMap(bool freeze)
    {
        PlanetTile tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer);
        if (!tile.Valid)
        {
            Note("scenario twomaps: no valid tile");
            return;
        }
        SettleUtility.AddNewHome(tile, Faction.OfPlayer);
        Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(150, 1, 150), WorldObjectDefOf.Settlement);
        MapSimState? state = MapSimState.For(map);
        if (freeze && state != null)
        {
            state.frozen = true;
        }
        List<Map> maps = Find.Maps;
        Note($"scenario twomaps: tile={tile}, new map id={map.uniqueID}, frozen={freeze}, maps now={maps.Count}");
    }
}

/// <summary>
/// Выполняется один раз при запуске игры, когда загрузка модов завершена.
/// Если в файле-метке указан сейв, загружает его без участия человека.
/// </summary>
[StaticConstructorOnStartup]
public static class TestAutomationBoot
{
    static TestAutomationBoot()
    {
        try
        {
            TestAutomation.Config cfg = TestAutomation.Read();
            if (cfg.armed && cfg.save == null && cfg.serverHost != null)
            {
                TestAutomation.ScheduleEntryConnect(cfg);
                return;
            }
            if (!cfg.armed || cfg.save == null)
            {
                return;
            }
            string save = cfg.save;
            if (!File.Exists(GenFilePaths.FilePathForSavedGame(save)))
            {
                TestAutomation.Note("autoload: save not found: " + save);
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                TestAutomation.Note("autoload: loading save '" + save + "'");
                GameDataSaveLoader.CheckVersionAndLoadGame(save);
            });
        }
        catch (Exception ex)
        {
            TestAutomation.Note("autoload FAILED: " + ex);
        }
    }
}
