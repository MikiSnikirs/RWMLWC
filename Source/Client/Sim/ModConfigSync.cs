using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Sim;

/// <summary>
/// Настройки модов у всех игроков — как у хозяина (как в RimWorld Multiplayer). Многие моды держат в настройках
/// то, что меняет ход игры (частоты событий, урон, правила работ); разные настройки у двух игроков — разошедшиеся миры.
///
/// Когда игрок входит в сеть (служебная команда сервера «в сети», у всех на одном тике), игрок в сети с наименьшим
/// номером, кроме вошедшего, шлёт командой <see cref="Kind"/> свои файлы Config/Mod_*.xml запущенных модов.
/// Вошедший сравнивает их со своими: отличающиеся копирует в RWMLWC/config-backup/, записывает файлы хозяина и
/// просит перезапустить игру — моды читают настройки при запуске. Остальные игроки команду пропускают.
///
/// Настройки нашего мода не передаются (адрес сервера, имя игрока — личные). Настройки, которые мод хранит
/// не в Mod_*.xml (например HugsLib), пока не передаются.
/// </summary>
public static class ModConfigSync
{
    /// <summary>Вид команды (номера видов — CommandKinds; эта исполняется до общего пути, без обстановки интерфейса).</summary>
    public const int Kind = RWMLWC.Client.Input.CommandKinds.ModConfig;

    private const int MaxFiles = 2000;
    private const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>Тест (метка modcfgtest): получатель перед сравнением портит один свой файл и удаляет другой.</summary>
    public static bool TestTamper;

    /// <summary>Сколько файлов записано в последний раз (для журнала прогона).</summary>
    public static int LastApplied { get; private set; }

    /// <summary>Игрок вошёл в сеть (из общего порядка, одинаково у всех): отправить ему настройки, если отправитель — мы.</summary>
    public static void OnPresence(int playerId, bool online, IEnumerable<int> onlinePlayers)
    {
        if (!online || playerId == LockstepClient.PlayerId)
        {
            return;
        }
        int sender = onlinePlayers.Where(p => p != playerId).DefaultIfEmpty(0).Min();
        if (sender != LockstepClient.PlayerId)
        {
            return;
        }
        try
        {
            List<(string name, byte[] content)> files = Collect();
            PacketWriter w = new PacketWriter(1024);
            w.WriteInt32(playerId);
            w.WriteBytes(Pack(files));
            int seq = LockstepClient.SendCommand(ProtocolIds.WorldMapId, Kind, PermissionLevel.Full, w.ToArray());
            TestAutomation.Note($"modconfig: sent {files.Count} files to player {playerId} (seq {seq})");
        }
        catch (Exception ex)
        {
            Log.Warning("[RWMLWC] настройки модов не отправлены: " + ex);
        }
    }

    /// <summary>Команда из общего порядка. Исполняет только тот, кому она адресована.</summary>
    public static void Execute(int author, byte[] payload)
    {
        PacketReader r = new PacketReader(payload);
        int target = r.ReadInt32();
        if (target != LockstepClient.PlayerId)
        {
            return;
        }
        try
        {
            List<(string name, byte[] content)> theirs = Unpack(r.ReadBytes());
            if (TestTamper)
            {
                Tamper(theirs);
            }
            Apply(author, theirs);
        }
        catch (Exception ex)
        {
            TestAutomation.Note("modconfig: apply FAILED: " + ex);
            Log.Error("[RWMLWC] настройки модов хозяина не применены: " + ex);
        }
    }

    /// <summary>Имена файлов настроек запущенных модов (как их строит LoadedModManager.GetSettingsFilename).</summary>
    private static IEnumerable<string> SettingsFileNames()
    {
        foreach (Mod mod in LoadedModManager.ModHandles)
        {
            if (mod is RwmlwcMod || mod.Content == null)
            {
                continue;
            }
            yield return GenText.SanitizeFilename($"Mod_{mod.Content.FolderName}_{mod.GetType().Name}.xml");
        }
    }

