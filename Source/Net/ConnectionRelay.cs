using System;
using System.Threading;
using System.Threading.Tasks;

namespace RWMLWC.Net;

/// <summary>
/// Перекладывает сообщения между двумя соединениями в обе стороны, пока одно из них не закроется;
/// тогда закрывает второе. Основа Steam-моста: соединение друга через Steam ↔ локальный сервер по QUIC.
/// </summary>
public static class ConnectionRelay
{
    public static async Task RunAsync(IConnection a, IConnection b, CancellationToken ct = default)
    {
        Task ab = Pump(a, b, ct);
        Task ba = Pump(b, a, ct);
        await Task.WhenAny(ab, ba, a.Closed, b.Closed).ConfigureAwait(false);
        string reason = a.CloseReason ?? b.CloseReason ?? "мост остановлен";
        a.Close("мост: " + reason);
        b.Close("мост: " + reason);
        try { await Task.WhenAll(ab, ba).ConfigureAwait(false); } catch { }
    }

    private static async Task Pump(IConnection from, IConnection to, CancellationToken ct)
    {
        try
        {
            while (true)
                to.Send(await from.ReceiveAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is ConnectionClosedException or OperationCanceledException)
        {
        }
    }
}
