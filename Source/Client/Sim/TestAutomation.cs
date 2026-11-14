using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using RimWorld.Planet;
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
        public bool quit;
    }

    private static Config? activeConfig;
    private static string? pendingScenario;
    private static bool quitWhenDone;
    private static bool automationActive;
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

    public static void OnRunFinished(string? hashFile)
    {
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

    public static void KeepRunning()
    {
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
