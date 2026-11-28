using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;

namespace RWMLWC.Server;

/// <summary>
/// Запускает <see cref="Session"/> поверх сети: принимает подключения (QUIC, TCP) и раз в <see cref="Interval"/>
/// вызывает <c>Pump</c> и <c>Advance</c>. Вся работа с сессией идёт в одном потоке цикла.
/// </summary>
public sealed class SessionHost
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    private readonly Session _session;
    private readonly ConcurrentQueue<IConnection> _incoming = new();

    public SessionHost(Session session) => _session = session;

    public Session Session => _session;

    /// <summary>Подключение из любого потока (принимающий цикл, Steam-мост в процессе и т. п.).</summary>
    public void Enqueue(IConnection connection) => _incoming.Enqueue(connection);

    private readonly ConcurrentQueue<Action<Session>> _actions = new();

    /// <summary>Выполнить действие над сессией в потоке цикла (команды консоли и т. п.).</summary>
    public void Post(Action<Session> action) => _actions.Enqueue(action);

    public Task RunAsync(IConnectionListener? server, CancellationToken ct, Action<string>? log = null) =>
        RunAsync(server == null ? Array.Empty<IConnectionListener>() : new[] { server }, ct, log);

    public async Task RunAsync(IReadOnlyList<IConnectionListener> servers, CancellationToken ct, Action<string>? log = null)
    {
        foreach (IConnectionListener server in servers) _ = AcceptLoop(server, ct, log);
        var clock = Stopwatch.StartNew();
        TimeSpan last = TimeSpan.Zero;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                while (_incoming.TryDequeue(out IConnection? c)) _session.Accept(c);
                while (_actions.TryDequeue(out Action<Session>? a)) a(_session);
                _session.Pump();
                TimeSpan now = clock.Elapsed;
                _session.Advance(now - last);
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptLoop(IConnectionListener server, CancellationToken ct, Action<string>? log)
    {
        try
        {
            while (true)
                Enqueue(await server.AcceptAsync(ct, r => log?.Invoke("отклонено подключение: " + r)).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
