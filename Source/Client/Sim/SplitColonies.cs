using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Net;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Раздельный счёт колоний (T-056, вариант (б) «заглушка» как настоящий режим; T-057 — гости).
///
/// Кто держит карту колонии (и считает её):
///  - владелец в сети — только владелец и гости этой колонии (<see cref="SplitColoniesComponent.visitors"/>);
///  - владелец не в сети — все, как раньше: колония в стазисе или на автопилоте (Maps/AbsenceModes.cs);
///  - карта без владельца (стоянка, место задания) — все.
/// У остальных колонии нет вовсе: объект мира (поселение) есть, карты нет («заглушка»).
///
/// Набор карт меняется только общей перезагрузкой (вход игрока, лечение расхождения, включение режима,
/// приход и уход гостя, уход владельца): каждый клиент загружает один и тот же полный сейв и вырезает из него
/// то, что держать не должен (<see cref="StripForeignMaps"/>). Полного сейва при этом нет ни у кого — он
/// собирается из частей (<see cref="SplitCompose"/>): каждый владелец в сети шлёт свой сейв кусками снимка с
/// особым <c>MapId</c> (сервер пересылает такие куски всем, протокол не меняется), отдающий мир вшивает в свой
/// сейв недостающие карты и восстанавливает ссылки мира на их вещи. Так же собирается автосохранение для сервера:
/// неполный мир на сервер не уходит.
/// </summary>
public sealed class SplitColoniesComponent : GameComponent
{
    /// <summary>Режим включён (общий для всех, в сейве).</summary>
    public bool enabled;

    /// <summary>Гости: «номер поселения:игрок» — игрок держит карту этой чужой колонии и считает её с владельцем.</summary>
    public List<string> visitors = new List<string>();

