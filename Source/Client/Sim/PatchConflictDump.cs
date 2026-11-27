using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Диагностика для сборок модов (T-082): какие методы игры патчит RWMLWC и кто ещё патчит те же методы.
/// Включается файлом-меткой RWMLWC/patchdump.txt в папке данных игры (-savedatafolder). Пишет
/// RWMLWC/patch-conflicts.txt: метод, наши патчи, чужие патчи по владельцам. Чужой префикс опасен
/// больше остального: он может не дать выполниться оригиналу и нашему перехвату.
/// </summary>
[StaticConstructorOnStartup]
internal static class PatchConflictDump
{
    static PatchConflictDump()
    {
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
        if (!File.Exists(Path.Combine(dir, "patchdump.txt")))
        {
            return;
        }
        // После всех StaticConstructorOnStartup модов: часть модов ставит патчи там.
        LongEventHandler.ExecuteWhenFinished(() => Write(dir));
    }

    private static string Describe(Patch p) => $"{p.owner} {p.PatchMethod.DeclaringType?.FullName}.{p.PatchMethod.Name} (приоритет {p.priority})";

    private static void Write(string dir)
    {
        try
        {
            string ours = RwmlwcMod.HarmonyInstance.Id;
            List<string> lines = new List<string>();
            int total = 0;
            int shared = 0;
            int foreignPrefix = 0;
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().OrderBy(m => m.DeclaringType?.FullName + "." + m.Name, StringComparer.Ordinal))
            {
                Patches? info = Harmony.GetPatchInfo(method);
                if (info == null || !info.Owners.Contains(ours))
                {
                    continue;
                }
                total++;
                List<string> others = info.Owners.Where(o => o != ours).OrderBy(o => o, StringComparer.Ordinal).ToList();
                if (others.Count == 0)
                {
                    continue;
                }
                shared++;
                List<Patch> prefixes = info.Prefixes.Where(p => p.owner != ours).ToList();
                if (prefixes.Count > 0)
                {
                    foreignPrefix++;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append(method.DeclaringType?.FullName).Append('.').Append(method.Name)
                  .Append(" | наши: ").Append(string.Join(", ", info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                      .Where(p => p.owner == ours).Select(p => p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name)));
                foreach (Patch p in prefixes)
                {
                    sb.Append("\n    ПРЕФИКС ").Append(Describe(p));
                }
                foreach (Patch p in info.Transpilers.Where(p => p.owner != ours))
                {
                    sb.Append("\n    транспайлер ").Append(Describe(p));
                }
                foreach (Patch p in info.Postfixes.Concat(info.Finalizers).Where(p => p.owner != ours))
                {
                    sb.Append("\n    постфикс ").Append(Describe(p));
                }
                lines.Add(sb.ToString());
            }
            lines.Insert(0, $"# RWMLWC: методов с нашими патчами {total}, из них патчат и другие {shared}, с чужим префиксом {foreignPrefix}");
            File.WriteAllText(Path.Combine(dir, "patch-conflicts.txt"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            Log.Message($"[RWMLWC] пересечения патчей: {shared} из {total} методов, с чужим префиксом {foreignPrefix} — RWMLWC/patch-conflicts.txt");
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] выгрузка пересечений патчей упала: " + ex);
        }
    }
}
