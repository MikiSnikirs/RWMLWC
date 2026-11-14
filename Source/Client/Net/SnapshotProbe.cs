using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;
using Verse;

namespace RWMLWC.Client.Net;

/// <summary>
/// ВРЕМЕННАЯ проверка для T-004: снимок одной карты в память через Scribe (без файла),
/// размер, сжатие, время, повторяемость. Удаляется до слияния ветки.
/// </summary>
public sealed class SnapshotProbe : GameComponent
{
    private static readonly FieldInfo SaveStream = typeof(ScribeSaver).GetField("saveStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Writer = typeof(ScribeSaver).GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public SnapshotProbe(Game game) { }

    public override void StartedNewGame() => Run("StartedNewGame");
    public override void LoadedGame() => Run("LoadedGame");

    private static void Run(string when)
    {
        try
        {
            Map map = Find.CurrentMap;
            Log.Message($"[RWMLWC][T-004] snapshot probe ({when}): map {map.uniqueID}, size {map.Size}, things {map.listerThings.AllThings.Count}, pawns {map.mapPawns.AllPawnsSpawnedCount}");
            var sw = Stopwatch.StartNew();
            byte[] a = Snapshot(map);
            long saveMs = sw.ElapsedMilliseconds;
            byte[] b = Snapshot(map);

            sw.Restart();
            byte[] gz = Compress(a, CompressionLevel.Fastest, brotli: false);
            long gzMs = sw.ElapsedMilliseconds;
            sw.Restart();
            byte[] br = Compress(a, CompressionLevel.Optimal, brotli: true);
            long brMs = sw.ElapsedMilliseconds;

            sw.Restart();
            var doc = new XmlDocument();
            using (var ms = new MemoryStream(a)) doc.Load(ms);
            long parseMs = sw.ElapsedMilliseconds;

            Log.Message($"[RWMLWC][T-004] snapshot: xml {a.Length:N0} B in {saveMs} ms; gzip(fastest) {gz.Length:N0} B in {gzMs} ms; brotli(optimal) {br.Length:N0} B in {brMs} ms; XmlDocument parse {parseMs} ms; root <{doc.DocumentElement?.Name}> children {doc.DocumentElement?.ChildNodes.Count}");
            Log.Message($"[RWMLWC][T-004] snapshot repeat identical: {a.AsSpan().SequenceEqual(b)} (sha256 {Convert.ToHexString(SHA256.HashData(a))[..16]} vs {Convert.ToHexString(SHA256.HashData(b))[..16]}, lengths {a.Length} vs {b.Length})");
            if (!a.AsSpan().SequenceEqual(b))
            {
                int i = 0;
                while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
                int from = Math.Max(0, i - 200);
                Log.Message($"[RWMLWC][T-004] first diff at byte {i}:\nA: {System.Text.Encoding.UTF8.GetString(a, from, Math.Min(400, a.Length - from))}\nB: {System.Text.Encoding.UTF8.GetString(b, from, Math.Min(400, b.Length - from))}");
            }
        }
        catch (Exception e)
        {
            Log.Warning("[RWMLWC][T-004] snapshot probe FAILED: " + e);
        }
    }

    /// <summary>То же, что ScribeSaver.InitSaving/FinalizeSaving, но поток — MemoryStream.</summary>
    private static byte[] Snapshot(Map map)
    {
        var ms = new MemoryStream();
        Scribe.mode = LoadSaveMode.Saving;
        SaveStream.SetValue(Scribe.saver, ms);
        var writer = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false });
        Writer.SetValue(Scribe.saver, writer);
        writer.WriteStartDocument();
        Scribe.saver.EnterNode("rwmlwcMapSnapshot");
        try
        {
            Scribe_Deep.Look(ref map, "map");
        }
        finally
        {
            Scribe.saver.FinalizeSaving();
        }
        return ms.ToArray();
    }

    private static byte[] Compress(byte[] data, CompressionLevel level, bool brotli)
    {
        using var output = new MemoryStream();
        using (Stream z = brotli ? new BrotliStream(output, level) : new GZipStream(output, level))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
