using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Sim
{
    /// <summary>Смещение локальных часов карты, которая сейчас в контексте симуляции (0 — мировое время).</summary>
    public static class StasisClock
    {
        public static int Offset;
    }

    /// <summary>
    /// Пока тикает карта, побывавшая в стазисе, <c>TicksGame</c> — её локальное время (мировое минус время в стазисе).
    /// Дата и сезон (<c>TicksAbs</c>) — по общим часам. Вне тика (интерфейс) — мировое время.
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TicksGame), MethodType.Getter)]
    internal static class Patch_TickManager_TicksGame_Local
    {
        private static void Postfix(ref int __result)
        {
            if (StasisClock.Offset != 0)
            {
                __result -= StasisClock.Offset;
            }
        }
    }
}

namespace RWMLWC.Client.Maps
{
    /// <summary>
    /// Стазис (T-043, этап 1; docs/ARCHITECTURE.md, раздел 5) — режим отсутствия по умолчанию. Карта поселения
    /// замирает (её вещи и подсистемы не тикают), когда:
    ///  - у карты режим «Стазис» (выбран или по умолчанию);
    ///  - её владелец не в сети (владелец колонии, иначе — тот, кто выбирал режим карты);
    ///  - и никто из соавторов этой колонии не в сети — колонией, которой играют, стазис не мешает.
    /// Решение — в тике раз в 60 тиков, из состояния игры (присутствие — команда сервера 102), у всех одинаково.
    /// Время в стазисе копится по карте (<see cref="MapSimState.stasisTicks"/>): локальные часы.
    /// Этап 2 (не сделано): пауза квестов замороженной карты, пересчёт таймеров пешки при выходе в мир,
    /// предупреждение о скачке сезона, запрет стазиса при чужих пешках на карте.
    /// </summary>
    public sealed class StasisComponent : GameComponent
    {
        public const int CheckInterval = 60;

        public StasisComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (!LockstepClient.InSession || !PerMapSim.Enabled)
            {
                return;
            }
            List<Map> maps = Find.Maps;
            bool check = Find.TickManager.TicksGame % CheckInterval == 0;
            for (int i = 0; i < maps.Count; i++)
            {
                MapSimState? state = MapSimState.For(maps[i]);
                if (state == null)
                {
                    continue;
                }
                if (check)
                {
                    bool freeze = ShouldFreeze(maps[i]);
                    if (freeze != state.frozen)
                    {
                        state.frozen = freeze;
                        TestAutomation.Note($"stasis: map {maps[i].uniqueID} {(freeze ? "frozen" : "thawed")} at tick {Find.TickManager.TicksGame}, stasis ticks {state.stasisTicks}");
                        if (!freeze)
                        {
                            Messages.Message($"RWMLWC: «{maps[i].Parent?.LabelCap}» вышла из стазиса (провела в нём {state.stasisTicks / (float)GenDate.TicksPerDay:F1} дн.)", MessageTypeDefOf.NeutralEvent, false);
                        }
                    }
                }
                if (state.frozen)
                {
                    state.stasisTicks++;
                }
            }
        }

        public static bool ShouldFreeze(Map map)
        {
            if (!(map.Parent is Settlement settlement) || settlement.Faction != Faction.OfPlayer || AbsenceModes.Get(map) != AbsenceMode.Stasis)
            {
                return false;
            }
            RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
            int owner = colonies?.OwnerOf(settlement) ?? 0;
            if (owner == 0)
            {
                owner = AbsenceModes.Component?.OwnerOf(map.uniqueID) ?? 0;
            }
            if (owner == 0 || PlayerPresence.IsOnline(owner))
            {
                return false;
            }
            return colonies == null || !colonies.MembersOf(settlement).Any(PlayerPresence.IsOnline);
        }
    }
}
