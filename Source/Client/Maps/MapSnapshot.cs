using System.IO;
using System.Text;
using System.Xml;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Снимок одной карты (T-030 этап 2, T-056 кирпич 1; docs/research/io.md, раздел 1).
///
/// Подмена карты в идущей игре оставила бы висеть ссылки извне на старые объекты карты: журнал бесед, сказания,
/// родственники среди мировых пешек, задания, письма (в сейве 14.11 — шесть видов узлов вне карты со ссылками
/// на её вещи). Поэтому карта не подменяется на месте, а «вшивается» в полный сейв: узел карты
/// <c>game/maps/li</c> с тем же <c>uniqueID</c> заменяется узлом из снимка владельца, и вся игра загружается
/// штатным загрузчиком (<see cref="GameSnapshot.Apply"/>). Ссылки с карты в мир и из мира в карту находятся
/// по номерам, как при обычной загрузке; работает с любыми модами.
///
/// Цена — полная перезагрузка у того, кто вшивает (этап 1 проекта это допускает: вход гостя останавливает игру).
/// </summary>
public static class MapSnapshot
{
    /// <summary>XML узла карты <paramref name="mapUniqueId"/> из полного сейва (несжатые байты) или null.</summary>
    public static string? ExtractMap(byte[] rawSave, int mapUniqueId)
    {
        XmlDocument doc = Load(rawSave);
        XmlNode? node = FindMap(doc, mapUniqueId);
        return node?.OuterXml;
    }

    /// <summary>
    /// Полный сейв <paramref name="rawSave"/>, в котором карта <paramref name="mapUniqueId"/> заменена на
    /// <paramref name="mapXml"/>. Если такой карты в сейве нет — null.
    /// </summary>
    public static byte[]? Splice(byte[] rawSave, int mapUniqueId, string mapXml)
    {
        XmlDocument doc = Load(rawSave);
        XmlNode? old = FindMap(doc, mapUniqueId);
        if (old?.ParentNode == null)
        {
            return null;
        }
        XmlDocument part = new XmlDocument();
        part.LoadXml(mapXml);
        XmlNode imported = doc.ImportNode(part.DocumentElement!, true);
        old.ParentNode.ReplaceChild(imported, old);
        using MemoryStream ms = new MemoryStream();
        using (XmlWriter w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            doc.Save(w);
        }
        return ms.ToArray();
    }

    private static XmlDocument Load(byte[] raw)
    {
        XmlDocument doc = new XmlDocument();
        using MemoryStream ms = new MemoryStream(raw);
        doc.Load(ms);
        return doc;
    }

    private static XmlNode? FindMap(XmlDocument doc, int mapUniqueId)
    {
        XmlNodeList? maps = doc.SelectNodes("/savegame/game/maps/li");
        if (maps == null)
        {
            return null;
        }
        string id = mapUniqueId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (XmlNode m in maps)
        {
            if (m["uniqueID"]?.InnerText == id)
            {
                return m;
            }
        }
        return null;
    }
}
