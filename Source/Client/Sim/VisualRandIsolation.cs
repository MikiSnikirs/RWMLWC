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

        // Вспышки и мошки (flecks, motes): пыль из-под ног, дым выстрела, искры, надписи над головой.
        // Игра создаёт их из тика и тут же берёт для них числа из Rand, но только если
        // GenView.ShouldSpawnMotesAt вернул true — а он проверяет, что карта открыта у игрока
        // (map == Find.CurrentMap), а для части эффектов ещё и что клетка в кадре камеры; мошки
        // к тому же ограничены счётчиком, который зависит от их времени жизни в реальных секундах.
        // У двух игроков, смотрящих на разные карты, число вызовов Rand в тике было бы разным.
        // «*» — все открытые статические методы класса. См. docs/research/sim.md, 6ж.
        ("RimWorld.FleckMaker", "*"),
        ("RimWorld.MoteMaker", "*"),
        ("Verse.FleckManager", "CreateFleck"),
        ("Verse.Mote", ".ctor"),
        ("Verse.Effecter", "EffectTick"),
        ("Verse.Effecter", "Trigger"),
        ("RimWorld.CompFleckEmitterLongTerm", "CompTick"),
        ("Verse.LavaFXComponent", "MapComponentTick"),

        // Крик животного: звучит, только если животное в кадре камеры. Решение принимается в тике,
        // а само действие — только звук. Внутри изолированного кода камера настоящая (см. SimCamera).
        ("Verse.Pawn_CallTracker", "TryDoCall"),
    };

    /// <summary>Сейчас выполняется изолированный визуальный код (вложенность). Для SimCamera.</summary>
    public static int Depth { get; private set; }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach ((string type, string method) target in Targets)
        {
            System.Type? type = AccessTools.TypeByName(target.type);
            int count = 0;
            if (type != null)
            {
                const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                if (target.method == ".ctor")
                {
                    foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        count++;
                        yield return ctor;
                    }
                }
                else
                {
                    // Все перегрузки с этим именем; «*» — все открытые статические методы.
                    foreach (MethodInfo method in type.GetMethods(declared))
                    {
                        bool match = target.method == "*" ? method.IsStatic && method.IsPublic : method.Name == target.method;
                        if (match && !method.IsAbstract && !method.IsGenericMethodDefinition && method.GetMethodBody() != null)
                        {
                            count++;
                            yield return method;
                        }
                    }
                }
            }
            if (count == 0)
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
            Depth++;
        }
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            Depth--;
            RandTracer.PopSuppress();
            Rand.PopState();
        }
    }
}
