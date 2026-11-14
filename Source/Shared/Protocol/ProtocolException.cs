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
}
