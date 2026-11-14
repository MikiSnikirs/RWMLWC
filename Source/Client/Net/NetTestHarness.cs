using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Автоматический тест транспорта в игре (TST-016, TST-017 часть 2). Включается файлом
/// <c>RWMLWC/net-test.txt</c> в папке данных игры, строки <c>ключ=значение</c>:
/// <list type="bullet">
/// <item><c>role=selftest</c> — одна машина: пара соединений через стек Steam ↔ мост ↔ локальный сервер-эхо.</item>
/// <item><c>role=host</c> — хост: сервер-эхо в игре + Steam-мост; ждёт друга, пока игра открыта.</item>
/// <item><c>role=join</c>, <c>host=&lt;SteamID64&gt;</c> — друг: подключается к хосту по Steam и гоняет проверки.</item>
/// </list>
/// Результаты — в <c>RWMLWC/net-test-log.txt</c> и в лог игры с меткой <c>[RWMLWC][net-test]</c>.
/// </summary>
[StaticConstructorOnStartup]
internal static class NetTestHarness
{
    private static readonly string Dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
    private static readonly string LogFile = Path.Combine(Dir, "net-test-log.txt");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    static NetTestHarness()
    {
        string cfgPath = Path.Combine(Dir, "net-test.txt");
        if (!File.Exists(cfgPath)) return;
        Dictionary<string, string> cfg = File.ReadAllLines(cfgPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#") && l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')].Trim(), l => l[(l.IndexOf('=') + 1)..].Trim());
        if (cfg.GetValueOrDefault("armed") != "1") return;

        string role = cfg.GetValueOrDefault("role", "");
        Write($"=== старт, роль {role}, протокол v{RWMLWC.Shared.ProtocolInfo.ProtocolVersion}, Steam {(SteamNet.Available ? "есть" : "НЕТ")}");
        if (!SteamNet.Available) { Write("FAIL: Steam не инициализирован"); return; }
        try
        {
            SteamNet.Init(); // главный поток
            Write($"мой SteamID: {SteamNet.MySteamId}");
            switch (role)
            {
                case "selftest": _ = Guard(SelfTestAsync); break;
                case "host": _ = Guard(HostAsync); break;
                case "join":
                    ulong host = ulong.Parse(cfg["host"]);
                    _ = Guard(() => JoinAsync(host));
                    break;
                default: Write("FAIL: неизвестная роль '" + role + "'"); break;
            }
        }
        catch (Exception e)
        {
            Write("FAIL: " + e);
        }
    }

    private static async Task Guard(Func<Task> body)
    {
        try { await body(); }
        catch (Exception e) { Write("FAIL: " + e); }
    }

    private static async Task<(QuicServer server, string thumb)> StartEchoServerAsync()
    {
        var cert = ServerCertificate.LoadOrCreate(Path.Combine(Dir, "net-test-server.pfx"));
        QuicServer server = await QuicServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        _ = EchoService.RunAsync(server, CancellationToken.None, s => Write("эхо-сервер: " + s));
        Write($"эхо-сервер слушает 127.0.0.1:{server.LocalEndPoint.Port}");
        return (server, ServerCertificate.Thumbprint(cert));
    }

    private static async Task SelfTestAsync()
    {
        var (server, thumb) = await StartEchoServerAsync();

        // Замер 1: только QUIC, без Steam.
        IConnection direct = await QuicTransport.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, thumb);
        await RunChecksAsync(direct, "quic-direct");
        direct.Close("замер окончен");

        // Замер 2: только стек Steam, эхо прямо на второй стороне пары, без моста и QUIC.
        var (echoSide, probeSide) = SteamNet.CreateLoopbackPair();
        _ = Task.Run(async () =>
        {
            try { while (true) echoSide.Send(await echoSide.ReceiveAsync()); }
            catch (ConnectionClosedException) { }
        });
        await RunChecksAsync(probeSide, "steam-only");
        probeSide.Close("замер окончен");

        // Замер 3: полный путь — Steam ↔ мост ↔ QUIC ↔ сервер-эхо.
        var bridge = new SteamBridge("127.0.0.1", server.LocalEndPoint.Port, thumb, Write);
        var (hostSide, friendSide) = SteamNet.CreateLoopbackPair();
        Task relay = bridge.AttachAsync(hostSide);

        await RunChecksAsync(friendSide, "selftest");

        var sw = Stopwatch.StartNew();
        friendSide.Close("конец теста");
        await hostSide.Closed.WaitAsync(Wait);
        await relay.WaitAsync(Wait);
        Write($"закрытие дошло через мост за {sw.ElapsedMilliseconds} мс");
        Write("PASS selftest");
    }

