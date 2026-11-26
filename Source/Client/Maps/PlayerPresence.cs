using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Кто из игроков в сети — как состояние игры (T-072). Меняется только служебной командой сервера 102
/// (КОНТРАКТ v7) в общем порядке, поэтому одинаково у всех и лежит в сейве. Неизвестный игрок считается в сети.
/// </summary>
public sealed class PlayerPresenceComponent : GameComponent
{
    private Dictionary<int, bool> online = new Dictionary<int, bool>();
    private List<int>? keysWork;
    private List<bool>? valuesWork;

    public PlayerPresenceComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref online, "rwmlwcPlayersOnline", LookMode.Value, LookMode.Value, ref keysWork, ref valuesWork);
        online ??= new Dictionary<int, bool>();
    }

    /// <summary>Игроки, отмеченные «в сети» (по возрастанию номера).</summary>
    public System.Collections.Generic.IEnumerable<int> OnlinePlayers() => online.Where(p => p.Value).Select(p => p.Key).OrderBy(p => p).ToList();

    public bool IsOnline(int playerId) => !online.TryGetValue(playerId, out bool value) || value;

    public void Apply(int playerId, bool isOnline)
    {
        online[playerId] = isOnline;
        TestAutomation.Note($"presence: player {playerId} {(isOnline ? "online" : "offline")}");
    }

    /// <summary>Сводка: кто не в сети (в сети — значение по умолчанию; «в сети» до входа клиента он мог не получить).</summary>
    public string Digest() => string.Join(",", online.Where(p => !p.Value).OrderBy(p => p.Key).Select(p => p.Key));
}

public static class PlayerPresence
{
    public static PlayerPresenceComponent? Component => Current.Game?.GetComponent<PlayerPresenceComponent>();

    public static bool IsOnline(int playerId) => Component?.IsOnline(playerId) ?? true;

    /// <summary>
    /// Владелец карты (тот, кто выбрал ей режим отсутствия) не в сети: крупные угрозы на эту карту не приходят
    /// ни в одном режиме (docs/ARCHITECTURE.md, раздел 4). Карта без выбранного режима — без запрета: кто её
    /// владелец, знает только сервер, а решение должно быть одинаковым у всех.
    /// </summary>
    public static bool BlocksThreats(Map map) => OwnerAbsent(map);

    /// <summary>
    /// Колонии карты сейчас нет в игре: её владелец не в сети (владелец колонии, иначе тот, кто выбирал режим
    /// отсутствия) и никто из её соавторов не в сети. Одно правило для запрета угроз, заморозки адаптации
    /// и стазиса. Владелец неизвестен — колония считается в игре.
    /// </summary>
    public static bool OwnerAbsent(Map map)
    {
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        RimWorld.Planet.Settlement? settlement = map.Parent as RimWorld.Planet.Settlement;
        int owner = settlement != null ? colonies?.OwnerOf(settlement) ?? 0 : 0;
        if (owner == 0)
        {
            owner = AbsenceModes.Component?.OwnerOf(map.uniqueID) ?? 0;
        }
        if (owner == 0 || IsOnline(owner))
        {
            return false;
        }
        return settlement == null || colonies == null || !colonies.MembersOf(settlement).Any(IsOnline);
    }
}

/// <summary>Рассказчик не запускает крупную угрозу на карту отсутствующего владельца (в тике, одинаково у всех).</summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
internal static class Patch_Storyteller_TryFire_AbsentOwner
{
    private static bool Prefix(FiringIncident fi, ref bool __result)
    {
        if (fi?.def == null || fi.def.category != IncidentCategoryDefOf.ThreatBig || !(fi.parms?.target is Map map) || !PlayerPresence.BlocksThreats(map))
        {
            return true;
        }
        TestAutomation.Note($"presence: {fi.def.defName} on map {map.uniqueID} skipped — owner {AbsenceModes.Component?.OwnerOf(map.uniqueID)} is offline");
        __result = false;
        return false;
    }
}

/// <summary>
/// То же для крупных угроз мимо рассказчика (T-072): рейды из заданий (<c>QuestPart_Incident</c>,
/// <c>QuestPart_RandomRaid</c>), сигналов, мест и способностей идут через <c>IncidentWorker.TryExecute</c>.
/// На карту отсутствующего владельца такая угроза не исполняется; задание получает «не вышло» (false).
/// Решение B на время отсутствия A (docs/ARCHITECTURE.md, раздел 4: «ни в одном режиме»).
/// </summary>
[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
internal static class Patch_IncidentWorker_TryExecute_AbsentOwner
{
    private static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
    {
        IncidentDef? def = __instance.def;
        if (def == null || def.category != IncidentCategoryDefOf.ThreatBig || !(parms?.target is Map map) || !PlayerPresence.BlocksThreats(map))
        {
            return true;
        }
        TestAutomation.Note($"presence: {def.defName} (not from storyteller) on map {map.uniqueID} skipped — owner {AbsenceModes.Component?.OwnerOf(map.uniqueID)} is offline");
        __result = false;
        return false;
    }
}
