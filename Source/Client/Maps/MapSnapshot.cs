using System.IO;
using System.Linq;
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

    /// <summary>
    /// Полный сейв без карт <paramref name="mapUniqueIds"/> (T-056, вариант (б): чужой колонии у игрока
    /// нет — заглушка без карты). Узлы <c>maps/li</c> удаляются, мир не трогается. <c>currentMapIndex</c>
    /// указывает на оставшуюся карту <paramref name="currentMapId"/> (своя карта клиента), иначе на первую.
    /// Ссылки из мира на вещи удалённых карт при загрузке станут null — это измеряется прогоном шага 1.
    /// </summary>
    public static byte[] RemoveMaps(byte[] rawSave, System.Collections.Generic.IReadOnlyCollection<int> mapUniqueIds, int currentMapId = -1)
    {
        XmlDocument doc = Load(rawSave);
        XmlNodeList? maps = doc.SelectNodes("/savegame/game/maps/li");
        bool removed = false;
        if (maps != null)
        {
            for (int i = maps.Count - 1; i >= 0; i--)
            {
                XmlNode m = maps[i]!;
                if (int.TryParse(m["uniqueID"]?.InnerText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int id) && mapUniqueIds.Contains(id))
                {
                    m.ParentNode!.RemoveChild(m);
                    removed = true;
                }
            }
        }
        if (!removed)
        {
            return rawSave;
        }
        XmlNode? indexNode = doc.SelectSingleNode("/savegame/game/currentMapIndex");
        if (indexNode != null)
        {
            int index = 0;
            XmlNodeList? left = doc.SelectNodes("/savegame/game/maps/li");
            if (left != null && currentMapId >= 0)
            {
                for (int i = 0; i < left.Count; i++)
                {
                    if (left[i]!["uniqueID"]?.InnerText == currentMapId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    {
                        index = i;
                        break;
                    }
                }
            }
            indexNode.InnerText = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
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
