using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Состояние, которое игра обновляет раз в кадр (Map.MapUpdate), а читает в тике.
///
/// При нескольких тиках за кадр такие данные в тике «отстают», и на сколько — зависит от того,
/// сколько тиков попало в кадр на этой машине. У двух игроков это число разное, значит и симуляция
/// разная. Здесь то же обновление выполняется перед каждым тиком: к началу тика состояние
/// одинаково, сколько бы кадров ни прошло. Обновление в кадре остаётся и ничего не меняет
/// (оно идемпотентно: считает по текущему состоянию симуляции).
///
/// Что обновляется (docs/research/sim.md, 6л):
///  - яркость неба карты (SkyManager.curSkyGlowInt) — её читают освещённость клеток, штраф
///    за бой в темноте, солнечные генераторы;
///  - электросети: постройки подключаются к сетям только здесь (PowerNetManager.delayedActions);
///  - регионы и комнаты после изменения стен;
///  - сетка освещения после изменения источников света;
///  - счётчик сигналов за кадр (SignalManager) и покадровые кэши, которые читает симуляция.
/// </summary>
internal static class FrameIndependentState
{
    private static readonly AccessTools.FieldRef<SkyManager, float>? skyGlow = FieldRefOrNull<SkyManager, float>("curSkyGlowInt");
    private static readonly MethodInfo? currentSkyTarget = AccessTools.Method(typeof(SkyManager), "CurrentSkyTarget");
    private static readonly AccessTools.FieldRef<SignalManager, int>? signalsThisFrame = FieldRefOrNull<SignalManager, int>("signalsThisFrame");
    private static readonly FieldInfo? accessibleThingsFrame = AccessTools.Field(typeof(PlayerItemAccessibilityUtility), "cachedAccessibleThingsForFrame");
    private static bool warned;

    private static AccessTools.FieldRef<T, F>? FieldRefOrNull<T, F>(string name)
    {
        return AccessTools.Field(typeof(T), name) != null ? AccessTools.FieldRefAccess<T, F>(name) : null;
    }

    /// <summary>Перед тиком, уже в контексте симуляции (после MapRandContext.BeginTick).</summary>
    public static void BeforeTick()
    {
        if (!warned && (skyGlow == null || currentSkyTarget == null || signalsThisFrame == null))
        {
            warned = true;
            Log.Warning("[RWMLWC] FrameIndependentState: в этой версии игры не найдены SkyManager.curSkyGlowInt, "
                + "SkyManager.CurrentSkyTarget или SignalManager.signalsThisFrame — часть покадрового состояния не выравнивается.");
        }

        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            Map map = maps[i];
            MapSimState? sim = MapSimState.For(map);
            if (sim != null && sim.frozen)
            {
                continue; // замороженная карта не тикает, её состояние не трогаем
            }
            MapRandContext.Enter(map);
            try
            {
                if (skyGlow != null && currentSkyTarget != null)
                {
                    skyGlow(map.skyManager) = ((SkyTarget)currentSkyTarget.Invoke(map.skyManager, null)!).glow;
                }
                map.powerNetManager.UpdatePowerNetsAndConnections_First();
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                map.glowGrid.GlowGridUpdate_First();
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[RWMLWC] FrameIndependentState: обновление карты " + map.uniqueID + " упало: " + ex, 0x52574D31 + map.uniqueID);
            }
        }
        MapRandContext.ExitToGlobal();

        if (signalsThisFrame != null && Current.Game?.signalManager != null)
        {
            signalsThisFrame(Current.Game.signalManager) = 0;
        }
        // Кэши «на один кадр»: при нескольких тиках за кадр второй тик читал бы данные первого.
        // Поле статическое. Кэш способности «Скачок обломков» — поле экземпляра, его сбрасывает
        // Patch_CompAbilityEffect_Chunkskip_FindClosestChunks.
        accessibleThingsFrame?.SetValue(null, -1);
    }
}

/// <summary>
/// CompAbilityEffect_Chunkskip.FindClosestChunks хранит найденные обломки до конца кадра
/// (поле экземпляра lastChunkUpdateFrame). В сессии кэш не используется: список считается заново
/// при каждом вызове, результат зависит только от состояния карты.
/// </summary>
[HarmonyPatch(typeof(CompAbilityEffect_Chunkskip), "FindClosestChunks")]
internal static class Patch_CompAbilityEffect_Chunkskip_FindClosestChunks
{
    private static void Prefix(ref int ___lastChunkUpdateFrame)
    {
        if (PerMapSim.Enabled)
        {
            ___lastChunkUpdateFrame = -1;
        }
    }
}

/// <summary>
/// CompBiosculpterPod.CanReachRequiredIngredients(useCache: true) держит ответ 2 реальные секунды
/// (Time.realtimeSinceStartup). Его зовёт ИИ пешки (JobGiver_GetAgeReversal → PawnCanUseNow), то есть
/// симуляция: при разной скорости игры у игроков кэш давал бы разные ответы. В сессии кэш не используется.
/// </summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
internal static class Patch_Pawn_JobTracker_StartJob_FrameCount
{
    /// <summary>
    /// На паузе игра считает задание в счётчик «заданий за тик» (предохранитель «больше 10 — аварийное задание»)
    /// только если прошлое выдано в том же кадре (lastJobGivenAtFrame == RealTime.frameCount). Команды игроков
    /// в сессии исполняются на паузе у каждого в свои кадры — в сессии считаем всегда, одинаково у всех.
    /// </summary>
    private static void Prefix(ref int ___lastJobGivenAtFrame)
    {
        if (PerMapSim.Enabled && Find.TickManager.Paused)
        {
            ___lastJobGivenAtFrame = RealTime.frameCount;
        }
    }
}

[HarmonyPatch(typeof(CompBiosculpterPod), "CanReachRequiredIngredients")]
internal static class Patch_CompBiosculpterPod_CanReachRequiredIngredients
{
    private static void Prefix(ref bool useCache)
    {
        if (PerMapSim.Enabled)
        {
            useCache = false;
        }
    }
}

/// <summary>
/// Яркость карты (Anomaly, «неестественная тьма») меняется в тике, но шаг берётся из длительности
/// кадра (Time.deltaTime). В сессии шаг постоянный: как при 60 кадрах в секунду.
/// </summary>
[HarmonyPatch]
internal static class Patch_MapBrightnessTracker_Tick
{
    private static MethodBase? target;

    private static bool Prepare()
    {
        Type? type = AccessTools.Inner(typeof(GameConditionManager), "MapBrightnessTracker");
        target = type != null ? AccessTools.Method(type, "Tick") : null;
        if (target == null || AccessTools.Field(type, "lerp") == null || AccessTools.Field(type, "lerpSeconds") == null)
        {
            Log.Warning("[RWMLWC] не найден GameConditionManager.MapBrightnessTracker.Tick — яркость карты зависит от кадров.");
            return false;
        }
        return true;
    }

    private static MethodBase TargetMethod() => target!;

    private static bool Prefix(ref float ___lerp, float ___lerpSeconds)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        if (ModsConfig.AnomalyActive && ___lerp < 1f)
        {
            ___lerp += ___lerpSeconds / 60f * (1f / 60f);
        }
        return false;
    }
}
