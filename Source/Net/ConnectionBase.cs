using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Net;

/// <summary>Общая часть соединений: входящая очередь и состояние закрытия.</summary>
public abstract class ConnectionBase : IConnection
{
    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _closeReason;

    public abstract string Remote { get; }
    public bool IsOpen => _closeReason == null;
    public string? CloseReason => _closeReason;
    public Task Closed => _closed.Task;

    public abstract void Send(Message message);

    public bool TryReceive(out Message message) => _inbox.Reader.TryRead(out message!);

    public async ValueTask<Message> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            return await _inbox.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ConnectionClosedException(_closeReason ?? "закрыто");
        }
    }

    public void Close(string reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, reason, null) != null) return;
        // Уже полученные сообщения остаются доступны для чтения.
        _inbox.Writer.TryComplete();
        OnClose(reason);
        _closed.TrySetResult();
    }

    private int _roundTripMs = -1;

    /// <summary>Последнее измеренное время туда-обратно в мс (по <see cref="SendPing"/>); -1 — ещё не измерено.</summary>
    public int RoundTripMs => Volatile.Read(ref _roundTripMs);

    /// <summary>Отправить проверку связи. Ответ обработает само соединение и обновит <see cref="RoundTripMs"/>.</summary>
    public void SendPing() => Send(new PingMessage { Timestamp = (ulong)Stopwatch.GetTimestamp() });

    /// <summary>
    /// Входящее сообщение от транспорта. <c>Ping</c> и <c>Pong</c> обрабатываются здесь, в потоке приёма,
    /// и до приложения не доходят: так замер не зависит от того, как часто приложение читает очередь.
    /// </summary>
    protected void Deliver(Message message)
    {
        switch (message)
        {
            case PingMessage ping:
                Send(new PongMessage { Timestamp = ping.Timestamp });
                return;
            case PongMessage pong:
                long elapsed = Stopwatch.GetTimestamp() - (long)pong.Timestamp;
                if (elapsed >= 0)
                    Volatile.Write(ref _roundTripMs, (int)Math.Min(int.MaxValue, elapsed * 1000 / Stopwatch.Frequency));
                return;
        }
        _inbox.Writer.TryWrite(message);
    }

    /// <summary>Освобождение ресурсов транспорта. Вызывается один раз.</summary>
    protected abstract void OnClose(string reason);

    public virtual ValueTask DisposeAsync()
    {
        Close("закрыто локально");
        return ValueTask.CompletedTask;
    }
}
