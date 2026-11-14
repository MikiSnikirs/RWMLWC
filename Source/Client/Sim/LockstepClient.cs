using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Net;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;
using Verse;
using Message = RWMLWC.Shared.Protocol.Message;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Клиент сессии (T-014): тики идут только до номера, разрешённого сервером.
///
/// Пока клиент подключается или находится в сессии, штатный цикл игры (TickManager.TickManagerUpdate)
/// отключён: раз в кадр читаем сообщения сервера и догоняем его разрешение TickUpTo.
/// Скорость и пауза — у сервера: нажатия игрока превращаются в SpeedRequest, а действует то,
/// что пришло в сообщении Tick (правило меньшей скорости считает сервер).
///
/// Команды игроков (T-020): SendCommand отправляет действие серверу; пришедшие в общем порядке
/// команды (CommandOrdered) исполняются между тиками — команда с Tick = T после тика T и до тика T+1.
///
/// Нет: переподключения, догона (T-045),
/// реакции на рассинхронизацию (T-034).
/// </summary>
public static class LockstepClient
{
    public enum State
    {
        Off,
        Connecting,
        WaitingWelcome,
        InSession,
    }

    /// <summary>Не дольше стольких миллисекунд за кадр тратим на догон тиков.</summary>
    private const int FrameBudgetMs = 25;

    /// <summary>Раз в столько тиков отправляем серверу хеши карт.</summary>
    public const int HashInterval = 60;

    private static State state = State.Off;
    private static Task<IConnection>? connecting;
    private static IConnection? connection;
    private static string playerName = "";
    private static int tickUpTo;
    private static byte serverSpeed;
    private static int sentSpeed = -1;
    private static bool applyingServerSpeed;
    private static int nextClientSeq = 1;
    private static readonly Queue<CommandOrderedMessage> pendingCommands = new Queue<CommandOrderedMessage>();

    /// <summary>Сколько команд этот клиент отправил серверу.</summary>
    public static int CommandsSent => nextClientSeq - 1;
    private static readonly Stopwatch frameClock = new Stopwatch();
    private static readonly Stopwatch holdClock = new Stopwatch();
    private static int heldSpeed = -1;

    /// <summary>
    /// Для тестов: столько секунд после Welcome клиент не просит снять паузу. Сервер держит паузу,
    /// пока новый игрок не прислал SpeedRequest, поэтому за это время успевает войти второй клиент
    /// (вход в уже идущую сессию — T-045).
    /// </summary>
    public static int HoldSeconds;

    private static bool Holding => HoldSeconds > 0 && holdClock.IsRunning && holdClock.ElapsedMilliseconds < HoldSeconds * 1000L;

    private static readonly AccessTools.FieldRef<TickManager, int> ticksThisFrameRef =
        AccessTools.FieldRefAccess<TickManager, int>("ticksThisFrame");

    private static readonly AccessTools.FieldRef<TickManager, TimeSpeed> curTimeSpeedRef =
        AccessTools.FieldRefAccess<TickManager, TimeSpeed>("curTimeSpeed");

    public static State CurrentState => state;

    /// <summary>Клиент владеет тиками: штатный цикл игры отключён.</summary>
    public static bool OwnsTicking => state != State.Off;

    /// <summary>Идёт сессия: сервер прислал Welcome.</summary>
    public static bool InSession => state == State.InSession;

    public static int PlayerId { get; private set; }

    public static int TickUpTo => tickUpTo;

    /// <summary>Начать подключение. Игра перестаёт тикать сама до конца сессии.</summary>
    public static void Connect(string host, int port, string certThumbprintSha256, string name)
    {
        if (state != State.Off)
        {
            TestAutomation.Note("lockstep: Connect ignored, state=" + state);
            return;
        }
        Connect(QuicTransport.ConnectAsync(host, port, certThumbprintSha256), $"{host}:{port}", name);
    }

    /// <summary>
    /// Начать сессию через готовое (или ещё устанавливаемое) соединение любого транспорта:
    /// QUIC, Steam (SteamNet.ConnectAsync), сессия в игре хоста. description — только для журнала.
    /// </summary>
    public static void Connect(Task<IConnection> connectingTask, string description, string name)
    {
        if (state != State.Off)
        {
            TestAutomation.Note("lockstep: Connect ignored, state=" + state);
            return;
        }
        playerName = name;
        sentSpeed = -1;
        heldSpeed = -1;
        holdClock.Reset();
        nextClientSeq = 1;
        pendingCommands.Clear();
        CommandBus.Reset();
        connecting = connectingTask;
        state = State.Connecting;
        TestAutomation.Note($"lockstep: connecting to {description} as '{name}'");
    }

