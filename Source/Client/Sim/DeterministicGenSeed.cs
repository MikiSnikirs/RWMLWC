using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// <c>Map.NextGenSeed</c> — зерно для частей генерации карты (разброс групп: руины, мусор, обломки —
/// <c>GenStep_ScatterGroup</c>). В игре оно считается через <c>System.HashCode.Combine</c>, а у него в .NET
/// случайное зерно в каждом процессе: одна и та же карта в двух копиях игры генерируется по-разному.
/// Найдено B (T-073, tst073b): карта поселения у двух клиентов совпадала до первого разброса группы,
/// а трассировка Rand разошлась в <c>GenStep_ScatterGroup.ScatterAt</c>.
///
/// Здесь то же самое — клетка мира (или номер карты), счётчик карты, зерно мира, — но через
/// детерминированный <see cref="Gen.HashCombineInt(int, int)"/>. Счётчик <c>generatedId</c> сохраняется в сейве,
/// как и в игре. Действует всегда: в одиночной игре значение и так было случайным.
/// </summary>
[HarmonyPatch(typeof(Map), nameof(Map.NextGenSeed), MethodType.Getter)]
internal static class Patch_Map_NextGenSeed
{
    private static readonly AccessTools.FieldRef<Map, int> generatedId = AccessTools.FieldRefAccess<Map, int>("generatedId");

    private static bool Prefix(Map __instance, ref int __result)
    {
        PlanetTile tile = __instance.TileInfo.tile;
        int place = tile.Valid ? tile.GetHashCode() : __instance.uniqueID;
        int n = generatedId(__instance)++;
        __result = Gen.HashCombineInt(Gen.HashCombineInt(place, n), Find.World.info.Seed);
        return false;
    }
}

/// <summary>
/// Остальные места игры, где <c>System.HashCode.Combine</c> (случайное зерно в каждом процессе) идёт в зерно Rand
/// симуляции или генерации. Вызов <c>HashCode.Combine(int, int)</c> заменяется на детерминированный
/// <see cref="Gen.HashCombineInt(int, int)"/>:
///  - <c>LayoutWorker.FillAllRooms</c> — наполнение комнат строений при генерации карты (древние комплексы и т. п.);
///  - <c>Building_Trap.KnowsOfTrap</c> — обходит ли рейдер ловушку (решается в тике).
/// Остальные вызовы <c>HashCode.Combine</c> в игре — ключи кэшей и <c>Log.WarningOnce</c>, на симуляцию не влияют
/// (поиск по всей декомпилированной сборке, 15.11).
/// </summary>
[HarmonyPatch]
internal static class Patch_HashCodeCombine_Seeds
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(LayoutWorker), "FillAllRooms");
        yield return AccessTools.Method(typeof(RimWorld.Building_Trap), nameof(RimWorld.Building_Trap.KnowsOfTrap));
    }

    private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions, System.Reflection.MethodBase __originalMethod)
    {
        System.Reflection.MethodInfo replacement = AccessTools.Method(typeof(Patch_HashCodeCombine_Seeds), nameof(Combine));
        int replaced = 0;
        foreach (CodeInstruction ci in instructions)
        {
            if ((ci.opcode == System.Reflection.Emit.OpCodes.Call) && ci.operand is System.Reflection.MethodInfo m
                && m.DeclaringType == typeof(System.HashCode) && m.Name == nameof(System.HashCode.Combine)
                && m.GetParameters().Length == 2 && m.GetGenericArguments() is { Length: 2 } g && g[0] == typeof(int) && g[1] == typeof(int))
            {
                ci.operand = replacement;
                replaced++;
            }
            yield return ci;
        }
        if (replaced == 0)
        {
            Log.Warning("[RWMLWC] HashCode.Combine не найден в " + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + " — детерминизм генерации не исправлен");
        }
    }

    private static int Combine(int a, int b) => Gen.HashCombineInt(a, b);
}

/// <summary><c>Gravship.ConstantRandSeed</c> — то же: <c>HashCode.Combine(ID, 18948292)</c> → детерминированно.</summary>
[HarmonyPatch(typeof(Gravship), nameof(Gravship.ConstantRandSeed), MethodType.Getter)]
internal static class Patch_Gravship_ConstantRandSeed
{
    private static bool Prefix(Gravship __instance, ref int __result)
    {
        __result = Gen.HashCombineInt(__instance.ID, 18948292);
        return false;
    }
}

/// <summary>
/// Шум скорости ветра карты засевается от <c>map.GetHashCode()</c>, а у <c>Map</c> своего <c>GetHashCode</c> нет —
/// это хеш объекта: свой в каждом процессе и у каждого нового объекта карты после загрузки. Скорость ветра
/// читают ветряки (выработка энергии), то есть симуляция. Вместо хеша объекта — номер карты (найдено B, T-076).
/// </summary>
[HarmonyPatch(typeof(WindManager), "BaseWindSpeedAt")]
internal static class Patch_WindManager_BaseWindSpeedAt_Seed
{
    private static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
    {
        System.Reflection.MethodInfo getHash = AccessTools.Method(typeof(object), nameof(GetHashCode));
        System.Reflection.MethodInfo stable = AccessTools.Method(typeof(Patch_WindManager_BaseWindSpeedAt_Seed), nameof(StableHash));
        int replaced = 0;
        foreach (CodeInstruction ci in instructions)
        {
            if ((ci.opcode == System.Reflection.Emit.OpCodes.Callvirt || ci.opcode == System.Reflection.Emit.OpCodes.Call) && ci.operand is System.Reflection.MethodInfo m && m == getHash)
            {
                ci.opcode = System.Reflection.Emit.OpCodes.Call;
                ci.operand = stable;
                replaced++;
            }
            yield return ci;
        }
        if (replaced == 0)
        {
            Log.Warning("[RWMLWC] WindManager.BaseWindSpeedAt: GetHashCode не найден — зерно ветра не исправлено");
        }
    }

    private static int StableHash(object o) => o is Map map ? map.uniqueID : o.GetHashCode();
}
