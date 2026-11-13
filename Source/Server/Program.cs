using System;
using RWMLWC.Shared;

namespace RWMLWC.Server;

/// <summary>
/// Лёгкий сервер: хранит сейв, ведёт общие часы, упорядочивает команды. Игру не симулирует.
/// Сейчас это заглушка; логика появится в T-013.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine($"RWMLWC server, protocol v{ProtocolInfo.ProtocolVersion}");
        Console.WriteLine("Сервер ещё не реализован (см. TASKS.md, T-013).");
        return 0;
    }
}
