using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Verse.Sound;

namespace RWMLWC.Client.Sim;

// T-088: правки детерминизма ванили, найденные сверкой с RimWorld Multiplayer (rwmt, MIT) и проверенные
// по коду 1.7.5021. Что сверялось и почему остальное не нужно — docs/research/rwmt-multiplayer.md,
// раздел «Сверка исправлений ванили (T-088)».

/// <summary>Общее условие: сессия, а код выполняется не симуляцией (кадр, интерфейс, изолированный визуальный код).</summary>
internal static class SimUi
{
    public static bool Interface => PerMapSim.Enabled && !(MapRandContext.InTick && VisualRandIsolation.Depth == 0);
}

/// <summary>
/// Клетки зоны и плана перемешиваются через Rand при первом обращении после изменения (Zone.Cells,
/// Plan.Cells). Первым часто обращается интерфейс: выделенный огород считает в подсказке растения
/// (Zone_Growing.GetInspectString). Вне тика Rand — состояние показа, у каждого игрока своё, и порядок
/// клеток выходит разным, а по нему носят на склад и сеют. Вне симуляции клетки отдаются без
/// перемешивания и флаг не ставится — перемешает первая симуляция, у всех одинаково.
/// </summary>
[HarmonyPatch(typeof(Zone), nameof(Zone.Cells), MethodType.Getter)]
internal static class Patch_Zone_Cells_NoShuffleInInterface
{
    private static bool Prefix(Zone __instance, ref List<IntVec3> __result)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        __result = __instance.cells;
        return false;
    }
}

/// <summary>То же для планов (Plan.Cells).</summary>
[HarmonyPatch(typeof(Plan), nameof(Plan.Cells), MethodType.Getter)]
internal static class Patch_Plan_Cells_NoShuffleInInterface
{
    private static readonly AccessTools.FieldRef<Plan, List<IntVec3>> cells = AccessTools.FieldRefAccess<Plan, List<IntVec3>>("cells");

    private static bool Prefix(Plan __instance, ref List<IntVec3> __result)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        __result = cells(__instance);
        return false;
    }
}

/// <summary>
/// Пересчёт богатства карты (WealthWatcher.ForceRecount): его зовёт вкладка «История» и любой геттер
/// богатства, если с прошлого счёта прошло 5000 тиков. Богатство берёт рассказчик для очков угроз,
/// поэтому момент пересчёта должен быть общим. Вне симуляции пересчёт пропускается (интерфейс видит
/// последнее посчитанное значение). Пересчёт при создании карты (allowDuringInit) не трогаем.
/// </summary>
[HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
internal static class Patch_WealthWatcher_ForceRecount_SimOnly
{
    private static bool Prefix(bool allowDuringInit) => allowDuringInit || !SimUi.Interface;
}

/// <summary>
/// Уровень опасности карты (DangerWatcher.DangerRating) пересчитывается, если с прошлого раза прошло
/// больше 101 тика, кто бы ни спросил. Каждый кадр его спрашивает музыка, плюс подсказки; симуляция
/// читает тот же кэш (автоматическое снятие призыва и др.). Вне симуляции — только последнее значение.
/// </summary>
[HarmonyPatch(typeof(DangerWatcher), nameof(DangerWatcher.DangerRating), MethodType.Getter)]
internal static class Patch_DangerWatcher_DangerRating_SimOnly
{
    private static readonly AccessTools.FieldRef<DangerWatcher, StoryDanger> rating = AccessTools.FieldRefAccess<DangerWatcher, StoryDanger>("dangerRatingInt");

    private static bool Prefix(DangerWatcher __instance, ref StoryDanger __result)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        __result = rating(__instance);
        return false;
    }
}

/// <summary>
/// «Караван перегружен» (Caravan.ImmobilizedByMass) кэшируется на 60 тиков от первого спросившего;
/// спрашивают и кнопки и панель каравана. Вне симуляции значение считается без записи в кэш.
/// </summary>
[HarmonyPatch(typeof(Caravan), nameof(Caravan.ImmobilizedByMass), MethodType.Getter)]
internal static class Patch_Caravan_ImmobilizedByMass_SimOnly
{
    private static bool Prefix(Caravan __instance, ref bool __result)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        __result = __instance.MassUsage > __instance.MassCapacity;
        return false;
    }
}

