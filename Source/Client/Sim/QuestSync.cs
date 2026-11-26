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
    public int heldSince = -1;
    public int heldRequest = -1;

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
        Scribe_Values.Look(ref heldSince, "rwmlwcQsHeldSince", -1);
        Scribe_Values.Look(ref heldRequest, "rwmlwcQsHeldReq", -1);
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
        int request = c.nextRequest++;
        if (c.heldRequest >= 0)
        {
            TestAutomation.Note($"quest sync: request #{request} {defName} dropped — #{c.heldRequest} still pending");
            return;
        }
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
        string xml = ok ? Serialize(quests, objs, pawns) : "";
        TestAutomation.Note($"quest sync: request #{request} {defName} created by me: ok={ok}, quests [{string.Join(",", c.heldQuests)}], objects [{string.Join(",", c.heldObjects)}], pawns {c.heldPawns.Count}, xml {xml.Length} chars");
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

    private static string Serialize(List<Quest> quests, List<WorldObject> objs, List<Pawn> pawns)
    {
        var sb = new System.Text.StringBuilder("<qs>");
        sb.Append("<pawns>");
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
        bool mine = c.heldQuests.Count + c.heldObjects.Count + c.heldPawns.Count > 0 || xml.Length == 0;
        c.blockCounter = Math.Max(c.blockCounter, counter);
        if (c.heldQuests.Count + c.heldObjects.Count + c.heldPawns.Count > 0)
        {
            // Создатель: снять удержание, сдвинуть отметки времени на задержку.
            foreach (Quest q in Find.QuestManager.QuestsListForReading.Where(q => c.heldQuests.Contains(q.id))) Shift(q, delay);
            foreach (WorldObject o in Find.WorldObjects.AllWorldObjects.Where(o => c.heldObjects.Contains(o.ID))) Shift(o, delay);
            foreach (Pawn p in Find.WorldPawns.AllPawnsAliveOrDead.Where(p => c.heldPawns.Contains(p.thingIDNumber))) PawnClockShift.Shift(p, delay);
            TestAutomation.Note($"quest sync: result #{request} released (own), delay {delay}");
        }
        else if (xml.Length > 0)
        {
            Restore(xml, delay, request);
        }
        c.heldQuests.Clear(); c.heldObjects.Clear(); c.heldPawns.Clear();
        c.heldRequest = -1; c.heldSince = -1;
        _ = mine;
    }

    private static void Shift(object o, int delay)
    {
        PawnClockShift.ShiftObject(o, delay);
        if (o is Quest q) foreach (QuestPart part in q.PartsListForReading) PawnClockShift.ShiftObject(part, delay);
        if (o is WorldObject wo) foreach (WorldObjectComp comp in wo.AllComps) PawnClockShift.ShiftObject(comp, delay);
    }

    private static readonly AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> directoryRef =
        AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");

    private static void Restore(string xml, int delay, int request)
    {
        List<Pawn>? pawns = null;
        List<WorldObject>? objs = null;
        List<Quest>? quests = null;
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml.Replace("<saveable", "<li").Replace("</saveable>", "</li>"));
            Scribe.loader.curXmlParent = doc.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Deep);
            Scribe_Collections.Look(ref objs, "objects", LookMode.Deep);
            Scribe_Collections.Look(ref quests, "quests", LookMode.Deep);
            LoadedObjectDirectory dir = directoryRef(Scribe.loader.crossRefs);
            var mine = new HashSet<int>((pawns ?? new List<Pawn>()).Select(p => p.thingIDNumber));
            foreach (Pawn p in PawnsFinder.All_AliveOrDead.Where(p => !mine.Contains(p.thingIDNumber))) dir.RegisterLoaded(p);
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading) dir.RegisterLoaded(f);
            foreach (WorldObject o in Find.WorldObjects.AllWorldObjects) dir.RegisterLoaded(o);
            foreach (Map m in Find.Maps) dir.RegisterLoaded(m);
            if (ModsConfig.IdeologyActive) foreach (Ideo i in Find.IdeoManager.IdeosListForReading) dir.RegisterLoaded(i);
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
        foreach (Pawn p in pawns ?? new List<Pawn>()) { PawnClockShift.Shift(p, delay); Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever); }
        foreach (WorldObject o in objs ?? new List<WorldObject>()) { Shift(o, delay); Find.WorldObjects.Add(o); }
        foreach (Quest q in quests ?? new List<Quest>()) { Shift(q, delay); Find.QuestManager.Add(q); }
        TestAutomation.Note($"quest sync: result #{request} restored: quests [{string.Join(",", (quests ?? new List<Quest>()).Select(q => q.id + ":" + q.root?.defName))}], objects {objs?.Count ?? 0}, pawns {pawns?.Count ?? 0}, delay {delay}");
    }

    public static void Execute(byte op, ref PacketReader r)
    {
        if (op == OpRequest) ExecuteRequest(ref r);
        else if (op == OpResult) ExecuteResult(ref r);
    }

    internal static bool IsHeldQuest(Quest q) => Component?.heldQuests.Contains(q.id) == true;
    internal static bool IsHeldObject(WorldObject o) => Component?.heldObjects.Contains(o.ID) == true;
    internal static bool IsHeldPawn(Pawn p) => Component?.heldPawns.Contains(p.thingIDNumber) == true;
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
