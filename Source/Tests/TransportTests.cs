using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace RWMLWC.Tests;

/// <summary>TST-012: транспорт — обмен, обрыв, переподключение.</summary>
public class TransportTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly ITestOutputHelper _out;

    public TransportTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task InMemoryPairExchangesAndPropagatesClose()
    {
        var (a, b) = InMemoryConnection.CreatePair();
        a.Send(new TickMessage { TickUpTo = 5, Speed = 1 });
        b.Send(new SpeedRequestMessage { Speed = 3 });

        var atB = (TickMessage)await b.ReceiveAsync().AsTask().WaitAsync(Wait);
        var atA = (SpeedRequestMessage)await a.ReceiveAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(5, atB.TickUpTo);
        Assert.Equal(3, atA.Speed);

        a.Close("тест");
        await b.Closed.WaitAsync(Wait);
        Assert.False(b.IsOpen);
        await Assert.ThrowsAsync<ConnectionClosedException>(() => b.ReceiveAsync().AsTask());
    }

    [Fact]
    public async Task QuicExchangesAllMessagesInOrderPerStream()
    {
        if (!QuicTransport.IsSupported) { _out.WriteLine("QUIC не поддерживается — тест пропущен"); return; }
        using var dir = new TempDir();
        var cert = ServerCertificate.LoadOrCreate(Path.Combine(dir.Path, "server.pfx"));
        await using var server = await QuicServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        Task<IConnection> accept = server.AcceptAsync();

        await using IConnection client = await QuicTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, ServerCertificate.Thumbprint(cert)).WaitAsync(Wait);
        await using IConnection srv = await accept.WaitAsync(Wait);

        // Управляющий поток: порядок обязан сохраниться.
        for (int i = 1; i <= 200; i++)
            client.Send(new CommandMessage { ClientSeq = i, Kind = 1, Payload = new byte[i] });
        // Объёмный поток: несколько больших кусков снимка.
        var chunk = new byte[ProtocolLimits.SnapshotChunkBytes];
        new Random(1).NextBytes(chunk);
        for (int i = 0; i < 4; i++)
            client.Send(new SnapshotChunkMessage { SnapshotId = 1, ChunkIndex = i, ChunkCount = 4, Data = chunk });

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

        // И в обратную сторону.
        srv.Send(new WelcomeMessage { PlayerId = 7, SessionSeed = 42 });
        var w = (WelcomeMessage)await client.ReceiveAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(7, w.PlayerId);
        Assert.Equal(42u, w.SessionSeed);
    }

    [Fact]
    public async Task QuicRejectsWrongCertificate()
    {
        if (!QuicTransport.IsSupported) { _out.WriteLine("QUIC не поддерживается — тест пропущен"); return; }
        using var dir = new TempDir();
        var cert = ServerCertificate.LoadOrCreate(Path.Combine(dir.Path, "server.pfx"));
        await using var server = await QuicServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        _ = server.AcceptAsync();

        string wrong = new string('0', 64);
        await Assert.ThrowsAnyAsync<Exception>(() => QuicTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, wrong).WaitAsync(Wait));
    }

    [Fact]
    public async Task QuicPeerCloseIsDetected()
    {
        if (!QuicTransport.IsSupported) { _out.WriteLine("QUIC не поддерживается — тест пропущен"); return; }
        using var dir = new TempDir();
        var cert = ServerCertificate.LoadOrCreate(Path.Combine(dir.Path, "server.pfx"));
        await using var server = await QuicServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        Task<IConnection> accept = server.AcceptAsync();
        IConnection client = await QuicTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, ServerCertificate.Thumbprint(cert)).WaitAsync(Wait);
        IConnection srv = await accept.WaitAsync(Wait);

        srv.Close("сервер закрыл");
        await client.Closed.WaitAsync(Wait);
        _out.WriteLine("Причина у клиента: " + client.CloseReason);
        Assert.False(client.IsOpen);
    }

    /// <summary>
    /// Два процесса: сервер (RWMLWC.Server --echo) и клиент (тест). Сервер убивается и запускается снова
    /// на том же порту с тем же сертификатом — клиент сам переподключается и обмен продолжается.
    /// </summary>
    [Fact]
    public async Task TwoProcessesExchangeAndReconnectAfterServerKill()
    {
        if (!QuicTransport.IsSupported) { _out.WriteLine("QUIC не поддерживается — тест пропущен"); return; }
        using var dir = new TempDir();

        using ServerProcess first = await ServerProcess.StartAsync(dir.Path, port: 0, _out);
        int port = first.Port;
        string thumb = first.Thumbprint;

        var connected = new SemaphoreSlim(0);
        var disconnected = new SemaphoreSlim(0);
        await using var client = new ReconnectingClient(
            ct => QuicTransport.ConnectAsync("127.0.0.1", port, thumb, ct),
            minDelay: TimeSpan.FromMilliseconds(200), maxDelay: TimeSpan.FromSeconds(1));
        client.Connected += _ => connected.Release();
        client.Disconnected += reason => { _out.WriteLine("Обрыв: " + reason); disconnected.Release(); };
        client.ConnectFailed += reason => _out.WriteLine("Неудачная попытка: " + reason);
        client.Start();

        Assert.True(await connected.WaitAsync(Wait), "первое подключение");
        await AssertEcho(client.Current!, 1);

        var sw = Stopwatch.StartNew();
        first.Kill();
        Assert.True(await disconnected.WaitAsync(Wait), "обрыв не обнаружен");
        _out.WriteLine($"Обрыв обнаружен за {sw.ElapsedMilliseconds} мс");

        using ServerProcess second = await ServerProcess.StartAsync(dir.Path, port, _out);
        Assert.Equal(thumb, second.Thumbprint);
        sw.Restart();
        Assert.True(await connected.WaitAsync(Wait), "переподключение");
        _out.WriteLine($"Переподключение за {sw.ElapsedMilliseconds} мс после старта второго сервера");
        Assert.Equal(2, client.ConnectCount);
        await AssertEcho(client.Current!, 2);
    }

    private static async Task AssertEcho(IConnection connection, int tick)
    {
        connection.Send(new TickMessage { TickUpTo = tick, Speed = 2 });
        var echoed = (TickMessage)await connection.ReceiveAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(tick, echoed.TickUpTo);
    }

    private sealed class ServerProcess : IDisposable
    {
        private readonly Process _process;
        public int Port { get; private set; }
        public string Thumbprint { get; private set; } = "";

        private ServerProcess(Process process) => _process = process;

        public static async Task<ServerProcess> StartAsync(string dataDir, int port, ITestOutputHelper log)
        {
            // Сборка сервера лежит рядом со сборкой тестов: Source/Server/bin/<конфигурация>/net10.0/.
            string testBin = AppContext.BaseDirectory;
            string config = new DirectoryInfo(testBin).Parent!.Name;
            string serverDll = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", "Server", "bin", config, "net10.0", "RWMLWC.Server.dll"));
            Assert.True(File.Exists(serverDll), "Не найден " + serverDll);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in new[] { serverDll, "--echo", "--port", port.ToString(), "--data", dataDir })
                psi.ArgumentList.Add(a);
            var process = Process.Start(psi)!;
            var sp = new ServerProcess(process);

            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                log.WriteLine("[server] " + e.Data);
                if (e.Data.StartsWith("LISTENING ")) ready.TrySetResult(e.Data);
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) log.WriteLine("[server:err] " + e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            string[] parts = (await ready.Task.WaitAsync(Wait)).Split(' ');
            sp.Port = int.Parse(parts[1]);
            sp.Thumbprint = parts[2];
            return sp;
        }

        public void Kill()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        public void Dispose()
        {
            try { Kill(); } catch { }
            _process.Dispose();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rwmlwc-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
