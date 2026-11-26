using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Симметрия сбора мусора мировых пешек (T-056, шаг 4, прогон sr14).
///
/// <c>WorldPawnGC.GetCriticalPawnReason</c> оставляет пешку в мировых, если есть причина. Часть причин
/// зависит от карты и списков «только для игрока», которых у спящего клиента нет:
/// CorpseExists (труп на карте), InPlayLog/InBattleLog/InActiveTale (списки игрока), BondedAnimal
/// (привязанность колониста на карте). У владельца пешка остаётся, у спящего — убирается; списки
/// мировых пешек расходятся, за ними — общий Rand (sr14: тик 164000, та же сигнатура, что sr10).
///
/// В режиме sleepremove эти причины не учитываются у всех одинаково, и связи пешек
/// (<c>AddAllRelationships</c>) не расширяют список оставляемых. Остаются симметричные причины:
/// Colonist (флаг в данных пешки), FactionLeader, Kidnapped, CaravanMember, TransportPod, ForSale,
/// ForceKept, ReservedByQuest, CompPawnSource. Поведение сбора мусора в тестовом режиме меняется
/// (часть пешек убирается раньше, чем в игре) — осознанная плата за симметрию; вне режима — как в игре.
/// </summary>
[HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
internal static class Patch_WorldPawnGC_Reason_SleepRemove
{
    private static void Postfix(ref string __result)
    {
        if (!PerMapSim.SleepRemoveTest)
        {
            return;
        }
        switch (__result)
        {
            case "CorpseExists":
            case "InPlayLog":
            case "InBattleLog":
            case "InActiveTale":
            case "BondedAnimal":
                __result = null!;
                break;
        }
    }
}

[HarmonyPatch(typeof(WorldPawnGC), nameof(WorldPawnGC.AddAllRelationships))]
internal static class Patch_WorldPawnGC_Relationships_SleepRemove
{
    private static bool Prefix()
    {
        return !PerMapSim.SleepRemoveTest;
    }
}

/// <summary>Диагностика sleepremove: кого убирает сбор мусора мировых пешек (список входит в симметрию Rand).</summary>
[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.RemoveAndDiscardPawnViaGC))]
internal static class Patch_WorldPawns_Discard_Diag
{
    private static void Prefix(Pawn p)
    {
        if (PerMapSim.SleepRemoveTest && p != null)
        {
            TestAutomation.Note($"worldpawn gc: discarding {p.def.defName} #{p.thingIDNumber} (dead={p.Dead}, colonist={RimWorld.PawnUtility.EverBeenColonistOrTameAnimal(p)}) at tick {Find.TickManager.TicksGame}");
        }
    }
}
