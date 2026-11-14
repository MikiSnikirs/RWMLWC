using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Пишет хеши состояния карт в файл через равные промежутки тиков (T-010, T-015).
/// Два прогона одного сейва дают два файла; одинаковые файлы = детерминированная симуляция.
/// Файлы лежат в папке данных игры, подпапка RWMLWC.
/// </summary>
public sealed class HashRecorder : GameComponent
{
    public const int Interval = 250;

    /// <summary>Первые тики прогона замеряются каждый тик: так видно, на каком тике началось расхождение.</summary>
    public const int DenseTicks = 300;

    /// <summary>Фиксированное глобальное состояние Rand для тестового прогона.</summary>
    public const ulong TestGlobalRandState = 0x0000000052574D4CUL; // seed "RWML", счётчик 0

    private bool recording;
    private int startTick;
    private int endTick;
    private string? filePath;
    private string scenarioName = "none";

    /// <summary>Добавка к имени файла хешей (имя игрока в сессии), чтобы два экземпляра игры не писали в один файл.</summary>
    public static string FileTag = "";
    private int denseFrom;
    private int denseTo = DenseTicks;
    private readonly StringBuilder buffer = new StringBuilder();

    // Диагностика поворотов в первых тиках прогона (по одной карте — первой).
    private string? rotLogPath;
    private readonly StringBuilder rotBuffer = new StringBuilder();
    private readonly Dictionary<int, int> lastRot = new Dictionary<int, int>();

    // Прогон должен начаться строго до первого тика после загрузки, иначе два запуска
    // стартуют с разных тиков и их нельзя сравнивать. Поэтому старт не ручной:
    // при загрузке сейва проверяется файл-метка, а сам старт делает префикс DoSingleTick.
    private static HashRecorder? pendingStart;

    // Игра создаёт компоненты через конструктор с параметром Game.
    public HashRecorder(Game game)
    {
    }

    /// <summary>Файл-метка: пока он существует, каждый загруженный сейв начинает прогон сам.</summary>
    public static string ArmFilePath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");

    public static bool Armed => TestAutomation.Read().armed;

