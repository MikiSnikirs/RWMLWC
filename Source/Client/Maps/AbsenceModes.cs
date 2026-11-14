using System.Collections.Generic;
using System.Linq;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>Режим карты, пока её владельца нет в игре (docs/ARCHITECTURE.md, раздел 4).</summary>
public enum AbsenceMode : byte
{
    /// <summary>Карта не тикает (по умолчанию).</summary>
    Stasis = 0,

    /// <summary>Пешки живут сами; крупных угроз нет.</summary>
    Autopilot = 1,

    /// <summary>Как автопилот, другу действуют выданные «пока меня нет» права.</summary>
    Manager = 2,
}

/// <summary>
/// Режимы отсутствия по картам (T-072). Хранятся в сейве и меняются только командой из общего порядка —
/// одинаковы у всех игроков, их читает симуляция (угрозы, стазис). Окно выбора — агента B, оно зовёт <see cref="AbsenceModes.Set"/>.
/// </summary>
public sealed class AbsenceModesComponent : GameComponent
{
    private Dictionary<int, AbsenceMode> modes = new Dictionary<int, AbsenceMode>();
    private List<int>? keysWork;
    private List<AbsenceMode>? valuesWork;

    public AbsenceModesComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref modes, "rwmlwcAbsenceModes", LookMode.Value, LookMode.Value, ref keysWork, ref valuesWork);
        modes ??= new Dictionary<int, AbsenceMode>();
    }

    public AbsenceMode Get(int mapId) => modes.TryGetValue(mapId, out AbsenceMode mode) ? mode : AbsenceMode.Stasis;

    internal void SetDirect(int mapId, AbsenceMode mode)
    {
        if (mode == AbsenceMode.Stasis)
        {
            modes.Remove(mapId);
        }
        else
        {
            modes[mapId] = mode;
        }
    }

    /// <summary>Сводка для сравнения у игроков: «карта:режим», по возрастанию номера карты.</summary>
    public string Digest() => string.Join(",", modes.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
}

public static class AbsenceModes
{
    public static AbsenceModesComponent? Component => Current.Game?.GetComponent<AbsenceModesComponent>();

    public static AbsenceMode Get(Map map) => Component?.Get(map.uniqueID) ?? AbsenceMode.Stasis;

    /// <summary>Выбрать режим отсутствия карты. В сессии — командой (у всех на одном тике), без сессии — сразу.</summary>
    public static void Set(Map map, AbsenceMode mode)
    {
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte((byte)mode);
        if (LockstepClient.InSession)
        {
            CommandBus.Send(map, CommandKinds.AbsenceMode, PermissionLevel.Full, w);
            return;
        }
        PacketReader r = new PacketReader(w.ToArray());
        CommandContext.Read(ref r);
        Execute(map, ref r);
    }

    public static void Execute(Map map, ref PacketReader r)
    {
        byte value = r.ReadByte();
        if (value > (byte)AbsenceMode.Manager)
        {
            TestAutomation.Note($"absence: unknown mode {value}, skipped");
            return;
        }
        Component?.SetDirect(map.uniqueID, (AbsenceMode)value);
        TestAutomation.Note($"absence: map {map.uniqueID} -> {(AbsenceMode)value} by player {CommandBus.CurrentPlayerId}");
    }
}