/// <summary>
/// Список способностей пешки (Pawn_AbilityTracker.AllAbilitiesForReading) собирается заново, если помечен
/// «грязным», и запоминается тем, кто спросил первым, — часто кнопками выделенной пешки. Вне симуляции
/// список собирается во временный, а кэш и пометка остаются как были.
/// </summary>
[HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.AllAbilitiesForReading), MethodType.Getter)]
internal static class Patch_Pawn_AbilityTracker_AllAbilities_SimOnly
{
    private static readonly AccessTools.FieldRef<Pawn_AbilityTracker, bool> dirty = AccessTools.FieldRefAccess<Pawn_AbilityTracker, bool>("allAbilitiesCachedDirty");
    private static readonly AccessTools.FieldRef<Pawn_AbilityTracker, List<Ability>> cached = AccessTools.FieldRefAccess<Pawn_AbilityTracker, List<Ability>>("allAbilitiesCached");

    private static void Prefix(Pawn_AbilityTracker __instance, out List<Ability>? __state)
    {
        __state = null;
        if (SimUi.Interface && dirty(__instance))
        {
            __state = cached(__instance);
            cached(__instance) = new List<Ability>();
        }
    }

    private static void Finalizer(Pawn_AbilityTracker __instance, List<Ability>? __state)
    {
        if (__state != null)
        {
            cached(__instance) = __state;
            dirty(__instance) = true;
        }
    }
}

/// <summary>
/// Приоритетная работа (PriorityWork): геттер IsPrioritized по истечении срока сам вызывает Clear,
/// а его спрашивают кнопки выделенной пешки. Вне симуляции сброс пропускается — сбросит симуляция.
/// </summary>
[HarmonyPatch(typeof(PriorityWork), nameof(PriorityWork.Clear))]
internal static class Patch_PriorityWork_Clear_SimOnly
{
    private static bool Prefix() => !SimUi.Interface;
}

/// <summary>
/// Ситуативные мысли пешки (SituationalThoughtHandler). Окно потребностей и вкладка отношений спрашивают
/// мысли в кадре, и тогда игра пересчитывает их состояние, создаёт новые и сдвигает сроки кэша
/// (lastRecalculationTick, lastQueryTick) на тик кадра. Настроение и мнения потом читает симуляция.
/// Вне симуляции отдаются только уже посчитанные мысли, без пересчёта и без записи в кэш.
/// </summary>
[HarmonyPatch]
internal static class Patch_SituationalThoughts_SimOnly
{
    private static readonly AccessTools.FieldRef<SituationalThoughtHandler, List<Thought_Situational>> cachedThoughts =
        AccessTools.FieldRefAccess<SituationalThoughtHandler, List<Thought_Situational>>("cachedThoughts");
    private static readonly FieldInfo? socialCache = AccessTools.Field(typeof(SituationalThoughtHandler), "cachedSocialThoughts");
    private static readonly FieldInfo? activeThoughts = AccessTools.Inner(typeof(SituationalThoughtHandler), "CachedSocialThoughts") is Type t
        ? AccessTools.Field(t, "activeThoughts") : null;

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendMoodThoughts), new[] { typeof(List<Thought>) })]
    [HarmonyPrefix]
    private static bool AppendMood(SituationalThoughtHandler __instance, List<Thought> outThoughts)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        foreach (Thought_Situational thought in cachedThoughts(__instance))
        {
            if (thought.Active)
            {
                outThoughts.Add(thought);
            }
        }
        return false;
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendMoodThoughts), new[] { typeof(ThoughtDef), typeof(List<Thought>) })]
    [HarmonyPrefix]
    private static bool AppendMoodOfDef(SituationalThoughtHandler __instance, ThoughtDef def, List<Thought> thoughts)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        foreach (Thought_Situational thought in cachedThoughts(__instance))
        {
            if (thought.def == def && thought.Active)
            {
                thoughts.Add(thought);
            }
        }
        return false;
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendSocialThoughts))]
    [HarmonyPrefix]
    private static bool AppendSocial(SituationalThoughtHandler __instance, Pawn otherPawn, List<ISocialThought> outThoughts)
    {
        if (!SimUi.Interface)
        {
            return true;
        }
        // Тип записи кэша закрытый — через отражение (только интерфейс, вкладка отношений).
        if (socialCache?.GetValue(__instance) is System.Collections.IDictionary dict && dict.Contains(otherPawn)
            && activeThoughts?.GetValue(dict[otherPawn]) is List<Thought_SituationalSocial> active)
        {
            for (int i = 0; i < active.Count; i++)
            {
                outThoughts.Add(active[i]);
            }
        }
        return false;
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), "UpdateAllMoodThoughts")]
    [HarmonyPrefix]
    private static bool UpdateAll() => !SimUi.Interface;

    [HarmonyPatch(typeof(SituationalThoughtHandler), "CheckRecalculateSocialThoughts")]
    [HarmonyPrefix]
    private static bool CheckSocial() => !SimUi.Interface;
}

