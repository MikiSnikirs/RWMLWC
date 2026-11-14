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

/// <summary>
/// Лимит поселений игрока (Prefs.MaxNumberOfPlayerSettlements) — тоже личная настройка (по умолчанию 1), а от неё
/// зависит, включена ли кнопка «Основать поселение»; кнопки объектов мира проверяются ещё и при исполнении
/// команды (T-073). В сессии лимит у всех один — <see cref="SessionLimit"/> (максимум настройки игры), чтобы
/// в кооперативе каждый мог основать своё поселение. Решение B на время отсутствия A; вынести в настройки
/// сессии (host.txt / server.txt) — вопрос владельцу.
/// </summary>
[HarmonyPatch(typeof(Prefs), nameof(Prefs.MaxNumberOfPlayerSettlements), MethodType.Getter)]
internal static class Patch_Prefs_MaxNumberOfPlayerSettlements
{
    public const int SessionLimit = 5;

    private static bool Prefix(ref int __result)
    {
        if (!LockstepClient.InSession)
        {
            return true;
        }
        __result = SessionLimit;
        return false;
    }
}
