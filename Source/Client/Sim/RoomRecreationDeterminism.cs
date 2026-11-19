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
/// Вне сессии — ванильное поведение.
/// </summary>
internal static class RoomRecreationDeterminism
{
    internal sealed class Carry
    {
        public float[] Temps = System.Array.Empty<float>();
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
        foreach (Room r in rooms)
        {
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
        foreach (Room r in AllRoomsList(map.regionGrid))
        {
            float sum = 0f;
            int n = 0;
            foreach (IntVec3 c in r.Cells)
            {
                sum += carry.Temps[map.cellIndices.CellToIndex(c)];
                n++;
            }
            if (n > 0)
            {
                r.Temperature = sum / n;
            }
        }
    }
}