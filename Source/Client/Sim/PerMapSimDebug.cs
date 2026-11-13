using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Отладочные действия для проверки T-005 (меню отладочных действий, раздел RWMLWC).
/// </summary>
internal static class PerMapSimDebug
{
    [DebugAction("RWMLWC", "Per-map sim: on/off", allowedGameStates = AllowedGameStates.Playing)]
    private static void Toggle()
    {
        if (PerMapSim.Enabled)
        {
            // Вернуть Rand в глобальный контекст, пока патчи ещё активны.
            MapRandContext.ExitToGlobal();
        }
        PerMapSim.Enabled = !PerMapSim.Enabled;
        Say("Per-map sim: " + (PerMapSim.Enabled ? "ON" : "OFF"));
    }

    [DebugAction("RWMLWC", "Freeze/unfreeze current map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void ToggleFreezeCurrentMap()
    {
        Map? map = Find.CurrentMap;
        if (map == null)
        {
            return;
        }
        MapSimState? state = MapSimState.For(map);
        if (state == null)
        {
            Log.Error("[RWMLWC] у карты нет MapSimState");
            return;
        }
        state.frozen = !state.frozen;
        string note = PerMapSim.Enabled ? "" : " (per-map sim is OFF, no effect)";
        Say($"Map {map.uniqueID}: " + (state.frozen ? "FROZEN" : "running") + note);
    }

    [DebugAction("RWMLWC", "Log per-map sim state", allowedGameStates = AllowedGameStates.Playing)]
    private static void LogState()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"[RWMLWC] per-map sim: enabled={PerMapSim.Enabled}, tick={Find.TickManager.TicksGame}, sessionSeed={PerMapSim.SessionSeed}");
        Map? ctx = MapRandContext.Current;
        sb.AppendLine("  Rand context: " + (ctx == null ? "global" : "map " + ctx.uniqueID)
            + $", raw=0x{MapRandContext.CurrentRawState:X16}");
        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            Map map = maps[i];
            MapSimState? state = MapSimState.For(map);
            if (state == null)
            {
                sb.AppendLine($"  map {map.uniqueID}: NO MapSimState");
                continue;
            }
            sb.AppendLine($"  map {map.uniqueID}: frozen={state.frozen}, randInitialized={state.randInitialized}, rand=0x{state.randState:X16}");
        }
        Log.Message(sb.ToString().TrimEnd());
    }

    private static void Say(string text)
    {
        Log.Message("[RWMLWC] " + text);
        Messages.Message("[RWMLWC] " + text, MessageTypeDefOf.NeutralEvent, false);
    }
}
