using System;
using System.Collections.Generic;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-040 и TST-068: простой игрока, тревога, пароль сервера.</summary>
public sealed class IdleAlarmTests
{
    private sealed class C
    {
        public readonly IConnection Conn;
        public readonly List<Message> Got = new();
        public C(IConnection conn) => Conn = conn;
        public void Drain() { while (Conn.TryReceive(out Message m)) Got.Add(m); }
        public int Id => Got.OfType<WelcomeMessage>().First().PlayerId;
    }

    private static C Join(Session s, string name, string password = "")
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name, GameVersion = "1.7.5005", ModListHash = 7, Password = password });
        s.Pump();
        var c = new C(client);
        c.Drain();
        return c;
    }

    private static void Speed(Session s, params (C c, byte speed)[] req)
    {
        foreach (var (c, sp) in req) c.Conn.Send(new SpeedRequestMessage { Speed = sp });
        s.Pump();
    }

    [Theory]
    [InlineData("", "на сервере пароль")]
    [InlineData("не тот", "неверный пароль")]
    public void PasswordIsRequired(string given, string expected)
    {
        var s = new Session(new SessionOptions { Password = "секрет" });
        C c = Join(s, "A", given);
        RejectMessage r = Assert.IsType<RejectMessage>(c.Got.Single());
        Assert.Equal(RejectReason.WrongPassword, r.Reason);
        Assert.Contains(expected, r.Detail);
    }

    [Fact]
    public void RightPasswordOrNoPasswordOnServerLetsIn()
    {
        Assert.IsType<WelcomeMessage>(Join(new Session(new SessionOptions { Password = "секрет" }), "A", "секрет").Got[0]);
        Assert.IsType<WelcomeMessage>(Join(new Session(new SessionOptions()), "A", "что угодно").Got[0]);
    }

    [Fact]
    public void HelloWithoutPasswordFieldStillDecodes()
    {
        // Старый клиент (до v6) шлёт Hello без пароля: кодек читает его, сервер отвечает «другая версия протокола».
        var w = new PacketWriter();
        w.WriteInt32(5); w.WriteString(ProtocolInfo.ModId); w.WriteString("1.7"); w.WriteString("old");
        w.WriteUInt64(1); w.WriteUInt64(0); w.WriteInt32(0);
        byte[] body = new byte[2 + w.Length];
        body[0] = (byte)MessageType.Hello; body[1] = 0;
        w.ToArray().CopyTo(body, 2);
        var hello = Assert.IsType<HelloMessage>(ProtocolCodec.DecodeBody(body));
        Assert.Equal("", hello.Password);
        Assert.Equal(5, hello.ProtocolVersion);
    }

    [Fact]
    public void IdlePlayerDoesNotHoldTheSpeed()
    {
        var s = new Session(new SessionOptions());
        C a = Join(s, "A");
        C b = Join(s, "B");
        Speed(s, (a, 3), (b, 0)); // b на паузе
        Assert.Equal(0, s.Speed);
        b.Conn.Send(new PlayerStateMessage { Idle = true });
        s.Pump();
        Assert.Equal(3, s.Speed); // b отошёл — не держит
        a.Drain();
        Assert.True(a.Got.OfType<PlayersMessage>().Last().Players.Single(p => p.PlayerId == b.Id).Idle);

        a.Conn.Send(new PlayerStateMessage { Idle = true });
        s.Pump();
        Assert.Equal(0, s.Speed); // отошли все — решают все
        b.Conn.Send(new PlayerStateMessage { Idle = false });
        s.Pump();
        Assert.Equal(0, s.Speed); // b вернулся и стоит на паузе
    }

    private static (Session s, C owner, C other) TwoWithOwnedMap(int alarmSeconds = 45)
    {
        var s = new Session(new SessionOptions { AlarmSeconds = alarmSeconds });
        C owner = Join(s, "owner");
        C other = Join(s, "other");
        owner.Conn.Send(new MapClaimMessage { MapId = 5 });
        Speed(s, (owner, 3), (other, 3));
        return (s, owner, other);
    }

    [Fact]
    public void AlarmPausesEveryoneWhenTheOwnerIsAwayAndEndsWhenHeReturns()
    {
        var (s, owner, other) = TwoWithOwnedMap();
        owner.Conn.Send(new PlayerStateMessage { Idle = true });
        s.Pump();
        other.Conn.Send(new AlarmMessage { MapId = 5, Text = "Рейд" });
        s.Pump();
        Assert.Equal(0, s.Speed);
        Assert.True(s.AlarmActive);
        owner.Drain();
        AlarmMessage alarm = owner.Got.OfType<AlarmMessage>().Single();
        Assert.Equal((5, owner.Id, 45, "Рейд"), (alarm.MapId, alarm.PlayerId, alarm.Seconds, alarm.Text));

        owner.Conn.Send(new PlayerStateMessage { Idle = false });
        s.Pump();
        Assert.False(s.AlarmActive);
        Assert.Equal(3, s.Speed);
        other.Drain();
        Assert.Equal(0, other.Got.OfType<AlarmMessage>().Last().Seconds); // «тревога снята»
    }

    [Fact]
    public void AlarmEndsAfterItsTimeAndDoesNotRepeatAtOnce()
    {
        var (s, owner, other) = TwoWithOwnedMap(alarmSeconds: 30);
        owner.Conn.Send(new PlayerStateMessage { Idle = true });
        other.Conn.Send(new AlarmMessage { MapId = 5, Text = "Рейд" });
        s.Pump();
        Assert.Equal(0, s.Speed);
        s.Advance(TimeSpan.FromSeconds(31));
        Assert.False(s.AlarmActive);
        Assert.Equal(3, s.Speed); // отошедший владелец не держит скорость

        other.Conn.Send(new AlarmMessage { MapId = 5, Text = "Ещё рейд" });
        s.Pump();
        Assert.False(s.AlarmActive); // повтор по той же карте — не раньше чем через AlarmRepeatSeconds
    }

    [Fact]
    public void NoAlarmWhenTheOwnerIsActive()
    {
        var (s, _, other) = TwoWithOwnedMap();
        other.Conn.Send(new AlarmMessage { MapId = 5, Text = "Рейд" });
        s.Pump();
        Assert.False(s.AlarmActive);
        Assert.Equal(3, s.Speed);
    }
}
