using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Shared.Protocol;
using Verse;
using RWMLWC.Client.Maps;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Задания в режиме заглушки (T-056, docs/ARCHITECTURE.md, 11.4в): создаёт один, получают все.
///  1. TryFire задания у любого клиента — не создание, а запрос (команда 49): для события карты шлёт владелец карты,
///     для мирового — докладчик мира (младший игрок в сети).
///  2. При исполнении запроса создатель (владелец карты цели / докладчик) исполняет настоящий TryFire в изолированном
///     Rand и с номерами из блока заданий; новое (задание, объекты мира, мировые пешки) у него «удержано»: не тикает.
///  3. Создатель шлёт результат (команда 50): XML всего нового одним документом и счётчик блока. При исполнении
///     остальные восстанавливают, создатель снимает удержание; отметки времени у всех сдвигаются на задержку.
/// Только при ключе sleepremove.
/// </summary>
public sealed class QuestSyncComponent : GameComponent
{
    public int nextRequest = 1;
    public int blockCounter;
    public List<int> heldQuests = new List<int>();
    public List<int> heldObjects = new List<int>();
    public List<int> heldPawns = new List<int>();
    public List<int> heldFactions = new List<int>();
    public List<string> heldShips = new List<string>();
    public int heldSince = -1;
    public int heldRequest = -1;
    /// <summary>Запросы, пришедшие, пока ждали результат предыдущего: (событие, карта, поселение, очки, сценарий).</summary>
    public List<string> queued = new List<string>();

    public QuestSyncComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref nextRequest, "rwmlwcQsNext", 1);
        Scribe_Values.Look(ref blockCounter, "rwmlwcQsBlock", 0);
        Scribe_Collections.Look(ref heldQuests, "rwmlwcQsHeldQ", LookMode.Value);
        Scribe_Collections.Look(ref heldObjects, "rwmlwcQsHeldO", LookMode.Value);
        Scribe_Collections.Look(ref heldPawns, "rwmlwcQsHeldP", LookMode.Value);
        Scribe_Collections.Look(ref heldFactions, "rwmlwcQsHeldF", LookMode.Value);
        heldFactions ??= new List<int>();
        Scribe_Collections.Look(ref heldShips, "rwmlwcQsHeldS", LookMode.Value);
        heldShips ??= new List<string>();
        Scribe_Values.Look(ref heldSince, "rwmlwcQsHeldSince", -1);
        Scribe_Values.Look(ref heldRequest, "rwmlwcQsHeldReq", -1);
        Scribe_Collections.Look(ref queued, "rwmlwcQsQueued", LookMode.Value);
        queued ??= new List<string>();
        heldQuests ??= new List<int>(); heldObjects ??= new List<int>(); heldPawns ??= new List<int>();
    }
}

public static class QuestSync
{
    public const byte OpRequest = 49;
    public const byte OpResult = 50;
    /// <summary>Блок номеров заданий: выше блоков карт (MapSimState: 20 карт × 100 млн).</summary>
    public const int Block = 2_100_000_000;

    public static QuestSyncComponent? Component => Current.Game?.GetComponent<QuestSyncComponent>();

    public static bool Active => PerMapSim.SleepRemoveTest && LockstepClient.InSession;

    /// <summary>Идёт создание у создателя: номера — из блока заданий.</summary>
    internal static bool Generating;

    /// <summary>Идёт исполнение запроса создателем: TryFire задания пропускается насквозь.</summary>
    internal static bool Executing;

    internal static int ReporterOfWorld()
    {
        IEnumerable<int> online = RWMLWC.Client.SessionView.SessionUi.Players.Select(p => p.PlayerId).Where(RWMLWC.Client.Maps.PlayerPresence.IsOnline);
        return online.DefaultIfEmpty(LockstepClient.PlayerId).Min();
    }

    internal static int OwnerOf(Map map) =>
        map.Parent is Settlement st ? RWMLWC.Client.World.Colonies.Component?.OwnerOf(st) ?? 0 : 0;

    // ---------- 1. запрос ----------

