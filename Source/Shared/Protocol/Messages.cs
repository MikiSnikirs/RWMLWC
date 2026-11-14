using System;
using System.Collections.Generic;

namespace RWMLWC.Shared.Protocol;

/// <summary>
/// Тип сообщения — первые 2 байта кадра. Номера не переиспользуются:
/// удалённый тип оставляет дыру, новый получает следующий номер.
/// </summary>
public enum MessageType : ushort
{
    Hello = 1,
    Welcome = 2,
    Reject = 3,
    Command = 4,
    CommandOrdered = 5,
    Tick = 6,
    SpeedRequest = 7,
    SnapshotChunk = 8,
    MapHash = 9,
    MapClaim = 10,
    GrantPermission = 11,
    Permissions = 12,
    CommandRejected = 13,
    Notice = 14,
    SnapshotApplied = 15,
    Players = 16,
}

/// <summary>
/// Уровень прав игрока на карте (T-031). Владелец карты всегда <see cref="Full"/>.
/// Карта без владельца и уровень мира (<see cref="ProtocolIds.WorldMapId"/>) — <see cref="Full"/> для всех.
/// </summary>
public enum PermissionLevel : byte
{
    /// <summary>Нет доступа.</summary>
    None = 0,
    /// <summary>Видит карту, команд не отдаёт.</summary>
    Observer = 1,
    /// <summary>Строительство и разметка (дезигнаторы, чертежи, зоны).</summary>
    Build = 2,
    /// <summary>Всё, включая приказы пешкам.</summary>
    Full = 3,
}

/// <summary>Причина отказа сервера в подключении.</summary>
public enum RejectReason : byte
{
    Other = 0,
    ProtocolMismatch = 1,
    ModMismatch = 2,
    GameVersionMismatch = 3,
    ModListMismatch = 4,
    ServerFull = 5,
    NameTaken = 6,
}

/// <summary>Константы полей.</summary>
public static class ProtocolIds
{
    /// <summary><c>MapId</c> для команд и данных уровня мира, а не карты.</summary>
    public const int WorldMapId = -1;
}

public abstract class Message
{
    public abstract MessageType Type { get; }
    public abstract void Write(PacketWriter w);
}

/// <summary>Клиент → сервер, первое сообщение после подключения.</summary>
public sealed class HelloMessage : Message
{
    public override MessageType Type => MessageType.Hello;

    /// <summary>Версия протокола клиента; сервер сверяет её с <see cref="ProtocolInfo.ProtocolVersion"/>.</summary>
    public int ProtocolVersion = ProtocolInfo.ProtocolVersion;
    public string ModId = ProtocolInfo.ModId;
    /// <summary>Версия игры, например «1.7.5005».</summary>
    public string GameVersion = "";
    public string PlayerName = "";
    /// <summary>Хеш списка модов и их версий. Как он считается — решает клиент; сервер только сравнивает.</summary>
    public ulong ModListHash;
    /// <summary>Мир локальной копии сейва у игрока (<c>WorldInfo.persistentRandomValue</c>); 0 — копии нет (T-044, v2).</summary>
    public ulong SaveWorldId;
    /// <summary>Игровой тик, на котором снята локальная копия сейва (T-044, v2); 0 — нет. Больше тик того же мира — новее копия.</summary>
    public int SaveTick;

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(ProtocolVersion);
        w.WriteString(ModId);
        w.WriteString(GameVersion);
        w.WriteString(PlayerName);
        w.WriteUInt64(ModListHash);
        w.WriteUInt64(SaveWorldId);
        w.WriteInt32(SaveTick);
    }

    internal static HelloMessage Read(ref PacketReader r) => new()
    {
        ProtocolVersion = r.ReadInt32(),
        ModId = r.ReadString(),
        GameVersion = r.ReadString(),
        PlayerName = r.ReadString(),
        ModListHash = r.ReadUInt64(),
        SaveWorldId = r.ReadUInt64(),
        SaveTick = r.ReadInt32(),
    };
}

/// <summary>Сервер → клиент: подключение принято.</summary>
public sealed class WelcomeMessage : Message
{
    public override MessageType Type => MessageType.Welcome;