    public static void Disconnect(string reason)
    {
        if (state == State.Off)
        {
            return;
        }
        TestAutomation.Note("lockstep: disconnect: " + reason);
        try
        {
            connection?.Close(reason);
        }
        catch (Exception)
        {
        }
        connection = null;
        connecting = null;
        state = State.Off;
    }

    /// <summary>
    /// Раз в кадр, вместо штатного TickManagerUpdate. Возвращает true, если тиками управляет клиент.
    /// </summary>
    public static bool OnFrame(TickManager tickManager)
    {
        if (state == State.Off)
        {
            return false;
        }
        ticksThisFrameRef(tickManager) = 0;

        if (state == State.Connecting)
        {
            Task<IConnection>? task = connecting;
            if (task == null || !task.IsCompleted)
            {
                return true;
            }
            connecting = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                TestAutomation.Note("lockstep: connect FAILED: " + (task.Exception?.GetBaseException().Message ?? "cancelled"));
                state = State.Off;
                return false;
            }
            connection = task.Result;
            connection.Send(new HelloMessage
            {
                GameVersion = VersionControl.CurrentVersionStringWithRev,
                PlayerName = playerName,
                ModListHash = ModListHash(),
                // Версия локальной копии сейва (контракт v2): мир и тик, на котором стоит игра.
                SaveWorldId = Current.Game != null ? unchecked((uint)Find.World.info.persistentRandomValue) : 0UL,
                SaveTick = Current.Game != null ? tickManager.TicksGame : 0,
            });
            state = State.WaitingWelcome;
            TestAutomation.Note("lockstep: connected to " + connection.Remote + ", Hello sent");
            return true;
        }

        IConnection? conn = connection;
        if (conn == null)
        {
            state = State.Off;
            return false;
        }

        while (conn.TryReceive(out Message message))
        {
            Handle(tickManager, message);
            if (state == State.Off)
            {
                return false;
            }
        }

        if (!conn.IsOpen)
        {
            TestAutomation.Note("lockstep: connection closed: " + conn.CloseReason);
            connection = null;
            state = State.Off;
            return false;
        }

