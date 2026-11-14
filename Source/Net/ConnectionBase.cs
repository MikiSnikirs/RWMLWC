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

    protected void Deliver(Message message) => _inbox.Writer.TryWrite(message);

    /// <summary>Освобождение ресурсов транспорта. Вызывается один раз.</summary>
    protected abstract void OnClose(string reason);

    public virtual ValueTask DisposeAsync()
    {
        Close("закрыто локально");
        return ValueTask.CompletedTask;
    }
}
