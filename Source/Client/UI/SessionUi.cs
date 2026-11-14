using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;
using Message = RWMLWC.Shared.Protocol.Message;

namespace RWMLWC.Client.SessionView;

/// <summary>
/// Чат и состояние подключения (T-025). Получает от <see cref="LockstepClient"/> сообщения, которые
/// не касаются симуляции (<c>Chat</c>, <c>Players</c>), и хранит их для окна и строки состояния.
/// Всё — в главном потоке.
/// </summary>
public static class SessionUi
{
    public const int MaxLines = 200;

    public readonly struct ChatLine
    {
        public ChatLine(DateTime time, int playerId, string text)
        {
            Time = time;
            PlayerId = playerId;
            Text = text;
        }

        public DateTime Time { get; }
        public int PlayerId { get; }
        public string Text { get; }
    }

    private static readonly List<ChatLine> Lines = new();
    private static List<PlayerInfo> _players = new();

    public static IReadOnlyList<ChatLine> ChatLines => Lines;
    public static IReadOnlyList<PlayerInfo> Players => _players;

    /// <summary>Сообщений чата, пришедших, пока окно было закрыто.</summary>
    public static int Unread { get; private set; }

    /// <summary>Сообщение сервера, которое <see cref="LockstepClient"/> не обрабатывает сам.</summary>
    public static void Receive(Message message)
    {
        switch (message)
        {
            case ChatMessage chat:
                Lines.Add(new ChatLine(DateTime.Now, chat.PlayerId, chat.Text));
                if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines);
                if (Find.WindowStack?.WindowOfType<Dialog_Session>() == null)
                {
                    Unread++;
                    if (chat.PlayerId != LockstepClient.PlayerId && Current.ProgramState == ProgramState.Playing)
                        Messages.Message($"{NameOf(chat.PlayerId)}: {chat.Text}", MessageTypeDefOf.SilentInput, false);
                }
                break;
            case PlayersMessage players:
                _players = players.Players.ToList();
                break;
        }
    }

    public static string NameOf(int playerId)
    {
        foreach (PlayerInfo p in _players)
            if (p.PlayerId == playerId) return p.Name;
        return "игрок " + playerId;
    }

    /// <summary>Отправить сообщение в чат. false — нет сессии или пустой текст.</summary>
    public static bool SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        if (text.Length > ProtocolLimits.MaxChatChars) text = text.Substring(0, ProtocolLimits.MaxChatChars);
        return LockstepClient.TrySend(new ChatMessage { Text = text });
    }

    internal static void MarkRead() => Unread = 0;

    /// <summary>Короткая строка состояния для игрока.</summary>
    public static string StatusLine()
    {
        switch (LockstepClient.CurrentState)
        {
            case LockstepClient.State.Connecting:
                return "RWMLWC: подключение...";
            case LockstepClient.State.WaitingWelcome:
                return "RWMLWC: ожидание сервера...";
            case LockstepClient.State.InSession:
                int ping = LockstepClient.RoundTripMs;
                int online = _players.Count(p => p.Online);
                string pingText = ping >= 0 ? ping + " мс" : "—";
                return $"RWMLWC: игрок {LockstepClient.PlayerId}, в сети {online}, пинг {pingText}";
            default:
                return "";
        }
    }
}

/// <summary>Окно сессии: игроки с пингом, чат и поле ввода.</summary>
public class Dialog_Session : Window
{
    private string _input = "";
    private Vector2 _scroll;
    private int _seenLines = -1;
    private const string InputControl = "RWMLWC_ChatInput";

    public override Vector2 InitialSize => new Vector2(560f, 520f);

    public Dialog_Session()
    {
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
        draggable = true;
        resizeable = true;
        forcePause = false; // окно сессии не останавливает общую игру
        SessionUi.MarkRead();
    }

