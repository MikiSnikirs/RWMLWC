using System.Collections.Generic;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>Хеш состояния карты, разбитый по подсистемам (T-010).</summary>
public struct MapStateHash
{
    public int thingCount;
    public int pawnCount;
    public ulong things;

    // Части хеша вещей по отдельным свойствам: показывают, какое именно свойство разошлось.
    public ulong thingIds;
    public ulong thingDefs;
    public ulong thingPos;
    public ulong thingRot;
    public ulong thingStack;
    public ulong thingHp;

    public ulong pawns;
    public ulong rand;
    public ulong total;

    public override string ToString() =>
        $"things={things:X16};pawns={pawns:X16};rand={rand:X16};total={total:X16};nThings={thingCount};nPawns={pawnCount}"
        + $";tId={thingIds:X16};tDef={thingDefs:X16};tPos={thingPos:X16};tRot={thingRot:X16};tStack={thingStack:X16};tHp={thingHp:X16}";
}

/// <summary>
/// Считает хеш состояния карты. Первая версия: вещи, пешки, состояние Rand.
/// Не входят: письма и сообщения (зависят от реального времени, см. docs/research/sim.md, 6а),
/// сетки (температура, газ, загрязнение), задания пешек целиком, лорды. Добавлять по мере надобности:
/// хеш по подсистемам для того и нужен, чтобы видеть, где началось расхождение.
/// </summary>
public static class MapStateHasher
{
    private static readonly List<Thing> tmpThings = new List<Thing>();
    private static readonly List<Pawn> tmpPawns = new List<Pawn>();

    public static MapStateHash Compute(Map map)
    {
        MapStateHash result = default;

        // Вещи: порядок в списках игры зависит от истории, поэтому сортируем по номеру.
        tmpThings.Clear();
        tmpThings.AddRange(map.listerThings.AllThings);
        tmpThings.Sort(CompareThings);
        Fnv64 th = Fnv64.Create();
        Fnv64 hId = Fnv64.Create();
        Fnv64 hDef = Fnv64.Create();
        Fnv64 hPos = Fnv64.Create();
        Fnv64 hRot = Fnv64.Create();
        Fnv64 hStack = Fnv64.Create();
        Fnv64 hHp = Fnv64.Create();
        for (int i = 0; i < tmpThings.Count; i++)
        {
            Thing t = tmpThings[i];
            IntVec3 pos = t.Position;
            int def = t.def.shortHash;
            int rot = StableRotation(t);
            int hp = t.HitPoints;
            th.Add(t.thingIDNumber);
            th.Add(def);
            th.Add(pos.x);
            th.Add(pos.z);
            th.Add(rot);
            th.Add(t.stackCount);
            th.Add(hp);
            hId.Add(t.thingIDNumber);
            hDef.Add(def);
            hPos.Add(pos.x);
            hPos.Add(pos.z);
            hRot.Add(rot);
            hStack.Add(t.stackCount);
            hHp.Add(hp);
        }
        result.thingCount = tmpThings.Count;
        result.things = th.Value;
        result.thingIds = hId.Value;
        result.thingDefs = hDef.Value;
        result.thingPos = hPos.Value;
        result.thingRot = hRot.Value;
        result.thingStack = hStack.Value;
        result.thingHp = hHp.Value;
        tmpThings.Clear();

        // Пешки: дополнительно то, что меняется каждый тик.
        tmpPawns.Clear();
        IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
        for (int i = 0; i < spawned.Count; i++)
        {
            tmpPawns.Add(spawned[i]);
        }
        tmpPawns.Sort(ComparePawns);
        Fnv64 ph = Fnv64.Create();
        for (int i = 0; i < tmpPawns.Count; i++)
        {
            Pawn p = tmpPawns[i];
            ph.Add(p.thingIDNumber);
            IntVec3 pos = p.Position;
            ph.Add(pos.x);
            ph.Add(pos.z);
            ph.Add(p.Dead);
            ph.Add(p.Downed);
            ph.Add(p.CurJobDef != null ? (int)p.CurJobDef.shortHash : 0);
            if (p.health != null)
            {
                ph.Add(p.health.hediffSet.hediffs.Count);
                ph.Add(p.health.summaryHealth.SummaryHealthPercent);
            }
            if (p.needs != null)
            {
                ph.Add(p.needs.food != null ? p.needs.food.CurLevel : -1f);
                ph.Add(p.needs.rest != null ? p.needs.rest.CurLevel : -1f);
                ph.Add(p.needs.mood != null ? p.needs.mood.CurLevel : -1f);
            }
        }
        result.pawnCount = tmpPawns.Count;
        result.pawns = ph.Value;
        tmpPawns.Clear();

        // Rand: своё состояние карты, если раздельная симуляция включена; иначе общее.
        MapSimState? sim = MapSimState.For(map);
        if (PerMapSim.Enabled && sim != null && sim.randInitialized)
        {
            result.rand = ReferenceEquals(MapRandContext.Current, map) ? MapRandContext.CurrentRawState : sim.randState;
        }
        else
        {
            result.rand = MapRandContext.GlobalState;
        }

        Fnv64 total = Fnv64.Create();
        total.Add(result.things);
        total.Add(result.pawns);
        total.Add(result.rand);
        result.total = total.Value;
        return result;
    }