    public SplitColoniesComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref enabled, "rwmlwcSplitColonies", false);
        Scribe_Collections.Look(ref visitors, "rwmlwcSplitVisitors", LookMode.Value);
        visitors ??= new List<string>();
    }

    public bool Visits(int settlementId, int playerId) => visitors.Contains(Key(settlementId, playerId));

    public IEnumerable<int> VisitorsOf(int settlementId)
    {
        string prefix = settlementId.ToString(CultureInfo.InvariantCulture) + ":";
        foreach (string v in visitors)
        {
            if (v.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(v.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
            {
                yield return p;
            }
        }
    }

    internal static string Key(int settlementId, int playerId) =>
        settlementId.ToString(CultureInfo.InvariantCulture) + ":" + playerId.ToString(CultureInfo.InvariantCulture);
}

public static class SplitColonies
{
    public const byte OpMode = 52;
    public const byte OpVisit = 53;

    /// <summary><c>MapId</c> кусков «сейв владельца для сборки»: <c>BundleMapBase - игрок</c>.</summary>
    public const int BundleMapBase = -1000;

    /// <summary>Номер кусков сборки для круга <paramref name="roundId"/> (снимок или автосохранение).</summary>
    public static int BundleId(int roundId) => roundId | 0x40000000;

    public static SplitColoniesComponent? Component => Current.Game?.GetComponent<SplitColoniesComponent>();

    public static bool ModeOn => Component?.enabled ?? false;

    // ---------- кто держит карту ----------

    /// <summary>Поселение колонии игрока и её владелец (0 — не колония игрока).</summary>
    private static int OwnerOfMapParent(MapParent? parent)
    {
        if (parent is not Settlement st)
        {
            return 0;
        }
        return RWMLWC.Client.World.Colonies.Component?.OwnerOf(st) ?? 0;
    }

    /// <summary>
    /// Держит ли игрок <paramref name="player"/> карту поселения <paramref name="parent"/>. <paramref name="extraOnline"/> —
    /// игрок, который только входит (его «в сети» ещё может не дойти командой до общего порядка).
    /// </summary>
    public static bool Holds(int player, MapParent? parent, int extraOnline = 0)
    {
        int owner = OwnerOfMapParent(parent);
        if (owner == 0 || owner == player)
        {
            return true;
        }
        bool ownerOnline = owner == extraOnline || RWMLWC.Client.Maps.PlayerPresence.IsOnline(owner);
        if (!ownerOnline)
        {
            return true;
        }
        return Component?.Visits(parent!.ID, player) ?? false;
    }

    // ---------- снимок к загрузке: вырезать чужое ----------

    /// <summary>Игрок, ради которого идёт текущая перезагрузка (входит или пришёл в гости); 0 — нет.</summary>
    internal static int RoundPlayer;

    /// <summary>Последние полные сейвы других игроков (куски сборки) — для вшивания колонии ушедшего владельца.</summary>
    private static readonly Dictionary<int, (byte[] packed, int tick)> lastBundles = new Dictionary<int, (byte[], int)>();

    /// <summary>Полный сейв последней загрузки: из него берётся колония, если её свежей копии нет.</summary>
    private static (byte[] packed, int tick) lastLoaded;

    public static byte[] StripForeignMaps(byte[] packed)
    {
        if (!PerMapSim.Split)
        {
            return packed;
        }
        byte[] raw = RWMLWC.Client.Maps.GameSnapshot.Unpack(packed);
        XmlDocument doc = SplitCompose.Load(raw);
        int tick = SplitCompose.TicksOf(doc);
        lastLoaded = (packed, tick);
        // Решение «держать карту» — по состоянию игры в снимке (владельцы, гости, кто в сети): у всех одно и то же.
        Dictionary<int, int> owners = SplitCompose.SettlementOwners(doc);
        HashSet<int> online = SplitCompose.OfflinePlayers(doc);
        HashSet<string> visitors = SplitCompose.Visitors(doc);
        bool modeOn = PerMapSim.SleepRemoveTest || SplitCompose.ModeOn(doc);
        if (!modeOn)
        {
            return packed;
        }
        int me = LockstepClient.PlayerId;
        List<int> remove = new List<int>();
        int keep = -1;
        int visitKeep = -1;
        foreach ((int mapId, int parentId) in SplitCompose.MapsWithParents(doc))
        {
            int owner = owners.TryGetValue(parentId, out int o) ? o : 0;
            bool ownerOnline = owner != 0 && (owner == RoundPlayer || !online.Contains(owner));
            bool holds = owner == 0 || owner == me || !ownerOnline || visitors.Contains(SplitColoniesComponent.Key(parentId, me));
            if (!holds)
            {
                remove.Add(mapId);
            }
            else if (owner == me && keep < 0)
            {
                keep = mapId;
            }
            else if (owner != me && owner != 0 && visitors.Contains(SplitColoniesComponent.Key(parentId, me)) && RoundPlayer == me)
            {
                visitKeep = mapId; // пришёл в гости — смотреть на чужую колонию
            }
        }
        if (visitKeep >= 0)
        {
            keep = visitKeep;
        }
        if (remove.Count == 0)
        {
            return packed;
        }
        byte[] stripped = RWMLWC.Client.Maps.MapSnapshot.RemoveMaps(raw, remove, keep);
        TestAutomation.Note($"split: player {me} loads without maps [{string.Join(",", remove)}], keeps map {keep} (round player {RoundPlayer})");
        return RWMLWC.Client.Maps.GameSnapshot.Pack(stripped);
    }

    // ---------- сборка полного сейва ----------

    /// <summary>
    /// Игроки, чей сейв нужен отдающему мир для сборки: владельцы в сети (кроме входящего и себя), у чьих колоний
    /// здесь нет карты.
    /// </summary>
    internal static HashSet<int> ExpectedBundles(int roundPlayer)
    {
        HashSet<int> need = new HashSet<int>();
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        if (colonies == null)
        {
            return need;
        }
        foreach (Settlement st in Find.WorldObjects.Settlements)
        {
            int owner = colonies.OwnerOf(st);
            if (owner == 0 || owner == LockstepClient.PlayerId || owner == roundPlayer || st.HasMap)
            {
                continue;
            }
            if (RWMLWC.Client.Maps.PlayerPresence.IsOnline(owner))
            {
                need.Add(owner);
            }
        }
        return need;
    }

    /// <summary>Должен ли этот клиент послать свой сейв для сборки: у него есть карта своей колонии.</summary>
    internal static bool ShouldSendBundle()
    {
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        return colonies != null && Find.Maps.Any(m => m.Parent is Settlement st && colonies.OwnerOf(st) == LockstepClient.PlayerId);
    }

    private static readonly Dictionary<int, Dictionary<int, byte[]?[]>> incomingBundles = new Dictionary<int, Dictionary<int, byte[]?[]>>();
    private static readonly Dictionary<int, Dictionary<int, byte[]>> completeBundles = new Dictionary<int, Dictionary<int, byte[]>>();

    internal static void OnBundleChunk(SnapshotChunkMessage chunk)
    {
        int player = BundleMapBase - chunk.MapId;
        if (!incomingBundles.TryGetValue(chunk.SnapshotId, out Dictionary<int, byte[]?[]>? byPlayer))
        {
            byPlayer = new Dictionary<int, byte[]?[]>();
            incomingBundles[chunk.SnapshotId] = byPlayer;
            while (incomingBundles.Count > 4)
            {
                incomingBundles.Remove(incomingBundles.Keys.Min());
            }
        }
        if (!byPlayer.TryGetValue(player, out byte[]?[]? parts) || parts.Length != chunk.ChunkCount)
        {
            parts = new byte[]?[chunk.ChunkCount];
            byPlayer[player] = parts;
        }
        parts[chunk.ChunkIndex] = chunk.Data;
        if (parts.Any(p => p == null))
        {
            return;
        }
        byte[] data = parts.SelectMany(p => p!).ToArray();
        byPlayer.Remove(player);
        if (!completeBundles.TryGetValue(chunk.SnapshotId, out Dictionary<int, byte[]>? done))
        {
            done = new Dictionary<int, byte[]>();
            completeBundles[chunk.SnapshotId] = done;
            while (completeBundles.Count > 4)
            {
                completeBundles.Remove(completeBundles.Keys.Min());
            }
        }
        done[player] = data;
        lastBundles[player] = (data, chunk.Tick);
        TestAutomation.Note($"split: bundle {chunk.SnapshotId} from player {player} received, {data.Length} bytes, tick {chunk.Tick}");
    }

    internal static Dictionary<int, byte[]> BundlesFor(int bundleId) =>
        completeBundles.TryGetValue(bundleId, out Dictionary<int, byte[]>? d) ? d : new Dictionary<int, byte[]>();

    internal static void ForgetBundles(int bundleId) => completeBundles.Remove(bundleId);

    /// <summary>Сейв для сборки: снимок всей игры этого клиента, куски с <c>MapId = BundleMapBase - я</c>.</summary>
    internal static void SendBundle(IConnection conn, int bundleId, int tick)
    {
        byte[] packed = RWMLWC.Client.Maps.GameSnapshot.Take(out RWMLWC.Client.Maps.GameSnapshot.Stats stats);
        const int size = 200_000;
        int count = Math.Max(1, (packed.Length + size - 1) / size);
        for (int i = 0; i < count; i++)
        {
            int offset = i * size;
            int length = Math.Min(size, packed.Length - offset);
            byte[] part = new byte[length];
            Buffer.BlockCopy(packed, offset, part, 0, length);
            conn.Send(new SnapshotChunkMessage
            {
                SnapshotId = bundleId,
                MapId = BundleMapBase - LockstepClient.PlayerId,
                Tick = tick,
                ChunkIndex = i,
                ChunkCount = count,
                Data = part,
            });
        }
        TestAutomation.Note($"split: bundle {bundleId} sent at tick {tick}: {stats}, {count} chunks");
    }

    /// <summary>
    /// Собрать полный сейв: свой снимок <paramref name="ownPacked"/> + недостающие карты из сейвов других игроков
    /// (<paramref name="bundles"/>) + колонии ушедших владельцев из последних известных копий.
    /// </summary>
    internal static byte[] Compose(byte[] ownPacked, Dictionary<int, byte[]> bundles, int tick, out string report)
    {
        XmlDocument doc = SplitCompose.Load(RWMLWC.Client.Maps.GameSnapshot.Unpack(ownPacked));
        List<string> notes = new List<string>();
        foreach (KeyValuePair<int, byte[]> b in bundles.OrderBy(b => b.Key))
        {
            XmlDocument part = SplitCompose.Load(RWMLWC.Client.Maps.GameSnapshot.Unpack(b.Value));
            notes.Add($"player {b.Key}: " + SplitCompose.InsertMissingMaps(doc, part, tick, fresh: true));
        }
        // Колонии без карты в собранном сейве — владелец ушёл, свежей копии нет: последняя известная копия.
        foreach (int owner in SplitCompose.OwnersWithoutMap(doc).OrderBy(x => x))
        {
            (byte[] packed, int copyTick) src = lastBundles.TryGetValue(owner, out (byte[] packed, int tick) lb) ? lb : lastLoaded;
            if (src.packed == null)
            {
                notes.Add($"player {owner}: NO COPY of the colony");
                continue;
            }
            XmlDocument part = SplitCompose.Load(RWMLWC.Client.Maps.GameSnapshot.Unpack(src.packed));
            notes.Add($"player {owner} (copy of tick {src.copyTick}): " + SplitCompose.InsertMissingMaps(doc, part, tick, fresh: false, onlyOwner: owner));
        }
        report = string.Join("; ", notes);
        return RWMLWC.Client.Maps.GameSnapshot.Pack(SplitCompose.Save(doc));
    }

    /// <summary>Есть ли в игре колония, чьей карты здесь нет и которую никто в сети не держит (владелец ушёл).</summary>
    internal static bool OrphanColonyPresent()
    {
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        if (colonies == null)
        {
            return false;
        }
        foreach (Settlement st in Find.WorldObjects.Settlements)
        {
            int owner = colonies.OwnerOf(st);
            if (owner != 0 && !st.HasMap && !RWMLWC.Client.Maps.PlayerPresence.IsOnline(owner))
            {
                return true;
            }
        }
        return false;
    }

    // ---------- команды: режим и гости ----------

    public static void SendMode(bool on) => WorldCommands.SendOp(OpMode, w => w.WriteBool(on));

    public static void SendVisit(Settlement st, bool enter) => WorldCommands.SendOp(OpVisit, w => { w.WriteInt32(st.ID); w.WriteBool(enter); });

    public static void Execute(byte op, ref PacketReader r)
    {
        SplitColoniesComponent? c = Component;
        if (c == null)
        {
            return;
        }
        int player = CommandBus.CurrentPlayerId;
        if (op == OpMode)
        {
            bool on = r.ReadBool();
            if (c.enabled == on)
            {
                return;
            }
            c.enabled = on;
            if (!on)
            {
                c.visitors.Clear();
            }
            TestAutomation.Note($"split: mode {(on ? "ON" : "OFF")} by player {player} at tick {Find.TickManager.TicksGame}");
            if (player == LockstepClient.PlayerId)
            {
                RoundPlayerRequest = player;
                LockstepClient.RequestCommonSnapshot();
            }
            return;
        }
        if (op == OpVisit)
        {
            int settlementId = r.ReadInt32();
            bool enter = r.ReadBool();
            Settlement? st = Find.WorldObjects.Settlements.FirstOrDefault(s => s.ID == settlementId);
            int owner = st == null ? 0 : RWMLWC.Client.World.Colonies.Component?.OwnerOf(st) ?? 0;
            if (st == null || owner == 0 || owner == player || !c.enabled && !PerMapSim.SleepRemoveTest)
            {
                return;
            }
            string key = SplitColoniesComponent.Key(settlementId, player);
            bool changed = enter ? !c.visitors.Contains(key) : c.visitors.Remove(key);
            if (enter && changed)
            {
                c.visitors.Add(key);
            }
            TestAutomation.Note($"split: player {player} {(enter ? "visits" : "leaves")} colony {st.LabelCap} (settlement {settlementId}, owner {owner}) at tick {Find.TickManager.TicksGame}, changed={changed}");
            if (changed && player == LockstepClient.PlayerId)
            {
                RoundPlayerRequest = player;
                LockstepClient.RequestCommonSnapshot();
            }
        }
    }

    /// <summary>Кто попросил перезагрузку ради себя (гость, включение режима) — у этого клиента.</summary>
    internal static int RoundPlayerRequest;

    /// <summary>
    /// Игрок ушёл (команда «не в сети», у всех в одном месте порядка): если его колонии здесь нет, её надо вшить
    /// из последней копии — общая перезагрузка. Просит младший в сети.
    /// </summary>
    internal static void OnPresence(int playerId, bool online)
    {
        if (!PerMapSim.Split || Current.Game == null)
        {
            return;
        }
        SplitColoniesComponent? c = Component;
        if (!online && c != null)
        {
            // Гость ушёл — его посещения снимаются.
            c.visitors.RemoveAll(v => v.EndsWith(":" + playerId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        if (online || !OrphanColonyPresent())
        {
            return;
        }
        int lowest = QuestSync.ReporterOfWorldPublic();
        TestAutomation.Note($"split: player {playerId} went offline at tick {Find.TickManager.TicksGame}, its colony has no holder; reload requested by player {lowest}");
        if (lowest == LockstepClient.PlayerId)
        {
            LockstepClient.RequestCommonSnapshot();
        }
    }
}

/// <summary>Работа с XML полного сейва для сборки (без загрузки в игру).</summary>
internal static class SplitCompose
{
    internal static XmlDocument Load(byte[] raw)
    {
        XmlDocument doc = new XmlDocument();
        using MemoryStream ms = new MemoryStream(raw);
        doc.Load(ms);
        return doc;
    }

    internal static byte[] Save(XmlDocument doc)
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

    internal static int TicksOf(XmlDocument doc) => Int(doc.SelectSingleNode("/savegame/game/tickManager/ticksGame"));

    internal static bool ModeOn(XmlDocument doc) =>
        doc.SelectSingleNode("/savegame/game/components/li[@Class='RWMLWC.Client.Sim.SplitColoniesComponent']/rwmlwcSplitColonies")?.InnerText == "True";

    internal static HashSet<string> Visitors(XmlDocument doc)
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
    internal static Dictionary<int, int> SettlementOwners(XmlDocument doc)
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
    internal static HashSet<int> OfflinePlayers(XmlDocument doc)
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

    internal static IEnumerable<XmlNode> Maps(XmlDocument doc)
    {
        XmlNodeList? maps = doc.SelectNodes("/savegame/game/maps/li");
        return maps == null ? Enumerable.Empty<XmlNode>() : maps.Cast<XmlNode>();
    }

    /// <summary>Номер объекта мира родителя карты: <c>mapInfo/parent</c> = «WorldObject_123».</summary>
    internal static int ParentId(XmlNode map)
    {
        string? p = map.SelectSingleNode("mapInfo/parent")?.InnerText;
        if (p == null)
        {
            return -1;
        }
        int us = p.LastIndexOf('_');
        return us >= 0 && int.TryParse(p.Substring(us + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1;
    }

    internal static IEnumerable<(int mapId, int parentId)> MapsWithParents(XmlDocument doc) =>
        Maps(doc).Select(m => (Int(m["uniqueID"], -1), ParentId(m))).ToList();

    /// <summary>Владельцы колоний (по сейву), у чьих поселений нет карты в этом сейве.</summary>
    internal static IEnumerable<int> OwnersWithoutMap(XmlDocument doc)
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
    internal static string InsertMissingMaps(XmlDocument doc, XmlDocument part, int tick, bool fresh, int onlyOwner = 0)
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

/// <summary>
/// Прибытие к чужой колонии, карты которой здесь нет (режим раздельного счёта): игра сгенерировала бы новую карту
/// поверх существующего поселения. Не создавать — у держателей карты (владелец, гости) всё идёт как обычно; у прочих
/// поселение без карты возвращает null, и действие прибытия не исполняется.
/// </summary>
[HarmonyPatch(typeof(GetOrGenerateMapUtility), nameof(GetOrGenerateMapUtility.GetOrGenerateMap), new[] { typeof(PlanetTile), typeof(IntVec3), typeof(WorldObjectDef), typeof(IEnumerable<GenStepWithParams>), typeof(bool) })]
internal static class Patch_GetOrGenerateMap_Split
{
    private static bool Prefix(PlanetTile tile, ref Map __result)
    {
        if (!PerMapSim.Split || Current.Game == null)
        {
            return true;
        }
        Settlement? st = Find.WorldObjects.SettlementAt(tile);
        if (st == null || st.HasMap || SplitColonies.Holds(LockstepClient.PlayerId, st))
        {
            return true;
        }
        TestAutomation.Note($"split: map generation for foreign colony {st.LabelCap} (settlement {st.ID}) skipped here at tick {Find.TickManager.TicksGame}");
        __result = null!;
        return false;
    }
}

/// <summary>Кнопки на чужой колонии на карте мира: «Посетить колонию» / «Покинуть колонию» (T-057).</summary>
[HarmonyPatch(typeof(Settlement), nameof(Settlement.GetGizmos))]
internal static class Patch_Settlement_GetGizmos_Visit
{
    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Settlement __instance)
    {
        foreach (Gizmo g in gizmos)
        {
            yield return g;
        }
        if (!LockstepClient.InSession || !PerMapSim.Split)
        {
            yield break;
        }
        int owner = RWMLWC.Client.World.Colonies.Component?.OwnerOf(__instance) ?? 0;
        if (owner == 0 || owner == LockstepClient.PlayerId || !RWMLWC.Client.Maps.PlayerPresence.IsOnline(owner))
        {
            yield break;
        }
        bool visiting = SplitColonies.Component?.Visits(__instance.ID, LockstepClient.PlayerId) ?? false;
        Settlement st = __instance;
        yield return new LocalCommand_Action
        {
            defaultLabel = visiting ? "Покинуть колонию" : "Посетить колонию",
            defaultDesc = visiting
                ? "Перестать считать эту колонию: она снова будет только у владельца. Игра у всех ненадолго остановится."
                : "Получить карту колонии и считать её вместе с владельцем: можно смотреть и отдавать приказы по выданным правам. Игра у всех ненадолго остановится.",
            icon = TexCommand.ForbidOff,
            action = () => SplitColonies.SendVisit(st, !visiting),
        };
    }
}
