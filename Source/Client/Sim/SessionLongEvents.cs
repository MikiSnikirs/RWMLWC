using System;
using HarmonyLib;
using RWMLWC.Client.Input;
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

    internal static void Wrap(ref Action action, ref bool doAsynchronously, ref Action<Exception> exceptionHandler)
    {
        if (!PerMapSim.Enabled || !(CommandBus.Executing || MapRandContext.InTick))
        {
            return;
        }
        doAsynchronously = false;
        Action inner = action;
        pending++;
        action = () =>
        {
            bool began = !MapRandContext.InTick;
            if (began)
            {
                MapRandContext.BeginTick();
            }
            try
            {
                MapRandContext.Enter(null);
                inner();
            }
            finally
            {
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
    private static void Prefix(ref Action action, ref bool doAsynchronously, ref Action<Exception> exceptionHandler)
    {
        SessionLongEvents.Wrap(ref action, ref doAsynchronously, ref exceptionHandler);
    }
}
