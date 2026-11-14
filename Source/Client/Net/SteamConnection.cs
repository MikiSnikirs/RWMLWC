using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Shared.Protocol;
using Steamworks;

namespace RWMLWC.Client.Net;

/// <summary>
/// Соединение поверх Steam Networking Sockets (P2P через релей Valve). Одно сообщение Steam —
/// одно сообщение протокола (тип + тело, без префикса длины), все надёжные и упорядоченные.
/// Приём — из фонового потока <see cref="SteamNet"/>, состояние — из обратного вызова Steam.
/// </summary>
public sealed class SteamConnection : ConnectionBase
{
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _remote;
    // Своя очередь отправки: когда буфер Steam полон (k_EResultLimitExceeded), сообщение ждёт
    // следующего прохода потока приёма, а не теряется.
    private readonly ConcurrentQueue<byte[]> _outbox = new();

    internal SteamConnection(HSteamNetConnection handle, string remote)
    {
        Handle = handle;
        _remote = remote;
    }

    internal HSteamNetConnection Handle { get; }

    public override string Remote => _remote;

    /// <summary>Завершается, когда Steam перевёл соединение в состояние «соединено».</summary>
    public Task Connected => _connected.Task;

    public override void Send(Message message)
    {
        if (!IsOpen) return;
        byte[] body = ProtocolCodec.EncodeBody(message);
        if (body.Length > Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend)
        {
            Close($"сообщение {message.Type} ({body.Length} байт) больше предела Steam");
            return;
        }
        _outbox.Enqueue(body);
        SteamNet.WakePoll();
    }

    /// <summary>Отдать Steam очередь отправки, пока он принимает. Вызывается только потоком <see cref="SteamNet"/>.</summary>
    internal int Flush()
    {
        int sent = 0;
        while (IsOpen && _outbox.TryPeek(out byte[]? body))
        {
            GCHandle pin = GCHandle.Alloc(body, GCHandleType.Pinned);
            EResult r;
            try
            {
                // Без Nagle: команды и тики уходят сразу, а не копятся до 5 мс.
                r = SteamNetworkingSockets.SendMessageToConnection(Handle, pin.AddrOfPinnedObject(), (uint)body.Length,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle, out _);
            }
            finally
            {
                pin.Free();
            }
            if (r == EResult.k_EResultLimitExceeded) break; // буфер полон — повтор на следующем проходе
            if (r != EResult.k_EResultOK)
            {
                Close("Steam: отправка не удалась: " + r);
                break;
            }
            _outbox.TryDequeue(out _);
            sent++;
        }
        return sent;
    }

    /// <summary>Забрать пришедшие сообщения. Возвращает число прочитанных.</summary>
    internal int Poll(nint[] scratch)
    {
        if (!IsOpen) return 0;
        int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(Handle, scratch, scratch.Length);
        for (int i = 0; i < n; i++)
        {
            byte[] data;
            try
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(scratch[i]);
                data = new byte[msg.m_cbSize];
                Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
            }
            finally
            {
                SteamNetworkingMessage_t.Release(scratch[i]);
            }
            try
            {
                Deliver(ProtocolCodec.DecodeBody(data));
            }
            catch (ProtocolException e)
            {
                Close("ошибка протокола: " + e.Message);
                // Остальные сообщения пакета освобождаем без разбора.
                for (int j = i + 1; j < n; j++) SteamNetworkingMessage_t.Release(scratch[j]);
                return n;
            }
        }
        return Math.Max(n, 0);
    }

    internal void OnState(ESteamNetworkingConnectionState state, string endDebug)
    {
        switch (state)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                _connected.TrySetResult();
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                Close("удалённая сторона закрыла: " + endDebug);
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                Close("Steam: проблема со связью: " + endDebug);
                break;
        }
    }

    protected override void OnClose(string reason)
    {
        _connected.TrySetException(new ConnectionClosedException(reason));
        SteamNet.Forget(this);
        SteamNetworkingSockets.CloseConnection(Handle, 0, reason.Length > 120 ? reason[..120] : reason, true);
    }
}