    private static List<(string, byte[])> Collect()
    {
        List<(string, byte[])> files = new List<(string, byte[])>();
        foreach (string name in SettingsFileNames().Distinct().OrderBy(n => n, StringComparer.Ordinal))
        {
            string path = Path.Combine(GenFilePaths.ConfigFolderPath, name);
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes)
            {
                files.Add((name, File.ReadAllBytes(path)));
            }
        }
        return files;
    }

    private static void Apply(int author, List<(string name, byte[] content)> theirs)
    {
        HashSet<string> allowed = new HashSet<string>(SettingsFileNames(), StringComparer.OrdinalIgnoreCase);
        string backup = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "config-backup");
        List<string> changed = new List<string>();
        int skipped = 0;
        foreach ((string name, byte[] content) in theirs)
        {
            // Имя только из списка своих модов: чужой файл с «..\» или настройки мода, которого у нас нет, не пишем.
            if (!allowed.Contains(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                skipped++;
                continue;
            }
            string path = Path.Combine(GenFilePaths.ConfigFolderPath, name);
            byte[]? mine = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (mine != null && mine.AsSpan().SequenceEqual(content))
            {
                continue;
            }
            if (mine != null)
            {
                Directory.CreateDirectory(backup);
                File.WriteAllBytes(Path.Combine(backup, name), mine);
            }
            File.WriteAllBytes(path, content);
            changed.Add(name);
        }
        LastApplied = changed.Count;
        TestAutomation.Note($"modconfig: from player {author}: {theirs.Count} files, {changed.Count} written [{string.Join(", ", changed)}], {skipped} skipped");
        if (changed.Count == 0)
        {
            return;
        }
        Log.Message($"[RWMLWC] записаны настройки модов хозяина ({changed.Count}): {string.Join(", ", changed)}. Прежние — в {backup}");
        if (!TestAutomation.AutomationActive)
        {
            string text = "Настройки модов у вас отличались от настроек хозяина. Записаны настройки хозяина для:\n\n"
                + string.Join("\n", changed.Select(ModLabel))
                + "\n\nМоды читают настройки при запуске игры: перезапустите игру, иначе миры могут разойтись.\n"
                + "Ваши прежние файлы сохранены в:\n" + backup;
            Find.WindowStack.Add(new Dialog_MessageBox(text, "Перезапустить сейчас", GenCommandLine.Restart, "Позже"));
        }
    }

    private static string ModLabel(string fileName)
    {
        foreach (Mod mod in LoadedModManager.ModHandles)
        {
            if (mod.Content != null && GenText.SanitizeFilename($"Mod_{mod.Content.FolderName}_{mod.GetType().Name}.xml") == fileName)
            {
                return "— " + mod.Content.Name;
            }
        }
        return "— " + fileName;
    }

    private static void Tamper(List<(string name, byte[] content)> theirs)
    {
        if (theirs.Count == 0)
        {
            return;
        }
        string first = Path.Combine(GenFilePaths.ConfigFolderPath, theirs[0].name);
        File.WriteAllText(first, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock><!-- rwmlwc tamper --></SettingsBlock>\n");
        if (theirs.Count > 1)
        {
            File.Delete(Path.Combine(GenFilePaths.ConfigFolderPath, theirs[1].name));
        }
        TestAutomation.Note($"modconfig: test tamper: overwrote {theirs[0].name}" + (theirs.Count > 1 ? $", deleted {theirs[1].name}" : ""));
    }

    private static byte[] Pack(List<(string name, byte[] content)> files)
    {
        PacketWriter w = new PacketWriter(16 * 1024);
        w.WriteInt32(files.Count);
        foreach ((string name, byte[] content) in files)
        {
            w.WriteString(name);
            w.WriteBytes(content);
        }
        using MemoryStream ms = new MemoryStream();
        using (DeflateStream z = new DeflateStream(ms, CompressionLevel.Optimal, true))
        {
            byte[] raw = w.ToArray();
            z.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static List<(string, byte[])> Unpack(byte[] packed)
    {
        using MemoryStream raw = new MemoryStream();
        using (DeflateStream z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
        {
            z.CopyTo(raw);
        }
        PacketReader r = new PacketReader(raw.ToArray());
        int count = r.ReadCount(MaxFiles, 8);
        List<(string, byte[])> files = new List<(string, byte[])>(count);
        for (int i = 0; i < count; i++)
        {
            files.Add((r.ReadString(), r.ReadBytes()));
        }
        return files;
    }
}
