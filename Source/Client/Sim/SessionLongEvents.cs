using System;
using System.Linq;
using HarmonyLib;
using RWMLWC.Client.Input;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Долгие события игры, которые ставит симуляция или команда игрока (генерация карты при основании поселения,
/// лагерь, вход каравана на карту). В одиночной игре они идут в другом потоке, пока игрок видит экран загрузки,
/// а ошибка генерации выкидывает в главное меню.
///
/// В сессии (решение для T-035 агента B):
///  - событие выполняется в главном потоке (doAsynchronously = false) и в состоянии симуляции (Rand — глобальный
///    контекст сессии, а не «показа»): у всех клиентов одинаково. Генератор карты сам ставит зерно от клетки мира;
///  - после команды, поставившей такое событие, клиент не идёт дальше по тикам в этом кадре
///    (<see cref="PendingFromSimulation"/>; цикл тиков в LockstepClient прерывается), поэтому событие у всех
///    выполняется сразу после тика команды;
///  - ошибка генерации пишется в журнал, а игрок остаётся в сессии: генерация одинакова у всех,
///    так что и ошибка будет у всех одинаковой.
/// </summary>
internal static class SessionLongEvents
{
    /// <summary>Симуляция поставила долгое событие, и оно ещё не выполнено.</summary>
    public static bool PendingFromSimulation => pending > 0;

    private static int pending;

    /// <summary>Тест (ключ tracelong=1): писать вызовы Rand внутри долгих событий сессии в randtrace-long-*.txt.</summary>
    public static bool TraceLong;

    /// <summary>Идёт долгое событие, поставленное чужой командой: камера и выделение автора не трогают.</summary>
    public static bool RunningRemote { get; private set; }

    internal static void Wrap(ref Action action, string textKey, ref bool doAsynchronously, ref Action<Exception> exceptionHandler)
    {
        if (!PerMapSim.Enabled || !(CommandBus.Executing || MapRandContext.InTick))
        {
            return;
        }
        doAsynchronously = false;
        Action inner = action;
        bool remote = CommandBus.Executing && CommandBus.ExecutingRemote;
        pending++;
        action = () =>
        {
            bool began = !MapRandContext.InTick;
            if (began)
            {
                MapRandContext.BeginTick();
            }
            bool wasRemote = RunningRemote;
            RunningRemote = remote;
            try
            {
                MapRandContext.Enter(null);
                TestAutomation.Note($"long event '{textKey}' start, maps {Find.Maps.Count}");
                bool tracing = TraceLong && !RandTracer.Running;
                if (tracing)
                {
                    RandTracer.StartNow(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "randtrace-long-" + textKey + ".txt"));
                }
                try
                {
                    inner();
                }
                finally
                {
                    if (tracing)
                    {
                        RandTracer.Stop();
                    }
                }
                TestAutomation.Note($"long event '{textKey}' done, maps {Find.Maps.Count}");
                if (Find.Maps.Count > 0)
                {
                    // Диагностика T-073: состав самой новой карты, чтобы сравнить игроков.
                    Map last = Find.Maps[Find.Maps.Count - 1];
                    string byCat = string.Join(",", last.listerThings.AllThings.GroupBy(t => t.def.category).OrderBy(g => g.Key).Select(g => g.Key + "=" + g.Count()));
                    string pawns = string.Join(",", last.mapPawns.AllPawns.OrderBy(p => p.thingIDNumber).Select(p => p.def.defName + "#" + p.thingIDNumber + "@" + p.Position));
                    TestAutomation.Note($"map {last.uniqueID} after '{textKey}': {byCat}; pawns {pawns}; rand {MapRandContext.CurrentRawState:X16}");
                }
            }
            finally
            {
                RunningRemote = wasRemote;
                if (began)
                {
                    MapRandContext.EndTick();
                }
                pending = Math.Max(0, pending - 1);
            }
        };
        exceptionHandler = ex =>
        {
            pending = Math.Max(0, pending - 1);
            TestAutomation.Note("long event FAILED in session: " + ex.GetType().Name + ": " + ex.Message);
            Log.Error("[RWMLWC] долгое событие в сессии упало (игра продолжается): " + ex);
        };
    }
}

[HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
    new[] { typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action) })]
internal static class Patch_LongEventHandler_QueueLongEvent_Session
{
    private static void Prefix(ref Action action, string textKey, ref bool doAsynchronously, ref Action<Exception> exceptionHandler)
    {
        SessionLongEvents.Wrap(ref action, textKey, ref doAsynchronously, ref exceptionHandler);
    }
}

// Долгое событие с обычным окном загрузки ждёт его отрисовки (ShouldWaitUntilDisplayed → repaint).
// В тихом/свёрнутом окне Waylith repaint не приходит — событие висит вечно и держит тики
// (ForcePause). Так вставали: ванильное автосохранение на суточной границе (t076-autosave) и
// генерация карты новой колонии (t076-verify1, тик 15305). В сессии ждать отрисовку нельзя:
// событие выполняется сразу на следующем кадре LongEventsUpdate, окно может не рисоваться вовсе.
[HarmonyPatch]
internal static class Patch_QueuedLongEvent_ShouldWaitUntilDisplayed_Session
{
    private static System.Reflection.MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("Verse.LongEventHandler+QueuedLongEvent");
        return t == null ? null : AccessTools.PropertyGetter(t, "ShouldWaitUntilDisplayed");
    }

    private static bool Prefix(ref bool __result)
    {
        if (!LockstepClient.InSession)
        {
            return true;
        }
        __result = false;
        return false;
    }
}

// Ванильное автосохранение раз в игровые сутки ставит долгое событие с окном загрузки и ждёт его
// отрисовки (ShouldWaitUntilDisplayed). В сессии это лишнее: сервер сам периодически получает копию
// мира (SaveWorldSnapshotKind). А ожидание отрисовки в тихом тестовом окне не заканчивается никогда
// (repaint не приходит) — игра замирает на сутки от старта (найдено в T-076: заморозка на тике 74998).
// В одиночной игре автосохранение ванильное.
[HarmonyPatch(typeof(RimWorld.Autosaver), nameof(RimWorld.Autosaver.AutosaverTick))]
internal static class Patch_Autosaver_AutosaverTick_Session
{
    private static bool Prefix() => !LockstepClient.InSession;
}

// Прыжок камеры и выделение — только у того, чья команда (после основания поселения игра переводит камеру на пешку).
[HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJump), new[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) })]
internal static class Patch_CameraJumper_TryJump_Remote
{
    private static bool Prefix() => !(CommandBus.ExecutingRemote || SessionLongEvents.RunningRemote);
}

[HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect))]
internal static class Patch_CameraJumper_TryJumpAndSelect_Remote
{
    private static bool Prefix() => !(CommandBus.ExecutingRemote || SessionLongEvents.RunningRemote);
}
