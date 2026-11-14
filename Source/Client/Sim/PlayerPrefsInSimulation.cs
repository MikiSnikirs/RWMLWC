using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Личные настройки игрока, которые читает симуляция.
///
/// «Предпочитаемые имена» (Prefs.PreferredNames): генератор пешек с вероятностью 1/2 перемешивает
/// этот список через Rand и ищет в нём подходящее имя. У каждого игрока список свой, поэтому
/// разными получились бы и число вызовов Rand, и имя новой пешки. В тике список считается пустым.
/// См. docs/research/sim.md, 6и.
/// </summary>
[HarmonyPatch(typeof(Prefs), nameof(Prefs.PreferredNames), MethodType.Getter)]
internal static class Patch_Prefs_PreferredNames
{
    private static readonly List<string> Empty = new List<string>();

    private static bool Prefix(ref List<string> __result)
    {
        if (!PerMapSim.Enabled || !MapRandContext.InTick)
        {
            return true;
        }
        Empty.Clear(); // на случай, если вызывающий код что-то в него добавил
        __result = Empty;
        return false;
    }
}
