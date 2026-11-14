using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RWMLWC.Net;
using RWMLWC.Shared.Protocol;

namespace RWMLWC.Server;

/// <summary>
/// Состояние сессии на диске (T-071): игроки (номер, имя, цвет), следующий номер, владельцы и права карт.
/// Без него после перезапуска выделенного сервера номера игроков и владельцы карт выдавались бы заново,
/// а сейв и права ссылаются на номера. Файл — <c>session.json</c> в папке сервера; пишется атомарно.
/// </summary>
public sealed class SessionStateFile
{
    public sealed class PlayerRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Color { get; set; }
    }

    public sealed class GrantRecord
    {
        public int PlayerId { get; set; }
        public PermissionLevel Level { get; set; }
        public bool UntilReturn { get; set; }
    }

    public sealed class MapRecord
    {
        public int MapId { get; set; }
        public int Owner { get; set; }
        public List<GrantRecord> Grants { get; set; } = new();
    }

    public sealed class Data
    {
        public int NextPlayerId { get; set; } = 1;
        public List<PlayerRecord> Players { get; set; } = new();
        public List<MapRecord> Maps { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; }

    public SessionStateFile(string path) => Path = path;

    public Data? Load()
    {
        if (!File.Exists(Path)) return null;
        return JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Json);
    }

    public void Save(Data data)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>Соединение-заглушка для игроков, восстановленных с диска: они не в сети, пока не вернутся.</summary>
internal sealed class OfflineConnection : IConnection
{
    public static readonly OfflineConnection Instance = new();
    public string Remote => "не в сети";
    public bool IsOpen => false;
    public string? CloseReason => "не в сети";
    public Task Closed => Task.CompletedTask;
    public void Send(Message message) { }
    public bool TryReceive(out Message message) { message = null!; return false; }
    public ValueTask<Message> ReceiveAsync(CancellationToken ct = default) => throw new ConnectionClosedException("не в сети");
    public void Close(string reason) { }
    public void SendPing() { }
    public int RoundTripMs => -1;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
