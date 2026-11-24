using System.Reflection;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// ID комнат, районов и регионов выдаются статическими счётчиками игры (<c>Room.nextRoomID</c>,
/// <c>District.nextDistrictID</c>, <c>Region.nextId</c>) и в сейв не сохраняются. От ID зависят
/// хеш-коды (<c>Room/District/Region.GetHashCode</c>), а симуляция обходит HashSet комнат и
/// регионов (<c>GenTemperature</c>, <c>PrisonBreakUtility</c>, <c>RegionAndRoomUpdater</c>).
/// Пока история клиентов общая, счётчики идут одинаково. После долгой одиночной игры одного
/// клиента его счётчики уходят вперёд; при загрузке снимка комнаты создаются заново и получают
/// разные ID в разных процессах — порядок обхода коллекций и симуляция расходятся, и общий
/// снимок это не лечит (T-076: расхождение после возвращения из долгого отсутствия).
///
/// Сброс счётчиков при каждой полной загрузке игры возвращает одинаковые ID: дальше история
/// у клиентов общая, счётчики снова идут одинаково. Карты, созданные в середине игры
/// (поселение, лагерь), не трогаем: там история у клиентов и так общая.
/// </summary>
internal static class DeterministicRegionIds
{
    private static readonly FieldInfo? NextRoomId = AccessTools.Field(typeof(Room), "nextRoomID");
    private static readonly FieldInfo? NextDistrictId = AccessTools.Field(typeof(District), "nextDistrictID");
    private static readonly FieldInfo? NextRegionId = AccessTools.Field(typeof(Region), "nextId");
    private static bool warned;

    /// <summary>
    /// Диагностика T-076 (ключ метки <c>bumpids=N</c>, применяется только у c2): сдвиг счётчиков
    /// при каждой загрузке — имитация «разной истории» без долгой одиночной игры. Если расхождение
    /// появляется со сдвигом и исчезает без него, причина — порядок обхода коллекций комнат.
    /// </summary>
    public static int TestBump;

    public static void Reset()
    {
        if (!PerMapSim.Enabled && TestBump == 0)
        {
            return; // одиночная игра без метки: поведение ванильное
        }
        if (NextRoomId == null || NextDistrictId == null || NextRegionId == null)
        {
            if (!warned)
            {
                warned = true;
                Log.Warning("[RWMLWC] DeterministicRegionIds: счётчики ID комнат/регионов не найдены — после загрузки снимка они могут отличаться у игроков.");
            }
            return;
        }
        NextRoomId.SetValue(null, TestBump);
        NextDistrictId.SetValue(null, TestBump);
        NextRegionId.SetValue(null, 1 + TestBump);
        TestAutomation.Note($"regionids: reset at load, bump={TestBump}");
    }

    public static string Digest()
    {
        return $"room={NextRoomId?.GetValue(null)} district={NextDistrictId?.GetValue(null)} region={NextRegionId?.GetValue(null)}";
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
internal static class Patch_Game_InitNewGame_RegionIds
{
    private static void Prefix() => DeterministicRegionIds.Reset();
}

[HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
internal static class Patch_Game_LoadGame_RegionIds
{
    private static void Prefix() => DeterministicRegionIds.Reset();

    private static void Postfix() => TestAutomation.Note("regionids: after load " + DeterministicRegionIds.Digest());
}