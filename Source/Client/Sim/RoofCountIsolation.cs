using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Подсчёт открытых крыш и вакуумных клеток принадлежит только симуляции (T-076, класс 6м).
///
/// <c>District.OpenRoofCountStopAt</c> считает открытые клетки <b>порциями</b>: вызов с малым
/// порогом (300 — <c>PsychologicallyOutdoors</c>, 101 — <c>OutdoorsForWork</c>) останавливает
/// перечислитель на середине списка клеток и сохраняет его (<c>cachedOpenRoofState</c>);
/// следующий вызов продолжает с того же места. <c>Room.OpenRoofCount</c> кэширует полный итог.
/// Интерфейс и отрисовка рисующего клиента зовут эти getters в кадре, второй клиент — нет.
///
/// Сброс кэша при изменении крыши теряется, когда регион клетки в этот момент невалиден:
/// <c>RoofGrid.SetRoof</c> уведомляет через <c>GetValidRegionAt_NoRebuild</c> (null → уведомления
/// нет). Так бывает при немедленном обрушении крыши после сноса стены в тике
/// (<c>RoofCollapseCellsFinder</c>): регион снесённой стены ещё не перестроен. Частичный подсчёт,
/// оставленный кадром, переживает потерю уведомления и даёт устаревший итог; у клиента без
/// отрисовки этой карты подсчёта не было — итог свежий. <c>CanMakeFilth</c> читает
/// <c>UsesOutdoorTemperature</c> и кладёт кровь в разные клетки (данные B: tstad12, tstrr3 —
/// при одинаковом Rand и вещах одна новая грязь в разных клетках).
///
/// Вне тика считаем заново и кэши не трогаем (как <c>StatCacheIsolation</c>): частичные
/// перечислители остаются только от тиков — одинаковых у всех клиентов. Потерянное уведомление
/// тогда даёт одинаковую устарелость у всех, а не разную.
/// </summary>
internal static class RoofCountIsolation
{
    public static bool Isolated => PerMapSim.Enabled && !(MapRandContext.InTick && VisualRandIsolation.Depth == 0);
}

[HarmonyPatch(typeof(District), "OpenRoofCountStopAt")]
internal static class Patch_District_OpenRoofCountStopAt_UiIsolation
{
    private static bool Prefix(District __instance, int threshold, ref int __result)
    {
        if (!RoofCountIsolation.Isolated)
        {
            return true;
        }
        int n = 0;
        if (threshold <= 0)
        {
            __result = 0;
            return false;
        }
        Map map = __instance.Map;
        foreach (IntVec3 c in __instance.Cells)
        {
            if (!map.roofGrid.Roofed(c) && ++n >= threshold)
            {
                break;
            }
        }
        __result = n;
        return false;
    }
}

[HarmonyPatch(typeof(District), "ExposedVacuumCount")]
internal static class Patch_District_ExposedVacuumCount_UiIsolation
{
    private static bool Prefix(District __instance, int threshold, ref int __result)
    {
        if (!RoofCountIsolation.Isolated)
        {
            return true;
        }
        int n = 0;
        if (threshold <= 0)
        {
            __result = 0;
            return false;
        }
        Map map = __instance.Map;
        foreach (IntVec3 c in __instance.Cells)
        {
            if ((!map.roofGrid.Roofed(c) || map.terrainGrid.TerrainAt(c).exposesToVacuum) && ++n >= threshold)
            {
                break;
            }
        }
        __result = n;
        return false;
    }
}

[HarmonyPatch(typeof(Room), "OpenRoofCountStopAt")]
internal static class Patch_Room_OpenRoofCountStopAt_UiIsolation
{
    private static bool Prefix(Room __instance, int threshold, ref int __result)
    {
        if (!RoofCountIsolation.Isolated)
        {
            return true;
        }
        int n = 0;
        foreach (District d in __instance.Districts)
        {
            n += d.OpenRoofCountStopAt(threshold - n);
            if (n >= threshold)
            {
                break;
            }
        }
        __result = n;
        return false;
    }
}

[HarmonyPatch(typeof(Room), "ExposedCountStopAt")]
internal static class Patch_Room_ExposedCountStopAt_UiIsolation
{
    private static bool Prefix(Room __instance, int threshold, ref int __result)
    {
        if (!RoofCountIsolation.Isolated)
        {
            return true;
        }
        int n = 0;
        foreach (District d in __instance.Districts)
        {
            n += d.ExposedVacuumCount(threshold - n);
            if (n >= threshold)
            {
                break;
            }
        }
        __result = n;
        return false;
    }
}

/// <summary>
/// Геттер <c>Room.OpenRoofCount</c> сам пишет итог в <c>cachedOpenRoofCount</c> — вне тика
/// отдаём свежий подсчёт без записи (метод <c>OpenRoofCountStopAt</c> уже изолирован выше).
/// </summary>
[HarmonyPatch(typeof(Room), nameof(Room.OpenRoofCount), MethodType.Getter)]
internal static class Patch_Room_OpenRoofCount_UiIsolation
{
    private static bool Prefix(Room __instance, ref int __result)
    {
        if (!RoofCountIsolation.Isolated)
        {
            return true;
        }
        int n = 0;
        foreach (District d in __instance.Districts)
        {
            n += d.OpenRoofCountStopAt(int.MaxValue);
        }
        __result = n;
        return false;
    }
}