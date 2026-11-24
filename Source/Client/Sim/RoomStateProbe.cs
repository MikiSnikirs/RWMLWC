using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Диагностика T-076 (ключ метки <c>roomdump=1</c>): состояние комнат и районов против
/// свежесчитанного. Каждые 10 тиков — компактный снимок в кольцо (последние 30); по сообщению
/// сервера о расхождении — в файлы <c>rooms-&lt;тик&gt;.txt</c>.
///
/// Кэши (<c>Room.cachedOpenRoofCount</c>, <c>cachedCellCount</c>, частичный перечислитель района
/// <c>District.cachedOpenRoofState</c>) сравниваются со свежим подсчётом БЕЗ записи в кэш и без
/// вызова геттеров, которые кэш заполняют: зонд не меняет ход симуляции. Устаревший кэш виден
/// сразу (cached != fresh), а сравнение файлов двух клиентов показывает, какое состояние
/// разошлось первым. Комнаты и районы упорядочены по первой клетке — ID у процессов могут отличаться.
/// </summary>
internal static class RoomStateProbe
{
    public static bool On;

    private static readonly AccessTools.FieldRef<Room, int> RoomOpenRoof = AccessTools.FieldRefAccess<Room, int>("cachedOpenRoofCount");
    private static readonly AccessTools.FieldRef<Room, int> RoomCellCount = AccessTools.FieldRefAccess<Room, int>("cachedCellCount");
    private static readonly AccessTools.FieldRef<Room, bool> RoomDirty = AccessTools.FieldRefAccess<Room, bool>("statsAndRoleDirty");
    private static readonly AccessTools.FieldRef<Room, RoomRoleDef> RoomRole = AccessTools.FieldRefAccess<Room, RoomRoleDef>("role");
    private static readonly AccessTools.FieldRef<District, int> DistOpenRoof = AccessTools.FieldRefAccess<District, int>("cachedOpenRoofCount");
    private static readonly AccessTools.FieldRef<District, int> DistCellCount = AccessTools.FieldRefAccess<District, int>("cachedCellCount");
    private static readonly FieldInfo? DistRoofState = AccessTools.Field(typeof(District), "cachedOpenRoofState");
    private static readonly AccessTools.FieldRef<RegionGrid, List<District>> AllDistricts = AccessTools.FieldRefAccess<RegionGrid, List<District>>("allDistricts");

    private static readonly Queue<(int tick, string text)> Ring = new Queue<(int, string)>();

    public static void AfterTick()
    {
        if (!On || Current.Game == null || Find.Maps.Count == 0)
        {
            return;
        }
        int t = Find.TickManager.TicksGame;
        if (t % 10 != 0)
        {
            return;
        }
        try
        {
            Ring.Enqueue((t, Digest()));
            while (Ring.Count > 30)
            {
                Ring.Dequeue();
            }
        }
        catch (Exception e)
        {
            // Зонд не должен ломать игру: ошибка — в журнал автоматизации (Player.log общий у двух копий).
            TestAutomation.Note("roomdump: capture failed at tick " + t + ": " + e);
            On = false;
        }
    }

    /// <summary>Сервер сообщил о расхождении: записать кольцо снимков комнат.</summary>
    public static void OnDesyncNotice()
    {
        if (!On)
        {
            return;
        }
        try
        {
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
            Directory.CreateDirectory(dir);
            foreach ((int tick, string text) in Ring)
            {
                File.WriteAllText(Path.Combine(dir, $"rooms-{tick}.txt"), text);
            }
            File.WriteAllText(Path.Combine(dir, $"rooms-now-{Find.TickManager.TicksGame}.txt"), Digest());
            TestAutomation.Note($"roomdump: written {Ring.Count} snapshots");
            Ring.Clear();
        }
        catch (Exception e)
        {
            TestAutomation.Note("roomdump failed: " + e.Message);
        }
    }

    private static string Digest()
    {
        var sb = new StringBuilder();
        foreach (Map map in Find.Maps)
        {
            sb.Append("map ").Append(map.uniqueID).Append(" rand ").Append(MapSimState.For(map)?.randState.ToString("X16") ?? "-").AppendLine();
            foreach (Room r in DistrictsOf(map.regionGrid).Select(d => d.Room).OfType<Room>().Where(r => r.Map != null).Distinct().OrderBy(r => FirstCell(map, r.Cells)))
            {
                int first = FirstCell(map, r.Cells);
                int freshCells = r.Cells.Count();
                int freshRoof = FreshOpenRoof(map, r.Cells);
                bool edge = r.TouchesMapEdge;
                bool outdoor = edge || freshRoof >= UnityEngine.Mathf.CeilToInt(freshCells * 0.25f);
                sb.Append("r").Append(first)
                  .Append(" cc=").Append(RoomCellCount(r)).Append('/').Append(freshCells)
                  .Append(" ro=").Append(RoomOpenRoof(r)).Append('/').Append(freshRoof)
                  .Append(" edge=").Append(edge ? 1 : 0)
                  .Append(" out=").Append(outdoor ? 1 : 0)
                  .Append(" dirty=").Append(RoomDirty(r) ? 1 : 0)
                  .Append(" role=").Append(RoomRole(r)?.defName ?? "-")
                  .Append(" temp=").Append(r.Temperature.ToString("F3"))
                  .Append(' ').Append(RoomRecreationDeterminism.EqDigest(map, r.TempTracker))
                  .AppendLine();
            }
            foreach (District d in DistrictsOf(map.regionGrid).OrderBy(d => FirstCell(map, d.Cells)))
            {
                bool partial = DistRoofState?.GetValue(d) != null;
                sb.Append("d").Append(FirstCell(map, d.Cells))
                  .Append(" cc=").Append(DistCellCount(d))
                  .Append(" ro=").Append(DistOpenRoof(d))
                  .Append(" part=").Append(partial ? 1 : 0)
                  .Append(" edge=").Append(d.TouchesMapEdge ? 1 : 0)
                  .AppendLine();
            }
        }
        return sb.ToString();
    }

    private static List<District> DistrictsOf(RegionGrid grid) => AllDistricts(grid);

    private static int FirstCell(Map map, IEnumerable<IntVec3> cells)
    {
        foreach (IntVec3 c in cells)
        {
            return map.cellIndices.CellToIndex(c);
        }
        return -1;
    }

    /// <summary>Свежий подсчёт открытых клеток без записи в кэши игры.</summary>
    private static int FreshOpenRoof(Map map, IEnumerable<IntVec3> cells)
    {
        int n = 0;
        foreach (IntVec3 c in cells)
        {
            if (!map.roofGrid.Roofed(c))
            {
                n++;
            }
        }
        return n;
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class Patch_TickManager_DoSingleTick_RoomProbe
{
    private static void Postfix() => RoomStateProbe.AfterTick();
}