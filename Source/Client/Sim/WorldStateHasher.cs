using System.Collections.Generic;
using System.Linq;
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

    private static readonly HarmonyLib.AccessTools.FieldRef<UniqueIDsManager, int> NextTaleId =
        HarmonyLib.AccessTools.FieldRefAccess<UniqueIDsManager, int>("nextTaleID");

    /// <summary>Подхеши частей мира для поиска расхождения (файл хешей, строка world): карты, объекты мира, колонии, сказания, Rand.</summary>
    public static string Parts()
    {
        Fnv64 m = Fnv64.Create();
        foreach (Map map in Find.Maps) { m.Add(map.uniqueID); m.Add(map.Tile.tileId); }
        Fnv64 o = Fnv64.Create();
        tmp.Clear();
        tmp.AddRange(Find.WorldObjects.AllWorldObjects);
        tmp.Sort((a, b) => a.ID.CompareTo(b.ID));
        foreach (WorldObject w in tmp)
        {
            o.Add(w.ID); o.Add((int)w.def.shortHash); o.Add(w.Tile.tileId); o.Add(w.Faction?.loadID ?? -1);
            if (w is Caravan c) { o.Add(c.PawnsListForReading.Count); o.Add(c.pather.Moving ? c.pather.Destination.tileId : -1); }
        }
        string objs = string.Join(",", tmp.Select(w => w.ID + ":" + w.def.defName + "@" + w.Tile.tileId));
        tmp.Clear();
        Fnv64 col = Fnv64.Create();
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        if (colonies != null) { foreach (int v in colonies.HashValues()) { col.Add(v); } }
        // T-056 (11.4б): номера сказаний входят в хеш мира; список — чтобы видеть, какой номер разошёлся.
        string taleIds = string.Join(",", Find.TaleManager.AllTalesListForReading.Select(t => t.id));
        return $"pMaps={m.Value:X16};pObjs={o.Value:X16};pCol={col.Value:X16};pRand={MapRandContext.GlobalState:X16};taleIds={taleIds};nextTale={NextTaleId(Find.UniqueIDsManager)};objs={objs}";
    }

    public static ulong Compute()
    {
        Fnv64 h = Fnv64.Create();
        // sleepremove (T-056, 11.4б): карт чужих колоний у игрока нет, список карт у каждого свой.
        // Поселения всё равно проверяются — они в объектах мира ниже. Список карт для диагностики — pMaps в Parts().
        if (!PerMapSim.SleepRemoveTest)
        {
            List<Map> maps = Find.Maps;
            h.Add(maps.Count);
            for (int i = 0; i < maps.Count; i++)
            {
                h.Add(maps[i].uniqueID);
                h.Add(maps[i].Tile.tileId);
            }
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
        // sleepremove (11.4б): жизненный цикл сказания у владельца и у спящего разный — у спящего ссылки на пешек
        // чужой карты null, и Notify_PawnDiscarded не убирает то, что у владельца убрано (sr7/sr8: тик 90250).
        // Влияние сказаний на симуляцию — выбор сюжета искусства (CompArt) — в этой схеме считает только владелец
        // карты, под Rand карты. Поэтому в хеш мира сказания не входят; синхронизация событием сохраняется,
        // список номеров — в диагностике Parts() (taleIds). Полный возврат сказаний в хеш — с событиями ухода
        // и смерти пешек (шаг 4 плана 11.4б).
        if (!PerMapSim.SleepRemoveTest)
        {
            List<Tale> tales = Find.TaleManager.AllTalesListForReading;
            h.Add(tales.Count);
            for (int i = 0; i < tales.Count; i++)
            {
                h.Add(tales[i].id);
            }
        }

        h.Add(MapRandContext.GlobalState);
        return h.Value;
    }
}
