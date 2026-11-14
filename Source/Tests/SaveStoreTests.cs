using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-044: хранение сейва на сервере, версии, защита от запуска со старой копии.</summary>
public sealed class SaveStoreTests : IDisposable
{
    private const ulong World = 0x00000000_1234ABCD;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwmlwc-saves-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class C
    {
        public readonly IConnection Conn;
        public readonly List<Message> Got = new();
        public C(IConnection conn) => Conn = conn;
        public void Drain() { while (Conn.TryReceive(out Message m)) Got.Add(m); }
    }

    private static C Join(Session s, string name, ulong world, int saveTick)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name, SaveWorldId = world, SaveTick = saveTick });
        s.Pump();
        var c = new C(client);
        c.Drain();
        return c;
    }

    private static void SendSnapshot(C host, Session s, int snapshotId, int tick, byte[] data, int chunkSize)
    {
        int count = Math.Max(1, (data.Length + chunkSize - 1) / chunkSize);
        // Куски в обратном порядке: сборка не должна зависеть от порядка.
        for (int i = count - 1; i >= 0; i--)
        {
            int len = Math.Min(chunkSize, data.Length - i * chunkSize);
            host.Conn.Send(new SnapshotChunkMessage
            {
                SnapshotId = snapshotId, MapId = ProtocolIds.WorldMapId, Tick = tick,
                ChunkIndex = i, ChunkCount = count, Data = data.AsSpan(i * chunkSize, len).ToArray(),
            });
        }
        s.Pump();
    }

    [Fact]
    public void WorldSnapshotIsAssembledStoredAndReloadedAfterRestart()
    {
        var data = new byte[1_000_000];
        new Random(44).NextBytes(data);

        var s1 = new Session(new SessionOptions { Store = new SaveStore(_dir) });
        C host = Join(s1, "Host", World, 0);
        C guest = Join(s1, "Guest", World, 0);
        SendSnapshot(host, s1, snapshotId: 1, tick: 20000, data, chunkSize: 256 * 1024);
        guest.Drain();

        Assert.Equal((World, 20000), (s1.SaveWorldId, s1.SaveTick));
        Assert.Equal(4, guest.Got.OfType<SnapshotChunkMessage>().Count()); // остальным пересылается как раньше

        // «Перезапуск сервера»: новая сессия на той же папке видит копию.
        var store = new SaveStore(_dir);
        SaveCopy latest = Assert.IsType<SaveCopy>(store.Latest);
        Assert.Equal((World, 20000, (long)data.Length), (latest.WorldId, latest.Tick, latest.Size));
        Assert.Equal(data, store.Read(latest));

        var s2 = new Session(new SessionOptions { Store = store, StartTick = latest.Tick });
        C again = Join(s2, "Host", World, 20000);
        var w = again.Got.OfType<WelcomeMessage>().Single();
        Assert.Equal((World, 20000, 20000), (w.SaveWorldId, w.SaveTick, w.ServerTick));
        Assert.Empty(again.Got.OfType<NoticeMessage>());
    }

    [Fact]
    public void ServerStartedFromOldCopyWarnsEveryone()
    {
        var store = new SaveStore(_dir);
        store.Store(World, 10000, new byte[] { 1 });
        var log = new List<string>();
        var s = new Session(new SessionOptions { Store = store }, log.Add);

        C old = Join(s, "A", World, 10000);       // та же копия — тихо
        C newer = Join(s, "B", World, 25000);     // у игрока новее
        old.Drain();

        var n = Assert.Single(old.Got.OfType<NoticeMessage>());
        Assert.Equal(NoticeCode.NewerSaveOnClient, n.Code);
        Assert.Contains("25000", n.Text);
        Assert.Single(newer.Got.OfType<NoticeMessage>());
        Assert.True(newer.Conn.IsOpen);           // предупреждение, не отказ
        Assert.Contains(log, l => l.StartsWith("ВНИМАНИЕ"));
    }

    [Fact]
    public void DifferentWorldIsReportedAndNotStored()
    {
        var store = new SaveStore(_dir);
        store.Store(World, 10000, new byte[] { 1 });
        var s = new Session(new SessionOptions { Store = store });
        C other = Join(s, "X", 0xDEAD, 5);
        Assert.Equal(NoticeCode.DifferentWorld, Assert.Single(other.Got.OfType<NoticeMessage>()).Code);

        SendSnapshot(other, s, 1, 30000, new byte[] { 9, 9 }, 1);
        Assert.Equal(10000, s.SaveTick);          // копия чужого мира не принята
        Assert.Single(store.List());
    }

    [Fact]
    public void KeepsOnlyLastCopies()
    {
        var store = new SaveStore(_dir);
        for (int t = 1; t <= 8; t++) store.Store(World, t * 1000, new byte[] { (byte)t });
        Assert.Equal(new[] { 4000, 5000, 6000, 7000, 8000 }, store.List().Select(c => c.Tick));
        Assert.Equal(SaveStore.DefaultKeepCopies * 2, Directory.GetFiles(Path.Combine(_dir, "saves")).Length);
    }

    [Fact]
    public void BrokenOrInconsistentSnapshotIsNotStored()
    {
        var s = new Session(new SessionOptions { Store = new SaveStore(_dir) });
        C host = Join(s, "Host", World, 0);
        // Кусок 0 из 2, потом кусок 0 из 3 — несогласованно.
        host.Conn.Send(new SnapshotChunkMessage { SnapshotId = 5, MapId = -1, Tick = 100, ChunkIndex = 0, ChunkCount = 2, Data = new byte[] { 1 } });
        host.Conn.Send(new SnapshotChunkMessage { SnapshotId = 5, MapId = -1, Tick = 100, ChunkIndex = 1, ChunkCount = 3, Data = new byte[] { 2 } });
        // Неполный снимок.
        host.Conn.Send(new SnapshotChunkMessage { SnapshotId = 6, MapId = -1, Tick = 200, ChunkIndex = 0, ChunkCount = 2, Data = new byte[] { 1 } });
        // Снимок карты (не мира) не сохраняется.
        host.Conn.Send(new SnapshotChunkMessage { SnapshotId = 7, MapId = 0, Tick = 300, ChunkIndex = 0, ChunkCount = 1, Data = new byte[] { 1 } });
        s.Pump();
        Assert.Equal(0, s.SaveTick);
        Assert.Empty(new SaveStore(_dir).List());
    }
}