    /// <summary>Номер игрока в сессии, выдаёт сервер.</summary>
    public int PlayerId;
    /// <summary>Тик общих часов на момент входа.</summary>
    public int ServerTick;
    /// <summary>Зерно сессии: от него клиент считает начальные состояния <c>Rand</c> карт (<c>PerMapSim.SessionSeed</c>).</summary>
    public uint SessionSeed;
    /// <summary>Мир, который хранит сервер; 0 — сервер ещё не получал сейв (T-044, v2).</summary>
    public ulong SaveWorldId;
    /// <summary>Тик последней копии сейва на сервере; 0 — нет (T-044, v2).</summary>
    public int SaveTick;

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(PlayerId);
        w.WriteInt32(ServerTick);
        w.WriteUInt32(SessionSeed);
        w.WriteUInt64(SaveWorldId);
        w.WriteInt32(SaveTick);
    }

    internal static WelcomeMessage Read(ref PacketReader r) => new()
    {
        PlayerId = r.ReadInt32(),
        ServerTick = r.ReadInt32(),
        SessionSeed = r.ReadUInt32(),
        SaveWorldId = r.ReadUInt64(),
        SaveTick = r.ReadInt32(),
    };
}

/// <summary>Сервер → клиент: подключение отклонено, после сообщения сервер закрывает соединение.</summary>
public sealed class RejectMessage : Message
{
    public override MessageType Type => MessageType.Reject;

    public RejectReason Reason;
    /// <summary>Текст для игрока.</summary>
    public string Detail = "";

    public override void Write(PacketWriter w)
    {
        w.WriteByte((byte)Reason);
        w.WriteString(Detail);
    }

    internal static RejectMessage Read(ref PacketReader r) => new()
    {
        Reason = (RejectReason)r.ReadByte(),
        Detail = r.ReadString(),
    };
}

/// <summary>
/// Клиент → сервер: команда игрока. Сервер не разбирает <see cref="Payload"/>,
/// он назначает порядок и тик исполнения и рассылает <see cref="CommandOrderedMessage"/>.
/// </summary>
public sealed class CommandMessage : Message
{
    public override MessageType Type => MessageType.Command;

    /// <summary>Номер команды у этого клиента, растёт с 1. Нужен, чтобы клиент узнал свою команду в рассылке.</summary>
    public int ClientSeq;
    /// <summary>Карта (<c>Map.uniqueID</c>) или <see cref="ProtocolIds.WorldMapId"/>.</summary>
    public int MapId;
    /// <summary>Вид команды. Реестр видов ведёт клиент (зона A, T-020–T-023).</summary>
    public int Kind;
    /// <summary>
    /// Какой уровень прав нужен на карте <see cref="MapId"/>, чтобы сервер принял команду (T-031).
    /// Его ставит механизм перехвата ввода: разметка и чертежи — <see cref="PermissionLevel.Build"/>, остальное — <see cref="PermissionLevel.Full"/>.
    /// </summary>
    public PermissionLevel Required = PermissionLevel.Full;
    public byte[] Payload = Array.Empty<byte>();

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(ClientSeq);
        w.WriteInt32(MapId);
        w.WriteInt32(Kind);
        w.WriteByte((byte)Required);
        w.WriteBytes(Payload);
    }

    internal static CommandMessage Read(ref PacketReader r) => new()
    {
        ClientSeq = r.ReadInt32(),
        MapId = r.ReadInt32(),
        Kind = r.ReadInt32(),
        Required = ReadLevel(ref r),
        Payload = r.ReadBytes(),
    };

    internal static PermissionLevel ReadLevel(ref PacketReader r)
    {
        byte b = r.ReadByte();
        if (b > (byte)PermissionLevel.Full) throw new ProtocolException($"Неверный уровень прав: {b}");
        return (PermissionLevel)b;
    }
}

/// <summary>
/// Сервер → все клиенты: команда в общем порядке. Клиенты исполняют команды тика
/// <see cref="Tick"/> по возрастанию <see cref="Seq"/>.
/// </summary>
public sealed class CommandOrderedMessage : Message
{
    public override MessageType Type => MessageType.CommandOrdered;

    /// <summary>Глобальный номер команды в сессии, без пропусков.</summary>
    public long Seq;
    /// <summary>Тик, на котором команду исполняют все клиенты.</summary>
    public int Tick;
    public int PlayerId;
    public int ClientSeq;
    public int MapId;
    public int Kind;
    public byte[] Payload = Array.Empty<byte>();

