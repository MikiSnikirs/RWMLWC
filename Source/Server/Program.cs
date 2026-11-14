using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Server;

/// <summary>
/// Выделенный сервер (T-058): хранит копии мира, ведёт общие часы, упорядочивает команды. Игру не симулирует.
/// Сессия — <see cref="Session"/> (T-013). Режим <c>--echo</c> — для проверки транспорта (TST-012).
/// Команды консоли — <c>help</c>.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Консоль Windows по умолчанию не в UTF-8 — русские сообщения сервера выходили нечитаемыми.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"RWMLWC server, protocol v{ProtocolInfo.ProtocolVersion}");

        // Настройки: файл server.txt в папке данных; аргументы командной строки важнее файла.
        string dataDir = "server-data";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--data") dataDir = args[i + 1];
        SettingsFile settings = SettingsFile.LoadOrCreate(Path.Combine(dataDir, "server.txt"), SettingsTemplate, w => Console.WriteLine("ВНИМАНИЕ: " + w));
        Console.WriteLine((settings.Created ? "создан файл настроек: " : "настройки: ") + Path.GetFullPath(settings.Path));
        int port = settings.GetInt("port", DefaultPort, 0, 65535);
        string? publicHost = settings.GetString("public_host");
        double autosaveMinutes = settings.GetDouble("autosave_minutes", 5, 0, 24 * 60);
        int maxPlayers = settings.GetInt("max_players", 8, 1, 64);
        int maxLead = settings.GetInt("max_lead_ticks", 180, 0, 100000);
        int keepCopies = settings.GetInt("keep_copies", SaveStore.DefaultKeepCopies, 1, 1000);
        double backupHours = settings.GetDouble("backup_hours", 24, 0, 24 * 365);
        int keepBackups = settings.GetInt("keep_backups", 7, 1, 1000);
        var whitelist = new HashSet<string>(
            (settings.GetString("whitelist") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        bool echo = false;
        int startTick = 0;
        uint? seed = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--data": i++; break;
                case "--echo": echo = true; break;
                case "--start-tick": startTick = int.Parse(args[++i]); break;
                case "--seed": seed = uint.Parse(args[++i]); break;
                case "--public-host": publicHost = args[++i]; break;
                default:
                    Console.Error.WriteLine($"Неизвестный аргумент: {args[i]}");
                    Console.Error.WriteLine("Использование: RWMLWC.Server [--data папка] [--port N] [--public-host адрес] [--start-tick T] [--seed S] [--echo]. Остальное — в <папка>/server.txt.");
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

        var store = new SaveStore(dataDir, keepCopies)
        {
            BackupInterval = backupHours > 0 ? TimeSpan.FromHours(backupHours) : null,
            KeepBackups = keepBackups,
        };
        SaveCopy? latest = store.Latest;
        if (latest != null)
        {
            Console.WriteLine($"SAVE world={latest.WorldId:X16} tick={latest.Tick} saved={latest.SavedUtc:u} size={latest.Size}");
            if (startTick == 0) startTick = latest.Tick;
        }
        else
        {
            Console.WriteLine("SAVE нет — ждём снимок мира от хоста");
        }
        var options = new SessionOptions
        {
            StartTick = startTick,
            SessionSeed = seed ?? (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
            Store = store,
            Dedicated = true,
            MaxPlayers = maxPlayers,
            MaxLeadTicks = maxLead,
            AllowedNames = whitelist.Count > 0 ? whitelist : null,
            Password = settings.GetString("password"),
            State = new SessionStateFile(Path.Combine(dataDir, "session.json")),
            AlarmSeconds = settings.GetInt("alarm_seconds", 45, 5, 600),
            AutosaveInterval = autosaveMinutes > 0 ? TimeSpan.FromMinutes(autosaveMinutes) : null,
        };
        Console.WriteLine($"SESSION startTick={options.StartTick} seed={options.SessionSeed}");
        Console.WriteLine($"игроков до {options.MaxPlayers}, автосохранение {(options.AutosaveInterval is { } a ? $"каждые {a.TotalMinutes:0.##} мин" : "выключено")}"); Console.WriteLine($"копий мира: {keepCopies}, резервные копии: {(backupHours > 0 ? $"раз в {backupHours:0.##} ч, хранить {keepBackups}" : "выключены")}, белый список: {(whitelist.Count > 0 ? string.Join(", ", whitelist) : "нет (пускать всех)")}");
        PrintConnectionCodes(server.LocalEndPoint.Port, ServerCertificate.Thumbprint(cert), publicHost);
        var host = new SessionHost(new Session(options, Console.WriteLine));
        _ = Task.Run(() => ConsoleLoop(host, store, stop));
        await host.RunAsync(server, stop.Token, Console.WriteLine);
        Console.WriteLine("сервер остановлен");
        return 0;
    }

    /// <summary>Порт выделенного сервера по умолчанию (UDP: QUIC).</summary>
    public const int DefaultPort = 47800;

    private const string SettingsTemplate =
        "# Настройки выделенного сервера RWMLWC.\r\n" +
        "# Строки «ключ = значение»; строки с # — комментарии. Изменения действуют после перезапуска сервера.\r\n" +
        "\r\n" +
        "# Порт UDP (QUIC). Для игры через интернет пробросьте его на роутере. 0 — любой свободный.\r\n" +
        "port = 47800\r\n" +
        "\r\n" +
        "# Внешний адрес (IP или имя) — сервер напечатает готовый код подключения для друзей через интернет.\r\n" +
        "public_host =\r\n" +
        "\r\n" +
        "# Автосохранение мира на сервер, минут (0 — выключено). Ещё сохраняется, когда кто-то выходит.\r\n" +
        "autosave_minutes = 5\r\n" +
        "\r\n" +
        "# Сколько игроков одновременно.\r\n" +
        "max_players = 8\r\n" +
        "\r\n" +
        "# На сколько тиков часы сервера могут опережать самого медленного игрока (0 — без ограничения).\r\n" +
        "# Меньше — быстрее исполняются приказы и входят новички; больше — медленный компьютер реже тормозит остальных.\r\n" +
        "max_lead_ticks = 180\r\n" +
        "\r\n" +
        "# Сколько последних копий мира хранить в saves/.\r\n" +
        "keep_copies = 5\r\n" +
        "\r\n" +
        "# Резервная копия мира в backups/ раз в столько часов (0 — не делать) и сколько их хранить.\r\n" +
        "backup_hours = 24\r\n" +
        "keep_backups = 7\r\n" +
        "\r\n" +
        "# Белый список: имена игроков через запятую (регистр не важен). Пусто — пускать всех.\r\n" +
        "# Имя игрок задаёт сам, так что это защита от случайных людей, а не пароль.\r\n" +
        "whitelist =\r\n" +
        "\r\n" +
        "# Пароль сервера. Пусто — без пароля. Игроки вводят его в окне «Подключиться к серверу».\r\n" +
        "password =\r\n" +
        "\r\n" +
        "# Тревога: если на карте отошедшего игрока крупная угроза, игра у всех встаёт на паузу на столько секунд\r\n" +
        "# (или пока он не вернётся к компьютеру).\r\n" +
        "alarm_seconds = 45\r\n";

    private static void PrintConnectionCodes(int port, string thumbprint, string? publicHost)
    {
        Console.WriteLine("Код подключения (в игре: «Сетевая игра» → «Подключиться к серверу»):");
        if (publicHost != null)
        {
            Console.WriteLine("  через интернет: " + ConnectionCode.Format(publicHost, port, thumbprint));
        }
        foreach (IPAddress ip in LocalAddresses())
            Console.WriteLine("  в локальной сети: " + ConnectionCode.Format(ip.ToString(), port, thumbprint));
        if (publicHost == null)
            Console.WriteLine($"  через интернет: укажите внешний адрес (--public-host) и пробросьте UDP-порт {port} на роутере.");
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) result.Add(a.Address);
            }
        }
        catch (NetworkInformationException)
        {
        }
        if (result.Count == 0) result.Add(IPAddress.Loopback);
        return result;
    }

    private const string Help =
        "Команды: status — состояние; players — игроки; say <текст> — сообщение в чат; " +
        "kick <номер> — отключить игрока; save — сохранить мир сейчас; saves — копии мира; stop — остановить сервер; help — эта справка.";

    /// <summary>Команды администратора из консоли. Действия над сессией выполняются в её потоке.</summary>
    private static void ConsoleLoop(SessionHost host, SaveStore store, CancellationTokenSource stop)
    {
        Console.WriteLine(Help);
        while (!stop.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                return;
            }
            if (line == null) return; // ввода нет (запуск без консоли) — сервер работает дальше
            line = line.Trim();
            if (line.Length == 0) continue;
            int space = line.IndexOf(' ');
            string cmd = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
            string arg = space < 0 ? "" : line.Substring(space + 1).Trim();
            switch (cmd)
            {
                case "help":
                    Console.WriteLine(Help);
                    break;
                case "status":
                    host.Post(s => Console.WriteLine(
                        $"тик {s.TickUpTo}, скорость {s.Speed}{(s.AlarmActive ? " (ТРЕВОГА)" : "")}, игроков в сети {s.Players.Count(p => p.Online)} из {s.Players.Count}" +
                        $" (отошли {s.Players.Count(p => p.Online && p.Idle)}), " +
                        (store.Latest is { } l ? $"копия мира: тик {l.Tick}, {l.SavedUtc.ToLocalTime():g}" : "копии мира нет")));
                    break;
                case "players":
                    host.Post(s =>
                    {
                        if (s.Players.Count == 0) Console.WriteLine("игроков не было");
                        foreach (Session.Player p in s.Players)
                            Console.WriteLine($"  {p.Id}. {p.Name} — {(p.Online ? $"в сети, пинг {p.Connection.RoundTripMs} мс{(p.Idle ? ", отошёл" : "")}{(p.HasWorld ? "" : ", загружает мир")}" : "не в сети")}" +
                                (p.Color != 0 ? $", цвет #{p.Color:X6}" : ""));
                    });
                    break;
                case "say":
                    host.Post(s => s.ServerChat(arg));
                    break;
                case "kick":
                    if (!int.TryParse(arg, out int id)) { Console.WriteLine("kick <номер игрока>"); break; }
                    host.Post(s => { if (!s.Kick(id, "администратор")) Console.WriteLine($"игрока {id} нет в сети"); });
                    break;
                case "save":
                    host.Post(s =>
                    {
                        int? sender = s.SaveNow();
                        Console.WriteLine(sender == null ? "сохранить нельзя: в сети нет игрока с миром" : $"мир запрошен у игрока {sender}; копия появится через несколько секунд (saves)");
                    });
                    break;
                case "saves":
                    foreach (SaveCopy c in store.List())
                        Console.WriteLine($"  мир {c.WorldId:X16}, тик {c.Tick}, {c.SavedUtc.ToLocalTime():g}, {c.Size} байт");
                    break;
                case "stop":
                case "exit":
                case "quit":
                    stop.Cancel();
                    return;
                default:
                    Console.WriteLine("неизвестная команда. " + Help);
                    break;
            }
        }
    }
}
