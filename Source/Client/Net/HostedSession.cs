using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using LudeonTK;
using RimWorld;
using RWMLWC.Server;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Сессия сервера внутри игры хоста (T-019): отдельный консольный сервер не нужен.
/// <list type="bullet">
/// <item>QUIC слушает только 127.0.0.1 — через него к сессии подключается сама игра хоста (<c>LockstepClient</c>).</item>
/// <item>Друзья приходят через Steam: входящее Steam-соединение отдаётся сессии напрямую, без промежуточного QUIC.</item>
/// </list>
/// Сессия работает в своём фоновом цикле (<see cref="SessionHost"/>), с игрой общается только через соединения.
/// </summary>
public static class HostedSession
{
    private static SessionHost? _host;
    private static QuicServer? _quic;
    private static CancellationTokenSource? _cts;
    private static readonly object LogGate = new();

    public static bool Running => _host != null;
    public static int Port { get; private set; }
    public static string Thumbprint { get; private set; } = "";
    public static Session? Session => _host?.Session;

    /// <summary>Порт прямого подключения из host.txt; 0 — только Steam.</summary>
    public static int DirectPort { get; private set; }

    /// <summary>Код прямого подключения (адрес в локальной сети); пусто — прямое подключение выключено.</summary>
    public static string DirectCode { get; private set; } = "";

