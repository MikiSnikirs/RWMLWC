using System;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Накопительный хеш FNV-1a (64 бита). Детерминирован на любой машине:
/// только целочисленная арифметика, порядок байт задан явно.
/// </summary>
public struct Fnv64
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong hash;

    public static Fnv64 Create()
    {
        Fnv64 h;
        h.hash = OffsetBasis;
        return h;
    }

    public ulong Value => hash;

    public void Add(int value)
    {
        unchecked
        {
            uint v = (uint)value;
            for (int i = 0; i < 4; i++)
            {
                hash ^= v & 0xFFu;
                hash *= Prime;
                v >>= 8;
            }
        }
    }

    public void Add(ulong value)
    {
        unchecked
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= value & 0xFFUL;
                hash *= Prime;
                value >>= 8;
            }
        }
    }

    public void Add(bool value) => Add(value ? 1 : 0);

    /// <summary>Добавляет точные биты числа, без округления: расхождение в младшем бите меняет хеш.</summary>
    public void Add(float value) => Add(BitConverter.SingleToInt32Bits(value));
}
