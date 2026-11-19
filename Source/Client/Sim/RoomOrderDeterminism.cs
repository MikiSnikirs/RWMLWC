using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Порядок списка комнат карты — детерминированный (T-076, второй механизм).
///
/// <c>RegionGrid.AllRooms</c> хранит комнаты в порядке создания. Перестройка регионов/комнат
/// (<c>RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms</c>) зовётся и в тике (наш BeforeTick),
/// и в кадре (<c>Map.MapUpdate</c> рисующей карты); при разном темпе кадров клиенты перестраивают
/// в разные моменты → порядок создания комнат разный. Симуляция обходит AllRooms и читает соседей,
/// обновлённых в том же проходе (температура — <c>MapTemperature.MapTemperatureTick</c>,
/// <c>GenTemperature</c>, вакуум, ИИ рейдов, медитация): разный порядок → разное накопление float
/// → расхождение (данные ab2: кэши крыш identical, температура комнаты 14.282 против 14.278).
///
/// После каждой перестройки сортируем список по минимальному индексу клетки комнаты — ключ не
/// зависит от порядка обхода районов/регионов и одинаков у клиентов с одинаковым миром.
/// Вне сессии — ванильный порядок.
/// </summary>
[HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
internal static class Patch_RegionAndRoomUpdater_RoomOrder
{
    private static readonly AccessTools.FieldRef<RegionAndRoomUpdater, Map> UpdaterMap =
        AccessTools.FieldRefAccess<RegionAndRoomUpdater, Map>("map");
    private static readonly AccessTools.FieldRef<RegionGrid, List<Room>> AllRoomsList =
        AccessTools.FieldRefAccess<RegionGrid, List<Room>>("allRooms");

    private static bool Prefix(RegionAndRoomUpdater __instance, ref bool __state)
    {
        __state = LockstepClient.InSession && __instance.AnythingToRebuild;
        return true;
    }

    private static void Postfix(RegionAndRoomUpdater __instance, bool __state)
    {
        if (!__state)
        {
            return;
        }
        Map map = UpdaterMap(__instance);
        List<Room> rooms = AllRoomsList(map.regionGrid);
        if (rooms.Count < 2)
        {
            return;
        }
        var keyed = new List<(int key, Room room)>(rooms.Count);
        foreach (Room r in rooms)
        {
            int min = int.MaxValue;
            foreach (IntVec3 c in r.Cells)
            {
                int idx = map.cellIndices.CellToIndex(c);
                if (idx < min)
                {
                    min = idx;
                }
            }
            keyed.Add((min, r));
        }
        keyed.Sort((a, b) => a.key.CompareTo(b.key));
        for (int i = 0; i < rooms.Count; i++)
        {
            rooms[i] = keyed[i].room;
        }
    }
}