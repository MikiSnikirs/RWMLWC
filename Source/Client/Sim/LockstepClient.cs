using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Client.Maps;
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
/// Вход в идущую игру (T-045, первая версия): клиент без мира или с другим тиком просит снимок командой
/// RequestSnapshot. Команда стоит в общем порядке, поэтому все исполняют её в одной точке: клиент-хост
/// снимает снимок всей игры и рассылает его кусками, после чего снимок загружают все, включая хоста
/// (загруженная игра и непрерывная идут по-разному, см. docs/research/sim.md, 6д).
///
/// Нет: переподключения, реакции на рассинхронизацию (T-034), отдачи копии с сервера без хоста.
/// </summary>
public static class LockstepClient
{
    public enum State
    {
        Off,
        Connecting,
        WaitingWelcome,
        InSession,

        /// <summary>В сессии, но ждём куски снимка мира (вошли без мира либо идёт общая перезагрузка).</summary>
        WaitingSnapshot,

        /// <summary>Снимок получен, игра загружается.</summary>
        Loading,
    }

    /// <summary>Вид команды «нужен снимок мира»; исполняет её сам LockstepClient, не CommandBus.</summary>
    public const int RequestSnapshotKind = 100;

    private const byte SnapshotFormat = 1;
    private const int SnapshotChunkPayload = 200_000;

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
    private static uint sessionSeed;
    private static int expectedSnapshotId = -1;
    private static bool snapshotRequestSeen;
    private static readonly Dictionary<int, byte[]?[]> incomingSnapshots = new Dictionary<int, byte[]?[]>();
    private static bool gameLoadedPending;

    /// <summary>Для тестов с отдельным сервером: этот клиент отдаёт снимок (в обычной игре — тот, кто держит сессию).</summary>
    public static bool ForceSnapshotSender;