        if (state == State.InSession)
        {
            TestAutomation.SessionFrame();
            if (heldSpeed >= 0 && !Holding)
            {
                int speed = heldSpeed;
                heldSpeed = -1;
                TestAutomation.Note("lockstep: hold is over, requesting speed " + speed);
                RequestSpeed(speed);
            }
            RunTicks(tickManager);
        }
        return true;
    }

    private static void Handle(TickManager tickManager, Message message)
    {
        switch (message)
        {
            case WelcomeMessage welcome:
                PlayerId = welcome.PlayerId;
                tickUpTo = welcome.ServerTick;
                if (welcome.ServerTick != tickManager.TicksGame)
                {
                    // Клиент и сервер должны стартовать с одного тика (догон — T-045).
                    TestAutomation.Note($"lockstep: tick mismatch, server={welcome.ServerTick}, game={tickManager.TicksGame}");
                    Disconnect("несовпадение тика с сервером");
                    return;
                }
                SimSession.Begin(welcome.SessionSeed);
                state = State.InSession;
                holdClock.Restart();
                TestAutomation.Note($"lockstep: Welcome, playerId={welcome.PlayerId}, serverTick={welcome.ServerTick}, seed={welcome.SessionSeed}");
                // Новый игрок держит паузу, пока не пришлёт SpeedRequest: шлём, как только готовы тикать.
                RequestSpeed((int)tickManager.CurTimeSpeed);
                break;

            case RejectMessage reject:
                TestAutomation.Note($"lockstep: REJECTED: {reject.Reason} {reject.Detail}");
                Disconnect("сервер отказал");
                break;

            case TickMessage tick:
                tickUpTo = tick.TickUpTo;
                if (tick.Speed != serverSpeed || (int)tickManager.CurTimeSpeed != tick.Speed)
                {
                    serverSpeed = tick.Speed;
                    applyingServerSpeed = true;
                    try
                    {
                        // Мимо сеттера: он проверяет, может ли игрок управлять временем, и шлёт сообщения.
                        curTimeSpeedRef(tickManager) = (TimeSpeed)tick.Speed;
                    }
                    finally
                    {
                        applyingServerSpeed = false;
                    }
                }
                break;

            case CommandOrderedMessage ordered:
                pendingCommands.Enqueue(ordered);
                break;

            case CommandRejectedMessage rejected:
                TestAutomation.Note($"lockstep: command #{rejected.ClientSeq} REJECTED on map {rejected.MapId}: need {rejected.Required}, have {rejected.Had}");
                Messages.Message("RWMLWC: нет прав на это действие на этой карте", MessageTypeDefOf.RejectInput, false);
                break;

            default:
                // Сообщения не про симуляцию (чат, состав сессии) — интерфейсу сессии (T-025, зона B).
                RWMLWC.Client.UI.SessionUi.Receive(message);
                break;
        }
    }

    /// <summary>Отправить команду игрока серверу. Исполнится у всех, когда вернётся в общем порядке.</summary>
    public static int SendCommand(int mapId, int kind, PermissionLevel required, byte[] payload)
    {
        IConnection? conn = connection;
        if (state != State.InSession || conn == null)
        {
            return -1;
        }
        int clientSeq = nextClientSeq++;
        conn.Send(new CommandMessage
        {
            ClientSeq = clientSeq,
            MapId = mapId,
            Kind = kind,
            Required = required,
            Payload = payload,
        });
        return clientSeq;
    }

    // Команда с Tick = T исполняется, когда тик T завершён (TicksGame == T), до тика T+1.
    private static void ExecuteDueCommands(TickManager tickManager)
    {
        while (pendingCommands.Count > 0 && pendingCommands.Peek().Tick <= tickManager.TicksGame && state == State.InSession)
        {
            CommandOrderedMessage command = pendingCommands.Dequeue();
            if (command.Tick < tickManager.TicksGame)
            {
                // Сервер обещает прислать команду раньше разрешения на следующий тик; сюда попадать не должны.
                TestAutomation.Note($"lockstep: command #{command.Seq} is LATE: tick {command.Tick}, game {tickManager.TicksGame}");
            }
            CommandBus.Execute(command);
        }
    }

    private static void RunTicks(TickManager tickManager)
    {
        // Команды, пришедшие, пока игра стоит на этом тике (в том числе на паузе).
        ExecuteDueCommands(tickManager);
        if (tickManager.TicksGame >= tickUpTo)
        {
            return;
        }
        frameClock.Restart();
        int done = 0;
        while (tickManager.TicksGame < tickUpTo)
        {
            tickManager.DoSingleTick();
            done++;
            if (tickManager.TicksGame % HashInterval == 0)
            {
                SendHashes(tickManager.TicksGame);
            }
            // Порядок у всех клиентов один: тик T, хеш тика T, команды тика T.
            ExecuteDueCommands(tickManager);
            if (state != State.InSession || frameClock.ElapsedMilliseconds > FrameBudgetMs)
            {
                break;
            }
        }
        ticksThisFrameRef(tickManager) = done;
    }

    private static void SendHashes(int tick)
    {
        IConnection? conn = connection;
        if (conn == null)
        {
            return;
        }
        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            MapStateHash hash = MapStateHasher.Compute(maps[i]);
            MapHashMessage message = new MapHashMessage
            {
                MapId = maps[i].uniqueID,
                Tick = tick,
                Total = hash.total,
            };
            message.Parts.Add(new HashPart("things", hash.things));
            message.Parts.Add(new HashPart("pawns", hash.pawns));
            message.Parts.Add(new HashPart("rand", hash.rand));
            conn.Send(message);
        }
    }

    /// <summary>Отправить серверу сообщение не про симуляцию (чат и т. п., T-025). false — нет сессии.</summary>
    public static bool TrySend(Message message)
    {
        IConnection? conn = connection;
        if (state != State.InSession || conn == null)
        {
            return false;
        }
        conn.Send(message);
        return true;
    }

    /// <summary>
    /// Время туда-обратно до сервера в мс, свой замер (T-025); -1 — нет данных. Чтение не чаще раза в секунду
    /// отправляет новый <c>Ping</c> — ответ обрабатывает транспорт.
    /// </summary>
    public static int RoundTripMs
    {
        get
        {
            IConnection? conn = connection;
            if (conn == null)
            {
                return -1;
            }
            if (conn.RoundTripMs < 0 || pingClock.ElapsedMilliseconds >= 1000 || !pingClock.IsRunning)
            {
                pingClock.Restart();
                conn.SendPing();
            }
            return conn.RoundTripMs;
        }
    }

    private static readonly Stopwatch pingClock = new Stopwatch();

    /// <summary>Попросить у сервера скорость (0 — пауза, 1..4 — как TimeSpeed). Повторы не шлём.</summary>
    public static void RequestSpeed(int speed)
    {
        if (state != State.InSession || connection == null)
        {
            return;
        }
        if (speed < 0)
        {
            speed = 0;
        }
        if (speed > 4)
        {
            speed = 4;
        }
        if (speed > 0 && Holding)
        {
            heldSpeed = speed;
            return;
        }
        heldSpeed = -1;
        if (speed == sentSpeed)
        {
            return;
        }
        sentSpeed = speed;
        connection.Send(new SpeedRequestMessage { Speed = (byte)speed });
    }

    /// <summary>Игрок нажал паузу: если сейчас пауза — просим прежнюю скорость, иначе паузу.</summary>
    public static void RequestTogglePause()
    {
        if (serverSpeed == 0)
        {
            RequestSpeed(sentSpeed > 0 ? sentSpeed : 1);
        }
        else
        {
            // Запоминать «прежнюю» не нужно: sentSpeed останется 0, а при снятии паузы попросим 1.
            RequestSpeed(0);
        }
    }

    /// <summary>Истина, пока клиент сам ставит скорость, пришедшую от сервера.</summary>
    public static bool ApplyingServerSpeed => applyingServerSpeed;

    // Сервер только сравнивает это число между игроками. Порядок модов важен для игры, поэтому
    // хеш считается по списку включённых модов в порядке загрузки.
    private static ulong ModListHash()
    {
        Fnv64 hash = Fnv64.Create();
        foreach (ModMetaData mod in ModsConfig.ActiveModsInLoadOrder)
        {
            string id = mod.PackageId ?? "";
            for (int i = 0; i < id.Length; i++)
            {
                hash.Add((int)char.ToLowerInvariant(id[i]));
            }
            hash.Add(-1);
        }
        return hash.Value;
    }
}

