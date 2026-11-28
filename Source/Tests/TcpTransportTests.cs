using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-055: запасной транспорт TCP + TLS и выбор транспорта.</summary>
public sealed class TcpTransportTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwmlwc-tcp-" + Guid.NewGuid().ToString("N"));
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _cert;
    private readonly string _thumb;

    public TcpTransportTests()
    {
        _cert = ServerCertificate.LoadOrCreate(Path.Combine(_dir, "server.pfx"));
        _thumb = ServerCertificate.Thumbprint(_cert);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task TcpExchangesAllMessagesInOrder()
    {
        await using TcpServer server = TcpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), _cert);
        Task<IConnection> accept = server.AcceptAsync();
        await using IConnection client = await TcpTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, _thumb).WaitAsync(Wait);
        await using IConnection srv = await accept.WaitAsync(Wait);
        Assert.StartsWith("tcp:", client.Remote);

        var chunk = new byte[ProtocolLimits.SnapshotChunkBytes];
        new Random(1).NextBytes(chunk);
        for (int i = 0; i < 4; i++)
            client.Send(new SnapshotChunkMessage { SnapshotId = 1, ChunkIndex = i, ChunkCount = 4, Data = chunk });
        for (int i = 1; i <= 200; i++)
            client.Send(new CommandMessage { ClientSeq = i, Kind = 1, Payload = new byte[i] });

        int nextSeq = 1, nextChunk = 0;
        while (nextSeq <= 200 || nextChunk < 4)
        {
            Message m = await srv.ReceiveAsync().AsTask().WaitAsync(Wait);
            switch (m)
            {
                case CommandMessage c:
                    Assert.Equal(nextSeq++, c.ClientSeq);
                    Assert.Equal(c.ClientSeq, c.Payload.Length);
                    break;
                case SnapshotChunkMessage s:
                    Assert.Equal(nextChunk++, s.ChunkIndex);
                    Assert.Equal(chunk, s.Data);
                    break;
                default:
                    Assert.Fail("Неожиданное сообщение " + m.Type);
                    break;
            }
        }

        srv.Send(new WelcomeMessage { PlayerId = 7, SessionSeed = 42 });
        var w = (WelcomeMessage)await client.ReceiveAsync().AsTask().WaitAsync(Wait);
        Assert.Equal((7, 42u), (w.PlayerId, w.SessionSeed));
    }

    [Fact]
    public async Task TcpRejectsWrongCertificate()
    {
        await using TcpServer server = TcpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), _cert);
        var rejected = new List<string>();
        _ = server.AcceptAsync(default, r => { lock (rejected) rejected.Add(r); });
        await Assert.ThrowsAnyAsync<Exception>(() => TcpTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, new string('0', 64)).WaitAsync(Wait));
    }

    [Fact]
    public async Task TcpPeerCloseIsDetectedAndPingMeasured()
    {
        await using TcpServer server = TcpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), _cert);
        Task<IConnection> accept = server.AcceptAsync();
        IConnection client = await TcpTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, _thumb).WaitAsync(Wait);
        IConnection srv = await accept.WaitAsync(Wait);

        // Ping каждые 2 с идёт сам — время туда-обратно появляется без участия приложения.
        for (int i = 0; i < 60 && client.RoundTripMs < 0; i++) await Task.Delay(100);
        Assert.True(client.RoundTripMs >= 0);

        srv.Close("сервер закрыл");
        await client.Closed.WaitAsync(Wait);
        Assert.False(client.IsOpen);
    }

    [Fact]
    public async Task SilentPeerIsDroppedByIdleTimeout()
    {
        // «Сервер», который принял TCP и молчит: TLS не начнётся, клиент сдаётся по таймауту рукопожатия.
        var raw = new TcpListener(IPAddress.Loopback, 0);
        raw.Start();
        try
        {
            Task<TcpClient> accepted = raw.AcceptTcpClientAsync();
            var started = DateTime.UtcNow;
            await Assert.ThrowsAnyAsync<Exception>(() => TcpTransport.ConnectAsync("127.0.0.1", ((IPEndPoint)raw.LocalEndpoint).Port, _thumb).WaitAsync(Wait));
            Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 5, 14);
            (await accepted).Dispose();
        }
        finally
        {
            raw.Stop();
        }
    }

    [Fact]
    public async Task ClientFallsBackToTcpWhenQuicDoesNotAnswer()
    {
        // Слушает только TCP — как сервер на системе без QUIC.
        await using TcpServer server = TcpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), _cert);
        Task<IConnection> accept = server.AcceptAsync();
        var log = new List<string>();
        TimeSpan was = NetTransport.QuicAttempt;
        NetTransport.QuicAttempt = TimeSpan.FromSeconds(2);
        try
        {
            await using IConnection client = await NetTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, _thumb, log: log.Add).WaitAsync(Wait);
            await using IConnection srv = await accept.WaitAsync(Wait);
            Assert.StartsWith("tcp:", client.Remote);
            if (QuicTransport.IsSupported) Assert.Contains(log, l => l.Contains("пробую TCP"));
        }
        finally
        {
            NetTransport.QuicAttempt = was;
        }
    }

    [Fact]
    public async Task SessionAcceptsQuicAndTcpOnOnePort()
    {
        IReadOnlyList<IConnectionListener> listeners = await NetTransport.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), _cert);
        try
        {
            Assert.Single(listeners.Select(l => l.LocalEndPoint.Port).Distinct());
            int port = listeners[0].LocalEndPoint.Port;
            var host = new SessionHost(new Session(new SessionOptions { StartTick = 1000, SessionSeed = 1 }));
            using var cts = new CancellationTokenSource();
            Task run = host.RunAsync(listeners, cts.Token);

            IConnection tcp = await TcpTransport.ConnectAsync("127.0.0.1", port, _thumb).WaitAsync(Wait);
            IConnection other = QuicTransport.IsSupported
                ? await QuicTransport.ConnectAsync("127.0.0.1", port, _thumb).WaitAsync(Wait)
                : await TcpTransport.ConnectAsync("127.0.0.1", port, _thumb).WaitAsync(Wait);
            tcp.Send(new HelloMessage { PlayerName = "T" });
            other.Send(new HelloMessage { PlayerName = "Q" });

            async Task<WelcomeMessage> WelcomeOf(IConnection c)
            {
                while (true)
                    if (await c.ReceiveAsync().AsTask().WaitAsync(Wait) is WelcomeMessage w) return w;
            }
            WelcomeMessage wt = await WelcomeOf(tcp), wq = await WelcomeOf(other);
            Assert.NotEqual(wt.PlayerId, wq.PlayerId);

            cts.Cancel();
            await run;
            tcp.Close("конец");
            other.Close("конец");
        }
        finally
        {
            foreach (IConnectionListener l in listeners) await l.DisposeAsync();
        }
    }
}
