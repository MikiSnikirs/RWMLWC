using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-065: отдающий мир по пингу, лечение расхождения сервером, автосохранение на сервер.</summary>
public sealed class WorldSenderTests : IDisposable
{
    private const ulong World = 0x00000000_0000BEEF;
    private const int Tick = 20000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwmlwc-v4-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Соединение с заданным пингом: сервер видит его <see cref="RoundTripMs"/>.</summary>
    private sealed class FixedRttConnection : IConnection
    {
        private readonly IConnection _inner;
        public int Rtt;
        public FixedRttConnection(IConnection inner, int rtt) { _inner = inner; Rtt = rtt; }
        public string Remote => _inner.Remote;
        public bool IsOpen => _inner.IsOpen;
        public string? CloseReason => _inner.CloseReason;
        public Task Closed => _inner.Closed;
        public void Send(Message message) => _inner.Send(message);
        public bool TryReceive(out Message message) => _inner.TryReceive(out message);
        public ValueTask<Message> ReceiveAsync(CancellationToken ct = default) => _inner.ReceiveAsync(ct);
        public void Close(string reason) => _inner.Close(reason);
        public void SendPing() { }
        public int RoundTripMs => Rtt;
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class C
    {
        public readonly IConnection Conn;
        public readonly List<Message> Got = new();
        public C(IConnection conn) => Conn = conn;
        public void Drain() { while (Conn.TryReceive(out Message m)) Got.Add(m); }
        public int Id => Got.OfType<WelcomeMessage>().First().PlayerId;
        public IEnumerable<CommandOrderedMessage> Ordered(int kind) => Got.OfType<CommandOrderedMessage>().Where(c => c.Kind == kind);
    }

    private Session NewSession(bool dedicated = true, TimeSpan? autosave = null, byte[]? stored = null, List<string>? log = null, int maxLead = 0)
    {
        var store = new SaveStore(_dir);
        if (stored != null) store.Store(World, Tick, stored);
        return new Session(new SessionOptions { StartTick = Tick, Store = store, Dedicated = dedicated, AutosaveInterval = autosave, MaxLeadTicks = maxLead }, (log ?? new List<string>()).Add);
    }

    private static C Join(Session s, string name, int rtt, bool withWorld)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(new FixedRttConnection(server, rtt));
        client.Send(new HelloMessage { PlayerName = name, GameVersion = "1.7.5005", ModListHash = 7, SaveWorldId = withWorld ? World : 0, SaveTick = withWorld ? Tick : 0 });
        s.Pump();
        var c = new C(client);
        c.Drain();
        return c;
    }

    private static void RequestWorld(Session s, C c)
    {
        c.Conn.Send(new CommandMessage { ClientSeq = 1, MapId = ProtocolIds.WorldMapId, Kind = ProtocolIds.RequestWorldSnapshotKind, Required = PermissionLevel.Observer });
        s.Pump();
    }

    [Fact]
    public void JoinerGetsTheWorldFromThePlayerWithTheBestPing()
    {
        Session s = NewSession();
        C far = Join(s, "far", 120, withWorld: true);
        C near = Join(s, "near", 25, withWorld: true);
        C unknown = Join(s, "unknown", -1, withWorld: true);
        C joiner = Join(s, "joiner", 5, withWorld: false); // лучший пинг, но мира нет — не может отдавать
        RequestWorld(s, joiner);

        foreach (C c in new[] { far, near, unknown, joiner })
        {
            c.Drain();
            CommandOrderedMessage order = c.Ordered(ProtocolIds.RequestWorldSnapshotKind).Single();
            Assert.Equal(near.Id, WorldSnapshotOrder.Sender(order.Payload));
            Assert.Equal(joiner.Id, order.PlayerId);
        }
        Assert.Empty(joiner.Got.OfType<SnapshotChunkMessage>()); // мир отдаёт игрок, не сервер
    }

    [Fact]
    public void JoinerBecomesASenderAfterSnapshotApplied()
    {
        Session s = NewSession();
        C a = Join(s, "a", 80, withWorld: true);
        C b = Join(s, "b", 10, withWorld: false);
        Assert.Equal(a.Id, s.ChooseWorldSender());
        b.Conn.Send(new SnapshotAppliedMessage { SnapshotId = 1, Tick = Tick });
        s.Pump();
        Assert.Equal(b.Id, s.ChooseWorldSender());
    }

