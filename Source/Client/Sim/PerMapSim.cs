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
    public static bool Enabled;

    /// <summary>
    /// Общая для сессии добавка к начальному состоянию Rand карт.
    /// Будет задаваться сервером при входе в сессию (T-013/T-014); до тех пор 0.
    /// </summary>
    public static uint SessionSeed;

    public static bool IsFrozen(Map? map)
    {
        if (!Enabled || map == null)
        {
            return false;
        }
        MapSimState? state = MapSimState.For(map);
        return state != null && state.frozen;
    }
}
