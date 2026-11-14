using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Продолжение команды через интерфейс (T-021): команда игрока может не изменить игру сразу,
/// а открыть меню или начать выбор цели. Действия, которые за этим стоят, — замыкания; по сети
/// их не передать. Поэтому каждый клиент, исполняя команду, запоминает их у себя «за игрока»:
/// у автора меню и прицел настоящие, у остальных — только запомненные действия. Выбор автора
/// приходит следующей командой («строка N меню», «цель такая-то»), и каждый клиент вызывает своё
/// запомненное действие. Меню у всех построено из одного состояния игры, поэтому действия совпадают.
/// </summary>
public static class VirtualUi
{
    private const byte TargetByAction = 0;  // прицеливание с действием (Targeter.BeginTargeting(..., action))
    private const byte TargetBySource = 1;  // источник прицеливания, запомненный при BeginTargeting(source)
    private const byte TargetByVerb = 2;    // приказ глаголу пешки (несколько выделенных пешек)

    private sealed class PlayerUi
    {
        public int menuSerial;
        public Action?[]? menuActions;
        public int targetSerial;
        public Action<LocalTargetInfo>? targetAction;
        public ITargetingSource? targetSource;
    }

    private static readonly Dictionary<int, PlayerUi> players = new Dictionary<int, PlayerUi>();

    private static readonly AccessTools.FieldRef<FloatMenu, List<FloatMenuOption>> menuOptions =
        AccessTools.FieldRefAccess<FloatMenu, List<FloatMenuOption>>("options");

    public static void Reset()
    {
        players.Clear();
    }

    private static PlayerUi For(int playerId)
    {
        if (!players.TryGetValue(playerId, out PlayerUi? ui))
        {
            ui = new PlayerUi();
            players.Add(playerId, ui);
        }
        return ui;
    }

    // ---------- меню ----------

    /// <summary>Из префикса WindowStack.Add. false — окно не показывать.</summary>
    public static bool OnWindowAdd(Window window)
    {
        if (!CommandBus.Executing)
        {
            // Меню, открытое прямо из интерфейса методом, за объектом которого идёт наблюдение
            // (FieldWatch): действия строк исполняются под тем же наблюдением.
            object? watched = FieldWatch.AmbientTarget;
            if (watched != null && CommandBus.CaptureActive && window is FloatMenu direct)
            {
                foreach (FloatMenuOption option in menuOptions(direct))
                {
                    if (option.action != null)
                    {
                        option.action = FieldWatch.WrapMenuAction(watched, option.action);
                    }
                }
            }
            return true;
        }
        if (window is FloatMenu menu)
        {
            PlayerUi ui = For(CommandBus.CurrentPlayerId);
            List<FloatMenuOption> options = menuOptions(menu);
            int serial = ++ui.menuSerial;
            Action?[] actions = new Action?[options.Count];
            for (int i = 0; i < options.Count; i++)
            {
                actions[i] = options[i].action;
            }
            ui.menuActions = actions;
            if (CommandBus.ExecutingRemote)
            {
                return false;
            }
            // Автор: меню настоящее, но выбор строки уходит командой.
            Map? map = Find.CurrentMap;
            int mapId = map != null ? map.uniqueID : -1;
            for (int i = 0; i < options.Count; i++)
            {
                if (actions[i] == null)
                {
                    continue;
                }
                int index = i;
                options[i].action = () => SendMenuOption(mapId, serial, index);
            }
            return true;
        }
        if (CommandBus.ExecutingRemote)
        {
            // Окно, открытое чужой командой, этому игроку не показываем. Продолжение через такие окна — T-023.
            TestAutomation.Note("virtual ui: remote command opened window " + window.GetType().FullName + ", not shown");
            return false;
        }
        return true;
    }

    private static void SendMenuOption(int mapId, int serial, int index)
    {
        Map? map = FindMap(mapId);
        if (map == null || !LockstepClient.InSession)
        {
            return;
        }
        PacketWriter w = CommandBus.Begin(map);
        w.WriteInt32(serial);
        w.WriteInt32(index);
        CommandBus.Send(map, CommandKinds.MenuOption, PermissionLevel.Full, w);
    }

