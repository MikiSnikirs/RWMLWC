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
///   traceframes=7      — глубина цепочки вызовов в трассировке (больше — видно, кто вызвал, но файл крупнее);
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
        public int traceFrames = 7;
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
        public bool cameraWander;
        public bool desync;
        public int connectDelaySec;
        public bool quit;
        /// <summary>Потолок тиков за кадр в сессии (0 — без потолка): ключ tpf.</summary>
        public int ticksPerFrame;
        /// <summary>Ключ hostsession=1: после загрузки сейва открыть сессию в этой игре (приём друзей через Steam) и войти в неё.</summary>
        public bool hostSession;
        /// <summary>Ключ steamhost=auto: из главного меню войти через Steam к другу, который сейчас держит сессию RWMLWC.</summary>
        public bool steamAuto;
    }

    private static Config? activeConfig;

    /// <summary>
    /// Ровно столько тиков за кадр делает клиент в сессии, если сервер разрешил (0 — как обычно:
    /// сколько успеет за бюджет кадра). Нужен, чтобы воспроизвести зависимость симуляции от числа
    /// тиков на кадр: два клиента с разными tpf, при этом трассировка не меняет темп.
    /// </summary>
    public static int TicksPerFrameCap { get; private set; }
    private static string? pendingScenario;
    private static bool quitWhenDone;
    private static bool automationActive;
    // Шаги сценария, которые действуют как игрок: (тик, действие), по возрастанию тика.
    private static readonly Queue<(int tick, string name, Action act)> sessionSteps = new Queue<(int, string, Action)>();

    // Дополнительное условие шага (по имени): шаг ждёт, пока условие не выполнится, даже если его тик уже наступил.
    private static readonly Dictionary<string, Func<bool>> stepGates = new Dictionary<string, Func<bool>>();
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
                case "camera":
                    cfg.cameraWander = value == "1";
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
                case "traceframes":
                    if (int.TryParse(value, out int frames) && frames >= 1 && frames <= 64)
                    {
                        cfg.traceFrames = frames;
                    }
                    break;
                case "trace":
                    cfg.trace = value != "0";
                    break;
                case "quit":
                    cfg.quit = value == "1";
                    break;
                case "hostsession":
                    cfg.hostSession = value == "1";
                    break;
                case "steamhost":
                    // auto — найти среди друзей Steam; число — SteamID64 хоста (B: только в локальной метке, в журнал не пишется).
                    cfg.steamAuto = value == "auto" || ulong.TryParse(value, out steamHostId);
                    if (value == "auto") steamHostId = 0;
                    break;
                case "tpf":
                    if (int.TryParse(value, out int tpf) && tpf >= 0)
                    {
                        cfg.ticksPerFrame = tpf;
                    }
                    break;
            }
        }
        TicksPerFrameCap = cfg.armed ? cfg.ticksPerFrame : 0;
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
        stepGates.Clear();
        cameraHeld = false;
        RWMLWC.Client.Maps.RemoteCursors.FakeCursor = null;
        if (cfg.scenario == "access")
        {
            // T-033: c2 (issuer=0) занимает карту и выдаёт права; c1 (issuer=1) пробует действовать.
            if (cfg.issuer)
            {
                sessionSteps.Enqueue((startTick + 500, "access: designate without rights", () => AccessTry("designate, no rights", ScenarioDesignate)));
                sessionSteps.Enqueue((startTick + 2000, "access: designate with Build", () => AccessTry("designate, Build", ScenarioDesignate)));
                sessionSteps.Enqueue((startTick + 2100, "access: draft with Build", () => AccessTry("draft, Build", GizmoDraft)));
                // Отзыв действует сразу: c2 отзывает права на 3000-м тике, шаг ждёт, пока отзыв дойдёт до c1.
                sessionSteps.Enqueue((startTick + 3300, "access: designate after revoke", () => AccessTry("designate, revoked", ScenarioDesignate)));
                sessionSteps.Enqueue((startTick + 4000, "access: draft with Full until return", () => AccessTry("draft, Full until return, owner online", GizmoDraft)));
                stepGates["access: draft with Full until return"] = () =>
                    RWMLWC.Client.Maps.MapAccess.IsUntilReturn((Find.CurrentMap ?? Find.Maps[0]).uniqueID, LockstepClient.PlayerId);
                stepGates["access: designate after revoke"] = () =>
                    RWMLWC.Client.Maps.MapAccess.MyLevel((Find.CurrentMap ?? Find.Maps[0]).uniqueID) == RWMLWC.Shared.Protocol.PermissionLevel.None;
                // Права приходят вне общего порядка команд, а клиенты идут по тикам не вровень.
                // Поэтому шаг «с правами» ждёт не только своего тика, но и самих прав; выдача стоит
                // с запасом в 1000 тиков после попытки «без прав» (в прогоне агента B отстающий c1
                // получил права раньше, чем дошёл до своей первой попытки).
                // Попытка «без прав» имеет смысл, только когда карту уже занял c2: иначе карта свободна
                // и открыта всем (прогон A 14.11, 17:54 МСК — c1 дошёл до шага раньше, чем узнал о владельце).
                stepGates["access: designate without rights"] = () =>
                    RWMLWC.Client.Maps.MapAccess.OwnerOf((Find.CurrentMap ?? Find.Maps[0]).uniqueID) != 0;
                stepGates["access: designate with Build"] = () =>
                    RWMLWC.Client.Maps.MapAccess.MyLevel((Find.CurrentMap ?? Find.Maps[0]).uniqueID) >= RWMLWC.Shared.Protocol.PermissionLevel.Build;
            }
            else
            {
                sessionSteps.Enqueue((startTick + 300, "access: claim", AccessClaim));
                sessionSteps.Enqueue((startTick + 1500, "access: grant Build", AccessGrantBuild));
                sessionSteps.Enqueue((startTick + 3000, "access: revoke", AccessRevoke));
                // Управляющий (v7): полные права «пока меня нет»; пока владелец в сети, они не действуют.
                sessionSteps.Enqueue((startTick + 3500, "access: grant Full until return", () =>
                {
                    Map map = Find.CurrentMap ?? Find.Maps[0];
                    int other = LockstepClient.PlayerId == 1 ? 2 : 1;
                    RWMLWC.Client.Maps.MapAccess.Grant(map.uniqueID, other, RWMLWC.Shared.Protocol.PermissionLevel.Full, true);
                    Note($"access: granting Full until return to player {other}");
                }));
                sessionSteps.Enqueue((startTick + 3900, "access: show rights window", () =>
                {
                    Find.WindowStack.Add(new RWMLWC.Client.Maps.Dialog_MapAccess());
                }));
                sessionSteps.Enqueue((startTick + 4000, "access: screenshot", () => ScreenshotTaker.TakeNonSteamShot("access-look")));
            }
        }
        if (cfg.issuer && cfg.scenario == "tabs")
        {
            // T-023, ч. 2: в тестовом сейве нет верстака и склада — ставим их обычной разметкой (у места для крафта
            // время постройки 0, игра ставит его сразу). Шаги со счётом и складом ждут, пока они появятся у этого клиента.
            sessionSteps.Enqueue((startTick + 150, "tabs: setup table and stockpile", TabsSetup));
            stepGates["tabs: add bill"] = () => FirstWorkTable() != null;
            stepGates["tabs: storage filter"] = () => FirstStockpile() != null;
            sessionSteps.Enqueue((startTick + 300, "tabs: work priority", TabsWorkPriority));
            sessionSteps.Enqueue((startTick + 400, "tabs: schedule", TabsSchedule));
            sessionSteps.Enqueue((startTick + 500, "tabs: outfit policy", TabsOutfit));
            sessionSteps.Enqueue((startTick + 600, "tabs: allowed area", TabsArea));
            sessionSteps.Enqueue((startTick + 700, "tabs: research", TabsResearch));
            sessionSteps.Enqueue((startTick + 800, "tabs: add bill", TabsAddBill));
            sessionSteps.Enqueue((startTick + 900, "tabs: suspend bill", TabsSuspendBill));
            sessionSteps.Enqueue((startTick + 1000, "tabs: medical care", TabsMedCare));
            sessionSteps.Enqueue((startTick + 1100, "tabs: storage filter", TabsStorageFilter));
            sessionSteps.Enqueue((startTick + 1200, "tabs: storage priority", TabsStoragePriority));
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
        if (cfg.scenario == "color")
        {
            // Опыт по сообщению владельца: расхождение при выборе цвета игрока. Оба открывают окно выбора и выбирают цвет посреди игры.
            int index = cfg.issuer ? 1 : 3;
            sessionSteps.Enqueue((startTick + 300, "color: open window", () => Find.WindowStack.Add(new RWMLWC.Client.SessionView.Dialog_ChooseColor())));
            sessionSteps.Enqueue((startTick + (cfg.issuer ? 400 : 450), "color: choose", () =>
            {
                RWMLWC.Client.SessionView.SessionUi.ChooseColor(RWMLWC.Client.Maps.Squads.Palette[index]);
                Find.WindowStack.TryRemove(typeof(RWMLWC.Client.SessionView.Dialog_ChooseColor));
                Note($"color: chose {RWMLWC.Client.Maps.Squads.Palette[index]:X6} at tick {Find.TickManager.TicksGame}");
            }));
        }
        if (cfg.scenario == "cursors")
        {
            // TST-050: c1 «водит» курсором (точки рядом с центром карты), c2 видит его в цвете c1 с именем.
            if (cfg.issuer)
            {
                sessionSteps.Enqueue((startTick + 200, "cursors: point A", () => CursorAt(3f, 3f)));
                sessionSteps.Enqueue((startTick + 500, "cursors: point B", () => CursorAt(-4f, 2f)));
            }
            else
            {
                sessionSteps.Enqueue((startTick + 700, "cursors: look at remote cursor", CursorLook));
                sessionSteps.Enqueue((startTick + 800, "cursors: report and screenshot", () =>
                {
                    Note($"cursors: seen {string.Join(", ", RWMLWC.Client.SessionView.SessionUi.Cursors.Select(c => $"player {c.Key} map {c.Value.MapId} at {c.Value.X:0.#},{c.Value.Z:0.#} color {RWMLWC.Client.SessionView.SessionUi.ColorOf(c.Key):X6}"))}; drawn last frame {RWMLWC.Client.Maps.RemoteCursors.DrawnLastFrame}, labels {RWMLWC.Client.Maps.RemoteCursors.LabelsLastFrame}");
                    ScreenshotTaker.TakeNonSteamShot("cursors-look");
                }));
            }
        }
        if (cfg.issuer && cfg.scenario == "squads")
        {
            // TST-060, ч. 1: c1 создаёт два отряда, назначает участников и пешек, переводит пешку,
            // затем просит общий снимок: после загрузки отряды должны остаться теми же у обоих.
            sessionSteps.Enqueue((startTick + 300, "squads: create", () => SquadsStep(map => { RWMLWC.Client.Maps.Squads.Create(map, "Альфа"); RWMLWC.Client.Maps.Squads.Create(map, "Бета"); })));
            sessionSteps.Enqueue((startTick + 400, "squads: members", () => SquadsStep(map =>
            {
                int me = LockstepClient.PlayerId;
                int other = RWMLWC.Client.SessionView.SessionUi.Players.Select(p => p.PlayerId).Where(id => id != me).DefaultIfEmpty(0).Min();
                RWMLWC.Client.Maps.Squads.SetMember(map, 1, me, true);
                RWMLWC.Client.Maps.Squads.SetMember(map, 2, other, true);
                Note($"squads: members sent, me={me} other={other}");
            })));
            sessionSteps.Enqueue((startTick + 500, "squads: assign", () => SquadsStep(map =>
            {
                List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                RWMLWC.Client.Maps.Squads.AssignPawns(map, 1, colonists.Take(1));
                RWMLWC.Client.Maps.Squads.AssignPawns(map, 2, colonists.Skip(1).Take(1));
            })));
            sessionSteps.Enqueue((startTick + 700, "squads: move first pawn to squad 2", () => SquadsStep(map =>
                RWMLWC.Client.Maps.Squads.AssignPawns(map, 2, map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Take(1)))));
            sessionSteps.Enqueue((startTick + 900, "squads: digest before reload", () => SquadsStep(map => { })));
            sessionSteps.Enqueue((startTick + 950, "absence: autopilot for map", () => SquadsStep(map => RWMLWC.Client.Maps.AbsenceModes.Set(map, RWMLWC.Client.Maps.AbsenceMode.Autopilot))));
            sessionSteps.Enqueue((startTick + 1000, "squads: common snapshot", () => { Note("squads: requesting a common snapshot"); LockstepClient.RequestCommonSnapshot(); }));
            // TST-060, ч. 2 (T-061): после снимка у c1 — отряд «Альфа» без пешек, пешки первого и второго
            // колонистов в «Бете» (участник c2), третий колонист общий.
            // Шаги ждут, пока у c1 исполнятся его же команды об отрядах (команды отстающего клиента
            // встают в общий порядок позже, чем он их отдал).
            stepGates["squads: draft foreign via UI"] = () =>
            {
                Map m = Find.CurrentMap ?? Find.Maps[0];
                Pawn? first = m.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
                RWMLWC.Client.Maps.Squad? squad = first == null ? null : RWMLWC.Client.Maps.Squads.Component?.SquadOf(first.thingIDNumber);
                return squad != null && squad.id == 2 && !squad.players.Contains(LockstepClient.PlayerId);
            };
            sessionSteps.Enqueue((startTick + 1300, "squads: draft foreign via UI", () => SquadDraft(0, false)));
            sessionSteps.Enqueue((startTick + 1400, "squads: draft foreign bypassing UI", () => SquadDraft(0, true)));
            sessionSteps.Enqueue((startTick + 1600, "squads: draft common pawn", () => SquadDraft(2, false)));
            sessionSteps.Enqueue((startTick + 1800, "squads: work of foreign bypassing UI", SquadForeignWork));
            // T-062: имя и цвет своего отряда меняются, чужого — нет (c1 — участник «Альфы», не «Беты»).
            sessionSteps.Enqueue((startTick + 2000, "squads: rename and recolor own", () => SquadsStep(map =>
            {
                RWMLWC.Client.Maps.Squads.Rename(map, 1, "Орлы");
                RWMLWC.Client.Maps.Squads.SetColor(map, 1, RWMLWC.Client.Maps.Squads.Palette[2]);
            })));
            sessionSteps.Enqueue((startTick + 2100, "squads: rename and recolor foreign", () => SquadsStep(map =>
            {
                RWMLWC.Client.Maps.Squads.Rename(map, 2, "Чужие");
                RWMLWC.Client.Maps.Squads.SetColor(map, 2, RWMLWC.Client.Maps.Squads.Palette[3]);
            })));
            // Для проверки глазами: окно «Отряды» открыто, камера на пешке «Беты» (кольца цвета), она выделена.
            sessionSteps.Enqueue((startTick + 2300, "squads: show window", () => SquadsStep(map =>
            {
                Pawn? pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn != null)
                {
                    SelectOnly(pawn);
                    // Пешка левее центра экрана: окно справа её не закроет, кольца видны.
                    Find.CameraDriver.SetRootPosAndSize(pawn.Position.ToVector3Shifted() + new UnityEngine.Vector3(10f, 0f, 0f), 14f);
                    List<Gizmo> gizmos = pawn.GetGizmos().ToList();
                    Note($"squads: {pawn.LabelShort} gizmos: commands {gizmos.Count(g => g is Command)}, disabled {gizmos.Count(g => g is Command && g.Disabled)}"
                        + $", draft disabled={gizmos.OfType<Command_Toggle>().FirstOrDefault(t => t.hotKey == KeyBindingDefOf.Command_ColonistDraft)?.Disabled}");
                }
                RWMLWC.Client.Maps.Dialog_Squads window = new RWMLWC.Client.Maps.Dialog_Squads();
                Find.WindowStack.Add(window);
                window.windowRect.x = Verse.UI.screenWidth - window.windowRect.width - 8f;
            })));
            // Снимок окна игрой самой (внешний захват экрана окно Vulkan без фокуса не видит): папка Screenshots данных c1.
            sessionSteps.Enqueue((startTick + 2400, "squads: screenshot", () =>
            {
                ScreenshotTaker.TakeNonSteamShot("squads-look");
                Note("squads: screenshot saved to " + GenFilePaths.ScreenshotFolderPath);
            }));
        }
        if (cfg.issuer && cfg.scenario == "gizmo")
        {
            sessionSteps.Enqueue((startTick + 300, "gizmo: draft", GizmoDraft));
            sessionSteps.Enqueue((startTick + 400, "gizmo: forbid", GizmoForbid));
            sessionSteps.Enqueue((startTick + 500, "gizmo: attack", GizmoAttack));
            sessionSteps.Enqueue((startTick + 600, "gizmo: plant menu", GizmoPlantMenu));
            sessionSteps.Enqueue((startTick + 700, "gizmo: menu option", GizmoMenuOption));
        }
        RandTracer.MaxFrames = cfg.traceFrames;
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
        if (cfg.hostSession && LockstepClient.CurrentState == LockstepClient.State.Off)
        {
            // Совместный тест двух машин: сессия в игре хоста, друг входит через Steam. SteamID и ник Steam в журнал
            // автопрогона не пишем (он попадает в публичный репозиторий): имя игрока — из метки.
            RWMLWC.Client.Net.HostedSession.Start(Find.TickManager.TicksGame, seed: 7);
            RWMLWC.Client.Net.SteamInvite.AdvertiseHost();
            LockstepClient.HoldSeconds = cfg.holdSec;
            LockstepClient.HoldUntilPlayers = 2; // ждём друга (не дольше holdsec), затем оба начинают с одного тика
            LockstepClient.ForceSnapshotSender = true;
            LockstepClient.ConnectPassword = RWMLWC.Client.Net.HostedSession.Password;
            LockstepClient.Connect("127.0.0.1", RWMLWC.Client.Net.HostedSession.Port, RWMLWC.Client.Net.HostedSession.Thumbprint, cfg.playerName);
            Note($"hostsession: open at tick {Find.TickManager.TicksGame}, steam={RWMLWC.Client.Net.SteamNet.Available}, hold {cfg.holdSec} s");
            return;
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
                case "darkness":
                    ScenarioDarkness();
                    break;
                case "absence-raid":
                    ScenarioAbsenceRaid();
                    break;
                case "ui":
                case "access":
                case "tabs":
                case "desync":
                case "designate":
                case "gizmo":
                case "rightclick":
                case "squads":
                case "cursors":
                case "color":
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

    // Камера этого клиента «гуляет»: раз в 150 тиков прыгает в случайное место карты и меняет приближение.
    // У двух экземпляров игры на одной машине камеры иначе одинаковы, и зависимость симуляции
    // от камеры (docs/research/sim.md, 6з) такой тест не поймал бы.
    private static readonly System.Random cameraRandom = new System.Random();
    private static int nextCameraJumpTick;
    private static int cameraJumps;

    private static void WanderCamera()
    {
        Config? cfg = activeConfig;
        if (cfg == null || !cfg.cameraWander || cameraHeld || Current.Game == null || Find.CurrentMap == null)
        {
            return;
        }
        int tick = Find.TickManager.TicksGame;
        if (tick < nextCameraJumpTick)
        {
            return;
        }
        nextCameraJumpTick = tick + 150;
        Map map = Find.CurrentMap;
        IntVec3 cell = new IntVec3(cameraRandom.Next(5, map.Size.x - 5), 0, cameraRandom.Next(5, map.Size.z - 5));
        float size = 11f + (float)cameraRandom.NextDouble() * 49f;
        Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted(), size);
        cameraJumps++;
    }

    /// <summary>Раз в кадр в сессии, между тиками: сценарии, которые действуют как игрок.</summary>
    public static void SessionFrame()
    {
        WanderCamera();
        if (sessionSteps.Count == 0 || Current.Game == null || Find.TickManager.TicksGame < sessionSteps.Peek().tick)
        {
            return;
        }
        if (stepGates.TryGetValue(sessionSteps.Peek().name, out Func<bool>? gate) && !gate())
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
            // Карточка ставит игру на паузу (forcePause): прогон без людей тогда стоит, пока её не закроют.
            // Для проверки «интерфейс не меняет игру» пауза не нужна — открываем без неё.
            Dialog_InfoCard card = new Dialog_InfoCard(pawn);
            card.forcePause = false;
            Find.WindowStack.Add(card);
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

    private static void TabsResearch()
    {
        ResearchProjectDef? current = Find.ResearchManager.GetProject();
        ResearchProjectDef? other = DefDatabase<ResearchProjectDef>.AllDefsListForReading.OrderBy(p => p.defName)
            .FirstOrDefault(p => p != current && p.CanStartNow);
        if (other == null)
        {
            Note("tabs: research: nothing to start");
            return;
        }
        int sentBefore = LockstepClient.CommandsSent;
        Find.ResearchManager.SetCurrentProject(other);
        Note($"tabs: research {(current != null ? current.defName : "-")} -> {other.defName}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static Building_WorkTable? FirstWorkTable()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        return map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()
            .Where(t => t.def.AllRecipes.Any(r => r.AvailableNow)).OrderBy(t => t.thingIDNumber).FirstOrDefault();
    }

    private static void TabsSetup()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        ThingDef? spotDef = DefDatabase<ThingDef>.GetNamedSilentFail("CraftingSpot");
        IntVec3 center = map.Center + new IntVec3(-12, 0, -12);
        string spotAt = "none";
        if (spotDef != null)
        {
            Designator_Build spot = new Designator_Build(spotDef);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(center, 10f, true))
            {
                if (c.InBounds(map) && spot.CanDesignateCell(c).Accepted)
                {
                    spot.DesignateSingleCell(c);
                    spotAt = c.ToString();
                    break;
                }
            }
        }
        List<IntVec3> zoneCells = new List<IntVec3>();
        Designator_ZoneAddStockpile_Resources stockpile = new Designator_ZoneAddStockpile_Resources();
        foreach (IntVec3 c in CellRect.CenteredOn(center + new IntVec3(6, 0, 0), 2))
        {
            if (c.InBounds(map) && stockpile.CanDesignateCell(c).Accepted)
            {
                zoneCells.Add(c);
            }
        }
        if (zoneCells.Count > 0)
        {
            stockpile.DesignateMultiCell(zoneCells);
        }
        Note($"tabs: setup: crafting spot at {spotAt}, stockpile {zoneCells.Count} cells; sent={LockstepClient.PlayerCommandsSent}");
    }

    private static void TabsAddBill()
    {
        Building_WorkTable? table = FirstWorkTable();
        if (table == null)
        {
            Note("tabs: add bill: no work table in this save");
            return;
        }
        RecipeDef recipe = table.def.AllRecipes.First(r => r.AvailableNow);
        int sentBefore = LockstepClient.CommandsSent;
        // Как вкладка «Счета»: сначала создаётся счёт, потом добавляется.
        table.billStack.AddBill(recipe.MakeNewBill());
        Note($"tabs: add bill {recipe.defName} to {table.LabelShort} (id {table.thingIDNumber}), bills before={table.billStack.Count}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static void TabsSuspendBill()
    {
        Building_WorkTable? table = FirstWorkTable();
        if (table == null || table.billStack.Count == 0)
        {
            Note("tabs: suspend bill: no bill");
            return;
        }
        Bill bill = table.billStack[table.billStack.Count - 1];
        int sentBefore = LockstepClient.CommandsSent;
        // Как строка счёта в списке: запись прямо в поле, под наблюдением FieldWatch.
        FieldWatch.RunWatched(bill, () => bill.suspended = !bill.suspended);
        Note($"tabs: suspend bill {bill.recipe.defName} on {table.LabelShort}, sent={LockstepClient.CommandsSent - sentBefore}, value now={bill.suspended}");
    }

    private static void TabsMedCare()
    {
        Pawn? pawn = FirstColonist();
        if (pawn == null || pawn.playerSettings == null)
        {
            Note("tabs: medical care: no colonist");
            return;
        }
        MedicalCareCategory before = pawn.playerSettings.medCare;
        MedicalCareCategory after = before == MedicalCareCategory.Best ? MedicalCareCategory.NoMeds : MedicalCareCategory.Best;
        int sentBefore = LockstepClient.CommandsSent;
        FieldWatch.RunWatched(pawn.playerSettings, () => pawn.playerSettings.medCare = after);
        Note($"tabs: medical care of {pawn.LabelShort}: {before} -> {after}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static Zone_Stockpile? FirstStockpile()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        return map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID).FirstOrDefault();
    }

    private static void TabsStorageFilter()
    {
        Zone_Stockpile? zone = FirstStockpile();
        if (zone == null)
        {
            Note("tabs: storage filter: no stockpile in this save");
            return;
        }
        bool before = zone.settings.filter.Allows(ThingDefOf.WoodLog);
        int sentBefore = LockstepClient.CommandsSent;
        zone.settings.filter.SetAllow(ThingDefOf.WoodLog, !before);
        Note($"tabs: storage filter of zone {zone.ID}: wood {before} -> {!before}, sent={LockstepClient.CommandsSent - sentBefore}, value now={zone.settings.filter.Allows(ThingDefOf.WoodLog)}");
    }

    private static void TabsStoragePriority()
    {
        Zone_Stockpile? zone = FirstStockpile();
        if (zone == null)
        {
            Note("tabs: storage priority: no stockpile in this save");
            return;
        }
        StoragePriority before = zone.settings.Priority;
        StoragePriority after = before == StoragePriority.Critical ? StoragePriority.Low : StoragePriority.Critical;
        int sentBefore = LockstepClient.CommandsSent;
        zone.settings.Priority = after;
        Note($"tabs: storage priority of zone {zone.ID}: {before} -> {after}, sent={LockstepClient.CommandsSent - sentBefore}");
    }

    private static string SettingsDigest(Map map)
    {
        Fnv64 hash = Fnv64.Create();
        foreach (Zone_Stockpile zone in map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID))
        {
            hash.Add(zone.ID);
            hash.Add((int)zone.settings.Priority);
            hash.Add(zone.settings.filter.Allows(ThingDefOf.WoodLog));
        }
        ResearchProjectDef? project = Find.ResearchManager.GetProject();
        hash.Add(project != null ? (int)project.shortHash : -1);
        foreach (Building_WorkTable table in map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().OrderBy(t => t.thingIDNumber))
        {
            hash.Add(table.thingIDNumber);
            foreach (Bill bill in table.billStack.Bills)
            {
                hash.Add((int)bill.recipe.shortHash);
                hash.Add(bill.suspended);
            }
        }
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
            hash.Add(pawn.playerSettings != null ? (int)pawn.playerSettings.medCare : -1);
        }
        return hash.Value.ToString("X16");
    }

    // --- сценарий access (T-033): владелец карты и права ---

    private static void AccessClaim()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        RWMLWC.Client.Maps.MapAccess.Claim(map);
        Note($"access: player {LockstepClient.PlayerId} claims map {map.uniqueID}");
    }

    private static void AccessGrantBuild()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        int other = LockstepClient.PlayerId == 1 ? 2 : 1;
        RWMLWC.Client.Maps.MapAccess.Grant(map.uniqueID, other, RWMLWC.Shared.Protocol.PermissionLevel.Build);
        Note($"access: owner of map {map.uniqueID} is {RWMLWC.Client.Maps.MapAccess.OwnerOf(map.uniqueID)}; granting Build to player {other}");
    }

    private static void AccessRevoke()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        int other = LockstepClient.PlayerId == 1 ? 2 : 1;
        RWMLWC.Client.Maps.MapAccess.Grant(map.uniqueID, other, RWMLWC.Shared.Protocol.PermissionLevel.None);
        Note($"access: revoking rights of player {other} on map {map.uniqueID}");
    }

    private static void AccessTry(string what, Action act)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        int sentBefore = LockstepClient.CommandsSent;
        Note($"access: try '{what}': my level on map {map.uniqueID} is {RWMLWC.Client.Maps.MapAccess.MyLevel(map.uniqueID)}, owner {RWMLWC.Client.Maps.MapAccess.OwnerOf(map.uniqueID)}");
        act();
        Note($"access: try '{what}': commands sent={LockstepClient.CommandsSent - sentBefore}");
    }

    // --- сценарий desync (T-034): искусственное расхождение ---

    private static void DesyncBreak()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        MapSimState? sim = MapSimState.For(map);
        MapStateHash before = MapStateHasher.Compute(map);

        // Порча в двух независимых местах, оба входят в хеш карты:
        //  1) счётчик Rand карты сдвигается на единицу прямо в её сохранённом состоянии;
        //  2) у одной постройки отнимается единица прочности.
        // Первая версия брала лишнее число через Rand.Value между тиками, и хеши у клиентов
        // остались одинаковыми (прогон TST-034 у агента B) — причина не найдена, поэтому теперь
        // состояние меняется напрямую, а в журнал пишутся хеши до и после.
        if (sim != null)
        {
            if (!sim.randInitialized)
            {
                sim.randState = sim.InitialRandState();
                sim.randInitialized = true;
            }
            unchecked
            {
                sim.randState += 1UL << 32;
            }
        }
        Thing? victim = map.listerThings.AllThings
            .Where(t => t.def.category == ThingCategory.Building && t.def.useHitPoints && t.HitPoints > 1)
            .OrderBy(t => t.thingIDNumber)
            .FirstOrDefault();
        if (victim != null)
        {
            victim.HitPoints -= 1;
        }

        MapStateHash after = MapStateHasher.Compute(map);
        Note($"desync: broke map {map.uniqueID} at tick {Find.TickManager.TicksGame}: rand {before.rand:X16} -> {after.rand:X16}, "
            + $"victim {(victim != null ? victim.ThingID : "none")}, total {before.total:X16} -> {after.total:X16}, changed={before.total != after.total}");
    }

    // --- сценарий gizmo (T-021): нажатия кнопок выделенных объектов, приказ атаки, меню ---

    private static Pawn? gizmoPawn;

    private static void SelectOnly(object obj)
    {
        Find.Selector.ClearSelection();
        Find.Selector.Select(obj, false, false);
    }

    private static void SquadDraft(int colonistIndex, bool bypass)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Skip(colonistIndex).FirstOrDefault();
        if (pawn == null)
        {
            Note("squads: draft: no colonist #" + colonistIndex);
            return;
        }
        SelectOnly(pawn);
        RWMLWC.Client.Maps.SquadRules.BypassLocalCheck = bypass;
        int before = LockstepClient.PlayerCommandsSent;
        try
        {
            bool sent = GizmoCommands.SendClick(map, pawn, g => g is Command_Toggle toggle && toggle.hotKey == KeyBindingDefOf.Command_ColonistDraft);
            Note($"squads: draft {pawn.LabelShort} (id {pawn.thingIDNumber}, bypass={bypass}): click={sent}, commands sent={LockstepClient.PlayerCommandsSent - before}");
        }
        finally
        {
            RWMLWC.Client.Maps.SquadRules.BypassLocalCheck = false;
        }
    }

    private static void SquadForeignWork()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        Pawn? pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Skip(1).FirstOrDefault();
        WorkTypeDef? work = pawn == null || pawn.workSettings == null ? null
            : DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderBy(w => w.defName).FirstOrDefault(w => !pawn.WorkTypeIsDisabled(w));
        if (pawn == null || work == null)
        {
            Note("squads: work: no colonist or work type");
            return;
        }
        int before = pawn.workSettings!.GetPriority(work);
        int sentBefore = LockstepClient.PlayerCommandsSent;
        RWMLWC.Client.Maps.SquadRules.BypassLocalCheck = true;
        try
        {
            pawn.workSettings.SetPriority(work, before == 0 ? 3 : 0);
        }
        finally
        {
            RWMLWC.Client.Maps.SquadRules.BypassLocalCheck = false;
        }
        Note($"squads: work {work.defName} of {pawn.LabelShort} (id {pawn.thingIDNumber}) {before} -> {(before == 0 ? 3 : 0)} bypassing UI, commands sent={LockstepClient.PlayerCommandsSent - sentBefore}");
    }

    private static bool cameraHeld;

    private static void CursorAt(float dx, float dz)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        RWMLWC.Client.Maps.RemoteCursors.FakeCursor = map.Center.ToVector3Shifted() + new UnityEngine.Vector3(dx, 0f, dz);
        Note($"cursors: my cursor at {RWMLWC.Client.Maps.RemoteCursors.FakeCursor.Value.x:0.#},{RWMLWC.Client.Maps.RemoteCursors.FakeCursor.Value.z:0.#}");
    }

    private static void CursorLook()
    {
        cameraHeld = true; // камера c2 больше не гуляет
        foreach (KeyValuePair<int, RWMLWC.Client.SessionView.SessionUi.RemoteCursor> c in RWMLWC.Client.SessionView.SessionUi.Cursors)
        {
            if (c.Key != LockstepClient.PlayerId)
            {
                Find.CameraDriver.SetRootPosAndSize(new UnityEngine.Vector3(c.Value.X, 0f, c.Value.Z), 10f);
                Note($"cursors: camera on player {c.Key} cursor");
                return;
            }
        }
        Note("cursors: no remote cursor to look at");
    }

    private static void SquadsStep(Action<Map> act)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        act(map);
        Note("squads: now " + (RWMLWC.Client.Maps.Squads.Component?.Digest() ?? "no component"));
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
            if (cameraJumps > 0)
            {
                Note($"camera: this client moved its camera {cameraJumps} times during the run");
            }
            Note($"summary: map{map.uniqueID} settings={SettingsDigest(map)}");
            Note("summary: absence=" + (RWMLWC.Client.Maps.AbsenceModes.Component?.Digest() ?? "-") + " offline=[" + (RWMLWC.Client.Maps.PlayerPresence.Component?.Digest() ?? "-") + "]");
            Note("summary: squads=" + (RWMLWC.Client.Maps.Squads.Component?.Digest() ?? "-") + " rejected=" + RWMLWC.Client.Maps.SquadRules.Rejected
                + " drafted=[" + string.Join(",", map.mapPawns.FreeColonistsSpawned.Where(p => p.Drafted).Select(p => p.thingIDNumber).OrderBy(i => i)) + "]");
            Note($"summary: map{map.uniqueID} designations={map.designationManager.AllDesignations.Count} blueprints={map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count} zones={map.zoneManager.AllZones.Count}; commands sent={LockstepClient.CommandsSent} executed={CommandBus.Executed} failed={CommandBus.Failed}");
        }
    }

    public static void OnRunFinished(string? hashFile)
    {
        NoteMapSummary();
        if (LockstepClient.InSession && LockstepClient.PlayerCommandsSent > CommandBus.ExecutedOwn)
        {
            // Команда встаёт в общий порядок там, где сервер на момент её прихода; отстающий клиент
            // может закончить прогон раньше, чем дойдёт до неё. Тогда совпадение хешей ничего не доказывает.
            Note($"FAILED: own commands executed {CommandBus.ExecutedOwn} of {LockstepClient.PlayerCommandsSent} by the end of the run; increase ticks");
        }
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

    // Как «Присоединиться» в списке друзей Steam: хост объявляет rich presence connect = "+rwmlwc_join <SteamID>"
    // (SteamInvite.AdvertiseHost). SteamID берётся оттуда и никуда не записывается.
    // Явный SteamID64 хоста из метки (steamhost=<число>); 0 — искать среди друзей.
    private static ulong steamHostId;

    private static async System.Threading.Tasks.Task<RWMLWC.Net.IConnection> ConnectToHostingFriend()
    {
        await RWMLWC.Client.Net.SteamNet.WaitForRelayAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        int seenPlaying = -1;
        for (int attempt = 1; ; attempt++)
        {
            ulong host = steamHostId;
            int count = Steamworks.SteamFriends.GetFriendCount(Steamworks.EFriendFlags.k_EFriendFlagImmediate);
            int playing = 0;
            for (int i = 0; i < count && host == 0; i++)
            {
                Steamworks.CSteamID friend = Steamworks.SteamFriends.GetFriendByIndex(i, Steamworks.EFriendFlags.k_EFriendFlagImmediate);
                // Без запроса Steam может не прислать rich presence друга (B, TST-080: автопоиск не находил хоста).
                Steamworks.SteamFriends.RequestFriendRichPresence(friend);
                if (Steamworks.SteamFriends.GetFriendGamePlayed(friend, out Steamworks.FriendGameInfo_t game) && game.m_gameID.AppID().m_AppId == 294100u)
                {
                    playing++;
                }
                string connect = Steamworks.SteamFriends.GetFriendRichPresence(friend, "connect") ?? "";
                if (connect.StartsWith("+rwmlwc_join ", StringComparison.Ordinal) && ulong.TryParse(connect.Substring(13).Trim(), out ulong id))
                {
                    host = id;
                }
            }
            if (steamHostId == 0 && playing != seenPlaying)
            {
                seenPlaying = playing; // сколько друзей в RimWorld — без имён и номеров
                Note($"entry: Steam friends in RimWorld: {playing}, hosting RWMLWC: {(host != 0 ? 1 : 0)}");
            }
            if (host != 0)
            {
                try
                {
                    return await RWMLWC.Client.Net.SteamNet.ConnectAsync(host, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (Exception) when (attempt < (steamHostId != 0 ? 360 : 10)) // с явным номером ждём хоста до 30 минут
                {
                }
            }
            else if (attempt >= 360) // до 30 минут: хост может открыть сессию позже
            {
                throw new InvalidOperationException("no Steam friend hosts an RWMLWC session");
            }
            await System.Threading.Tasks.Task.Delay(5000).ConfigureAwait(false);
        }
    }
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
        if (cfg == null || (cfg.serverHost == null && !cfg.steamAuto) || entryClock.ElapsedMilliseconds < cfg.connectDelaySec * 1000L)
        {
            return;
        }
        entryConnect = null;
        if (cfg.quit)
        {
            automationActive = true;
        }
        if (cfg.steamAuto)
        {
            Note("entry: joining a Steam friend who hosts an RWMLWC session");
            LockstepClient.Connect(ConnectToHostingFriend(), "steam friend", cfg.playerName);
            return;
        }
        LockstepClient.Connect(cfg.serverHost!, cfg.serverPort, cfg.thumbprint, cfg.playerName);
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

    // TST-072: рейд рассказчика не приходит на карту, чей владелец (выбравший режим отсутствия) не в сети.
    private static void ScenarioAbsenceRaid()
    {
        Map map = Find.Maps[0];
        RWMLWC.Client.Maps.AbsenceModes.Component?.SetDirect(map.uniqueID, RWMLWC.Client.Maps.AbsenceMode.Autopilot, 7);
        RWMLWC.Client.Maps.PlayerPresence.Component?.Apply(7, false);
        Note("absence-raid: offline owner, raid fired=" + FireRaidViaStoryteller(map));
        RWMLWC.Client.Maps.PlayerPresence.Component?.Apply(7, true);
        Note("absence-raid: owner back, raid fired=" + FireRaidViaStoryteller(map));
    }

    private static bool FireRaidViaStoryteller(Map map)
    {
        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
        parms.points = 500f;
        parms.forced = true;
        return Find.Storyteller.TryFire(new FiringIncident(IncidentDefOf.RaidEnemy, null, parms));
    }

    // TST-015, ч. 2: неестественная тьма (Anomaly) — яркость карты меняется в тике, шаг раньше брался из длительности кадра.
    private static void ScenarioDarkness()
    {
        Map map = Find.Maps[0];
        if (!ModsConfig.AnomalyActive || GameConditionDefOf.UnnaturalDarkness == null)
        {
            Note("scenario darkness: Anomaly is not active");
            return;
        }
        GameCondition condition = GameConditionMaker.MakeCondition(GameConditionDefOf.UnnaturalDarkness, 30000);
        map.gameConditionManager.RegisterCondition(condition);
        Note($"scenario darkness: registered {condition.def.defName} for {condition.Duration} ticks");
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
            if (cfg.armed && cfg.save == null && (cfg.serverHost != null || cfg.steamAuto))
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
