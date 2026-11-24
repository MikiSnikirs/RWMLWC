using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// T-056, этап 1: номера, которые берёт тик карты, — из счётчика этой карты (как номера вещей, T-007).
/// Зонд <c>worldwrites=1</c> показал (t056-ww1/ww2): в обычной игре почти каждая пешка в тике сдвигает общий
/// счётчик <c>nextJobID</c>, бой — <c>nextHediffID</c>, <c>nextLogID</c>, <c>nextTaleID</c>, <c>nextBattleID</c>,
/// смерть — <c>nextLetterID</c>, <c>nextMessageID</c>, <c>nextRitualObligationID</c>. Пока все считают всё, это
/// одинаково у всех, но связывает карты: события одной карты меняют номера на другой. Когда карту будет считать
/// только её владелец, общий счётчик разошёлся бы между игроками.
///
/// Только виды, которые живут на карте или в общих списках с номером как ключом. Виды мира (фракции, объекты
/// мира, карты, караваны, задания, идеологии, условия мира…) остаются общими: их создание из тика карты —
/// отдельный вопрос («события карты», docs/ARCHITECTURE.md, 11.2).
/// Вне сессии, вне тика или вне контекста карты — как в игре.
/// </summary>
[HarmonyPatch]
internal static class Patch_UniqueIDsManager_PerMap
{
    internal static readonly string[] Kinds =
    {
        nameof(UniqueIDsManager.GetNextJobID),
        nameof(UniqueIDsManager.GetNextHediffID),
        nameof(UniqueIDsManager.GetNextLogID),
        nameof(UniqueIDsManager.GetNextTaleID),
        nameof(UniqueIDsManager.GetNextBattleID),
        nameof(UniqueIDsManager.GetNextLetterID),
        nameof(UniqueIDsManager.GetNextMessageID),
        nameof(UniqueIDsManager.GetNextRitualObligationID),
        nameof(UniqueIDsManager.GetNextLordID),
        nameof(UniqueIDsManager.GetNextBillID),
        nameof(UniqueIDsManager.GetNextAreaID),
        nameof(UniqueIDsManager.GetNextZoneID),
        nameof(UniqueIDsManager.GetNextPlanID),
        nameof(UniqueIDsManager.GetNextAnimalPenID),
        nameof(UniqueIDsManager.GetNextTransporterGroupID),
        nameof(UniqueIDsManager.GetNextArchivedDialogID),
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in Kinds)
        {
            yield return AccessTools.Method(typeof(UniqueIDsManager), name);
        }
    }

    private static bool Prefix(MethodBase __originalMethod, ref int __result)
    {
        if (!PerMapSim.Enabled || !MapRandContext.InTick)
        {
            return true;
        }
        Map? map = MapRandContext.Current;
        MapSimState? state = map == null ? null : MapSimState.For(map);
        if (state == null)
        {
            return true;
        }
        int id = state.TakeId(__originalMethod.Name);
        if (id < 0)
        {
            return true;
        }
        __result = id;
        return false;
    }
}