    public override void Write(PacketWriter w)
    {
        w.WriteInt64(Seq);
        w.WriteInt32(Tick);
        w.WriteInt32(PlayerId);
        w.WriteInt32(ClientSeq);
        w.WriteInt32(MapId);
        w.WriteInt32(Kind);
        w.WriteBytes(Payload);
    }

    internal static CommandOrderedMessage Read(ref PacketReader r) => new()
    {
        Seq = r.ReadInt64(),
        Tick = r.ReadInt32(),
        PlayerId = r.ReadInt32(),
        ClientSeq = r.ReadInt32(),
        MapId = r.ReadInt32(),
        Kind = r.ReadInt32(),
        Payload = r.ReadBytes(),
    };
}

/// <summary>
/// Сервер → все клиенты: разрешение тикать. Клиент может исполнить тики до <see cref="TickUpTo"/> включительно;
/// все команды для этих тиков уже разосланы раньше этого сообщения.
/// </summary>
public sealed class TickMessage : Message
{
    public override MessageType Type => MessageType.Tick;

    public int TickUpTo;
    /// <summary>Общая скорость: 0 — пауза, 1..4 — как <c>TimeSpeed</c> игры.</summary>
    public byte Speed;

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(TickUpTo);
        w.WriteByte(Speed);
    }

    internal static TickMessage Read(ref PacketReader r) => new()
    {
        TickUpTo = r.ReadInt32(),
        Speed = r.ReadByte(),
    };
}

/// <summary>Клиент → сервер: скорость, которую выбрал игрок. Сервер применяет правило меньшей скорости.</summary>
public sealed class SpeedRequestMessage : Message
{
    public override MessageType Type => MessageType.SpeedRequest;

    /// <summary>0 — пауза, 1..4 — как <c>TimeSpeed</c> игры.</summary>
    public byte Speed;

    public override void Write(PacketWriter w) => w.WriteByte(Speed);

    internal static SpeedRequestMessage Read(ref PacketReader r) => new() { Speed = r.ReadByte() };
}

/// <summary>
/// Кусок снимка карты (в обе стороны: владелец → сервер → гость). Снимок режется на куски
/// по <see cref="ProtocolLimits.SnapshotChunkBytes"/>; формат и сжатие данных — T-030 (зона A).
/// </summary>
public sealed class SnapshotChunkMessage : Message
{
    public override MessageType Type => MessageType.SnapshotChunk;

    /// <summary>Номер снимка у отправителя; куски одного снимка имеют один номер.</summary>
    public int SnapshotId;
    public int MapId;
    /// <summary>Тик, на котором снят снимок.</summary>
    public int Tick;
    public int ChunkIndex;
    public int ChunkCount;
    public byte[] Data = Array.Empty<byte>();

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(SnapshotId);
        w.WriteInt32(MapId);
        w.WriteInt32(Tick);
        w.WriteInt32(ChunkIndex);
        w.WriteInt32(ChunkCount);
        w.WriteBytes(Data);
    }

    internal static SnapshotChunkMessage Read(ref PacketReader r)
    {
        var m = new SnapshotChunkMessage
        {
            SnapshotId = r.ReadInt32(),
            MapId = r.ReadInt32(),
            Tick = r.ReadInt32(),
            ChunkIndex = r.ReadInt32(),
            ChunkCount = r.ReadInt32(),
            Data = r.ReadBytes(),
        };
        if (m.ChunkCount < 1 || m.ChunkIndex < 0 || m.ChunkIndex >= m.ChunkCount)
            throw new ProtocolException($"Неверный номер куска снимка: {m.ChunkIndex} из {m.ChunkCount}");
        return m;
    }
}

/// <summary>Одна часть хеша карты (подсистема), например «things», «pawns», «rand».</summary>
public readonly record struct HashPart(string Name, ulong Value);

/// <summary>
/// Клиент → сервер: хеш состояния карты на тике. Сервер сравнивает хеши игроков по (MapId, Tick).
/// Части передаются по имени, чтобы новые подсистемы хеша (зона A, <c>MapStateHasher</c>) не меняли протокол.
/// </summary>
public sealed class MapHashMessage : Message
{
    public override MessageType Type => MessageType.MapHash;

    public int MapId;
    public int Tick;
    public ulong Total;
    public List<HashPart> Parts = new();

