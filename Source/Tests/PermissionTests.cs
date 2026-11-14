using System;
using System.Collections.Generic;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-031: владельцы карт и уровни прав; команда без прав отклоняется сервером.</summary>
public class PermissionTests
{
    private sealed class C
    {
        public readonly IConnection Conn;
        public readonly List<Message> Got = new();
        public int Id;
        public int Seq;
        public C(IConnection conn) => Conn = conn;
        public void Drain() { while (Conn.TryReceive(out Message m)) Got.Add(m); }
        public void Cmd(int map, PermissionLevel req) => Conn.Send(new CommandMessage { ClientSeq = ++Seq, MapId = map, Kind = 1, Required = req });
        public PermissionsMessage LastPermissions => Got.OfType<PermissionsMessage>().Last();
    }

    private readonly Session _s = new(new SessionOptions { StartTick = 100 });
    private readonly List<C> _all = new();

    private C Join(string name)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        _s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name });
        var c = new C(client);
        _all.Add(c);
        Step();
        c.Id = c.Got.OfType<WelcomeMessage>().Single().PlayerId;
        return c;
    }

    private void Step()
    {
        _s.Pump();
        _s.Advance(TimeSpan.FromMilliseconds(10));
        foreach (C c in _all) c.Drain();
    }

    private static List<int> Accepted(C observer, C author) =>
        observer.Got.OfType<CommandOrderedMessage>().Where(m => m.PlayerId == author.Id).Select(m => m.ClientSeq).ToList();

    private static List<int> Rejected(C author) => author.Got.OfType<CommandRejectedMessage>().Select(m => m.ClientSeq).ToList();

    [Fact]
    public void UnownedMapAndWorldAreOpenToEveryone()
    {
        C a = Join("A"); C b = Join("B");
        b.Cmd(5, PermissionLevel.Full);
        b.Cmd(ProtocolIds.WorldMapId, PermissionLevel.Full);
        Step();
        Assert.Equal(new[] { 1, 2 }, Accepted(a, b));
        Assert.Empty(Rejected(b));
    }

    [Fact]
    public void OwnerControlsGuestByLevel()
    {
        C owner = Join("Owner"); C guest = Join("Guest");
        owner.Conn.Send(new MapClaimMessage { MapId = 0 });
        Step();
        Assert.Equal(owner.Id, guest.LastPermissions.Maps.Single(m => m.MapId == 0).OwnerPlayerId);

        // Нет прав: отклоняется всё, включая стройку; владельцу — можно всё.
        guest.Cmd(0, PermissionLevel.Build);                // 1 — отказ
        owner.Cmd(0, PermissionLevel.Full);
        Step();

        // Наблюдатель: команд не отдаёт.
        owner.Conn.Send(new GrantPermissionMessage { MapId = 0, PlayerId = guest.Id, Level = PermissionLevel.Observer });
        Step();
        guest.Cmd(0, PermissionLevel.Build);                // 2 — отказ
        Step();
        // Стройка: разметка да, приказы пешкам нет.
        owner.Conn.Send(new GrantPermissionMessage { MapId = 0, PlayerId = guest.Id, Level = PermissionLevel.Build });
        Step();
        guest.Cmd(0, PermissionLevel.Build);                // 3 — принята
        guest.Cmd(0, PermissionLevel.Full);                 // 4 — отказ
        Step();
        // Полный.
        owner.Conn.Send(new GrantPermissionMessage { MapId = 0, PlayerId = guest.Id, Level = PermissionLevel.Full });
        Step();
        guest.Cmd(0, PermissionLevel.Full);                 // 5 — принята
        Step();
        // Отзыв действует сразу, со следующей команды.
        owner.Conn.Send(new GrantPermissionMessage { MapId = 0, PlayerId = guest.Id, Level = PermissionLevel.None });
        guest.Cmd(0, PermissionLevel.Build);                // 6 — отказ (отзыв владельца обработан раньше: игроки идут по номеру)
        Step();

        Assert.Equal(new[] { 3, 5 }, Accepted(owner, guest));
        Assert.Equal(new[] { 3, 5 }, Accepted(guest, guest));
        Assert.Equal(new[] { 1, 2, 4, 6 }, Rejected(guest));
        Assert.Single(Accepted(guest, owner));
        var r = guest.Got.OfType<CommandRejectedMessage>().First(m => m.ClientSeq == 4);
        Assert.Equal((PermissionLevel.Full, PermissionLevel.Build), (r.Required, r.Had));
        // Отказы видит только автор.
        Assert.Empty(owner.Got.OfType<CommandRejectedMessage>());
        Assert.Empty(guest.LastPermissions.Maps.Single().Grants);
    }

    [Fact]
    public void OnlyOwnerCanGrantAndClaimIsFirstComeFirstServed()
    {
        C a = Join("A"); C b = Join("B"); C c = Join("C");
        a.Conn.Send(new MapClaimMessage { MapId = 1 });
        Step();
        b.Conn.Send(new MapClaimMessage { MapId = 1 });                                                  // чужая карта
        b.Conn.Send(new GrantPermissionMessage { MapId = 1, PlayerId = c.Id, Level = PermissionLevel.Full }); // не владелец
        a.Conn.Send(new MapClaimMessage { MapId = ProtocolIds.WorldMapId });                               // мир нельзя
        Step();
        Assert.Equal(a.Id, _s.OwnerOf(1));
        Assert.Equal(PermissionLevel.None, _s.LevelOf(c.Id, 1));
        Assert.Equal(0, _s.OwnerOf(ProtocolIds.WorldMapId));
        Assert.Single(c.LastPermissions.Maps);
    }

    [Fact]
    public void RightsSurviveReconnectByName()
    {
        C owner = Join("Owner"); C guest = Join("Guest");
        owner.Conn.Send(new MapClaimMessage { MapId = 0 });
        Step();
        owner.Conn.Send(new GrantPermissionMessage { MapId = 0, PlayerId = guest.Id, Level = PermissionLevel.Build });
        Step();
        guest.Conn.Close("обрыв");
        Step();
        C guest2 = Join("Guest");
        Assert.Equal(guest.Id, guest2.Id);
        var grant = guest2.LastPermissions.Maps.Single().Grants.Single();
        Assert.Equal(new PermissionGrant(guest.Id, PermissionLevel.Build), grant);
        guest2.Cmd(0, PermissionLevel.Build);
        Step();
        Assert.Single(Accepted(owner, guest2));
    }
}
