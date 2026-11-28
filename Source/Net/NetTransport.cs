using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RWMLWC.Net;

/// <summary>Слушающая сторона любого транспорта: QUIC или TCP + TLS.</summary>
public interface IConnectionListener : IAsyncDisposable
{
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Название транспорта для журналов: «QUIC», «TCP».</summary>
    string Kind { get; }

    /// <summary>Следующее подключение; неудачные пропускаются, о них сообщает <paramref name="onRejected"/>.</summary>
    Task<IConnection> AcceptAsync(CancellationToken ct = default, Action<string>? onRejected = null);
}

/// <summary>
/// Выбор транспорта (T-055). Клиент пробует QUIC, а если его нет в системе или он не соединился, — TCP + TLS
/// на тот же порт. Сервер слушает оба на одном номере порта (UDP и TCP).
/// Переменная окружения <c>RWMLWC_TRANSPORT</c> = <c>quic</c> или <c>tcp</c> — только этот транспорт (для проверок).
/// </summary>
public static class NetTransport
{
    /// <summary>Сколько ждать QUIC, прежде чем пробовать TCP.</summary>
    public static TimeSpan QuicAttempt = TimeSpan.FromSeconds(5);

    public static string Forced => (Environment.GetEnvironmentVariable("RWMLWC_TRANSPORT") ?? "").Trim().ToLowerInvariant();

    public static async Task<IConnection> ConnectAsync(string host, int port, string certThumbprintSha256, CancellationToken ct = default, Action<string>? log = null)
    {
        string forced = Forced;
        if (forced != "tcp")
        {
            if (QuicTransport.IsSupported)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(QuicAttempt);
                try
                {
                    return await QuicTransport.ConnectAsync(host, port, certThumbprintSha256, attempt.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (!ct.IsCancellationRequested && forced != "quic")
                {
                    log?.Invoke($"QUIC не соединился ({(attempt.IsCancellationRequested ? "нет ответа за " + QuicAttempt.TotalSeconds.ToString("0") + " с" : e.Message)}), пробую TCP");
                }
            }
            else if (forced == "quic")
            {
                throw new PlatformNotSupportedException("QUIC не поддерживается на этой системе");
            }
            else
            {
                log?.Invoke("QUIC не поддерживается на этой системе, подключаюсь по TCP");
            }
        }
        return await TcpTransport.ConnectAsync(host, port, certThumbprintSha256, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Слушать QUIC (если система умеет) и TCP на одном номере порта. Порт 0 — любой свободный, общий для обоих.
    /// Не удалось открыть один из двух — работает второй, причина уходит в <paramref name="log"/>.
    /// </summary>
    public static async Task<IReadOnlyList<IConnectionListener>> ListenAsync(IPEndPoint endPoint, X509Certificate2 certificate, Action<string>? log = null)
    {
        var listeners = new List<IConnectionListener>();
        int port = endPoint.Port;
        string forced = Forced;
        if (forced != "tcp")
        {
            if (QuicTransport.IsSupported)
            {
                QuicServer quic = await QuicServer.StartAsync(endPoint, certificate).ConfigureAwait(false);
                listeners.Add(quic);
                port = quic.LocalEndPoint.Port;
            }
            else
            {
                log?.Invoke("QUIC не поддерживается на этой системе — только TCP");
            }
        }
        if (forced != "quic")
        {
            try
            {
                listeners.Add(TcpServer.Start(new IPEndPoint(endPoint.Address, port), certificate));
            }
            catch (Exception e) when (listeners.Count > 0)
            {
                log?.Invoke($"TCP-порт {port} не открылся ({e.Message}) — только QUIC");
            }
        }
        if (listeners.Count == 0) throw new PlatformNotSupportedException("нет доступного транспорта");
        return listeners;
    }
}