    internal static void SendRequest(FiringIncident fi)
    {
        int mapId = fi.parms.target is Map m ? m.uniqueID : -1;
        int settlementId = fi.parms.target is Map mp && mp.Parent != null ? mp.Parent.ID : -1;
        RWMLWC.Client.Input.WorldCommands.SendOp(OpRequest, w =>
        {
            w.WriteString(fi.def.defName);
            w.WriteInt32(mapId);
            w.WriteInt32(settlementId);
            w.WriteInt32(BitConverter.SingleToInt32Bits(fi.parms.points));
            w.WriteString(fi.parms.questScriptDef?.defName ?? "");
        });
        TestAutomation.Note($"quest sync: request {fi.def.defName} for {(mapId >= 0 ? "map" + mapId : "world")} sent at tick {Find.TickManager.TicksGame}");
    }

    // ---------- 2. создание у одного ----------

    private static void ExecuteRequest(ref PacketReader r)
    {
        string defName = r.ReadString();
        int mapId = r.ReadInt32();
        int settlementId = r.ReadInt32();
        float points = BitConverter.Int32BitsToSingle(r.ReadInt32());
        string scriptName = r.ReadString();
        QuestSyncComponent? c = Component;
        if (c == null)
        {
            return;
        }
        if (c.heldRequest >= 0)
        {
            // Ждём результат предыдущего — в очередь (у всех одинаково), исполнится при его результате.
            c.queued.Add(string.Join("|", defName, mapId, settlementId, BitConverter.SingleToInt32Bits(points), scriptName));
            TestAutomation.Note($"quest sync: request {defName} queued behind #{c.heldRequest} ({c.queued.Count} in queue)");
            return;
        }
        StartRequest(c, defName, mapId, settlementId, points, scriptName);
    }

