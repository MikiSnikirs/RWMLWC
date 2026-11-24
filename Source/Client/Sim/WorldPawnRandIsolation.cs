using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// T-056: тик мировой пешки — со своим Rand, выведенным из её номера и тика, а не с общим Rand мира.
///
/// Трассировка t056-sl4 (раздельный счёт карт, рейд): первым общий Rand разошёлся в <c>WorldPawns.WorldPawnsTick</c> —
/// у клиента, считавшего карту, в мир ушла живая пешка с неё, и её <c>HediffGiver_RandomAgeCurved</c> и
/// <c>InspirationHandler</c> брали общий Rand. Со своим Rand у каждой пешки лишняя или недостающая пешка не сдвигает
/// случайность мира и других пешек; заодно не важен порядок обхода (игра обходит <c>HashSet</c>).
/// Сами списки мировых пешек при раздельном счёте ещё различаются — это события карты (ARCHITECTURE, 11.4а).
/// Только в сессии с раздельной симуляцией; иначе — как в игре.
/// </summary>
internal static class WorldPawnRandIsolation
{
    internal static bool Applies(Pawn pawn) =>
        PerMapSim.Enabled && LockstepClient.InSession && pawn.MapHeld == null && Find.WorldPawns.Contains(pawn);

    internal static int Seed(Pawn pawn) =>
        Gen.HashCombineInt(Gen.HashCombineInt(pawn.thingIDNumber, Find.TickManager.TicksGame), 0x5EED0A7);
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick_WorldPawnRand
{
    private static void Prefix(Thing __instance, out bool __state)
    {
        __state = __instance is Pawn pawn && WorldPawnRandIsolation.Applies(pawn);
        if (__state)
        {
            Rand.PushState(WorldPawnRandIsolation.Seed((Pawn)__instance));
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            Rand.PopState();
        }
    }
}

[HarmonyPatch(typeof(TendUtility), nameof(TendUtility.DoTend))]
internal static class Patch_TendUtility_DoTend_WorldPawnRand
{
    // Самолечение мировой пешки (WorldPawnsTick → ShouldAutoTendTo → DoTend без врача).
    private static void Prefix(Pawn doctor, Pawn patient, out bool __state)
    {
        __state = doctor == null && patient != null && WorldPawnRandIsolation.Applies(patient);
        if (__state)
        {
            Rand.PushState(WorldPawnRandIsolation.Seed(patient) ^ 0x7E4D);
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            Rand.PopState();
        }
    }
}
