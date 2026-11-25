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
    internal static bool Active => LockstepClient.InSession && ModsConfig.OdysseyActive;
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
