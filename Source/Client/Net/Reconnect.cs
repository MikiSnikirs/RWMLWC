using System;
using RimWorld;
using RWMLWC.Client.Sim;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Переподключение при обрыве (T-070). Окна подключения запоминают, как подключались (<see cref="Remember"/>).
/// Если сессия оборвалась не по воле игрока, показывается окно «Связь потеряна» с кнопкой «Переподключиться»:
/// то же подключение ещё раз. Мир, ушедший вперёд, клиент догрузит снимком (тик игры не совпадёт с тиком сервера).
/// </summary>
public static class Reconnect
{
    private static Action? _again;
    private static string _target = "";
    private static bool _wasInSession;
    private static bool _userLeft;

    /// <summary>Запомнить способ подключения (вызывают окна подключения перед <c>LockstepClient.Connect</c>).</summary>
    public static void Remember(string target, Action connect)
    {
        _target = target;
        _again = connect;
    }

    /// <summary>Игрок сам отключается или останавливает сессию — окно не показывать.</summary>
    public static void UserLeaving() => _userLeft = true;

    public static string Target => _target;

    private static float _selfTestAt = -1f;
    private static (string host, int port, string thumb, string name)? _selfTestArm;

    private static (string host, int port, string thumb, string name)? SelfTestArm()
    {
        try
        {
            string path = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");
            if (!System.IO.File.Exists(path)) return null;
            var kv = new System.Collections.Generic.Dictionary<string, string>();
            foreach (string l in System.IO.File.ReadAllLines(path))
            {
                int eq = l.IndexOf('=');
                if (eq > 0) kv[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
            }
            if (!kv.TryGetValue("reconnecttest", out string? on) || on != "1") return null;
            string[] hp = kv["server"].Split(':');
            return (hp[0], int.Parse(hp[1]), kv["thumbprint"], kv.TryGetValue("name", out string? n) ? n : "player");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Раз в кадр в загруженной игре.</summary>
    public static void Frame()
    {
        if (_selfTestAt > 0 && Time.realtimeSinceStartup >= _selfTestAt && _selfTestArm is { } a)
        {
            _selfTestAt = -1f;
            Log.Message("[RWMLWC] самопроверка: переподключаюсь к " + a.host + ":" + a.port);
            TestAutomation.Note("reconnect: connecting again to " + a.host + ":" + a.port);
            LockstepClient.Connect(a.host, a.port, a.thumb, a.name);
        }
        bool inSession = LockstepClient.InSession;
        if (inSession)
        {
            _wasInSession = true;
            _userLeft = false;
            return;
        }
        if (!_wasInSession || LockstepClient.CurrentState != LockstepClient.State.Off) return;
        _wasInSession = false;
        if (TestAutomation.AutomationActive && !_userLeft && SelfTestArm() is { } arm)
        {
            // Автопрогон (ключ reconnecttest=1): переподключиться самому через 3 с по адресу из метки.
            Log.Message("[RWMLWC] связь потеряна, самопроверка: переподключение через 3 с");
            _selfTestAt = Time.realtimeSinceStartup + 3f;
            _selfTestArm = arm;
            return;
        }
        if (_userLeft || HostedSession.Running || _again == null || TestAutomation.AutomationActive)
        {
            _userLeft = false;
            return;
        }
        Log.Message("[RWMLWC] связь с сессией потеряна: " + _target);
        if (Find.WindowStack.WindowOfType<Dialog_Reconnect>() == null)
            Find.WindowStack.Add(new Dialog_Reconnect());
    }

    internal static bool TryAgain()
    {
        if (_again == null || LockstepClient.CurrentState != LockstepClient.State.Off) return false;
        Log.Message("[RWMLWC] переподключаюсь: " + _target);
        _again();
        return true;
    }
}

/// <summary>Окно «Связь с сессией потеряна».</summary>
public class Dialog_Reconnect : Window
{
    private bool _started;

    public override Vector2 InitialSize => new Vector2(480f, 220f);

    public Dialog_Reconnect()
    {
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        Text.Font = GameFont.Medium;
        list.Label("Связь с сессией потеряна");
        Text.Font = GameFont.Small;
        LockstepClient.State st = LockstepClient.CurrentState;
        if (!_started)
        {
            list.Label("Игра остановлена у вас; у остальных она идёт дальше. Переподключиться к " + Reconnect.Target + "?");
            if (list.ButtonText("Переподключиться") && Reconnect.TryAgain()) _started = true;
            if (list.ButtonText("Играть одному")) Close();
        }
        else
        {
            list.Label(st switch
            {
                LockstepClient.State.InSession => "Снова в сессии. Окно можно закрыть.",
                LockstepClient.State.Off => LockstepClient.LastReject.Length > 0
                    ? "Отказ: " + LockstepClient.LastReject + "."
                    : "Не получилось. Сервер или друг недоступен — попробуйте ещё раз позже.",
                _ => "Подключаюсь и догружаю мир...",
            });
            if (st == LockstepClient.State.InSession && list.ButtonText("Закрыть")) Close();
            if (st == LockstepClient.State.Off && list.ButtonText("Ещё раз")) _started = Reconnect.TryAgain();
        }
        list.End();
    }
}
