using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Записывает, кто вызывал Rand во время тиков: строка на вызов — тик, контекст, цепочка вызывающих методов.
/// Два файла от двух запусков одного сейва сравниваются построчно; первая отличающаяся строка
/// указывает на код, который ведёт себя по-разному.
///
/// Только диагностика: снятие стека на каждый вызов Rand дорого, поэтому пишутся лишь первые тики прогона.
/// Имена кадров стека могут отличаться между запусками из-за встраивания методов JIT-ом —
/// при разборе смотреть на число вызовов за тик и общий рисунок, а не на каждую мелочь.
/// </summary>
public static class RandTracer
{
    private const int MaxFrames = 7;

    private static bool active;
    private static string? filePath;
    private static int startTick;
    private static int ticksToTrace;
    private static int currentTick;
    private static readonly StringBuilder buffer = new StringBuilder();

    /// <summary>Идёт запись. Проверяется в патчах Rand на каждый вызов, поэтому простое поле.</summary>
    public static bool Active => active && suppress == 0;

    // Больше нуля, пока выполняется изолированный визуальный код (см. VisualRandIsolation):
    // его вызовы Rand не влияют на симуляцию и в трассировку не идут.
    private static int suppress;

    public static void PushSuppress() => suppress++;

    public static void PopSuppress()
    {
        if (suppress > 0)
        {
            suppress--;
        }
    }

    public static void Start(string path, int fromTick, int ticks)
    {
        filePath = path;
        startTick = fromTick;
        ticksToTrace = ticks;
        currentTick = fromTick;
        buffer.Clear();
        buffer.AppendLine("# tick;context;callers (innermost first)");
        active = true;
    }

    public static void Stop()
    {
        if (!active && buffer.Length == 0)
        {
            return;
        }
        active = false;
        suppress = 0;
        Flush();
    }

    /// <summary>Вызывается в начале каждого тика игры.</summary>
    public static void BeginTick()
    {
        suppress = 0;
        if (!active)
        {
            return;
        }
        // DoSingleTick увеличивает счётчик внутри себя, поэтому идущий тик — это текущее значение + 1.
        currentTick = Find.TickManager.TicksGame + 1;
        if (currentTick - startTick > ticksToTrace)
        {
            Stop();
            return;
        }
        if (buffer.Length > 65536)
        {
            Flush();
        }
    }

    /// <summary>Вызывается из префиксов Rand.Value и Rand.Int.</summary>
    public static void Record()
    {
        // Вне тика Rand принадлежит показу (интерфейс, отрисовка, звук) — это не симуляция, не пишем.
        if (!MapRandContext.InTick)
        {
            return;
        }
        Map? map = MapRandContext.Current;
        buffer.Append(currentTick).Append(';');
        if (map == null)
        {
            buffer.Append("global");
        }
        else
        {
            buffer.Append("map").Append(map.uniqueID);
        }
        buffer.Append(';');

        StackTrace trace = new StackTrace(1, false);
        int written = 0;
        for (int i = 0; i < trace.FrameCount && written < MaxFrames; i++)
        {
            MethodBase? method = trace.GetFrame(i)?.GetMethod();
            if (method == null)
            {
                continue;
            }
            Type? type = method.DeclaringType;
            // Пропускаем сам Rand, наш трассировщик и обёртки Harmony: интересен код, который просил случайность.
            if (type == typeof(Rand) || type == typeof(RandTracer) || type == null)
            {
                continue;
            }
            string typeName = type.Name;
            if (typeName.StartsWith("Patch_Rand_", StringComparison.Ordinal))
            {
                continue;
            }
            if (written > 0)
            {
                buffer.Append(" < ");
            }
            buffer.Append(typeName).Append('.').Append(method.Name);
            written++;
        }
        buffer.Append('\n');
    }

    private static void Flush()
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
            Log.Error("[RWMLWC] не удалось записать трассировку Rand: " + ex);
        }
        buffer.Clear();
    }
}
