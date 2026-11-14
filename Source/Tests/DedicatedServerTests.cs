using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-058: выделенный сервер — выбор тика пустой сессии, мир с сервера, консоль администратора.</summary>
public sealed class DedicatedServerTests : IDisposable
{
    private const ulong World = 0x00000000_5EED5EED;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwmlwc-dedicated-" + Guid.NewGuid().ToString("N"));

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
        public WelcomeMessage? Welcome => Got.OfType<WelcomeMessage>().FirstOrDefault();
    }

    private Session NewSession(byte[]? stored = null, int storedTick = 0, List<string>? log = null)
    {
        var store = new SaveStore(_dir);
        if (stored != null) store.Store(World, storedTick, stored);
        return new Session(new SessionOptions { StartTick = 0, SessionSeed = 1, Store = store, Dedicated = true }, (log ?? new List<string>()).Add);
    }

    private static C Join(Session s, string name, ulong world = 0, int saveTick = 0)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name, GameVersion = "1.7.5005", ModListHash = 7, SaveWorldId = world, SaveTick = saveTick });
        s.Pump();
        var c = new C(client);
        c.Drain();
        return c;
    }

    private static byte[] Assemble(IEnumerable<SnapshotChunkMessage> chunks)
    {
        var list = chunks.OrderBy(c => c.ChunkIndex).ToList();
        Assert.All(list, c => Assert.Equal(list.Count, c.ChunkCount));
        return list.SelectMany(c => c.Data).ToArray();
    }

    [Fact]
    public void EmptyServerWithoutWorldRejectsPlayerWithoutSave()
    {
        Session s = NewSession();
        C c = Join(s, "A");
        Assert.Contains("нет мира", Assert.IsType<RejectMessage>(c.Got.Single()).Detail);
        Assert.False(c.Conn.IsOpen);
    }

    [Fact]
    public void FirstPlayerWithSaveSetsTheClockWhenServerHasNoWorld()
    {
        Session s = NewSession();
        C c = Join(s, "A", World, 15000);
        Assert.Equal(15000, c.Welcome!.ServerTick);
    }

    [Fact]
    public void PlayerWithoutWorldGetsTheStoredCopyWhenAlone()
    {
        var stored = new byte[700_000];
        new Random(5).NextBytes(stored);
        Session s = NewSession(stored, 20000);
        C c = Join(s, "A");
        Assert.Equal(20000, c.Welcome!.ServerTick);

        c.Conn.Send(new CommandMessage { ClientSeq = 1, MapId = ProtocolIds.WorldMapId, Kind = Session.RequestWorldSnapshotKind, Required = PermissionLevel.Observer });
        s.Pump();
        c.Drain();

        CommandOrderedMessage request = c.Got.OfType<CommandOrderedMessage>().Single();
        var chunks = c.Got.OfType<SnapshotChunkMessage>().ToList();
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, ch =>
        {
            Assert.Equal((int)(request.Seq & 0x7FFFFFFF), ch.SnapshotId);
            Assert.Equal(ProtocolIds.WorldMapId, ch.MapId);
            Assert.Equal(20000, ch.Tick);
        });
        Assert.Equal(stored, Assemble(chunks));
    }

    [Fact]
    public void NewerSaveOfTheSameWorldWinsOverTheStoredCopy()
    {
        Session s = NewSession(new byte[] { 1, 2, 3 }, 20000);
        C c = Join(s, "A", World, 26000);
        Assert.Equal(26000, c.Welcome!.ServerTick);
    }

    [Fact]
    public void OlderSaveGetsTheStoredWorldTick()
    {
        Session s = NewSession(new byte[] { 1, 2, 3 }, 20000);
        C c = Join(s, "A", World, 18000);
        Assert.Equal(20000, c.Welcome!.ServerTick); // клиент увидит другой тик и попросит мир
    }

    [Fact]
    public void ClockReturnsToTheStoredWorldWhenTheSessionEmpties()
    {
        var log = new List<string>();
        Session s = NewSession(new byte[] { 1, 2, 3 }, 20000, log);
        C a = Join(s, "A", World, 20000);
        a.Conn.Send(new SpeedRequestMessage { Speed = 1 });
        s.Pump();
        s.Advance(TimeSpan.FromSeconds(2));
        Assert.True(s.TickUpTo > 20000);

        a.Conn.Close("ушёл");
        s.Pump();
        C b = Join(s, "B");
        Assert.Equal(20000, b.Welcome!.ServerTick);
        Assert.Contains(log, l => l.Contains("сессия пуста"));
    }

    [Fact]
    public void StoredWorldIsNotServedWhenSomeoneElseIsOnline()
    {
        Session s = NewSession(new byte[] { 1, 2, 3 }, 20000);
        C a = Join(s, "A", World, 20000);
        C b = Join(s, "B");
        b.Conn.Send(new CommandMessage { ClientSeq = 1, MapId = ProtocolIds.WorldMapId, Kind = Session.RequestWorldSnapshotKind, Required = PermissionLevel.Observer });
        s.Pump();
        b.Drain();
        Assert.Empty(b.Got.OfType<SnapshotChunkMessage>());
    }

    [Fact]
    public void HostedSessionDoesNotServeOrMoveTheClock()
    {
        var store = new SaveStore(_dir);
        store.Store(World, 20000, new byte[] { 1 });
        var s = new Session(new SessionOptions { StartTick = 15000, Store = store, Dedicated = false });
        C c = Join(s, "A");
        Assert.Equal(15000, c.Welcome!.ServerTick);
    }

    [Fact]
    public void ServerChatAndKick()
    {
        Session s = NewSession(new byte[] { 1 }, 20000);
        C a = Join(s, "A", World, 20000);
        C b = Join(s, "B", World, 20000);
        s.ServerChat("  перезапуск через 5 минут ");
        a.Drain();
        ChatMessage chat = a.Got.OfType<ChatMessage>().Single();
        Assert.Equal((Session.ServerPlayerId, "перезапуск через 5 минут"), (chat.PlayerId, chat.Text));

        Assert.True(s.Kick(b.Welcome!.PlayerId, "проверка"));
        Assert.False(b.Conn.IsOpen);
        Assert.False(s.Kick(b.Welcome!.PlayerId, "повтор"));
    }
}

public sealed class ConnectionCodeTests
{
    private const string Thumb = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("rwmlwc://192.168.1.5:47800/", "192.168.1.5", 47800)]
    [InlineData("  example.org:1234/", "example.org", 1234)]
    [InlineData("rwmlwc://[2001:db8::1]:47800/", "2001:db8::1", 47800)]
    public void ParsesWhatFormatProduces(string prefix, string host, int port)
    {
        Assert.True(RWMLWC.Net.ConnectionCode.TryParse(prefix + Thumb.ToLowerInvariant(), out string h, out int p, out string t));
        Assert.Equal((host, port, Thumb), (h, p, t));
        Assert.True(RWMLWC.Net.ConnectionCode.TryParse(RWMLWC.Net.ConnectionCode.Format(h, p, t), out string h2, out int p2, out string t2));
        Assert.Equal((h, p, t), (h2, p2, t2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rwmlwc://host:0/" + Thumb)]
    [InlineData("rwmlwc://host:70000/" + Thumb)]
    [InlineData("rwmlwc://host:47800/ABC")]
    [InlineData("76561198000000000")]
    public void RejectsGarbage(string text)
    {
        Assert.False(RWMLWC.Net.ConnectionCode.TryParse(text, out _, out _, out _));
    }
}
