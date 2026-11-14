using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Выбор приёма ближнего боя — состояние симуляции, интерфейс его не меняет.
///
/// Pawn_MeleeVerbs.TryGetMeleeVerb помнит выбранный приём, его цель и тик выбора; раз в 60 тиков,
/// при смене цели или если приём сброшен, выбирает заново через Rand (ChooseMeleeVerb). Тот же
/// метод зовёт отрисовка (PawnRenderUtility → Pawn.CurrentEffectiveVerb → TryGetAttackVerb(null)),
/// меню приказов (FloatMenuUtility) и подсказки — в кадре, то есть у каждого игрока в свой тик
/// и со своим состоянием Rand. Выбранный там приём потом берёт бой в тике.
///
/// В сессии вне тика метод только отвечает: текущий приём, если он ещё годен, иначе первый
/// доступный; ничего не запоминает и Rand не трогает.
/// </summary>
[HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryGetMeleeVerb))]
internal static class Patch_Pawn_MeleeVerbs_TryGetMeleeVerb
{
    private static bool Prefix(Pawn_MeleeVerbs __instance, Verb ___curMeleeVerb, Thing target, ref Verb? __result)
    {
        if (!PerMapSim.Enabled || (MapRandContext.InTick && VisualRandIsolation.Depth == 0))
        {
            return true;
        }
        Pawn pawn = __instance.Pawn;
        if (___curMeleeVerb != null && ___curMeleeVerb.IsStillUsableBy(pawn) && (target == null || ___curMeleeVerb.IsUsableOn(target)))
        {
            __result = ___curMeleeVerb;
            return false;
        }
        __result = null;
        List<VerbEntry> verbs = __instance.GetUpdatedAvailableVerbsList(terrainTools: false);
        for (int i = 0; i < verbs.Count; i++)
        {
            Verb verb = verbs[i].verb;
            if (target == null || verb.IsUsableOn(target))
            {
                __result = verb;
                break;
            }
        }
        return false;
    }
}