/// <summary>
/// Уровни способностей пешки (PawnCapacitiesHandler.GetLevel: сознание, движение, зрение…) кэшируются
/// до следующего изменения здоровья тем, кто спросил первым. Боль и часть показателей меняются плавно,
/// без сброса кэша, поэтому значение зависит от тика, в который его посчитали. Вне симуляции значение
/// помечается особым состоянием «посчитано интерфейсом» (3): интерфейс им пользуется, а симуляция
/// считает такое значение отсутствующим и пересчитывает (как в RimWorld Multiplayer).
/// </summary>
[HarmonyPatch(typeof(PawnCapacitiesHandler), nameof(PawnCapacitiesHandler.GetLevel))]
internal static class Patch_PawnCapacitiesHandler_GetLevel_SimOnly
{
    private const int Cached = 2;
    private const int CachedInInterface = 3;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        Type? element = AccessTools.Inner(typeof(PawnCapacitiesHandler), "CacheElement");
        FieldInfo? status = element == null ? null : AccessTools.Field(element, "status");
        List<CodeInstruction> code = new List<CodeInstruction>(instructions);
        if (status == null)
        {
            Log.Warning("[RWMLWC] PawnCapacitiesHandler.CacheElement.status не найден — кэш способностей не изолирован от интерфейса.");
            return code;
        }
        int checks = 0;
        int stores = 0;
        for (int i = 0; i < code.Count - 1; i++)
        {
            // «status == Uncached»: ldfld status; brtrue — перед переходом подменяем значение.
            if (code[i].LoadsField(status) && (code[i + 1].opcode == OpCodes.Brtrue || code[i + 1].opcode == OpCodes.Brtrue_S))
            {
                code.Insert(i + 1, CodeInstruction.Call(typeof(Patch_PawnCapacitiesHandler_GetLevel_SimOnly), nameof(StatusForCheck)));
                checks++;
                i++;
            }
            // «status = Cached»: ldc.i4.2; stfld status.
            else if (code[i].opcode == OpCodes.Ldc_I4_2 && code[i + 1].StoresField(status))
            {
                code.Insert(i + 1, CodeInstruction.Call(typeof(Patch_PawnCapacitiesHandler_GetLevel_SimOnly), nameof(StatusToStore)));
                stores++;
                i++;
            }
        }
        if (checks != 1 || stores != 1)
        {
            Log.Warning($"[RWMLWC] PawnCapacitiesHandler.GetLevel изменился (проверок {checks}, записей {stores}) — кэш способностей не изолирован.");
            return instructions;
        }
        return code;
    }

    /// <summary>0 — «не посчитано»: так симуляция видит и значение, посчитанное интерфейсом.</summary>
    private static int StatusForCheck(int status) => status == CachedInInterface && !SimUi.Interface ? 0 : status;

    private static int StatusToStore(int status) => SimUi.Interface ? CachedInInterface : status;
}

