using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Сколько тиков карта провела в стазисе (T-043). Локальные часы карты = мировое время минус это число:
    /// пока тикает эта карта, <c>TicksGame</c> возвращает локальное время (StasisClock), и таймеры, отсчитанные
    /// до стазиса, после разморозки не срабатывают разом.
    /// </summary>
    public int stasisTicks;

    /// <summary>Мировой абсолютный тик, когда карта последний раз вошла в стазис (для предупреждения о сезоне).</summary>
    public long stasisSinceAbs;

    /// <summary>
    /// Богатство карты для рассказчика, запомненное в момент ухода владельца (T-072), или -1. Пока владельца
    /// нет, очки угроз карты считаются от него (Maps/AbsentThreatPoints.cs): в автопилоте богатство растёт,
    /// а рассказчик не должен «отыграться» после возвращения.
    /// </summary>
    public float absentWealth = -1f;

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

    /// <summary>
    /// Счётчики прочих номеров, взятых в тике этой карты (задания пешек, ранения, записи журнала, сказания, письма…;
    /// T-056, Sim/PerMapIds.cs): ключ — имя метода <c>UniqueIDsManager.GetNext…ID</c>. Тот же блок, что у номеров вещей:
    /// пространства номеров разных видов не пересекаются, поэтому один блок на карту для всех видов не конфликтует.
    /// </summary>
    private Dictionary<string, int> idCounters = new Dictionary<string, int>();
    private List<string>? tmpIdKeys;
    private List<int>? tmpIdValues;

    /// <summary>Следующий номер вида <paramref name="kind"/> для этой карты или -1.</summary>
    public int TakeId(string kind)
    {
        int slot = map.uniqueID + 1;
        idCounters.TryGetValue(kind, out int next);
        if (slot < 1 || slot > MaxMapsWithOwnIds || next >= ThingIdBlock - 1)
        {
            return -1;
        }
        idCounters[kind] = next + 1;
        return slot * ThingIdBlock + next;
    }

    /// <summary>Для хеша и тестов: счётчики по видам в порядке имени.</summary>
    public string IdCountersDigest() => string.Join(",", idCounters.OrderBy(kv => kv.Key, System.StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

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
        Scribe_Values.Look(ref stasisTicks, "rwmlwc_stasisTicks", 0);
        Scribe_Values.Look(ref stasisSinceAbs, "rwmlwc_stasisSinceAbs", 0L);
        Scribe_Values.Look(ref randState, "rwmlwc_randState", 0UL);
        Scribe_Values.Look(ref randInitialized, "rwmlwc_randInitialized", false);
        Scribe_Values.Look(ref nextThingId, "rwmlwc_nextThingId", 0);
        Scribe_Values.Look(ref absentWealth, "rwmlwc_absentWealth", -1f);
        Scribe_Collections.Look(ref idCounters, "rwmlwc_idCounters", LookMode.Value, LookMode.Value, ref tmpIdKeys, ref tmpIdValues);
        idCounters ??= new Dictionary<string, int>();
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
