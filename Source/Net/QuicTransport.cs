using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RWMLWC.Net;

/// <summary>
/// QUIC-транспорт (см. docs/research/io.md, раздел 3). Сервер — с самоподписанным сертификатом,
/// клиент проверяет его по отпечатку SHA-256, который игрок получает вместе с адресом.
/// </summary>
public static class QuicTransport
{
    public static readonly SslApplicationProtocol Alpn = new("rwmlwc/1");

    /// <summary>Нет обмена дольше этого — соединение считается оборванным.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Пустые пакеты для поддержания соединения и быстрого обнаружения обрыва.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(2);

    public static bool IsSupported => QuicConnection.IsSupported && QuicListener.IsSupported;

    public static async Task<IConnection> ConnectAsync(string host, int port, string certThumbprintSha256, CancellationToken ct = default)
    {
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = IPAddress.TryParse(host, out IPAddress? ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port),
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 1,
            IdleTimeout = IdleTimeout,
            KeepAliveInterval = KeepAlive,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { Alpn },
                TargetHost = "rwmlwc",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is X509Certificate2 c2 && string.Equals(ServerCertificate.Thumbprint(c2), certThumbprintSha256, StringComparison.OrdinalIgnoreCase),
            },
        };
        QuicConnection connection = await QuicConnection.ConnectAsync(options, ct).ConfigureAwait(false);
        try
        {
            return await QuicMessageConnection.OpenAsClientAsync(connection, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Слушающая сторона QUIC.</summary>
public sealed class QuicServer : IAsyncDisposable
{
    private readonly QuicListener _listener;

    private QuicServer(QuicListener listener) => _listener = listener;

    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    public static async Task<QuicServer> StartAsync(IPEndPoint endPoint, X509Certificate2 certificate)
    {
        var alpn = new List<SslApplicationProtocol> { QuicTransport.Alpn };
        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = endPoint,
            ApplicationProtocols = alpn,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = QuicTransport.IdleTimeout,
                KeepAliveInterval = QuicTransport.KeepAlive,
                MaxInboundBidirectionalStreams = 2,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = alpn,
                    ServerCertificate = certificate,
                },
            }),
        }).ConfigureAwait(false);
        return new QuicServer(listener);
    }

    /// <summary>
    /// Следующее подключение, уже с открытыми потоками. Неудачные подключения
    /// (чужой клиент, обрыв на рукопожатии) пропускаются, о них сообщает <paramref name="onRejected"/>.
    /// </summary>
    public async Task<IConnection> AcceptAsync(CancellationToken ct = default, Action<string>? onRejected = null)
    {
        while (true)
        {
            QuicConnection connection;
            try
            {
                connection = await _listener.AcceptConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is QuicException or System.Security.Authentication.AuthenticationException)
            {
                onRejected?.Invoke(e.Message);
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QuicTransport.IdleTimeout);
            try
            {
                return await QuicMessageConnection.AcceptAsServerAsync(connection, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                onRejected?.Invoke($"{connection.RemoteEndPoint}: {e.Message}");
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => _listener.DisposeAsync();
}

/// <summary>Самоподписанный сертификат сервера. Хранится в файле, чтобы отпечаток не менялся между запусками.</summary>
public static class ServerCertificate
{
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (!File.Exists(pfxPath))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=rwmlwc", key, HashAlgorithmName.SHA256);
            using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            string? dir = Path.GetDirectoryName(Path.GetFullPath(pfxPath));
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pfx));
        }
        // Повторная загрузка из PFX нужна на Windows: TLS не работает с ключом, созданным в памяти.
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
    }

    /// <summary>Отпечаток SHA-256 в шестнадцатеричном виде — его игрок передаёт другу вместе с адресом.</summary>
    public static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
}