    private static void StartRequest(QuestSyncComponent c, string defName, int mapId, int settlementId, float points, string scriptName)
    {
        int request = c.nextRequest++;
        // Создатель: для события карты — владелец её колонии (по объекту мира: карта есть не у всех), иначе докладчик мира.
        int creator = 0;
        if (settlementId >= 0 && Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.ID == settlementId) is Settlement st)
        {
            creator = RWMLWC.Client.World.Colonies.Component?.OwnerOf(st) ?? 0;
        }
        if (creator == 0)
        {
            creator = ReporterOfWorld();
        }
        c.heldRequest = request; // у всех одинаково: ждём результат этого запроса
        c.heldSince = Find.TickManager.TicksGame;
        if (creator != LockstepClient.PlayerId)
        {
            return;
        }
        IncidentDef? def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
        IIncidentTarget? target = mapId >= 0 ? Find.Maps.FirstOrDefault(m => m.uniqueID == mapId) : Find.World;
        if (def == null || target == null)
        {
            SendResult(request, "", c.blockCounter);
            return;
        }
        IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, target);
        parms.points = points;
        if (scriptName.Length > 0)
        {
            parms.questScriptDef = DefDatabase<QuestScriptDef>.GetNamedSilentFail(scriptName);
        }
        var quests0 = new HashSet<int>(Find.QuestManager.QuestsListForReading.Select(q => q.id));
        var objs0 = new HashSet<int>(Find.WorldObjects.AllWorldObjects.Select(o => o.ID));
        var pawns0 = new HashSet<int>(Find.WorldPawns.AllPawnsAliveOrDead.Select(p => p.thingIDNumber));
        var factions0 = new HashSet<int>(Find.FactionManager.AllFactionsListForReading.Select(f => f.loadID));
        var ships0 = new HashSet<TransportShip>(Find.TransportShipManager.AllTransportShips);
        Map? prev = MapRandContext.Current;
        MapRandContext.Enter(null); // общий контекст: номера — из блока заданий, а не из блока карты
        Rand.PushState(Gen.HashCombineInt(Gen.HashCombineInt(request, Find.TickManager.TicksGame), 0x0E57));
        Generating = true;
        Executing = true;
        bool ok;
        try
        {
            ok = def.Worker.TryExecute(parms);
        }
        finally
        {
            Executing = false;
            Generating = false;
            Rand.PopState();
            MapRandContext.Enter(prev);
        }
        List<Quest> quests = Find.QuestManager.QuestsListForReading.Where(q => !quests0.Contains(q.id)).ToList();
        List<WorldObject> objs = Find.WorldObjects.AllWorldObjects.Where(o => !objs0.Contains(o.ID)).ToList();
        List<Pawn> pawns = Find.WorldPawns.AllPawnsAliveOrDead.Where(p => !pawns0.Contains(p.thingIDNumber)).ToList();
        c.heldQuests = quests.Select(q => q.id).ToList();
        c.heldObjects = objs.Select(o => o.ID).ToList();
        c.heldPawns = pawns.Select(p => p.thingIDNumber).ToList();
        List<Faction> factions = Find.FactionManager.AllFactionsListForReading.Where(f => !factions0.Contains(f.loadID)).ToList();
        List<TransportShip> ships = Find.TransportShipManager.AllTransportShips.Where(x => !ships0.Contains(x)).ToList();
        c.heldShips = ships.Select(x => x.GetUniqueLoadID()).ToList();
        // Родственники созданных пешек, созданные тут же, но не попавшие в мир (у создателя — только ссылки).
        var have = new HashSet<int>(pawns.Select(p => p.thingIDNumber));
        List<Pawn> related = new List<Pawn>();
        var stack = new Stack<Pawn>(pawns.Concat(factions.Where(f => f.leader != null).Select(f => f.leader)));
        while (stack.Count > 0)
        {
            Pawn p = stack.Pop();
            if (p.relations == null) continue;
            foreach (DirectPawnRelation rel in p.relations.DirectRelations)
            {
                Pawn o = rel.otherPawn;
                if (o != null && o.thingIDNumber >= QuestSync.Block && have.Add(o.thingIDNumber) && !Find.WorldPawns.Contains(o))
                {
                    related.Add(o);
                    stack.Push(o);
                }
            }
        }
        c.heldFactions = factions.Select(f => f.loadID).ToList();
        // Отношения существующих фракций к новым: игра пишет их в существующие при создании, XML новой этого не несёт.
        var rels = new System.Text.StringBuilder("<rels>");
        foreach (Faction nf in factions)
        {
            foreach (Faction e in Find.FactionManager.AllFactionsListForReading.Where(x => factions0.Contains(x.loadID)))
            {
                FactionRelation? r = e.RelationWith(nf, allowNull: true);
                if (r != null) rels.Append($"<r e=\"{e.loadID}\" f=\"{nf.loadID}\" g=\"{r.baseGoodwill}\" k=\"{(int)r.kind}\"/>");
            }
        }
        rels.Append("</rels>");
        var shipsXml = new System.Text.StringBuilder("<ships>");
        foreach (TransportShip ship in ships) shipsXml.Append(Scribe.saver.DebugOutputFor(ship));
        shipsXml.Append("</ships>");
        string xml = ok ? Serialize(quests, objs, pawns, factions, related).Replace("<pawns>", shipsXml + "<pawns>").Replace("</qs>", rels + "</qs>") : "";
        TestAutomation.Note($"quest sync: request #{request} {defName} created by me: ok={ok}, quests [{string.Join(",", c.heldQuests)}], objects [{string.Join(",", c.heldObjects)}], pawns {c.heldPawns.Count}, ships {c.heldShips.Count}, factions {factions.Count}, related {related.Count}, xml {xml.Length} chars");
        SendResult(request, xml, c.blockCounter);
    }

    private static void SendResult(int request, string xml, int counter)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(xml);
        RWMLWC.Client.Input.WorldCommands.SendOp(OpResult, w =>
        {
            w.WriteInt32(request);
            w.WriteInt32(counter);
            w.WriteBytes(data);
        });
    }

    private static string Serialize(List<Quest> quests, List<WorldObject> objs, List<Pawn> pawns, List<Faction> factions, List<Pawn> related)
    {
        var sb = new System.Text.StringBuilder("<qs>");
        sb.Append("<factions>");
        foreach (Faction f in factions) sb.Append(Scribe.saver.DebugOutputFor(f));
        sb.Append("</factions><related>");
        foreach (Pawn p in related) sb.Append(Scribe.saver.DebugOutputFor(p));
        sb.Append("</related><pawns>");
        foreach (Pawn p in pawns) sb.Append(Scribe.saver.DebugOutputFor(p));
        sb.Append("</pawns><objects>");
        foreach (WorldObject o in objs) sb.Append(Scribe.saver.DebugOutputFor(o));
        sb.Append("</objects><quests>");
        foreach (Quest q in quests) sb.Append(Scribe.saver.DebugOutputFor(q));
        sb.Append("</quests></qs>");
        return sb.ToString();
    }

    // ---------- 3. результат ----------

    private static void ExecuteResult(ref PacketReader r)
    {
        int request = r.ReadInt32();
        int counter = r.ReadInt32();
        string xml = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
        QuestSyncComponent? c = Component;
        if (c == null || c.heldRequest != request)
        {
            return;
        }
        int delay = Find.TickManager.TicksGame - c.heldSince;
        bool mine = c.heldQuests.Count + c.heldObjects.Count + c.heldPawns.Count + c.heldFactions.Count > 0 || xml.Length == 0;
        c.blockCounter = Math.Max(c.blockCounter, counter);
        if (c.heldQuests.Count + c.heldObjects.Count + c.heldPawns.Count + c.heldFactions.Count > 0)
        {
            // Создатель: снять удержание, сдвинуть отметки времени на задержку.
            foreach (Quest q in Find.QuestManager.QuestsListForReading.Where(q => c.heldQuests.Contains(q.id))) Shift(q, delay);
            foreach (WorldObject o in Find.WorldObjects.AllWorldObjects.Where(o => c.heldObjects.Contains(o.ID))) Shift(o, delay);
            foreach (Pawn p in Find.WorldPawns.AllPawnsAliveOrDead.Where(p => c.heldPawns.Contains(p.thingIDNumber))) PawnClockShift.Shift(p, delay);
            foreach (TransportShip ship in Find.TransportShipManager.AllTransportShips.Where(x => c.heldShips.Contains(x.GetUniqueLoadID()))) PawnClockShift.ShiftObject(ship, delay);
            TestAutomation.Note($"quest sync: result #{request} released (own), delay {delay}");
        }
        else if (xml.Length > 0)
        {
            Restore(xml, delay, request);
        }
        c.heldQuests.Clear(); c.heldObjects.Clear(); c.heldPawns.Clear(); c.heldFactions.Clear(); c.heldShips.Clear();
        c.heldRequest = -1; c.heldSince = -1;
        _ = mine;
        if (c.queued.Count > 0)
        {
            string[] q = c.queued[0].Split('|');
            c.queued.RemoveAt(0);
            StartRequest(c, q[0], int.Parse(q[1]), int.Parse(q[2]), BitConverter.Int32BitsToSingle(int.Parse(q[3])), q.Length > 4 ? q[4] : "");
        }
    }

    private static void Shift(object o, int delay)
    {
        PawnClockShift.ShiftObject(o, delay);
        if (o is Quest q) foreach (QuestPart part in q.PartsListForReading) PawnClockShift.ShiftObject(part, delay);
        if (o is WorldObject wo) foreach (WorldObjectComp comp in wo.AllComps) PawnClockShift.ShiftObject(comp, delay);
    }

    private static readonly AccessTools.FieldRef<Faction, List<FactionRelation>> relationsRef = AccessTools.FieldRefAccess<Faction, List<FactionRelation>>("relations");

    private static readonly AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> directoryRef =
        AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");

    private static void Restore(string xml, int delay, int request)
    {
        var errors = new List<string>();
        UnityEngine.Application.LogCallback cb = (msg, st, type) =>
        {
            if (type == UnityEngine.LogType.Error || type == UnityEngine.LogType.Exception || type == UnityEngine.LogType.Warning)
            {
                errors.Add(type + ": " + (msg.Length > 160 ? msg.Substring(0, 160) : msg));
            }
        };
        UnityEngine.Application.logMessageReceived += cb;
        try
        {
            RestoreInner(xml, delay, request);
        }
        finally
        {
            UnityEngine.Application.logMessageReceived -= cb;
            TestAutomation.Note($"quest sync: restore #{request} log: {errors.Count} errors/warnings{(errors.Count > 0 ? " — " + string.Join(" | ", errors.Take(6)) : "")}");
        }
    }

    private static void RestoreInner(string xml, int delay, int request)
    {
        List<Faction>? factions = null;
        List<Pawn>? related = null;
        List<TransportShip>? ships = null;
        List<Pawn>? pawns = null;
        List<WorldObject>? objs = null;
        List<Quest>? quests = null;
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml.Replace("<saveable", "<li").Replace("</saveable>", "</li>"));
            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
            Scribe_Collections.Look(ref factions, "factions", LookMode.Deep);
            Scribe_Collections.Look(ref related, "related", LookMode.Deep);
            Scribe_Collections.Look(ref ships, "ships", LookMode.Deep);
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Deep);
            Scribe_Collections.Look(ref objs, "objects", LookMode.Deep);
            Scribe_Collections.Look(ref quests, "quests", LookMode.Deep);
            LoadedObjectDirectory dir = directoryRef(Scribe.loader.crossRefs);
            var mine = new HashSet<int>((pawns ?? new List<Pawn>()).Concat(related ?? new List<Pawn>()).Select(p => p.thingIDNumber));
            var newFactions = new HashSet<int>((factions ?? new List<Faction>()).Select(f => f.loadID));
            foreach (Pawn p in PawnsFinder.All_AliveOrDead.Where(p => !mine.Contains(p.thingIDNumber))) dir.RegisterLoaded(p);
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading.Where(f => !newFactions.Contains(f.loadID))) dir.RegisterLoaded(f);
            foreach (WorldObject o in Find.WorldObjects.AllWorldObjects) dir.RegisterLoaded(o);
            foreach (Map m in Find.Maps) dir.RegisterLoaded(m);
            if (ModsConfig.IdeologyActive)
            {
                foreach (Ideo i in Find.IdeoManager.IdeosListForReading)
                {
                    dir.RegisterLoaded(i);
                    foreach (Precept pr in i.PreceptsListForReading) dir.RegisterLoaded(pr);
                }
            }
            // Политики колонии (еда, лекарства, одежда, чтение): пешка ссылается на них (sr27: FoodPolicy, DrugPolicy).
            Game g = Current.Game;
            foreach (DrugPolicy x in g.drugPolicyDatabase.AllPolicies) dir.RegisterLoaded(x);
            foreach (FoodPolicy x in g.foodRestrictionDatabase.AllFoodRestrictions) dir.RegisterLoaded(x);
            foreach (ApparelPolicy x in g.outfitDatabase.AllOutfits) dir.RegisterLoaded(x);
            foreach (ReadingPolicy x in g.readingPolicyDatabase.AllReadingPolicies) dir.RegisterLoaded(x);
            foreach (Quest q in Find.QuestManager.QuestsListForReading) dir.RegisterLoaded(q);
            Scribe.loader.FinalizeLoading();
        }
        catch (Exception ex)
        {
            Scribe.ForceStop();
            TestAutomation.Note("quest sync: restore FAILED: " + ex.Message);
            Log.Error("[RWMLWC] задание: не удалось восстановить: " + ex);
            return;
        }
        foreach (Faction f in factions ?? new List<Faction>()) { Find.FactionManager.Add(f); }
        try
        {
            XmlDocument rd = new XmlDocument();
            rd.LoadXml(xml.Substring(xml.IndexOf("<rels>", StringComparison.Ordinal), xml.IndexOf("</rels>", StringComparison.Ordinal) + 7 - xml.IndexOf("<rels>", StringComparison.Ordinal)));
            foreach (XmlElement el in rd.DocumentElement!.ChildNodes.OfType<XmlElement>())
            {
                Faction? e = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.loadID == int.Parse(el.GetAttribute("e")));
                Faction? nf = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.loadID == int.Parse(el.GetAttribute("f")));
                if (e == null || nf == null) continue;
                List<FactionRelation> list = relationsRef(e);
                list.RemoveAll(x => x.other == nf);
                list.Add(new FactionRelation { other = nf, baseGoodwill = int.Parse(el.GetAttribute("g")), kind = (FactionRelationKind)int.Parse(el.GetAttribute("k")) });
            }
        }
        catch (Exception ex)
        {
            TestAutomation.Note("quest sync: relations restore FAILED: " + ex.Message);
        }
        foreach (Pawn p in related ?? new List<Pawn>()) { PawnClockShift.Shift(p, delay); } // только для ссылок, в мир — нет (как у создателя)
        foreach (TransportShip ship in ships ?? new List<TransportShip>()) { PawnClockShift.ShiftObject(ship, delay); Find.TransportShipManager.RegisterShipObject(ship); }
        foreach (Pawn p in pawns ?? new List<Pawn>()) { PawnClockShift.Shift(p, delay); Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever); }
        foreach (WorldObject o in objs ?? new List<WorldObject>()) { Shift(o, delay); Find.WorldObjects.Add(o); }
        foreach (Quest q in quests ?? new List<Quest>()) { Shift(q, delay); Find.QuestManager.Add(q); }
        TestAutomation.Note($"quest sync: result #{request} restored: quests [{string.Join(",", (quests ?? new List<Quest>()).Select(q => q.id + ":" + q.root?.defName))}], objects {objs?.Count ?? 0}, ships {ships?.Count ?? 0}, pawns {pawns?.Count ?? 0}, factions {factions?.Count ?? 0}, related {related?.Count ?? 0}, delay {delay}");
    }

    public static void Execute(byte op, ref PacketReader r)
    {
        if (op == OpRequest) ExecuteRequest(ref r);
        else if (op == OpResult) ExecuteResult(ref r);
    }

    internal static bool IsHeldQuest(Quest q) => Component?.heldQuests.Contains(q.id) == true;
    internal static bool IsHeldObject(WorldObject o) => Component?.heldObjects.Contains(o.ID) == true;
    internal static bool IsHeldPawn(Pawn p) => Component?.heldPawns.Contains(p.thingIDNumber) == true;
    internal static bool IsHeldFaction(Faction f) => Component?.heldFactions.Contains(f.loadID) == true;
}

