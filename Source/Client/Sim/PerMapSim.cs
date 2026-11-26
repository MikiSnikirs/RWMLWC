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
    /// Тестовый режим sleepremove (T-056, план 11.4б шаг 1, вариант (б)): чужой колонии у игрока нет вовсе —
    /// заглушка без карты. После появления обеих колоний TestAutomation просит общий снимок, и каждый клиент
    /// перед загрузкой вырезает из него карты чужих колоний (<see cref="StripForeignMaps"/>). Мир при этом
    /// один и тот же; хеш мира не включает список карт этого процесса (<see cref="WorldStateHasher"/>).
    /// </summary>
    public static bool SleepRemoveTest;

    /// <summary>
    /// Снимок всей игры к загрузке в режиме sleepremove: без карт колоний других игроков.
    /// Вызывается между тиками, до <c>GameSnapshot.Apply</c>; владельцы читаются из текущей игры
    /// (она в том же тике, что и снимок). Без режима или без чужих карт — снимок как был.
    /// </summary>
    public static byte[] StripForeignMaps(byte[] packed)
    {
        if (!SleepRemoveTest || Current.Game == null)
        {
            return packed;
        }
        RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
        if (colonies == null)
        {
            return packed;
        }
        System.Collections.Generic.List<int> remove = new System.Collections.Generic.List<int>();
        foreach (Map m in Find.Maps)
        {
            int owner = m.Parent is RimWorld.Planet.Settlement st ? colonies.OwnerOf(st) : 0;
            if (owner != 0 && owner != LockstepClient.PlayerId)
            {
                remove.Add(m.uniqueID);
            }
        }
        if (remove.Count == 0)
        {
            return packed;
        }
        int keep = Find.CurrentMap?.uniqueID ?? -1;
        if (keep < 0 || remove.Contains(keep))
        {
            keep = -1;
            foreach (Map m in Find.Maps)
            {
                if (!remove.Contains(m.uniqueID))
                {
                    keep = m.uniqueID;
                    break;
                }
            }
        }
        byte[] raw = RWMLWC.Client.Maps.GameSnapshot.Unpack(packed);
        byte[] stripped = RWMLWC.Client.Maps.MapSnapshot.RemoveMaps(raw, remove, keep);
        TestAutomation.Note($"sleepremove: player {LockstepClient.PlayerId} reloads without maps [{string.Join(",", remove)}], keeps map {keep}");
        return RWMLWC.Client.Maps.GameSnapshot.Pack(stripped);
    }
}
