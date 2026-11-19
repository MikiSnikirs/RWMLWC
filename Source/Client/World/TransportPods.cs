using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Транспортные капсулы в сессии (T-053). Кнопки капсул и так идут командой (GizmoCommands), окно загрузки
/// открывается у всех общим окном (SharedWindows). Здесь — то, что игра делала бы только у нажавшего:
///  - «Принять» в окне загрузки: команда с капсулами и количествами по номерам вещей; у всех окно собирается
///    заново и вызывается тот же <c>TryAccept</c> (назначение груза капсулам, работа на погрузку);
///  - выбор цели на карте мира открывается только у автора нажатия «Запустить»;
///  - сам запуск (<c>CompLaunchable.TryLaunch</c>): команда с капсулой, клеткой мира и действием по прибытии;
///    действие передаётся по полям (объект мира — по номеру, остальное — ValueCodec), у всех создаётся заново.
/// Пешки в капсулах запоминаются за игроком, который запустил; если они высаживаются в чужой колонии — гости
/// (<see cref="RWMLWC.Client.Maps.GuestsComponent"/>), как пешки каравана.
/// </summary>
public static class TransportPods
{
    internal const byte OpLoad = 21;
    internal const byte OpLaunch = 22;

    private const byte FieldWorldObject = 1;
    private const byte FieldValue = 2;
    private const int MaxItems = 4096;

    private static readonly FieldInfo DialogMap = AccessTools.Field(typeof(Dialog_LoadTransporters), "map");
    private static readonly FieldInfo DialogTransporters = AccessTools.Field(typeof(Dialog_LoadTransporters), "transporters");
    private static readonly FieldInfo DialogTransferables = AccessTools.Field(typeof(Dialog_LoadTransporters), "transferables");
    private static readonly MethodInfo DialogRecache = AccessTools.Method(typeof(Dialog_LoadTransporters), "CalculateAndRecacheTransferables");
    internal static readonly MethodInfo DialogTryAccept = AccessTools.Method(typeof(Dialog_LoadTransporters), "TryAccept");

    public static int Captured;

    private static void Send(PacketWriter w)
    {
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
        Captured++;
    }

