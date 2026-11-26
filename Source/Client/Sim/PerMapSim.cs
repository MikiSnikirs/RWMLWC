using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Раздельная симуляция карт (T-005).
///
/// В игре списки тиков и генератор случайных чисел общие на все карты
/// (см. docs/research/sim.md, разделы 2–3). Здесь поверх этого делается два независимых свойства:
///  1. карту можно заморозить: её вещи и подсистемы не тикают;
///  2. у каждой карты своё состояние Rand, поэтому тик одной карты не сдвигает случайность другой.
///
/// По умолчанию выключено: в одиночной игре поведение ванильное.
/// </summary>
public static class PerMapSim
{
    /// <summary>Включает раздельную симуляцию. Пока включается только отладочным действием.</summary>
    public static bool Enabled { get; private set; }

    public static void SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
        {
            return;
        }
        if (enabled)
        {
            MapRandContext.OnEnabled();
        }
        else
        {
            MapRandContext.OnDisabled();
        }
        Enabled = enabled;
    }

    /// <summary>
    /// Общая для сессии добавка к начальному состоянию Rand карт.
    /// Будет задаваться сервером при входе в сессию (T-013/T-014); до тех пор 0.
    /// </summary>
    public static uint SessionSeed;

    /// <summary>
    /// Спящие карты (T-056, этап 1): их считает другой игрок, этот компьютер их не тикает и не шлёт их хеш.
    /// Только в памяти этого процесса, не в сейве и не в состоянии игры — в отличие от стазиса.
    /// Пока задаётся только тестовым ключом <c>sleepforeign=1</c> (карты колоний других игроков).
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<int> sleeping = new System.Collections.Generic.HashSet<int>();

    public static bool SleepForeignTest;

    public static bool IsSleeping(Map? map) => Enabled && map != null && sleeping.Contains(map.uniqueID);

    public static void SetSleeping(Map map, bool asleep)
    {
        if (asleep ? sleeping.Add(map.uniqueID) : sleeping.Remove(map.uniqueID))
        {
            TestAutomation.Note($"sleeping: map {map.uniqueID} {(asleep ? "asleep" : "awake")} at tick {Find.TickManager.TicksGame}");
        }
    }

    /// <summary>Тик карты пропускается: стазис (у всех) или спящая карта (только здесь).</summary>
    public static bool SkipsTick(Map? map) => IsFrozen(map) || IsSleeping(map);

    /// <summary>Тестовый режим sleepforeign: каждый кадр — карты колоний других игроков спят.</summary>
    public static void UpdateSleepForeign()
    {
        if (!SleepForeignTest || !Enabled || Current.Game == null)
        {
            return;
        }
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        foreach (Map m in Find.Maps)
        {
            int owner = m.Parent is RimWorld.Planet.Settlement st ? colonies?.OwnerOf(st) ?? 0 : 0;
            SetSleeping(m, owner != 0 && owner != LockstepClient.PlayerId);
        }
    }

    public static bool IsFrozen(Map? map)
    {
        if (!Enabled || map == null)
        {
            return false;
        }
        MapSimState? state = MapSimState.For(map);
        return state != null && state.frozen;
    }

    /// <summary>
    /// Тестовый ключ sleepremove=1 (T-056, план 11.4б): раздельный счёт колоний без включения в игре.
    /// Настоящий режим — <see cref="SplitColoniesComponent.enabled"/> (в сейве, общий для всех).
    /// </summary>
    public static bool SleepRemoveTest;

    /// <summary>Раздельный счёт колоний: у игрока нет карт колоний других игроков в сети (Sim/SplitColonies.cs).</summary>
    public static bool Split => SleepRemoveTest || SplitColonies.ModeOn;
}