    public override void DoWindowContents(Rect inRect)
    {
        SessionUi.MarkRead();
        Text.Font = GameFont.Small;
        float y = inRect.y;

        Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), SessionUi.StatusLine());
        y += 26f;

        // Игроки.
        foreach (PlayerInfo p in SessionUi.Players)
        {
            string ping = !p.Online ? "не в сети" : p.PingMs >= 0 ? p.PingMs + " мс" : "—";
            string me = p.PlayerId == LockstepClient.PlayerId ? " (вы)" : "";
            GUI.color = p.Online ? Color.white : Color.gray;
            Widgets.Label(new Rect(inRect.x + 8f, y, inRect.width - 8f, 22f), $"{p.PlayerId}. {p.Name}{me} — {ping}");
            GUI.color = Color.white;
            y += 22f;
        }
        y += 4f;
        Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
        y += 6f;

        // Чат.
        float inputH = 30f;
        Rect logRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - inputH - 6f);
        IReadOnlyList<SessionUi.ChatLine> lines = SessionUi.ChatLines;
        float width = logRect.width - 16f;
        float total = 0f;
        var texts = new List<(string text, float h)>(lines.Count);
        foreach (SessionUi.ChatLine l in lines)
        {
            string t = $"{l.Time:HH:mm} {SessionUi.NameOf(l.PlayerId)}: {l.Text}";
            float h = Text.CalcHeight(t, width);
            texts.Add((t, h));
            total += h;
        }
        Rect view = new Rect(0f, 0f, width, Mathf.Max(total, logRect.height));
        if (lines.Count != _seenLines)
        {
            _seenLines = lines.Count;
            _scroll.y = Mathf.Max(0f, total - logRect.height); // новые сообщения — вниз
        }
        Widgets.BeginScrollView(logRect, ref _scroll, view);
        float ly = 0f;
        foreach (var (text, h) in texts)
        {
            Widgets.Label(new Rect(0f, ly, width, h), text);
            ly += h;
        }
        Widgets.EndScrollView();

        // Ввод.
        Rect inputRect = new Rect(inRect.x, inRect.yMax - inputH, inRect.width - 110f, inputH);
        Rect sendRect = new Rect(inRect.xMax - 100f, inRect.yMax - inputH, 100f, inputH);
        GUI.SetNextControlName(InputControl);
        _input = Widgets.TextField(inputRect, _input, ProtocolLimits.MaxChatChars);
        if (Widgets.ButtonText(sendRect, "Отправить", active: LockstepClient.InSession))
        {
            Send();
        }
    }

    private void Send()
    {
        if (SessionUi.SendChat(_input)) _input = "";
    }

    // Enter окно игры по умолчанию считает «принять и закрыть»; здесь он отправляет сообщение.
    public override void OnAcceptKeyPressed()
    {
        Send();
    }
}

// Кнопка окна сессии в ряду переключателей справа внизу (рядом с кнопкой карт A), только в сетевой игре.
[HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
internal static class Patch_PlaySettings_SessionButton
{
    private static void Postfix(WidgetRow row, bool worldView)
    {
        if (row == null || LockstepClient.CurrentState == LockstepClient.State.Off) return;
        string tip = "RWMLWC: игроки и чат";
        if (SessionUi.Unread > 0) tip += $" (новых сообщений: {SessionUi.Unread})";
        if (row.ButtonIcon(TexButton.ToggleLog, tip))
        {
            Dialog_Session? open = Find.WindowStack.WindowOfType<Dialog_Session>();
            if (open != null) open.Close();
            else Find.WindowStack.Add(new Dialog_Session());
        }
    }
}

// Строка состояния подключения над правым нижним углом.
[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))]
internal static class Patch_MapInterface_SessionStatus
{
    private static void Postfix()
    {
        ChatSelfTest.Frame();
        string line = SessionUi.StatusLine();
        if (line.Length == 0) return;
        if (SessionUi.Unread > 0) line += $" · чат: {SessionUi.Unread}";
        Text.Font = GameFont.Tiny;
        Vector2 size = Text.CalcSize(line);
        // Левее кнопок скорости (они занимают правые ~140 пикселей над рядом значков).
        Rect r = new Rect(UI.screenWidth - 150f - size.x - 8f, UI.screenHeight - 165f, size.x + 8f, size.y + 2f);
        Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.45f));
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(r, line);
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
    }
}

/// <summary>
/// Самопроверка чата для автопрогонов (TST-025): строка <c>chattest=1</c> в метке автопрогона.
/// Через 5 с сессии отправляет два сообщения, раз в секунду пишет в <c>RWMLWC/chat-test-log.txt</c>
/// своё состояние, пинг и полученный чат.
/// </summary>
internal static class ChatSelfTest
{
    private static bool? _enabled;
    private static readonly System.Diagnostics.Stopwatch Clock = new();
    private static int _sent;
    private static int _loggedLines;
    private static long _nextLogMs;

    private static string Dir => System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");

    public static void Frame()
    {
        _enabled ??= ReadEnabled();
        if (_enabled != true || !LockstepClient.InSession) return;
        if (!Clock.IsRunning) Clock.Start();
        long ms = Clock.ElapsedMilliseconds;
        if (_sent == 0 && ms > 5000) { _sent++; Write("send: " + SessionUi.SendChat($"привет от игрока {LockstepClient.PlayerId}")); }
        if (_sent == 1 && ms > 5300) { _sent++; Write("send empty: " + SessionUi.SendChat("   ")); }
        if (ms < _nextLogMs) return;
        _nextLogMs = ms + 1000;
        Write(SessionUi.StatusLine() + " | players: " + string.Join(", ", SessionUi.Players.Select(p => $"{p.PlayerId}:{p.Name}:{(p.Online ? "on" : "off")}:{p.PingMs}")));
        var lines = SessionUi.ChatLines;
        for (; _loggedLines < lines.Count; _loggedLines++)
            Write($"chat: {lines[_loggedLines].PlayerId} {SessionUi.NameOf(lines[_loggedLines].PlayerId)}: {lines[_loggedLines].Text}");
    }

    private static bool ReadEnabled()
    {
        try
        {
            string arm = System.IO.Path.Combine(Dir, "arm-determinism-run.txt");
            return System.IO.File.Exists(arm) && System.IO.File.ReadAllLines(arm).Any(l => l.Trim() == "chattest=1");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Write(string line)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(Dir, "chat-test-log.txt"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
