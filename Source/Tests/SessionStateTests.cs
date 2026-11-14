using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RWMLWC.Net;
using RWMLWC.Server;
using RWMLWC.Shared.Protocol;
using Xunit;

namespace RWMLWC.Tests;

/// <summary>TST-071: игроки и права переживают перезапуск сервера.</summary>
public sealed class SessionStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rwmlwc-state-" + Guid.NewGuid().ToString("N"), "session.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch { }
    }

    private Session NewSession() => new(new SessionOptions { State = new SessionStateFile(_path) });

    private static (IConnection conn, List<Message> got) Join(Session s, string name)
    {
        var (client, server) = InMemoryConnection.CreatePair(name, "server");
        s.Accept(server);
        client.Send(new HelloMessage { PlayerName = name, GameVersion = "1.7.5005", ModListHash = 7 });
        s.Pump();
        var got = new List<Message>();
        while (client.TryReceive(out Message m)) got.Add(m);
        return (client, got);
    }

    [Fact]
    public void PlayersColorsAndRightsSurviveARestart()
    {
        Session s1 = NewSession();
        var (a, ga) = Join(s1, "Miki");
        var (b, gb) = Join(s1, "Друг");
        int idA = ga.OfType<WelcomeMessage>().First().PlayerId, idB = gb.OfType<WelcomeMessage>().First().PlayerId;
        a.Send(new ChooseColorMessage { Color = 0xE53935 });
        a.Send(new MapClaimMessage { MapId = 9 });
        a.Send(new GrantPermissionMessage { MapId = 9, PlayerId = idB, Level = PermissionLevel.Full, UntilReturn = true });
        s1.Pump();
        Assert.True(File.Exists(_path));

        Session s2 = NewSession(); // «перезапуск»
        Assert.Equal(new[] { (idA, "Miki", false), (idB, "Друг", false) }, s2.Players.Select(p => (p.Id, p.Name, p.Online)));
        Assert.Equal(0xE53935, s2.Players.Single(p => p.Id == idA).Color);
        Assert.Equal(idA, s2.OwnerOf(9));
        Assert.Equal(PermissionLevel.Full, s2.LevelOf(idB, 9)); // владельца нет — управляющий действует

        var (b2, gb2) = Join(s2, "Друг");
        Assert.Equal(idB, gb2.OfType<WelcomeMessage>().First().PlayerId); // тот же номер
        var (c, gc) = Join(s2, "Новый");
        Assert.Equal(Math.Max(idA, idB) + 1, gc.OfType<WelcomeMessage>().First().PlayerId);
        // Восстановленные игроки объявлены «не в сети» в общем порядке.
        Assert.Contains(gb2.OfType<CommandOrderedMessage>().Where(o => o.Kind == ProtocolIds.PlayerPresenceKind)
            .Select(o => PlayerPresence.Decode(o.Payload)!.Value), x => x == (idA, false));
    }

    [Fact]
    public void BrokenStateFileDoesNotStopTheServer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ это не json");
        var log = new List<string>();
        var s = new Session(new SessionOptions { State = new SessionStateFile(_path) }, log.Add);
        Assert.Empty(s.Players);
        Assert.Contains(log, l => l.Contains("не прочитано"));
    }
}
