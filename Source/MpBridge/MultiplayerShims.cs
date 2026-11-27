using System;
using RimWorld;
using Verse;

// T-085: внутренности RimWorld Multiplayer, к которым Multiplayer Compatibility обращается в обход API.
// Здесь только те, у которых в модели RWMLWC верная и простая реализация. Остальные (своё время карт,
// фракции игроков, сессии) в RWMLWC устроены иначе — такие части Compatibility не включаются.

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// В Multiplayer у каждой карты своё время, и отметки времени в полях объектов приходится переводить.
    /// В RWMLWC время общее для всех карт — переводить нечего.
    /// </summary>
    public static class TimestampFixer
    {
        public static void RegisterTimestampFields(Type type, Action<object, int> convert) { }

        public static Map? GetPawnClockMap(Pawn pawn) => pawn?.MapHeld;
    }
}

namespace Multiplayer.Client.Compatibility
{
    /// <summary>
    /// В Multiplayer — может ли автор команды действовать за фракцию или пешку (у каждого игрока своя фракция).
    /// В RWMLWC фракция игроков одна, а права проверяются по картам и отрядам до исполнения команды.
    /// </summary>
    public static class CommandAuthority
    {
        public static bool CanActForFaction(Faction faction) => true;

        public static bool CanActForPawn(Pawn pawn) => true;
    }
}

namespace Multiplayer.Client.Util
{
    /// <summary>Область «действуем за фракцию»: в RWMLWC фракция игроков одна — область пустая.</summary>
    public static class MpScope
    {
        private sealed class Empty : IDisposable
        {
            public static readonly Empty Instance = new Empty();

            public void Dispose() { }
        }

        public static IDisposable PushFaction(Faction faction) => Empty.Instance;
    }
}
