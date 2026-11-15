using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Диагностика T-076 (ключ метки <c>trackcurrentmap=1</c>): какие места кода внутри тика читают текущую карту
/// игрока (<c>Game.CurrentMap</c>). У игроков она разная, и такой код в симуляции ведёт себя по-разному.
/// Уникальные цепочки вызовов пишутся в <c>currentmap-reads.txt</c> при каждой сводке.
/// </summary>
public static class SimReadsProbe
{
    private static readonly Dictionary<string, int> sites = new Dictionary<string, int>();
    private static bool? on;

    public static bool On => on ??= CaravanSelfTest.ArmKey("trackcurrentmap=1");

    internal static void Record()
    {
        if (!MapRandContext.InTick || VisualRandIsolation.Depth != 0)
        {
            return;
        }
        var st = new StackTrace(2, false);
        var sb = new System.Text.StringBuilder();
        int n = 0;
        for (int i = 0; i < st.FrameCount && n < 6; i++)
        {
            MethodBase? m = st.GetFrame(i)?.GetMethod();
            if (m?.DeclaringType == null) continue;
            if (n > 0) sb.Append(" < ");
            sb.Append(m.DeclaringType.Name).Append('.').Append(m.Name);
            n++;
        }
        string key = sb.ToString();
        sites[key] = sites.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    public static void Write()
    {
        if (!On || sites.Count == 0) return;
        try
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, int> kv in sites) lines.Add(kv.Value + "\t" + kv.Key);
            lines.Sort(StringComparer.Ordinal);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "currentmap-reads.txt"), lines);
        }
        catch (Exception)
        {
        }
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Getter)]
internal static class Patch_Game_CurrentMap_Probe
{
    private static void Postfix()
    {
        if (SimReadsProbe.On)
        {
            SimReadsProbe.Record();
        }
    }
}