/// <summary>
/// Растворение отходов (Biotech) копит события загрязнения мира и потери отношений с фракциями,
/// а применяет их World.WorldUpdate — раз в кадр, то есть на тике, который зависит от частоты кадров.
/// В сессии они применяются перед каждым тиком (из FrameIndependentState), а в кадре — нет.
/// </summary>
[HarmonyPatch]
internal static class Patch_CompDissolutionEffect_WorldUpdate_TickOnly
{
    private static bool flushing;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.WorldUpdate));
        yield return AccessTools.Method(typeof(CompDissolutionEffect_Pollution), nameof(CompDissolutionEffect_Pollution.WorldUpdate));
    }

    private static bool Prefix() => !PerMapSim.Enabled || flushing;

    /// <summary>Перед тиком: применить накопленное за прошлый тик, как это сделал бы кадр.</summary>
    public static void Flush()
    {
        if (!ModsConfig.BiotechActive)
        {
            return;
        }
        flushing = true;
        try
        {
            CompDissolutionEffect_Goodwill.WorldUpdate();
            CompDissolutionEffect_Pollution.WorldUpdate();
        }
        catch (Exception ex)
        {
            Log.ErrorOnce("[RWMLWC] растворение отходов перед тиком: " + ex, 0x52574D58);
        }
        finally
        {
            flushing = false;
        }
    }
}

/// <summary>
/// Обрушение пещеры под вратами (Anomaly, UndercaveMapComponent.MapComponentTick): вся логика, включая
/// падение камней (меняет карту) и вызовы Rand, идёт только у того, кто смотрит на эту карту
/// (Find.CurrentMap == map). В сессии тик пещеры одинаков у всех: камни падают у всех, тряска камеры,
/// звук и фоновые эффекты — только у смотрящего, а фоновые эффекты берут числа из отдельного состояния Rand.
/// </summary>
[HarmonyPatch(typeof(UndercaveMapComponent), nameof(UndercaveMapComponent.MapComponentTick))]
internal static class Patch_UndercaveMapComponent_Tick_SameForAll
{
    private static readonly AccessTools.FieldRef<UndercaveMapComponent, Sustainer> sustainer = AccessTools.FieldRefAccess<UndercaveMapComponent, Sustainer>("collapsingSustainer");
    private static readonly SimpleCurve? mtbCurve = AccessTools.Field(typeof(UndercaveMapComponent), "HoursToShakeMTBTicksCurve")?.GetValue(null) as SimpleCurve;
    private static readonly object? stageOne = AccessTools.Field(typeof(UndercaveMapComponent), "StageOneNumCollapseEffects")?.GetValue(null);
    private static readonly object? stageTwo = AccessTools.Field(typeof(UndercaveMapComponent), "StageTwoNumCollapseEffects")?.GetValue(null);
    private static readonly MethodInfo? collapseFx = AccessTools.Method(typeof(UndercaveMapComponent), "TriggerCollapseFX");
    private static readonly MethodInfo? ambientFx = AccessTools.Method(typeof(UndercaveMapComponent), "TriggerAmbientFX");
    private static bool warned;

    private static bool Prefix(UndercaveMapComponent __instance)
    {
        if (!PerMapSim.Enabled)
        {
            return true;
        }
        if (mtbCurve == null || stageOne is not IntRange one || stageTwo is not IntRange two || collapseFx == null || ambientFx == null)
        {
            if (!warned)
            {
                warned = true;
                Log.Warning("[RWMLWC] UndercaveMapComponent изменился — обрушение пещеры идёт как в одиночной игре (только у смотрящего).");
            }
            return true;
        }
        Map map = __instance.map;
        bool looking = Find.CurrentMap == map;
        if (!looking)
        {
            sustainer(__instance)?.End();
        }
        PitGate gate = __instance.pitGate;
        if (gate == null)
        {
            return false;
        }
        if (gate.IsCollapsing)
        {
            float mtb = mtbCurve.Evaluate(gate.TicksUntilCollapse / 2500f);
            bool stageOneNow = gate.CollapseStage == 1;
            if (looking && (sustainer(__instance) == null || sustainer(__instance).Ended))
            {
                SoundDef sound = stageOneNow ? SoundDefOf.UndercaveCollapsingStage1 : SoundDefOf.UndercaveCollapsingStage2;
                sustainer(__instance) = sound.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.PerTick));
            }
            if (Rand.MTBEventOccurs(mtb, 1f, 1f))
            {
                int dust = (stageOneNow ? one : two).RandomInRange;
                float shake = looking ? (stageOneNow ? 0.1f : 0.2f) : 0f;
                collapseFx.Invoke(__instance, new object[] { shake, dust });
            }
            if (looking)
            {
                sustainer(__instance)?.Maintain();
            }
        }
        if (looking)
        {
            Rand.PushState();
            try
            {
                ambientFx.Invoke(__instance, null);
            }
            finally
            {
                Rand.PopState();
            }
        }
        __instance.ReadFxQueue();
        return false;
    }
}

