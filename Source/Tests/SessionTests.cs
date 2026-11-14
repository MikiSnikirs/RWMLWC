using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-013: сервер — одинаковый порядок команд у двух клиентов, общие часы, правило меньшей скорости.</summary>
public class SessionTests
{
    private const int StartTick = 15000;

    /// <summary>Клиент в памяти: копит всё, что прислал сервер.</summary>
    private sealed class TestClient
    {
        public readonly IConnection Conn;
        public readonly List<Message> Received = new();
        public int PlayerId;

        public TestClient(IConnection conn) => Conn = conn;

        public void Drain()
        {
            while (Conn.TryReceive(out Message m)) Received.Add(m);
        }

        public IEnumerable<CommandOrderedMessage> Commands => Received.OfType<CommandOrderedMessage>().Where(c => c.Kind != ProtocolIds.PlayerPresenceKind);
        public TickMessage LastTick => Received.OfType<TickMessage>().Last();
    }

    private static (Session s, List<string> log) NewSession()
    {
        var log = new List<string>();
        return (new Session(new SessionOptions { StartTick = StartTick, SessionSeed = 0xABCDEF }, log.Add), log);
    }

    private static TestClient Join(Session s, string name, ulong modList = 7, int protocol = ProtocolInfo.ProtocolVersion)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name, GameVersion = "1.7.5005", ModListHash = modList, ProtocolVersion = protocol });
        s.Pump();
        var c = new TestClient(client);
        c.Drain();
        if (c.Received.FirstOrDefault() is WelcomeMessage w) c.PlayerId = w.PlayerId;
        return c;
    }

    private static void Step(Session s, int ms, params TestClient[] clients)
    {
        s.Pump();
        s.Advance(TimeSpan.FromMilliseconds(ms));
        foreach (TestClient c in clients) c.Drain();
    }

    [Fact]
    public void HandshakeGivesIdsSeedAndStartTick()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        var wa = Assert.IsType<WelcomeMessage>(a.Received[0]);
        var wb = Assert.IsType<WelcomeMessage>(b.Received[0]);
        Assert.Equal(1, wa.PlayerId);
        Assert.Equal(2, wb.PlayerId);
        Assert.Equal(0xABCDEFu, wa.SessionSeed);
        Assert.Equal(StartTick, wa.ServerTick);
        // Сразу после Welcome — таблица прав и текущее разрешение тикать.
        Assert.IsType<PermissionsMessage>(a.Received[1]);
        Assert.IsType<PlayersMessage>(a.Received[2]);
        Assert.Equal(StartTick, Assert.IsType<TickMessage>(a.Received[3]).TickUpTo);
    }

    [Fact]
    public void TwoClientsReceiveCommandsInSameOrderOnSameTick()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        a.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        Step(s, 50, a, b);

        var rnd = new Random(13);
        int seqA = 0, seqB = 0;
        for (int step = 0; step < 200; step++)
        {
            // Случайно: команды одного, другого или обоих в одном проходе, иногда пауза и снятие паузы.
            int r = rnd.Next(6);
            if (r <= 2) a.Conn.Send(new CommandMessage { ClientSeq = ++seqA, MapId = 0, Kind = 1, Payload = new[] { (byte)step } });
            if (r >= 1 && r <= 3) b.Conn.Send(new CommandMessage { ClientSeq = ++seqB, MapId = 0, Kind = 2 });
            if (r == 4) a.Conn.Send(new SpeedRequestMessage { Speed = (byte)(step % 7 == 0 ? 0 : 3) });
            Step(s, rnd.Next(1, 40), a, b);
        }

        List<CommandOrderedMessage> ca = a.Commands.ToList();
        List<CommandOrderedMessage> cb = b.Commands.ToList();
        Assert.Equal(seqA + seqB, ca.Count);
        Assert.Equal(ca.Count, cb.Count);
        for (int i = 0; i < ca.Count; i++)
        {
            Assert.Equal(ca[0].Seq + i, ca[i].Seq); // общий номер без пропусков (до них — служебные 102 о входе игроков)
            Assert.Equal((ca[i].Seq, ca[i].Tick, ca[i].PlayerId, ca[i].ClientSeq, ca[i].Kind),
                         (cb[i].Seq, cb[i].Tick, cb[i].PlayerId, cb[i].ClientSeq, cb[i].Kind));
        }
        // Команды каждого игрока сохраняют его собственный порядок.
        Assert.Equal(Enumerable.Range(1, seqA), ca.Where(c => c.PlayerId == a.PlayerId).Select(c => c.ClientSeq));
        Assert.Equal(Enumerable.Range(1, seqB), ca.Where(c => c.PlayerId == b.PlayerId).Select(c => c.ClientSeq));

        AssertCommandsNeverLate(a);
        AssertCommandsNeverLate(b);
        Assert.True(a.LastTick.TickUpTo > StartTick);
    }

    /// <summary>
    /// Гарантия протокола: команда с тиком T приходит раньше любого Tick с TickUpTo &gt; T,
    /// и T не меньше уже разрешённого — клиент ещё не прошёл тик T+1 и успевает её исполнить.
    /// </summary>
    private static void AssertCommandsNeverLate(TestClient c)
    {
        int allowed = int.MinValue;
        foreach (Message m in c.Received)
        {
            if (m is TickMessage t)
            {
                Assert.True(t.TickUpTo >= allowed, "TickUpTo не должен уменьшаться");
                allowed = t.TickUpTo;
            }
            else if (m is CommandOrderedMessage cmd)
            {
                Assert.True(cmd.Tick >= allowed, $"команда #{cmd.Seq} на тике {cmd.Tick}, а клиенту уже разрешено {allowed}");
            }
        }
    }

    [Fact]
    public void ClockRunsAtLowestSpeedAndAnyPausePausesAll()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");

        // Новые игроки держат паузу.
        Step(s, 1000, a, b);
        Assert.Equal(0, s.Speed);
        Assert.Equal(StartTick, s.TickUpTo);

        a.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        Step(s, 1000, a, b);
        Assert.Equal(1, s.Speed);
        Assert.Equal(StartTick + 60, s.TickUpTo); // 60 тиков в секунду на скорости 1
        Assert.Equal((byte)1, a.LastTick.Speed);
        Assert.Equal(b.LastTick.TickUpTo, a.LastTick.TickUpTo);

        b.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        Step(s, 1000, a, b);
        Assert.Equal(3, s.Speed);
        Assert.Equal(StartTick + 60 + 360, s.TickUpTo); // скорость 3 = Superfast = 6x

        b.Conn.Send(new SpeedRequestMessage { Speed = 0 });
        Step(s, 1000, a, b);
        Assert.Equal(0, s.Speed);
        Assert.Equal(StartTick + 420, s.TickUpTo);
        Assert.Equal((byte)0, a.LastTick.Speed);

        // Ушедший игрок не держит скорость.
        b.Conn.Close("вышел");
        Step(s, 1000, a);
        Assert.Equal(3, s.Speed);
        Assert.Equal(StartTick + 420 + 360, s.TickUpTo);
    }

    [Fact]
    public void CommandsAreAcceptedDuringPause()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        a.Conn.Send(new CommandMessage { ClientSeq = 1, Kind = 5 });
        Step(s, 500, a, b);
        CommandOrderedMessage c = Assert.Single(b.Commands);
        Assert.Equal(StartTick, c.Tick); // исполняется сразу после текущего тика, без снятия паузы
        Assert.Equal(0, s.Speed);
    }

    [Theory]
    [InlineData("protocol", RejectReason.ProtocolMismatch)]
    [InlineData("modlist", RejectReason.ModListMismatch)]
    [InlineData("name", RejectReason.NameTaken)]
    public void BadHandshakeIsRejected(string what, RejectReason expected)
    {
        var (s, _) = NewSession();
        Join(s, "A", modList: 7);
        TestClient bad = what switch
        {
            "protocol" => Join(s, "B", protocol: ProtocolInfo.ProtocolVersion + 1),
            "modlist" => Join(s, "B", modList: 8),
            _ => Join(s, "A"),
        };
        var reject = Assert.IsType<RejectMessage>(Assert.Single(bad.Received));
        Assert.Equal(expected, reject.Reason);
        Assert.False(bad.Conn.IsOpen);
        Assert.Single(s.Players);
    }

    [Fact]
    public void SilentOrWrongFirstMessageIsDropped()
    {
        var (s, _) = NewSession();
        var (silent, silentServer) = InMemoryConnection.CreatePair();
        var (wrong, wrongServer) = InMemoryConnection.CreatePair();
        s.Accept(silentServer);
        s.Accept(wrongServer);
        wrong.Send(new SpeedRequestMessage { Speed = 1 });
        s.Pump();
        Assert.False(wrong.IsOpen);
        s.Advance(TimeSpan.FromSeconds(11));
        Assert.False(silent.IsOpen);
        Assert.Empty(s.Players);
    }

    [Fact]
    public void ReturningPlayerKeepsIdAndHoldsPauseUntilReady()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        a.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        Step(s, 100, a, b);
        Assert.Equal(1, s.Speed);

        b.Conn.Close("обрыв");
        Step(s, 100, a);
        TestClient b2 = Join(s, "B");
        Assert.Equal(b.PlayerId, Assert.IsType<WelcomeMessage>(b2.Received[0]).PlayerId);
        Step(s, 100, a, b2);
        Assert.Equal(0, s.Speed); // вернувшийся держит паузу, пока не загрузится
        Assert.Equal(2, s.Players.Count);
    }

    [Fact]
    public void HashMismatchRaisesDesync()
    {
        var (s, log) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        var desyncs = new List<(int, int)>();
        s.Desync += (h1, p1, h2, p2) => desyncs.Add((h1.Tick, p2));

        a.Conn.Send(new MapHashMessage { MapId = 0, Tick = 15100, Total = 1 });
        b.Conn.Send(new MapHashMessage { MapId = 0, Tick = 15100, Total = 1 });
        a.Conn.Send(new MapHashMessage { MapId = 0, Tick = 15200, Total = 2 });
        b.Conn.Send(new MapHashMessage { MapId = 0, Tick = 15200, Total = 3 });
        Step(s, 10, a, b);

        Assert.Equal(new[] { (15200, b.PlayerId) }, desyncs);
        Assert.Contains(log, l => l.Contains("РАСХОЖДЕНИЕ"));
    }

    [Fact]
    public void DesyncNoticeGoesToAllAtMostOncePerIntervalPerMap()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        void Pair(int map, int tick)
        {
            a.Conn.Send(new MapHashMessage { MapId = map, Tick = tick, Total = 1 });
            b.Conn.Send(new MapHashMessage { MapId = map, Tick = tick, Total = 2 });
        }
        Pair(0, 15060);
        Pair(0, 15120);                                            // та же карта, раньше интервала — без Notice
        Pair(1, 15120);                                            // другая карта — свой Notice
        Pair(0, 15060 + ProtocolLimits.DesyncNoticeIntervalTicks); // интервал прошёл
        Step(s, 10, a, b);

        foreach (TestClient c in new[] { a, b })
        {
            var notices = c.Received.OfType<NoticeMessage>().Where(n => n.Code == NoticeCode.Desync).Select(n => n.Text).ToList();
            Assert.Equal(new[]
            {
                $"карта 0 тик 15060: игрок {a.PlayerId} ≠ игрок {b.PlayerId}",
                $"карта 1 тик 15120: игрок {a.PlayerId} ≠ игрок {b.PlayerId}",
                $"карта 0 тик {15060 + ProtocolLimits.DesyncNoticeIntervalTicks}: игрок {a.PlayerId} ≠ игрок {b.PlayerId}",
            }, notices);
        }
    }

    private static List<(int id, string name, bool online)> LastPlayers(TestClient c) =>
        c.Received.OfType<PlayersMessage>().Last().Players.Select(p => (p.PlayerId, p.Name, p.Online)).ToList();

    [Fact]
    public void PlayersListFollowsJoinLeaveAndReturn()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        Assert.Equal(new[] { (1, "A", true) }, LastPlayers(a));

        TestClient b = Join(s, "B");
        a.Drain();
        Assert.Equal(new[] { (1, "A", true), (2, "B", true) }, LastPlayers(a));
        Assert.Equal(new[] { (1, "A", true), (2, "B", true) }, LastPlayers(b));

        b.Conn.Close("обрыв");
        Step(s, 10, a);
        Assert.Equal(new[] { (1, "A", true), (2, "B", false) }, LastPlayers(a));
        int count = a.Received.OfType<PlayersMessage>().Count();
        Step(s, 10, a);
        Assert.Equal(count, a.Received.OfType<PlayersMessage>().Count()); // без изменений — не рассылается

        TestClient b2 = Join(s, "B");
        a.Drain();
        Assert.Equal(new[] { (1, "A", true), (2, "B", true) }, LastPlayers(a));
        Assert.Equal(new[] { (1, "A", true), (2, "B", true) }, LastPlayers(b2));
    }

    [Fact]
    public void PlayerReturningBeforeLeaveIsNoticedStillGetsPlayers()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        b.Conn.Close("обрыв");
        // Сервер ещё не прошёл Pump и не разослал уход — B уже вернулся.
        var (client, server) = InMemoryConnection.CreatePair("B", "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = "B", GameVersion = "1.7.5005", ModListHash = 7 });
        s.Pump();
        var b2 = new TestClient(client);
        b2.Drain();
        Assert.Equal(new[] { (1, "A", true), (2, "B", true) }, LastPlayers(b2));
    }

    [Fact]
    public void SnapshotChunkGoesToOthersOnly()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        a.Conn.Send(new SnapshotChunkMessage { SnapshotId = 1, ChunkIndex = 0, ChunkCount = 1, Data = new byte[] { 9 } });
        Step(s, 10, a, b);
        Assert.Single(b.Received.OfType<SnapshotChunkMessage>());
        Assert.Empty(a.Received.OfType<SnapshotChunkMessage>());
    }

    /// <summary>То же по-настоящему: сервер с QUIC и часами в реальном времени, два клиента по сети.</summary>
    [Fact]
    public async Task OverQuicTwoClientsGetSameOrder()
    {
        if (!QuicTransport.IsSupported) return;
        string dir = Path.Combine(Path.GetTempPath(), "rwmlwc-session-" + Guid.NewGuid().ToString("N"));
        var cert = ServerCertificate.LoadOrCreate(Path.Combine(dir, "server.pfx"));
        await using var server = await QuicServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        var host = new SessionHost(new Session(new SessionOptions { StartTick = StartTick, SessionSeed = 1 }));
        using var cts = new CancellationTokenSource();
        Task run = host.RunAsync(server, cts.Token);

        string thumb = ServerCertificate.Thumbprint(cert);
        IConnection a = await QuicTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, thumb);
        IConnection b = await QuicTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, thumb);
        a.Send(new HelloMessage { PlayerName = "A" });
        b.Send(new HelloMessage { PlayerName = "B" });
        a.Send(new SpeedRequestMessage { Speed = 2 });
        b.Send(new SpeedRequestMessage { Speed = 2 });
        for (int i = 1; i <= 50; i++)
        {
            a.Send(new CommandMessage { ClientSeq = i, Kind = 1 });
            b.Send(new CommandMessage { ClientSeq = i, Kind = 2 });
        }

        async Task<List<CommandOrderedMessage>> Collect(IConnection c)
        {
            var list = new List<CommandOrderedMessage>();
            while (list.Count < 100)
                if (await c.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) is CommandOrderedMessage m) list.Add(m);
            return list;
        }

        List<CommandOrderedMessage> la = await Collect(a);
        List<CommandOrderedMessage> lb = await Collect(b);
        Assert.Equal(la.Select(m => (m.Seq, m.Tick, m.PlayerId, m.ClientSeq)), lb.Select(m => (m.Seq, m.Tick, m.PlayerId, m.ClientSeq)));
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), la.Select(m => m.Seq));

        cts.Cancel();
        await run;
        a.Close("конец"); b.Close("конец");
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void PingIsAnsweredByTheConnectionItselfAndMeasured()
    {
        var (a, b) = InMemoryConnection.CreatePair();
        Assert.Equal(-1, a.RoundTripMs);
        a.SendPing();
        Assert.True(a.RoundTripMs >= 0);
        // Ни Ping, ни Pong до приложения не доходят.
        Assert.False(a.TryReceive(out _));
        Assert.False(b.TryReceive(out _));
    }

    [Fact]
    public void ServerPingsPlayersAndSendsPingsInPlayers()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        Assert.Equal(-1, a.Received.OfType<PlayersMessage>().Last().Players[0].PingMs);
        Step(s, ProtocolLimits.PingIntervalMs, a);          // сервер отправил Ping, соединение в памяти ответило сразу
        Step(s, ProtocolLimits.PlayersRefreshMs, a);        // пинг изменился (-1 → 0) — новый состав
        PlayerInfo me = a.Received.OfType<PlayersMessage>().Last().Players.Single();
        Assert.True(me.PingMs >= 0);
    }

    /// <summary>Соединение с постоянным пингом: в памяти пинг меряется по часам и может прыгать 0 ↔ 1 мс.</summary>
    private sealed class FixedPing : IConnection
    {
        private readonly IConnection _inner;
        public FixedPing(IConnection inner) => _inner = inner;
        public string Remote => _inner.Remote;
        public bool IsOpen => _inner.IsOpen;
        public string? CloseReason => _inner.CloseReason;
        public System.Threading.Tasks.Task Closed => _inner.Closed;
        public void Send(Message message) => _inner.Send(message);
        public bool TryReceive(out Message message) => _inner.TryReceive(out message);
        public System.Threading.Tasks.ValueTask<Message> ReceiveAsync(System.Threading.CancellationToken ct = default) => _inner.ReceiveAsync(ct);
        public void Close(string reason) => _inner.Close(reason);
        public void SendPing() { }
        public int RoundTripMs => 25;
        public System.Threading.Tasks.ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Fact]
    public void PlayersAreNotResentWhilePingsStayTheSame()
    {
        var (s, _) = NewSession();
        var (client, server) = InMemoryConnection.CreatePair("A", "server");
        s.Accept(new FixedPing(server));
        client.Send(new HelloMessage { PlayerName = "A", GameVersion = "1.7.5005", ModListHash = 7 });
        var a = new TestClient(client);
        Step(s, ProtocolLimits.PlayersRefreshMs, a);        // пинг стал известен (25) — новый состав
        Assert.Equal(25, a.Received.OfType<PlayersMessage>().Last().Players.Single().PingMs);
        int count = a.Received.OfType<PlayersMessage>().Count();
        Step(s, ProtocolLimits.PlayersRefreshMs, a);        // пинг тот же — не рассылается
        Step(s, ProtocolLimits.PlayersRefreshMs, a);
        Assert.Equal(count, a.Received.OfType<PlayersMessage>().Count());
    }

    [Fact]
    public void ChatGoesToEveryoneWithAuthorTrimmedAndLimited()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        a.Conn.Send(new ChatMessage { PlayerId = 99, Text = "  привет  " });  // номер автора ставит сервер
        a.Conn.Send(new ChatMessage { Text = "   " });                        // пустое — отбрасывается
        b.Conn.Send(new ChatMessage { Text = new string('я', ProtocolLimits.MaxChatChars + 50) });
        Step(s, 10, a, b);

        foreach (TestClient c in new[] { a, b })
        {
            var chats = c.Received.OfType<ChatMessage>().ToList();
            Assert.Equal(2, chats.Count);
            Assert.Equal((a.PlayerId, "привет"), (chats[0].PlayerId, chats[0].Text));
            Assert.Equal(b.PlayerId, chats[1].PlayerId);
            Assert.Equal(ProtocolLimits.MaxChatChars, chats[1].Text.Length);
        }
    }

    [Fact]
    public void ChatFloodIsDropped()
    {
        var (s, log) = NewSession();
        TestClient a = Join(s, "A");
        for (int i = 0; i < ProtocolLimits.MaxChatPerWindow + 3; i++)
            a.Conn.Send(new ChatMessage { Text = "спам " + i });
        Step(s, 10, a);
        Assert.Equal(ProtocolLimits.MaxChatPerWindow, a.Received.OfType<ChatMessage>().Count());
        Assert.Contains(log, l => l.Contains("слишком часто"));

        Step(s, ProtocolLimits.ChatWindowMs, a);  // окно прошло
        a.Conn.Send(new ChatMessage { Text = "снова можно" });
        Step(s, 10, a);
        Assert.Equal("снова можно", a.Received.OfType<ChatMessage>().Last().Text);
    }

    [Fact]
    public void ColorIsUniqueFromThePaletteAndSurvivesReconnect()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        int red = 0xE53935, green = 0x43A047;
        a.Conn.Send(new ChooseColorMessage { Color = red });
        b.Conn.Send(new ChooseColorMessage { Color = red });       // занят
        b.Conn.Send(new ChooseColorMessage { Color = PlayerColors.None });  // «нет цвета» выбрать нельзя
        b.Conn.Send(new ChooseColorMessage { Color = 0x1000000 });          // не RGB
        b.Conn.Send(new ChooseColorMessage { Color = green });
        Step(s, 10, a, b);
        Assert.Contains(b.Received, m => m is NoticeMessage n && n.Code == NoticeCode.ColorTaken);
        var players = b.Received.OfType<PlayersMessage>().Last().Players;
        Assert.Equal(red, players.Single(p => p.PlayerId == a.PlayerId).Color);
        Assert.Equal(green, players.Single(p => p.PlayerId == b.PlayerId).Color);

        b.Conn.Close("обрыв");
        Step(s, 10, a);
        TestClient b2 = Join(s, "B");
        Assert.Equal(green, b2.Received.OfType<PlayersMessage>().Last().Players.Single(p => p.PlayerId == b.PlayerId).Color);
        // Цвет ушедшего игрока занят и пока его нет.
    }

    [Fact]
    public void CursorsAreForwardedToOthersOnlyAndRateLimited()
    {
        var (s, _) = NewSession();
        TestClient a = Join(s, "A");
        TestClient b = Join(s, "B");
        for (int i = 0; i < ProtocolLimits.MaxCursorsPerSecond + 5; i++)
            a.Conn.Send(new CursorMessage { PlayerId = 99, MapId = 0, X = i, Z = 2 });
        Step(s, 10, a, b);
        var got = b.Received.OfType<CursorMessage>().ToList();
        Assert.Equal(ProtocolLimits.MaxCursorsPerSecond, got.Count);
        Assert.All(got, c => Assert.Equal(a.PlayerId, c.PlayerId)); // номер ставит сервер
        Assert.Empty(a.Received.OfType<CursorMessage>());

        Step(s, 1000, a, b); // секунда прошла
        a.Conn.Send(new CursorMessage { MapId = CursorMessage.NoCursor });
        Step(s, 10, a, b);
        Assert.Equal(CursorMessage.NoCursor, b.Received.OfType<CursorMessage>().Last().MapId);
    }
}