    private static PacketWriter Begin(byte op)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(op);
        return w;
    }

    // ---------- загрузка ----------

    internal static bool CaptureLoad(Dialog_LoadTransporters dialog, ref bool result)
    {
        Map map = (Map)DialogMap.GetValue(dialog)!;
        List<CompTransporter> transporters = (List<CompTransporter>)DialogTransporters.GetValue(dialog)!;
        List<TransferableOneWay> transferables = (List<TransferableOneWay>)DialogTransferables.GetValue(dialog)!;
        List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(transferables);
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(pawns.Cast<object>()))
        {
            result = false;
            return false;
        }
        PacketWriter w = Begin(OpLoad);
        w.WriteInt32(map.uniqueID);
        w.WriteInt32(transporters.Count);
        foreach (CompTransporter t in transporters)
        {
            w.WriteInt32(t.parent.thingIDNumber);
        }
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
        Send(w);
        TestAutomation.Note($"pods: load sent, map {map.uniqueID}, pods {transporters.Count}, item groups {moving.Count}, pawns {pawns.Count}");
        result = true; // окно закрывается; погрузка начнётся, когда команда вернётся в общем порядке
        return false;
    }

    private static void ExecuteLoad(ref PacketReader r)
    {
        int mapId = r.ReadInt32();
        int podCount = r.ReadCount(64, 4);
        List<int> podIds = new List<int>(podCount);
        for (int i = 0; i < podCount; i++)
        {
            podIds.Add(r.ReadInt32());
        }
        int groups = r.ReadCount(MaxItems, 8);
        List<(int count, List<int> ids)> sent = new List<(int, List<int>)>(groups);
        for (int i = 0; i < groups; i++)
        {
            int count = r.ReadInt32();
            int n = r.ReadCount(MaxItems, 4);
            List<int> ids = new List<int>(n);
            for (int k = 0; k < n; k++)
            {
                ids.Add(r.ReadInt32());
            }
            sent.Add((count, ids));
        }
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        if (map == null)
        {
            TestAutomation.Note($"pods: load: map {mapId} is gone, dropped");
            return;
        }
        List<CompTransporter> transporters = new List<CompTransporter>();
        foreach (int id in podIds)
        {
            if (ValueCodec.FindThing(map, id) is ThingWithComps thing && thing.Spawned && thing.GetComp<CompTransporter>() is CompTransporter comp)
            {
                transporters.Add(comp);
            }
        }
        if (transporters.Count == 0)
        {
            TestAutomation.Note("pods: load: no pods left, dropped");
            return;
        }
        MapRandContext.Enter(map);
        Dialog_LoadTransporters dialog = new Dialog_LoadTransporters(map, transporters);
        DialogRecache.Invoke(dialog, null);
        List<TransferableOneWay> transferables = (List<TransferableOneWay>)DialogTransferables.GetValue(dialog)!;
        foreach (TransferableOneWay t in transferables)
        {
            t.AdjustTo(0);
        }
        foreach ((int count, List<int> ids) in sent)
        {
            TransferableOneWay? t = transferables.FirstOrDefault(x => x.things.Any(th => ids.Contains(th.thingIDNumber)));
            t?.AdjustTo(Math.Min(count, t.MaxCount));
        }
        List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(transferables);
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(pawns.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        bool ok = (bool)DialogTryAccept.Invoke(dialog, null)!;
        if (ok)
        {
            SharedWindows.CloseMatching(w => w is Dialog_LoadTransporters d && ReferenceEquals(DialogMap.GetValue(d), map)
                && ((List<CompTransporter>)DialogTransporters.GetValue(d)!).Any(transporters.Contains));
        }
        TestAutomation.Note($"pods: load by player {CommandBus.CurrentPlayerId}, map {mapId}, pods {transporters.Count}, item groups {sent.Count}, pawns {pawns.Count}: {(ok ? "started" : "rejected")}");
    }

    // ---------- запуск ----------

    internal static bool CaptureLaunch(CompLaunchable launchable, PlanetTile destinationTile, TransportersArrivalAction? arrivalAction)
    {
        Map? map = launchable.parent.MapHeld;
        if (map == null)
        {
            return true;
        }
        List<CompTransporter>? group = launchable.parent.GetComp<CompTransporter>()?.TransportersInGroup(map);
        List<Pawn> pawns = group == null ? new List<Pawn>() : PawnsIn(group);
        if (!RWMLWC.Client.Maps.SquadRules.LocalMay(pawns.Cast<object>()))
        {
            return false;
        }
        PacketWriter w = Begin(OpLaunch);
        w.WriteInt32(map.uniqueID);
        w.WriteInt32(launchable.parent.thingIDNumber);
        WorldCommands.WriteTile(w, destinationTile);
        if (!WriteArrival(w, arrivalAction, out string why))
        {
            Messages.Message("RWMLWC: такое прибытие капсул пока не работает в сетевой игре (" + why + ")", MessageTypeDefOf.RejectInput, false);
            return false;
        }
        Send(w);
        TestAutomation.Note($"pods: launch sent, pod {launchable.parent.thingIDNumber}, to {destinationTile}, arrival {arrivalAction?.GetType().Name ?? "none"}, pawns {pawns.Count}");
        return false;
    }

    private static List<Pawn> PawnsIn(List<CompTransporter> group) =>
        group.SelectMany(t => t.innerContainer).OfType<Pawn>().ToList();

    private static void ExecuteLaunch(ref PacketReader r)
    {
        int mapId = r.ReadInt32();
        int podId = r.ReadInt32();
        PlanetTile dest = WorldCommands.ReadTile(ref r);
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        if (map == null)
        {
            TestAutomation.Note($"pods: launch: map {mapId} is gone, dropped");
            return;
        }
        TransportersArrivalAction? arrival;
        try
        {
            arrival = ReadArrival(ref r, map);
        }
        catch (Exception e)
        {
            TestAutomation.Note("pods: launch: arrival action unreadable, dropped: " + e.Message);
            return;
        }
        CompLaunchable? launchable = (ValueCodec.FindThing(map, podId) as ThingWithComps)?.GetComp<CompLaunchable>();
        if (launchable == null || !launchable.parent.Spawned)
        {
            TestAutomation.Note($"pods: launch: pod {podId} is gone, dropped");
            return;
        }
        List<CompTransporter> group = launchable.parent.GetComp<CompTransporter>()?.TransportersInGroup(map) ?? new List<CompTransporter>();
        List<Pawn> pawns = PawnsIn(group);
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(pawns.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        int pods = group.Count;
        RWMLWC.Client.Maps.GuestsComponent.NotePodPawns(pawns, CommandBus.CurrentPlayerId);
        MapRandContext.Enter(map);
        Map? viewBefore = Find.CurrentMap;
        bool worldBefore = WorldRendererUtility.WorldSelected;
        launching = true;
        try
        {
            launchable.TryLaunch(dest, arrival!);
        }
        finally
        {
            launching = false;
        }
        if (CommandBus.ExecutingRemote)
        {
            // TryLaunch переключает текущую карту и прячет карту мира — это интерфейс автора, не остальных.
            if (viewBefore != null && Find.Maps.Contains(viewBefore) && Find.CurrentMap != viewBefore)
            {
                Current.Game.CurrentMap = viewBefore;
            }
            if (worldBefore && !WorldRendererUtility.WorldSelected)
            {
                CameraJumper.TryShowWorld();
            }
        }
        bool launched = !launchable.parent.Spawned || launchable.parent.Destroyed;
        TestAutomation.Note($"pods: launch by player {CommandBus.CurrentPlayerId}, map {mapId}, pods {pods}, pawns {pawns.Count}, to {dest}, arrival {arrival?.GetType().Name ?? "none"}: {(launched ? "launched" : "not launched")}");
    }

    private static bool launching;

    /// <summary>Чужая команда исполняет запуск: камеру не трогать.</summary>
    internal static bool SuppressCamera => launching && CommandBus.ExecutingRemote;

    // ---------- действие по прибытии ----------

    private static List<FieldInfo> ArrivalFields(Type type)
    {
        List<FieldInfo> fields = new List<FieldInfo>();
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
        return fields.OrderBy(f => f.DeclaringType!.FullName, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    private static bool WriteArrival(PacketWriter w, TransportersArrivalAction? action, out string why)
    {
        why = "";
        if (action == null)
        {
            w.WriteString("");
            return true;
        }
        Type type = action.GetType();
        if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
        {
            why = type.Name + ": нет конструктора без параметров";
            return false;
        }
        List<FieldInfo> fields = ArrivalFields(type);
        foreach (FieldInfo f in fields)
        {
            if (!typeof(WorldObject).IsAssignableFrom(f.FieldType) && !ValueCodec.Supports(f.FieldType))
            {
                why = type.Name + "." + f.Name + ": " + f.FieldType.Name;
                return false;
            }
        }
        w.WriteString(type.FullName!);
        w.WriteInt32(fields.Count);
        foreach (FieldInfo f in fields)
        {
            object? value = f.GetValue(action);
            if (typeof(WorldObject).IsAssignableFrom(f.FieldType))
            {
                w.WriteByte(FieldWorldObject);
                w.WriteInt32(value is WorldObject o ? o.ID : -1);
            }
            else
            {
                w.WriteByte(FieldValue);
                ValueCodec.Write(w, value);
            }
        }
        return true;
    }

    private static TransportersArrivalAction? ReadArrival(ref PacketReader r, Map map)
    {
        string typeName = r.ReadString();
        if (typeName.Length == 0)
        {
            return null;
        }
        Type type = GenTypes.GetTypeInAnyAssembly(typeName) ?? throw new InvalidOperationException("unknown type " + typeName);
        if (!typeof(TransportersArrivalAction).IsAssignableFrom(type))
        {
            throw new InvalidOperationException("not an arrival action: " + typeName);
        }
        TransportersArrivalAction action = (TransportersArrivalAction)Activator.CreateInstance(type, nonPublic: true)!;
        List<FieldInfo> fields = ArrivalFields(type);
        int count = r.ReadCount(64, 2);
        if (count != fields.Count)
        {
            throw new InvalidOperationException($"field count {count} != {fields.Count}");
        }
        foreach (FieldInfo f in fields)
        {
            byte kind = r.ReadByte();
            object? value;
            if (kind == FieldWorldObject)
            {
                int id = r.ReadInt32();
                value = id < 0 ? null : Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.ID == id);
            }
            else
            {
                value = ValueCodec.Read(ref r, map);
            }
            if (value == null && f.FieldType.IsValueType)
            {
                continue;
            }
            f.SetValue(action, value);
        }
        return action;
    }

    // ---------- разбор ----------

    internal static void Execute(byte op, ref PacketReader r)
    {
        if (op == OpLoad)
        {
            ExecuteLoad(ref r);
        }
        else if (op == OpLaunch)
        {
            ExecuteLaunch(ref r);
        }
    }
}

[HarmonyPatch(typeof(Dialog_LoadTransporters), "TryAccept")]
internal static class Patch_Dialog_LoadTransporters_TryAccept
{
    private static bool Prefix(Dialog_LoadTransporters __instance, ref bool __result)
    {
        return !CommandBus.CaptureActive || TransportPods.CaptureLoad(__instance, ref __result);
    }
}

[HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.TryLaunch))]
internal static class Patch_CompLaunchable_TryLaunch
{
    private static bool Prefix(CompLaunchable __instance, PlanetTile destinationTile, TransportersArrivalAction arrivalAction)
    {
        return !CommandBus.CaptureActive || TransportPods.CaptureLaunch(__instance, destinationTile, arrivalAction);
    }
}

/// <summary>Нажатие «Запустить» пришло чужой командой: выбор цели на карте мира — только у автора.</summary>
[HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.StartChoosingDestination))]
internal static class Patch_CompLaunchable_StartChoosingDestination
{
    private static bool Prefix() => !CommandBus.ExecutingRemote;
}

[HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryHideWorld))]
internal static class Patch_CameraJumper_TryHideWorld_Pods
{
    private static bool Prefix() => !TransportPods.SuppressCamera;
}

/// <summary>Капсулы высаживаются на карту: пешки, запущенные другим игроком в его колонию, — гости.</summary>
[HarmonyPatch(typeof(TransportersArrivalActionUtility), nameof(TransportersArrivalActionUtility.DropTravellingDropPods))]
internal static class Patch_DropTravellingDropPods_Guests
{
    private static void Prefix(List<ActiveTransporterInfo> transporters, Map map)
    {
        if (LockstepClient.InSession && transporters != null && map != null)
        {
            RWMLWC.Client.Maps.GuestsComponent.OnPodsDrop(transporters.SelectMany(t => t.innerContainer).OfType<Pawn>().ToList(), map);
        }
    }
}
