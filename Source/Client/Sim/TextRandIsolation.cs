using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Изоляция генерации текстов от Rand симуляции.
///
/// Имена, названия заданий, описания предметов искусства игра собирает из правил активного языка
/// и берёт для этого числа из Rand. У разных языков правила разные, поэтому разным получается и
/// число вызовов Rand. Чтобы игроки с разными языками могли играть вместе, такие методы в тике
/// работают на отдельном состоянии Rand: состояние симуляции после вызова то же, что до него.
///
/// Зерно отдельного состояния — от состояния симуляции и номера вызова в тике, поэтому у игроков
/// с одним языком тексты одинаковые, а повторный вызов в цикле («пока имя не свободно») даёт
/// новый результат. У игроков с разными языками сами тексты разные — это только отображение;
/// после общей загрузки мира (вход, расхождение) тексты у всех становятся как у отправителя снимка.
/// См. docs/research/sim.md, 6и.
/// </summary>
[HarmonyPatch]
internal static class TextRandIsolation
{
    private static readonly (string type, string method)[] Targets =
    {
        ("Verse.Grammar.GrammarResolver", "Resolve"),
        ("Verse.Grammar.GrammarResolver", "ResolveUnsafe"),
        ("RimWorld.NameGenerator", "GenerateName"),
        ("RimWorld.PawnBioAndNameGenerator", "GeneratePawnName"),
    };

    private static int callsThisTick;
    private static int depth;

    /// <summary>Начало тика: счётчик вызовов с нуля.</summary>
    public static void BeginTick() => callsThisTick = 0;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach ((string type, string method) target in Targets)
        {
            System.Type? type = AccessTools.TypeByName(target.type);
            int count = 0;
            if (type != null)
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name == target.method && !method.IsGenericMethodDefinition && method.GetMethodBody() != null)
                    {
                        count++;
                        yield return method;
                    }
                }
            }
            if (count == 0)
            {
                Log.Warning($"[RWMLWC] TextRandIsolation: не найден {target.type}.{target.method}");
            }
        }
    }

    private static void Prefix(out bool __state)
    {
        // Только в тике (и при исполнении команд: там тоже контекст симуляции). Вне тика в Rand
        // лежит состояние показа, изолировать нечего. Вложенные вызовы идут в уже изолированном состоянии.
        __state = PerMapSim.Enabled && MapRandContext.InTick && depth == 0;
        if (__state)
        {
            ulong state = MapRandContext.CurrentRawState;
            int seed = Gen.HashCombineInt(Gen.HashCombineInt((int)(state & 0xFFFFFFFFu), (int)(state >> 32)), callsThisTick++);
            Rand.PushState(seed);
            RandTracer.PushSuppress();
            depth++;
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            depth--;
            RandTracer.PopSuppress();
            Rand.PopState();
        }
    }
}
