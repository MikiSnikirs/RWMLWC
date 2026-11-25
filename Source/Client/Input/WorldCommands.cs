using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Ввод на карте мира командами (T-073). Сейчас: сформировать караван из окна, основать поселение, маршрут каравана
/// без «действия по прибытии». Действие игрока не исполняется сразу, а уходит командой; все клиенты исполняют её
/// в одной точке общего порядка тем же вызовом игры.
/// </summary>
public static class WorldCommands
{
    private const byte OpSettle = 1;
    private const byte OpRoute = 2;
    private const byte OpGizmo = 3;
    private const byte OpSplit = 4;
    private const byte OpReform = 5;
    private const byte OpColonyClaim = 6;
    private const byte OpColonyJoin = 7;
    private const byte OpColonyNew = 8;
    private const byte OpColonyTestAbsent = 13; // только тестовые прогоны (ключ abandontest=1)
    private const byte OpTestPresence = 14;     // только тестовые прогоны (ключ stasistest=1)
    private const byte OpTestKill = 15;         // только тестовые прогоны (ключ adapttest=1)
    private const byte OpTestTransfer = 16;     // только тестовые прогоны (ключ clocktest=1)
    private const byte OpTestTrader = 18;       // только тестовые прогоны (ключ tradetest=1): караван торговцев
    private const byte OpTestGoTrade = 19;      // только тестовые прогоны (ключ tradetest=1): колонист идёт торговать
    private const byte OpTestHomeArea = 20;     // только тестовые прогоны (ключ exchangetest=1): домашняя зона под вещами карты
    // 21, 22 — капсулы (TransportPods)
    private const byte OpTestPodKit = 23;
    private const byte OpTestGravship = 41;    // только тестовые прогоны (ключ gravtest=1): a=0 — построить корабль, a=1 — взлёт (T-081)
    // 24 — SOS (Absence/Sos.cs); 25, 26 — аренда пешек (World/Rentals.cs); 40 — события карты (Sim/MapEvents.cs); 42 — посадка гравикорабля (Sim/GravshipSession.cs)       // только тестовые прогоны (ключ podtest=1): пусковая площадка с капсулой и топливом

    private static readonly FieldInfo FormMapField = AccessTools.Field(typeof(Dialog_FormCaravan), "map");
    private static readonly FieldInfo FormReformField = AccessTools.Field(typeof(Dialog_FormCaravan), "reform");
    private static readonly FieldInfo FormDestinationField = AccessTools.Field(typeof(Dialog_FormCaravan), "destinationTile");
    private static readonly FieldInfo FormAboutToBeRemovedField = AccessTools.Field(typeof(Dialog_FormCaravan), "mapAboutToBeRemoved");
    private static readonly MethodInfo FormRecacheMethod = AccessTools.Method(typeof(Dialog_FormCaravan), "CalculateAndRecacheTransferables");
    internal static readonly MethodInfo ReformMethod = AccessTools.Method(typeof(Dialog_FormCaravan), "TryReformCaravan");

    private static readonly FieldInfo SplitCaravanField = AccessTools.Field(typeof(Dialog_SplitCaravan), "caravan");
    private static readonly FieldInfo SplitTransferablesField = AccessTools.Field(typeof(Dialog_SplitCaravan), "transferables");
    private static readonly MethodInfo SplitCheckMethod = AccessTools.Method(typeof(Dialog_SplitCaravan), "CheckForErrors");
    private static readonly MethodInfo SplitRecacheMethod = AccessTools.Method(typeof(Dialog_SplitCaravan), "CalculateAndRecacheTransferables");
    internal static readonly MethodInfo SplitMethod = AccessTools.Method(typeof(Dialog_SplitCaravan), "TrySplitCaravan");
    private const int MaxItems = 4096;

    private static readonly FieldInfo LayerIdField = AccessTools.Field(typeof(PlanetTile), "layerId");

    public static int Captured { get; private set; }

    internal static void WriteTile(PacketWriter w, PlanetTile tile)
    {
        w.WriteInt32(tile.tileId);
        w.WriteInt32((int)(LayerIdField.GetValue(tile) ?? 0));
    }

    internal static PlanetTile ReadTile(ref PacketReader r)
    {
        int id = r.ReadInt32();
        int layer = r.ReadInt32();
        return id < 0 ? PlanetTile.Invalid : new PlanetTile(id, layer);
    }

