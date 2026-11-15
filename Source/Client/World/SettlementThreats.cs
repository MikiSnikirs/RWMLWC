using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Maps;
using RWMLWC.Client.Sim;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Очки угроз по поселению (решение владельца 15.11). В игре сила рейда = богатство и колонисты карты-цели
/// (уже по карте) × два общих множителя: «адаптация» рассказчика (растёт со временем, падает при гибели
/// и ранении колонистов — любых, в любой колонии) и «дни с начала игры». В сессии оба множителя — свои
/// у каждой карты-поселения:
///  - адаптация растёт по той же формуле, но у каждого поселения отдельно, и падает только от потерь
///    этого поселения (население — колонисты этой карты);
///  - «дни» — от основания поселения (возраст карты), а не от начала игры: новая колония на сотый день
///    не получает сразу рейды сотого дня;
///  - пока владелец не в сети (режим отсутствия, <see cref="PlayerPresence.BlocksThreats"/>), адаптация
///    поселения не растёт и его «дни» не идут — рассказчик не отыгрывается после возвращения.
/// Состояние в сейве, меняется только в тике — одинаково у всех. Вне сессии — как в игре.
/// </summary>
public sealed class SettlementThreatsComponent : GameComponent
{
    private Dictionary<int, float> adaptDays = new Dictionary<int, float>();
    private Dictionary<int, int> absentTicks = new Dictionary<int, int>();
    private List<int>? tmpK;
    private List<float>? tmpV;
    private List<int>? tmpK2;
    private List<int>? tmpV2;

    public SettlementThreatsComponent(Game game)
    {
    }

    public bool TryGetAdapt(Map map, out float days) => adaptDays.TryGetValue(map.uniqueID, out days);

    /// <summary>Дни поселения для множителя «дни»: возраст карты без времени отсутствия владельца.</summary>
    public int DaysOf(Map map)
    {
        int ticks = Find.TickManager.TicksGame - map.generationTick - (absentTicks.TryGetValue(map.uniqueID, out int a) ? a : 0);
        return Mathf.Max(0, ticks) / GenDate.TicksPerDay;
    }

    /// <summary>Раз в тик (после адаптации игры): завести новые поселения, рост, время отсутствия.</summary>
    public void Tick(StoryWatcher_Adaptation global)
    {
        StorytellerDef def = Find.Storyteller.def;
        bool growTick = Find.TickManager.TicksGame % 30000 == 0;
        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            Map map = maps[i];
            if (!map.IsPlayerHome)
            {
                continue;
            }
            if (!adaptDays.TryGetValue(map.uniqueID, out float adapt))
            {
                // Самое старое поселение продолжает общую адаптацию игры, новые начинают с нуля, как новая игра.
                bool oldest = maps.Where(m => m.IsPlayerHome).OrderBy(m => m.generationTick).ThenBy(m => m.uniqueID).First() == map;
                adapt = oldest && adaptDays.Count == 0 ? global.AdaptDays : 0f;
                adaptDays[map.uniqueID] = adapt;
            }
            if (PlayerPresence.BlocksThreats(map))
            {
                absentTicks[map.uniqueID] = (absentTicks.TryGetValue(map.uniqueID, out int a) ? a : 0) + 1;
                continue;
            }
            if (growTick && (!(adapt >= 0f) || !(DaysOf(map) < def.adaptDaysGameStartGraceDays)))
            {
                float num = 0.5f * def.adaptDaysGrowthRateCurve.Evaluate(adapt);
                if (adapt > 0f)
                {
                    num *= Find.Storyteller.difficulty.adaptationGrowthRateFactorOverZero;
                }
                adaptDays[map.uniqueID] = Mathf.Min(adapt + num, def.adaptDaysMax);
            }
        }
    }

    /// <summary>Потеря колониста (гибель, похищение или ранение в бою) — у его поселения, по его населению.</summary>
    public void Loss(Pawn p, AdaptationEvent ev)
    {
        Map? map = p.MapHeld;
        if (map == null || !adaptDays.TryGetValue(map.uniqueID, out float adapt))
        {
            return;
        }
        StorytellerDef def = Find.Storyteller.def;
        int population = map.mapPawns.FreeColonistsCount;
        float loss = ev == AdaptationEvent.Downed
            ? def.adaptDaysLossFromColonistViolentlyDownedByPopulation.Evaluate(population)
            : def.adaptDaysLossFromColonistLostByPostPopulation.Evaluate(population - 1);
        adaptDays[map.uniqueID] = Mathf.Max(def.adaptDaysMin, adapt - loss);
        TestAutomation.Note($"threats: map {map.uniqueID} adaptation {adapt:F1} → {adaptDays[map.uniqueID]:F1} ({ev}, {p.LabelShort})");
    }

    /// <summary>Для сводок: по каждому поселению — дни и множитель адаптации (то, что идёт в очки угроз).</summary>
    public string Factors()
    {
        StorytellerDef def = Find.Storyteller.def;
        return string.Join(",", Find.Maps.Where(m => adaptDays.ContainsKey(m.uniqueID)).Select(m =>
            $"{m.uniqueID}:d{DaysOf(m)}×{def.pointsFactorFromDaysPassed.Evaluate(DaysOf(m)):F2}×a{def.pointsFactorFromAdaptDays.Evaluate(adaptDays[m.uniqueID]):F2}"));
    }

    public string Summary() =>
        string.Join(",", adaptDays.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value:F2}/{(absentTicks.TryGetValue(kv.Key, out int a) ? a : 0)}"));

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref adaptDays, "rwmlwcAdaptDays", LookMode.Value, LookMode.Value, ref tmpK, ref tmpV);
        Scribe_Collections.Look(ref absentTicks, "rwmlwcAbsentTicks", LookMode.Value, LookMode.Value, ref tmpK2, ref tmpV2);
        adaptDays ??= new Dictionary<int, float>();
        absentTicks ??= new Dictionary<int, int>();
    }
}

