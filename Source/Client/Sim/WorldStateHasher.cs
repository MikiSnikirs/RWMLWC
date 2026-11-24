using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Хеш уровня мира — то, что не попадает в хеши карт (T-073): список карт, объекты мира (поселения, караваны,
/// лагеря, места заданий) и глобальное состояние Rand. Шлётся серверу как хеш карты
/// <c>ProtocolIds.WorldMapId</c>; сервер сравнивает его так же, как хеш карты.
/// Без него карта, которая есть только у одного игрока (поселение основано только у него), ни с чем
/// не сравнивается, и сервер не видит расхождения (tst035c: ~24 000 тиков).
/// </summary>
public static class WorldStateHasher
{
    private static readonly List<WorldObject> tmp = new List<WorldObject>();

    public static ulong Compute()
    {
        Fnv64 h = Fnv64.Create();
        List<Map> maps = Find.Maps;
        h.Add(maps.Count);
        for (int i = 0; i < maps.Count; i++)
        {
            h.Add(maps[i].uniqueID);
            h.Add(maps[i].Tile.tileId);
        }

        tmp.Clear();
        tmp.AddRange(Find.WorldObjects.AllWorldObjects);
        tmp.Sort((a, b) => a.ID.CompareTo(b.ID));
        h.Add(tmp.Count);
        for (int i = 0; i < tmp.Count; i++)
        {
            WorldObject o = tmp[i];
            h.Add(o.ID);
            h.Add((int)o.def.shortHash);
            h.Add(o.Tile.tileId);
            h.Add(o.Tile.GetHashCode());
            h.Add(o.Faction?.loadID ?? -1);
            if (o is Caravan c)
            {
                h.Add(c.PawnsListForReading.Count);
                h.Add(c.pather.Moving ? c.pather.Destination.tileId : -1);
            }
        }
        tmp.Clear();

        // Колонии игроков: владельцы и соавторы.
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        if (colonies != null)
        {
            foreach (int v in colonies.HashValues())
            {
                h.Add(v);
            }
        }

        // Сказания (T-056): применяются событием карты у всех одинаково; при раздельном счёте без событий расходились.
        List<Tale> tales = Find.TaleManager.AllTalesListForReading;
        h.Add(tales.Count);
        for (int i = 0; i < tales.Count; i++)
        {
            h.Add(tales[i].id);
        }

        h.Add(MapRandContext.GlobalState);
        return h.Value;
    }
}
