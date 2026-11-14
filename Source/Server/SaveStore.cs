using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace RWMLWC.Server;

/// <summary>Одна сохранённая копия мира.</summary>
public sealed record SaveCopy(ulong WorldId, int Tick, DateTime SavedUtc, long Size, string Sha256, string Path);

/// <summary>
/// Хранилище сейвов сервера (T-044): копии мира, присланные хостом снимком уровня мира (<c>MapId = -1</c>),
/// по одному файлу на копию. Версия копии — игровой тик, на котором она снята.
/// Данные хранятся как пришли (формат снимка — T-030: Brotli-поток с файлом сейва).
/// </summary>
public sealed class SaveStore
{
    public const int KeepCopies = 5;

    private readonly string _dir;

    public SaveStore(string dataDir)
    {
        _dir = System.IO.Path.Combine(dataDir, "saves");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Все копии, от старой к новой (по тику).</summary>
    public IReadOnlyList<SaveCopy> List()
    {
        var list = new List<SaveCopy>();
        foreach (string meta in Directory.GetFiles(_dir, "*.meta"))
        {
            SaveCopy? c = ReadMeta(meta);
            if (c != null && File.Exists(c.Path)) list.Add(c);
        }
        return list.OrderBy(c => c.Tick).ThenBy(c => c.SavedUtc).ToList();
    }

    public SaveCopy? Latest => List().LastOrDefault();

    /// <summary>Сохранить копию. Старые сверх <see cref="KeepCopies"/> удаляются. Копию другого мира не принимает.</summary>
    public SaveCopy Store(ulong worldId, int tick, byte[] data)
    {
        SaveCopy? latest = Latest;
        if (latest != null && latest.WorldId != worldId)
            throw new InvalidOperationException($"на сервере мир {latest.WorldId:X16}, прислан {worldId:X16}");

        string name = $"world-{worldId:X16}-tick-{tick:D10}";
        string dataPath = System.IO.Path.Combine(_dir, name + ".snapshot");
        // Сначала во временный файл, потом переименование: обрыв посреди записи не портит последнюю копию.
        string tmp = dataPath + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, dataPath, overwrite: true);

        var copy = new SaveCopy(worldId, tick, DateTime.UtcNow, data.Length, Convert.ToHexString(SHA256.HashData(data)), dataPath);
        File.WriteAllLines(System.IO.Path.Combine(_dir, name + ".meta"), new[]
        {
            "world=" + worldId.ToString("X16"),
            "tick=" + tick.ToString(CultureInfo.InvariantCulture),
            "saved=" + copy.SavedUtc.ToString("O", CultureInfo.InvariantCulture),
            "size=" + copy.Size.ToString(CultureInfo.InvariantCulture),
            "sha256=" + copy.Sha256,
        });

        foreach (SaveCopy old in List().SkipLast(KeepCopies))
        {
            File.Delete(old.Path);
            File.Delete(System.IO.Path.ChangeExtension(old.Path, ".meta"));
        }
        return copy;
    }

    public byte[] Read(SaveCopy copy) => File.ReadAllBytes(copy.Path);

    private static SaveCopy? ReadMeta(string metaPath)
    {
        try
        {
            Dictionary<string, string> kv = File.ReadAllLines(metaPath)
                .Where(l => l.Contains('='))
                .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
            return new SaveCopy(
                ulong.Parse(kv["world"], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(kv["tick"], CultureInfo.InvariantCulture),
                DateTime.Parse(kv["saved"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                long.Parse(kv["size"], CultureInfo.InvariantCulture),
                kv["sha256"],
                System.IO.Path.ChangeExtension(metaPath, ".snapshot"));
        }
        catch (Exception)
        {
            return null; // испорченный файл описания — копию пропускаем
        }
    }
}
