using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Рассказчик по картам (T-056, план 11.4б; docs/ARCHITECTURE.md, 11.3.4).
///
/// В игре <c>Storyteller.StorytellerTick</c> раз в 1000 тиков генерирует события для всех целей
/// (<c>AllIncidentTargets</c> = карты + караваны + мир) и исполняет их. Генерация для карты потребляет
/// общий Rand (<c>StorytellerComp.MakeIntervalIncidents</c>), а зерно события зависит от карты
/// (<c>target.ConstantRandSeed</c>). Когда у игроков разные списки карт (sleepremove: чужой колонии —
/// заглушка без карты), общий Rand расходится (sr2/sr3: тик 16000, +1 вызов у клиента с другой картой).
///
/// Здесь в режиме sleepremove события для карты генерируются и исполняются в контексте этой карты
/// (<see cref="MapRandContext.Enter"/>): свой Rand карты, сказания уходят событием карты
/// (<see cref="MapEvents"/>), номера — счётчиком карты. Мир и караваны остаются в общем контексте —
/// они одинаковы у всех. Порядок обхода — по целям, затем по компонентам рассказчика (в игре —
/// наоборот); на общий Rand это не влияет: карты его больше не трогают.
///
/// Записи в мир из исполнения событий (мировые пешки, исследования, отношения) сверх сказаний —
/// шаг 4 плана 11.4б; до него они расходятся и фиксируются прогоном.
/// </summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.StorytellerTick))]
internal static class Patch_Storyteller_StorytellerTick_PerMap
{
    private static readonly AccessTools.FieldRef<Storyteller, List<StorytellerComp>> CompsRef =
        AccessTools.FieldRefAccess<Storyteller, List<StorytellerComp>>("storytellerComps");

    private static readonly List<IIncidentTarget> oneTarget = new List<IIncidentTarget>();

    private static bool Prefix(Storyteller __instance)
    {
        if (!PerMapSim.SleepRemoveTest || !PerMapSim.Enabled || !LockstepClient.InSession)
        {
            return true;
        }
        __instance.incidentQueue.IncidentQueueTick();
        if (Find.TickManager.TicksGame % 1000 != 0 || !DebugSettings.enableStoryteller)
        {
            return false;
        }
        List<StorytellerComp> comps = CompsRef(__instance);
        foreach (IIncidentTarget target in __instance.AllIncidentTargets)
        {
            bool isMap = target is Map;
            if (isMap)
            {
                MapRandContext.Enter((Map)target);
            }
            try
            {
                oneTarget.Clear();
                oneTarget.Add(target);
                for (int i = 0; i < comps.Count; i++)
                {
                    foreach (FiringIncident fi in __instance.MakeIncidentsForInterval(comps[i], oneTarget))
                    {
                        __instance.TryFire(fi);
                    }
                }
            }
            finally
            {
                oneTarget.Clear();
                if (isMap)
                {
                    MapRandContext.Enter(null);
                }
            }
        }
        // Задания-источники событий — как в игре, в общем контексте (задания общие у всех).
        List<Quest> quests = Find.QuestManager.QuestsListForReading;
        for (int i = 0; i < quests.Count; i++)
        {
            if (quests[i].State != QuestState.Ongoing)
            {
                continue;
            }
            List<QuestPart> parts = quests[i].PartsListForReading;
            for (int j = 0; j < parts.Count; j++)
            {
                if (parts[j] is IIncidentMakerQuestPart maker && ((QuestPartActivable)parts[j]).State == QuestPartState.Enabled)
                {
                    foreach (FiringIncident fi in maker.MakeIntervalIncidents())
                    {
                        fi.sourceQuestPart = parts[j];
                        fi.parms.quest = quests[i];
                        __instance.TryFire(fi);
                    }
                }
            }
        }
        return false;
    }
}

/// <summary>
/// Событие для карты, которой у этого игрока нет (заглушка), не исполняется: ни из очереди рассказчика,
/// ни из заданий. Иначе <c>CanFireNow</c>/<c>TryExecute</c> получили бы null вместо карты.
/// Очередь при этом симметрична (общий снимок), повторные попытки — <c>Rand.RangeSeeded</c>, общий Rand не трогают.
/// </summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
internal static class Patch_Storyteller_TryFire_MissingMap
{
    private static bool Prefix(FiringIncident fi, ref bool __result)
    {
        if (PerMapSim.SleepRemoveTest && fi.parms.target is Map map && !Find.Maps.Contains(map))
        {
            __result = false;
            return false;
        }
        return true;
    }
}

