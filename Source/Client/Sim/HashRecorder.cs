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

    public override void LoadedGame()
    {
        pendingStart = Armed ? this : null;
    }

    /// <summary>Вызывается перед каждым тиком игры (патч TickManager.DoSingleTick).</summary>
    public static void BeforeTick()
    {
        HashRecorder? recorder = pendingStart;
        if (recorder == null)
        {
            return;
        }
        pendingStart = null;
        if (ReferenceEquals(recorder, Instance))
        {
            TestAutomation.Config cfg = TestAutomation.Read();
            recorder.StartDeterminismRun(cfg.ticks, cfg.scenario, cfg.trace);
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
    public void StartDeterminismRun(int durationTicks, string scenario = "none", bool trace = true)
    {
        PerMapSim.SetEnabled(true);
        MapRandContext.SetGlobalState(TestGlobalRandState);

        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            MapSimState? state = MapSimState.For(maps[i]);
            if (state != null)
            {
                state.frozen = false;
                state.randState = state.InitialRandState();
                state.randInitialized = true;
            }
        }

        startTick = Find.TickManager.TicksGame;
        endTick = startTick + durationTicks;
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        filePath = Path.Combine(dir, "hash-" + stamp + ".txt");
        scenarioName = scenario;
        DumpThingsAtStart(dir, stamp);
        rotLogPath = Path.Combine(dir, "rotlog-" + stamp + ".txt");
        rotBuffer.Clear();
        lastRot.Clear();
        if (trace)
        {
            RandTracer.Start(Path.Combine(dir, "randtrace-" + stamp + ".txt"), startTick, DenseTicks);
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
        if (elapsed <= DenseTicks || elapsed % Interval == 0)
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
        if (tick - startTick <= DenseTicks && maps.Count > 0)
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