/// <summary>Начало сессии: включить раздельную симуляцию и привести Rand к состоянию, общему для всех игроков.</summary>
public static class SimSession
{
    public static void Begin(uint sessionSeed)
    {
        PerMapSim.SessionSeed = sessionSeed;
        if (!PerMapSim.Enabled)
        {
            // Игра ещё не была в режиме сессии (обычный сейв): глобальный Rand симуляции — от зерна сессии.
            // Если режим уже включён (сейв сделан в сессии), состояния Rand берутся из сейва как есть.
            PerMapSim.SetEnabled(true);
            unchecked
            {
                MapRandContext.SetGlobalState((ulong)(sessionSeed * 0x85EBCA6Bu ^ 0x52574D4Cu));
            }
        }
        // Состояния Rand карт, которые ещё не тикали в сессии, создаются при первом тике карты
        // из SessionSeed и номера карты (MapSimState.InitialRandState).
    }
}

// Штатный цикл тиков: пока клиент подключается или в сессии, его заменяет LockstepClient.OnFrame.
[HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
internal static class Patch_TickManager_TickManagerUpdate
{
    private static bool Prefix(TickManager __instance)
    {
        return !LockstepClient.OnFrame(__instance);
    }
}

// Игрок меняет скорость: в сессии это просьба серверу, а не локальное изменение.
[HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
internal static class Patch_TickManager_CurTimeSpeed_Set
{
    private static bool Prefix(TimeSpeed value)
    {
        if (!LockstepClient.InSession || LockstepClient.ApplyingServerSpeed)
        {
            return true;
        }
        LockstepClient.RequestSpeed((int)value);
        return false;
    }
}

[HarmonyPatch(typeof(TickManager), nameof(TickManager.TogglePaused))]
internal static class Patch_TickManager_TogglePaused
{
    private static bool Prefix()
    {
        if (!LockstepClient.InSession)
        {
            return true;
        }
        LockstepClient.RequestTogglePause();
        return false;
    }
}
