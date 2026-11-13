using HarmonyLib;
using RWMLWC.Shared;
using Verse;

namespace RWMLWC.Client;

/// <summary>
/// Точка входа мода. Игра создаёт экземпляр при загрузке модов.
/// Здесь только инициализация: логика живёт в Sim/, Input/, Maps/, Net/, World/, Absence/, UI/.
/// </summary>
public sealed class RwmlwcMod : Mod
{
    public static Harmony HarmonyInstance { get; private set; } = null!;

    public RwmlwcMod(ModContentPack content) : base(content)
    {
        HarmonyInstance = new Harmony(ProtocolInfo.ModId);
        HarmonyInstance.PatchAll(typeof(RwmlwcMod).Assembly);

        Log.Message($"[RWMLWC] загружен, протокол v{ProtocolInfo.ProtocolVersion}");
    }
}
