using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// То из состояния симуляции, чего игра сама в сейв не пишет: включена ли раздельная симуляция
/// и глобальное состояние Rand симуляции. Без этого снимок (сейв) не продолжался бы одинаково
/// на двух клиентах. Состояния Rand карт сохраняет MapSimState.
/// </summary>
public sealed class SimGlobalState : GameComponent
{
    private bool perMapSimEnabled;
    private ulong globalRandState;

    // Игра создаёт компоненты через конструктор с параметром Game.
    public SimGlobalState(Game game)
    {
    }

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            perMapSimEnabled = PerMapSim.Enabled;
            globalRandState = MapRandContext.GlobalState;
        }
        Scribe_Values.Look(ref perMapSimEnabled, "rwmlwc_perMapSimEnabled", false);
        Scribe_Values.Look(ref globalRandState, "rwmlwc_globalRandState", 0UL);
    }

    public override void LoadedGame()
    {
        // Статический выключатель мог остаться от предыдущей игры в этом же процессе:
        // приводим его к тому, что записано в загруженном сейве.
        PerMapSim.SetEnabled(perMapSimEnabled);
        if (perMapSimEnabled)
        {
            MapRandContext.SetGlobalState(globalRandState);
        }
    }

    public override void StartedNewGame()
    {
        PerMapSim.SetEnabled(false);
    }
}