    public static string SettingsPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "host.txt");

    private const string SettingsTemplate =
        "# Настройки сессии в игре хоста (RWMLWC).\r\n" +
        "# Строки «ключ = значение»; строки с # — комментарии. Действуют при следующем открытии сессии.\r\n" +
        "\r\n" +
        "# Друзья всегда могут войти через Steam. Чтобы пускать и по коду подключения (без Steam, в локальной сети\r\n" +
        "# или через интернет с пробросом UDP-порта на роутере), укажите порт, например 47801. 0 — только Steam.\r\n" +
        "direct_port = 0\r\n" +
        "\r\n" +
        "# Сколько игроков одновременно.\r\n" +
        "max_players = 8\r\n";

    private static string LocalAddress()
    {
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    || ni.GetIPProperties().GatewayAddresses.Count == 0) continue; // без шлюза — виртуальные адаптеры
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return a.Address.ToString();
            }
        }
        catch (Exception)
        {
        }
        return "127.0.0.1";
    }

    /// <summary>Папка сервера в данных игры: сертификат, копии мира, лог.</summary>
    public static string DataDir => Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "server");

    /// <summary>
    /// Запустить сессию. Вызывать из главного потока (слушающий Steam-сокет открывается здесь).
    /// <paramref name="port"/> 0 — любой свободный.
    /// </summary>
    public static void Start(int startTick, int port = 0, bool steam = true, uint? seed = null)
    {
        if (Running) throw new InvalidOperationException("сессия уже запущена");
        Directory.CreateDirectory(DataDir);
        var cert = ServerCertificate.LoadOrCreate(Path.Combine(DataDir, "server.pfx"));
        // Настройки хоста — RWMLWC/host.txt (T-065). direct_port > 0 — принимать и прямые подключения по коду.
        SettingsFile settings = SettingsFile.LoadOrCreate(SettingsPath, SettingsTemplate, w => Write("ВНИМАНИЕ: " + w));
        int directPort = settings.GetInt("direct_port", 0, 0, 65535);
        int maxPlayers = settings.GetInt("max_players", 8, 1, 64);
        DirectPort = directPort;
        IPEndPoint bind = directPort > 0 ? new IPEndPoint(IPAddress.Any, directPort) : new IPEndPoint(IPAddress.Loopback, port);
        // Синхронное ожидание допустимо: ListenAsync завершается сразу, сети не ждёт.
        _quic = QuicServer.StartAsync(bind, cert).GetAwaiter().GetResult();
        Port = _quic.LocalEndPoint.Port;
        Thumbprint = ServerCertificate.Thumbprint(cert);
        DirectCode = directPort > 0 ? ConnectionCode.Format(LocalAddress(), Port, Thumbprint) : "";

        var options = new SessionOptions
        {
            StartTick = startTick,
            SessionSeed = seed ?? (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
            Store = new SaveStore(DataDir),
            MaxPlayers = maxPlayers,
            MaxLeadTicks = settings.GetInt("max_lead_ticks", 180, 0, 100000),
        };
        _host = new SessionHost(new Session(options, Write));
        _cts = new CancellationTokenSource();
        _ = _host.RunAsync(_quic, _cts.Token, Write);
        Write($"сессия запущена: тик {startTick}, seed {options.SessionSeed}, QUIC {bind.Address}:{Port}");
        if (DirectCode.Length > 0) Write("прямое подключение (локальная сеть; через интернет — проброс UDP-порта " + Port + "): " + DirectCode);

        if (steam && SteamNet.Available)
        {
            SteamNet.Accepted += OnSteamAccepted;
            SteamNet.Listen();
            Write($"Steam: принимаю друзей, мой SteamID {SteamNet.MySteamId}");
        }
    }

    /// <summary>
    /// Открыть сессию в загруженной игре и войти в неё самому (кнопка меню и отладочное действие).
    /// Главный поток, игра загружена, сетевой игры ещё нет.
    /// </summary>
    public static void HostAndJoin()
    {
        if (Running || RWMLWC.Client.Sim.LockstepClient.CurrentState != RWMLWC.Client.Sim.LockstepClient.State.Off)
        {
            Messages.Message("RWMLWC: сетевая игра уже идёт", MessageTypeDefOf.RejectInput, false);
            return;
        }
        Start(Find.TickManager.TicksGame);
        RWMLWC.Client.Sim.LockstepClient.Connect("127.0.0.1", Port, Thumbprint, SteamNet.Available ? Steamworks.SteamFriends.GetPersonaName() : "host");
        string id = SteamNet.Available ? SteamNet.MySteamId.ToString() : "(Steam недоступен)";
        if (SteamNet.Available)
        {
            UnityEngine.GUIUtility.systemCopyBuffer = id;
            SteamInvite.AdvertiseHost();
        }
        Messages.Message($"RWMLWC: сессия открыта. Друг может нажать «Присоединиться» в списке друзей Steam или ввести SteamID {id} (скопирован в буфер обмена)"
            + (DirectCode.Length > 0 ? ". Прямое подключение — код в окне «Игроки и чат»" : ""), MessageTypeDefOf.PositiveEvent, false);
    }

    /// <summary>Выйти из сессии и закрыть её (хост).</summary>
    public static void StopAndLeave()
    {
        RWMLWC.Client.Sim.LockstepClient.Disconnect("хост остановил сессию");
        Stop();
        SteamInvite.Clear();
    }

    private static void OnSteamAccepted(SteamConnection friend)
    {
        Write("Steam: подключился " + friend.Remote);
        _host?.Enqueue(friend);
    }

    public static void Stop()
    {
        if (!Running) return;
        SteamNet.Accepted -= OnSteamAccepted;
        SteamNet.StopListening();
        _cts?.Cancel();
        try { _quic?.DisposeAsync().AsTask().Wait(2000); } catch { }
        _host = null;
        _quic = null;
        Write("сессия остановлена");
    }

    private static void Write(string line)
    {
        Log.Message("[RWMLWC][host] " + line);
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "session-log.txt"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Лог в файл — удобство, не должен ронять сессию.
        }
    }
}

/// <summary>Отладочные действия (режим разработчика) для хостинга. Полноценный интерфейс — позже.</summary>
internal static class HostedSessionDebug
{
    [DebugAction("RWMLWC", "Host session here (Steam)", allowedGameStates = AllowedGameStates.Playing)]
    private static void HostHere() => HostedSession.HostAndJoin();

    [DebugAction("RWMLWC", "Stop hosted session", allowedGameStates = AllowedGameStates.Playing)]
    private static void StopHost() => HostedSession.StopAndLeave();
}
