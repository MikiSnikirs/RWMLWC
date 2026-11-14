using System;
using System.Collections.Generic;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>Виды команд игрока (поле Kind в Command). Номера не переиспользуются.</summary>
public static class CommandKinds
{
    /// <summary>Разметка: вызов Designator.DesignateSingleCell / DesignateMultiCell / DesignateThing (T-020).</summary>
    public const int Designate = 1;

    /// <summary>Нажатие кнопки выделенного объекта (гизмо): призыв, переключатели, действия (T-021).</summary>
    public const int GizmoClick = 2;

    /// <summary>Выбор строки в меню, которое открыла предыдущая команда этого игрока (T-021).</summary>
    public const int MenuOption = 3;

    /// <summary>Выбор цели после команды, которая начала прицеливание (T-021).</summary>
    public const int TargetPicked = 4;

    /// <summary>Правый щелчок по карте при выделенных пешках: приказ сразу или контекстное меню (T-022).</summary>
    public const int MapRightClick = 5;

    /// <summary>Приказ призванной пешке идти в клетку (перемещение группы с расстановкой) (T-022).</summary>
    public const int PawnGoto = 6;

    /// <summary>Вызов метода игры, которым окно или вкладка меняет настройку (приоритеты работ, расписание, политики) (T-023).</summary>
    public const int MethodCall = 7;

    /// <summary>Запись поля объекта, которое окно меняет напрямую, без метода (счета, медицина) (T-023).</summary>
    public const int FieldSet = 8;

    /// <summary>Изменение отрядов: создать, переименовать, удалить, участники, перевод пешек (T-060).</summary>
    public const int SquadEdit = 9;

    /// <summary>Режим отсутствия карты: стазис, автопилот, управляющий (T-072).</summary>
    public const int AbsenceMode = 10;

    /// <summary>Сформировать караван из окна (T-073): пешки, вещи с количествами, точки сбора и выхода, клетки мира.</summary>
    public const int CaravanForm = 11;

    /// <summary>Действие на карте мира (T-073): основать поселение, маршрут каравана.</summary>
    public const int WorldAction = 12;
}

/// <summary>
/// Общий путь команды игрока (T-020): действие в интерфейсе не исполняется сразу, а уходит серверу
/// (<see cref="Send"/>); сервер ставит его в общий порядок, и все клиенты, включая автора, исполняют его
/// между одними и теми же тиками (<see cref="Execute"/>).
///
/// Вместе с командой едет «обстановка» интерфейса автора, от которой зависит результат: текущая карта,
/// выделенные объекты, режим бога. На время исполнения она подставляется у каждого клиента.
/// </summary>
public static class CommandBus
{
    /// <summary>Сейчас исполняется команда из общего порядка: перехват ввода отключён.</summary>
    public static bool Executing { get; private set; }

    /// <summary>Исполняется команда другого игрока: звуки, сообщения и выбор инструмента на этом клиенте не трогаем.</summary>
    public static bool ExecutingRemote { get; private set; }

    /// <summary>Игрок — автор исполняемой команды.</summary>
    public static int CurrentPlayerId { get; private set; }

    /// <summary>Номер исполняемой команды в общем порядке (для журнала).</summary>
    public static long CurrentSeq { get; private set; }

    /// <summary>Обстановка автора исполняемой команды; вне исполнения — null.</summary>
    public static CommandContext? Context { get; private set; }

    public static int Executed { get; private set; }

    public static int Failed { get; private set; }

    /// <summary>Исполнено команд этого игрока (его собственных) — для проверки, что прогон их дождался.</summary>
    public static int ExecutedOwn { get; private set; }

    /// <summary>Действие игрока надо перехватить: идёт сессия, это не исполнение команды и не тик симуляции.</summary>
    public static bool CaptureActive => LockstepClient.InSession && !Executing && !MapRandContext.InTick;

    public static void Reset()
    {
        Executing = false;
        ExecutingRemote = false;
        Context = null;
        Executed = 0;
        ExecutedOwn = 0;
        Failed = 0;
        RWMLWC.Client.Maps.SquadRules.Reset();
        VirtualUi.Reset();
    }

    /// <summary>Начать команду: записывает обстановку автора. Дальше вызывающий дописывает данные команды и зовёт Send.</summary>
    public static PacketWriter Begin(Map map, UnityEngine.Vector3? mouseMapPosition = null)
    {
        PacketWriter w = new PacketWriter(256);
        CommandContext context = CommandContext.Capture(map);
        if (mouseMapPosition.HasValue)
        {
            context.mouseMapPosition = mouseMapPosition.Value; // щелчок: мышь там, где щёлкнули
        }
        context.Write(w);
        return w;
    }

