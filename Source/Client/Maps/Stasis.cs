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
    /// <summary>
    /// Кадр карты (Map.MapUpdate — у всех карт каждый кадр, с отрисовкой текущей) видит её локальное время:
    /// иначе кэши «пересчитать через N тиков», записанные в кадре мировым временем, в тике выглядели бы
    /// пришедшими из будущего, а кадры у игроков разные — расхождение (найдено в tstad2).
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    internal static class Patch_Map_MapUpdate_LocalClock
    {
        private static void Prefix(Map __instance, out int __state)
        {
            __state = StasisClock.Offset;
            if (!MapRandContext.InTick)
            {
                StasisClock.Offset = MapSimState.For(__instance)?.stasisTicks ?? 0;
            }
        }

        private static void Finalizer(int __state)
        {
            if (!MapRandContext.InTick)
            {
                StasisClock.Offset = __state;
            }
        }
    }

    /// <summary>Интерфейс (окна, вкладки, подсказки) вне тика — время текущей карты игрока.</summary>
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    internal static class Patch_UIRoot_Play_LocalClock
    {
        private static void Prefix(out int __state)
        {
            __state = StasisClock.Offset;
            if (!MapRandContext.InTick)
            {
                Map? map = Find.CurrentMap;
                StasisClock.Offset = map != null ? MapSimState.For(map)?.stasisTicks ?? 0 : 0;
            }
        }

        private static void Finalizer(int __state)
        {
            if (!MapRandContext.InTick)
            {
                StasisClock.Offset = __state;
            }
        }
    }

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
                        if (freeze)
                        {
                            state.stasisSinceAbs = Find.TickManager.TicksAbs;
                        }
                        TestAutomation.Note($"stasis: map {maps[i].uniqueID} {(freeze ? "frozen" : "thawed")} at tick {Find.TickManager.TicksGame}, stasis ticks {state.stasisTicks}");
                        if (!freeze)
                        {
                            Messages.Message($"RWMLWC: «{maps[i].Parent?.LabelCap}» вышла из стазиса (провела в нём {state.stasisTicks / (float)GenDate.TicksPerDay:F1} дн.)", MessageTypeDefOf.NeutralEvent, false);
                            WarnSeason(maps[i], state);
                        }
                    }
                }
                if (state.frozen)
                {
                    state.stasisTicks++;
                }
            }
        }

        /// <summary>Есть замороженная карта — из состояния карт (не кэш: после загрузки снимка у всех одинаково).</summary>
        public static bool AnyFrozen
        {
            get
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    if (PerMapSim.IsFrozen(maps[i]))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // Сезон на карте сменился, пока она стояла: игрок увидит другой сезон — предупредить.
        private static void WarnSeason(Map map, MapSimState state)
        {
            UnityEngine.Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
            Season before = GenDate.Season(state.stasisSinceAbs, longLat);
            Season now = GenDate.Season(Find.TickManager.TicksAbs, longLat);
            if (before != now)
            {
                Messages.Message($"RWMLWC: пока «{map.Parent?.LabelCap}» стояла в стазисе, сезон сменился: {before.LabelCap()} → {now.LabelCap()}", MessageTypeDefOf.CautionInput, false);
                TestAutomation.Note($"stasis: map {map.uniqueID} season changed during stasis: {before} -> {now}");
            }
        }

        public static bool ShouldFreeze(Map map)
        {
            if (!(map.Parent is Settlement settlement) || settlement.Faction != Faction.OfPlayer || AbsenceModes.Get(map) != AbsenceMode.Stasis)
            {
                return false;
            }
            return PlayerPresence.OwnerAbsent(map);
        }
    }
}

namespace RWMLWC.Client.Maps
{
    /// <summary>
    /// Пауза заданий замороженной карты (T-043, этап 2): задание, у которого есть часть с полем-ссылкой на эту
    /// карту (<c>MapParent</c>: рейд на колонию, гости, сроки), не тикает, пока карта в стазисе, — его задержки
    /// и сроки не идут. Задания мира (места на карте мира) — по общим часам.
    /// </summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.QuestTick))]
    internal static class Patch_Quest_QuestTick_Stasis
    {
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo[]> mapFields = new Dictionary<System.Type, System.Reflection.FieldInfo[]>();

        private static bool Prefix(Quest __instance)
        {
            if (!StasisComponent.AnyFrozen || __instance.Historical)
            {
                return true;
            }
            foreach (QuestPart part in __instance.PartsListForReading)
            {
                foreach (System.Reflection.FieldInfo f in FieldsOf(part.GetType()))
                {
                    if (f.GetValue(part) is MapParent parent && parent.HasMap && PerMapSim.IsFrozen(parent.Map))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static System.Reflection.FieldInfo[] FieldsOf(System.Type type)
        {
            if (!mapFields.TryGetValue(type, out System.Reflection.FieldInfo[]? fields))
            {
                fields = AccessTools.GetDeclaredFields(type).Concat(type.BaseType != null ? AccessTools.GetDeclaredFields(type.BaseType) : Enumerable.Empty<System.Reflection.FieldInfo>())
                    .Where(f => !f.IsStatic && typeof(MapParent).IsAssignableFrom(f.FieldType)).ToArray();
                mapFields[type] = fields;
            }
            return fields;
        }
    }
}