    private static Caravan? FindCaravan(int id) => Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == id);

    // ---------- сформировать караван ----------

    internal static bool CaptureCaravanForm(List<Pawn> pawns, List<Pawn> downedPawns, List<TransferableOneWay> transferables,
        IntVec3 meetingPoint, IntVec3 exitSpot, PlanetTile startingTile, PlanetTile destinationTile)
    {
        Map? map = pawns.Concat(downedPawns).FirstOrDefault(p => p.Spawned)?.Map ?? Find.CurrentMap;
        if (map == null)
        {
            return true;
        }
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(pawns.Concat(downedPawns).Cast<object>()))
        {
            return false;
        }
        PacketWriter w = CommandBus.Begin(map);
        WritePawns(w, pawns);
        WritePawns(w, downedPawns);
        List<TransferableOneWay> moving = transferables.Where(t => t.CountToTransfer > 0 && t.HasAnyThing).ToList();
        w.WriteInt32(moving.Count);
        foreach (TransferableOneWay t in moving)
        {
            w.WriteInt32(t.CountToTransfer);
            w.WriteInt32(t.things.Count);
            foreach (Thing thing in t.things)
            {
                w.WriteInt32(thing.thingIDNumber);
            }
        }
        ValueCodec.Write(w, meetingPoint);
        ValueCodec.Write(w, exitSpot);
        WriteTile(w, startingTile);
        WriteTile(w, destinationTile);
        CommandBus.Send(map, CommandKinds.CaravanForm, PermissionLevel.Full, w);
        Captured++;
        TestAutomation.Note($"world: caravan form sent, pawns {pawns.Count}+{downedPawns.Count}, item groups {moving.Count}");
        return false;
    }

    private static void WritePawns(PacketWriter w, List<Pawn> pawns)
    {
        w.WriteInt32(pawns.Count);
        foreach (Pawn p in pawns)
        {
            w.WriteInt32(p.thingIDNumber);
        }
    }

    private static List<Pawn> ReadPawns(ref PacketReader r)
    {
        int count = r.ReadCount(1024, 4);
        List<Pawn> result = new List<Pawn>(count);
        for (int i = 0; i < count; i++)
        {
            Pawn? p = ValueCodec.FindPawnAnywhere(r.ReadInt32());
            if (p != null)
            {
                result.Add(p);
            }
        }
        return result;
    }

    public static void ExecuteCaravanForm(Map map, ref PacketReader r)
    {
        List<Pawn> pawns = ReadPawns(ref r);
        List<Pawn> downed = ReadPawns(ref r);
        // Вещи для каравана — на карте или в инвентаре пешек карты.
        Dictionary<int, Thing> byId = new Dictionary<int, Thing>();
        foreach (Thing t in map.listerThings.AllThings)
        {
            byId[t.thingIDNumber] = t;
        }
        foreach (Pawn p in map.mapPawns.AllPawns)
        {
            if (p.inventory != null)
            {
                foreach (Thing t in p.inventory.innerContainer)
                {
                    byId[t.thingIDNumber] = t;
                }
            }
        }
        int groups = r.ReadCount(MaxItems, 8);
        List<TransferableOneWay> transferables = new List<TransferableOneWay>(groups);
        for (int i = 0; i < groups; i++)
        {
            int count = r.ReadInt32();
            int things = r.ReadCount(MaxItems, 4);
            TransferableOneWay t = new TransferableOneWay();
            for (int k = 0; k < things; k++)
            {
                int id = r.ReadInt32();
                Thing? thing = byId.TryGetValue(id, out Thing? found) ? found : ValueCodec.FindPawnAnywhere(id);
                if (thing != null)
                {
                    t.things.Add(thing);
                }
            }
            if (t.HasAnyThing)
            {
                t.AdjustTo(Math.Min(count, t.MaxCount));
                transferables.Add(t);
            }
        }
        IntVec3 meeting = (IntVec3)ValueCodec.Read(ref r, map)!;
        IntVec3 exit = (IntVec3)ValueCodec.Read(ref r, map)!;
        PlanetTile start = ReadTile(ref r);
        PlanetTile dest = ReadTile(ref r);
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(pawns.Concat(downed).Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        if (pawns.Count == 0)
        {
            TestAutomation.Note("world: caravan form: no pawns left, dropped");
            return;
        }
        CaravanFormingUtility.StartFormingCaravan(pawns, downed, Faction.OfPlayer, transferables, meeting, exit, start, dest);
        SharedWindows.CloseMatching(w => w is Dialog_FormCaravan d && ReferenceEquals(FormMapField.GetValue(d), map));
        TestAutomation.Note($"world: caravan forming started by player {CommandBus.CurrentPlayerId}, pawns {pawns.Count}+{downed.Count}, item groups {transferables.Count}");
    }

    // ---------- действия на карте мира ----------

    private static PacketWriter BeginWorld()
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        return CommandBus.Begin(map);
    }

    private static void SendWorld(PacketWriter w)
    {
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
        Captured++;
    }

    internal static bool CaptureSettle(Caravan caravan)
    {
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(caravan.PawnsListForReading.Cast<object>()))
        {
            return false;
        }
        PacketWriter w = BeginWorld();
        w.WriteByte(OpSettle);
        w.WriteInt32(caravan.ID);
        SendWorld(w);
        TestAutomation.Note($"world: settle sent, caravan {caravan.ID}");
        return false;
    }

    internal static bool CaptureRoute(Caravan caravan, PlanetTile destTile, CaravanArrivalAction? arrivalAction, bool repathImmediately, bool resetPauseStatus)
    {
        if (arrivalAction != null && !ArrivalSupported(arrivalAction, out string why))
        {
            Messages.Message("RWMLWC: " + why, MessageTypeDefOf.RejectInput, false);
            return false;
        }
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(caravan.PawnsListForReading.Cast<object>()))
        {
            return false;
        }
        PacketWriter w = BeginWorld();
        w.WriteByte(OpRoute);
        w.WriteInt32(caravan.ID);
        WriteTile(w, destTile);
        w.WriteBool(repathImmediately);
        w.WriteBool(resetPauseStatus);
        WriteArrival(w, arrivalAction);
        SendWorld(w);
        return false;
    }

    // ---------- действие по прибытии ----------
    // Все действия игры устроены одинаково: одно поле с объектом мира (поселение, место, карта) и конструктор
    // без параметров. Передаются тип и номер объекта; у всех действие собирается заново.

    // Открывают окно торговли у того, кто прибыл, — а оно в сессии ещё не умеет командами (T-036).
    private static readonly HashSet<Type> ArrivalWithWindow = new HashSet<Type>
    {
        typeof(CaravanArrivalAction_Trade),
        typeof(CaravanArrivalAction_OfferGifts),
    };

    private static FieldInfo? ArrivalTargetField(Type type)
    {
        FieldInfo? found = null;
        for (Type? t = type; t != null && t != typeof(CaravanArrivalAction); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!typeof(WorldObject).IsAssignableFrom(f.FieldType) || found != null)
                {
                    return null; // другие поля или второй объект — общим способом не передать
                }
                found = f;
            }
        }
        return found;
    }

    internal static bool ArrivalSupported(CaravanArrivalAction action, out string why)
    {
        Type type = action.GetType();
        if (ArrivalWithWindow.Contains(type))
        {
            why = "торговля караваном в сетевой игре пока недоступна";
            return false;
        }
        if (ArrivalTargetField(type) == null || AccessTools.Constructor(type, Type.EmptyTypes) == null)
        {
            why = "это действие каравана по прибытии пока не работает в сетевой игре: " + type.Name;
            return false;
        }
        why = "";
        return true;
    }

    private static void WriteArrival(PacketWriter w, CaravanArrivalAction? action)
    {
        if (action == null)
        {
            w.WriteString("");
            return;
        }
        Type type = action.GetType();
        WorldObject? target = (WorldObject?)ArrivalTargetField(type)!.GetValue(action);
        w.WriteString(type.FullName!);
        w.WriteInt32(target?.ID ?? -1);
    }

    private static CaravanArrivalAction? ReadArrival(ref PacketReader r, out bool failed)
    {
        failed = false;
        string typeName = r.ReadString();
        if (typeName.Length == 0)
        {
            return null;
        }
        int targetId = r.ReadInt32();
        Type? type = AccessTools.TypeByName(typeName);
        FieldInfo? field = type == null || !typeof(CaravanArrivalAction).IsAssignableFrom(type) ? null : ArrivalTargetField(type);
        WorldObject? target = FindWorldObject(targetId);
        if (type == null || field == null || target == null || !field.FieldType.IsInstanceOfType(target))
        {
            failed = true;
            TestAutomation.Note($"world: arrival action {typeName} for {targetId} can't be rebuilt");
            return null;
        }
        CaravanArrivalAction action = (CaravanArrivalAction)Activator.CreateInstance(type, true)!;
        field.SetValue(action, target);
        return action;
    }

    /// <summary>
    /// Кнопка объекта мира (лагерь, объединить, бросить вещи, покинуть…) — общим механизмом, как кнопки вещей
    /// (<see cref="GizmoCommands"/>): адрес «у объекта мира X кнопка номер N» и выделение на карте мира автора
    /// (кнопка «Объединить» берёт выделенные караваны). Исполнение — тем же нажатием у всех.
    /// </summary>
    internal static void CaptureWorldGizmo(WorldObject owner, int index, ulong signature)
    {
        List<WorldObject> selection = Find.WorldSelector.SelectedObjects;
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(selection.OfType<Caravan>().SelectMany(c => c.PawnsListForReading).Cast<object>()))
        {
            return;
        }
        PacketWriter w = BeginWorld();
        w.WriteByte(OpGizmo);
        w.WriteInt32(owner.ID);
        w.WriteInt32(index);
        w.WriteUInt64(signature);
        w.WriteInt32(selection.Count);
        foreach (WorldObject o in selection)
        {
            w.WriteInt32(o.ID);
        }
        SendWorld(w);
        TestAutomation.Note($"world: gizmo {index} of {owner.def.defName} {owner.ID} sent");
    }

    private static WorldObject? FindWorldObject(int id)
    {
        List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].ID == id)
            {
                return all[i];
            }
        }
        return null;
    }

    private static void ExecuteWorldGizmo(ref PacketReader r)
    {
        WorldObject? owner = FindWorldObject(r.ReadInt32());
        int index = r.ReadInt32();
        ulong signature = r.ReadUInt64();
        int count = r.ReadCount(256, 4);
        List<WorldObject> authorSelection = new List<WorldObject>(count);
        for (int i = 0; i < count; i++)
        {
            WorldObject? o = FindWorldObject(r.ReadInt32());
            if (o != null)
            {
                authorSelection.Add(o);
            }
        }
        if (owner == null)
        {
            TestAutomation.Note("world: gizmo owner is gone, dropped");
            return;
        }
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(authorSelection.Append(owner).OfType<Caravan>().SelectMany(c => c.PawnsListForReading).Cast<object>(), CommandBus.CurrentPlayerId),
                CommandBus.CurrentSeq))
        {
            return;
        }
        // Выделение на карте мира — как у автора, пока исполняется кнопка.
        List<WorldObject> selection = Find.WorldSelector.SelectedObjects;
        List<WorldObject> saved = new List<WorldObject>(selection);
        selection.Clear();
        selection.AddRange(authorSelection);
        if (!selection.Contains(owner))
        {
            selection.Add(owner);
        }
        // Объекты мира живут в глобальном состоянии: Rand и номера новых вещей — глобальные, не карты 0.
        MapRandContext.Enter(null);
        try
        {
            Gizmo? target = null;
            Gizmo? bySignature = null;
            int sameSignature = 0;
            int i = 0;
            foreach (Gizmo g in owner.GetGizmos())
            {
                ulong sig = GizmoCommands.Signature(g);
                if (i == index && sig == signature)
                {
                    target = g;
                    break;
                }
                if (sig == signature)
                {
                    sameSignature++;
                    bySignature = g;
                }
                i++;
            }
            if (target == null && sameSignature == 1)
            {
                target = bySignature;
            }
            if (target == null)
            {
                TestAutomation.Note($"world: gizmo {index} of {owner.def.defName} {owner.ID} not found, dropped");
                return;
            }
            if (target.Disabled)
            {
                TestAutomation.Note($"world: gizmo {index} of {owner.def.defName} {owner.ID} disabled at execution");
                return;
            }
            target.ProcessInput(GizmoCommands.ReplayEvent());
            TestAutomation.Note($"world: gizmo {index} ({(target as Command)?.defaultLabel}) of {owner.def.defName} {owner.ID} pressed by player {CommandBus.CurrentPlayerId}");
        }
        finally
        {
            selection.Clear();
            foreach (WorldObject o in saved)
            {
                if (o.Spawned)
                {
                    selection.Add(o);
                }
            }
        }
    }

    /// <summary>
    /// «Разделить караван»: кнопка «Принять» окна делит караван прямо в интерфейсе. Вместо этого — команда
    /// с выбранными пешками и вещами; у всех исполняется тот же <c>TrySplitCaravan</c> окна, собранного заново.
    /// </summary>
    internal static bool CaptureSplit(Dialog_SplitCaravan dialog, ref bool result)
    {
        Caravan caravan = (Caravan)SplitCaravanField.GetValue(dialog)!;
        List<TransferableOneWay> transferables = (List<TransferableOneWay>)SplitTransferablesField.GetValue(dialog)!;
        List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(transferables);
        if (!(bool)SplitCheckMethod.Invoke(dialog, new object[] { pawns })!)
        {
            result = false;
            return false;
        }
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(caravan.PawnsListForReading.Cast<object>()))
        {
            result = false;
            return false;
        }
        PacketWriter w = BeginWorld();
        w.WriteByte(OpSplit);
        w.WriteInt32(caravan.ID);
        List<TransferableOneWay> moving = transferables.Where(t => t.CountToTransfer > 0 && t.HasAnyThing).ToList();
        w.WriteInt32(moving.Count);
        foreach (TransferableOneWay t in moving)
        {
            w.WriteInt32(t.CountToTransfer);
            w.WriteInt32(t.things.Count);
            foreach (Thing thing in t.things)
            {
                w.WriteInt32(thing.thingIDNumber);
            }
        }
        SendWorld(w);
        TestAutomation.Note($"world: split sent, caravan {caravan.ID}, pawns {pawns.Count}, groups {moving.Count}");
        result = true; // окно закрывается; караван разделится, когда команда вернётся в общем порядке
        return false;
    }

    private static void ExecuteSplit(Caravan? caravan, ref PacketReader r)
    {
        // Сколько переводить в новый караван — по номеру вещи из каждой выбранной группы.
        int groups = r.ReadCount(MaxItems, 8);
        List<(int count, List<int> ids)> sent = new List<(int, List<int>)>(groups);
        for (int i = 0; i < groups; i++)
        {
            int count = r.ReadInt32();
            int things = r.ReadCount(MaxItems, 4);
            List<int> ids = new List<int>(things);
            for (int k = 0; k < things; k++)
            {
                ids.Add(r.ReadInt32());
            }
            sent.Add((count, ids));
        }
        if (caravan == null)
        {
            TestAutomation.Note("world: split: caravan is gone, dropped");
            return;
        }
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(caravan.PawnsListForReading.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        MapRandContext.Enter(null);
        // Окно собирает полный список (кто уходит и кто остаётся — проверка «в исходном караване остался колонист»
        // смотрит на весь список), как у игрока; затем проставляются выбранные количества.
        Dialog_SplitCaravan dialog = new Dialog_SplitCaravan(caravan);
        SplitRecacheMethod.Invoke(dialog, null);
        List<TransferableOneWay> transferables = (List<TransferableOneWay>)SplitTransferablesField.GetValue(dialog)!;
        foreach ((int count, List<int> ids) in sent)
        {
            TransferableOneWay? t = transferables.FirstOrDefault(x => x.things.Any(th => ids.Contains(th.thingIDNumber)));
            t?.AdjustTo(Math.Min(count, t.MaxCount));
        }
        bool ok = (bool)SplitMethod.Invoke(dialog, null)!;
        if (ok)
        {
            SharedWindows.CloseMatching(w => w is Dialog_SplitCaravan d && ReferenceEquals(SplitCaravanField.GetValue(d), caravan));
        }
        TestAutomation.Note($"world: split by player {CommandBus.CurrentPlayerId}, caravan {caravan.ID}: {(ok ? "done" : "rejected")}");
    }

    /// <summary>
    /// «Переформировать караван» (с временной карты: лагерь, место задания): окно создаёт караван сразу, прямо
    /// в интерфейсе, и раскладывает вещи по случайным пешкам. Вместо этого — команда (операция 5) с выбранными
    /// пешками и вещами и целью; у всех окно собирается заново и вызывается тот же <c>TryReformCaravan</c>.
    /// </summary>
    internal static bool CaptureReform(Dialog_FormCaravan dialog, ref bool result)
    {
        Map map = (Map)FormMapField.GetValue(dialog)!;
        List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(dialog.transferables);
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(pawns.Cast<object>()))
        {
            result = false;
            return false;
        }
        PacketWriter w = BeginWorld();
        w.WriteByte(OpReform);
        w.WriteInt32(map.uniqueID);
        w.WriteBool((bool)FormAboutToBeRemovedField.GetValue(dialog)!);
        WriteTile(w, (PlanetTile)FormDestinationField.GetValue(dialog)!);
        WriteGroups(w, dialog.transferables);
        SendWorld(w);
        TestAutomation.Note($"world: reform sent, map {map.uniqueID}, pawns {pawns.Count}");
        result = true; // окно закрывается; караван появится, когда команда вернётся в общем порядке
        return false;
    }

    private static void WriteGroups(PacketWriter w, List<TransferableOneWay> transferables)
    {
        List<TransferableOneWay> moving = transferables.Where(t => t.CountToTransfer > 0 && t.HasAnyThing).ToList();
        w.WriteInt32(moving.Count);
        foreach (TransferableOneWay t in moving)
        {
            w.WriteInt32(t.CountToTransfer);
            w.WriteInt32(t.things.Count);
            foreach (Thing thing in t.things)
            {
                w.WriteInt32(thing.thingIDNumber);
            }
        }
    }

    private static List<(int count, List<int> ids)> ReadGroups(ref PacketReader r)
    {
        int groups = r.ReadCount(MaxItems, 8);
        List<(int, List<int>)> sent = new List<(int, List<int>)>(groups);
        for (int i = 0; i < groups; i++)
        {
            int count = r.ReadInt32();
            int things = r.ReadCount(MaxItems, 4);
            List<int> ids = new List<int>(things);
            for (int k = 0; k < things; k++)
            {
                ids.Add(r.ReadInt32());
            }
            sent.Add((count, ids));
        }
        return sent;
    }

    private static void ApplyGroups(List<TransferableOneWay> transferables, List<(int count, List<int> ids)> sent)
    {
        foreach ((int count, List<int> ids) in sent)
        {
            TransferableOneWay? t = transferables.FirstOrDefault(x => x.things.Any(th => ids.Contains(th.thingIDNumber)));
            t?.AdjustTo(Math.Min(count, t.MaxCount));
        }
    }

    private static void ExecuteReform(ref PacketReader r)
    {
        int mapId = r.ReadInt32();
        bool aboutToBeRemoved = r.ReadBool();
        PlanetTile dest = ReadTile(ref r);
        List<(int count, List<int> ids)> sent = ReadGroups(ref r);
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        if (map == null)
        {
            TestAutomation.Note($"world: reform: map {mapId} is gone, dropped");
            return;
        }
        MapRandContext.Enter(map);
        Dialog_FormCaravan dialog = new Dialog_FormCaravan(map, reform: true, mapAboutToBeRemoved: aboutToBeRemoved);
        FormRecacheMethod.Invoke(dialog, null);
        ApplyGroups(dialog.transferables, sent);
        List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(dialog.transferables);
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(pawns.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        FormDestinationField.SetValue(dialog, dest);
        bool ok = (bool)ReformMethod.Invoke(dialog, null)!;
        if (ok)
        {
            SharedWindows.CloseMatching(w => w is Dialog_FormCaravan d && ReferenceEquals(FormMapField.GetValue(d), map));
        }
        TestAutomation.Note($"world: reform by player {CommandBus.CurrentPlayerId}, map {mapId}, pawns {pawns.Count}: {(ok ? "done" : "rejected")}");
    }

    // ---------- колонии игроков ----------

    /// <summary>Поселения игрока без владельца — автору команды (первая колония — хоста).</summary>
    public static void SendColonyClaim()
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpColonyClaim);
        SendWorld(w);
    }

    /// <summary>Присоединиться к колонии соавтором.</summary>
    public static void SendColonyJoin(Settlement settlement)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpColonyJoin);
        w.WriteInt32(settlement.ID);
        SendWorld(w);
    }

    /// <summary>Своя колония: новая стартовая группа основывает поселение на клетке.</summary>
    public static void SendColonyNew(PlanetTile tile)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpColonyNew);
        WriteTile(w, tile);
        SendWorld(w);
    }

    /// <summary>Тест: игрок «не был в сети 10 лет» — следующая суточная проверка покинет его колонию.</summary>
    public static void SendColonyTestAbsent(int player)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpColonyTestAbsent);
        w.WriteInt32(player);
        SendWorld(w);
    }

    /// <summary>Тест стазиса: игрок «не в сети» / «снова в сети» в состоянии игры.</summary>
    public static void SendTestPresence(int player, bool online)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpTestPresence);
        w.WriteInt32(player);
        w.WriteBool(online);
        SendWorld(w);
    }

    /// <summary>Тест адаптации по поселению: погиб колонист на карте поселения.</summary>
    public static void SendTestKill(int mapId)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpTestKill);
        w.WriteInt32(mapId);
        SendWorld(w);
    }

    /// <summary>Тест часов пешки: перевести колониста с карты from на карту to (как караваном, но сразу).</summary>
    // T-081: маленький корабль — настил 9×9 у центра карты на свободных клетках и двигатель посередине.
    private static string TestGravshipBuild(Map map)
    {
        for (int dz = 0; dz < 60; dz += 3)
        {
            foreach (int dx in new[] { 0, 12, -12, 24, -24 })
            {
                IntVec3 c0 = map.Center + new IntVec3(dx, 0, 20 + dz);
                CellRect rect = CellRect.CenteredOn(c0, 4);
                if (!rect.InBounds(map) || rect.Cells.Any(c => !c.Standable(map) || c.GetEdifice(map) != null || c.GetFirstPawn(map) != null))
                {
                    continue;
                }
                foreach (IntVec3 c in rect.Cells)
                {
                    map.terrainGrid.SetFoundation(c, TerrainDefOf.Substructure);
                }
                Thing engine = ThingMaker.MakeThing(ThingDefOf.GravEngine);
                engine.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(engine, c0, map);
                // Пульт пилота обязателен: по нему корабль выбирает направление взлёта (Gravship.DetermineLaunchDirection).
                ThingDef? consoleDef = DefDatabase<ThingDef>.GetNamedSilentFail("PilotConsole");
                if (consoleDef != null)
                {
                    Thing console = ThingMaker.MakeThing(consoleDef, consoleDef.MadeFromStuff ? GenStuff.DefaultStuffFor(consoleDef) : null);
                    console.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(console, c0 + new IntVec3(0, 0, 3), map, Rot4.North);
                }
                (engine as Building_GravEngine)?.ForceSubstructureDirty();
                int valid = (engine as Building_GravEngine)?.ValidSubstructure.Count ?? -1;
                return $"built engine {engine.thingIDNumber} at {c0}, substructure {rect.Area}, valid {valid}";
            }
        }
        return "no free place for the test ship";
    }

    private static string TestGravshipLaunch(Map map)
    {
        Building_GravEngine? engine = map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).OfType<Building_GravEngine>().FirstOrDefault();
        if (engine == null)
        {
            return "no engine to launch";
        }
        List<PlanetTile> near = new List<PlanetTile>();
        Find.WorldGrid.GetTileNeighbors(map.Tile, near);
        PlanetTile target = near.Where(t => !Find.WorldObjects.AnyWorldObjectAt(t)).OrderBy(t => t.tileId).FirstOrDefault();
        engine.launchInfo ??= new LaunchInfo { quality = 1f };
        Find.GravshipController.InitiateTakeoff(engine, target);
        return $"launch requested to tile {target}, maps now {Find.Maps.Count}, gravship {(Current.Game.Gravship != null ? "in flight" : "none")}";
    }

    /// <summary>Тест часов пешки (clocktest): поле-отметка времени в <c>mindState</c> со значением &gt; 0, lastIngestTick в приоритете, затем по имени.</summary>
    private static System.Reflection.FieldInfo? ClockTestField(Pawn p)
    {
        if (p.mindState == null) return null;
        return p.mindState.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(int) && f.Name.EndsWith("Tick", System.StringComparison.Ordinal) && (int)f.GetValue(p.mindState)! > 0)
            .OrderBy(f => f.Name == "lastIngestTick" ? 0 : 1).ThenBy(f => f.Name, System.StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static void SendTestTransfer(int fromMap, int toMap)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(OpTestTransfer);
        w.WriteInt32(fromMap);
        w.WriteInt32(toMap);
        SendWorld(w);
    }

    /// <summary>События карты (T-056, Sim/MapEvents.cs, операция 40).</summary>
    public static void SendMapEvents(System.Action<PacketWriter> write) => SendOp(RWMLWC.Client.Sim.MapEvents.OpMapEvents, write);

    /// <summary>Операция мира с произвольным телом (40 — события карты, 42 — посадка гравикорабля).</summary>
    public static void SendOp(byte op, System.Action<PacketWriter> write)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(op);
        write(w);
        SendWorld(w);
    }

    public static void SendTestOp(byte op, int a, int b)
    {
        PacketWriter w = BeginWorld();
        w.WriteByte(op);
        w.WriteInt32(a);
        w.WriteInt32(b);
        SendWorld(w);
    }

    public const byte TestTraderOp = OpTestTrader;
    public const byte TestGoTradeOp = OpTestGoTrade;
    public const byte TestHomeAreaOp = OpTestHomeArea;
    public const byte TestPodKitOp = OpTestPodKit;
    public const byte TestGravshipOp = OpTestGravship;

    /// <summary>Тест капсул: как если бы игрок построил пусковую площадку с капсулой у колонистов и заправил её.</summary>
    private static string SpawnPodKit(Map map)
    {
        List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();
        if (colonists.Count == 0) return "no colonists";
        IntVec3 center = new IntVec3((int)colonists.Average(p => p.Position.x), 0, (int)colonists.Average(p => p.Position.z));
        Rot4 rot = Rot4.North;
        bool Free(IntVec3 c) => c.InBounds(map) && c.Standable(map) && !c.Roofed(map) && !c.Fogged(map)
            && c.GetEdifice(map) == null && c.GetFirstItem(map) == null && c.GetFirstPawn(map) == null && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy);
        foreach (IntVec3 c in GenRadial.RadialCellsAround(center, 40f, useCenter: true))
        {
            CellRect rect = GenAdj.OccupiedRect(c, rot, DefDatabase<ThingDef>.GetNamed("PodLauncher").size);
            IntVec3 port = FuelingPortUtility.GetFuelingPortCell(c, rot);
            if (!rect.Cells.All(Free) || !Free(port) || rect.Contains(port))
            {
                continue;
            }
            Thing launcher = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("PodLauncher"));
            launcher.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(launcher, c, map, rot);
            launcher.TryGetComp<CompRefuelable>()?.Refuel(150f);
            Thing pod = ThingMaker.MakeThing(ThingDefOf.TransportPod);
            pod.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(pod, port, map);
            // Стартовые вещи (сталь с корабля) запрещены — игра их в капсулы не грузит; игрок снял бы запрет.
            int allowed = 0;
            foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.Spawned && t.IsForbidden(Faction.OfPlayer)).ToList())
            {
                t.SetForbidden(false, warnOnFail: false);
                allowed++;
            }
            return $"launcher {launcher.thingIDNumber} at {c}, pod {pod.thingIDNumber} at {port}, unforbidden {allowed}";
        }
        return "no place";
    }

    private static void ExecuteColony(byte op, ref PacketReader r)
    {
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        int player = CommandBus.CurrentPlayerId;
        if (colonies == null)
        {
            return;
        }
        MapRandContext.Enter(null);
        switch (op)
        {
            case OpColonyClaim:
                if (colonies.AnyOwner)
                {
                    TestAutomation.Note($"colonies: claim by player {player} ignored — owners already set");
                    return;
                }
                TestAutomation.Note($"colonies: player {player} is the host, settlements claimed: {colonies.ClaimUnowned(player)}");
                break;
            case OpColonyJoin:
                {
                    int id = r.ReadInt32();
                    Settlement? s = Find.WorldObjects.Settlements.FirstOrDefault(x => x.ID == id && x.Faction == Faction.OfPlayer);
                    if (s == null || colonies.OwnsColony(player))
                    {
                        TestAutomation.Note($"colonies: join {id} by player {player} rejected");
                        return;
                    }
                    colonies.SetMember(player, s);
                    TestAutomation.Note($"colonies: player {player} joined settlement {id} (owner {colonies.OwnerOf(s)})");
                    break;
                }
            case OpTestTrader:
            case OpTestGoTrade:
                {
                    int a = r.ReadInt32();
                    int b = r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("tradetest=1"))
                    {
                        return;
                    }
                    Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == a);
                    if (map == null) return;
                    MapRandContext.Enter(map);
                    if (op == OpTestTrader)
                    {
                        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                        parms.forced = true;
                        bool ok = IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(parms);
                        TestAutomation.Note($"trade test: trader caravan on map {a}: {ok}");
                    }
                    else
                    {
                        Pawn? trader = map.mapPawns.AllPawnsSpawned.Where(p => p.trader != null && p.CanTradeNow).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                        Pawn? colonist = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                        if (trader == null || colonist == null)
                        {
                            TestAutomation.Note($"trade test: no trader ({trader != null}) or colonist ({colonist != null}) on map {a}");
                            return;
                        }
                        colonist.jobs.TryTakeOrderedJob(Verse.JobMaker.MakeJob(JobDefOf.TradeWithPawn, trader), Verse.AI.JobTag.Misc);
                        TestAutomation.Note($"trade test: colonist {colonist.thingIDNumber} goes to trade with {trader.thingIDNumber}, ordered by player {CommandBus.CurrentPlayerId}");
                    }
                    break;
                }
            case OpTestHomeArea:
                {
                    int a = r.ReadInt32();
                    r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("exchangetest=1"))
                    {
                        return;
                    }
                    Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == a);
                    if (map == null) return;
                    // Как если бы игрок обвёл склад домашней зоной: окно каравана предлагает только вещи дома или на складе.
                    int cells = 0;
                    foreach (Thing t in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.Spawned && !t.Position.Fogged(map)).ToList())
                    {
                        if (!map.areaManager.Home[t.Position])
                        {
                            map.areaManager.Home[t.Position] = true;
                            cells++;
                        }
                    }
                    TestAutomation.Note($"exchange: home area on map {a}: +{cells} cells under items");
                    break;
                }
            case OpTestPodKit:
                {
                    int a = r.ReadInt32();
                    r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("podtest=1"))
                    {
                        return;
                    }
                    Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == a);
                    if (map == null) return;
                    MapRandContext.Enter(map);
                    TestAutomation.Note("pods: kit on map " + a + ": " + SpawnPodKit(map));
                    break;
                }
            case OpTestGravship:
                {
                    int a = r.ReadInt32();
                    r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("gravtest=1"))
                    {
                        return;
                    }
                    Map map = Find.Maps[0];
                    MapRandContext.Enter(map);
                    TestAutomation.Note("gravship test: " + (a == 0 ? TestGravshipBuild(map) : TestGravshipLaunch(map)));
                    break;
                }
            case OpTestTransfer:
                {
                    int fromId = r.ReadInt32();
                    int toId = r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("clocktest=1"))
                    {
                        return;
                    }
                    Map? from = Find.Maps.FirstOrDefault(m => m.uniqueID == fromId);
                    Map? to = Find.Maps.FirstOrDefault(m => m.uniqueID == toId);
                    // Сначала колонист, который уже ел; если такого нет (стартовые колонисты карты, замёрзшей рано,
                    // ещё не ели) — любой колонист с отметкой времени в mindState. Сравнивается «сколько тиков назад».
                    Pawn? pawn = from?.mapPawns.FreeColonistsSpawned.Where(p => p.mindState.lastIngestTick > 0).OrderBy(p => p.thingIDNumber).FirstOrDefault()
                        ?? from?.mapPawns.FreeColonistsSpawned.Where(p => ClockTestField(p) != null).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    System.Reflection.FieldInfo? field = pawn == null ? null : ClockTestField(pawn);
                    if (pawn == null || to == null || field == null)
                    {
                        TestAutomation.Note($"clock test: nobody to move {fromId} -> {toId}");
                        return;
                    }
                    MapRandContext.Enter(from);
                    int agoBefore = Find.TickManager.TicksGame - (int)field.GetValue(pawn.mindState)!;
                    pawn.DeSpawn();
                    MapRandContext.Enter(to);
                    IntVec3 cell = CellFinder.RandomClosewalkCellNear(to.Center, to, 20);
                    GenSpawn.Spawn(pawn, cell, to);
                    int agoAfter = Find.TickManager.TicksGame - (int)field.GetValue(pawn.mindState)!;
                    TestAutomation.Note($"clock test: pawn {pawn.thingIDNumber} {fromId}->{toId}: {field.Name} {agoBefore} ticks ago before, {agoAfter} after");
                    break;
                }
            case OpTestKill:
                {
                    int mapId = r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("adapttest=1"))
                    {
                        return;
                    }
                    Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                    Pawn? victim = map?.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
                    if (victim == null)
                    {
                        return;
                    }
                    MapRandContext.Enter(map);
                    RWMLWC.Client.World.TestSafety.AllowNextKill = true; // намеренная гибель — мимо защиты nodeath
                    victim.Kill(null);
                    TestAutomation.Note($"adapt test: colonist {victim.thingIDNumber} of map {mapId} killed");
                    break;
                }
            case OpTestPresence:
                {
                    int target = r.ReadInt32();
                    bool online = r.ReadBool();
                    if (!RWMLWC.Client.World.CaravanSelfTest.ArmKey("stasistest=1"))
                    {
                        return;
                    }
                    RWMLWC.Client.Maps.PlayerPresence.Component?.Apply(target, online);
                    TestAutomation.Note($"stasis test: player {target} {(online ? "online" : "offline")}");
                    break;
                }
            case OpColonyTestAbsent:
                {
                    int target = r.ReadInt32();
                    if (!RWMLWC.Client.World.CaravanSelfTest.AbandonTestArmed)
                    {
                        return; // вне тестового прогона команда ничего не делает
                    }
                    colonies.DebugSetLastActive(target, Find.TickManager.TicksGame - RWMLWC.Client.World.ColoniesComponent.AbandonAfterTicks - 1);
                    RWMLWC.Client.Maps.PlayerPresence.Component?.Apply(target, false);
                    TestAutomation.Note($"colonies: test — player {target} marked absent for 10 years");
                    break;
                }
            case OpColonyNew:
                {
                    PlanetTile tile = ReadTile(ref r);
                    if (colonies.OwnsColony(player) || !TileFinder.IsValidTileForNewSettlement(tile))
                    {
                        TestAutomation.Note($"colonies: new colony at {tile} by player {player} rejected");
                        return;
                    }
                    RWMLWC.Client.World.NewColony.Found(tile, player);
                    break;
                }
        }
    }

    public static void ExecuteWorldAction(ref PacketReader r)
    {
        byte op = r.ReadByte();
        if (op == OpGizmo)
        {
            ExecuteWorldGizmo(ref r);
            return;
        }
        if (op == RWMLWC.Client.World.TradeCommands.OpTrade)
        {
            RWMLWC.Client.World.TradeCommands.Execute(ref r);
            return;
        }
        if (op == RWMLWC.Client.World.Rentals.OpLend || op == RWMLWC.Client.World.Rentals.OpRecall)
        {
            RWMLWC.Client.World.Rentals.Execute(op, ref r);
            return;
        }
        if (op == RWMLWC.Client.Sim.GravshipSession.OpLand)
        {
            RWMLWC.Client.Sim.GravshipSession.ExecuteLand(ref r);
            return;
        }
        if (op == RWMLWC.Client.Sim.MapEvents.OpMapEvents)
        {
            RWMLWC.Client.Sim.MapEvents.Execute(ref r);
            return;
        }
        if (op == RWMLWC.Client.Absence.Sos.OpSos)
        {
            RWMLWC.Client.Absence.Sos.Execute(ref r);
            return;
        }
        if (op == RWMLWC.Client.World.TransportPods.OpLoad || op == RWMLWC.Client.World.TransportPods.OpLaunch)
        {
            RWMLWC.Client.World.TransportPods.Execute(op, ref r);
            return;
        }
        if (op >= SharedWindows.OpCount && op <= SharedWindows.OpSlider)
        {
            SharedWindows.Execute(op, ref r);
            return;
        }
        if (op == OpColonyClaim || op == OpColonyJoin || op == OpColonyNew || op == OpColonyTestAbsent || op == OpTestPresence || op == OpTestKill || op == OpTestTransfer || op == OpTestTrader || op == OpTestGoTrade || op == OpTestHomeArea || op == OpTestPodKit || op == OpTestGravship)
        {
            ExecuteColony(op, ref r);
            return;
        }
        if (op == OpReform)
        {
            ExecuteReform(ref r);
            return;
        }
        if (op == OpSplit)
        {
            ExecuteSplit(FindCaravan(r.ReadInt32()), ref r);
            return;
        }
        Caravan? caravan = FindCaravan(r.ReadInt32());
        switch (op)
        {
            case OpSettle:
                if (caravan == null)
                {
                    TestAutomation.Note("world: settle: caravan is gone, dropped");
                    return;
                }
                if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                        RWMLWC.Client.Maps.SquadRules.ForbiddenAny(caravan.PawnsListForReading.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
                {
                    return;
                }
                if (RWMLWC.Client.World.Colonies.Component?.OwnsColony(CommandBus.CurrentPlayerId) == true)
                {
                    TestAutomation.Note($"world: settle by player {CommandBus.CurrentPlayerId} rejected — already owns a colony");
                    return;
                }
                SettleInEmptyTileUtility.Settle(caravan);
                TestAutomation.Note($"world: settled by player {CommandBus.CurrentPlayerId}, caravan {caravan.ID}");
                break;
            case OpRoute:
                {
                    PlanetTile dest = ReadTile(ref r);
                    bool repath = r.ReadBool();
                    bool reset = r.ReadBool();
                    CaravanArrivalAction? arrival = ReadArrival(ref r, out bool arrivalFailed);
                    if (caravan == null || arrivalFailed)
                    {
                        return;
                    }
                    if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                            RWMLWC.Client.Maps.SquadRules.ForbiddenAny(caravan.PawnsListForReading.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
                    {
                        return;
                    }
                    caravan.pather.StartPath(dest, arrival, repath, reset);
                    RWMLWC.Client.Maps.GuestsComponent.NoteRoute(caravan, CommandBus.CurrentPlayerId);
                    if (arrival != null)
                    {
                        TestAutomation.Note($"world: route of caravan {caravan.ID} to {dest} with {arrival.GetType().Name} by player {CommandBus.CurrentPlayerId}");
                    }
                    break;
                }
            default:
                TestAutomation.Note($"world: unknown op {op}");
                break;
        }
    }
}

[HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.StartFormingCaravan))]
internal static class Patch_CaravanFormingUtility_StartFormingCaravan
{
    private static bool Prefix(List<Pawn> pawns, List<Pawn> downedPawns, List<TransferableOneWay> transferables,
        IntVec3 meetingPoint, IntVec3 exitSpot, PlanetTile startingTile, PlanetTile destinationTile)
    {
        return !CommandBus.CaptureActive || WorldCommands.CaptureCaravanForm(pawns, downedPawns, transferables, meetingPoint, exitSpot, startingTile, destinationTile);
    }
}

