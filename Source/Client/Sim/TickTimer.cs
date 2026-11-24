using System.Diagnostics;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Время самого тика (T-056, критерий этапа 1: «время кадра не зависит от размера чужого поселения»).
/// Темп прогона задаёт сервер и скорость игры, поэтому длительность прогона ничего не говорит о нагрузке —
/// меряется только <c>TickManager.DoSingleTick</c>. Только диагностика; итог — в журнале прогона.
/// </summary>
internal static class TickTimer
{
    private static readonly Stopwatch watch = new Stopwatch();
    private static long totalTicks;
    private static long count;

    public static void Reset()
    {
        totalTicks = 0;
        count = 0;
    }

    internal static void Begin() => watch.Restart();

    internal static void End()
    {
        totalTicks += watch.ElapsedTicks;
        count++;
    }

    public static string Summary() =>
        count == 0 ? "no ticks" : $"{count} ticks, avg {totalTicks * 1000.0 / Stopwatch.Frequency / count:F3} ms, total {totalTicks / (double)Stopwatch.Frequency:F1} s";
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class Patch_TickManager_DoSingleTick_Timer
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix() => TickTimer.Begin();

    [HarmonyPriority(Priority.Last)]
    private static void Finalizer() => TickTimer.End();
}