    public override void Write(PacketWriter w)
    {
        if (Parts.Count > ProtocolLimits.MaxHashParts)
            throw new ProtocolException($"Частей хеша больше {ProtocolLimits.MaxHashParts}");
        w.WriteInt32(MapId);
        w.WriteInt32(Tick);
        w.WriteUInt64(Total);
        w.WriteInt32(Parts.Count);
        foreach (HashPart p in Parts)
        {
            w.WriteString(p.Name);
            w.WriteUInt64(p.Value);
        }
    }

    internal static MapHashMessage Read(ref PacketReader r)
    {
        var m = new MapHashMessage
        {
            MapId = r.ReadInt32(),
            Tick = r.ReadInt32(),
            Total = r.ReadUInt64(),
        };
        // Минимум на часть: 4 байта длины строки + 8 байт значения.
        int count = r.ReadCount(ProtocolLimits.MaxHashParts, 12);
        m.Parts = new List<HashPart>(count);
        for (int i = 0; i < count; i++)
            m.Parts.Add(new HashPart(r.ReadString(), r.ReadUInt64()));
        return m;
    }
}

/// <summary>Клиент → сервер: «эта карта моя» (основал поселение, хост своей колонии). Свободную карту сервер отдаёт заявителю.</summary>
public sealed class MapClaimMessage : Message
{
    public override MessageType Type => MessageType.MapClaim;

    public int MapId;

    public override void Write(PacketWriter w) => w.WriteInt32(MapId);

    internal static MapClaimMessage Read(ref PacketReader r) => new() { MapId = r.ReadInt32() };
}

/// <summary>Клиент → сервер: владелец карты выдаёт игроку уровень прав; <see cref="PermissionLevel.None"/> — отзыв.</summary>
public sealed class GrantPermissionMessage : Message
{
    public override MessageType Type => MessageType.GrantPermission;

    public int MapId;
    public int PlayerId;
    public PermissionLevel Level;

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(MapId);
        w.WriteInt32(PlayerId);
        w.WriteByte((byte)Level);
    }

    internal static GrantPermissionMessage Read(ref PacketReader r) => new()
    {
        MapId = r.ReadInt32(),
        PlayerId = r.ReadInt32(),
        Level = CommandMessage.ReadLevel(ref r),
    };
}

/// <summary>Выданные права на одной карте.</summary>
public readonly record struct PermissionGrant(int PlayerId, PermissionLevel Level);

/// <summary>Владелец и выданные права одной карты.</summary>
public sealed class MapPermissions
{
    public int MapId;
    public int OwnerPlayerId;
    public List<PermissionGrant> Grants = new();
}

/// <summary>Сервер → все: полная таблица владельцев и прав. Рассылается при входе игрока и при каждом изменении.</summary>
public sealed class PermissionsMessage : Message
{
    public override MessageType Type => MessageType.Permissions;

    public List<MapPermissions> Maps = new();

    public override void Write(PacketWriter w)
    {
        if (Maps.Count > ProtocolLimits.MaxMaps) throw new ProtocolException($"Карт больше {ProtocolLimits.MaxMaps}");
        w.WriteInt32(Maps.Count);
        foreach (MapPermissions m in Maps)
        {
            if (m.Grants.Count > ProtocolLimits.MaxGrantsPerMap) throw new ProtocolException($"Выданных прав больше {ProtocolLimits.MaxGrantsPerMap}");
            w.WriteInt32(m.MapId);
            w.WriteInt32(m.OwnerPlayerId);
            w.WriteInt32(m.Grants.Count);
            foreach (PermissionGrant g in m.Grants)
            {
                w.WriteInt32(g.PlayerId);
                w.WriteByte((byte)g.Level);
            }
        }
    }

    internal static PermissionsMessage Read(ref PacketReader r)
    {
        var msg = new PermissionsMessage();
        int maps = r.ReadCount(ProtocolLimits.MaxMaps, 12);
        for (int i = 0; i < maps; i++)
        {
            var m = new MapPermissions { MapId = r.ReadInt32(), OwnerPlayerId = r.ReadInt32() };
            int grants = r.ReadCount(ProtocolLimits.MaxGrantsPerMap, 5);
            for (int j = 0; j < grants; j++)
                m.Grants.Add(new PermissionGrant(r.ReadInt32(), CommandMessage.ReadLevel(ref r)));
            msg.Maps.Add(m);
        }
        return msg;
    }
}

/// <summary>Сервер → автору: команда отклонена (нет прав) и никому не разослана.</summary>
public sealed class CommandRejectedMessage : Message
{
    public override MessageType Type => MessageType.CommandRejected;

