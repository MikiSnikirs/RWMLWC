using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace RWMLWC.Client.Sim;

/// <summary>Работа с XML полного сейва для сборки (без загрузки в игру).</summary>
public static class SplitCompose
{
    public static XmlDocument Load(byte[] raw)
    {
        XmlDocument doc = new XmlDocument();
        using MemoryStream ms = new MemoryStream(raw);
        doc.Load(ms);
        return doc;
    }

    public static byte[] Save(XmlDocument doc)
    {
        using MemoryStream ms = new MemoryStream();
        using (XmlWriter w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            doc.Save(w);
        }
        return ms.ToArray();
    }

    private static int Int(XmlNode? n, int fallback = 0) =>
        n != null && int.TryParse(n.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public static int TicksOf(XmlDocument doc) => Int(doc.SelectSingleNode("/savegame/game/tickManager/ticksGame"));

    public static bool ModeOn(XmlDocument doc) =>
        doc.SelectSingleNode("/savegame/game/components/li[@Class='RWMLWC.Client.Sim.SplitColoniesComponent']/rwmlwcSplitColonies")?.InnerText == "True";

    public static HashSet<string> Visitors(XmlDocument doc)
    {
        HashSet<string> set = new HashSet<string>();
        XmlNodeList? list = doc.SelectNodes("/savegame/game/components/li[@Class='RWMLWC.Client.Sim.SplitColoniesComponent']/rwmlwcSplitVisitors/li");
        if (list != null)
        {
            foreach (XmlNode n in list)
            {
                set.Add(n.InnerText);
            }
        }
        return set;
    }

    /// <summary>Номер поселения → владелец (ColoniesComponent в сейве).</summary>
    public static Dictionary<int, int> SettlementOwners(XmlDocument doc)
    {
        Dictionary<int, int> owners = new Dictionary<int, int>();
        XmlNode? comp = doc.SelectSingleNode("/savegame/game/components/li[@Class='RWMLWC.Client.World.ColoniesComponent']");
        if (comp == null)
        {
            return owners;
        }
        // Словарь Scribe: <owners><keys><li>…</li></keys><values><li>…</li></values></owners>
        foreach (XmlNode dict in comp.ChildNodes)
        {
            XmlNodeList? keys = dict.SelectNodes("keys/li");
            XmlNodeList? values = dict.SelectNodes("values/li");
            if (keys == null || values == null || keys.Count != values.Count || keys.Count == 0)
            {
                continue;
            }
            for (int i = 0; i < keys.Count; i++)
            {
                owners[Int(keys[i])] = Int(values[i]);
            }
            break; // первый словарь — владельцы
        }
        return owners;
    }

    /// <summary>Игроки «не в сети» по PlayerPresenceComponent в сейве.</summary>
    public static HashSet<int> OfflinePlayers(XmlDocument doc)
    {
        HashSet<int> off = new HashSet<int>();
        XmlNode? comp = doc.SelectSingleNode("/savegame/game/components/li[@Class='RWMLWC.Client.Maps.PlayerPresenceComponent']");
        XmlNode? dict = comp?.FirstChild;
        XmlNodeList? keys = dict?.SelectNodes("keys/li");
        XmlNodeList? values = dict?.SelectNodes("values/li");
        if (keys != null && values != null && keys.Count == values.Count)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (values[i]!.InnerText == "False")
                {
                    off.Add(Int(keys[i]));
                }
            }
        }
        return off;
    }

    public static IEnumerable<XmlNode> Maps(XmlDocument doc)
    {
        XmlNodeList? maps = doc.SelectNodes("/savegame/game/maps/li");
        return maps == null ? Enumerable.Empty<XmlNode>() : maps.Cast<XmlNode>();
    }

    /// <summary>Номер объекта мира родителя карты: <c>mapInfo/parent</c> = «WorldObject_123».</summary>
    public static int ParentId(XmlNode map)
    {
        string? p = map.SelectSingleNode("mapInfo/parent")?.InnerText;
        if (p == null)
        {
            return -1;
        }
        int us = p.LastIndexOf('_');
        return us >= 0 && int.TryParse(p.Substring(us + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1;
    }

    public static IEnumerable<(int mapId, int parentId)> MapsWithParents(XmlDocument doc) =>
        Maps(doc).Select(m => (Int(m["uniqueID"], -1), ParentId(m))).ToList();

    /// <summary>Владельцы колоний (по сейву), у чьих поселений нет карты в этом сейве.</summary>
    public static IEnumerable<int> OwnersWithoutMap(XmlDocument doc)
    {
        HashSet<int> withMap = new HashSet<int>(MapsWithParents(doc).Select(x => x.parentId));
        return SettlementOwners(doc).Where(kv => kv.Value != 0 && !withMap.Contains(kv.Key) && SettlementExists(doc, kv.Key))
            .Select(kv => kv.Value).Distinct().ToList();
    }

    private static bool SettlementExists(XmlDocument doc, int id)
    {
        XmlNodeList? objs = doc.SelectNodes("/savegame/game/world/worldObjects/worldObjects/li/ID");
        if (objs == null)
        {
            return true;
        }
        string s = id.ToString(CultureInfo.InvariantCulture);
        return objs.Cast<XmlNode>().Any(n => n.InnerText == s);
    }

    /// <summary>
    /// Вставить в <paramref name="doc"/> карты из <paramref name="part"/>, которых в нём нет (колонии другого игрока),
    /// и вернуть ссылки мира на их вещи. <paramref name="fresh"/> — копия того же тика; иначе старая копия: время
    /// между копией и сборкой засчитывается карте стазисом, а вещи, которые уже есть в сейве (ушли с карты после
    /// копии), из карты убираются. <paramref name="onlyOwner"/> — брать только карты колоний этого владельца.
    /// </summary>
    public static string InsertMissingMaps(XmlDocument doc, XmlDocument part, int tick, bool fresh, int onlyOwner = 0)
    {
        XmlNode? mapsNode = doc.SelectSingleNode("/savegame/game/maps");
        if (mapsNode == null)
        {
            return "no maps node";
        }
        HashSet<int> have = new HashSet<int>(MapsWithParents(doc).Select(x => x.mapId));
        Dictionary<int, int> owners = SettlementOwners(doc);
        int partTick = TicksOf(part);
        List<XmlNode> inserted = new List<XmlNode>();
        foreach (XmlNode m in Maps(part).ToList())
        {
            int id = Int(m["uniqueID"], -1);
            int parent = ParentId(m);
            int owner = owners.TryGetValue(parent, out int o) ? o : 0;
            if (have.Contains(id) || owner == 0 || onlyOwner != 0 && owner != onlyOwner)
            {
                continue;
            }
            XmlNode imported = doc.ImportNode(m, true);
            // По порядку номеров карт.
            XmlNode? before = Maps(doc).FirstOrDefault(x => Int(x["uniqueID"], -1) > id);
            if (before != null)
            {
                mapsNode.InsertBefore(imported, before);
            }
            else
            {
                mapsNode.AppendChild(imported);
            }
            inserted.Add(imported);
        }
        if (inserted.Count == 0)
        {
            return "nothing to insert";
        }
        int removedDup = 0;
        if (!fresh)
        {
            AddStasis(inserted, tick - partTick);
            removedDup = RemoveDuplicates(doc, inserted);
        }
        int refs = RestoreRefs(doc, part, inserted);
        return $"maps [{string.Join(",", inserted.Select(n => n["uniqueID"]?.InnerText))}] inserted, refs restored {refs}" +
               (fresh ? "" : $", stasis +{tick - partTick}, duplicates removed {removedDup}");
    }

    private static void AddStasis(List<XmlNode> maps, int delta)
    {
        if (delta <= 0)
        {
            return;
        }
        foreach (XmlNode m in maps)
        {
            XmlNode? comp = m.SelectSingleNode("components/li[@Class='RWMLWC.Client.Sim.MapSimState']");
            if (comp == null)
            {
                continue;
            }
            XmlNode? st = comp["rwmlwc_stasisTicks"];
            if (st == null)
            {
                st = comp.OwnerDocument!.CreateElement("rwmlwc_stasisTicks");
                st.InnerText = "0";
                comp.AppendChild(st);
            }
            st.InnerText = (Int(st) + delta).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Вещи вставленной старой копии, которые уже есть в сейве в другом месте (мировые пешки и т.п.), — убрать.</summary>
    private static int RemoveDuplicates(XmlDocument doc, List<XmlNode> inserted)
    {
        HashSet<XmlNode> insertedSet = new HashSet<XmlNode>(inserted);
        HashSet<string> elsewhere = new HashSet<string>();
        XmlNodeList? ids = doc.SelectNodes("//thing/id | //li[@Class]/id");
        if (ids != null)
        {
            foreach (XmlNode id in ids)
            {
                if (!Inside(id, insertedSet))
                {
                    elsewhere.Add(id.InnerText);
                }
            }
        }
        int removed = 0;
        foreach (XmlNode m in inserted)
        {
            XmlNodeList? things = m.SelectNodes("things/thing");
            if (things == null)
            {
                continue;
            }
            foreach (XmlNode t in things.Cast<XmlNode>().ToList())
            {
                string? id = t["id"]?.InnerText;
                if (id != null && elsewhere.Contains(id))
                {
                    t.ParentNode!.RemoveChild(t);
                    removed++;
                }
            }
        }
        return removed;
    }

    private static bool Inside(XmlNode n, HashSet<XmlNode> roots)
    {
        for (XmlNode? p = n.ParentNode; p != null; p = p.ParentNode)
        {
            if (roots.Contains(p))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Ссылки мира (вне карт) на вещи и карты вставленных колоний: у отдающего они записаны как «null» (при его
    /// загрузке без этих карт ссылки не разрешились). Берём их из сейва владельца по тому же пути в дереве, если
    /// родитель узла устроен одинаково (то же число детей) — иначе списки разошлись, и ссылку не трогаем.
    /// </summary>
    private static int RestoreRefs(XmlDocument doc, XmlDocument part, List<XmlNode> inserted)
    {
        HashSet<string> targets = new HashSet<string>();
        foreach (XmlNode m in inserted)
        {
            targets.Add("Map_" + m["uniqueID"]?.InnerText);
            XmlNodeList? ids = m.SelectNodes(".//id");
            if (ids != null)
            {
                foreach (XmlNode id in ids)
                {
                    targets.Add("Thing_" + id.InnerText);
                }
            }
        }
        XmlNode? partGame = part.SelectSingleNode("/savegame/game");
        XmlNode? docGame = doc.SelectSingleNode("/savegame/game");
        if (partGame == null || docGame == null)
        {
            return 0;
        }
        int restored = 0;
        foreach (XmlNode top in partGame.ChildNodes)
        {
            if (top.Name == "maps")
            {
                continue;
            }
            foreach (XmlNode leaf in Leaves(top))
            {
                if (!targets.Contains(leaf.InnerText))
                {
                    continue;
                }
                XmlNode? mine = Follow(docGame, PathOf(leaf, partGame));
                if (mine != null && mine.InnerText == "null" && mine.ParentNode != null && leaf.ParentNode != null
                    && mine.ParentNode.ChildNodes.Count == leaf.ParentNode.ChildNodes.Count)
                {
                    mine.InnerText = leaf.InnerText;
                    restored++;
                }
            }
        }
        return restored;
    }

    private static IEnumerable<XmlNode> Leaves(XmlNode root)
    {
        Stack<XmlNode> stack = new Stack<XmlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            XmlNode n = stack.Pop();
            bool hasElement = false;
            foreach (XmlNode c in n.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Element)
                {
                    hasElement = true;
                    stack.Push(c);
                }
            }
            if (!hasElement && n.NodeType == XmlNodeType.Element)
            {
                yield return n;
            }
        }
    }

    private static List<(string name, int index)> PathOf(XmlNode node, XmlNode root)
    {
        List<(string, int)> path = new List<(string, int)>();
        for (XmlNode? n = node; n != null && n != root; n = n.ParentNode)
        {
            int index = 0;
            for (XmlNode? s = n.PreviousSibling; s != null; s = s.PreviousSibling)
            {
                if (s.NodeType == XmlNodeType.Element && s.Name == n.Name)
                {
                    index++;
                }
            }
            path.Add((n.Name, index));
        }
        path.Reverse();
        return path;
    }

    private static XmlNode? Follow(XmlNode root, List<(string name, int index)> path)
    {
        XmlNode? cur = root;
        foreach ((string name, int index) in path)
        {
            XmlNode? next = null;
            int i = 0;
            foreach (XmlNode c in cur!.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Element && c.Name == name && i++ == index)
                {
                    next = c;
                    break;
                }
            }
            if (next == null)
            {
                return null;
            }
            cur = next;
        }
        return cur;
    }
}

