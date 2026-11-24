using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Кадровые хуки сессии — из <c>GameComponentUpdate</c>, а не из отрисовки интерфейса карты.
/// Раньше они звались из postfix <c>MapInterfaceOnGUI_AfterMainTabs</c>: не работали в виде мира
/// и пропадали, когда игра не рисует интерфейс карты (тихое тестовое окно на 1.7.5013; ожидание
/// отрисовки долгим событием). Логика сессии не должна зависеть от того, рисуется ли карта.
/// Визуальные части (баннер стазиса, строка состояния) остались в OnGUI.
/// </summary>
public sealed class SessionFrameHooks : GameComponent
{
    public SessionFrameHooks(Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (!LockstepClient.InSession || Current.ProgramState != ProgramState.Playing || Current.Game == null)
        {
            return;
        }
        RoofCountIsolation.ProbeFrame();
        RWMLWC.Client.SessionView.ChatSelfTest.Frame();
        RWMLWC.Client.Absence.IdleAlarm.Frame();
        RWMLWC.Client.Net.Reconnect.Frame();
        RWMLWC.Client.World.CaravanSelfTest.Frame();
        RWMLWC.Client.World.ColonyChoice.Frame();
        RWMLWC.Client.Input.SharedWindows.Frame();
        RWMLWC.Client.Net.NetworkGameMenu.Frame();
    }
}