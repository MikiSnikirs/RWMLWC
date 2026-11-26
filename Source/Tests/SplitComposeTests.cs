using System.Linq;
using System.Text;
using System.Xml;
using RWMLWC.Client.Sim;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>
/// TST-056/TST-057: сборка полного сейва из частей в режиме раздельного счёта колоний (SplitCompose) —
/// без игры, на искусственных сейвах того же устройства, что у игры.
/// </summary>
public class SplitComposeTests
{
    // Поселение 10 — игрок 1 (карта 0), поселение 11 — игрок 2 (карта 1).
    private const string Components =
        "<components>"
        + "<li Class=\"RWMLWC.Client.World.ColoniesComponent\"><rwmlwcColonyOwners><keys><li>10</li><li>11</li></keys><values><li>1</li><li>2</li></values></rwmlwcColonyOwners></li>"
        + "<li Class=\"RWMLWC.Client.Maps.PlayerPresenceComponent\"><rwmlwcPlayersOnline><keys><li>1</li><li>2</li></keys><values><li>True</li><li>{0}</li></values></rwmlwcPlayersOnline></li>"
        + "<li Class=\"RWMLWC.Client.Sim.SplitColoniesComponent\"><rwmlwcSplitColonies>True</rwmlwcSplitColonies><rwmlwcSplitVisitors>{1}</rwmlwcSplitVisitors></li>"
        + "</components>";

    private static string Map(int id, int parent, string things, int stasis = 0) =>
        $"<li><uniqueID>{id}</uniqueID><mapInfo><parent>WorldObject_{parent}</parent></mapInfo>"
        + "<components><li Class=\"RWMLWC.Client.Sim.MapSimState\">" + (stasis > 0 ? $"<rwmlwc_stasisTicks>{stasis}</rwmlwc_stasisTicks>" : "") + "</li></components>"
        + $"<things>{things}</things></li>";

    private static string Thing(string id) => $"<thing Class=\"Pawn\"><def>Human</def><id>{id}</id></thing>";

    private static XmlDocument Doc(int tick, string maps, string world, bool p2Online = true, string visitors = "")
    {
        string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><savegame><game>"
            + $"<tickManager><ticksGame>{tick}</ticksGame></tickManager>"
            + string.Format(Components, p2Online ? "True" : "False", visitors)
            + "<world><worldObjects><worldObjects><li Class=\"Settlement\"><ID>10</ID></li><li Class=\"Settlement\"><ID>11</ID></li></worldObjects></worldObjects>"
            + world + "</world>"
            + "<maps>" + maps + "</maps></game></savegame>";
        XmlDocument d = new XmlDocument();
        d.LoadXml(xml);
        return d;
    }

    private static string[] MapIds(XmlDocument d) => SplitCompose.Maps(d).Select(m => m["uniqueID"]!.InnerText).ToArray();

    [Fact]
    public void ReadsOwnersPresenceVisitorsAndParents()
    {
        XmlDocument d = Doc(100, Map(0, 10, "") + Map(1, 11, ""), "", p2Online: false, visitors: "<li>11:1</li>");
        Assert.Equal(2, SplitCompose.SettlementOwners(d)[11]);
        Assert.Contains(2, SplitCompose.OfflinePlayers(d));
        Assert.Contains("11:1", SplitCompose.Visitors(d));
        Assert.True(SplitCompose.ModeOn(d));
        Assert.Equal(new[] { (0, 10), (1, 11) }, SplitCompose.MapsWithParents(d).ToArray());
        Assert.Equal(100, SplitCompose.TicksOf(d));
    }

