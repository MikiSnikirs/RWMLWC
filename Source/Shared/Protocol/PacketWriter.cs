using System;
using System.Buffers.Binary;
using System.Text;

namespace RWMLWC.Shared.Protocol;

/// <summary>
/// Запись полей сообщения. Порядок байтов — little-endian на любой машине,
/// строки — UTF-8 с длиной впереди. Формат описан в docs/PROTOCOL.md.
/// </summary>
public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _length;

    public PacketWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }

    public int Length => _length;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    public void WriteInt32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteInt64(long value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    /// <summary>Строка: int32 длина в байтах UTF-8, затем байты. null не допускается.</summary>
    public void WriteString(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ProtocolLimits.MaxStringBytes)
            throw new ProtocolException($"Строка длиннее {ProtocolLimits.MaxStringBytes} байт");
        WriteInt32(byteCount);
        Ensure(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += byteCount;
    }

    /// <summary>Массив байтов: int32 длина, затем байты. null не допускается.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length > ProtocolLimits.MaxBlobBytes)
            throw new ProtocolException($"Блок данных длиннее {ProtocolLimits.MaxBlobBytes} байт");
        WriteInt32(value.Length);
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void Ensure(int extra)
    {
        int needed = _length + extra;
        if (needed <= _buffer.Length) return;
        int size = _buffer.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buffer, size);
    }
}
