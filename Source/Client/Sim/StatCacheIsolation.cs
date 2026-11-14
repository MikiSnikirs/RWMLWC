using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Кэш характеристик «устаревает через N тиков» принадлежит только симуляции.
///
/// StatWorker.GetValue(thing, applyPostProcess, cacheStaleAfterTicks) кладёт посчитанное значение
/// в общий кэш (temporaryStatCache) с номером текущего тика, кто бы ни спрашивал. Симуляция читает
/// оттуда с допуском: Thing.MaxHitPoints (10 тиков), Need_Food.MaxLevel (15), MentalBreaker (5),
/// комфорт (100), температурный комфорт (1–10) и др. Интерфейс (полоска колонистов, панель
/// выделенной пешки, полоски здоровья) и хеш состояния читают то же самое в кадре — у каждого
/// игрока в свой тик. Тогда симуляция одного игрока получает значение, посчитанное в тик T,
/// а другого — в тик T+2; после ранений они разные (TST-021: расхождение после приказа атаки;
/// с плотными хешами расхождения не было, потому что хеш каждый тик одинаково обновлял кэш).
///
/// В сессии вне тика (и в изолированном визуальном коде внутри тика) значение считается заново
/// и в кэш не записывается. Неизменяемые характеристики (stat.immutable) не трогаем: их значение
/// не зависит от момента.
/// </summary>
[HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValue), new[] { typeof(Thing), typeof(bool), typeof(int) })]
internal static class Patch_StatWorker_GetValue_TemporaryCache
{
    private static bool Prefix(StatWorker __instance, StatDef ___stat, Thing thing, ref float __result)
    {
        if (!PerMapSim.Enabled || ___stat.immutable)
        {
            return true;
        }
        if (MapRandContext.InTick && VisualRandIsolation.Depth == 0)
        {
            return true; // симуляция: кэш как в игре
        }
        // Как в игре без кэша: GetValue(StatRequest.For(thing)) с applyPostProcess по умолчанию.
        __result = __instance.GetValue(StatRequest.For(thing));
        return false;
    }
}
