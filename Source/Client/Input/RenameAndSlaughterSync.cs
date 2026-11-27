using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Input;

// T-089: окно автозабоя и переименование в сессии. Сами изменения уходят командами: строки окна автозабоя —
// FieldWatch (Dialog_AutoSlaughter.DoAnimalRow), имена — MethodCommands (IRenameable.RenamableLabel).
// Здесь — то, что окна делают в обход этих путей.

/// <summary>
/// Окно автозабоя после изменения строки само зовёт Notify_ConfigChanged — у автора и в кадре. Изменение при этом
/// откатывается и уходит командой, а пересчёт списка «кого забивать» делается при её исполнении у всех
/// (FieldWatch.AfterSet). Пометка из интерфейса не нужна.
/// </summary>
[HarmonyPatch(typeof(AutoSlaughterManager), nameof(AutoSlaughterManager.Notify_ConfigChanged))]
internal static class Patch_AutoSlaughterManager_Notify_ConfigChanged_NotFromUi
{
    private static bool Prefix() => !CommandBus.CaptureActive;
}

/// <summary>
/// Окно имени гравикорабля (при первой постройке оно открывается у всех игроков) после ввода пишет имя
/// через RenamableLabel — это уходит командой — и ещё отдельно поле nameHidden. В сессии пишется только имя:
/// запись имени сама снимает nameHidden, у всех при исполнении команды.
/// </summary>
[HarmonyPatch(typeof(Dialog_NamePlayerGravship), "Named")]
internal static class Patch_Dialog_NamePlayerGravship_Named
{
    private static readonly AccessTools.FieldRef<Dialog_NamePlayerGravship, Building_GravEngine> engine =
        AccessTools.FieldRefAccess<Dialog_NamePlayerGravship, Building_GravEngine>("engine");

    private static bool Prefix(Dialog_NamePlayerGravship __instance, string s)
    {
        if (!CommandBus.CaptureActive)
        {
            return true;
        }
        engine(__instance).RenamableLabel = s;
        return false;
    }
}

/// <summary>
/// Когда гравикорабль получил имя командой, окна имени этого корабля у остальных игроков закрываются:
/// имя уже дал кто-то один.
/// </summary>
[HarmonyPatch(typeof(Building_GravEngine), nameof(Building_GravEngine.RenamableLabel), MethodType.Setter)]
internal static class Patch_Building_GravEngine_RenamableLabel_CloseDialogs
{
    private static readonly AccessTools.FieldRef<Dialog_NamePlayerGravship, Building_GravEngine> engine =
        AccessTools.FieldRefAccess<Dialog_NamePlayerGravship, Building_GravEngine>("engine");

    private static void Postfix(Building_GravEngine __instance)
    {
        if (!CommandBus.Executing || Find.WindowStack == null)
        {
            return;
        }
        for (int i = Find.WindowStack.Windows.Count - 1; i >= 0; i--)
        {
            if (Find.WindowStack.Windows[i] is Dialog_NamePlayerGravship dialog && engine(dialog) == __instance)
            {
                Find.WindowStack.TryRemove(dialog, doCloseSound: false);
            }
        }
    }
}