/// <summary>
/// Прибытие из воды (PawnsArrivalModeWorker_EmergeFromWater.Arrive) ставит появление пешек на карту
/// Find.CurrentMap вместо карты события — ошибка игры, в 1.7.5021 ещё есть. У игроков, смотрящих на разные
/// карты, пешки появлялись бы на разных картах. В сессии берётся карта события.
/// </summary>
[HarmonyPatch(typeof(PawnsArrivalModeWorker_EmergeFromWater), nameof(PawnsArrivalModeWorker_EmergeFromWater.Arrive))]
internal static class Patch_EmergeFromWater_TargetMap
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo currentMap = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
        int replaced = 0;
        foreach (CodeInstruction ci in instructions)
        {
            if (ci.Calls(currentMap))
            {
                // Стек: parms (аргумент 2) → MapFor(parms).
                yield return new CodeInstruction(OpCodes.Ldarg_2).MoveLabelsFrom(ci);
                yield return CodeInstruction.Call(typeof(Patch_EmergeFromWater_TargetMap), nameof(MapFor));
                replaced++;
                continue;
            }
            yield return ci;
        }
        if (replaced == 0)
        {
            Log.Message("[RWMLWC] EmergeFromWater.Arrive больше не берёт Find.CurrentMap — правка не нужна.");
        }
    }

    private static Map MapFor(IncidentParms parms) => PerMapSim.Enabled && parms.target is Map map ? map : Find.CurrentMap;
}

/// <summary>
/// Поиск клеток мира (FastTileFinder.Query: места заданий, лагерей) идёт параллельными пакетами и
/// заполняет 50 мест результата «кто первый успел» (Interlocked.Increment). Какие клетки попадут
/// в результат, зависит от потоков машины. В сессии — один пакет: клетки проверяются по порядку.
/// </summary>
[HarmonyPatch(typeof(FastTileFinder), nameof(FastTileFinder.Query))]
internal static class Patch_FastTileFinder_Query_SingleBatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo ideal = AccessTools.Method(typeof(UnityData), nameof(UnityData.GetIdealBatchCount));
        int replaced = 0;
        foreach (CodeInstruction ci in instructions)
        {
            if (ci.Calls(ideal))
            {
                ci.operand = AccessTools.Method(typeof(Patch_FastTileFinder_Query_SingleBatch), nameof(BatchCount));
                replaced++;
            }
            yield return ci;
        }
        if (replaced == 0)
        {
            Log.Warning("[RWMLWC] FastTileFinder.Query изменился — поиск клеток мира может отличаться у игроков.");
        }
    }

    private static int BatchCount(int length) => PerMapSim.Enabled ? Math.Max(1, length) : UnityData.GetIdealBatchCount(length);
}

/// <summary>
/// Имена новых пешек: TryGetRandomUnusedSolidName перемешивает общий список имён на месте, и порядок
/// списка тянется через всю жизнь процесса (все прошлые игры и пешки). Тогда у игрока, давно запустившего
/// игру, и у только что вошедшего одинаковые числа Rand дают разные имена. В сессии перед выбором списки
/// приводятся к одному порядку. Сам вызов уже идёт на отдельном состоянии Rand (TextRandIsolation).
/// </summary>
[HarmonyPatch(typeof(PawnBioAndNameGenerator), nameof(PawnBioAndNameGenerator.TryGetRandomUnusedSolidName))]
internal static class Patch_SolidNames_CanonicalOrder
{
    private static readonly Comparison<NameTriple> byName = (a, b) =>
    {
        int c = string.CompareOrdinal(a.First, b.First);
        if (c == 0)
        {
            c = string.CompareOrdinal(a.Last, b.Last);
        }
        return c != 0 ? c : string.CompareOrdinal(a.Nick, b.Nick);
    };

