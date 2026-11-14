using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Снимок состояния игры для передачи другому клиенту (T-030, первый этап).
///
/// Первый этап — снимок ВСЕЙ игры штатными средствами: игра сама сохраняет себя в файл и сама
/// загружает из файла, мы только сжимаем и разжимаем байты. Никакой рефлексии и своих путей
/// загрузки: это тот же код, которым игра грузит сейвы, поэтому он работает с любыми модами.
///
/// Цена: размер и время загрузки как у обычного сейва. Снимок одной карты в идущую игру
/// (без перезагрузки мира) — второй этап, см. docs/research/io.md, раздел 1.
///
/// Вызывать только между тиками (не внутри DoSingleTick): тогда Rand симуляции убран
/// в MapRandContext, и ни сохранение, ни загрузка его не сдвигают.
/// </summary>
public static class GameSnapshot
{
    /// <summary>Имена временных сейвов в папке сохранений игры.</summary>
    public const string OutName = "RWMLWC-snapshot-out";
    public const string InName = "RWMLWC-snapshot-in";

    public struct Stats
    {
        public int rawBytes;
        public int packedBytes;
        public long saveMs;
        public long packMs;

        public override string ToString() =>
            $"raw={rawBytes} bytes, packed={packedBytes} bytes ({(rawBytes > 0 ? 100.0 * packedBytes / rawBytes : 0):F1}%), save={saveMs} ms, pack={packMs} ms";
    }

    /// <summary>Снять снимок текущей игры. Возвращает сжатые байты.</summary>
    public static byte[] Take(out Stats stats)
    {
        stats = default;
        Stopwatch sw = Stopwatch.StartNew();
        GameDataSaveLoader.SaveGame(OutName);
        stats.saveMs = sw.ElapsedMilliseconds;

        byte[] raw = File.ReadAllBytes(GenFilePaths.FilePathForSavedGame(OutName));
        stats.rawBytes = raw.Length;

        sw.Restart();
        byte[] packed = Pack(raw);
        stats.packMs = sw.ElapsedMilliseconds;
        stats.packedBytes = packed.Length;
        return packed;
    }

    /// <summary>
    /// Загрузить игру из снимка. Текущая игра уничтожается сразу, загрузка идёт штатным
    /// экраном загрузки. Нельзя вызывать изнутри тика.
    /// </summary>
    public static void Apply(byte[] packed)
    {
        byte[] raw = Unpack(packed);
        File.WriteAllBytes(GenFilePaths.FilePathForSavedGame(InName), raw);
        GameDataSaveLoader.LoadGame(InName);
    }

    public static byte[] Pack(byte[] raw)
    {
        using MemoryStream output = new MemoryStream(raw.Length / 8 + 1024);
        using (BrotliStream brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    public static byte[] Unpack(byte[] packed)
    {
        using MemoryStream input = new MemoryStream(packed);
        using BrotliStream brotli = new BrotliStream(input, CompressionMode.Decompress);
        using MemoryStream output = new MemoryStream(packed.Length * 8);
        brotli.CopyTo(output);
        return output.ToArray();
    }
}
