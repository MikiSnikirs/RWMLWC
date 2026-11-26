using System.Collections.Generic;
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