    /// <summary>
    /// Построчный список вещей карты для сравнения двух запусков: номер;тип;x;z;поворот;стопка;прочность.
    /// </summary>
    public static void DumpThings(Map map, System.Text.StringBuilder sb)
    {
        tmpThings.Clear();
        tmpThings.AddRange(map.listerThings.AllThings);
        tmpThings.Sort(CompareThings);
        for (int i = 0; i < tmpThings.Count; i++)
        {
            Thing t = tmpThings[i];
            IntVec3 pos = t.Position;
            sb.Append(t.thingIDNumber).Append(';')
              .Append(t.def.defName).Append(';')
              .Append(pos.x).Append(';')
              .Append(pos.z).Append(';')
              .Append(StableRotation(t)).Append(';')
              .Append(t.stackCount).Append(';')
              .Append(t.HitPoints).Append('\n');
        }
        tmpThings.Clear();
    }

    // У типов с randomizeRotationOnSpawn (обломки камня и шлака) игра заново выбирает случайный поворот
    // при каждом появлении вещи, включая загрузку сейва, причём из Rand, засеянного системными часами.
    // Поворот таких вещей не является состоянием симуляции и в хеш не входит.
    //
    // Поворот пешек входит: при включённой раздельной симуляции он обновляется по тикам
    // (см. TickDrivenRotation), а не по кадрам, как в игре.
    internal static int StableRotation(Thing t) => t.def.randomizeRotationOnSpawn ? 0 : t.Rotation.AsInt;

    /// <summary>
    /// Диагностика: дописывает строки о вещах, у которых поворот изменился с прошлого вызова.
    /// Формат: тик;номер;тип;было;стало. Первый вызов только запоминает повороты.
    /// </summary>
    public static void LogRotationChanges(Map map, int tick, Dictionary<int, int> lastRot, System.Text.StringBuilder sb)
    {
        bool first = lastRot.Count == 0;
        tmpThings.Clear();
        tmpThings.AddRange(map.listerThings.AllThings);
        tmpThings.Sort(CompareThings);
        for (int i = 0; i < tmpThings.Count; i++)
        {
            Thing t = tmpThings[i];
            int rot = StableRotation(t);
            if (lastRot.TryGetValue(t.thingIDNumber, out int old))
            {
                if (old != rot)
                {
                    sb.Append(tick).Append(';').Append(t.thingIDNumber).Append(';').Append(t.def.defName)
                      .Append(';').Append(old).Append(';').Append(rot).Append('\n');
                }
            }
            else if (!first)
            {
                sb.Append(tick).Append(';').Append(t.thingIDNumber).Append(';').Append(t.def.defName)
                  .Append(";new;").Append(rot).Append('\n');
            }
            lastRot[t.thingIDNumber] = rot;
        }
        tmpThings.Clear();
    }

    private static int CompareThings(Thing a, Thing b) => a.thingIDNumber.CompareTo(b.thingIDNumber);

    private static int ComparePawns(Pawn a, Pawn b) => a.thingIDNumber.CompareTo(b.thingIDNumber);
}
