using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Server;

/// <summary>
/// Лёгкий сервер: хранит сейв, ведёт общие часы, упорядочивает команды. Игру не симулирует.
/// Логика сессии появится в T-013. Сейчас есть режим <c>--echo</c> для проверки транспорта (TST-012).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"RWMLWC server, protocol v{ProtocolInfo.ProtocolVersion}");

        int port = 0;
        string dataDir = "server-data";
        bool echo = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--data": dataDir = args[++i]; break;
                case "--echo": echo = true; break;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    Console.Error.WriteLine("Использование: RWMLWC.Server [--port N] [--data папка] [--echo]");
                    return 2;
            }
        }

        if (!echo)
        {
            Console.WriteLine("Сессии ещё не реализованы (T-013). Для проверки транспорта: --echo.");
            return 0;
        }
        if (!QuicTransport.IsSupported)
        {
            Console.Error.WriteLine("QUIC не поддерживается на этой системе (нужна Windows 11 / Server 2022 или новее).");
            return 3;
        }

        var cert = ServerCertificate.LoadOrCreate(Path.Combine(dataDir, "server.pfx"));
        await using QuicServer server = await QuicServer.StartAsync(new IPEndPoint(IPAddress.Any, port), cert);
        // Строку разбирает тест TST-012: порт и отпечаток сертификата.
        Console.WriteLine($"LISTENING {server.LocalEndPoint.Port} {ServerCertificate.Thumbprint(cert)}");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            while (true)
            {
                IConnection connection = await server.AcceptAsync(stop.Token, reason => Console.WriteLine("REJECTED " + reason));
                Console.WriteLine("CONNECTED " + connection.Remote);
                _ = EchoAsync(connection, stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    private static async Task EchoAsync(IConnection connection, CancellationToken ct)
    {
        try
        {
            while (true)
                connection.Send(await connection.ReceiveAsync(ct));
        }
        catch (Exception e) when (e is ConnectionClosedException or OperationCanceledException)
        {
        }
        Console.WriteLine("DISCONNECTED " + connection.Remote + ": " + connection.CloseReason);
    }
}
