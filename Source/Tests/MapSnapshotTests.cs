using System.Text;
using System.Xml;
using RWMLWC.Client.Maps;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-030, этап 2: вшивание узла карты в полный сейв (MapSnapshot) — без игры, на искусственном сейве.</summary>
public class MapSnapshotTests
{
    private static byte[] Save(string mapsInner, string extra = "") =>
        Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><savegame><meta><gameVersion>1.7</gameVersion></meta><game>"
            + "<world><x>1</x></world><maps>" + mapsInner + "</maps>" + extra + "</game></savegame>");

    private static string Map(int id, string body) => $"<li><uniqueID>{id}</uniqueID>{body}</li>";

    [Fact]
    public void ExtractFindsMapByUniqueId()
    {
        byte[] save = Save(Map(0, "<things>a</things>") + Map(3, "<things>b</things>"));
        string? xml = MapSnapshot.ExtractMap(save, 3);
        Assert.NotNull(xml);
        Assert.Contains("<things>b</things>", xml);
        Assert.Null(MapSnapshot.ExtractMap(save, 7));
    }

    [Fact]
    public void SpliceReplacesOnlyThatMapAndKeepsOrderAndWorld()
    {
        byte[] owner = Save(Map(0, "<things>old0</things>") + Map(1, "<things>FRESH</things><rand>9</rand>"));
        byte[] guest = Save(Map(0, "<things>mine</things>") + Map(1, "<things>stale</things>"), "<tickManager><ticksGame>500</ticksGame></tickManager>");
        string mapXml = MapSnapshot.ExtractMap(owner, 1)!;
        byte[]? composed = MapSnapshot.Splice(guest, 1, mapXml);
        Assert.NotNull(composed);

        XmlDocument doc = new XmlDocument();
        doc.LoadXml(Encoding.UTF8.GetString(composed!).TrimStart('﻿'));
        XmlNodeList maps = doc.SelectNodes("/savegame/game/maps/li")!;
        Assert.Equal(2, maps.Count);
        Assert.Equal("0", maps[0]!["uniqueID"]!.InnerText);
        Assert.Equal("mine", maps[0]!["things"]!.InnerText);       // своя карта гостя — как была
        Assert.Equal("1", maps[1]!["uniqueID"]!.InnerText);
        Assert.Equal("FRESH", maps[1]!["things"]!.InnerText);      // карта владельца — из его снимка
        Assert.Equal("9", maps[1]!["rand"]!.InnerText);
        Assert.Equal("500", doc.SelectSingleNode("/savegame/game/tickManager/ticksGame")!.InnerText); // мир — гостя
    }

    [Fact]
    public void SpliceOfMissingMapReturnsNull()
    {
        byte[] guest = Save(Map(0, "<things>mine</things>"));
        Assert.Null(MapSnapshot.Splice(guest, 5, Map(5, "<x/>")));
    }

    // T-056 (11.4б шаг 1): сейв без карт чужих колоний — заглушка без карты.

    [Fact]
    public void RemoveMapsDropsOnlyThoseAndKeepsOrderAndWorld()
    {
        byte[] save = Save(Map(0, "<things>a</things>") + Map(1, "<things>b</things>") + Map(2, "<things>c</things>"),
            "<currentMapIndex>1</currentMapIndex><tickManager><ticksGame>500</ticksGame></tickManager>");
        byte[] stripped = MapSnapshot.RemoveMaps(save, new[] { 1 }, currentMapId: 2);

        XmlDocument doc = new XmlDocument();
        doc.LoadXml(Encoding.UTF8.GetString(stripped).TrimStart('﻿'));
        XmlNodeList maps = doc.SelectNodes("/savegame/game/maps/li")!;
        Assert.Equal(2, maps.Count);
        Assert.Equal("0", maps[0]!["uniqueID"]!.InnerText);
        Assert.Equal("a", maps[0]!["things"]!.InnerText);
        Assert.Equal("2", maps[1]!["uniqueID"]!.InnerText);
        Assert.Equal("c", maps[1]!["things"]!.InnerText);
        Assert.Equal("1", doc.SelectSingleNode("/savegame/game/world/x")!.InnerText);
        Assert.Equal("500", doc.SelectSingleNode("/savegame/game/tickManager/ticksGame")!.InnerText);
        // currentMapIndex — индекс оставшейся карты 2 в новом списке, а не прежний индекс удалённой.
        Assert.Equal("1", doc.SelectSingleNode("/savegame/game/currentMapIndex")!.InnerText);
    }

    [Fact]
    public void RemoveMapsOfMissingIdsReturnsSameBytes()
    {
        byte[] save = Save(Map(0, "<x/>"));
        Assert.Same(save, MapSnapshot.RemoveMaps(save, new[] { 5 }));
    }

    [Fact]
    public void RemoveMapsPointsCurrentToFirstWhenKeepNotFound()
    {
        byte[] save = Save(Map(0, "<x/>") + Map(1, "<y/>"), "<currentMapIndex>1</currentMapIndex>");
        byte[] stripped = MapSnapshot.RemoveMaps(save, new[] { 1 }, currentMapId: 9);
        XmlDocument doc = new XmlDocument();
        doc.LoadXml(Encoding.UTF8.GetString(stripped).TrimStart('﻿'));
        Assert.Equal("0", doc.SelectSingleNode("/savegame/game/currentMapIndex")!.InnerText);
    }
}