    public static void SetArmed(bool armed)
    {
        string path = ArmFilePath;
        if (armed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "# RWMLWC: automatic determinism run on game load. Keys: see TestAutomation.cs\narmed=1\n");
        }
        else if (File.Exists(path))
        {
            // Перезапись вместо удаления: агенту на машине игрока удаление файлов может быть запрещено.
            File.WriteAllText(path, "armed=0\n");
        }
    }

    // Прогон, прерванный загрузкой снимка: после загрузки запись продолжается в тот же файл,
    // без сброса Rand (сценарий snap-reload).
    private sealed class ResumeInfo
    {
        public string? filePath;
        public string scenario = "none";
        public int startTick;
        public int endTick;
        public int denseFrom;
        public int denseTo;
    }

    private static ResumeInfo? resumeInfo;
    private static HashRecorder? pendingResume;

    public override void LoadedGame()
    {
        if (resumeInfo != null)
        {
            pendingResume = this;
            pendingStart = null;
            return;
        }
        TestAutomation.Config cfg = TestAutomation.Read();
        pendingStart = cfg.armed ? this : null;
        if (cfg.armed)
        {
            TestAutomation.OnGameLoaded(cfg);
        }
    }

    /// <summary>Остановить запись перед загрузкой снимка; после загрузки она продолжится сама.</summary>
    public void SuspendForSnapshotReload()
    {
        if (!recording)
        {
            return;
        }
        resumeInfo = new ResumeInfo
        {
            filePath = filePath,
            scenario = scenarioName,
            startTick = startTick,
            endTick = endTick,
            denseFrom = denseFrom,
            denseTo = denseTo,
        };
        recording = false;
        Flush();
    }

    private void ResumeAfterSnapshotReload(ResumeInfo info)
    {
        filePath = info.filePath;
        scenarioName = info.scenario;
        startTick = info.startTick;
        endTick = info.endTick;
        denseFrom = info.denseFrom;
        denseTo = info.denseTo;
        rotLogPath = null;
        buffer.Clear();
        recording = true;
    }

    /// <summary>Вызывается перед каждым тиком игры (патч TickManager.DoSingleTick).</summary>
    public static void BeforeTick()
    {
        HashRecorder? resuming = pendingResume;
        if (resuming != null)
        {
            pendingResume = null;
            ResumeInfo? info = resumeInfo;
            resumeInfo = null;
            if (info != null && ReferenceEquals(resuming, Instance))
            {
                resuming.ResumeAfterSnapshotReload(info);
                TestAutomation.Note("resumed after snapshot reload at tick " + Find.TickManager.TicksGame
                    + ", perMapSim=" + PerMapSim.Enabled);
            }
            return;
        }

        HashRecorder? active = Instance;
        if (active != null && active.recording)
        {
            TestAutomation.BeforeTickDuringRun(active, Find.TickManager.TicksGame - active.startTick);
        }

        HashRecorder? recorder = pendingStart;
        if (recorder == null)
        {
            return;
        }
        pendingStart = null;
        if (ReferenceEquals(recorder, Instance))
        {
            TestAutomation.Config cfg = TestAutomation.Read();
            FileTag = cfg.serverHost != null ? cfg.playerName + "-" : "";
            recorder.StartDeterminismRun(cfg.ticks, cfg.scenario, cfg.trace, cfg.denseFrom, cfg.denseTo, resetRand: cfg.serverHost == null);
            TestAutomation.OnRunStarted(cfg, recorder.startTick, recorder.filePath);
            Log.Message("[RWMLWC] determinism run auto-started at tick " + recorder.startTick + ": " + recorder.filePath);
        }
    }

    public const int AutoRunTicks = 10000;

    public bool Recording => recording;

    public string? FilePath => filePath;

    public static HashRecorder? Instance => Current.Game?.GetComponent<HashRecorder>();

    /// <summary>
    /// Начать прогон: включить раздельную симуляцию, привести все состояния Rand к известным значениям
    /// и писать хеши <paramref name="durationTicks"/> тиков.
    /// </summary>
    /// <param name="resetRand">
    /// Привести Rand к тестовым константам. В сессии (LockstepClient) — false: там состояние
    /// уже задано зерном сессии, и прогон только записывает хеши.
    /// </param>
    public void StartDeterminismRun(int durationTicks, string scenario = "none", bool trace = true, int denseFromTick = 0, int denseToTick = DenseTicks, bool resetRand = true)
    {
        denseFrom = denseFromTick;
        denseTo = denseToTick;
        List<Map> maps = Find.Maps;
        if (resetRand)
        {
            PerMapSim.SetEnabled(true);
            MapRandContext.SetGlobalState(TestGlobalRandState);
            for (int i = 0; i < maps.Count; i++)
            {
                MapSimState? state = MapSimState.For(maps[i]);
                if (state != null)
                {
                    state.frozen = false;
                    state.randState = state.InitialRandState();
                    state.randInitialized = true;
                    state.nextThingId = 0;
                }
            }
        }

        startTick = Find.TickManager.TicksGame;
        endTick = startTick + durationTicks;
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        filePath = Path.Combine(dir, "hash-" + FileTag + stamp + ".txt");
        scenarioName = scenario;
        DumpThingsAtStart(dir, stamp);
        rotLogPath = Path.Combine(dir, "rotlog-" + stamp + ".txt");
        rotBuffer.Clear();
        lastRot.Clear();
        if (trace)
        {
            RandTracer.Start(Path.Combine(dir, "randtrace-" + stamp + ".txt"), startTick + denseFrom, startTick + denseTo);
        }
        buffer.Clear();
        buffer.AppendLine($"# RWMLWC determinism run; startTick={startTick}; duration={durationTicks}; interval={Interval}; sessionSeed={PerMapSim.SessionSeed}; scenario={scenario}");
        recording = true;
        WriteSample();
    }

    public void Stop()
    {
        if (!recording)
        {
            return;
        }
        recording = false;
        RandTracer.Stop();
        Flush();
    }

    public override void GameComponentUpdate()
    {
        TestAutomation.KeepRunning();
    }

    public override void GameComponentTick()
    {
        if (!recording)
        {
            return;
        }
        int tick = Find.TickManager.TicksGame;
        int elapsed = tick - startTick;
        if ((elapsed >= denseFrom && elapsed <= denseTo) || elapsed % Interval == 0)
        {
            WriteSample();
        }
        if (tick >= endTick)
        {
            Stop();
            Log.Message("[RWMLWC] determinism run finished: " + filePath);
            Messages.Message("[RWMLWC] determinism run finished", RimWorld.MessageTypeDefOf.NeutralEvent, false);
            TestAutomation.OnRunFinished(filePath);
        }
    }

    // Полный список вещей на старте: по нему видно, что именно отличается сразу после загрузки.
    private static void DumpThingsAtStart(string dir, string stamp)
    {
        try
        {
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                StringBuilder sb = new StringBuilder();
                MapStateHasher.DumpThings(maps[i], sb);
                File.WriteAllText(Path.Combine(dir, $"things-{stamp}-map{maps[i].uniqueID}.txt"), sb.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] не удалось записать список вещей: " + ex);
        }
    }

    private void WriteSample()
    {
        int tick = Find.TickManager.TicksGame;
        buffer.AppendLine($"{tick};global;rand={MapRandContext.GlobalState:X16}");
        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            Map map = maps[i];
            MapStateHash hash = MapStateHasher.Compute(map);
            buffer.AppendLine($"{tick};map{map.uniqueID};{hash}");
        }
        if (tick - startTick >= denseFrom && tick - startTick <= denseTo && maps.Count > 0)
        {
            MapStateHasher.LogRotationChanges(maps[0], tick, lastRot, rotBuffer);
        }
        // Сбрасываем на диск по ходу, чтобы файл уцелел при вылете.
        if (buffer.Length > 8192)
        {
            Flush();
        }
    }

    private void Flush()
    {
        if (rotLogPath != null && rotBuffer.Length > 0)
        {
            try
            {
                File.AppendAllText(rotLogPath, rotBuffer.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("[RWMLWC] не удалось записать журнал поворотов: " + ex);
            }
            rotBuffer.Clear();
        }
        if (filePath == null || buffer.Length == 0)
        {
            return;
        }
        try
        {
            File.AppendAllText(filePath, buffer.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] не удалось записать файл хешей: " + ex);
        }
        buffer.Clear();
    }
}
