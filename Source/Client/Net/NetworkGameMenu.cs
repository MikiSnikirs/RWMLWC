using System;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Net;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Окно «Сетевая игра» главного меню (T-058): создать игру из сейва, подключиться к другу по Steam,
/// подключиться к выделенному серверу по коду.
/// </summary>
public class Dialog_NetworkGame : Window
{
    public override Vector2 InitialSize => new Vector2(460f, 290f);

    public Dialog_NetworkGame()
    {
        doCloseX = true;
        closeOnClickedOutside = true;
        absorbInputAroundWindow = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        Text.Font = GameFont.Medium;
        list.Label("Сетевая игра");
        Text.Font = GameFont.Small;
        list.Gap(6f);

        if (Option(list, "Создать игру", "Загрузить сейв и открыть его для друзей. Вы — хост: игра идёт, пока вы в ней."))
        {
            Close();
            Find.WindowStack.Add(new Dialog_HostSaveList());
        }
        bool steam = SteamNet.Available;
        if (Option(list, "Подключиться к другу (Steam)", steam ? "К игре друга по его SteamID или через «Присоединиться» в списке друзей Steam." : "Steam недоступен: игра запущена не через Steam.", steam))
        {
            Close();
            Find.WindowStack.Add(new Dialog_SteamJoin());
        }
        if (Option(list, "Подключиться к серверу", "К выделенному серверу по коду подключения, который показывает его консоль."))
        {
            Close();
            Find.WindowStack.Add(new Dialog_ServerJoin());
        }
        list.End();
    }

    private static bool Option(Listing_Standard list, string label, string description, bool active = true)
    {
        Rect r = list.GetRect(32f);
        bool clicked = Widgets.ButtonText(r, label, active: active);
        GUI.color = Color.gray;
        Text.Font = GameFont.Tiny;
        list.Label(description);
        Text.Font = GameFont.Small;
        GUI.color = Color.white;
        list.Gap(4f);
        return clicked && active;
    }
}

/// <summary>Выбор сейва для «Создать игру»: тот же список, что «Загрузить игру», но после загрузки открывается сессия.</summary>
public class Dialog_HostSaveList : Dialog_SaveFileList_Load
{
    public Dialog_HostSaveList()
    {
        interactButLabel = "Создать игру";
    }

    protected override void DoFileInteraction(string saveFileName)
    {
        NetworkGameMenu.HostAfterLoad = true;
        base.DoFileInteraction(saveFileName);
    }
}

/// <summary>Окно «Подключиться к серверу»: код подключения и имя игрока.</summary>
public class Dialog_ServerJoin : Window
{
    private string _code = "";
    private string _name;
    private bool _started;

    public override Vector2 InitialSize => new Vector2(560f, 310f);
    private string _password = "";

    public Dialog_ServerJoin()
    {
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        _name = SteamNet.Available ? Steamworks.SteamFriends.GetPersonaName() : "Игрок";
        try
        {
            string clip = GUIUtility.systemCopyBuffer;
            if (ConnectionCode.TryParse(clip, out _, out _, out _)) _code = clip.Trim(); // код уже скопирован — подставим
        }
        catch (Exception)
        {
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        Text.Font = GameFont.Medium;
        list.Label("Подключиться к серверу");
        Text.Font = GameFont.Small;
        list.Label("Код подключения (rwmlwc://адрес:порт/...) — его показывает консоль сервера:");
        _code = list.TextEntry(_code);
        list.Label("Ваше имя в игре:");
        _name = list.TextEntry(_name);
        list.Label("Пароль (если сервер его требует):");
        _password = list.TextEntry(_password);

        bool valid = ConnectionCode.TryParse(_code, out string host, out int port, out string thumb);
        LockstepClient.State st = LockstepClient.CurrentState;
        if (!_started)
        {
            if (!valid && _code.Length > 0) list.Label("Это не похоже на код подключения.");
            bool ready = valid && _name.Trim().Length > 0;
            if (Widgets.ButtonText(list.GetRect(32f), "Подключиться", active: ready) && ready && st == LockstepClient.State.Off)
            {
                _started = true;
                LockstepClient.ConnectPassword = _password;
                LockstepClient.Connect(host, port, thumb, _name.Trim());
            }
        }
        else
        {
            list.Label(st switch
            {
                LockstepClient.State.InSession => "В сессии, игрок " + LockstepClient.PlayerId + ". Окно можно закрыть.",
                LockstepClient.State.Off => "Подключение не состоялось: сервер недоступен или отказал (пароль, имя, версия). Подробности — в логе игры (строки RWMLWC).",
                LockstepClient.State.Connecting => "Подключаюсь...",
                _ => "Сервер ответил, загружаю мир...",
            });
            if (st == LockstepClient.State.Off && list.ButtonText("Ещё раз")) _started = false;
        }
        list.End();
    }
}

internal static class NetworkGameMenu
{
    /// <summary>Сейв выбран через «Создать игру»: когда он загрузится, открыть сессию.</summary>
    internal static bool HostAfterLoad;

    /// <summary>Раз в кадр в загруженной игре.</summary>
    internal static void Frame()
    {
        if (!HostAfterLoad || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
        HostAfterLoad = false;
        HostedSession.HostAndJoin();
    }
}