/// <summary>Диагностика T-056 (режим sleepremove): каждое созданное задание — тик, корень, контекст Rand, источник.</summary>
[HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Add))]
internal static class Patch_QuestManager_Add_Log
{
    private static void Postfix(Quest quest)
    {
        if (PerMapSim.SleepRemoveTest && LockstepClient.InSession && quest != null)
        {
            Map? ctx = MapRandContext.Current;
            TestAutomation.Note($"quest added: #{quest.id} root={quest.root?.defName} at tick {Find.TickManager.TicksGame}, context {(MapRandContext.InTick ? (ctx != null ? "map" + ctx.uniqueID : "global") : "out of tick")}\n{new System.Diagnostics.StackTrace(1, false).ToString().Split('\n').Take(8).Aggregate((a, b) => a + "\n" + b)}");
        }
    }
}

/// <summary>
/// T-056, режим заглушки (sleepremove), временное правило: рассказчик не выдаёт задания (IncidentWorker_GiveQuest).
/// Задание порождается состоянием карт (своей — для события карты; колоний — для мирового), а карты у игроков в
/// этом режиме разные: задание появлялось только у владельца карты (sr22: MonolithMigration на тике 25000), и мир
/// расходился. Правильное решение — задания порождает один назначенный игрок и рассылает готовыми (11.3.4); до него
/// в режиме заглушки заданий от рассказчика нет у всех одинаково. Обычная игра (все считают всё) не затронута.
/// </summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
internal static class Patch_Storyteller_TryFire_NoQuestsInStubMode
{
    private static bool Prefix(FiringIncident fi, ref bool __result)
    {
        if (PerMapSim.SleepRemoveTest && LockstepClient.InSession && fi.def.Worker is IncidentWorker_GiveQuest)
        {
            TestAutomation.Note($"quest incident {fi.def.defName} for {(fi.parms.target is Map m ? "map" + m.uniqueID : fi.parms.target?.ToString())} skipped in stub mode at tick {Find.TickManager.TicksGame}");
            __result = false;
            return false;
        }
        return true;
    }
}

/// <summary>
/// T-056, режим заглушки: общая (ванильная) адаптация рассказчика не меняется из тика карты. Ранение и смерть
/// колониста считает только владелец карты, и общий счётчик расходился (sr23: 33750, 0 против −6 → очередь событий
/// → общий Rand на 39500). Адаптацию по поселению ведёт SettlementThreatsComponent (его постфикс на этом методе
/// исполняется и при пропуске оригинала); общий счётчик растёт тиком мира — у всех одинаково.
/// </summary>
[HarmonyPatch(typeof(StoryWatcher_Adaptation), nameof(StoryWatcher_Adaptation.Notify_PawnEvent))]
internal static class Patch_Adaptation_PawnEvent_StubMode
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix()
    {
        return !(PerMapSim.SleepRemoveTest && LockstepClient.InSession && MapRandContext.InTick && MapRandContext.Current != null);
    }
}

/// <summary>
/// T-056, режим заглушки: событие для карты исполняется в контексте Rand этой карты, откуда бы его ни вызвали —
/// из очереди рассказчика (IncidentQueueTick) или из задания. Такие события есть только у владельца карты
/// (в очередь их кладёт тик карты), и в общем контексте они сдвигали общий Rand только у него
/// (sr24: incidentQueue 127500 → общий Rand 127750).
/// </summary>
[HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
internal static class Patch_Storyteller_TryFire_MapContext
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(FiringIncident fi, out (bool entered, Map? prev) __state)
    {
        __state = (false, null);
        if (PerMapSim.SleepRemoveTest && PerMapSim.Enabled && LockstepClient.InSession && MapRandContext.InTick
            && fi.parms.target is Map map && Find.Maps.Contains(map) && MapRandContext.Current != map)
        {
            __state = (true, MapRandContext.Current);
            MapRandContext.Enter(map);
        }
    }

    private static void Finalizer((bool entered, Map? prev) __state)
    {
        if (__state.entered)
        {
            MapRandContext.Enter(__state.prev);
        }
    }
}

/// <summary>Диагностика T-056 (режим заглушки): что кладут в общую очередь событий рассказчика.</summary>
[HarmonyPatch(typeof(IncidentQueue), nameof(IncidentQueue.Add), new[] { typeof(QueuedIncident) })]
internal static class Patch_IncidentQueue_Add_Log
{
    private static void Postfix(QueuedIncident qi, bool __result)
    {
        if (PerMapSim.SleepRemoveTest && LockstepClient.InSession && qi?.FiringIncident != null)
        {
            Map? ctx = MapRandContext.Current;
            TestAutomation.Note($"incident queued: {qi.FiringIncident.def?.defName} for {(qi.FiringIncident.parms.target is Map m ? "map" + m.uniqueID : "world")} fire at {qi.FireTick}, added={__result} at tick {Find.TickManager.TicksGame}, context {(MapRandContext.InTick ? (ctx != null ? "map" + ctx.uniqueID : "global") : "out of tick")}");
        }
    }
}
