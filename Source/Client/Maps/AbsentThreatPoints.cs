using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// T-072, заморозка очков угроз на время отсутствия владельца (docs/ARCHITECTURE.md, раздел 4).
/// Очки угроз карты = f(богатство карты, её пешки) × адаптация × «дни». Адаптацию и «дни» поселения на время
/// отсутствия уже замораживает <c>SettlementThreatsComponent</c> (B). В стазисе карта не тикает, и богатство
/// не меняется; в автопилоте и у управляющего оно растёт. Поэтому в момент ухода владельца (проверка раз в
/// <see cref="StasisComponent.CheckInterval"/> тиков, из состояния игры — у всех на одном тике) богатство
/// карты для рассказчика запоминается в <see cref="MapSimState.absentWealth"/> (в сейве), и пока владельца нет,
/// <c>DefaultThreatPointsNow</c> для этой карты видит его, а не текущее. При возвращении — снова текущее.
/// Вклад пешек считается по-прежнему от живых пешек (их коэффициент — тоже от замороженного богатства):
/// число пешек в отсутствие меняется редко, а копировать формулу игры ради него хрупко.
/// Вне сессии — как в игре.
/// </summary>
internal static class AbsentThreatPoints
{
    /// <summary>Карта, для которой сейчас считаются очки угроз (внутри DefaultThreatPointsNow), или null.</summary>
    internal static Map? Target;

    public static void Update(Map map, MapSimState state)
    {
        bool absent = map.Parent is Settlement s && s.Faction == Faction.OfPlayer && PlayerPresence.OwnerAbsent(map);
        if (absent && state.absentWealth < 0f)
        {
            state.absentWealth = map.PlayerWealthForStoryteller;
            TestAutomation.Note($"threat points: map {map.uniqueID} owner absent at tick {Find.TickManager.TicksGame}, wealth frozen at {state.absentWealth:F0}");
        }
        else if (!absent && state.absentWealth >= 0f)
        {
            TestAutomation.Note($"threat points: map {map.uniqueID} owner back at tick {Find.TickManager.TicksGame}, frozen wealth {state.absentWealth:F0}, now {map.PlayerWealthForStoryteller:F0}");
            state.absentWealth = -1f;
        }
    }

    /// <summary>Замороженное богатство карты для рассказчика или -1.</summary>
    public static float FrozenWealth(Map map) => LockstepClient.InSession ? MapSimState.For(map)?.absentWealth ?? -1f : -1f;
}

[HarmonyPatch(typeof(StorytellerUtility), nameof(StorytellerUtility.DefaultThreatPointsNow))]
internal static class Patch_DefaultThreatPointsNow_AbsentWealth
{
    private static void Prefix(IIncidentTarget target, out Map? __state)
    {
        __state = AbsentThreatPoints.Target;
        Map? map = target as Map;
        if (map != null && map.IsPocketMap)
        {
            map = map.PocketMapParent?.sourceMap;
        }
        AbsentThreatPoints.Target = map;
    }

    private static void Finalizer(Map? __state)
    {
        AbsentThreatPoints.Target = __state;
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.PlayerWealthForStoryteller), MethodType.Getter)]
internal static class Patch_Map_PlayerWealth_Absent
{
    private static void Postfix(Map __instance, ref float __result)
    {
        if (ReferenceEquals(AbsentThreatPoints.Target, __instance))
        {
            float frozen = AbsentThreatPoints.FrozenWealth(__instance);
            if (frozen >= 0f)
            {
                __result = frozen;
            }
        }
    }
}
