using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Net;

/// <summary>
/// Запасной транспорт TCP + TLS (T-055) для систем без QUIC (Windows 10 и старше).
/// Тот же сертификат и та же проверка по отпечатку SHA-256, что у QUIC; порт тот же, но TCP.
/// Один поток вместо двух: куски снимков идут по одному между управляющими сообщениями,
/// поэтому команды и тики ждут не дольше одного куска.
/// </summary>
public static class TcpTransport
{
    public static async Task<IConnection> ConnectAsync(string host, int port, string certThumbprintSha256, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QuicTransport.IdleTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { QuicTransport.Alpn },
                TargetHost = "rwmlwc",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c2 && string.Equals(ServerCertificate.Thumbprint(c2), certThumbprintSha256, StringComparison.OrdinalIgnoreCase),
            }, timeout.Token).ConfigureAwait(false);
            return new TcpMessageConnection(client, ssl);
        }
        catch
        {
            ssl?.Dispose();
            client.Dispose();
            throw;
        }
    }
}

/// <summary>Слушающая сторона TCP + TLS. Рукопожатия идут параллельно: медленный клиент не держит остальных.</summary>
public sealed class TcpServer : IConnectionListener
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(IConnection? connection, string? rejected)> _accepted =
        Channel.CreateUnbounded<(IConnection?, string?)>(new UnboundedChannelOptions { SingleReader = false });

    private TcpServer(TcpListener listener, X509Certificate2 certificate)
    {
        _listener = listener;
        _certificate = certificate;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public string Kind => "TCP";

    public static TcpServer Start(IPEndPoint endPoint, X509Certificate2 certificate)
    {
        var listener = new TcpListener(endPoint);
        listener.Start();
        var server = new TcpServer(listener, certificate);
        _ = server.AcceptLoop();
        return server;
    }

    private async Task AcceptLoop()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException e)
            {
                _accepted.Writer.TryWrite((null, e.Message));
                continue;
            }
            _ = Handshake(client, ct);
        }
        _accepted.Writer.TryComplete();
    }

    private async Task Handshake(TcpClient client, CancellationToken ct)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        SslStream? ssl = null;
        try
        {
            client.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QuicTransport.IdleTimeout);
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { QuicTransport.Alpn },
                ServerCertificate = _certificate,
            }, timeout.Token).ConfigureAwait(false);
            if (ssl.NegotiatedApplicationProtocol != QuicTransport.Alpn)
                throw new ProtocolException("клиент не назвал протокол rwmlwc");
            if (!_accepted.Writer.TryWrite((new TcpMessageConnection(client, ssl), null)))
            {
                ssl.Dispose();
                client.Dispose();
            }
        }
        catch (Exception e)
        {
            ssl?.Dispose();
            client.Dispose();
            if (!ct.IsCancellationRequested) _accepted.Writer.TryWrite((null, $"tcp:{remote}: {e.Message}"));
        }
    }

    public async Task<IConnection> AcceptAsync(CancellationToken ct = default, Action<string>? onRejected = null)
    {
        while (true)
        {
            (IConnection? connection, string? rejected) item;
            try
            {
                item = await _accepted.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new OperationCanceledException("TCP-сервер остановлен");
            }
            if (item.connection != null) return item.connection;
            onRejected?.Invoke(item.rejected ?? "?");
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        while (_accepted.Reader.TryRead(out var item)) item.connection?.Close("сервер остановлен");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Соединение поверх TLS-потока TCP. Обрыв замечается по тишине: обе стороны шлют Ping раз в 2 с.</summary>
public sealed class TcpMessageConnection : ConnectionBase
{
    private readonly TcpClient _client;
    private readonly SslStream _ssl;
    private readonly ConcurrentQueue<byte[]> _controlOut = new();
    private readonly ConcurrentQueue<byte[]> _bulkOut = new();
    private readonly SemaphoreSlim _outSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _remote;
    private long _lastReceive = Stopwatch.GetTimestamp();

    internal TcpMessageConnection(TcpClient client, SslStream ssl)
    {
        _client = client;
        _ssl = ssl;
        _remote = "tcp:" + client.Client.RemoteEndPoint;
        _ = Pump(SendLoop);
        _ = Pump(ReceiveLoop);
        _ = Pump(KeepAliveLoop);
    }

    public override string Remote => _remote;

    public override void Send(Message message)
    {
        if (!IsOpen) return;
        byte[] frame = ProtocolCodec.EncodeFrame(message);
        (message.Type == MessageType.SnapshotChunk ? _bulkOut : _controlOut).Enqueue(frame);
        _outSignal.Release();
    }

    private async Task Pump(Func<Task> loop)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ProtocolException e)
        {
            Close("ошибка протокола: " + e.Message);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Close("TCP: " + (e.InnerException?.Message ?? e.Message));
        }
        catch (Exception e)
        {
            Close(e.GetType().Name + ": " + e.Message);
        }
    }

    // Каждое сообщение добавляет одну единицу сигнала, а проход отправляет все управляющие и не больше одного куска,
    // так что ни одно сообщение не застревает, а снимок не задерживает команды дольше одного куска.
    private async Task SendLoop()
    {
        CancellationToken ct = _cts.Token;
        while (true)
        {
            await _outSignal.WaitAsync(ct).ConfigureAwait(false);
            bool wrote = false;
            while (_controlOut.TryDequeue(out byte[]? frame))
            {
                await _ssl.WriteAsync(frame, ct).ConfigureAwait(false);
                wrote = true;
            }
            if (_bulkOut.TryDequeue(out byte[]? chunk))
            {
                await _ssl.WriteAsync(chunk, ct).ConfigureAwait(false);
                wrote = true;
            }
            if (wrote) await _ssl.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoop()
    {
        CancellationToken ct = _cts.Token;
        byte[] buffer = new byte[64 * 1024];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= ProtocolCodec.LengthPrefixBytes + ProtocolLimits.MaxFrameBytes)
                    throw new ProtocolException("Кадр больше допустимого");
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, ProtocolCodec.LengthPrefixBytes + ProtocolLimits.MaxFrameBytes));
            }
            int read = await _ssl.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                Close("удалённая сторона закрыла соединение");
                return;
            }
            Volatile.Write(ref _lastReceive, Stopwatch.GetTimestamp());
            length += read;

            int offset = 0;
            while (ProtocolCodec.TryDecodeFrame(buffer.AsSpan(offset, length - offset), out Message? message, out int consumed))
            {
                Deliver(message!);
                offset += consumed;
            }
            if (offset > 0)
            {
                Buffer.BlockCopy(buffer, offset, buffer, 0, length - offset);
                length -= offset;
            }
        }
    }

    private async Task KeepAliveLoop()
    {
        CancellationToken ct = _cts.Token;
        using var timer = new PeriodicTimer(QuicTransport.KeepAlive);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            long silent = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastReceive);
            if (silent > QuicTransport.IdleTimeout.TotalSeconds * Stopwatch.Frequency)
            {
                Close($"нет данных дольше {QuicTransport.IdleTimeout.TotalSeconds:0} с");
                return;
            }
            SendPing();
        }
    }

    protected override void OnClose(string reason)
    {
        _cts.Cancel();
        try { _ssl.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
