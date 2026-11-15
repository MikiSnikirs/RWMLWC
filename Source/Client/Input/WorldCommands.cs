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
        TestAutomation.Note($"world: reform by player {CommandBus.CurrentPlayerId}, map {mapId}, pawns {pawns.Count}: {(ok ? "done" : "rejected")}");
    }

    public static void ExecuteWorldAction(ref PacketReader r)
    {
        byte op = r.ReadByte();
        if (op == OpGizmo)
        {
            ExecuteWorldGizmo(ref r);
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
