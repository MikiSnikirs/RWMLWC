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
        // Синхронное ожидание допустимо: ListenAsync завершается сразу, сети не ждёт.
        _quic = QuicServer.StartAsync(new IPEndPoint(IPAddress.Loopback, port), cert).GetAwaiter().GetResult();
        Port = _quic.LocalEndPoint.Port;
        Thumbprint = ServerCertificate.Thumbprint(cert);

        var options = new SessionOptions
        {
            StartTick = startTick,
            SessionSeed = seed ?? (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
            Store = new SaveStore(DataDir),
        };
        _host = new SessionHost(new Session(options, Write));
        _cts = new CancellationTokenSource();
        _ = _host.RunAsync(_quic, _cts.Token, Write);
        Write($"сессия запущена: тик {startTick}, seed {options.SessionSeed}, QUIC 127.0.0.1:{Port}");

        if (steam && SteamNet.Available)
        {
            SteamNet.Accepted += OnSteamAccepted;
            SteamNet.Listen();
            Write($"Steam: принимаю друзей, мой SteamID {SteamNet.MySteamId}");
        }
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
    private static void HostHere()
    {
        if (HostedSession.Running)
        {
            Messages.Message("RWMLWC: сессия уже запущена", MessageTypeDefOf.RejectInput, false);
            return;
        }
        HostedSession.Start(Find.TickManager.TicksGame);
        RWMLWC.Client.Sim.LockstepClient.Connect("127.0.0.1", HostedSession.Port, HostedSession.Thumbprint, SteamNet.Available ? "host-" + SteamNet.MySteamId : "host");
        string id = SteamNet.Available ? SteamNet.MySteamId.ToString() : "(Steam недоступен)";
        if (SteamNet.Available) UnityEngine.GUIUtility.systemCopyBuffer = id;
        Messages.Message($"RWMLWC: сессия запущена. Другу — SteamID {id} (скопирован в буфер обмена)", MessageTypeDefOf.PositiveEvent, false);
    }

    [DebugAction("RWMLWC", "Stop hosted session", allowedGameStates = AllowedGameStates.Playing)]
    private static void StopHost()
    {
        RWMLWC.Client.Sim.LockstepClient.Disconnect("хост остановил сессию");
        HostedSession.Stop();
    }
}
