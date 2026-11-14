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
            case AlarmMessage alarm:
                RWMLWC.Client.Absence.IdleAlarm.OnAlarm(alarm);
                break;
            case CursorMessage cursor:
                if (cursor.MapId == CursorMessage.NoCursor) Cursors.Remove(cursor.PlayerId);
                else Cursors[cursor.PlayerId] = new RemoteCursor(cursor.MapId, cursor.X, cursor.Z, Time.realtimeSinceStartup);
                break;
        }
    }

    /// <summary>Курсор другого игрока (v5, T-064): карта, точка в клетках, когда получен (реальное время).</summary>
    public readonly record struct RemoteCursor(int MapId, float X, float Z, float ReceivedAt);

    /// <summary>Последние курсоры других игроков по номеру игрока. Рисует их T-050 (агент A).</summary>
    public static readonly Dictionary<int, RemoteCursor> Cursors = new();

    /// <summary>Цвет игрока 0xRRGGBB или PlayerColors.None (0), если не выбран.</summary>
    public static int ColorOf(int playerId)
    {
        foreach (PlayerInfo p in _players)
            if (p.PlayerId == playerId) return p.Color;
        return PlayerColors.None;
    }

    public static Color ToUnityColor(int rgb) =>
        rgb == PlayerColors.None ? Color.gray : new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    private static float _lastCursorSent = -100f;
    private static (int map, float x, float z) _lastCursor = (int.MinValue, 0, 0);

    /// <summary>
    /// Отправить свой курсор (для T-050): не чаще 10 раз в секунду; при сдвиге больше 0,1 клетки или раз в 2 с.
    /// mapId = <see cref="CursorMessage.NoCursor"/> — курсора нет (смотрю не на карту).
    /// </summary>
    public static void SendCursor(int mapId, float x, float z)
    {
        float now = Time.realtimeSinceStartup;
        if (now - _lastCursorSent < 1f / ProtocolLimits.MaxCursorsPerSecond) return;
        bool moved = _lastCursor.map != mapId || Mathf.Abs(_lastCursor.x - x) > 0.1f || Mathf.Abs(_lastCursor.z - z) > 0.1f;
        if (!moved && now - _lastCursorSent < 2f) return;
        if (LockstepClient.TrySend(new CursorMessage { MapId = mapId, X = x, Z = z }))
        {
            _lastCursorSent = now;
            _lastCursor = (mapId, x, z);
        }
    }

    public static void ChooseColor(int rgb) => LockstepClient.TrySend(new ChooseColorMessage { Color = rgb });

    public static string NameOf(int playerId)
    {
        if (playerId == 0) return "Сервер";
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

        // У хоста — как друзьям подключиться.
        if (RWMLWC.Client.Net.HostedSession.Running)
        {
            if (RWMLWC.Client.Net.SteamNet.Available)
                y = CopyRow(inRect, y, "SteamID: " + RWMLWC.Client.Net.SteamNet.MySteamId, RWMLWC.Client.Net.SteamNet.MySteamId.ToString());
            if (RWMLWC.Client.Net.HostedSession.DirectCode.Length > 0)
                y = CopyRow(inRect, y, "Код: " + RWMLWC.Client.Net.HostedSession.DirectCode, RWMLWC.Client.Net.HostedSession.DirectCode);
        }

        // Игроки.
        foreach (PlayerInfo p in SessionUi.Players)
        {
            string ping = !p.Online ? "не в сети" : p.PingMs >= 0 ? p.PingMs + " мс" : "—";
            string me = p.PlayerId == LockstepClient.PlayerId ? " (вы)" : "";
            Widgets.DrawBoxSolid(new Rect(inRect.x + 8f, y + 5f, 12f, 12f), SessionUi.ToUnityColor(p.Color));
            GUI.color = p.Online ? Color.white : Color.gray;
            Widgets.Label(new Rect(inRect.x + 26f, y, inRect.width - 130f, 22f), $"{p.PlayerId}. {p.Name}{me} — {ping}");
            GUI.color = Color.white;
            if (p.PlayerId == LockstepClient.PlayerId && Widgets.ButtonText(new Rect(inRect.xMax - 100f, y, 100f, 22f), "Цвет"))
                Find.WindowStack.Add(new Dialog_ChooseColor());
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

    private static float CopyRow(Rect inRect, float y, string label, string value)
    {
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inRect.x + 8f, y + 3f, inRect.width - 110f, 22f), label);
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inRect.xMax - 100f, y, 100f, 24f), "Копировать"))
        {
            GUIUtility.systemCopyBuffer = value;
            Messages.Message("RWMLWC: скопировано", MessageTypeDefOf.SilentInput, false);
        }
        return y + 26f;
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
        RWMLWC.Client.Absence.IdleAlarm.Frame();
        RWMLWC.Client.Net.NetworkGameMenu.Frame();
        StatusLineView.OnGUI();
    }
}