    private static void Prefix()
    {
        if (!PerMapSim.Enabled)
        {
            return;
        }
        PawnNameDatabaseSolid.GetListForGender(GenderPossibility.Either)?.Sort(byName);
        PawnNameDatabaseSolid.GetListForGender(GenderPossibility.Male)?.Sort(byName);
        PawnNameDatabaseSolid.GetListForGender(GenderPossibility.Female)?.Sort(byName);
    }
}

/// <summary>
/// Задание «просьба о торговле»: число запрошенных вещей берётся под зерном с thingDef.GetHashCode().
/// Хеш определения — хеш строки его имени, а в .NET хеш строки свой в каждом процессе. В сессии вместо
/// него берётся shortHash — он одинаков у всех (считается стабильным хешем имени).
/// </summary>
[HarmonyPatch(typeof(QuestNode_TradeRequest_GetRequestedThing), "RandomRequestCount")]
internal static class Patch_TradeRequest_StableDefHash
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo hash = AccessTools.Method(typeof(object), nameof(GetHashCode));
        int replaced = 0;
        foreach (CodeInstruction ci in instructions)
        {
            if (ci.Calls(hash))
            {
                yield return CodeInstruction.Call(typeof(Patch_TradeRequest_StableDefHash), nameof(DefHash)).MoveLabelsFrom(ci);
                replaced++;
                continue;
            }
            yield return ci;
        }
        if (replaced != 1)
        {
            Log.Warning($"[RWMLWC] QuestNode_TradeRequest_GetRequestedThing.RandomRequestCount изменился (хешей {replaced}).");
        }
    }

    private static int DefHash(object def) => PerMapSim.Enabled && def is Def d ? d.shortHash : def.GetHashCode();
}

/// <summary>
/// Поиск пути (PathFinder.PathFinderTick в MapPreTick) ставит задания, которые выполняются в других потоках
/// параллельно с тиком карты, а завершаются только в начале следующего тика. Задание «двери и пешки на пути»
/// (PathGridDoorsBlockedJob) при этом читает живые объекты пешек — позицию, текущее задание, — пока основной
/// поток их двигает. Что оно увидит, зависит от потоков машины, а от этого — путь и всё дальше (найдено
/// в RimWorld Multiplayer, PathFinderPatch). В сессии задания завершаются сразу после постановки, до тика пешек.
/// Результаты по-прежнему забираются в начале следующего тика. Цена — поиск пути больше не идёт
/// одновременно с тиком (замера нет).
/// </summary>
[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PathFinderTick))]
internal static class Patch_PathFinder_CompleteJobsBeforeTick
{
    private static readonly MethodInfo? complete = AccessTools.Method(typeof(PathFinder), "ForceCompleteScheduledJobs");
    private static bool warned;

    private static void Postfix(PathFinder __instance)
    {
        if (!PerMapSim.Enabled)
        {
            return;
        }
        if (complete == null)
        {
            if (!warned)
            {
                warned = true;
                Log.Warning("[RWMLWC] PathFinder.ForceCompleteScheduledJobs не найден — поиск пути идёт параллельно с тиком.");
            }
            return;
        }
        complete.Invoke(__instance, null);
    }
}

/// <summary>
/// Мошки (не сохраняемые вещи) регистрировались в сетке вещей карты. Появляются они только у того, кто
/// смотрит на карту (VisualRandIsolation), поэтому списки вещей клеток у игроков разные, а их читает
/// симуляция. В сессии не сохраняемые вещи в сетку не попадают (как в RimWorld Multiplayer); снятие с
/// сетки отсутствующей вещи игра переносит без ошибок.
/// </summary>
[HarmonyPatch(typeof(ThingGrid), nameof(ThingGrid.Register))]
internal static class Patch_ThingGrid_Register_SaveableOnly
{
    private static bool Prefix(Thing t) => !PerMapSim.Enabled || t.def.isSaveable;
}