    public static void Send(Map map, int kind, PermissionLevel required, PacketWriter payload)
    {
        if (ActsOnSelection(kind) && !RWMLWC.Client.Maps.SquadRules.LocalMay(Find.Selector.SelectedObjects))
        {
            return; // отряды (T-061): приказ чужим пешкам не отправляем
        }
        LockstepClient.SendCommand(map.uniqueID, kind, required, payload.ToArray());
    }

    /// <summary>Команда действует на выделенные объекты автора (кнопки, меню, цель, правый щелчок, «идти»).</summary>
    private static bool ActsOnSelection(int kind) =>
        kind == CommandKinds.GizmoClick || kind == CommandKinds.MenuOption || kind == CommandKinds.TargetPicked
        || kind == CommandKinds.MapRightClick || kind == CommandKinds.PawnGoto;

    /// <summary>Исполнить команду из общего порядка. Вызывается между тиками, у всех клиентов на одном и том же тике.</summary>
    public static void Execute(CommandOrderedMessage command)
    {
        Game game = Current.Game;
        if (game == null)
        {
            return;
        }
        Map? map = null;
        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].uniqueID == command.MapId)
            {
                map = maps[i];
                break;
            }
        }
        // Команды карты мира (T-073): своей карты нет; обстановка автора разбирается на первой карте (выделение там пустое).
        if (map == null && command.MapId == ProtocolIds.WorldMapId && command.Kind == CommandKinds.WorldAction && maps.Count > 0)
        {
            map = maps[0];
        }
        if (map == null)
        {
            Failed++;
            TestAutomation.Note($"command #{command.Seq}: map {command.MapId} not found, skipped");
            return;
        }

        bool own = command.PlayerId == LockstepClient.PlayerId;
        sbyte previousMapIndex = game.currentMapIndex;
        bool previousGodMode = DebugSettings.godMode;
        List<object> selection = Find.Selector.SelectedObjects;
        List<object>? savedSelection = null;
        bool beganTick = !MapRandContext.InTick;
        try
        {
            PacketReader reader = new PacketReader(command.Payload);
            CommandContext context = CommandContext.Read(ref reader);

            game.currentMapIndex = (sbyte)maps.IndexOf(map);
            List<object> contextSelection = context.Resolve(map);
            if (!own || !CommandContext.SameObjects(selection, contextSelection, map))
            {
                savedSelection = new List<object>(selection);
                selection.Clear();
                selection.AddRange(contextSelection);
            }
            DebugSettings.godMode = context.godMode;

            Executing = true;
            ExecutingRemote = !own;
            CurrentPlayerId = command.PlayerId;
            CurrentSeq = command.Seq;
            Context = context;
            // Как в тике карты: Rand и номера новых вещей — из состояния этой карты.
            if (beganTick)
            {
                MapRandContext.BeginTick();
            }
            MapRandContext.Enter(map);

            // Отряды (T-061): команда по выделению с чужой для автора пешкой пропускается у всех.
            if (ActsOnSelection(command.Kind)
                && RWMLWC.Client.Maps.SquadRules.RejectAtExecution(RWMLWC.Client.Maps.SquadRules.ForbiddenAny(contextSelection, command.PlayerId), command.Seq))
            {
                Executed++; // команда дошла и обработана (отклонена) — для проверки «свои команды исполнены»
                if (own)
                {
                    ExecutedOwn++;
                }
                return;
            }

            switch (command.Kind)
            {
                case CommandKinds.Designate:
                    DesignatorCommands.Execute(map, ref reader);
                    break;
                case CommandKinds.GizmoClick:
                    GizmoCommands.Execute(map, ref reader);
                    break;
                case CommandKinds.MenuOption:
                    VirtualUi.ExecuteMenuOption(ref reader);
                    break;
                case CommandKinds.TargetPicked:
                    VirtualUi.ExecuteTargetPicked(map, ref reader);
                    break;
                case CommandKinds.MapRightClick:
                    MapClickCommands.ExecuteRightClick(map, ref reader);
                    break;
                case CommandKinds.PawnGoto:
                    MapClickCommands.ExecuteGoto(map, ref reader);
                    break;
                case CommandKinds.MethodCall:
                    MethodCommands.Execute(map, ref reader);
                    break;
                case CommandKinds.FieldSet:
                    FieldWatch.Execute(map, ref reader);
                    break;
                case CommandKinds.SquadEdit:
                    RWMLWC.Client.Maps.Squads.Execute(map, ref reader);
                    break;
                case CommandKinds.AbsenceMode:
                    RWMLWC.Client.Maps.AbsenceModes.Execute(map, ref reader);
                    break;
                case CommandKinds.CaravanForm:
                    WorldCommands.ExecuteCaravanForm(map, ref reader);
                    break;
                case CommandKinds.WorldAction:
                    WorldCommands.ExecuteWorldAction(ref reader);
                    break;
                default:
                    Failed++;
                    TestAutomation.Note($"command #{command.Seq}: unknown kind {command.Kind}");
                    break;
            }
            Executed++;
            if (own)
            {
                ExecutedOwn++;
            }
        }
        catch (Exception ex)
        {
            Failed++;
            TestAutomation.Note($"command #{command.Seq} kind {command.Kind} FAILED: {ex}");
            Log.Error($"[RWMLWC] команда #{command.Seq} (вид {command.Kind}) упала: {ex}");
        }
        finally
        {
            if (beganTick)
            {
                MapRandContext.EndTick();
            }
            Executing = false;
            ExecutingRemote = false;
            Context = null;
            DebugSettings.godMode = previousGodMode;
            if (!own && savedSelection != null)
            {
                // Чужая команда: вернуть игроку его выделение.
                selection.Clear();
                selection.AddRange(savedSelection);
            }
            game.currentMapIndex = previousMapIndex;
        }
    }
}

