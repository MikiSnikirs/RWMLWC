using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

// Поворот пешек по тикам (T-007).
//
// В игре поворот пешки пересчитывает Pawn_RotationTracker.UpdateRotation, вызываемый из
// ProcessPostTickVisuals — раз в кадр, из Map.MapUpdate. Поэтому поворот меняется на тик раньше
// или позже в зависимости от частоты кадров (найдено в TST-010: животные, сдвиг 1–4 тика).
//
// При включённой раздельной симуляции:
//  - покадровый пересчёт отключается;
//  - тот же UpdateRotation вызывается в конце тика каждой пешки.
// Сам расчёт не меняется, меняется только момент вызова.
//
// Остаточный риск: при взгляде на цель-вещь UpdateRotation берёт её DrawPos — сглаженную
// позицию отрисовки, которая для движущейся цели зависит от кадра. Не исправлено.

[HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.ProcessPostTickVisuals))]
internal static class Patch_RotationTracker_ProcessPostTickVisuals
{
    private static bool Prefix()
    {
        return !PerMapSim.Enabled;
    }
}

[HarmonyPatch(typeof(Pawn), "Tick")]
internal static class Patch_Pawn_Tick
{
    private static void Postfix(Pawn __instance)
    {
        if (PerMapSim.Enabled && __instance.Spawned && !__instance.Suspended)
        {
            __instance.rotationTracker.UpdateRotation();
        }
    }
}
