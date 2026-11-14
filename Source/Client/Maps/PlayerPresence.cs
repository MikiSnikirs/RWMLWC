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
    public static bool BlocksThreats(Map map)
    {
        int owner = AbsenceModes.Component?.OwnerOf(map.uniqueID) ?? 0;
        return owner > 0 && !IsOnline(owner);
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
