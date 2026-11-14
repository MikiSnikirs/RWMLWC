using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Изоляция чисто визуального кода, который игра вызывает из тика и который берёт числа из Rand.
/// Такой код срабатывает по-разному на разных машинах (или в разных запусках), сдвигает общий
/// счётчик генератора и этим ломает совпадение симуляции.
///
/// Метод из списка выполняется между Rand.PushState и Rand.PopState: его вызовы на состояние
/// симуляции не влияют. Сам эффект может выглядеть по-разному у разных игроков — это допустимо.
///
/// Каждая запись списка должна иметь доказательство в docs/research/sim.md (раздел 6б).
/// </summary>
[HarmonyPatch]
internal static class VisualRandIsolation
{
    // (тип, метод). Тип ищется по имени: часть таких классов в игре не публичные.
    private static readonly (string type, string method)[] Targets =
    {
        // Тени рыб (Odyssey): момент появления зависит от WaterBody.GetHashCode, который включает
        // адрес объекта и потому различается между запусками. Найдено трассировкой TST-010.
        ("Verse.FishShadowComponent", "MapComponentTick"),

        // Звуки, запускаемые из тика (крики животных и т. п.). Будет ли звук сыгран, решает
        // SoundSlotManager.CanPlayNow по реальному времени; сыгранный звук берёт числа из Rand
        // (SubSoundDef.RandomizedResolvedGrain). Найдено трассировкой T-009: после перезагрузки
        // сейва в работающей игре крик животного прозвучал на тике, где в свежем процессе его не было.
        ("Verse.Sound.SoundStarter", "PlayOneShot"),
        ("Verse.Sound.SoundStarter", "TrySpawnSustainer"),
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach ((string type, string method) target in Targets)
        {
            System.Type? type = AccessTools.TypeByName(target.type);
            MethodInfo? found = type == null ? null : AccessTools.Method(type, target.method);
            if (found != null)
            {
                yield return found;
            }
            else
            {
                Log.Warning($"[RWMLWC] VisualRandIsolation: не найден {target.type}.{target.method}");
            }
        }
    }

    private static void Prefix(out bool __state)
    {
        __state = PerMapSim.Enabled;
        if (__state)
        {
            Rand.PushState();
            RandTracer.PushSuppress();
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            RandTracer.PopSuppress();
            Rand.PopState();
        }
    }
}
