using HarmonyLib;
using RimWorld;
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
    private static void Prefix()
    {
        // Точка «строго перед тиком»: здесь стартует тестовый прогон, заказанный при загрузке сейва.
        HashRecorder.BeforeTick();
        if (PerMapSim.Enabled)
        {
            MapRandContext.BeginTick();
            RandTracer.BeginTick();
            // Сценарий тестового прогона исполняется уже в контексте симуляции.
            TestAutomation.ApplyScenarioIfPending();
        }
    }

    private static void Finalizer()
    {
        if (MapRandContext.InTick)
        {
            MapRandContext.EndTick();
        }
    }
}

// Трассировка вызовов Rand для поиска источника расхождения. Value и Int — единственные
// два места в игре, где сдвигается счётчик генератора.
[HarmonyPatch(typeof(Rand), nameof(Rand.Value), MethodType.Getter)]
internal static class Patch_Rand_Value
{
    private static void Prefix()
    {
        if (RandTracer.Active)
        {
            RandTracer.Record();
        }
    }
}

[HarmonyPatch(typeof(Rand), nameof(Rand.Int), MethodType.Getter)]
internal static class Patch_Rand_Int
{
    private static void Prefix()
    {
        if (RandTracer.Active)
        {
            RandTracer.Record();
        }
    }
}

// Номера вещей по картам: вещь, созданная в тике карты, получает номер из счётчика этой карты.
// Вещи, созданные в глобальном контексте (рейд от рассказчика, мир) или вне тика, — из общего счётчика игры.
[HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextThingID))]
internal static class Patch_UniqueIDsManager_GetNextThingID
{
    private static bool Prefix(ref int __result)
    {
        if (!PerMapSim.Enabled || !MapRandContext.InTick)
        {
            return true;
        }
        Map? map = MapRandContext.Current;
        if (map == null)
        {
            return true;
        }
        MapSimState? state = MapSimState.For(map);
        if (state == null)
        {
            return true;
        }
        int id = state.TakeThingId();
        if (id < 0)
        {
            return true;
        }
        __result = id;
        return false;
    }
}