    /// <summary>Для тестовых сценариев: выбрать первую доступную строку меню, открытого командой этого игрока.</summary>
    public static bool TestChooseFirstOption(Map map)
    {
        PlayerUi ui = For(LockstepClient.PlayerId);
        Action?[]? actions = ui.menuActions;
        if (actions == null)
        {
            return false;
        }
        for (int i = 0; i < actions.Length; i++)
        {
            if (actions[i] != null)
            {
                SendMenuOption(map.uniqueID, ui.menuSerial, i);
                return true;
            }
        }
        return false;
    }

    public static void ExecuteMenuOption(ref PacketReader r)
    {
        int serial = r.ReadInt32();
        int index = r.ReadInt32();
        PlayerUi ui = For(CommandBus.CurrentPlayerId);
        Action?[]? actions = ui.menuActions;
        if (actions == null || ui.menuSerial != serial || index < 0 || index >= actions.Length)
        {
            TestAutomation.Note($"virtual ui: menu {serial} option {index} is stale, dropped");
            return;
        }
        ui.menuActions = null; // меню закрывается после выбора
        actions[index]?.Invoke();
    }

    // ---------- выбор цели ----------

    /// <summary>Из префикса Targeter.BeginTargeting с действием. false — прицеливание не начинать.</summary>
    public static bool OnBeginTargeting(ref Action<LocalTargetInfo> action)
    {
        if (!CommandBus.Executing || action == null)
        {
            return true;
        }
        PlayerUi ui = For(CommandBus.CurrentPlayerId);
        int serial = ++ui.targetSerial;
        ui.targetAction = action;
        ui.targetSource = null;
        if (CommandBus.ExecutingRemote)
        {
            return false;
        }
        Map? map = Find.CurrentMap;
        int mapId = map != null ? map.uniqueID : -1;
        action = target => SendTarget(mapId, TargetByAction, serial, target, null);
        return true;
    }

    /// <summary>Из префикса Targeter.BeginTargeting с источником (глагол, способность). false — не начинать.</summary>
    public static bool OnBeginTargetingSource(ITargetingSource source)
    {
        if (!CommandBus.Executing)
        {
            return true;
        }
        PlayerUi ui = For(CommandBus.CurrentPlayerId);
        ui.targetSerial++;
        ui.targetAction = null;
        ui.targetSource = source;
        return !CommandBus.ExecutingRemote;
    }

    /// <summary>Из префиксов OrderForceTarget. true — выполнить на месте.</summary>
    public static bool OnOrderForceTarget(ITargetingSource source, LocalTargetInfo target)
    {
        if (!CommandBus.CaptureActive)
        {
            return true;
        }
        Map? map = Find.CurrentMap;
        if (map == null)
        {
            return true;
        }
        PlayerUi ui = For(LockstepClient.PlayerId);
        if (ReferenceEquals(ui.targetSource, source))
        {
            SendTarget(map.uniqueID, TargetBySource, ui.targetSerial, target, null);
            return false;
        }
        if (source is Verb verb && verb.caster != null && verb.caster.Spawned && verb.caster.Map == map
            && ReferenceEquals(FindVerb(verb.caster, verb.loadID), verb))
        {
            SendTarget(map.uniqueID, TargetByVerb, 0, target, verb);
            return false;
        }
        Messages.Message("RWMLWC: этот приказ пока не работает в сетевой игре", MessageTypeDefOf.RejectInput, false);
        return false;
    }

    private static void SendTarget(int mapId, byte form, int serial, LocalTargetInfo target, Verb? verb)
    {
        Map? map = FindMap(mapId);
        if (map == null || !LockstepClient.InSession)
        {
            return;
        }
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(form);
        w.WriteInt32(serial);
        if (form == TargetByVerb)
        {
            w.WriteInt32(verb!.caster.thingIDNumber);
            w.WriteString(verb.loadID ?? "");
        }
        bool hasThing = target.HasThing;
        w.WriteBool(hasThing);
        if (hasThing)
        {
            w.WriteInt32(target.Thing.thingIDNumber);
        }
        else
        {
            ValueCodec.WriteCell(w, target.Cell);
        }
        CommandBus.Send(map, CommandKinds.TargetPicked, PermissionLevel.Full, w);
    }