/// <summary>
/// Строка состояния сессии (T-063): по умолчанию сверху справа, левее подсказок обучения
/// (<c>LearningReadout</c>: ширина 200, отступ 8 от правого края). Перетаскивается мышью; место — отступы
/// от правого и верхнего края — хранится в <c>RWMLWC/ui.txt</c> (личная настройка, не сейв).
/// </summary>
internal static class StatusLineView
{
    private const float DefaultRight = 8f + 200f + 8f; // правее — подсказки обучения
    private const float DefaultTop = 8f;

    private static float? _right;
    private static float? _top;
    private static bool _dragging;
    private static Vector2 _grab;

    private static string PathOnDisk => System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "ui.txt");

    private static bool _colorAsked;

    internal static void OnGUI()
    {
        // Первый вход в сессию: цвет ещё не выбран — предложить выбрать (T-064).
        if (!LockstepClient.InSession) _colorAsked = false;
        else if (!_colorAsked && !TestAutomation.AutomationActive && SessionUi.Players.Any(p => p.PlayerId == LockstepClient.PlayerId) && SessionUi.ColorOf(LockstepClient.PlayerId) == PlayerColors.None)
        {
            _colorAsked = true;
            Find.WindowStack.Add(new Dialog_ChooseColor());
        }
        string line = SessionUi.StatusLine();
        if (line.Length == 0) return;
        if (SessionUi.Unread > 0) line += $" · чат: {SessionUi.Unread}";
        line += RWMLWC.Client.Absence.IdleAlarm.StatusSuffix();
        Load();
        Text.Font = GameFont.Tiny;
        Vector2 size = Text.CalcSize(line);
        float w = size.x + 12f, h = size.y + 4f;
        float x = Mathf.Clamp(UI.screenWidth - _right!.Value - w, 0f, UI.screenWidth - w);
        float y = Mathf.Clamp(_top!.Value, 0f, UI.screenHeight - h);
        var r = new Rect(x, y, w, h);

        Event e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && Mouse.IsOver(r))
        {
            _dragging = true;
            _grab = e.mousePosition - r.position;
            e.Use();
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            Vector2 pos = e.mousePosition - _grab;
            _right = Mathf.Clamp(UI.screenWidth - pos.x - w, 0f, UI.screenWidth - w);
            _top = Mathf.Clamp(pos.y, 0f, UI.screenHeight - h);
            e.Use();
        }
        else if (_dragging && e.rawType == EventType.MouseUp)
        {
            _dragging = false;
            Save();
            e.Use();
        }

        Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, _dragging ? 0.7f : 0.45f));
        if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(r, line);
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
        TooltipHandler.TipRegion(r, "Сетевая игра RWMLWC. Перетащите мышью, чтобы переместить. Игроки и чат — кнопка справа внизу.");
    }

    private static void Load()
    {
        if (_right.HasValue) return;
        _right = DefaultRight;
        _top = DefaultTop;
        try
        {
            if (!System.IO.File.Exists(PathOnDisk)) return;
            RWMLWC.Server.SettingsFile f = RWMLWC.Server.SettingsFile.LoadOrCreate(PathOnDisk, "");
            _right = (float)f.GetDouble("status_right", DefaultRight, 0, 100000);
            _top = (float)f.GetDouble("status_top", DefaultTop, 0, 100000);
        }
        catch (Exception)
        {
        }
    }

    private static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
            System.IO.File.WriteAllLines(PathOnDisk, new[]
            {
                "# Положение строки состояния RWMLWC: отступ от правого и верхнего края экрана, пиксели интерфейса.",
                "# Удалите файл, чтобы вернуть место по умолчанию.",
                "status_right = " + _right!.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                "status_top = " + _top!.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        catch (Exception)
        {
        }
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
        if (_sent == 2 && ms > 5600)
        {
            _sent++;
            // Оба выбирают один и тот же цвет: второй получит отказ и возьмёт следующий.
            SessionUi.ChooseColor(RWMLWC.Client.Maps.Squads.Palette[0]);
            Write("color: asked " + RWMLWC.Client.Maps.Squads.Palette[0].ToString("X6"));
        }
        if (_sent == 3 && ms > 7000 && SessionUi.ColorOf(LockstepClient.PlayerId) == PlayerColors.None)
        {
            _sent++;
            SessionUi.ChooseColor(RWMLWC.Client.Maps.Squads.Palette[1]);
            Write("color: first was taken, asked " + RWMLWC.Client.Maps.Squads.Palette[1].ToString("X6"));
        }
        if (ms > 6000) SessionUi.SendCursor(0, 10f + (ms / 100 % 50), 20f + LockstepClient.PlayerId);
        if (ms < _nextLogMs) return;
        _nextLogMs = ms + 1000;
        Write(SessionUi.StatusLine() + " | players: " + string.Join(", ", SessionUi.Players.Select(p => $"{p.PlayerId}:{p.Name}:{(p.Online ? "on" : "off")}:{p.PingMs}:{p.Color:X6}"))
            + " | cursors: " + string.Join(", ", SessionUi.Cursors.Select(c => $"{c.Key}@{c.Value.MapId}:{c.Value.X:0.#},{c.Value.Z:0.#}")));
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

/// <summary>Окно выбора цвета игрока (T-064): палитра, занятые цвета недоступны.</summary>
public class Dialog_ChooseColor : Window
{
    public override Vector2 InitialSize => new Vector2(420f, 230f);

    public Dialog_ChooseColor()
    {
        doCloseX = true;
        closeOnClickedOutside = true;
        absorbInputAroundWindow = false;
        forcePause = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "Ваш цвет");
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 24f), "Им будут видны ваш курсор и имя у других игроков.");
        int mine = SessionUi.ColorOf(LockstepClient.PlayerId);
        const float size = 44f, gap = 8f;
        float x = inRect.x, y = inRect.y + 66f;
        int perRow = Mathf.Max(1, (int)((inRect.width + gap) / (size + gap)));
        int[] palette = RWMLWC.Client.Maps.Squads.Palette; // та же палитра, что у отрядов
        for (int i = 0; i < palette.Length; i++)
        {
            int rgb = palette[i];
            var r = new Rect(x + (i % perRow) * (size + gap), y + (i / perRow) * (size + gap), size, size);
            PlayerInfo? owner = SessionUi.Players.Where(p => p.Color == rgb && p.PlayerId != LockstepClient.PlayerId).Select(p => (PlayerInfo?)p).FirstOrDefault();
            Widgets.DrawBoxSolid(r, SessionUi.ToUnityColor(rgb));
            if (rgb == mine) Widgets.DrawBox(r, 3);
            if (owner != null)
            {
                Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.6f));
                TooltipHandler.TipRegion(r, "Занят: " + owner.Value.Name);
                continue;
            }
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            if (Widgets.ButtonInvisible(r))
            {
                SessionUi.ChooseColor(rgb);
                Close();
            }
        }
    }
}
