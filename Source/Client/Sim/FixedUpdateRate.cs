using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Частота «редких» обновлений вещи не должна зависеть от камеры.
///
/// Игра обновляет вещь двумя способами: Tick() каждый тик и TickInterval(delta) раз в несколько тиков.
/// Как часто — решает Thing.UpdateRateTicks → GenTicks.GetCameraUpdateRate: вещь в кадре камеры
/// обновляется раз в 1–5 тиков (по степени приближения), вне кадра или на другой карте — раз в 15.
/// TickInterval берёт числа из Rand (события «в среднем раз в N»), поэтому от того, куда и с каким
/// приближением смотрит игрок, зависит ход симуляции. У двух игроков камеры разные всегда; у одного
/// игрока камеру сдвигает тряска от взрывов, которая затухает по реальному времени.
/// См. docs/research/sim.md, 6з.
///
/// В режиме раздельной симуляции частота постоянна: как при максимальном приближении.
/// Здания (не чаще раза в 6 тиков) и животные (раз в 15) игра ограничивает сама, уже после этого значения.
/// </summary>
public static class FixedUpdateRate
{
    /// <summary>Вещи на картах: раз в столько тиков вызывается TickInterval.</summary>
    public const int Things = 1;

    /// <summary>Объекты мира: игра даёт 1, когда игрок смотрит на планету, и 15, когда на карту.</summary>
    public const int WorldObjects = 1;
}

[HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate))]
internal static class Patch_GenTicks_GetCameraUpdateRate
{
    private static bool Prefix(ref int __result)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        __result = FixedUpdateRate.Things;
        return false;
    }
}

// Снаряд спрашивает камеру сам, в обход GetCameraUpdateRate.
[HarmonyPatch(typeof(Projectile), nameof(Projectile.UpdateRateTicks), MethodType.Getter)]
internal static class Patch_Projectile_UpdateRateTicks
{
    private static bool Prefix(ref int __result)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        __result = FixedUpdateRate.Things;
        return false;
    }
}

[HarmonyPatch(typeof(WorldObject), "UpdateRateTicks", MethodType.Getter)]
internal static class Patch_WorldObject_UpdateRateTicks
{
    private static bool Prefix(ref int __result)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        __result = FixedUpdateRate.WorldObjects;
        return false;
    }
}
