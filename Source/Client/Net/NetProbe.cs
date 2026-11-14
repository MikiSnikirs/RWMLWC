#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// ВРЕМЕННАЯ проверка для T-004: работает ли QUIC и Steam внутри рантайма игры.
/// Удаляется до слияния ветки.
/// </summary>
[StaticConstructorOnStartup]
internal static class NetProbe
{
    static NetProbe()
    {
        Log.Message($"[RWMLWC][T-004] Steam: Initialized={Verse.Steam.SteamManager.Initialized}, Active={Verse.Steam.SteamManager.Active}");
        Log.Message($"[RWMLWC][T-004] QUIC: runtime {Environment.Version}, QuicListener.IsSupported={QuicListener.IsSupported}, QuicConnection.IsSupported={QuicConnection.IsSupported}");
        if (!QuicListener.IsSupported) return;
        Task.Run(async () =>
        {
            try { Log.Message("[RWMLWC][T-004] QUIC loopback: " + await Loopback()); }
            catch (Exception e) { Log.Warning("[RWMLWC][T-004] QUIC loopback FAILED: " + e); }
        });
    }

    private static async Task<string> Loopback()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=rwmlwc-probe", key, HashAlgorithmName.SHA256);
        using var tmp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pfx), null);
        var alpn = new List<SslApplicationProtocol> { new("rwmlwc") };

        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = alpn,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = alpn, ServerCertificate = cert },
            }),
        });
        var server = Task.Run(async () =>
        {
            await using var conn = await listener.AcceptConnectionAsync();
            await using var s = await conn.AcceptInboundStreamAsync();
            var buf = new byte[64];
            int n = await s.ReadAsync(buf);
            await s.WriteAsync(buf.AsMemory(0, n), completeWrites: true);
            await Task.Delay(200);
        });

        string thumb = cert.GetCertHashString(HashAlgorithmName.SHA256);
        var sw = Stopwatch.StartNew();
        await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            DefaultStreamErrorCode = 1,
            DefaultCloseErrorCode = 1,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = alpn,
                TargetHost = "rwmlwc-probe",
                RemoteCertificateValidationCallback = (_, c, _, _) => c is X509Certificate2 c2 && c2.GetCertHashString(HashAlgorithmName.SHA256) == thumb,
            },
        });
        long connectMs = sw.ElapsedMilliseconds;
        await using var cs = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        await cs.WriteAsync("ping"u8.ToArray());
        var rb = new byte[64];
        int rn = await cs.ReadAsync(rb);
        await server;
        return $"OK, connect {connectMs} ms, echo '{System.Text.Encoding.UTF8.GetString(rb, 0, rn)}'";
    }
}
