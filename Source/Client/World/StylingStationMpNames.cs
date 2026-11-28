using RimWorld;
using Verse;

// Имена классов и полей — как в RimWorld Multiplayer: модуль Compatibility для HAR (Multiplayer.Compat.AlienRace)
// находит их по имени (AccessTools.TypeByName) и патчит OnAccept, чтобы досылать цвета и части тела рас.
// Логика — RWMLWC.Client.World.StylingStationSync.
namespace Multiplayer.Client.Patches
{
    /// <summary>Копия пешки в окне станции стиля; origPawn — настоящая пешка.</summary>
    public class StylingDialog_DummyPawn : Pawn
    {
        public Pawn origPawn = null!;
    }

    /// <summary>Окно станции, которое сейчас рисуется (на время DoWindowContents).</summary>
    public static class StylingDialog_Marker
    {
        public static Dialog_StylingStation? drawing;
    }

    public static class StylingDialog_HandleAccept
    {
        /// <summary>Нажато «Принять» в окне <see cref="StylingDialog_Marker.drawing"/>.</summary>
        internal static void Press() => OnAccept();

        // Без параметров и с этим именем: на него ставит префикс и постфикс модуль HAR.
        private static void OnAccept()
        {
            Dialog_StylingStation? drawing = StylingDialog_Marker.drawing;
            if (drawing == null)
            {
                return;
            }
            RWMLWC.Client.World.StylingStationSync.Accept(drawing);
            Verse.Window window = drawing;
            window.Close();
        }
    }
}