    public int ClientSeq;
    public int MapId;
    public PermissionLevel Required;
    public PermissionLevel Had;

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(ClientSeq);
        w.WriteInt32(MapId);
        w.WriteByte((byte)Required);
        w.WriteByte((byte)Had);
    }

    internal static CommandRejectedMessage Read(ref PacketReader r) => new()
    {
        ClientSeq = r.ReadInt32(),
        MapId = r.ReadInt32(),
        Required = CommandMessage.ReadLevel(ref r),
        Had = CommandMessage.ReadLevel(ref r),
    };
}

/// <summary>Вид сообщения <see cref="NoticeMessage"/>.</summary>
public enum NoticeCode : ushort
{
    Info = 0,
    /// <summary>У игрока копия сейва новее, чем на сервере: сервер, вероятно, запущен со старой копии (T-044).</summary>
    NewerSaveOnClient = 1,
    /// <summary>У игрока копия другого мира.</summary>
    DifferentWorld = 2,
    /// <summary>
    /// Хеши карты у игроков разошлись (v3, для T-034). Текст: «карта M тик T: игрок X ≠ игрок Y».
    /// Сервер шлёт всем игрокам в сети, не чаще раза в <see cref="ProtocolLimits.DesyncNoticeIntervalTicks"/> тиков на карту.
    /// </summary>
    Desync = 3,
}

/// <summary>Сервер → клиенту: предупреждение или сведение для показа игроку; соединение не рвёт.</summary>
public sealed class NoticeMessage : Message
{
    public override MessageType Type => MessageType.Notice;

    public NoticeCode Code;
    public string Text = "";

    public override void Write(PacketWriter w)
    {
        w.WriteUInt16((ushort)Code);
        w.WriteString(Text);
    }

    internal static NoticeMessage Read(ref PacketReader r) => new()
    {
        Code = (NoticeCode)r.ReadUInt16(),
        Text = r.ReadString(),
    };
}

/// <summary>
/// Клиент → сервер: снимок загружен, клиент готов тикать с <see cref="Tick"/>. Сервер возобновляет тики,
/// когда подтвердили все, кто загружает снимок (правило «загружают все», запись A 15:10; поток — T-032/T-034/T-045).
/// </summary>
public sealed class SnapshotAppliedMessage : Message
{
    public override MessageType Type => MessageType.SnapshotApplied;

    public int SnapshotId;
    public int Tick;

    public override void Write(PacketWriter w)
    {
        w.WriteInt32(SnapshotId);
        w.WriteInt32(Tick);
    }

    internal static SnapshotAppliedMessage Read(ref PacketReader r) => new()
    {
        SnapshotId = r.ReadInt32(),
        Tick = r.ReadInt32(),
    };
}

/// <summary>Игрок сессии в сообщении <see cref="PlayersMessage"/>.</summary>
public readonly record struct PlayerInfo(int PlayerId, string Name, bool Online);

/// <summary>
/// Сервер → клиентам: состав сессии (v3). Все игроки, которые когда-либо входили, по возрастанию номера:
/// ушедший остаётся в списке с <c>Online = false</c> (его номер и права сохраняются до его возвращения).
/// Рассылается всем в сети при входе, возврате и уходе любого игрока; новый игрок получает его сразу после <c>Welcome</c>.
/// </summary>
public sealed class PlayersMessage : Message
{
    public override MessageType Type => MessageType.Players;

    public List<PlayerInfo> Players = new();

    public override void Write(PacketWriter w)
    {
        if (Players.Count > ProtocolLimits.MaxPlayersInList) throw new ProtocolException($"Игроков больше {ProtocolLimits.MaxPlayersInList}");
        w.WriteInt32(Players.Count);
        foreach (PlayerInfo p in Players)
        {
            w.WriteInt32(p.PlayerId);
            w.WriteString(p.Name);
            w.WriteBool(p.Online);
        }
    }

    internal static PlayersMessage Read(ref PacketReader r)
    {
        var msg = new PlayersMessage();
        int n = r.ReadCount(ProtocolLimits.MaxPlayersInList, 9);
        for (int i = 0; i < n; i++)
            msg.Players.Add(new PlayerInfo(r.ReadInt32(), r.ReadString(), r.ReadBool()));
        return msg;
    }
}
