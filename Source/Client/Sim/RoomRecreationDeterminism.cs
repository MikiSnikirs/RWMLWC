using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// T-076, третий механизм: перестройка регионов/комнат происходит в кадре (<c>Map.MapUpdate</c>)
/// и в тике (наш BeforeTick); при разном темпе кадров клиенты перестраивают в разные моменты.
/// Два следствия, оба видны в данных m2/m5/m8 (кэши крыш identical, температура расходится):
///
/// 1. <c>RoomTempTracker.RegenerateEqualizationData</c> (зовётся при пересоздании/изменении комнаты)
///    перемешивает <c>equalizeCells</c> через <c>GenList.Shuffle</c> — тот тратит текущий Rand.
///    В кадре это Rand «показа», в тике — Rand симуляции; момент перестройки разный → трата Rand
///    и порядок клеток разные. Обёртываем regeneration в фиксированное состояние Rand
///    (PushState/PopState): порядок детерминирован, Rand симуляции не тратится вовсе.
///    Туда же попадает <c>ResolveQueuedGenerateRoofs</c> — тоже детерминированно.
///
/// 2. Новая комната получает температуру наружного воздуха (<c>RoomTempTracker</c> ctor),
///    т.е. момент пересоздания сбрасывает температуру в разное время. Переносим температуру
///    через перестройку: до перестройки запоминаем температуру по клеткам, после — задаём
///    каждой комнате среднее по её клеткам (смешение старых комнат при изменении формы).
///    Момент перестройки перестаёт влиять на температуру.
///
/// 3. (24.11, данные s3) Данные выравнивания (<c>equalizeCells</c>, покрытие крышей) пересчитываются
///    только по уведомлениям, а часть уведомлений зависит от момента перестройки (например,
///    <c>RoofGrid.SetRoof</c> уведомляет комнату через <c>GetValidRegionAt_NoRebuild</c>: у клиента,
///    перестроившего регионы в кадре, уведомление проходит, у другого — нет). В s3 температура r44101
///    разошлась на первом же шаге выравнивания при одинаковой исходной температуре и соседях.
///    Поэтому в сессии перед каждым выравниванием (тик % 120 == 7) регионы достраиваются, данные
///    выравнивания всех комнат пересчитываются заново, а <c>cycleIndex</c> задаётся от номера тика:
///    данные становятся чистой функцией состояния карты в этот тик.
///
/// Вне сессии — ванильное поведение.
/// </summary>
internal static class RoomRecreationDeterminism
{
    /// <summary>Ключ noeqregen=1: выключить пересчёт данных выравнивания (контрольный прогон A/B).</summary>
    public static bool EqRegenDisabled;

    internal sealed class Carry
    {
        public float[] Temps = System.Array.Empty<float>();
        public HashSet<Room> OldRooms = new HashSet<Room>();
    }

    internal static readonly AccessTools.FieldRef<RoomTempTracker, List<IntVec3>> EqCells =
        AccessTools.FieldRefAccess<RoomTempTracker, List<IntVec3>>("equalizeCells");
    internal static readonly AccessTools.FieldRef<RoomTempTracker, int> CycleIndex =
        AccessTools.FieldRefAccess<RoomTempTracker, int>("cycleIndex");
    internal static readonly AccessTools.FieldRef<RoomTempTracker, float> NoRoof =
        AccessTools.FieldRefAccess<RoomTempTracker, float>("noRoofCoverage");
    internal static readonly AccessTools.FieldRef<RoomTempTracker, float> ThickRoof =
        AccessTools.FieldRefAccess<RoomTempTracker, float>("thickRoofCoverage");
    internal static readonly System.Action<RoomTempTracker> Regenerate =
        AccessTools.MethodDelegate<System.Action<RoomTempTracker>>(AccessTools.Method(typeof(RoomTempTracker), "RegenerateEqualizationData"));

    /// <summary>Хеш данных выравнивания для дампа комнат (порядок клеток учитывается).</summary>
    internal static string EqDigest(Map map, RoomTempTracker t)
    {
        List<IntVec3> cells = EqCells(t);
        ulong h = 1469598103934665603UL;
        foreach (IntVec3 c in cells)
        {
            h = (h ^ (ulong)map.cellIndices.CellToIndex(c)) * 1099511628211UL;
        }
        return $"eq={cells.Count}/{h:X16} ci={CycleIndex(t)} nr={NoRoof(t):R} tr={ThickRoof(t):R}";
    }

    internal static readonly ConditionalWeakTable<Map, Carry> Pending = new ConditionalWeakTable<Map, Carry>();
}

