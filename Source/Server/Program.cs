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
/// Сессия — <see cref="Session"/> (T-013). Режим <c>--echo</c> — для проверки транспорта (TST-012).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"RWMLWC server, protocol v{ProtocolInfo.ProtocolVersion}");

        int port = 0;
        string dataDir = "server-data";
        bool echo = false;
        int startTick = 0;
        uint? seed = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--data": dataDir = args[++i]; break;
                case "--echo": echo = true; break;
                case "--start-tick": startTick = int.Parse(args[++i]); break;
                case "--seed": seed = uint.Parse(args[++i]); break;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    Console.Error.WriteLine("Использование: RWMLWC.Server [--port N] [--data папка] [--start-tick T] [--seed S] [--echo]");
                    return 2;
            }
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
        if (echo)
        {
            await EchoService.RunAsync(server, stop.Token, Console.WriteLine);
            return 0;
        }

        var options = new SessionOptions
        {
            StartTick = startTick,
            SessionSeed = seed ?? (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
        };
        Console.WriteLine($"SESSION startTick={options.StartTick} seed={options.SessionSeed}");
        var host = new SessionHost(new Session(options, Console.WriteLine));
        await host.RunAsync(server, stop.Token, Console.WriteLine);
        return 0;
    }
}
