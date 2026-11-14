using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Камера глазами симуляции. Часть кода игры спрашивает камеру прямо из тика, и не только ради
/// картинки: «неестественный труп» перемещается, пока игрок на него не смотрит; существа Anomaly
/// появляются в клетках вне кадра; подземелье решает по приближению камеры, тянуть ли числа из Rand.
/// У каждого игрока камера своя, поэтому в тике и при исполнении команды камера для такого кода
/// одна и та же у всех: «никто никуда не смотрит» (кадр вне карты, максимальное отдаление).
///
/// Код, который только рисует и звучит (список VisualRandIsolation), видит настоящую камеру:
/// эффекты и крики животных остаются там, куда смотрит игрок.
///
/// Следствие для игры: «пока игрок не смотрит» в сетевой игре означает «всегда».
/// См. docs/research/sim.md, 6к.
/// </summary>
internal static class SimCamera
{
    public static bool Active => PerMapSim.Enabled && MapRandContext.InTick && VisualRandIsolation.Depth == 0;

    /// <summary>Кадр далеко за пределами любой карты: в нём нет ни одной клетки, даже после расширения.</summary>
    public static readonly CellRect NowhereRect = new CellRect(-100_000, -100_000, 0, 0);

    public const float FarthestRootSize = 60f;
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
internal static class Patch_CameraDriver_CurrentViewRect
{
    private static bool Prefix(ref CellRect __result)
    {
        if (!SimCamera.Active)
        {
            return true;
        }
        __result = SimCamera.NowhereRect;
        return false;
    }
}

[HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.ZoomRootSize), MethodType.Getter)]
internal static class Patch_CameraDriver_ZoomRootSize
{
    private static bool Prefix(ref float __result)
    {
        if (!SimCamera.Active)
        {
            return true;
        }
        __result = SimCamera.FarthestRootSize;
        return false;
    }
}
