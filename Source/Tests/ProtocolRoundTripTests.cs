using System;
using System.Collections.Generic;
using System.Linq;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-011: все сообщения протокола проходят сериализацию туда-обратно без потерь.</summary>
public class ProtocolRoundTripTests
{
    public static IEnumerable<object[]> AllMessages()
    {
        yield return new object[] { new HelloMessage { GameVersion = "1.7.5005", PlayerName = "Игрок ✓", ModListHash = 0xFEDCBA9876543210 } };
        yield return new object[] { new WelcomeMessage { PlayerId = 2, ServerTick = int.MaxValue, SessionSeed = 0x9E3779B1 } };
        yield return new object[] { new RejectMessage { Reason = RejectReason.ModListMismatch, Detail = "Разный список модов" } };
        yield return new object[] { new CommandMessage { ClientSeq = 7, MapId = 3, Kind = 42, Payload = new byte[] { 0, 1, 2, 255 } } };
        yield return new object[] { new CommandMessage { ClientSeq = 1, MapId = ProtocolIds.WorldMapId, Kind = 0 } };
        yield return new object[] { new CommandOrderedMessage { Seq = long.MaxValue, Tick = 15000, PlayerId = 1, ClientSeq = 7, MapId = 3, Kind = -5, Payload = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray() } };
        yield return new object[] { new TickMessage { TickUpTo = 123456, Speed = 3 } };
        yield return new object[] { new SpeedRequestMessage { Speed = 0 } };
        yield return new object[] { new SnapshotChunkMessage { SnapshotId = 9, MapId = 0, Tick = 777, ChunkIndex = 2, ChunkCount = 3, Data = new byte[ProtocolLimits.SnapshotChunkBytes] } };
        yield return new object[] { new MapHashMessage { MapId = 0, Tick = 15250, Total = ulong.MaxValue, Parts = { new("things", 1), new("pawns", 0xABCDEF), new("rand", 0) } } };
        yield return new object[] { new MapHashMessage { MapId = 5, Tick = 0 } };
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void FrameRoundTripIsLossless(Message original)
    {
        byte[] frame = ProtocolCodec.EncodeFrame(original);

        Assert.True(ProtocolCodec.TryDecodeFrame(frame, out Message? decoded, out int consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.NotNull(decoded);
        Assert.Equal(original.GetType(), decoded!.GetType());
        AssertSameFields(original, decoded);
        // Повторное кодирование даёт те же байты.
        Assert.Equal(frame, ProtocolCodec.EncodeFrame(decoded));
    }

    [Fact]
    public void EveryMessageTypeIsCovered()
    {
        var covered = AllMessages().Select(a => ((Message)a[0]).Type).ToHashSet();
        foreach (MessageType t in Enum.GetValues<MessageType>())
            Assert.Contains(t, covered);
    }

    [Fact]
    public void HelloDefaultsToCurrentProtocol()
    {
        var hello = (HelloMessage)ProtocolCodec.DecodeBody(ProtocolCodec.EncodeBody(new HelloMessage()));
        Assert.Equal(ProtocolInfo.ProtocolVersion, hello.ProtocolVersion);
        Assert.Equal(ProtocolInfo.ModId, hello.ModId);
    }

    [Fact]
    public void StreamWithSeveralFramesSplitAnywhereDecodesInOrder()
    {
        Message[] sent = AllMessages().Select(a => (Message)a[0]).ToArray();
        byte[] stream = sent.SelectMany(ProtocolCodec.EncodeFrame).ToArray();

        // Подаём поток кусками разного размера, как его отдаст сокет.
        foreach (int piece in new[] { 1, 3, 7, 4096, stream.Length })
        {
            var buffer = new List<byte>();
            var received = new List<Message>();
            for (int pos = 0; pos < stream.Length; pos += piece)
            {
                buffer.AddRange(stream.AsSpan(pos, Math.Min(piece, stream.Length - pos)).ToArray());
                while (ProtocolCodec.TryDecodeFrame(buffer.ToArray(), out Message? m, out int consumed))
                {
                    received.Add(m!);
                    buffer.RemoveRange(0, consumed);
                }
            }
            Assert.Empty(buffer);
            Assert.Equal(sent.Length, received.Count);
            for (int i = 0; i < sent.Length; i++)
                Assert.Equal(ProtocolCodec.EncodeFrame(sent[i]), ProtocolCodec.EncodeFrame(received[i]));
        }
    }

    [Fact]
    public void IncompleteFrameWaitsForMoreData()
    {
        byte[] frame = ProtocolCodec.EncodeFrame(new TickMessage { TickUpTo = 1, Speed = 1 });
        Assert.False(ProtocolCodec.TryDecodeFrame(frame.AsSpan(0, frame.Length - 1), out _, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void UnknownTypeIsRejected()
    {
        Assert.Throws<ProtocolException>(() => ProtocolCodec.DecodeBody(new byte[] { 0xFF, 0xFF }));
    }

    [Fact]
    public void TruncatedBodyIsRejected()
    {
        byte[] body = ProtocolCodec.EncodeBody(new CommandMessage { Payload = new byte[10] });
        Assert.Throws<ProtocolException>(() => ProtocolCodec.DecodeBody(body.AsSpan(0, body.Length - 1)));
    }

    [Fact]
    public void TrailingBytesAreRejected()
    {
        byte[] body = ProtocolCodec.EncodeBody(new SpeedRequestMessage { Speed = 1 });
        Assert.Throws<ProtocolException>(() => ProtocolCodec.DecodeBody(body.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void HugeDeclaredLengthIsRejected()
    {
        // Заявлена длина массива 2 ГБ при 4 байтах данных: должно упасть с ProtocolException, а не выделять память.
        byte[] body = ProtocolCodec.EncodeBody(new CommandMessage());
        BitConverter.TryWriteBytes(body.AsSpan(body.Length - 4), int.MaxValue);
        Assert.Throws<ProtocolException>(() => ProtocolCodec.DecodeBody(body));

        byte[] badFrame = { 0xFF, 0xFF, 0xFF, 0x7F, 0x06, 0x00 };
        Assert.Throws<ProtocolException>(() => ProtocolCodec.TryDecodeFrame(badFrame, out _, out _));
    }

    [Fact]
    public void BadSnapshotChunkIndexIsRejected()
    {
        byte[] body = ProtocolCodec.EncodeBody(new SnapshotChunkMessage { ChunkIndex = 3, ChunkCount = 3 });
        Assert.Throws<ProtocolException>(() => ProtocolCodec.DecodeBody(body));
    }

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        byte[] body = ProtocolCodec.EncodeBody(new RejectMessage { Detail = "ab" });
        body[^1] = 0xFF;
        Assert.Throws<ProtocolException>(() => ProtocolCodec.DecodeBody(body));
    }

    /// <summary>Сравнение всех публичных полей, включая массивы и списки по содержимому.</summary>
    private static void AssertSameFields(Message expected, Message actual)
    {
        foreach (var field in expected.GetType().GetFields())
        {
            object? e = field.GetValue(expected);
            object? a = field.GetValue(actual);
            switch (e)
            {
                case byte[] eb:
                    Assert.Equal(eb, (byte[])a!);
                    break;
                case List<HashPart> el:
                    Assert.Equal(el, (List<HashPart>)a!);
                    break;
                default:
                    Assert.Equal(e, a);
                    break;
            }
        }
    }
}