public static class SettlementThreats
{
    public static SettlementThreatsComponent? Component => Current.Game?.GetComponent<SettlementThreatsComponent>();

    public static bool Active => LockstepClient.InSession && PerMapSim.Enabled;

    /// <summary>Карта, для которой сейчас считаются очки угроз (внутри DefaultThreatPointsNow).</summary>
    internal static Map? PointsTarget;
}

[HarmonyPatch(typeof(StoryWatcher_Adaptation), nameof(StoryWatcher_Adaptation.AdaptationWatcherTick))]
internal static class Patch_Adaptation_Tick
{
    private static void Postfix(StoryWatcher_Adaptation __instance)
    {
        if (SettlementThreats.Active)
        {
            SettlementThreats.Component?.Tick(__instance);
        }
    }
}

[HarmonyPatch(typeof(StoryWatcher_Adaptation), nameof(StoryWatcher_Adaptation.Notify_PawnEvent))]
internal static class Patch_Adaptation_PawnEvent
{
    private static void Postfix(Pawn p, AdaptationEvent ev, DamageInfo? dinfo)
    {
        if (!SettlementThreats.Active || p == null || !p.RaceProps.Humanlike || !p.IsColonist || p.IsPrisoner)
        {
            return;
        }
        if (ev == AdaptationEvent.Downed && !(dinfo.HasValue && dinfo.Value.Def.ExternalViolenceFor(p)))
        {
            return;
        }
        SettlementThreats.Component?.Loss(p, ev);
    }
}

[HarmonyPatch(typeof(StorytellerUtility), nameof(StorytellerUtility.DefaultThreatPointsNow))]
internal static class Patch_DefaultThreatPointsNow_Target
{
    private static void Prefix(IIncidentTarget target, out Map? __state)
    {
        __state = SettlementThreats.PointsTarget;
        if (!SettlementThreats.Active)
        {
            return;
        }
        Map? map = target as Map;
        if (map != null && map.IsPocketMap)
        {
            map = map.PocketMapParent?.sourceMap;
        }
        SettlementThreats.PointsTarget = map != null && SettlementThreats.Component?.TryGetAdapt(map, out _) == true ? map : null;
    }

    private static void Finalizer(Map? __state)
    {
        SettlementThreats.PointsTarget = __state;
    }
}

[HarmonyPatch(typeof(StoryWatcher_Adaptation), nameof(StoryWatcher_Adaptation.TotalThreatPointsFactor), MethodType.Getter)]
internal static class Patch_Adaptation_Factor
{
    private static bool Prefix(ref float __result)
    {
        Map? map = SettlementThreats.PointsTarget;
        if (map == null || SettlementThreats.Component == null || !SettlementThreats.Component.TryGetAdapt(map, out float adapt))
        {
            return true;
        }
        __result = Find.Storyteller.def.pointsFactorFromAdaptDays.Evaluate(adapt);
        return false;
    }
}

[HarmonyPatch(typeof(GenDate), nameof(GenDate.DaysPassedSinceSettle), MethodType.Getter)]
internal static class Patch_GenDate_DaysSinceSettle
{
    private static bool Prefix(ref int __result)
    {
        Map? map = SettlementThreats.PointsTarget;
        if (map == null || SettlementThreats.Component == null)
        {
            return true;
        }
        __result = SettlementThreats.Component.DaysOf(map);
        return false;
    }
}
