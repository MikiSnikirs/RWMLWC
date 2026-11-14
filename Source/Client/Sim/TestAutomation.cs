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
///   quit=1             — после прогона закрыть игру.
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
        public bool quit;
    }

    private static Config? activeConfig;
    private static string? pendingScenario;
    private static bool quitWhenDone;
    private static bool automationActive;
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
        Note($"run started: #{cfg.queueIndex}, tick={startTick}, scenario={cfg.scenario}, ticks={cfg.ticks}, trace={cfg.trace}, quit={cfg.quit}, file={Path.GetFileName(hashFile)}");
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
            Note("quitting game");
            Root.Shutdown();
        }
    }

    /// <summary>Вызывается раз в кадр во время автоматического прогона: снять автопаузу (например, от письма о рейде).</summary>
    public static void KeepRunning()
    {
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