    public static void ExecuteTargetPicked(Map map, ref PacketReader r)
    {
        byte form = r.ReadByte();
        int serial = r.ReadInt32();
        int casterId = -1;
        string verbId = "";
        if (form == TargetByVerb)
        {
            casterId = r.ReadInt32();
            verbId = r.ReadString();
        }
        LocalTargetInfo target;
        if (r.ReadBool())
        {
            Thing? thing = ValueCodec.FindThing(map, r.ReadInt32());
            if (thing == null)
            {
                TestAutomation.Note("virtual ui: target thing is gone, dropped");
                return;
            }
            target = new LocalTargetInfo(thing);
        }
        else
        {
            target = new LocalTargetInfo(ValueCodec.ReadCell(ref r));
        }

        PlayerUi ui = For(CommandBus.CurrentPlayerId);
        switch (form)
        {
            case TargetByAction:
                if (ui.targetSerial != serial || ui.targetAction == null)
                {
                    TestAutomation.Note($"virtual ui: targeting {serial} is stale, dropped");
                    return;
                }
                ui.targetAction(target);
                break;
            case TargetBySource:
                if (ui.targetSerial != serial || ui.targetSource == null)
                {
                    TestAutomation.Note($"virtual ui: targeting source {serial} is stale, dropped");
                    return;
                }
                ui.targetSource.OrderForceTarget(target);
                break;
            case TargetByVerb:
            {
                Thing? caster = ValueCodec.FindThing(map, casterId);
                Verb? verb = caster != null ? FindVerb(caster, verbId) : null;
                if (verb == null)
                {
                    TestAutomation.Note($"virtual ui: verb {verbId} not found, dropped");
                    return;
                }
                verb.OrderForceTarget(target);
                break;
            }
            default:
                throw new ProtocolException("выбор цели: неизвестная форма " + form);
        }
    }

    /// <summary>Глагол вещи по его идентификатору из сейва (loadID): свои, оружия, одежды, способностей.</summary>
    private static Verb? FindVerb(Thing caster, string? loadId)
    {
        if (string.IsNullOrEmpty(loadId))
        {
            return null;
        }
        if (caster is Pawn pawn)
        {
            if (pawn.VerbTracker != null)
            {
                foreach (Verb verb in pawn.VerbTracker.AllVerbs)
                {
                    if (verb.loadID == loadId)
                    {
                        return verb;
                    }
                }
            }
            if (pawn.equipment != null)
            {
                foreach (Verb verb in pawn.equipment.AllEquipmentVerbs)
                {
                    if (verb.loadID == loadId)
                    {
                        return verb;
                    }
                }
            }
            if (pawn.apparel != null)
            {
                foreach (Verb verb in pawn.apparel.AllApparelVerbs)
                {
                    if (verb.loadID == loadId)
                    {
                        return verb;
                    }
                }
            }
            if (pawn.abilities != null)
            {
                foreach (Ability ability in pawn.abilities.AllAbilitiesForReading)
                {
                    foreach (Verb verb in ability.VerbTracker.AllVerbs)
                    {
                        if (verb.loadID == loadId)
                        {
                            return verb;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static Map? FindMap(int mapId)
    {
        if (Current.Game == null)
        {
            return null;
        }
        List<Map> maps = Find.Maps;
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].uniqueID == mapId)
            {
                return maps[i];
            }
        }
        return null;
    }

    // ---------- установка перехвата OrderForceTarget ----------

    public static void PatchOrderForceTarget(Harmony harmony)
    {
        HarmonyMethod prefix = new HarmonyMethod(typeof(VirtualUi), nameof(OrderForceTargetPrefix));
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        int patched = 0;
        int failed = 0;
        foreach (Type type in GenTypes.AllTypes)
        {
            if (!typeof(ITargetingSource).IsAssignableFrom(type) || type.IsInterface || type.ContainsGenericParameters)
            {
                continue;
            }
            MethodInfo? method = type.GetMethod(nameof(ITargetingSource.OrderForceTarget), flags, null, new[] { typeof(LocalTargetInfo) }, null);
            if (method == null || method.IsAbstract || method.GetMethodBody() == null)
            {
                continue;
            }
            try
            {
                harmony.Patch(method, prefix: prefix);
                patched++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"[RWMLWC] цели: не удалось перехватить {type.FullName}.OrderForceTarget: {ex.Message}");
            }
        }
        Log.Message($"[RWMLWC] цели: перехвачено методов {patched}, не удалось {failed}");
    }

    private static bool OrderForceTargetPrefix(object __instance, LocalTargetInfo __0)
    {
        return !(__instance is ITargetingSource source) || OnOrderForceTarget(source, __0);
    }
}

[StaticConstructorOnStartup]
internal static class VirtualUiStartup
{
    static VirtualUiStartup()
    {
        try
        {
            VirtualUi.PatchOrderForceTarget(RwmlwcMod.HarmonyInstance);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] цели: установка перехвата упала: " + ex);
        }
    }
}

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
internal static class Patch_WindowStack_Add
{
    private static bool Prefix(Window window) => VirtualUi.OnWindowAdd(window);
}

// Прицеливание с действием: все перегрузки BeginTargeting, у которых есть параметр action.
[HarmonyPatch]
internal static class Patch_Targeter_BeginTargeting_Action
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in typeof(Targeter).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.Name != nameof(Targeter.BeginTargeting))
            {
                continue;
            }
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (parameter.Name == "action" && parameter.ParameterType == typeof(Action<LocalTargetInfo>))
                {
                    yield return method;
                    break;
                }
            }
        }
    }

    private static bool Prefix(ref Action<LocalTargetInfo> action) => VirtualUi.OnBeginTargeting(ref action);
}

