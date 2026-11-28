using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using RWMLWC.Client.Sim;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.SessionView;

/// <summary>
/// Отчёт об ошибке: игрок пишет, что не так, — отчёт (текст, состояние сессии, журналы игры и мода, хеши,
/// список модов) собирается в zip, сохраняется у игрока в RWMLWC/bugreports/ и, если есть связь с сервером,
/// отправляется ему кусками (ProtocolIds.BugReportMapId, v8). Сервер кладёт его в bugreports/ рядом с копиями мира.
/// </summary>
public static class BugReport
{
    private const int ChunkBytes = 200 * 1024;
    private const long LogTailBytes = 4L * 1024 * 1024;
    private static int nextId = (int)(DateTime.UtcNow.Ticks & 0x3FFFFFFF);

    /// <summary>Собрать, сохранить у себя и отправить на сервер. Возвращает путь локальной копии.</summary>
    public static string Submit(string userText)
    {
        byte[] zip = Collect(userText, out string stamp);
        string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "bugreports");
        Directory.CreateDirectory(dir);
        string local = Path.Combine(dir, $"bugreport-{stamp}.zip");
        File.WriteAllBytes(local, zip);
        bool sent = Send(zip);
        Log.Message($"[RWMLWC] отчёт об ошибке: {zip.Length} байт, сохранён {local}, на сервер {(sent ? "отправлен" : "не отправлен (нет связи)")}");
        TestAutomation.Note($"bugreport: {zip.Length} bytes, local {local}, sent={sent}");
        return local;
    }

    private static bool Send(byte[] zip)
    {
        if (LockstepClient.Connection is not { } connection || LockstepClient.CurrentState == LockstepClient.State.Off)
        {
            return false;
        }
        int id = ++nextId;
        int count = Math.Max(1, (zip.Length + ChunkBytes - 1) / ChunkBytes);
        int tick = Find.TickManager?.TicksGame ?? 0;
        for (int i = 0; i < count; i++)
        {
            int offset = i * ChunkBytes;
            int len = Math.Min(ChunkBytes, zip.Length - offset);
            byte[] part = new byte[Math.Max(0, len)];
            Buffer.BlockCopy(zip, offset, part, 0, part.Length);
            connection.Send(new SnapshotChunkMessage
            {
                SnapshotId = id,
                MapId = ProtocolIds.BugReportMapId,
                Tick = tick,
                ChunkIndex = i,
                ChunkCount = count,
                Data = part,
            });
        }
        return true;
    }

    private static byte[] Collect(string userText, out string stamp)
    {
        DateTime now = DateTime.UtcNow;
        stamp = now.ToString("yyyyMMdd-HHmmss");
        using MemoryStream ms = new MemoryStream();
        using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            AddText(zip, "report.txt", Describe(userText, now));
            AddFileTail(zip, "Player.log", PlayerLogPath());
            string modDir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
            AddFileTail(zip, "automation-log.txt", Path.Combine(modDir, "automation-log.txt"));
            if (Directory.Exists(modDir))
            {
                foreach (string hash in Directory.GetFiles(modDir, "hash-*.txt").OrderByDescending(File.GetLastWriteTimeUtc).Take(1))
                {
                    AddFileTail(zip, Path.GetFileName(hash), hash);
                }
            }
            AddText(zip, "mods.txt", string.Join("\n", LoadedModManager.RunningModsListForReading.Select(m => $"{m.PackageId} | {m.Name}")));
        }
        return ms.ToArray();
    }

    private static string Describe(string userText, DateTime now)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# Отчёт об ошибке RWMLWC");
        sb.AppendLine("Что не так (со слов игрока):");
        sb.AppendLine(userText);
        sb.AppendLine();
        sb.AppendLine($"Время (UTC): {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Игра: {RimWorld.VersionControl.CurrentVersionStringWithRev}; протокол RWMLWC v{ProtocolInfo.ProtocolVersion}");
        sb.AppendLine($"Сессия: {LockstepClient.CurrentState}; игрок {LockstepClient.PlayerId}");
        foreach (var p in SessionUi.Players)
        {
            sb.AppendLine($"  игрок {p.PlayerId} «{p.Name}», в сети {p.Online}, пинг {p.PingMs} мс");
        }
        if (Current.Game != null)
        {
            sb.AppendLine($"Тик: {Find.TickManager.TicksGame}; скорость {Find.TickManager.CurTimeSpeed}; раздельный счёт {SplitColonies.ModeOn}");
            sb.AppendLine($"Текущая карта: {Find.CurrentMap?.uniqueID.ToString() ?? "-"}; выделено: {string.Join(", ", Find.Selector.SelectedObjects.Take(10).Select(o => o.ToString()))}");
            foreach (Map map in Find.Maps)
            {
                sb.AppendLine($"  карта {map.uniqueID}: {map.Parent?.Label} (#{map.Parent?.ID}), пешек {map.mapPawns.AllPawnsCount}, владелец {RWMLWC.Client.Maps.MapAccess.OwnerOf(map.uniqueID)}");
            }
        }
        return sb.ToString();
    }

    private static string PlayerLogPath()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string lowPath = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Logs", "Player.log");
        return File.Exists(lowPath) ? lowPath : Path.Combine(GenFilePaths.SaveDataFolderPath, "Logs", "Player.log");
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using Stream s = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Хвост файла (журнал игра пишет прямо сейчас — открываем с общим доступом).</summary>
    private static void AddFileTail(ZipArchive zip, string name, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - LogTailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using Stream s = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open();
            fs.CopyTo(s);
        }
        catch (Exception ex)
        {
            AddText(zip, name + ".error.txt", ex.ToString());
        }
    }
}

/// <summary>Окно «Сообщить об ошибке»: поле «что не так» и кнопка «Отправить».</summary>
public sealed class Dialog_BugReport : Window
{
    private string text = "";

    public override Vector2 InitialSize => new Vector2(560f, 380f);

    public Dialog_BugReport()
    {
        forcePause = false;
        doCloseX = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 36f), "Сообщить об ошибке");
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(0f, 40f, inRect.width, 44f),
            "Опишите, что пошло не так и что вы делали перед этим. Вместе с текстом сохранятся журналы игры и мода и состояние сессии.");
        text = Widgets.TextArea(new Rect(0f, 88f, inRect.width, inRect.height - 140f), text);
        if (Widgets.ButtonText(new Rect(inRect.width - 160f, inRect.height - 40f, 160f, 36f), "Отправить"))
        {
            try
            {
                string path = BugReport.Submit(text.NullOrEmpty() ? "(без описания)" : text);
                Messages.Message("RWMLWC: отчёт отправлен и сохранён у вас: " + path, RimWorld.MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception ex)
            {
                Log.Error("[RWMLWC] отчёт об ошибке не собран: " + ex);
                Messages.Message("RWMLWC: отчёт не удалось собрать — подробности в журнале", RimWorld.MessageTypeDefOf.RejectInput, false);
            }
            Close();
        }
    }
}
