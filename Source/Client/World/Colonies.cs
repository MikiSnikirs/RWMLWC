using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Колонии игроков (решение владельца 15.11): у каждого игрока не больше одной своей колонии.
/// Первая колония — хоста (тот, кто открыл сессию своим миром). Остальные игроки при входе выбирают:
/// присоединиться к чужой колонии соавтором или основать свою (новая стартовая группа на выбранной клетке).
/// Основать поселение может только игрок без своей колонии — так число поселений растёт с числом игроков.
///
/// Это состояние игры: лежит в сейве и меняется только командами из общего порядка, одинаково у всех.
/// </summary>
public sealed class ColoniesComponent : GameComponent
{
    // Поселение игрока (номер объекта мира) → номер игрока-владельца.
    private Dictionary<int, int> owners = new Dictionary<int, int>();

    // Игрок → поселение, к которому он присоединился соавтором.
    private Dictionary<int, int> members = new Dictionary<int, int>();

    private List<int>? tmpKeys;
    private List<int>? tmpValues;

    public ColoniesComponent(Game game)
    {
    }

    public bool AnyOwner => owners.Count > 0;

    public int OwnerOf(Settlement settlement) => owners.TryGetValue(settlement.ID, out int p) ? p : 0;

    /// <summary>Своё поселение игрока (существующее), иначе null.</summary>
    public Settlement? OwnedBy(int playerId)
    {
        foreach (KeyValuePair<int, int> kv in owners.OrderBy(kv => kv.Key))
        {
            if (kv.Value == playerId && Find(kv.Key) is Settlement s)
            {
                return s;
            }
        }
        return null;
    }

    public bool OwnsColony(int playerId) => OwnedBy(playerId) != null;

    /// <summary>Поселение, к которому игрок присоединился соавтором (существующее), иначе null.</summary>
    public Settlement? MemberOf(int playerId) => members.TryGetValue(playerId, out int id) ? Find(id) : null;

    /// <summary>Игрок уже определился: у него своя колония или он соавтор.</summary>
    public bool HasChosen(int playerId) => OwnsColony(playerId) || MemberOf(playerId) != null;

    /// <summary>Колонии с владельцами — для окна выбора.</summary>
    public List<(Settlement settlement, int owner)> OwnedColonies() =>
        owners.OrderBy(kv => kv.Key).Select(kv => (Find(kv.Key), kv.Value)).Where(x => x.Item1 != null).Select(x => (x.Item1!, x.Item2)).ToList();

    public void SetOwner(Settlement settlement, int playerId)
    {
        owners[settlement.ID] = playerId;
        members.Remove(playerId); // своя колония — больше не соавтор чужой
    }

    public void SetMember(int playerId, Settlement settlement) => members[playerId] = settlement.ID;

    /// <summary>Все поселения игрока без владельца — игроку (первая колония — хоста).</summary>
    public int ClaimUnowned(int playerId)
    {
        int n = 0;
        foreach (Settlement s in Verse.Find.WorldObjects.Settlements.Where(s => s.Faction == Faction.OfPlayer).OrderBy(s => s.ID))
        {
            if (!owners.ContainsKey(s.ID))
            {
                owners[s.ID] = playerId;
                n++;
            }
        }
        return n;
    }

    /// <summary>Для хеша мира: «поселение:владелец», «игрок@поселение».</summary>
    public IEnumerable<int> HashValues()
    {
        foreach (KeyValuePair<int, int> kv in owners.OrderBy(kv => kv.Key))
        {
            yield return kv.Key;
            yield return kv.Value;
        }
        yield return -1;
        foreach (KeyValuePair<int, int> kv in members.OrderBy(kv => kv.Key))
        {
            yield return kv.Key;
            yield return kv.Value;
        }
    }

    public string Summary() =>
        "owners=[" + string.Join(",", owners.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value)) + "] members=["
        + string.Join(",", members.OrderBy(kv => kv.Key).Select(kv => kv.Key + "@" + kv.Value)) + "]";

    private static Settlement? Find(int id) =>
        Verse.Find.WorldObjects.Settlements.FirstOrDefault(s => s.ID == id && s.Faction == Faction.OfPlayer);

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref owners, "rwmlwcColonyOwners", LookMode.Value, LookMode.Value, ref tmpKeys, ref tmpValues);
        Scribe_Collections.Look(ref members, "rwmlwcColonyMembers", LookMode.Value, LookMode.Value, ref tmpKeys, ref tmpValues);
        owners ??= new Dictionary<int, int>();
        members ??= new Dictionary<int, int>();
    }
}

public static class Colonies
{
    public static ColoniesComponent? Component => Current.Game?.GetComponent<ColoniesComponent>();

    /// <summary>Кто действует: автор исполняемой команды, иначе этот игрок (интерфейс).</summary>
    public static int Actor => CommandBus.Executing ? CommandBus.CurrentPlayerId : LockstepClient.PlayerId;
}

/// <summary>Новое поселение, основанное командой, — колония её автора.</summary>
[HarmonyPatch(typeof(SettleUtility), nameof(SettleUtility.AddNewHome))]
internal static class Patch_SettleUtility_AddNewHome_Owner
{
    private static void Postfix(Settlement __result)
    {
        if (!LockstepClient.InSession || !CommandBus.Executing || __result == null || Colonies.Component == null)
        {
            return;
        }
        Colonies.Component.SetOwner(__result, CommandBus.CurrentPlayerId);
        TestAutomation.Note($"colonies: settlement {__result.ID} belongs to player {CommandBus.CurrentPlayerId}");
    }
}

/// <summary>
/// «Лимит поселений достигнут» в сессии — у игрока уже есть своя колония. Так у кнопки «Основать поселение»
/// (и в интерфейсе, и при исполнении команды у всех) одно правило: по одной колонии на игрока.
/// Личная настройка Prefs.MaxNumberOfPlayerSettlements в сессии не действует.
/// </summary>
[HarmonyPatch(typeof(SettleUtility), nameof(SettleUtility.PlayerSettlementsCountLimitReached), MethodType.Getter)]
internal static class Patch_SettleUtility_LimitReached
{
    private static bool Prefix(ref bool __result)
    {
        if (!LockstepClient.InSession || Colonies.Component == null)
        {
            return true;
        }
        __result = Colonies.Component.OwnsColony(Colonies.Actor);
        return false;
    }
}

/// <summary>Текст выключенной кнопки «Основать»: у игрока уже есть своя колония.</summary>
[HarmonyPatch(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.SettleCommand))]
internal static class Patch_SettleCommand_Reason
{
    private static void Postfix(Command __result)
    {
        if (LockstepClient.InSession && __result.Disabled && Colonies.Component?.OwnsColony(Colonies.Actor) == true)
        {
            __result.disabledReason = "RWMLWC: у вас уже есть своя колония (одна на игрока)";
        }
    }
}
