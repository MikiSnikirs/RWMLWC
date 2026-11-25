using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// В сессии тики идут только из цикла <c>LockstepClient.RunTicks</c>, по общему порядку. Игра в нескольких местах
/// делает тик сама, в кадре: при включённой настройке «пауза после загрузки» (<c>Prefs.PauseOnLoad</c>) —
/// после загрузки сейва (<c>Game.LoadGame</c>, в том числе общего снимка), после начала игры и после посадки
/// гравикорабля на новую клетку (<c>GravshipUtility.ArriveNewMap</c>); клавиша разработчика «тик». Настройки у игроков
/// разные — у одного стало бы на тик больше. Такие тики в сессии не выполняются.
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class Patch_TickManager_DoSingleTick_StrayGuard
{
    [HarmonyPriority(Priority.First + 1)]
    private static bool Prefix()
    {
        if (!LockstepClient.OwnsTicking || LockstepClient.InRunTicks)
        {
            return true;
        }
        TestAutomation.Note($"stray tick blocked at tick {Find.TickManager.TicksGame}: {new System.Diagnostics.StackTrace(2, false).GetFrame(0)?.GetMethod()?.Name}");
        return false;
    }
}