[HarmonyPatch(typeof(RoomTempTracker), "RegenerateEqualizationData")]
internal static class Patch_RoomTempTracker_Regenerate_Deterministic
{
    private static void Prefix()
    {
        if (LockstepClient.InSession)
        {
            Rand.PushState(0x5EED123);
        }
    }

    private static void Postfix()
    {
        if (LockstepClient.InSession)
        {
            Rand.PopState();
        }
    }
}

[HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
internal static class Patch_RegionAndRoomUpdater_TempCarryover
{
    private static readonly AccessTools.FieldRef<RegionAndRoomUpdater, Map> UpdaterMap =
        AccessTools.FieldRefAccess<RegionAndRoomUpdater, Map>("map");
    private static readonly AccessTools.FieldRef<RegionGrid, List<Room>> AllRoomsList =
        AccessTools.FieldRefAccess<RegionGrid, List<Room>>("allRooms");

    private static void Prefix(RegionAndRoomUpdater __instance)
    {
        if (!LockstepClient.InSession || !__instance.AnythingToRebuild)
        {
            return;
        }
        Map map = UpdaterMap(__instance);
        List<Room> rooms = AllRoomsList(map.regionGrid);
        if (rooms.Count == 0)
        {
            return;
        }
        var carry = new RoomRecreationDeterminism.Carry { Temps = new float[map.cellIndices.NumGridCells] };
        for (int i = 0; i < carry.Temps.Length; i++)
        {
            carry.Temps[i] = float.NaN;
        }
        foreach (Room r in rooms)
        {
            carry.OldRooms.Add(r);
            float t = r.Temperature;
            foreach (IntVec3 c in r.Cells)
            {
                carry.Temps[map.cellIndices.CellToIndex(c)] = t;
            }
        }
        RoomRecreationDeterminism.Pending.Remove(map);
        RoomRecreationDeterminism.Pending.Add(map, carry);
    }

    private static void Postfix(RegionAndRoomUpdater __instance)
    {
        if (!LockstepClient.InSession)
        {
            return;
        }
        Map map = UpdaterMap(__instance);
        if (!RoomRecreationDeterminism.Pending.TryGetValue(map, out RoomRecreationDeterminism.Carry? carry))
        {
            return;
        }
        RoomRecreationDeterminism.Pending.Remove(map);
        // Только новые комнаты: пересчёт среднего у уцелевших при каждой перестройке давал бы
        // дрейф float, зависящий от числа перестроек (а оно у клиентов разное: кадр против тика).
        // Клетки без старой температуры (бывшие стены) не учитываются.
        foreach (Room r in AllRoomsList(map.regionGrid))
        {
            if (carry.OldRooms.Contains(r))
            {
                continue;
            }
            double sum = 0d;
            int n = 0;
            foreach (IntVec3 c in r.Cells)
            {
                float t = carry.Temps[map.cellIndices.CellToIndex(c)];
                if (!float.IsNaN(t))
                {
                    sum += t;
                    n++;
                }
            }
            if (n > 0)
            {
                r.Temperature = (float)(sum / n);
            }
        }
    }
}
[HarmonyPatch(typeof(MapTemperature), nameof(MapTemperature.MapTemperatureTick))]
internal static class Patch_MapTemperature_EqDataDeterministic
{
    private static readonly AccessTools.FieldRef<MapTemperature, Map> TempMap =
        AccessTools.FieldRefAccess<MapTemperature, Map>("map");

    private static void Prefix(MapTemperature __instance)
    {
        if (!LockstepClient.InSession || RoomRecreationDeterminism.EqRegenDisabled)
        {
            return;
        }
        int ticks = Find.TickManager.TicksGame;
        if (ticks % 120 != 7 && !DebugSettings.fastEcology)
        {
            return;
        }
        Map map = TempMap(__instance);
        // Достроить регионы сейчас: иначе пересчёт клеток ниже запустит перестройку посреди обхода комнат.
        if (map.regionAndRoomUpdater.Enabled)
        {
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }
        IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
        int interval = ticks / 120;
        for (int i = 0; i < rooms.Count; i++)
        {
            RoomTempTracker t = rooms[i].TempTracker;
            RoomRecreationDeterminism.Regenerate(t);
            int count = RoomRecreationDeterminism.EqCells(t).Count;
            if (count > 0)
            {
                int perInterval = UnityEngine.Mathf.CeilToInt(count * 0.2f);
                RoomRecreationDeterminism.CycleIndex(t) = (int)((long)interval * perInterval % count);
            }
        }
    }
}
