using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Тестовые прогоны (ключ метки <c>nodeath=1</c>): колонисты игрока не умирают — вместо смерти все ранения
/// и болезни снимаются. Иначе долгие проверки теряли колонистов от рейдов и голода, и сравнивать было нечего
/// (просьба владельца 15.11). Намеренная гибель в проверке адаптации разрешается <see cref="AllowNextKill"/>.
/// Ключ одинаков у всех клиентов прогона, решение — внутри тика: у всех одинаково.
/// </summary>
public static class TestSafety
{
    public static bool AllowNextKill;

    public static bool Active => CaravanSelfTest.ArmKey("nodeath=1");
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
internal static class Patch_Pawn_Kill_TestSafety
{
    private static bool Prefix(Pawn __instance)
    {
        if (TestSafety.AllowNextKill)
        {
            TestSafety.AllowNextKill = false;
            return true;
        }
        if (!__instance.IsColonist || __instance.Dead || !TestSafety.Active)
        {
            return true;
        }
        __instance.health.RemoveAllHediffs();
        TestAutomation.Note($"test safety: colonist {__instance.thingIDNumber} would have died — healed instead");
        return false;
    }
}
