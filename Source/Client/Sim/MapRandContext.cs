using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Переключает общий Verse.Rand между состояниями карт.
///
/// «Контекст» — чьё состояние сейчас загружено в Rand: конкретной карты или глобальное (null).
/// Глобальным пользуются мир, рассказчик, квесты, интерфейс и вещи вне карт.
/// Переключение ленивое: состояние меняется только когда следующая тикающая вещь
/// принадлежит другой карте.
/// </summary>
public static class MapRandContext
{
    private static readonly Func<ulong> getState;
    private static readonly Action<ulong> setState;

    private static Map? current;
    private static ulong globalState;

    // Третье состояние — «показ»: всё, что выполняется между тиками (интерфейс, отрисовка, звук).
    // Пока идёт тик, в Rand лежит состояние симуляции (глобальное или карты);
    // между тиками — состояние показа, и оно на симуляцию не влияет.
    private static bool inTick;
    private static ulong presentationState = 0x0000000050524553UL;

    static MapRandContext()
    {
        // Rand.StateCompressed — приватное свойство: seed | (iterations << 32).
        MethodInfo getter = AccessTools.PropertyGetter(typeof(Rand), "StateCompressed")
            ?? throw new MissingMemberException("Verse.Rand", "StateCompressed (get)");
        MethodInfo setter = AccessTools.PropertySetter(typeof(Rand), "StateCompressed")
            ?? throw new MissingMemberException("Verse.Rand", "StateCompressed (set)");
        getState = (Func<ulong>)Delegate.CreateDelegate(typeof(Func<ulong>), getter);
        setState = (Action<ulong>)Delegate.CreateDelegate(typeof(Action<ulong>), setter);
    }

    /// <summary>Карта, чьё состояние сейчас в Rand; null — глобальное.</summary>
    public static Map? Current => current;

    /// <summary>Текущее состояние Rand (для отладки и будущего хеша состояния).</summary>
    public static ulong CurrentRawState => getState();

    /// <summary>Глобальное состояние Rand, какой бы контекст ни был активен.</summary>
    public static ulong GlobalState => inTick && current == null ? getState() : globalState;

    /// <summary>Сейчас выполняется тик игры (между BeginTick и EndTick).</summary>
    public static bool InTick => inTick;

    /// <summary>Раздельная симуляция включается: текущее состояние Rand становится глобальным состоянием симуляции.</summary>
    public static void OnEnabled()
    {
        globalState = getState();
        current = null;
        inTick = false;
    }

    /// <summary>Раздельная симуляция выключается: вернуть в Rand глобальное состояние симуляции.</summary>
    public static void OnDisabled()
    {
        if (inTick)
        {
            Enter(null);
        }
        else
        {
            setState(globalState);
        }
        current = null;
        inTick = false;
    }

    /// <summary>Начало тика: убрать состояние показа, загрузить глобальное состояние симуляции.</summary>
    public static void BeginTick()
    {
        if (inTick)
        {
            return;
        }
        presentationState = getState();
        setState(globalState);
        current = null;
        inTick = true;
    }

    /// <summary>Конец тика: сохранить состояние симуляции, вернуть состояние показа.</summary>
    public static void EndTick()
    {
        if (!inTick)
        {
            return;
        }
        Enter(null);
        globalState = getState();
        setState(presentationState);
        inTick = false;
    }

    /// <summary>Задать глобальное состояние Rand (синхронизация при входе в сессию, тестовые прогоны).</summary>
    public static void SetGlobalState(ulong state)
    {
        if (inTick && current == null)
        {
            setState(state);
        }
        else
        {
            globalState = state;
        }
    }

    /// <summary>Сделать активным состояние указанной карты (null — глобальное).</summary>
    public static void Enter(Map? map)
    {
        if (!inTick || ReferenceEquals(map, current))
        {
            return; // вне тика в Rand лежит состояние показа, контексты симуляции не трогаем
        }

        // Сохранить уходящий контекст.
        if (current == null)
        {
            globalState = getState();
        }
        else
        {
            MapSimState? leaving = MapSimState.For(current);
            if (leaving != null)
            {
                leaving.randState = getState();
            }
        }

        // Загрузить приходящий.
        if (map == null)
        {
            setState(globalState);
        }
        else
        {
            MapSimState? entering = MapSimState.For(map);
            if (entering == null)
            {
                // У карты нет нашего компонента (не должно случаться): остаёмся на глобальном.
                setState(globalState);
                current = null;
                return;
            }
            if (!entering.randInitialized)
            {
                entering.randState = entering.InitialRandState();
                entering.randInitialized = true;
            }
            setState(entering.randState);
        }

        current = map;
    }

    public static void ExitToGlobal() => Enter(null);

    /// <summary>Карта удаляется: если её контекст активен, вернуться к глобальному, не сохраняя.</summary>
    public static void OnMapRemoved(Map map)
    {
        if (ReferenceEquals(map, current))
        {
            if (inTick)
            {
                setState(globalState);
            }
            current = null;
        }
    }
}
