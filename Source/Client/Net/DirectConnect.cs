using RWMLWC.Client.Sim;
using RWMLWC.Net;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// Прямое подключение по адресу и отпечатку (T-055): QUIC, а если его нет в системе или он не соединился, — TCP + TLS.
/// </summary>
public static class DirectConnect
{
    public static void Start(string host, int port, string thumbprint, string name)
    {
        LockstepClient.Connect(
            NetTransport.ConnectAsync(host, port, thumbprint, log: s => Log.Message("[RWMLWC] " + s)),
            $"{host}:{port}", name);
    }
}
