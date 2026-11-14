using System;
using System.Threading;
using System.Threading.Tasks;

namespace RWMLWC.Net;

/// <summary>
/// Держит соединение с сервером: при обрыве переподключается с нарастающей паузой.
/// Транспорт не восстанавливает состояние сессии: после <see cref="Connected"/> верхний слой
/// заново шлёт <c>Hello</c>. События приходят из фонового потока.
/// </summary>
public sealed class ReconnectingClient : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IConnection>> _connect;
    private readonly TimeSpan _minDelay;
    private readonly TimeSpan _maxDelay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ReconnectingClient(Func<CancellationToken, Task<IConnection>> connect, TimeSpan? minDelay = null, TimeSpan? maxDelay = null)
    {
        _connect = connect;
        _minDelay = minDelay ?? TimeSpan.FromMilliseconds(500);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Текущее открытое соединение или null.</summary>
    public IConnection? Current { get; private set; }

    /// <summary>Сколько раз соединение было установлено (первое подключение — 1).</summary>
    public int ConnectCount { get; private set; }

    public event Action<IConnection>? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? ConnectFailed;

    public void Start() => _loop ??= Task.Run(() => Loop(_cts.Token));

    private async Task Loop(CancellationToken ct)
    {
        TimeSpan delay = _minDelay;
        while (!ct.IsCancellationRequested)
        {
            IConnection connection;
            try
            {
                connection = await _connect(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                ConnectFailed?.Invoke(e.Message);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxDelay.Ticks));
                continue;
            }

            delay = _minDelay;
            Current = connection;
            ConnectCount++;
            Connected?.Invoke(connection);
            try { await connection.Closed.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            Current = null;
            if (ct.IsCancellationRequested)
            {
                connection.Close("клиент остановлен");
                return;
            }
            Disconnected?.Invoke(connection.CloseReason ?? "?");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        Current?.Close("клиент остановлен");
        _cts.Dispose();
    }
}
