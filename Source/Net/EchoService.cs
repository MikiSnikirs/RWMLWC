using System;
using System.Threading;
using System.Threading.Tasks;

namespace RWMLWC.Net;

/// <summary>
/// Сервер-эхо для проверки транспорта (TST-012, TST-016): каждое сообщение возвращается отправителю.
/// Используется консольным сервером (<c>--echo</c>) и тестом Steam-моста внутри игры.
/// </summary>
public static class EchoService
{
    public static async Task RunAsync(QuicServer server, CancellationToken ct, Action<string>? log = null)
    {
        try
        {
            while (true)
            {
                IConnection connection = await server.AcceptAsync(ct, reason => log?.Invoke("REJECTED " + reason)).ConfigureAwait(false);
                log?.Invoke("CONNECTED " + connection.Remote);
                _ = EchoAsync(connection, ct, log);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task EchoAsync(IConnection connection, CancellationToken ct, Action<string>? log)
    {
        try
        {
            while (true)
                connection.Send(await connection.ReceiveAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is ConnectionClosedException or OperationCanceledException)
        {
        }
        log?.Invoke("DISCONNECTED " + connection.Remote + ": " + connection.CloseReason);
    }
}
