using System.Reflection;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Vanilla Expanded Framework при каждой загрузке игры, где нет каких-то его фракций, открывает окно
/// «новая фракция» (Dialog_NewFactionSpawning.OpenDialog из постфикса на GameComponentUtility.LoadedGame).
/// Выбор в окне (добавить фракцию с поселениями) меняет мир только у того, кто нажал. В сессии загрузка —
/// это общий снимок у всех игроков, и окно у каждого дало бы расхождение (T-085, t085f).
///
/// В сессии и в тестовом прогоне окно не открывается — как «ничего не делать». Хозяин видит его при загрузке
/// своего мира до начала сессии; что он выбрал, уходит остальным вместе со снимком мира.
/// </summary>
[HarmonyPatch]
internal static class Patch_VEF_NewFactionDialog_NotInSession
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        target = AccessTools.Method("VEF.Factions.Dialog_NewFactionSpawning:OpenDialog");
        return target != null;
    }

    private static MethodBase TargetMethod() => target!;

    private static bool Prefix()
    {
        if (!LockstepClient.InSession && !TestAutomation.AutomationActive)
        {
            return true;
        }
        TestAutomation.Note("mods: VEF new faction dialog suppressed (session or test run)");
        return false;
    }
}