/// <summary>TryFire задания в режиме заглушки: запрос вместо создания (кроме исполнения запроса создателем).</summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
internal static class Patch_Storyteller_TryFire_QuestRequest
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(FiringIncident fi, ref bool __result)
    {
        if (!QuestSync.Active || QuestSync.Executing || !(fi.def.Worker is IncidentWorker_GiveQuest))
        {
            return true;
        }
        __result = false;
        int sender;
        if (fi.parms.target is Map m)
        {
            if (!Find.Maps.Contains(m))
            {
                return false; // карты нет — событие видит только владелец
            }
            sender = QuestSync.OwnerOf(m);
            if (sender == 0)
            {
                sender = QuestSync.ReporterOfWorld();
            }
        }
        else
        {
            sender = QuestSync.ReporterOfWorld();
        }
        if (sender == LockstepClient.PlayerId)
        {
            QuestSync.SendRequest(fi);
        }
        return false;
    }
}

/// <summary>Номера при создании задания — из блока заданий (не общий счётчик: у остальных его нет).</summary>
[HarmonyPatch]
internal static class Patch_UniqueIDs_QuestBlock
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        typeof(UniqueIDsManager).GetMethods().Where(mi => mi.Name.StartsWith("GetNext", StringComparison.Ordinal) && mi.Name.EndsWith("ID", StringComparison.Ordinal) && mi.ReturnType == typeof(int) && mi.GetParameters().Length == 0);

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref int __result)
    {
        if (!QuestSync.Generating || QuestSync.Component == null)
        {
            return true;
        }
        __result = QuestSync.Block + QuestSync.Component.blockCounter++;
        return false;
    }
}

[HarmonyPatch(typeof(Quest), nameof(Quest.QuestTick))]
internal static class Patch_Quest_QuestTick_Held
{
    private static bool Prefix(Quest __instance) => !(QuestSync.Active && QuestSync.IsHeldQuest(__instance));
}

[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.DoTick))]
internal static class Patch_WorldObject_DoTick_Held
{
    private static bool Prefix(WorldObject __instance) => !(QuestSync.Active && QuestSync.IsHeldObject(__instance));
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick_HeldPawn
{
    private static bool Prefix(Thing __instance) => !(QuestSync.Active && __instance is Pawn p && QuestSync.IsHeldPawn(p));
}

[HarmonyPatch(typeof(Faction), nameof(Faction.FactionTick))]
internal static class Patch_Faction_FactionTick_Held
{
    private static bool Prefix(Faction __instance) => !(QuestSync.Active && QuestSync.IsHeldFaction(__instance));
}

[HarmonyPatch(typeof(TransportShip), nameof(TransportShip.Tick))]
internal static class Patch_TransportShip_Tick_Held
{
    private static bool Prefix(TransportShip __instance) => !(QuestSync.Active && QuestSync.Component?.heldShips.Contains(__instance.GetUniqueLoadID()) == true);
}
