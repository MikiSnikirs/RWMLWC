using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

// Патчи раздельной симуляции карт (T-005). Точки выбраны по docs/research/sim.md:
//  - Thing.DoTick вызывается из общих списков тиков для вещей всех карт;
//  - Map.MapPreTick / Map.MapPostTick тикают подсистемы карты;
//  - TickList.Tick — конец прохода по списку, здесь возвращаем глобальный Rand.
// Harmony 3 строго сверяет типы: __instance объявлен точным типом класса.

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick
{
    private static bool Prefix(Thing __instance)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        Map? map = __instance.MapHeld;
        if (map != null)
        {
            MapSimState? state = MapSimState.For(map);
            if (state != null && state.frozen)
            {
                return false; // вещь замороженной карты не тикает
            }
        }
        MapRandContext.Enter(map); // вещи вне карт (караваны, мир) — глобальный контекст
        return true;
    }
}

[HarmonyPatch(typeof(TickList), nameof(TickList.Tick))]
internal static class Patch_TickList_Tick
{
    // Finalizer без возвращаемого значения не глотает исключения, а выполняется всегда.
    private static void Finalizer()
    {
        if (PerMapSim.Enabled)
        {
            MapRandContext.ExitToGlobal();
        }
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
internal static class Patch_Map_MapPreTick
{
    private static bool Prefix(Map __instance)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        if (PerMapSim.IsFrozen(__instance))
        {
            return false;
        }
        MapRandContext.Enter(__instance);
        return true;
    }

    private static void Finalizer()
    {
        if (PerMapSim.Enabled)
        {
            MapRandContext.ExitToGlobal();
        }
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
internal static class Patch_Map_MapPostTick
{
    private static bool Prefix(Map __instance)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        if (PerMapSim.IsFrozen(__instance))
        {
            return false;
        }
        MapRandContext.Enter(__instance);
        return true;
    }

    private static void Finalizer()
    {
        if (PerMapSim.Enabled)
        {
            MapRandContext.ExitToGlobal();
        }
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class Patch_TickManager_DoSingleTick
{
    // Точка «строго перед тиком»: здесь стартует тестовый прогон, заказанный при загрузке сейва.
    private static void Prefix()
    {
        HashRecorder.BeforeTick();
    }
}
