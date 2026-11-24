using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Инструмент T-056 (docs/ARCHITECTURE.md, 11.3.3): где тик карты пишет в общий мир. Тестовый ключ
/// <c>worldwrites=1</c>. Перед тиком каждой вещи карты и каждой подсистемы карты (MapPreTick/MapPostTick)
/// снимается дешёвый слепок общего состояния по частям; после — снова. Изменившаяся часть записывается вместе
/// с источником (def вещи или «map pre/post tick»). Сводка — каждые <see cref="ReportEvery"/> тиков и в конце.
///
/// Это диагностика, не логика симуляции: порядок обхода словарей здесь ни на что не влияет (только суммы для
/// сравнения «до/после» внутри одного процесса). Находит места записи, которые сработали в прогоне, но не
/// доказывает, что других нет.
/// </summary>
internal static class WorldWriteProbe
{
    public static bool Enabled;
    public const int ReportEvery = 2500;

    private static readonly string[] Parts =
    {
        "research", "factions", "worldPawns", "quests", "ids", "tales", "playLog", "letters", "archive",
        "adaptation", "incidentQueue", "worldObjects", "worldConditions", "ideos", "history",
    };

    private static readonly double[] before = new double[Parts.Length];
    private static readonly double[] after = new double[Parts.Length];
    private static readonly Dictionary<string, int> hits = new Dictionary<string, int>();
    private static readonly Dictionary<string, int> firstTick = new Dictionary<string, int>();
    private static int depth;
    private static int lastReport = -1;

    private static readonly AccessTools.FieldRef<ResearchManager, Dictionary<ResearchProjectDef, float>> progressRef =
        AccessTools.FieldRefAccess<ResearchManager, Dictionary<ResearchProjectDef, float>>("progress");
    private static readonly AccessTools.FieldRef<WorldPawns, HashSet<Pawn>> aliveRef = AccessTools.FieldRefAccess<WorldPawns, HashSet<Pawn>>("pawnsAlive");
    private static readonly AccessTools.FieldRef<WorldPawns, HashSet<Pawn>> deadRef = AccessTools.FieldRefAccess<WorldPawns, HashSet<Pawn>>("pawnsDead");
    private static readonly AccessTools.FieldRef<WorldPawns, HashSet<Pawn>> mothRef = AccessTools.FieldRefAccess<WorldPawns, HashSet<Pawn>>("pawnsMothballed");
    private static AccessTools.FieldRef<UniqueIDsManager, int>[]? idRefs;
    private static string[]? idNames;
    private static int[] idsBefore = new int[0];
    private static int[] idsAfter = new int[0];

