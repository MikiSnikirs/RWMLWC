using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Input.MpApi;

/// <summary>
/// Multiplayer Compatibility ставит патчи модулями по модам. Часть модулей после установки патчей лезет во
/// внутренности RimWorld Multiplayer, которых в RWMLWC нет, и падает — а уже поставленные патчи остаются с
/// незаполненными полями и бросают исключение на каждом вызове (Fortified: постфикс Map.MapPostTick падал каждый
/// тик, игра стояла). Здесь каждый метод-патч Compatibility получает финализатор: исключение пишется в журнал
/// один раз и гасится; префикс в этом случае пропускает оригинал как обычно (возвращает true).
/// Исключение у всех клиентов одинаковое (код одинаковый), так что поведение остаётся общим.
/// </summary>
[StaticConstructorOnStartup]
internal static class CompatPatchGuard
{
    private const string CompatOwner = "rimworld.multiplayer.compat";
    private static readonly HashSet<string> reported = new HashSet<string>();

    static CompatPatchGuard()
    {
        LongEventHandler.ExecuteWhenFinished(Install);
    }

    private static void Install()
    {
        try
        {
            HashSet<MethodInfo> seen = new HashSet<MethodInfo>();
            HarmonyMethod voidFinalizer = new HarmonyMethod(typeof(CompatPatchGuard), nameof(FinalizerVoid));
            HarmonyMethod boolFinalizer = new HarmonyMethod(typeof(CompatPatchGuard), nameof(FinalizerBool));
            int guarded = 0;
            foreach (MethodBase original in new List<MethodBase>(Harmony.GetAllPatchedMethods()))
            {
                Patches? info = Harmony.GetPatchInfo(original);
                if (info == null)
                {
                    continue;
                }
                foreach (Patch patch in Combine(info.Prefixes, info.Postfixes, info.Finalizers))
                {
                    if (patch.owner != CompatOwner || !seen.Add(patch.PatchMethod))
                    {
                        continue;
                    }
                    try
                    {
                        MpSync.Harmony.Patch(patch.PatchMethod,
                            finalizer: patch.PatchMethod.ReturnType == typeof(bool) ? boolFinalizer : patch.PatchMethod.ReturnType == typeof(void) ? voidFinalizer : null);
                        guarded++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[RWMLWC] Compatibility: не удалось защитить {patch.PatchMethod.DeclaringType?.Name}.{patch.PatchMethod.Name}: {ex.Message}");
                    }
                }
            }
            if (guarded > 0)
            {
                Log.Message($"[RWMLWC] Compatibility: патчей под защитой от исключений {guarded}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] Compatibility: защита патчей не установлена: " + ex);
        }
    }

    private static IEnumerable<Patch> Combine(params IEnumerable<Patch>[] lists)
    {
        foreach (IEnumerable<Patch> list in lists)
        {
            foreach (Patch p in list)
            {
                yield return p;
            }
        }
    }

    private static void Report(MethodBase method, Exception ex)
    {
        string key = method.DeclaringType?.FullName + "." + method.Name;
        if (reported.Add(key))
        {
            Log.Warning($"[RWMLWC] Compatibility: патч {key} падает (модуль не дозагрузился без RimWorld Multiplayer); исключения гасятся, пишется один раз: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Exception? FinalizerVoid(MethodBase __originalMethod, Exception? __exception)
    {
        if (__exception != null)
        {
            Report(__originalMethod, __exception);
        }
        return null;
    }

    private static Exception? FinalizerBool(MethodBase __originalMethod, Exception? __exception, ref bool __result)
    {
        if (__exception != null)
        {
            Report(__originalMethod, __exception);
            __result = true; // префикс упал — оригинал выполняется, как будто патча нет
        }
        return null;
    }
}
