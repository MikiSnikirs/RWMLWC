using System;
using System.Threading;
using System.Threading.Tasks;
using LudeonTK;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Net;
using Steamworks;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Вход друга в сессию хоста через Steam (T-019, часть 2): подключение по SteamID64 хоста
/// с ожиданием сети релеев и повторными попытками, затем обычный <see cref="LockstepClient"/>.
/// </summary>
public static class SteamJoin
{
    private const int Attempts = 5;
    private static readonly object StatusGate = new();
    private static string _status = "";

    /// <summary>Последнее состояние подключения — для окна.</summary>
    public static string Status
    {
        get { lock (StatusGate) return _status; }
    }

    /// <summary>Разобрать SteamID64 (17 цифр, начинается с 7656…). Ссылку на профиль тоже принимает.</summary>
    public static bool TryParseSteamId(string text, out ulong id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimEnd('/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s.Substring(slash + 1);
        return ulong.TryParse(s, out id) && (id >> 56) == 1; // вселенная Public, личный аккаунт
    }

    /// <summary>Начать вход в сессию хоста. Вызывать из главного потока, в загруженной игре.</summary>
    public static void Start(ulong hostSteamId)
    {
        string name = SteamNet.Available ? SteamFriends.GetPersonaName() : "player";
        LockstepClient.Connect(ConnectAsync(hostSteamId), "steam:" + hostSteamId, name);
    }

    private static async Task<IConnection> ConnectAsync(ulong host)
    {
        SetStatus("жду сеть релеев Steam...");
        string relay = await SteamNet.WaitForRelayAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Log.Message("[RWMLWC][join] " + relay);
        for (int attempt = 1; ; attempt++)
        {
            SetStatus($"подключаюсь к хосту, попытка {attempt} из {Attempts}...");
            try
            {
                SteamConnection c = await SteamNet.ConnectAsync(host, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                SetStatus("соединение установлено, жду ответа сессии...");
                Log.Message("[RWMLWC][join] соединение с хостом установлено, попытка " + attempt);
                return c;
            }
            catch (Exception e) when (attempt < Attempts)
            {
                Log.Message($"[RWMLWC][join] попытка {attempt} не удалась: {e.Message}");
                await Task.Delay(3000).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                SetStatus("не удалось подключиться: " + e.Message);
                throw;
            }
        }
    }

    private static void SetStatus(string s)
    {
        lock (StatusGate) _status = s;
    }

    internal static void ClearStatus() => SetStatus("");
}

/// <summary>
/// Окно «Подключиться по SteamID». Из главного меню мир приходит от хоста снимком (T-045);
/// из загруженной игры — у обоих должен быть тот же сейв.
/// </summary>
public class Dialog_SteamJoin : Window
{
    private string _input = "";
    private bool _started;

    public override Vector2 InitialSize => new Vector2(520f, 270f);
    private string _password = "";

    public Dialog_SteamJoin(ulong hostSteamId = 0, bool autoStart = false)
    {
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
        SteamJoin.ClearStatus();
        if (hostSteamId != 0) _input = hostSteamId.ToString();
        if (autoStart && hostSteamId != 0 && SteamNet.Available && LockstepClient.CurrentState == LockstepClient.State.Off)
        {
            _started = true;
            SteamJoin.Start(hostSteamId);
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        Text.Font = GameFont.Medium;
        list.Label("Подключиться к другу через Steam");
        Text.Font = GameFont.Small;
        list.Label(Current.ProgramState == ProgramState.Entry
            ? "SteamID64 хоста (17 цифр) или ссылка на его профиль. Мир загрузится от хоста."
            : "SteamID64 хоста (17 цифр) или ссылка на его профиль. Из загруженной игры у обоих должен быть один и тот же сейв; проще подключаться из главного меню.");
        _input = list.TextEntry(_input);
        list.Label("Пароль (если хост его задал):");
        _password = list.TextEntry(_password);

        bool valid = SteamJoin.TryParseSteamId(_input, out ulong id);
        LockstepClient.State st = LockstepClient.CurrentState;
        if (!SteamNet.Available)
        {
            list.Label("Steam недоступен: игра запущена не через Steam.");
        }
        else if (!_started)
        {
            if (!valid && _input.Length > 0) list.Label("Это не похоже на SteamID64.");
            if (list.ButtonText("Подключиться") && valid && st == LockstepClient.State.Off)
            {
                _started = true;
                string password = _password;
                ulong host = id;
                void Go()
                {
                    LockstepClient.ConnectPassword = password;
                    SteamJoin.Start(host);
                }
                Reconnect.Remember("другу (Steam)", Go);
                Go();
            }
        }
        else
        {
            string line = st switch
            {
                LockstepClient.State.InSession => "В сессии, игрок " + LockstepClient.PlayerId + ". Окно можно закрыть.",
                LockstepClient.State.Off => "Подключение не состоялось. " + SteamJoin.Status + " Подробности — в логе игры (строки RWMLWC).",
                _ => SteamJoin.Status,
            };
            list.Label(line);
            if (st == LockstepClient.State.Off && list.ButtonText("Ещё раз")) _started = false;
        }
        list.End();
    }
}

internal static class SteamJoinDebug
{
    [DebugAction("RWMLWC", "Join session by SteamID", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    private static void Join()
    {
        if (LockstepClient.CurrentState != LockstepClient.State.Off)
        {
            Messages.Message("RWMLWC: уже в сессии или подключаюсь", MessageTypeDefOf.RejectInput, false);
            return;
        }
        Find.WindowStack.Add(new Dialog_SteamJoin());
    }

    [DebugAction("RWMLWC", "Copy my SteamID", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    private static void CopyId()
    {
        if (!SteamNet.Available)
        {
            Messages.Message("RWMLWC: Steam недоступен", MessageTypeDefOf.RejectInput, false);
            return;
        }
        GUIUtility.systemCopyBuffer = SteamNet.MySteamId.ToString();
        Messages.Message("RWMLWC: SteamID скопирован в буфер обмена", MessageTypeDefOf.NeutralEvent, false);
    }
}
