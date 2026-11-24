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
                    AbsentThreatPoints.Update(maps[i], state);
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
            if (RWMLWC.Client.World.CaravanSelfTest.ArmKey("questtest=1"))
            {
                StasisQuestSelfTest.Tick();
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
            return PlayerPresence.OwnerAbsent(map) && !GuestsPresent(map);
        }

        /// <summary>
        /// На карте пешка из отряда игрока, который сейчас в сети (гость, его караван вошёл на карту): стазис
        /// невозможен (docs/ARCHITECTURE.md, раздел 5) — иначе гость замер бы вместе с чужой колонией.
        /// </summary>
        public static bool GuestsPresent(Map map)
        {
            SquadsComponent? squads = Squads.Component;
            GuestsComponent? guests = GuestsComponent.Component;
            // И ещё не вышедшие из капсул (капсула на замороженной карте не тикает и пешку не выпустит).
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.AllPawnsUnspawned.Where(x => x.IsFreeColonist)))
            {
                // Гость: его караван отправил игрок не из этой колонии (Guests.cs).
                int guestOf = guests?.GuestPlayer(p) ?? 0;
                if (guestOf != 0 && PlayerPresence.IsOnline(guestOf))
                {
                    return true;
                }
                Squad? squad = squads?.SquadOf(p.thingIDNumber);
                if (squad != null && squad.players.Any(PlayerPresence.IsOnline))
                {
                    return true;
                }
            }
            return false;
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
                        Pause(__instance);
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Задание пропускает тик: его отметки времени (по общим часам: <c>enableTick</c> задержек и таймеров,
        /// <c>fireRaidTick</c>, <c>endTick</c>… — поля частей на <c>Tick</c> со значением &gt; 0) и срок принятия
        /// сдвигаются на тик вперёд. Иначе пауза ничего не дала бы: задержка считается как
        /// <c>enableTick + delayTicks - TicksGame</c>, и после разморозки все сроки сработали бы разом.
        /// Сдвиг по тику, а не одним разом при разморозке, — верно и при нескольких замороженных картах.
        /// </summary>
        private static void Pause(Quest quest)
        {
            foreach (QuestPart part in quest.PartsListForReading)
            {
                PawnClockShift.ShiftObject(part, 1);
            }
            if (quest.acceptanceExpireTick >= 0)
            {
                quest.acceptanceExpireTick++;
            }
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

namespace RWMLWC.Client.Maps
{
    /// <summary>
    /// Пешка переходит между картами с разными часами (T-043, этап 2; «самое хрупкое место» раздела 5).
    /// Её отметки времени («когда последний раз ела, дралась, говорила…», поля трекеров пешки с именем на
    /// <c>Tick</c>) записаны по часам карты, где она была. При уходе с карты они переводятся в мировое время,
    /// при входе на карту — в её время: иначе после стазиса отметки оказались бы «в будущем» на дни.
    /// Пустые значения (отрицательные, −99999) не трогаются. Только трекеры самой пешки, без глубокого обхода.
    /// </summary>
    internal static class PawnClockShift
    {
        private static readonly Dictionary<System.Type, System.Reflection.FieldInfo[]> tickFields = new Dictionary<System.Type, System.Reflection.FieldInfo[]>();
        private static System.Reflection.FieldInfo[]? trackerFields;

        public static void Shift(Pawn pawn, int delta)
        {
            if (delta == 0)
            {
                return;
            }
            int shifted = 0;
            trackerFields ??= typeof(Pawn).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(f => !f.FieldType.IsValueType && f.FieldType.Namespace != null
                    && (f.FieldType.Namespace.StartsWith("Verse") || f.FieldType.Namespace.StartsWith("RimWorld"))
                    && !typeof(Thing).IsAssignableFrom(f.FieldType) && !typeof(Def).IsAssignableFrom(f.FieldType)
                    && f.FieldType != typeof(Map) && f.FieldType != typeof(Faction))
                .ToArray();
            foreach (System.Reflection.FieldInfo f in trackerFields)
            {
                object? tracker = f.GetValue(pawn);
                if (tracker != null)
                {
                    shifted += ShiftObject(tracker, delta);
                }
            }
            if (pawn.needs != null)
            {
                foreach (Need need in pawn.needs.AllNeeds)
                {
                    shifted += ShiftObject(need, delta);
                }
            }
            TestAutomation.Note($"stasis: pawn {pawn.thingIDNumber} clock shifted by {delta}, {shifted} fields");
        }

        internal static int ShiftObject(object o, int delta)
        {
            System.Type type = o.GetType();
            if (!tickFields.TryGetValue(type, out System.Reflection.FieldInfo[]? fields))
            {
                var list = new List<System.Reflection.FieldInfo>();
                for (System.Type? t = type; t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (System.Reflection.FieldInfo fi in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (fi.FieldType == typeof(int) && !fi.IsInitOnly && fi.Name.EndsWith("Tick", System.StringComparison.Ordinal))
                        {
                            list.Add(fi);
                        }
                    }
                }
                fields = list.ToArray();
                tickFields[type] = fields;
            }
            int n = 0;
            foreach (System.Reflection.FieldInfo fi in fields)
            {
                int v = (int)fi.GetValue(o)!;
                if (v > 0)
                {
                    fi.SetValue(o, v + delta);
                    n++;
                }
            }
            return n;
        }

        public static int OffsetOf(Map? map) => map == null ? 0 : MapSimState.For(map)?.stasisTicks ?? 0;
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    internal static class Patch_Pawn_DeSpawn_Clock
    {
        // Уходит с карты: отметки — из часов карты в мировое время (+ смещение карты).
        private static void Prefix(Pawn __instance)
        {
            if (LockstepClient.InSession && PerMapSim.Enabled && __instance.Spawned)
            {
                PawnClockShift.Shift(__instance, PawnClockShift.OffsetOf(__instance.Map));
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    internal static class Patch_Pawn_SpawnSetup_Clock
    {
        // Входит на карту: отметки — из мирового времени в часы карты (− смещение карты). При загрузке сейва — нет.
        private static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
        {
            if (!respawningAfterLoad && LockstepClient.InSession && PerMapSim.Enabled)
            {
                PawnClockShift.Shift(__instance, -PawnClockShift.OffsetOf(map));
            }
        }
    }
}