    private static void Snapshot(double[] into)
    {
        Game g = Current.Game;
        double research = 0;
        foreach (float v in progressRef(g.researchManager).Values)
        {
            research += v;
        }
        into[0] = research;

        double factions = 0;
        Faction player = Faction.OfPlayer;
        List<Faction> all = Find.FactionManager.AllFactionsListForReading;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] != player)
            {
                factions += all[i].GoodwillWith(player) * 7 + (int)all[i].RelationKindWith(player) + (all[i].defeated ? 1000 : 0);
            }
        }
        into[1] = factions + all.Count * 100000;

        WorldPawns wp = Find.WorldPawns;
        into[2] = aliveRef(wp).Count + deadRef(wp).Count * 1000.0 + mothRef(wp).Count * 1000000.0;

        List<Quest> quests = Find.QuestManager.QuestsListForReading;
        double q = quests.Count;
        for (int i = 0; i < quests.Count; i++)
        {
            q += (int)quests[i].State * (i + 1) * 0.001;
        }
        into[3] = q;

        if (idRefs == null)
        {
            var fields = AccessTools.GetDeclaredFields(typeof(UniqueIDsManager)).Where(f => f.FieldType == typeof(int) && !f.IsStatic).ToArray();
            idRefs = fields.Select(f => AccessTools.FieldRefAccess<UniqueIDsManager, int>(f.Name)).ToArray();
            idNames = fields.Select(f => f.Name).ToArray();
            idsBefore = new int[idRefs.Length];
            idsAfter = new int[idRefs.Length];
        }
        int[] idsInto = ReferenceEquals(into, before) ? idsBefore : idsAfter;
        double ids = 0;
        for (int i = 0; i < idRefs.Length; i++)
        {
            idsInto[i] = idRefs[i](g.uniqueIDsManager);
            ids += idsInto[i] * (double)(i + 1);
        }
        into[4] = ids;

        into[5] = Find.TaleManager.AllTalesListForReading.Count;
        into[6] = Find.PlayLog.AllEntries.Count;
        into[7] = Find.LetterStack.LettersListForReading.Count;
        into[8] = Find.Archive.ArchivablesListForReading.Count;
        into[9] = Find.StoryWatcher.watcherAdaptation.AdaptDays;
        into[10] = Find.Storyteller.incidentQueue.Count;
        into[11] = Find.WorldObjects.AllWorldObjects.Count;
        into[12] = Find.World.gameConditionManager.ActiveConditions.Count;
        into[13] = ModsConfig.IdeologyActive ? Find.IdeoManager.IdeosListForReading.Sum(i => (double)(i.development?.points ?? 0)) : 0;
        into[14] = Find.History?.archive?.ArchivablesListForReading.Count ?? 0;
    }

    /// <summary>Подробный слепок мира строкой «часть=значение» (для файла хешей, T-056).</summary>
    public static string DeepDigest()
    {
        double[] v = new double[Parts.Length];
        Snapshot(v);
        return string.Join(";", Parts.Select((p, i) => p + "=" + v[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static void Begin()
    {
        if (depth++ == 0)
        {
            Snapshot(before);
        }
    }

    public static void End(string source)
    {
        if (--depth != 0)
        {
            return;
        }
        Snapshot(after);
        for (int i = 0; i < Parts.Length; i++)
        {
            if (after[i] != before[i])
            {
                string part = Parts[i];
                if (i == 4 && idNames != null)
                {
                    part = "ids." + string.Join("+", Enumerable.Range(0, idNames.Length).Where(k => idsAfter[k] != idsBefore[k]).Select(k => idNames[k]));
                }
                string key = part + " <- " + source;
                hits.TryGetValue(key, out int n);
                hits[key] = n + 1;
                if (n == 0)
                {
                    firstTick[key] = Find.TickManager.TicksGame;
                    TestAutomation.Note($"worldwrites: first {key} at tick {Find.TickManager.TicksGame} ({before[i]} -> {after[i]})");
                }
            }
        }
    }

    public static void MaybeReport(bool force)
    {
        int tick = Find.TickManager.TicksGame;
        if (!force && (lastReport >= 0 && tick - lastReport < ReportEvery))
        {
            return;
        }
        if (lastReport < 0 && !force)
        {
            lastReport = tick;
            return;
        }
        lastReport = tick;
        TestAutomation.Note($"worldwrites: tick {tick}, by part: " + string.Join("; ", hits.GroupBy(kv => kv.Key.Substring(0, kv.Key.IndexOf(" <- ", System.StringComparison.Ordinal)))
            .OrderByDescending(gr => gr.Sum(kv => kv.Value)).Select(gr => $"{gr.Key} x{gr.Sum(kv => kv.Value)} ({gr.Count()} sources)")));
        TestAutomation.Note($"worldwrites: tick {tick}, {hits.Count} kinds: " +
            string.Join("; ", hits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, System.StringComparer.Ordinal).Select(kv => $"{kv.Key} x{kv.Value} (from {firstTick[kv.Key]})")));
    }

    internal static string SourceOf(Thing t) => "thing " + t.def.defName;
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
internal static class Patch_Thing_DoTick_WorldWrites
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(Thing __instance, out bool __state)
    {
        __state = WorldWriteProbe.Enabled && __instance.MapHeld != null && Current.Game != null;
        if (__state)
        {
            WorldWriteProbe.Begin();
        }
    }

    private static void Finalizer(Thing __instance, bool __state)
    {
        if (__state)
        {
            WorldWriteProbe.End(WorldWriteProbe.SourceOf(__instance));
        }
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
internal static class Patch_Map_MapPreTick_WorldWrites
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(out bool __state)
    {
        __state = WorldWriteProbe.Enabled;
        if (__state)
        {
            WorldWriteProbe.Begin();
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            WorldWriteProbe.End("map pre-tick");
        }
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
internal static class Patch_Map_MapPostTick_WorldWrites
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(out bool __state)
    {
        __state = WorldWriteProbe.Enabled;
        if (__state)
        {
            WorldWriteProbe.Begin();
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            WorldWriteProbe.End("map post-tick");
            WorldWriteProbe.MaybeReport(false);
        }
    }
}
