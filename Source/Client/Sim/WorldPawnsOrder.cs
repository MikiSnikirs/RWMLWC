using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Пешки мира (<see cref="WorldPawns"/>) хранятся в HashSet. Порядок их обхода зависит от истории добавлений
/// и удалений, а после загрузки снимка — от порядка в сейве, поэтому у клиентов он может отличаться.
/// Генератор пешек выбирает из этого списка, кого «переодеть» (PawnGenerator.GetValidCandidatesToRedress,
/// TryRandomElementByWeight): при разном порядке тот же Rand даёт разных пешек, и новая карта поселения
/// у игроков получалась разной (T-073). В сессии списки пешек мира отдаются упорядоченными по номеру.
/// </summary>
internal static class WorldPawnsOrder
{
    internal static readonly IComparer<Pawn> ById = Comparer<Pawn>.Create((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.AllPawnsAlive), MethodType.Getter)]
internal static class Patch_WorldPawns_AllPawnsAlive_Order
{
    private static void Postfix(List<Pawn> __result)
    {
        if (PerMapSim.Enabled)
        {
            __result.Sort(WorldPawnsOrder.ById);
        }
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.AllPawnsAliveOrDead), MethodType.Getter)]
internal static class Patch_WorldPawns_AllPawnsAliveOrDead_Order
{
    private static void Postfix(List<Pawn> __result)
    {
        if (PerMapSim.Enabled)
        {
            __result.Sort(WorldPawnsOrder.ById);
        }
    }
}
