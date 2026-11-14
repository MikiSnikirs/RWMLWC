using System;

namespace RWMLWC.Shared.Protocol;

/// <summary>Ошибка формата сообщения. Получатель должен разорвать соединение с отправителем.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Лимиты защищают от испорченных и злонамеренных данных.</summary>
public static class ProtocolLimits
{
    /// <summary>Максимальный размер кадра (тип + тело), без 4 байт длины.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Максимальная длина строки в байтах UTF-8.</summary>
    public const int MaxStringBytes = 64 * 1024;

    /// <summary>Максимальная длина блока данных (тело команды, кусок снимка).</summary>
    public const int MaxBlobBytes = MaxFrameBytes - 1024;

    /// <summary>Рекомендуемый размер куска снимка карты.</summary>
    public const int SnapshotChunkBytes = 256 * 1024;

    /// <summary>Максимум частей хеша карты в одном сообщении.</summary>
    public const int MaxHashParts = 256;

    /// <summary>Карт в таблице прав (в игре не больше 127).</summary>
    public const int MaxMaps = 128;

    /// <summary>Выданных прав на одну карту.</summary>
    public const int MaxGrantsPerMap = 32;

    /// <summary>Игроков в списке состава сессии (вместе с ушедшими).</summary>
    public const int MaxPlayersInList = 256;

    /// <summary>Не чаще одного <c>Notice Desync</c> на карту за столько тиков.</summary>
    public const int DesyncNoticeIntervalTicks = 600;

    /// <summary>Длина сообщения чата в символах; длиннее — сервер обрезает.</summary>
    public const int MaxChatChars = 500;

    /// <summary>Не больше стольких сообщений чата от игрока за <see cref="ChatWindowMs"/>; лишние сервер отбрасывает.</summary>
    public const int MaxChatPerWindow = 5;
    public const int ChatWindowMs = 5000;

    /// <summary>Сервер шлёт <c>Ping</c> каждому игроку раз в столько мс.</summary>
    public const int PingIntervalMs = 1000;

    /// <summary>Не чаще раза в столько мс сервер рассылает <c>Players</c> из-за изменения пинга.</summary>
    public const int PlayersRefreshMs = 2000;

    /// <summary>Курсоров от игрока в секунду (v5); лишние сервер отбрасывает.</summary>
    public const int MaxCursorsPerSecond = 10;

    /// <summary>Тревога по одной карте — не чаще раза в столько секунд (v6).</summary>
    public const int AlarmRepeatSeconds = 120;
}
