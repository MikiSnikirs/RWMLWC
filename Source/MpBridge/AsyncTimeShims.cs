using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

// T-085: «своё время карт» RimWorld Multiplayer, которое спрашивают модули Multiplayer Compatibility (VEF, DMS,
// Cinders, Fortified и др.). В RWMLWC время одно на все карты, поэтому время карты и время мира — это
// TickManager.TicksGame, а «тикающая карта» — карта, чей MapPreTick…MapPostTick сейчас идёт. Поля обновляются
// перед каждым тиком; если модуль сам пишет в часы карты, это ни на что не влияет (одинаково у всех игроков).

namespace Multiplayer.Client
{
    public class AsyncTimeComp
    {
        public static Map? tickingMap;
        public static Map? executingCmdMap;
        public Map map;
        public int mapTicks;
        public TimeSpeed timeSpeed = TimeSpeed.Normal;

        public AsyncTimeComp(Map map)
        {
            this.map = map;
        }

        public static int GetWorldTicks() => Find.TickManager?.TicksGame ?? 0;

        public static int GetWorldTicksAbs() => Find.TickManager?.TicksAbs ?? 0;

        public void PreContext() { }

        public void PostContext() { }
    }

    public static partial class Extensions
    {
        private static readonly Dictionary<Map, AsyncTimeComp> comps = new Dictionary<Map, AsyncTimeComp>();

        public static AsyncTimeComp AsyncTime(this Map map)
        {
            if (!comps.TryGetValue(map, out AsyncTimeComp? comp))
            {
                comp = new AsyncTimeComp(map);
                comps[map] = comp;
            }
            comp.mapTicks = Find.TickManager?.TicksGame ?? 0;
            return comp;
        }

        internal static void UpdateAll()
        {
            int ticks = Find.TickManager?.TicksGame ?? 0;
            List<Map>? dead = null;
            foreach (KeyValuePair<Map, AsyncTimeComp> pair in comps)
            {
                if (pair.Key.Disposed)
                {
                    (dead ??= new List<Map>()).Add(pair.Key);
                    continue;
                }
                pair.Value.mapTicks = ticks;
            }
            if (dead != null)
            {
                foreach (Map m in dead)
                {
                    comps.Remove(m);
                }
            }
        }
    }

    public class MultiplayerGame
    {
        public AsyncTime.AsyncWorldTimeComp asyncWorldTimeComp = new AsyncTime.AsyncWorldTimeComp();
        public Comp.MultiplayerGameComp gameComp = new Comp.MultiplayerGameComp();
    }

    public static class Multiplayer
    {
        public static MultiplayerGame game = new MultiplayerGame();
        public static bool dontSync;
    }

    public static class LoadPatch
    {
        public static bool IsInitializingCaches => false;
    }

    public static class SimulationTimeContext
    {
        public static object CurrentOwner => (object?)AsyncTimeComp.tickingMap?.AsyncTime() ?? Multiplayer.game.asyncWorldTimeComp;

        public static int ReadTicks(object owner) => Find.TickManager?.TicksGame ?? 0;

        public static void ReadClock(object owner, out int ticks, out TimeSpeed speed, out TimeSlower slower, out int gameStartAbsTick)
        {
            ticks = Find.TickManager?.TicksGame ?? 0;
            speed = Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Normal;
            slower = Find.TickManager?.slower ?? new TimeSlower();
            gameStartAbsTick = Find.TickManager?.gameStartAbsTick ?? 0;
        }
    }

    /// <summary>Обновление «часов» перед тиком и «тикающей карты» на время её тика.</summary>
    internal static class AsyncTimeShimPatches
    {
        private static bool installed;

        public static void Install()
        {
            if (installed)
            {
                return;
            }
            installed = true;
            Harmony h = new Harmony("MikiSnikirs.RWMLWC.MpShims");
            h.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                prefix: new HarmonyMethod(typeof(AsyncTimeShimPatches), nameof(BeforeTick)) { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapPreTick)),
                prefix: new HarmonyMethod(typeof(AsyncTimeShimPatches), nameof(MapTickBegin)) { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapPostTick)),
                finalizer: new HarmonyMethod(typeof(AsyncTimeShimPatches), nameof(MapTickEnd)));
        }

        private static void BeforeTick()
        {
            int ticks = Find.TickManager.TicksGame;
            Multiplayer.game.asyncWorldTimeComp.worldTicks = ticks;
            Extensions.UpdateAll();
        }

        private static void MapTickBegin(Map __instance)
        {
            AsyncTimeComp.tickingMap = __instance;
            __instance.AsyncTime();
        }

        private static void MapTickEnd()
        {
            AsyncTimeComp.tickingMap = null;
        }
    }
}

namespace Multiplayer.Client.AsyncTime
{
    public class AsyncWorldTimeComp
    {
        public int worldTicks;
        public TimeSpeed timeSpeed = TimeSpeed.Normal;
    }
}

namespace Multiplayer.Client.Comp
{
    public class MultiplayerGameComp
    {
        public bool asyncTime;
    }
}
