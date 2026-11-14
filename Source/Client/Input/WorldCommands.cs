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
        if (arrivalAction != null)
        {
            // Действия по прибытии (войти в поселение, торговать, атаковать…) — следующий шаг T-073.
            Messages.Message("RWMLWC: в сетевой игре пока можно отправить караван только в точку, без действия по прибытии", MessageTypeDefOf.RejectInput, false);
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
        SendWorld(w);
        return false;
    }

    public static void ExecuteWorldAction(ref PacketReader r)
    {
        byte op = r.ReadByte();
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
                    if (caravan == null)
                    {
                        return;
                    }
                    if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                            RWMLWC.Client.Maps.SquadRules.ForbiddenAny(caravan.PawnsListForReading.Cast<object>(), CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
                    {
                        return;
                    }
                    caravan.pather.StartPath(dest, null, repath, reset);
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
