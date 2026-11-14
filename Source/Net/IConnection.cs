using System;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Net;

/// <summary>
/// Соединение, по которому ходят сообщения протокола. Транспорт (QUIC, память, позже Steam)
/// скрыт за этим интерфейсом. Все методы потокобезопасны.
/// </summary>
public interface IConnection : IAsyncDisposable
{
    /// <summary>Описание удалённой стороны для логов.</summary>
    string Remote { get; }

    bool IsOpen { get; }

    /// <summary>Причина закрытия; null, пока соединение открыто.</summary>
    string? CloseReason { get; }

    /// <summary>Завершается, когда соединение закрыто (по любой причине).</summary>
    Task Closed { get; }

    /// <summary>Ставит сообщение в очередь отправки. После закрытия молча игнорируется.</summary>
    void Send(Message message);

    /// <summary>Неблокирующее чтение — для главного потока игры.</summary>
    bool TryReceive(out Message message);

    /// <summary>Ожидание следующего сообщения. Бросает <see cref="ConnectionClosedException"/> после закрытия.</summary>
    ValueTask<Message> ReceiveAsync(CancellationToken ct = default);

    void Close(string reason);

    /// <summary>Отправить проверку связи (<c>Ping</c>); ответ соединение обработает само.</summary>
    void SendPing();

    /// <summary>Последнее измеренное время туда-обратно в мс; -1 — ещё не измерено.</summary>
    int RoundTripMs { get; }
}

public sealed class ConnectionClosedException : Exception
{
    public ConnectionClosedException(string reason) : base("Соединение закрыто: " + reason) { }
}