    [Fact]
    public void LoneJoinerOnDedicatedServerGetsServerAsSender()
    {
        Session s = NewSession(stored: new byte[] { 9, 8, 7 });
        C c = Join(s, "c", 30, withWorld: false);
        RequestWorld(s, c);
        c.Drain();
        Assert.Equal(ProtocolIds.ServerPlayerId, WorldSnapshotOrder.Sender(c.Ordered(ProtocolIds.RequestWorldSnapshotKind).Single().Payload));
        Assert.Equal(new byte[] { 9, 8, 7 }, c.Got.OfType<SnapshotChunkMessage>().Single().Data);
    }

    [Fact]
    public void DesyncIsHealedByTheServerWithACommonSnapshot()
    {
        Session s = NewSession();
        C a = Join(s, "a", 40, withWorld: true);
        C b = Join(s, "b", 15, withWorld: true);
        a.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = 1 });
        b.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = 2 });
        s.Pump();
        a.Drain();
        CommandOrderedMessage order = a.Ordered(ProtocolIds.RequestWorldSnapshotKind).Single();
        Assert.Equal(ProtocolIds.ServerPlayerId, order.PlayerId);
        Assert.Equal(b.Id, WorldSnapshotOrder.Sender(order.Payload));
    }

    [Fact]
    public void AutosaveAsksTheBestPingPlayerAndStoresTheCopyWithoutForwardingIt()
    {
        var log = new List<string>();
        Session s = NewSession(autosave: TimeSpan.FromMinutes(1), log: log);
        C a = Join(s, "a", 90, withWorld: true);
        C b = Join(s, "b", 20, withWorld: true);
        a.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(30));
        a.Drain();
        Assert.Empty(a.Ordered(ProtocolIds.SaveWorldSnapshotKind)); // рано

        s.Advance(TimeSpan.FromSeconds(31));
        a.Drain();
        b.Drain();
        CommandOrderedMessage save = b.Ordered(ProtocolIds.SaveWorldSnapshotKind).Single();
        Assert.Equal(b.Id, WorldSnapshotOrder.Sender(save.Payload));
        Assert.Equal(ProtocolIds.ServerPlayerId, save.PlayerId);

        // b отдаёт мир: куски идут только на сервер.
        var data = new byte[] { 1, 2, 3, 4, 5 };
        b.Conn.Send(new SnapshotChunkMessage { SnapshotId = WorldSnapshotOrder.SnapshotId(save.Seq), MapId = ProtocolIds.WorldMapId, Tick = save.Tick, ChunkIndex = 0, ChunkCount = 1, Data = data });
        s.Pump();
        a.Drain();
        Assert.Empty(a.Got.OfType<SnapshotChunkMessage>());
        Assert.Contains(log, l => l.Contains("сохранена копия мира"));
        SaveCopy copy = new SaveStore(_dir).Latest!;
        Assert.Equal((World, save.Tick), (copy.WorldId, copy.Tick));
    }

    [Fact]
    public void AutosaveRunsWhenAPlayerLeavesAndNotWhenNothingChanged()
    {
        Session s = NewSession(autosave: TimeSpan.FromHours(1));
        C a = Join(s, "a", 10, withWorld: true);
        C b = Join(s, "b", 20, withWorld: true);
        s.Advance(TimeSpan.FromMilliseconds(10));
        a.Drain();
        int before = a.Ordered(ProtocolIds.SaveWorldSnapshotKind).Count();

        a.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(1)); // мир ушёл вперёд
        b.Conn.Close("ушёл");
        s.Pump();
        s.Advance(TimeSpan.FromMilliseconds(10));
        a.Drain();
        Assert.Equal(before + 1, a.Ordered(ProtocolIds.SaveWorldSnapshotKind).Count());
    }

    [Fact]
    public void ClientCannotOrderASave()
    {
        var log = new List<string>();
        Session s = NewSession(log: log);
        C a = Join(s, "a", 10, withWorld: true);
        a.Conn.Send(new CommandMessage { ClientSeq = 1, MapId = ProtocolIds.WorldMapId, Kind = ProtocolIds.SaveWorldSnapshotKind });
        s.Pump();
        a.Drain();
        Assert.Empty(a.Ordered(ProtocolIds.SaveWorldSnapshotKind));
        Assert.Contains(log, l => l.Contains("только от сервера"));
    }

    [Fact]
    public void LateHashesBeforeTheOrderedSnapshotDoNotOrderAnotherOne()
    {
        Session s = NewSession();
        C a = Join(s, "a", 40, withWorld: true);
        C b = Join(s, "b", 15, withWorld: true);
        a.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(10)); // сервер ушёл далеко вперёд, хеши приходят с отставанием
        void Pair(int tick)
        {
            a.Conn.Send(new MapHashMessage { MapId = 0, Tick = tick, Total = 1 });
            b.Conn.Send(new MapHashMessage { MapId = 0, Tick = tick, Total = 2 });
            s.Pump();
        }
        Pair(Tick + 60);
        Pair(Tick + 60 + ProtocolLimits.DesyncNoticeIntervalTicks);
        Pair(Tick + 60 + 2 * ProtocolLimits.DesyncNoticeIntervalTicks);
        a.Drain();
        Assert.Single(a.Ordered(ProtocolIds.RequestWorldSnapshotKind));

        // После тика снимка расхождение — снова лечится.
        Pair(s.TickUpTo + 60);
        a.Drain();
        Assert.Equal(2, a.Ordered(ProtocolIds.RequestWorldSnapshotKind).Count());
    }

    [Fact]
    public void ClockDoesNotRunAheadOfTheSlowestPlayer()
    {
        Session s = NewSession(maxLead: 120);
        C a = Join(s, "a", 10, withWorld: true);
        C b = Join(s, "b", 10, withWorld: true);
        a.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        b.Conn.Send(new SpeedRequestMessage { Speed = 3 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(5)); // без ограничения было бы +1800
        Assert.Equal(Tick + 120, s.TickUpTo);

        a.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = 1 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Tick + 120, s.TickUpTo); // b ещё не сообщил, где он

        b.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = 1 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Tick + 180, s.TickUpTo);
    }

    [Fact]
    public void ResyncSenderIsChosenFromTheMajority()
    {
        Session s = NewSession();
        C broken = Join(s, "broken", 1, withWorld: true);   // лучший пинг, но испорченный мир
        C x = Join(s, "x", 50, withWorld: true);
        C y = Join(s, "y", 30, withWorld: true);
        void Report(C c, ulong total) { c.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = total }); s.Pump(); }
        Report(broken, 7);
        Report(x, 1);           // расхождение замечено — ждём третий хеш
        x.Drain();
        Assert.Empty(x.Ordered(ProtocolIds.RequestWorldSnapshotKind));
        Report(y, 1);
        x.Drain();
        CommandOrderedMessage order = x.Ordered(ProtocolIds.RequestWorldSnapshotKind).Single();
        Assert.Equal(y.Id, WorldSnapshotOrder.Sender(order.Payload)); // лучший пинг среди большинства (x, y)
    }

    [Fact]
    public void ResyncWaitsForMissingHashesOnlyUntilTheDeadline()
    {
        Session s = NewSession();
        C a = Join(s, "a", 40, withWorld: true);
        C b = Join(s, "b", 20, withWorld: true);
        C silent = Join(s, "silent", 5, withWorld: true);
        a.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = 1 });
        b.Conn.Send(new MapHashMessage { MapId = 0, Tick = Tick + 60, Total = 2 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(1));
        a.Drain();
        Assert.Empty(a.Ordered(ProtocolIds.RequestWorldSnapshotKind));
        s.Advance(TimeSpan.FromSeconds(1.5));
        a.Drain();
        // Большинства нет (1:1) — лучший пинг среди всех с миром.
        Assert.Equal(silent.Id, WorldSnapshotOrder.Sender(a.Ordered(ProtocolIds.RequestWorldSnapshotKind).Single().Payload));
    }

    [Fact]
    public void SaveNowOrdersASaveFromTheBestPingPlayer()
    {
        Session s = NewSession(autosave: TimeSpan.FromHours(1));
        Assert.Null(s.SaveNow()); // никого нет
        C a = Join(s, "a", 50, withWorld: true);
        C b = Join(s, "b", 10, withWorld: true);
        Assert.Equal(b.Id, s.SaveNow());
        a.Drain();
        Assert.Single(a.Ordered(ProtocolIds.SaveWorldSnapshotKind));
    }
}
