using System;
using System.Buffers.Binary;
using System.Text;

namespace RWMLWC.Shared.Protocol;

/// <summary>
/// Чтение полей сообщения. Данные приходят из сети, поэтому любая ошибка формата
/// (обрыв, неверная длина, превышение лимитов) — <see cref="ProtocolException"/>, а не падение.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Remaining => _data.Length - _pos;

    public byte ReadByte()
    {
        Need(1);
        return _data[_pos++];
    }

    public bool ReadBool()
    {
        byte b = ReadByte();
        if (b > 1) throw new ProtocolException($"Неверное значение bool: {b}");
        return b == 1;
    }

    public ushort ReadUInt16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos));
        _pos += 2;
        return v;
    }

    public int ReadInt32()
    {
        Need(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos));
        _pos += 4;
        return v;
    }

    public long ReadInt64()
    {
        Need(8);
        long v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_pos));
        _pos += 8;
        return v;
    }

    public ulong ReadUInt64()
    {
        Need(8);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos));
        _pos += 8;
        return v;
    }

    public string ReadString()
    {
        int len = ReadLength(ProtocolLimits.MaxStringBytes);
        string s;
        try
        {
            s = new UTF8Encoding(false, true).GetString(_data.Slice(_pos, len));
        }
        catch (DecoderFallbackException e)
        {
            throw new ProtocolException("Строка не в UTF-8", e);
        }
        _pos += len;
        return s;
    }

    public byte[] ReadBytes()
    {
        int len = ReadLength(ProtocolLimits.MaxBlobBytes);
        byte[] result = _data.Slice(_pos, len).ToArray();
        _pos += len;
        return result;
    }

    /// <summary>Число элементов списка с проверкой: не отрицательное, не больше лимита и не больше остатка данных.</summary>
    public int ReadCount(int max, int minBytesPerItem)
    {
        int count = ReadInt32();
        if (count < 0 || count > max)
            throw new ProtocolException($"Неверное число элементов: {count} (допустимо 0..{max})");
        if ((long)count * minBytesPerItem > Remaining)
            throw new ProtocolException($"Число элементов {count} не помещается в оставшиеся {Remaining} байт");
        return count;
    }

    /// <summary>Сообщение должно быть прочитано целиком: лишние байты означают несовпадение формата.</summary>
    public void EnsureEnd()
    {
        if (Remaining != 0)
            throw new ProtocolException($"После сообщения остались лишние байты: {Remaining}");
    }

    private int ReadLength(int max)
    {
        int len = ReadInt32();
        if (len < 0 || len > max)
            throw new ProtocolException($"Неверная длина: {len} (допустимо 0..{max})");
        Need(len);
        return len;
    }

    private void Need(int count)
    {
        if (_data.Length - _pos < count)
            throw new ProtocolException($"Сообщение оборвано: нужно {count} байт, осталось {_data.Length - _pos}");
    }
}
