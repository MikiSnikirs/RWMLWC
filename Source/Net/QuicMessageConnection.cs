using System;
using System.Buffers.Binary;
using System.Net.Quic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Net;

/// <summary>
/// Соединение поверх QUIC. Два двунаправленных потока, оба открывает клиент:
/// «управление» (рукопожатие, команды, тики, хеши) и «объёмный» (куски снимков),
/// чтобы снимок карты не задерживал команды и тики. Внутри каждого потока порядок сохраняется.
/// </summary>
public sealed class QuicMessageConnection : ConnectionBase
{
    private const byte ControlTag = 1;
    private const byte BulkTag = 2;

    private readonly QuicConnection _connection;
    private readonly QuicStream _control;
    private readonly QuicStream _bulk;
    private readonly Channel<byte[]> _controlOut = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _bulkOut = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly string _remote;

    private QuicMessageConnection(QuicConnection connection, QuicStream control, QuicStream bulk)
    {
        _connection = connection;
        _control = control;
        _bulk = bulk;
        _remote = "quic:" + connection.RemoteEndPoint;
    }

    public override string Remote => _remote;

    /// <summary>Сторона клиента: открыть оба потока и пометить их.</summary>
    internal static async Task<QuicMessageConnection> OpenAsClientAsync(QuicConnection connection, CancellationToken ct)
    {
        QuicStream control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
        await control.WriteAsync(new[] { ControlTag }, ct).ConfigureAwait(false);
        QuicStream bulk = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
        await bulk.WriteAsync(new[] { BulkTag }, ct).ConfigureAwait(false);
        var c = new QuicMessageConnection(connection, control, bulk);
        c.Start();
        return c;
    }

    /// <summary>Сторона сервера: принять оба потока клиента и разобрать их по меткам.</summary>
    internal static async Task<QuicMessageConnection> AcceptAsServerAsync(QuicConnection connection, CancellationToken ct)
    {
        QuicStream? control = null, bulk = null;
        for (int i = 0; i < 2; i++)
        {
            QuicStream s = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
            var tag = new byte[1];
            await s.ReadExactlyAsync(tag, ct).ConfigureAwait(false);
            if (tag[0] == ControlTag && control == null) control = s;
            else if (tag[0] == BulkTag && bulk == null) bulk = s;
            else throw new ProtocolException($"Неожиданная метка потока: {tag[0]}");
        }
        var c = new QuicMessageConnection(connection, control!, bulk!);
        c.Start();
        return c;
    }

    private void Start()
    {
        _ = Pump(() => SendLoop(_control, _controlOut.Reader));
        _ = Pump(() => SendLoop(_bulk, _bulkOut.Reader));
        _ = Pump(() => ReceiveLoop(_control));
        _ = Pump(() => ReceiveLoop(_bulk));
    }

    public override void Send(Message message)
    {
        if (!IsOpen) return;
        byte[] frame = ProtocolCodec.EncodeFrame(message);
        (message.Type == MessageType.SnapshotChunk ? _bulkOut : _controlOut).Writer.TryWrite(frame);
    }

    private async Task Pump(Func<Task> loop)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ProtocolException e)
        {
            Close("ошибка протокола: " + e.Message);
        }
        catch (QuicException e)
        {
            Close("QUIC: " + e.QuicError + (e.ApplicationErrorCode is long code ? $" (код {code})" : "") + ": " + e.Message);
        }
        catch (Exception e)
        {
            Close(e.GetType().Name + ": " + e.Message);
        }
    }

    private async Task SendLoop(QuicStream stream, ChannelReader<byte[]> outbox)
    {
        CancellationToken ct = _cts.Token;
        await foreach (byte[] frame in outbox.ReadAllAsync(ct).ConfigureAwait(false))
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoop(QuicStream stream)
    {
        CancellationToken ct = _cts.Token;
        byte[] buffer = new byte[64 * 1024];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= ProtocolCodec.LengthPrefixBytes + ProtocolLimits.MaxFrameBytes)
                    throw new ProtocolException("Кадр больше допустимого");
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, ProtocolCodec.LengthPrefixBytes + ProtocolLimits.MaxFrameBytes));
            }
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                Close("удалённая сторона закрыла поток");
                return;
            }
            length += read;

            int offset = 0;
            while (ProtocolCodec.TryDecodeFrame(buffer.AsSpan(offset, length - offset), out Message? message, out int consumed))
            {
                Deliver(message!);
                offset += consumed;
            }
            if (offset > 0)
            {
                Buffer.BlockCopy(buffer, offset, buffer, 0, length - offset);
                length -= offset;
            }
        }
    }

    protected override void OnClose(string reason)
    {
        _controlOut.Writer.TryComplete();
        _bulkOut.Writer.TryComplete();
        _cts.Cancel();
        _ = DisposeTransportAsync();
    }

    private async Task DisposeTransportAsync()
    {
        try
        {
            await _connection.CloseAsync(0).ConfigureAwait(false);
        }
        catch
        {
            // Соединение уже разорвано — закрывать нечего.
        }
        await _control.DisposeAsync().ConfigureAwait(false);
        await _bulk.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