    private static bool IsSnapshotSender => ForceSnapshotSender || RWMLWC.Client.Net.HostedSession.Running;
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
        expectedSnapshotId = -1;
        snapshotRequestSeen = false;
        incomingSnapshots.Clear();
        gameLoadedPending = false;
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
        return Pump(tickManager);
    }

    /// <summary>Раз в кадр, пока игры нет или она загружается (главное меню, экран загрузки).</summary>
    public static void PumpWithoutGame()
    {
        if (state == State.Off)
        {
            return;
        }
        if (state == State.InSession && Current.Game == null)
        {
            Disconnect("игра закрыта"); // игрок вышел в главное меню
            return;
        }
        Pump(null);
    }

    /// <summary>Игра загружена (любая). Если это наш снимок — продолжим сессию со следующего кадра.</summary>
    public static void OnGameLoaded()
    {
        if (state == State.Loading)
        {
            gameLoadedPending = true;
        }
        else if (state != State.Off)
        {
            TestAutomation.Note("lockstep: another game was loaded during the session, state=" + state);
            Disconnect("загружена другая игра");
        }
    }

    private static bool Pump(TickManager? tickManager)
    {
        if (state == State.Off)
        {
            return false;
        }
        if (tickManager != null)
        {
            ticksThisFrameRef(tickManager) = 0;
        }

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
                SaveTick = tickManager != null ? tickManager.TicksGame : 0,
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

        if (state == State.Loading && gameLoadedPending && tickManager != null)
        {
            FinishSnapshotLoad(tickManager);
        }

        if (state == State.InSession && tickManager != null)
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

    private static void Handle(TickManager? tickManager, Message message)
    {
        switch (message)
        {
            case WelcomeMessage welcome:
                PlayerId = welcome.PlayerId;
                tickUpTo = welcome.ServerTick;
                sessionSeed = welcome.SessionSeed;
                if (tickManager == null || welcome.ServerTick != tickManager.TicksGame)
                {
                    // Мира нет или он на другом тике: просим снимок. Пока не пришлём SpeedRequest,
                    // сервер держит паузу для всех, так что мир за это время не уйдёт вперёд.
                    TestAutomation.Note($"lockstep: Welcome, playerId={welcome.PlayerId}, serverTick={welcome.ServerTick}, game={(tickManager != null ? tickManager.TicksGame.ToString() : "none")}: requesting world snapshot");
                    state = State.WaitingSnapshot;
                    snapshotRequestSeen = false;
                    expectedSnapshotId = -1;
                    connection?.Send(new CommandMessage
                    {
                        ClientSeq = nextClientSeq++,
                        MapId = ProtocolIds.WorldMapId,
                        Kind = RequestSnapshotKind,
                        Required = PermissionLevel.Observer,
                    });
                    break;
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
                if (tickManager == null || state != State.InSession)
                {
                    serverSpeed = tick.Speed; // применим, когда игра загрузится
                    break;
                }
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
                if (state == State.WaitingSnapshot && !snapshotRequestSeen)
                {
                    // Вошли без мира. Всё, что стоит в общем порядке до нашей просьбы, уже будет внутри снимка.
                    if (ordered.Kind == RequestSnapshotKind && ordered.PlayerId == PlayerId)
                    {
                        snapshotRequestSeen = true;
                        expectedSnapshotId = SnapshotIdFor(ordered);
                        pendingCommands.Clear();
                        TryLoadExpectedSnapshot();
                    }
                    break;
                }
                pendingCommands.Enqueue(ordered);
                break;

            case SnapshotChunkMessage chunk:
                OnSnapshotChunk(chunk);
                break;

            case NoticeMessage notice:
                OnNotice(notice);
                break;

            case CommandRejectedMessage rejected:
                TestAutomation.Note($"lockstep: command #{rejected.ClientSeq} REJECTED on map {rejected.MapId}: need {rejected.Required}, have {rejected.Had}");
                Messages.Message("RWMLWC: нет прав на это действие на этой карте", MessageTypeDefOf.RejectInput, false);
                break;

            default:
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
            if (command.Kind == RequestSnapshotKind)
            {
                BeginSnapshotRound(command);
                continue; // состояние сменилось: цикл остановится, остальные команды — после загрузки
            }
            if (command.Tick < tickManager.TicksGame)
            {
                // Сервер обещает прислать команду раньше разрешения на следующий тик; сюда попадать не должны.
                TestAutomation.Note($"lockstep: command #{command.Seq} is LATE: tick {command.Tick}, game {tickManager.TicksGame}");
            }
            CommandBus.Execute(command);
        }
    }

    // ---------- сообщения сервера игроку; расхождение ----------

    /// <summary>
    /// Код Notice «состояния игроков разошлись» (T-034). В NoticeCode его пока нет — предложение
    /// агенту B (КОНТРАКТ); число выбрано следующим свободным. Пока сервер его не шлёт, код не срабатывает.
    /// </summary>
    public const ushort NoticeDesync = 3;

    private static readonly Stopwatch resyncClock = new Stopwatch();

    /// <summary>Сколько раз этот клиент запускал общую перезагрузку из-за расхождения.</summary>
    public static int ResyncsStarted { get; private set; }

    private static void OnNotice(NoticeMessage notice)
    {
        TestAutomation.Note($"lockstep: notice {(ushort)notice.Code}: {notice.Text}");
        if ((ushort)notice.Code != NoticeDesync)
        {
            if (Current.ProgramState == ProgramState.Playing)
            {
                Messages.Message("RWMLWC: " + notice.Text, MessageTypeDefOf.CautionInput, false);
            }
            return;
        }
        // Лечение расхождения (T-034, первая версия): тот же общий снимок, что при входе игрока.
        // Просит его клиент, который держит сессию; не чаще раза в 10 секунд.
        if (!IsSnapshotSender || state != State.InSession || (resyncClock.IsRunning && resyncClock.ElapsedMilliseconds < 10_000))
        {
            return;
        }
        resyncClock.Restart();
        ResyncsStarted++;
        TestAutomation.Note("lockstep: desync reported, requesting a common snapshot");
        if (Current.ProgramState == ProgramState.Playing)
        {
            Messages.Message("RWMLWC: игра разошлась между игроками — перезагружаем общий снимок", MessageTypeDefOf.CautionInput, false);
        }
        connection?.Send(new CommandMessage
        {
            ClientSeq = nextClientSeq++,
            MapId = ProtocolIds.WorldMapId,
            Kind = RequestSnapshotKind,
            Required = PermissionLevel.Observer,
        });
    }

    // ---------- снимок мира: вход в идущую игру ----------

    // Снимок опознаётся номером команды-просьбы в общем порядке: так нельзя принять снимок, снятый раньше просьбы.
    private static int SnapshotIdFor(CommandOrderedMessage request) => unchecked((int)(request.Seq & 0x7FFFFFFF));

    // Все клиенты с миром приходят сюда в одной и той же точке общего порядка.
    private static void BeginSnapshotRound(CommandOrderedMessage request)
    {
        TickManager tickManager = Find.TickManager;
        expectedSnapshotId = SnapshotIdFor(request);
        snapshotRequestSeen = true;
        state = State.WaitingSnapshot;
        HashRecorder.Instance?.SuspendForSnapshotReload();
        if (!IsSnapshotSender)
        {
            TestAutomation.Note($"lockstep: player {request.PlayerId} asked for a snapshot at tick {tickManager.TicksGame}; waiting for it");
            TryLoadExpectedSnapshot(); // куски могли прийти раньше
            return;
        }
        IConnection? conn = connection;
        if (conn == null)
        {
            return;
        }
        byte[] packed = GameSnapshot.Take(out GameSnapshot.Stats stats);
        byte[] data = new byte[packed.Length + 1];
        data[0] = SnapshotFormat;
        Buffer.BlockCopy(packed, 0, data, 1, packed.Length);
        int count = (data.Length + SnapshotChunkPayload - 1) / SnapshotChunkPayload;
        for (int i = 0; i < count; i++)
        {
            int offset = i * SnapshotChunkPayload;
            int length = Math.Min(SnapshotChunkPayload, data.Length - offset);
            byte[] part = new byte[length];
            Buffer.BlockCopy(data, offset, part, 0, length);
            conn.Send(new SnapshotChunkMessage
            {
                SnapshotId = expectedSnapshotId,
                MapId = ProtocolIds.WorldMapId,
                Tick = tickManager.TicksGame,
                ChunkIndex = i,
                ChunkCount = count,
                Data = part,
            });
        }
        TestAutomation.Note($"lockstep: snapshot for player {request.PlayerId} at tick {tickManager.TicksGame}: {stats}, {count} chunks sent; reloading it here too");
        // Отправитель загружает тот же снимок, что и остальные.
        state = State.Loading;
        gameLoadedPending = false;
        GameSnapshot.Apply(packed);
    }

    // Куски копятся при любом состоянии: отстающий клиент может получить их раньше, чем дойдёт до просьбы в общем порядке.
    private static void OnSnapshotChunk(SnapshotChunkMessage chunk)
    {
        if (chunk.MapId != ProtocolIds.WorldMapId)
        {
            return; // пересылка одной карты — T-034
        }
        if (!incomingSnapshots.TryGetValue(chunk.SnapshotId, out byte[]?[]? parts) || parts.Length != chunk.ChunkCount)
        {
            parts = new byte[]?[chunk.ChunkCount];
            incomingSnapshots[chunk.SnapshotId] = parts;
        }
        parts[chunk.ChunkIndex] = chunk.Data;
        TryLoadExpectedSnapshot();
    }

    private static void TryLoadExpectedSnapshot()
    {
        if (state != State.WaitingSnapshot || expectedSnapshotId < 0
            || !incomingSnapshots.TryGetValue(expectedSnapshotId, out byte[]?[]? parts))
        {
            return;
        }
        int total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            byte[]? part = parts[i];
            if (part == null)
            {
                return; // ещё не все куски
            }
            total += part.Length;
        }
        byte[] data = new byte[total];
        int offset = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            byte[] part = parts[i]!;
            Buffer.BlockCopy(part, 0, data, offset, part.Length);
            offset += part.Length;
        }
        incomingSnapshots.Clear();
        if (data.Length < 2 || data[0] != SnapshotFormat)
        {
            TestAutomation.Note("lockstep: snapshot has unknown format " + (data.Length > 0 ? data[0].ToString() : "empty"));
            Disconnect("неизвестный формат снимка");
            return;
        }
        byte[] packed = new byte[data.Length - 1];
        Buffer.BlockCopy(data, 1, packed, 0, packed.Length);
        TestAutomation.Note($"lockstep: snapshot {expectedSnapshotId} received, {data.Length} bytes; loading");
        state = State.Loading;
        gameLoadedPending = false;
        try
        {
            GameSnapshot.Apply(packed);
        }
        catch (Exception ex)
        {
            TestAutomation.Note("lockstep: snapshot load FAILED: " + ex);
            Disconnect("снимок не загрузился");
        }
    }

    // Первый кадр после загрузки снимка: все GameComponent.LoadedGame уже отработали.
    private static void FinishSnapshotLoad(TickManager tickManager)
    {
        gameLoadedPending = false;
        SimSession.Begin(sessionSeed);
        VirtualUi.Reset();
        state = State.InSession;
        holdClock.Restart();
        connection?.Send(new SnapshotAppliedMessage { SnapshotId = expectedSnapshotId, Tick = tickManager.TicksGame });
        TestAutomation.Note($"lockstep: snapshot loaded, tick {tickManager.TicksGame}, serverTick={tickUpTo}, perMapSim={PerMapSim.Enabled}");
        // Скорость — та, что была в мире на момент снимка; на паузе остаёмся на паузе, пока кто-то её не снимет.
        sentSpeed = -1;
        RequestSpeed((int)tickManager.CurTimeSpeed);
        applyingServerSpeed = true;
        try
        {
            curTimeSpeedRef(tickManager) = (TimeSpeed)serverSpeed;
        }
        finally
        {
            applyingServerSpeed = false;
        }
    }

    private static void RunTicks(TickManager tickManager)
    {
        // Команды, пришедшие, пока игра стоит на этом тике (в том числе на паузе).
        ExecuteDueCommands(tickManager);
        if (state != State.InSession || tickManager.TicksGame >= tickUpTo)
        {
            return;
        }
        frameClock.Restart();
        int done = 0;
        while (tickManager.TicksGame < tickUpTo && state == State.InSession)
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

// Пока игры нет или она загружается, штатный цикл тиков не вызывается; сообщения сервера читаем отсюда.
[HarmonyPatch(typeof(Root_Entry), nameof(Root_Entry.Update))]
internal static class Patch_Root_Entry_Update
{
    private static void Postfix()
    {
        TestAutomation.EntryFrame();
        LockstepClient.PumpWithoutGame();
    }
}

[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
internal static class Patch_Root_Play_Update
{
    private static void Postfix()
    {
        if (Current.ProgramState != ProgramState.Playing)
        {
            LockstepClient.PumpWithoutGame();
        }
    }
}