[HarmonyPatch(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.Settle))]
internal static class Patch_SettleInEmptyTileUtility_Settle
{
    private static bool Prefix(Caravan caravan)
    {
        return !CommandBus.CaptureActive || WorldCommands.CaptureSettle(caravan);
    }
}

[HarmonyPatch(typeof(Caravan_PathFollower), nameof(Caravan_PathFollower.StartPath))]
internal static class Patch_Caravan_PathFollower_StartPath
{
    private static bool Prefix(Caravan ___caravan, PlanetTile destTile, CaravanArrivalAction arrivalAction, bool repathImmediately, bool resetPauseStatus, ref bool __result)
    {
        if (!CommandBus.CaptureActive || ___caravan == null || ___caravan.Faction != Faction.OfPlayer)
        {
            return true;
        }
        __result = true; // у автора путь появится, когда команда вернётся в общем порядке
        return WorldCommands.CaptureRoute(___caravan, destTile, arrivalAction, repathImmediately, resetPauseStatus);
    }
}

[HarmonyPatch(typeof(Dialog_SplitCaravan), "TrySplitCaravan")]
internal static class Patch_Dialog_SplitCaravan_TrySplitCaravan
{
    private static bool Prefix(Dialog_SplitCaravan __instance, ref bool __result)
    {
        return !CommandBus.CaptureActive || WorldCommands.CaptureSplit(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(Dialog_FormCaravan), "TryReformCaravan")]
internal static class Patch_Dialog_FormCaravan_TryReformCaravan
{
    private static bool Prefix(Dialog_FormCaravan __instance, ref bool __result)
    {
        return !CommandBus.CaptureActive || WorldCommands.CaptureReform(__instance, ref __result);
    }
}
