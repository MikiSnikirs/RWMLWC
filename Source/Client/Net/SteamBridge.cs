using System;
using System.Threading.Tasks;
using RWMLWC.Net;

namespace RWMLWC.Client.Net;

/// <summary>
/// Steam-мост хоста (T-016): каждое входящее Steam-подключение друга соединяется
/// по QUIC с локальным сервером, сообщения перекладываются в обе стороны.
/// Сервер о Steam ничего не знает — для него друг выглядит как обычный QUIC-клиент.
/// </summary>
public sealed class SteamBridge : IDisposable
{
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly string _serverThumbprint;
    private readonly Action<string> _log;

    public SteamBridge(string serverHost, int serverPort, string serverThumbprint, Action<string> log)
    {
        _serverHost = serverHost;
        _serverPort = serverPort;
        _serverThumbprint = serverThumbprint;
        _log = log;
    }

    /// <summary>Открыть слушающий Steam-сокет. Вызывать из главного потока.</summary>
    public void Start()
    {
        SteamNet.Accepted += OnAccepted;
        SteamNet.Listen();
        _log($"Steam-мост: слушаю, мой SteamID {SteamNet.MySteamId}, сервер {_serverHost}:{_serverPort}");
    }

    /// <summary>Подключить уже установленное соединение (например, пару для самопроверки).</summary>
    public Task AttachAsync(IConnection friend) => RelayAsync(friend);

    private void OnAccepted(SteamConnection friend) => _ = RelayAsync(friend);

    private async Task RelayAsync(IConnection friend)
    {
        _log("Steam-мост: подключился " + friend.Remote);
        IConnection server;
        try
        {
            server = await QuicTransport.ConnectAsync(_serverHost, _serverPort, _serverThumbprint).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log("Steam-мост: сервер недоступен: " + e.Message);
            friend.Close("сервер недоступен");
            return;
        }
        await ConnectionRelay.RunAsync(friend, server).ConfigureAwait(false);
        _log($"Steam-мост: {friend.Remote} отключён: {friend.CloseReason}");
    }

    public void Dispose()
    {
        SteamNet.Accepted -= OnAccepted;
        SteamNet.StopListening();
    }
}
