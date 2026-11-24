using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// T-076, четвёртый механизм (данные m2/m8/s6/ig5): <c>ThingSetMaker_MarketValue</c> и
/// <c>ThingSetMaker_Nutrition</c> хранят поле <c>nextSeed</c>, которое задаётся <c>Rand.Int</c> в конструкторе —
/// при загрузке определений, то есть своё в каждом процессе, и в сейв не попадает. Генерация набора вещей
/// (добыча рейда <c>IncidentWorker_RaidEnemy.GenerateRaidLoot</c>, награды, посылки) идёт под этим зерном,
/// поэтому у клиентов разная добыча, а иногда и разное число вещей: в ig5 c2 сделал лишний
/// <c>MealSurvivalPack</c>, общий счётчик номеров вещей сдвинулся на 1, а на тике 20880 разошёлся хеш.
///
/// В сессии, в тике, зерно выводится из состояния, одинакового у всех клиентов и сохраняемого в сейв:
/// номер тика, порядковый номер генерации в этом тике и <c>WorldInfo.persistentRandomValue</c>.
/// Проверка <c>CanGenerateSub</c> перед генерацией получает то же зерно, что и сама генерация (как в ванили).
/// Вне сессии и вне тика — ванильное поведение.
/// </summary>
internal static class ThingSetMakerSeedDeterminism
{
    private static int counterTick = -1;
    private static int generatedThisTick;

    internal static bool Active => LockstepClient.InSession && MapRandContext.InTick && Current.Game != null;

    internal static int CurrentSeed()
    {
        int tick = Find.TickManager.TicksGame;
        if (tick != counterTick)
        {
            counterTick = tick;
            generatedThisTick = 0;
        }
        int world = Find.World?.info?.persistentRandomValue ?? 0;
        return Gen.HashCombineInt(Gen.HashCombineInt(world, tick), generatedThisTick);
    }

    internal static void AfterGenerate()
    {
        if (counterTick == Find.TickManager.TicksGame)
        {
            generatedThisTick++;
        }
    }
}

[HarmonyPatch]
internal static class Patch_ThingSetMaker_SeedDeterministic
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (System.Type t in new[] { typeof(ThingSetMaker_MarketValue), typeof(ThingSetMaker_Nutrition) })
        {
            yield return AccessTools.Method(t, "CanGenerateSub");
            yield return AccessTools.Method(t, "Generate", new[] { typeof(ThingSetMakerParams), typeof(System.Collections.Generic.List<Thing>) });
        }
    }

    private static void Prefix(ThingSetMaker __instance)
    {
        if (!ThingSetMakerSeedDeterminism.Active)
        {
            return;
        }
        Traverse.Create(__instance).Field("nextSeed").SetValue(ThingSetMakerSeedDeterminism.CurrentSeed());
    }

    private static void Postfix(System.Reflection.MethodBase __originalMethod)
    {
        if (ThingSetMakerSeedDeterminism.Active && __originalMethod.Name == "Generate")
        {
            ThingSetMakerSeedDeterminism.AfterGenerate();
        }
    }
}
