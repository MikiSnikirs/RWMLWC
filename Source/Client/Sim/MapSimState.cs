using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Состояние симуляции одной карты. Игра сама создаёт по экземпляру на каждую карту
/// (все неабстрактные наследники MapComponent) и сохраняет его вместе с картой.
/// </summary>
public sealed class MapSimState : MapComponent
{
    /// <summary>Карта заморожена: её вещи и подсистемы не тикают.</summary>
    public bool frozen;

    /// <summary>Собственное состояние Rand этой карты (seed в младших 32 битах, счётчик в старших).</summary>
    public ulong randState;

    /// <summary>Состояние Rand уже создано. До первого тика карты оно не определено.</summary>
    public bool randInitialized;

    /// <summary>
    /// Счётчик номеров вещей, созданных в тике этой карты. В игре счётчик номеров общий на все карты,
    /// а номер вещи задаёт фазу её периодических действий (HashOffset), поэтому события одной карты
    /// сдвигали бы поведение другой (найдено прогоном twomaps против twomaps-freeze).
    /// </summary>
    public int nextThingId;

    /// <summary>Номера карт не пересекаются между собой и с номерами из общего счётчика игры.</summary>
    public const int ThingIdBlock = 100_000_000;

    /// <summary>Сколько карт помещается в диапазон int при таком размере блока.</summary>
    public const int MaxMapsWithOwnIds = 20;

    /// <summary>Следующий номер вещи для этой карты или -1, если своих номеров у карты быть не может.</summary>
    public int TakeThingId()
    {
        int slot = map.uniqueID + 1;
        if (slot < 1 || slot > MaxMapsWithOwnIds || nextThingId >= ThingIdBlock - 1)
        {
            return -1;
        }
        int id = slot * ThingIdBlock + nextThingId;
        nextThingId++;
        return id;
    }

    // Кэш последнего поиска: вещи одной карты обычно идут в списке тиков подряд.
    private static Map? cachedMap;
    private static MapSimState? cachedState;

    public MapSimState(Map map) : base(map)
    {
    }

    public static MapSimState? For(Map map)
    {
        if (ReferenceEquals(map, cachedMap))
        {
            return cachedState;
        }
        MapSimState? state = map.GetComponent<MapSimState>();
        cachedMap = map;
        cachedState = state;
        return state;
    }

    /// <summary>Начальное состояние Rand карты: одинаково у всех клиентов с тем же SessionSeed.</summary>
    public ulong InitialRandState()
    {
        unchecked
        {
            // +1: у первой карты uniqueID = 0, без сдвига её seed был бы равен SessionSeed (в тесте — нулю).
            uint seed = (((uint)map.uniqueID + 1u) * 0x9E3779B1u) ^ PerMapSim.SessionSeed;
            return seed; // счётчик итераций = 0
        }
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref frozen, "rwmlwc_frozen", false);
        Scribe_Values.Look(ref randState, "rwmlwc_randState", 0UL);
        Scribe_Values.Look(ref randInitialized, "rwmlwc_randInitialized", false);
        Scribe_Values.Look(ref nextThingId, "rwmlwc_nextThingId", 0);
    }

    public override void MapRemoved()
    {
        MapRandContext.OnMapRemoved(map);
        if (ReferenceEquals(map, cachedMap))
        {
            cachedMap = null;
            cachedState = null;
        }
    }
}
