using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// T-081, этап 1: взлёт гравикорабля (Odyssey) в сессии. В игре снятие корабля с карты, брошенная карта и полёт
/// случаются в обратных вызовах катсцены — по кадрам, рендер-снимку и реальному времени (docs/research/sim.md, 6ф),
/// у каждого клиента на своём тике. В сессии <c>InitiateTakeoff</c> (его вызывает кнопка пульта — команда, в тике)
/// делает то же сразу: <c>GenerateGravship</c> → <c>TakeoffEnded</c>, без катсцены и без смены текущей карты.
/// Посадка — этап 2 (подтверждение посадки в игре — кнопка в OnGUI контроллера, не команда).
/// </summary>
internal static class GravshipSession
{
    public const byte OpLand = 42;

    internal static bool Active => LockstepClient.InSession && ModsConfig.OdysseyActive;

    /// <summary>
    /// Нажатие «подтвердить посадку» (кнопка в OnGUI контроллера, не команда игры): в сессии — команда.
    /// При исполнении все вызывают настоящий <c>BeginLanding</c> для того же маркера (номер вещи).
    /// </summary>
    public static void SendLand(GravshipLandingMarker marker)
    {
        Map map = marker.Map;
        RWMLWC.Client.Input.WorldCommands.SendOp(OpLand, w =>
        {
            w.WriteInt32(map.uniqueID);
            w.WriteInt32(marker.thingIDNumber);
            RWMLWC.Client.Input.ValueCodec.WriteCell(w, marker.Position);
            w.WriteInt32(marker.Rotation.AsInt);
        });
        TestAutomation.Note($"gravship: landing confirmed locally on map {map.uniqueID} at {marker.Position}, sent as a command");
    }

    public static void ExecuteLand(ref RWMLWC.Shared.Protocol.PacketReader r)
    {
        int mapId = r.ReadInt32();
        int markerId = r.ReadInt32();
        IntVec3 pos = RWMLWC.Client.Input.ValueCodec.ReadCell(ref r);
        Rot4 rot = new Rot4(r.ReadInt32());
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        GravshipLandingMarker? marker = map?.listerThings.ThingsOfDef(ThingDefOf.GravshipLandingMarker).OfType<GravshipLandingMarker>().FirstOrDefault(m => m.thingIDNumber == markerId);
        if (map == null || marker == null)
        {
            TestAutomation.Note($"gravship: landing command for marker {markerId} on map {mapId} — marker not found, dropped");
            return;
        }
        if (marker.Position != pos || marker.Rotation != rot)
        {
            TestAutomation.Note($"gravship: marker at {marker.Position}/{marker.Rotation} differs from the command's {pos}/{rot}");
        }
        MapRandContext.Enter(map);
        executingLand = true;
        try
        {
            marker.BeginLanding(Find.GravshipController);
        }
        finally
        {
            executingLand = false;
        }
    }

    internal static bool executingLand;
}

[HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateTakeoff))]
internal static class Patch_Gravship_InitiateTakeoff_Session
{
    private static bool Prefix(WorldComponent_GravshipController __instance, Building_GravEngine engine, PlanetTile targetTile)
    {
        if (!GravshipSession.Active)
        {
            return true;
        }
        Map map = engine.Map;
        Traverse t = Traverse.Create(__instance);
        bool anchor = map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor);
        t.Field("map").SetValue(map);
        t.Field("takeoffTile").SetValue(map.Tile);
        t.Field("landingTile").SetValue(targetTile);
        t.Field("mapHasGravAnchor").SetValue(anchor);
        t.Field("isTakeoff").SetValue(true);
        t.Field("cutsceneInProgress").SetValue(true);
        Gravship gravship = GravshipUtility.GenerateGravship(engine);
        t.Field("gravship").SetValue(gravship);
        if (anchor)
        {
            map.GetComponent<VacuumComponent>()?.RebuildData();
        }
        TestAutomation.Note($"gravship: takeoff from map {map.uniqueID} to tile {targetTile} at tick {Find.TickManager.TicksGame}: {gravship?.Things.Count() ?? -1} things, {gravship?.Pawns.Count() ?? -1} pawns");
        t.Method("TakeoffEnded").GetValue();
        return false;
    }
}

[HarmonyPatch(typeof(GravshipLandingMarker), nameof(GravshipLandingMarker.BeginLanding))]
internal static class Patch_GravshipLandingMarker_BeginLanding_Session
{
    private static bool Prefix(GravshipLandingMarker __instance)
    {
        if (!GravshipSession.Active || GravshipSession.executingLand)
        {
            return true;
        }
        GravshipSession.SendLand(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(WorldComponent_GravshipController), nameof(WorldComponent_GravshipController.InitiateLanding))]
internal static class Patch_Gravship_InitiateLanding_Session
{
    // T-081, этап 2: посадка сразу, в тике команды — без снимков рендером, катсцены и таймера реального времени.
    private static bool Prefix(WorldComponent_GravshipController __instance, Gravship gravship, Map map, IntVec3 landingPos, Rot4 landingRot)
    {
        if (!GravshipSession.Active)
        {
            return true;
        }
        Traverse t = Traverse.Create(__instance);
        t.Field("map").SetValue(map);
        t.Field("gravship").SetValue(null);
        t.Field("cutsceneInProgress").SetValue(true);
        t.Field("isTakeoff").SetValue(false);
        GravshipLandingMarker? marker = t.Field("landingMarker").GetValue<GravshipLandingMarker>();
        if (marker != null)
        {
            System.Collections.Generic.HashSet<IntVec3> cells = new System.Collections.Generic.HashSet<IntVec3>(marker.GravshipCells.Select(c => c + landingPos));
            WorldComponent_GravshipController.DestroyTreesAroundSubstructure(map, cells);
        }
        Building_GravEngine engine = t.Method("PlaceGravship", new[] { typeof(Gravship), typeof(IntVec3), typeof(Map) }).GetValue<Building_GravEngine>(gravship, landingPos, map);
        t.Field("gravship").SetValue(gravship);
        TestAutomation.Note($"gravship: landed on map {map.uniqueID} at {landingPos} rot {landingRot} at tick {Find.TickManager.TicksGame}, engine {engine?.thingIDNumber}");
        t.Method("LandingEnded").GetValue();
        return false;
    }
}