/// <summary>Обстановка интерфейса автора команды в момент действия.</summary>
public sealed class CommandContext
{
    private const byte RefThing = 1;
    private const byte RefZone = 2;
    private const int MaxSelection = 256;

    public bool godMode;

    /// <summary>Автор держал клавишу «в очередь» (Shift): приказ добавляется к текущим, а не заменяет их.</summary>
    public bool queueOrder;

    /// <summary>Где на карте был указатель мыши автора. Код приказов местами спрашивает мышь сам.</summary>
    public UnityEngine.Vector3 mouseMapPosition;

    private readonly List<(byte kind, int id)> selection = new List<(byte, int)>();

    public static CommandContext Capture(Map map)
    {
        CommandContext context = new CommandContext { godMode = DebugSettings.godMode };
        try
        {
            context.queueOrder = KeyBindingDefOf.QueueOrder.IsDownEvent;
        }
        catch (Exception)
        {
            context.queueOrder = false;
        }
        try
        {
            context.mouseMapPosition = UI.MouseMapPosition();
        }
        catch (Exception)
        {
            context.mouseMapPosition = default;
        }
        List<object> selected = Find.Selector.SelectedObjects;
        for (int i = 0; i < selected.Count && context.selection.Count < MaxSelection; i++)
        {
            if (TryRef(selected[i], map, out (byte, int) reference))
            {
                context.selection.Add(reference);
            }
        }
        return context;
    }

    private static bool TryRef(object obj, Map map, out (byte kind, int id) reference)
    {
        if (obj is Thing thing && thing.Spawned && thing.Map == map)
        {
            reference = (RefThing, thing.thingIDNumber);
            return true;
        }
        if (obj is Zone zone && zone.Map == map)
        {
            reference = (RefZone, zone.ID);
            return true;
        }
        reference = default;
        return false;
    }

    public void Write(PacketWriter w)
    {
        w.WriteBool(godMode);
        w.WriteBool(queueOrder);
        w.WriteInt32(BitConverter.SingleToInt32Bits(mouseMapPosition.x));
        w.WriteInt32(BitConverter.SingleToInt32Bits(mouseMapPosition.z));
        w.WriteInt32(selection.Count);
        foreach ((byte kind, int id) in selection)
        {
            w.WriteByte(kind);
            w.WriteInt32(id);
        }
    }

    public static CommandContext Read(ref PacketReader r)
    {
        CommandContext context = new CommandContext { godMode = r.ReadBool() };
        context.queueOrder = r.ReadBool();
        float mouseX = BitConverter.Int32BitsToSingle(r.ReadInt32());
        float mouseZ = BitConverter.Int32BitsToSingle(r.ReadInt32());
        context.mouseMapPosition = new UnityEngine.Vector3(mouseX, 0f, mouseZ);
        int count = r.ReadCount(MaxSelection, 5);
        for (int i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            context.selection.Add((kind, r.ReadInt32()));
        }
        return context;
    }

    /// <summary>Объекты выделения автора на этом клиенте (которых нет — пропускаются).</summary>
    public List<object> Resolve(Map map)
    {
        List<object> result = new List<object>(selection.Count);
        foreach ((byte kind, int id) in selection)
        {
            object? obj = kind switch
            {
                RefThing => ValueCodec.FindThing(map, id),
                RefZone => ValueCodec.FindZone(map, id),
                _ => null,
            };
            if (obj != null)
            {
                result.Add(obj);
            }
        }
        return result;
    }

    /// <summary>Текущее выделение (та его часть, что передаётся по сети) совпадает с выделением из команды.</summary>
    public static bool SameObjects(List<object> current, List<object> fromCommand, Map map)
    {
        int index = 0;
        for (int i = 0; i < current.Count; i++)
        {
            if (!TryRef(current[i], map, out _))
            {
                continue;
            }
            if (index >= fromCommand.Count || !ReferenceEquals(current[i], fromCommand[index]))
            {
                return false;
            }
            index++;
        }
        return index == fromCommand.Count;
    }
}
