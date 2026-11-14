using System;
using System.Collections.Generic;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-032 (этап 0) и TST-042: «в сети / не в сети» в общем порядке, права управляющего.</summary>
public sealed class PresenceTests
{
    private sealed class C
    {
        public readonly IConnection Conn;
        public readonly List<Message> Got = new();
        public C(IConnection conn) => Conn = conn;
        public void Drain() { while (Conn.TryReceive(out Message m)) Got.Add(m); }
        public int Id => Got.OfType<WelcomeMessage>().First().PlayerId;
        public List<(int player, bool online)> Presence() => Got.OfType<CommandOrderedMessage>()
            .Where(c => c.Kind == ProtocolIds.PlayerPresenceKind)
            .Select(c => PlayerPresence.Decode(c.Payload)!.Value).ToList();
    }

    private static C Join(Session s, string name)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name, GameVersion = "1.7.5005", ModListHash = 7 });
        s.Pump();
        var c = new C(client);
        c.Drain();
        return c;
    }

    [Fact]
    public void PresenceIsOrderedOnJoinLeaveAndReturn()
    {
        var s = new Session(new SessionOptions { StartTick = 100 });
        C a = Join(s, "A");
        C b = Join(s, "B");
        b.Conn.Close("вылет");
        s.Pump();
        C b2 = Join(s, "B");
        a.Drain();
        Assert.Equal(new[] { (a.Id, true), (b.Id, true), (b.Id, false), (b.Id, true) }, a.Presence());
        CommandOrderedMessage first = a.Got.OfType<CommandOrderedMessage>().First();
        Assert.Equal((ProtocolIds.ServerPlayerId, ProtocolIds.WorldMapId, 100), (first.PlayerId, first.MapId, first.Tick));
        Assert.Contains((b2.Id, true), b2.Presence()); // вернувшийся тоже получает своё «в сети»
    }

    [Fact]
    public void PresenceIsNotRepeatedWithoutChange()
    {
        var s = new Session(new SessionOptions());
        C a = Join(s, "A");
        for (int i = 0; i < 5; i++) { s.Pump(); s.Advance(TimeSpan.FromSeconds(1)); }
        a.Drain();
        Assert.Single(a.Presence());
    }

    [Fact]
    public void ManagerRightsWorkOnlyWhileTheOwnerIsAwayAndAreDroppedOnReturn()
    {
        var s = new Session(new SessionOptions());
        C owner = Join(s, "owner");
        C friend = Join(s, "friend");
        owner.Conn.Send(new MapClaimMessage { MapId = 7 });
        owner.Conn.Send(new GrantPermissionMessage { MapId = 7, PlayerId = friend.Id, Level = PermissionLevel.Full, UntilReturn = true });
        s.Pump();
        Assert.Equal(PermissionLevel.None, s.LevelOf(friend.Id, 7)); // владелец здесь — управляющий не действует
        friend.Drain();
        PermissionGrant g = friend.Got.OfType<PermissionsMessage>().Last().Maps.Single(m => m.MapId == 7).Grants.Single();
        Assert.True(g.UntilReturn);

        owner.Conn.Close("ушёл");
        s.Pump();
        Assert.Equal(PermissionLevel.Full, s.LevelOf(friend.Id, 7)); // владельца нет — управляет друг

        Join(s, "owner");
        Assert.Equal(PermissionLevel.None, s.LevelOf(friend.Id, 7)); // вернулся — права сняты
        friend.Drain();
        Assert.Empty(friend.Got.OfType<PermissionsMessage>().Last().Maps.Single(m => m.MapId == 7).Grants);
    }

    [Fact]
    public void OrdinaryGrantSurvivesTheOwnersReturn()
    {
        var s = new Session(new SessionOptions());
        C owner = Join(s, "owner");
        C friend = Join(s, "friend");
        owner.Conn.Send(new MapClaimMessage { MapId = 7 });
        owner.Conn.Send(new GrantPermissionMessage { MapId = 7, PlayerId = friend.Id, Level = PermissionLevel.Build });
        s.Pump();
        owner.Conn.Close("ушёл");
        s.Pump();
        Join(s, "owner");
        Assert.Equal(PermissionLevel.Build, s.LevelOf(friend.Id, 7));
    }
}
