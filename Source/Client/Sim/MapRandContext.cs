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

    /// <summary>Глобальное состояние, пока активен контекст карты.</summary>
    public static ulong SavedGlobalState => globalState;

    /// <summary>Сделать активным состояние указанной карты (null — глобальное).</summary>
    public static void Enter(Map? map)
    {
        if (ReferenceEquals(map, current))
        {
            return;
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
            setState(globalState);
            current = null;
        }
    }
}
