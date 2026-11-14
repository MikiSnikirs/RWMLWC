using RWMLWC.Shared.Protocol;

namespace RWMLWC.Net;

/// <summary>
/// Пара соединений в памяти для тестов сервера и клиента без сети.
/// Сообщения проходят через кодек, как по сети, поэтому ошибки сериализации ловятся и здесь.
/// </summary>
public sealed class InMemoryConnection : ConnectionBase
{
    private InMemoryConnection? _peer;
    private readonly string _name;

    private InMemoryConnection(string name) => _name = name;

    public override string Remote => "mem:" + (_peer?._name ?? "?");

    public static (InMemoryConnection a, InMemoryConnection b) CreatePair(string nameA = "a", string nameB = "b")
    {
        var a = new InMemoryConnection(nameA);
        var b = new InMemoryConnection(nameB);
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public override void Send(Message message)
    {
        if (!IsOpen || _peer == null || !_peer.IsOpen) return;
        byte[] body = ProtocolCodec.EncodeBody(message);
        _peer.Deliver(ProtocolCodec.DecodeBody(body));
    }

    protected override void OnClose(string reason) => _peer?.Close("удалённая сторона: " + reason);
}
