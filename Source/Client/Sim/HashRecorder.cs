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

    /// <summary>Фиксированное глобальное состояние Rand для тестового прогона.</summary>
    public const ulong TestGlobalRandState = 0x0000000052574D4CUL; // seed "RWML", счётчик 0

    private bool recording;
    private int startTick;
    private int endTick;
    private string? filePath;
    private readonly StringBuilder buffer = new StringBuilder();

    // Игра создаёт компоненты через конструктор с параметром Game.
    public HashRecorder(Game game)
    {
    }

    public bool Recording => recording;

    public string? FilePath => filePath;

    public static HashRecorder? Instance => Current.Game?.GetComponent<HashRecorder>();

    /// <summary>
    /// Начать прогон: включить раздельную симуляцию, привести все состояния Rand к известным значениям
    /// и писать хеши <paramref name="durationTicks"/> тиков.
    /// </summary>
    public void StartDeterminismRun(int durationTicks)
    {
        PerMapSim.Enabled = true;
        MapRandContext.ExitToGlobal();
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
        filePath = Path.Combine(dir, "hash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        buffer.Clear();
        buffer.AppendLine($"# RWMLWC determinism run; startTick={startTick}; duration={durationTicks}; interval={Interval}; sessionSeed={PerMapSim.SessionSeed}");
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
        Flush();
    }

    public override void GameComponentTick()
    {
        if (!recording)
        {
            return;
        }
        int tick = Find.TickManager.TicksGame;
        if ((tick - startTick) % Interval == 0)
        {
            WriteSample();
        }
        if (tick >= endTick)
        {
            Stop();
            Log.Message("[RWMLWC] determinism run finished: " + filePath);
            Messages.Message("[RWMLWC] determinism run finished", RimWorld.MessageTypeDefOf.NeutralEvent, false);
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
        // Сбрасываем на диск по ходу, чтобы файл уцелел при вылете.
        if (buffer.Length > 8192)
        {
            Flush();
        }
    }

    private void Flush()
    {
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
