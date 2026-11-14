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

        public IEnumerable<CommandOrderedMessage> Commands => Received.OfType<CommandOrderedMessage>();
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
        Assert.Equal(StartTick, Assert.IsType<TickMessage>(a.Received[2]).TickUpTo);
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
            Assert.Equal(i + 1, ca[i].Seq); // общий номер без пропусков
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
}