    [Fact]
    public void InsertsMissingMapInOrderAndRestoresNullWorldRefs()
    {
        // Отдающий (игрок 1): своей карты 0 нет в списке до карты 2 (стоянка), чужой карты 1 нет вовсе;
        // ссылки мира на пешку чужой колонии — null (не разрешились при загрузке без её карты).
        XmlDocument sender = Doc(500, Map(0, 10, Thing("Human1")) + Map(2, 99, ""),
            "<quest><pawns><li>Thing_Human1</li><li>null</li></pawns><map>null</map></quest>");
        XmlDocument owner2 = Doc(500, Map(1, 11, Thing("Human7")),
            "<quest><pawns><li>null</li><li>Thing_Human7</li></pawns><map>Map_1</map></quest>");
        string report = SplitCompose.InsertMissingMaps(sender, owner2, 500, fresh: true);
        Assert.Contains("inserted", report);
        Assert.Equal(new[] { "0", "1", "2" }, MapIds(sender));
        XmlNodeList refs = sender.SelectNodes("/savegame/game/world/quest/pawns/li")!;
        Assert.Equal("Thing_Human1", refs[0]!.InnerText); // своя ссылка отдающего не тронута
        Assert.Equal("Thing_Human7", refs[1]!.InnerText); // восстановлена из сейва владельца
        Assert.Equal("Map_1", sender.SelectSingleNode("/savegame/game/world/quest/map")!.InnerText);
        Assert.Equal(1, SplitCompose.InsertMissingMaps(sender, owner2, 500, fresh: true) == "nothing to insert" ? 1 : 0);
    }

    [Fact]
    public void RefsAreNotRestoredWhenListsDiffer()
    {
        // У отдающего список короче (null удалён при загрузке) — по позиции не совпадает, ссылку не трогаем.
        XmlDocument sender = Doc(500, Map(0, 10, ""), "<rel><li>null</li></rel>");
        XmlDocument owner2 = Doc(500, Map(1, 11, Thing("Human7")), "<rel><li>Thing_Other</li><li>Thing_Human7</li></rel>");
        SplitCompose.InsertMissingMaps(sender, owner2, 500, fresh: true);
        Assert.Equal("null", sender.SelectSingleNode("/savegame/game/world/rel/li")!.InnerText);
    }

    [Fact]
    public void OldCopyGetsStasisAndLosesThingsThatAreAlreadyElsewhere()
    {
        // Владелец 2 ушёл: его колония — из копии тика 300, сборка на тике 500. Пешка Human8 после копии ушла
        // с карты в мир (она уже есть среди мировых пешек) — из старой карты убирается.
        XmlDocument sender = Doc(500, Map(0, 10, ""), "<worldPawns><pawnsAlive>" + Thing("Human8") + "</pawnsAlive></worldPawns>", p2Online: false);
        XmlDocument copy = Doc(300, Map(1, 11, Thing("Human7") + Thing("Human8"), stasis: 40), "", p2Online: true);
        Assert.Equal(new[] { 2 }, SplitCompose.OwnersWithoutMap(sender).ToArray());
        string report = SplitCompose.InsertMissingMaps(sender, copy, 500, fresh: false, onlyOwner: 2);
        Assert.Contains("stasis +200", report);
        Assert.Contains("duplicates removed 1", report);
        XmlNode map1 = SplitCompose.Maps(sender).First(m => m["uniqueID"]!.InnerText == "1");
        Assert.Equal("240", map1.SelectSingleNode("components/li/rwmlwc_stasisTicks")!.InnerText);
        Assert.Equal(new[] { "Human7" }, map1.SelectNodes("things/thing/id")!.Cast<XmlNode>().Select(n => n.InnerText).ToArray());
        Assert.Empty(SplitCompose.OwnersWithoutMap(sender));
    }

    [Fact]
    public void StasisElementIsCreatedWhenAbsent()
    {
        XmlDocument sender = Doc(1000, Map(0, 10, ""), "", p2Online: false);
        XmlDocument copy = Doc(900, Map(1, 11, ""), "");
        SplitCompose.InsertMissingMaps(sender, copy, 1000, fresh: false, onlyOwner: 2);
        XmlNode map1 = SplitCompose.Maps(sender).First(m => m["uniqueID"]!.InnerText == "1");
        Assert.Equal("100", map1.SelectSingleNode("components/li/rwmlwc_stasisTicks")!.InnerText);
    }

    [Fact]
    public void SaveRoundTrips()
    {
        XmlDocument d = Doc(5, Map(0, 10, Thing("Human1")), "");
        XmlDocument back = SplitCompose.Load(SplitCompose.Save(d));
        Assert.Equal(MapIds(d), MapIds(back));
        Assert.Equal((byte)'<', SplitCompose.Save(d)[0]); // без BOM: загрузчик игры читает и с ним, но сравнения байтов проще
    }
}