    private static async Task HostAsync()
    {
        var (server, thumb) = await StartEchoServerAsync();
        var bridge = new SteamBridge("127.0.0.1", server.LocalEndPoint.Port, thumb, Write);
        // Слушающий сокет открываем в главном потоке.
        var started = new TaskCompletionSource();
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            try { bridge.Start(); started.SetResult(); }
            catch (Exception e) { started.SetException(e); }
        });
        await started.Task;
        Write($"HOST READY. Другу: role=join, host={SteamNet.MySteamId}. Игру не закрывать до команды.");
    }

    private static async Task JoinAsync(ulong host)
    {
        Write($"подключаюсь к {host} через Steam...");
        var sw = Stopwatch.StartNew();
        SteamConnection c1 = await SteamNet.ConnectAsync(host, TimeSpan.FromSeconds(60));
        Write($"соединение установлено за {sw.ElapsedMilliseconds} мс");
        await RunChecksAsync(c1, "join");
        c1.Close("проверка переподключения");

        await Task.Delay(1000);
        sw.Restart();
        SteamConnection c2 = await SteamNet.ConnectAsync(host, TimeSpan.FromSeconds(60));
        Write($"переподключение за {sw.ElapsedMilliseconds} мс");
        await EchoTickAsync(c2, 1);
        Write("PASS join (обмен, большие куски, порядок, переподключение)");

        Write("WAITING FOR HOST CLOSE: хост, закройте игру — проверяем, как быстро друг заметит обрыв.");
        sw.Restart();
        await c2.Closed;
        Write($"обрыв замечен через {sw.ElapsedMilliseconds} мс после начала ожидания: {c2.CloseReason}");
    }

    private static async Task RunChecksAsync(IConnection c, string label)
    {
        // 0. Прогрев: первое сообщение ждёт, пока мост подключится к серверу.
        var warm = Stopwatch.StartNew();
        await EchoTickAsync(c, -1);
        Write($"{label}: первое эхо (с подключением моста к серверу) {warm.ElapsedMilliseconds} мс");

        // 1. Задержка: 50 эхо по одному.
        var rtts = new List<double>();
        for (int i = 0; i < 50; i++)
        {
            var sw = Stopwatch.StartNew();
            await EchoTickAsync(c, i);
            rtts.Add(sw.Elapsed.TotalMilliseconds);
        }
        rtts.Sort();
        Write($"{label}: RTT 50 эхо — медиана {rtts[rtts.Count / 2]:F1} мс, среднее {rtts.Average():F1}, мин {rtts[0]:F1}, макс {rtts[^1]:F1}");

        // 2. Порядок: 200 команд подряд.
        for (int i = 1; i <= 200; i++)
            c.Send(new CommandMessage { ClientSeq = i, Kind = 1, Payload = new byte[i] });
        for (int i = 1; i <= 200; i++)
        {
            var m = (CommandMessage)await c.ReceiveAsync().AsTask().WaitAsync(Wait);
            if (m.ClientSeq != i || m.Payload.Length != i) throw new Exception($"порядок нарушен: ждали {i}, пришло {m.ClientSeq}");
        }
        Write($"{label}: 200 команд вернулись по порядку");

        // 3. Большие куски снимка.
        var chunk = new byte[ProtocolLimits.SnapshotChunkBytes];
        new Random(16).NextBytes(chunk);
        var swc = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++)
            c.Send(new SnapshotChunkMessage { SnapshotId = 1, ChunkIndex = i, ChunkCount = 4, Data = chunk });
        for (int i = 0; i < 4; i++)
        {
            var s = (SnapshotChunkMessage)await c.ReceiveAsync().AsTask().WaitAsync(Wait);
            if (s.ChunkIndex != i || !s.Data.AsSpan().SequenceEqual(chunk)) throw new Exception($"кусок снимка {i} повреждён");
        }
        double mb = 4.0 * chunk.Length * 2 / 1024 / 1024;
        Write($"{label}: 4 куска по 256 КиБ туда и обратно за {swc.ElapsedMilliseconds} мс ({mb / swc.Elapsed.TotalSeconds:F1} МиБ/с суммарно)");
    }

    private static async Task EchoTickAsync(IConnection c, int tick)
    {
        c.Send(new TickMessage { TickUpTo = tick, Speed = 1 });
        var m = (TickMessage)await c.ReceiveAsync().AsTask().WaitAsync(Wait);
        if (m.TickUpTo != tick) throw new Exception($"эхо: ждали тик {tick}, пришёл {m.TickUpTo}");
    }

    private static readonly object LogGate = new();

    private static void Write(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        lock (LogGate)
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogFile, stamped + Environment.NewLine);
        }
        if (line.StartsWith("FAIL")) Log.Warning("[RWMLWC][net-test] " + line);
        else Log.Message("[RWMLWC][net-test] " + line);
    }
}
