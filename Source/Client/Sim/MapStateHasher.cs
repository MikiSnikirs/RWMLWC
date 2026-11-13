using System.Collections.Generic;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>Хеш состояния карты, разбитый по подсистемам (T-010).</summary>
public struct MapStateHash
{
    public int thingCount;
    public int pawnCount;
    public ulong things;
    public ulong pawns;
    public ulong rand;
    public ulong total;

    public override string ToString() =>
        $"things={things:X16};pawns={pawns:X16};rand={rand:X16};total={total:X16};nThings={thingCount};nPawns={pawnCount}";
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
        for (int i = 0; i < tmpThings.Count; i++)
        {
            Thing t = tmpThings[i];
            th.Add(t.thingIDNumber);
            th.Add((int)t.def.shortHash);
            IntVec3 pos = t.Position;
            th.Add(pos.x);
            th.Add(pos.z);
            th.Add(t.Rotation.AsInt);
            th.Add(t.stackCount);
            th.Add(t.HitPoints);
        }
        result.thingCount = tmpThings.Count;
        result.things = th.Value;
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

    private static int CompareThings(Thing a, Thing b) => a.thingIDNumber.CompareTo(b.thingIDNumber);

    private static int ComparePawns(Pawn a, Pawn b) => a.thingIDNumber.CompareTo(b.thingIDNumber);
}
