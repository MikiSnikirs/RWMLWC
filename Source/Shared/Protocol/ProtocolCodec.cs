using System;
using System.Buffers.Binary;

namespace RWMLWC.Shared.Protocol;

/// <summary>
/// Кодирование сообщений в кадры и обратно.
/// Кадр: <c>int32 длина</c> (little-endian, без учёта самих 4 байт) + <c>uint16 тип</c> + тело.
/// Транспорт (T-012) только доставляет кадры целиком и по порядку.
/// </summary>
public static class ProtocolCodec
{
    public const int LengthPrefixBytes = 4;
    public const int TypeBytes = 2;

    /// <summary>Тип + тело, без префикса длины (для транспорта, который сам режет на кадры).</summary>
    public static byte[] EncodeBody(Message message)
    {
        var w = new PacketWriter();
        w.WriteUInt16((ushort)message.Type);
        message.Write(w);
        if (w.Length > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException($"Сообщение {message.Type} больше {ProtocolLimits.MaxFrameBytes} байт");
        return w.ToArray();
    }

    /// <summary>Полный кадр с префиксом длины (для потоковых транспортов: TCP, QUIC-поток).</summary>
    public static byte[] EncodeFrame(Message message)
    {
        byte[] body = EncodeBody(message);
        byte[] frame = new byte[LengthPrefixBytes + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, LengthPrefixBytes);
        return frame;
    }

    /// <summary>Разбор типа + тела (без префикса длины).</summary>
    public static Message DecodeBody(ReadOnlySpan<byte> body)
    {
        if (body.Length > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException($"Кадр больше {ProtocolLimits.MaxFrameBytes} байт");
        var r = new PacketReader(body);
        var type = (MessageType)r.ReadUInt16();
        Message m = type switch
        {
            MessageType.Hello => HelloMessage.Read(ref r),
            MessageType.Welcome => WelcomeMessage.Read(ref r),
            MessageType.Reject => RejectMessage.Read(ref r),
            MessageType.Command => CommandMessage.Read(ref r),
            MessageType.CommandOrdered => CommandOrderedMessage.Read(ref r),
            MessageType.Tick => TickMessage.Read(ref r),
            MessageType.SpeedRequest => SpeedRequestMessage.Read(ref r),
            MessageType.SnapshotChunk => SnapshotChunkMessage.Read(ref r),
            MessageType.MapHash => MapHashMessage.Read(ref r),
            MessageType.MapClaim => MapClaimMessage.Read(ref r),
            MessageType.GrantPermission => GrantPermissionMessage.Read(ref r),
            MessageType.Permissions => PermissionsMessage.Read(ref r),
            MessageType.CommandRejected => CommandRejectedMessage.Read(ref r),
            MessageType.Notice => NoticeMessage.Read(ref r),
            MessageType.SnapshotApplied => SnapshotAppliedMessage.Read(ref r),
            MessageType.Players => PlayersMessage.Read(ref r),
            MessageType.Ping => PingMessage.Read(ref r),
            MessageType.Pong => PongMessage.Read(ref r),
            MessageType.Chat => ChatMessage.Read(ref r),
            _ => throw new ProtocolException($"Неизвестный тип сообщения: {(ushort)type}"),
        };
        r.EnsureEnd();
        return m;
    }

    /// <summary>
    /// Пытается выделить один кадр из начала буфера потока.
    /// Возвращает false, если кадр ещё не пришёл целиком; <paramref name="consumed"/> — сколько байт снять с буфера.
    /// </summary>
    public static bool TryDecodeFrame(ReadOnlySpan<byte> buffer, out Message? message, out int consumed)
    {
        message = null;
        consumed = 0;
        if (buffer.Length < LengthPrefixBytes) return false;
        int len = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (len < TypeBytes || len > ProtocolLimits.MaxFrameBytes)
            throw new ProtocolException($"Неверная длина кадра: {len}");
        if (buffer.Length - LengthPrefixBytes < len) return false;
        message = DecodeBody(buffer.Slice(LengthPrefixBytes, len));
        consumed = LengthPrefixBytes + len;
        return true;
    }
}