// Прицеливание с источником (глагол, способность): единственная перегрузка без действия.
[HarmonyPatch]
internal static class Patch_Targeter_BeginTargeting_Source
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in typeof(Targeter).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.Name != nameof(Targeter.BeginTargeting))
            {
                continue;
            }
            ParameterInfo[] parameters = method.GetParameters();
            bool hasAction = false;
            foreach (ParameterInfo parameter in parameters)
            {
                hasAction |= parameter.Name == "action";
            }
            if (!hasAction && parameters.Length > 0 && parameters[0].ParameterType == typeof(ITargetingSource))
            {
                yield return method;
            }
        }
    }

    private static bool Prefix(ITargetingSource __0) => VirtualUi.OnBeginTargetingSource(__0);
}

// Чужая команда не должна сбрасывать прицеливание этого игрока.
[HarmonyPatch(typeof(Targeter), nameof(Targeter.StopTargeting))]
internal static class Patch_Targeter_StopTargeting
{
    private static bool Prefix() => !CommandBus.ExecutingRemote;
}

// Клавиши в симуляции. Код приказов читает клавишу «в очередь» (Pawn_JobTracker.TryTakeOrderedJob)
// прямо с клавиатуры этого компьютера. В тике и при исполнении команды ответ должен быть одинаков
// у всех: в тике клавиш нет, при исполнении команды — то, что держал её автор.
[HarmonyPatch]
internal static class Patch_KeyBindingDef_InSimulation
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "IsDownEvent", "KeyDownEvent", "JustPressed", "IsDown" })
        {
            MethodInfo? getter = AccessTools.PropertyGetter(typeof(KeyBindingDef), name);
            if (getter != null)
            {
                yield return getter;
            }
        }
    }

    private static bool Prefix(KeyBindingDef __instance, ref bool __result)
    {
        if (!MapRandContext.InTick)
        {
            return true;
        }
        CommandContext? context = CommandBus.Context;
        __result = context != null && context.queueOrder && __instance == KeyBindingDefOf.QueueOrder;
        return false;
    }
}

// Мышь при исполнении команды. Код приказов местами сам спрашивает, где указатель (например,
// GenUI.ThingsUnderMouse выбирает предмет из нескольких в одной клетке по расстоянию до мыши).
// При исполнении команды указатель у всех там, где он был у автора.
[HarmonyPatch(typeof(UI), nameof(UI.MouseMapPosition))]
internal static class Patch_UI_MouseMapPosition
{
    private static bool Prefix(ref UnityEngine.Vector3 __result)
    {
        CommandContext? context = CommandBus.Context;
        if (context == null)
        {
            return true;
        }
        __result = context.mouseMapPosition;
        return false;
    }
}

[HarmonyPatch(typeof(UI), nameof(UI.MouseCell))]
internal static class Patch_UI_MouseCell
{
    private static bool Prefix(ref IntVec3 __result)
    {
        CommandContext? context = CommandBus.Context;
        if (context == null)
        {
            return true;
        }
        __result = IntVec3.FromVector3(context.mouseMapPosition);
        return false;
    }
}
