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
            uint seed = ((uint)map.uniqueID * 0x9E3779B1u) ^ PerMapSim.SessionSeed;
            return seed; // счётчик итераций = 0
        }
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref frozen, "rwmlwc_frozen", false);
        Scribe_Values.Look(ref randState, "rwmlwc_randState", 0UL);
        Scribe_Values.Look(ref randInitialized, "rwmlwc_randInitialized", false);
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
