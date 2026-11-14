using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Sim;

// Позиция отрисовки пешки, читаемая из тика (T-018).
//
// Pawn.DrawPos — это Pawn_DrawTracker.DrawPos: сглаженная позиция (PawnTweener.TweenedPos) плюс
// дрожание и наклон. Сглаживание считается по кадрам (RealTime.deltaTime) и к тому же кэшируется
// на весь кадр, в который может уложиться несколько тиков. При этом игровая логика читает DrawPos
// прямо из тика: Verb_LaunchProjectile берёт её как точку вылета снаряда, Verb_MeleeAttack и
// Pawn_RotationTracker — как точку, куда смотреть. На машине с другой частотой кадров снаряд
// вылетает из чуть другой точки и долетает на тик раньше или позже.
// Найдено в TST-017: рейд на машине B не повторялся от запуска к запуску.
//
// В тике отдаём позицию, зависящую только от состояния симуляции: корень сглаживания
// (PawnTweener.TweenedPosRoot — клетка пешки плюс доля пройденного пути), без кадровых добавок.
// Высота (Y) берётся из исходного результата. Вне тика (отрисовка) ничего не меняется.

[HarmonyPatch(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos), MethodType.Getter)]
internal static class Patch_Pawn_DrawTracker_DrawPos
{
    private static readonly Func<PawnTweener, Vector3>? tweenedPosRoot = MakeRootGetter();

    private static Func<PawnTweener, Vector3>? MakeRootGetter()
    {
        MethodInfo? method = AccessTools.Method(typeof(PawnTweener), "TweenedPosRoot");
        if (method == null)
        {
            Log.Error("[RWMLWC] PawnTweener.TweenedPosRoot не найден: позиция пешек в тике останется зависимой от кадров");
            return null;
        }
        return (Func<PawnTweener, Vector3>)Delegate.CreateDelegate(typeof(Func<PawnTweener, Vector3>), null, method);
    }

    private static void Postfix(Pawn_DrawTracker __instance, ref Vector3 __result)
    {
        if (tweenedPosRoot == null || !PerMapSim.Enabled || !MapRandContext.InTick)
        {
            return;
        }
        Vector3 root = tweenedPosRoot(__instance.tweener);
        __result = new Vector3(root.x, __result.y, root.z);
    }
}
