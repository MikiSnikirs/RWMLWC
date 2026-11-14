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
/// Разметка (T-020): стройка, зоны, области, приказы на клетки и вещи.
///
/// Перехватываются три метода Designator — DesignateSingleCell, DesignateMultiCell, DesignateThing —
/// во всех классах игры и модов, где они объявлены. В сессии вызов из интерфейса не исполняется,
/// а уходит командой: тип инструмента, его состояние и аргументы. При исполнении у каждого клиента
/// создаётся такой же инструмент с тем же состоянием, и метод вызывается по-настоящему.
///
/// Состояние инструмента — его поля простых типов и ссылки на объекты игры (см. ValueCodec),
/// объявленные в классах ниже Designator. Так без отдельного кода передаются материал и поворот
/// постройки, цвет покраски, выбранная область и то, что добавят моды.
/// </summary>
public static class DesignatorCommands
{
    private const byte MethodCell = 0;
    private const byte MethodCells = 1;
    private const byte MethodThing = 2;
    private const int MaxFields = 128;
    private const int MaxCells = 100_000;

    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<Type, List<(byte depth, FieldInfo field)>> fieldsByType = new Dictionary<Type, List<(byte, FieldInfo)>>();
    private static readonly Dictionary<Type, bool> rebuildable = new Dictionary<Type, bool>();
    private static readonly AccessTools.FieldRef<DesignatorWithEyedropper, bool> eyedropModeRef =
        AccessTools.FieldRefAccess<DesignatorWithEyedropper, bool>("eyedropMode");

    /// <summary>Сколько вызовов перехвачено и отправлено командами (для тестов).</summary>
    public static int Captured { get; private set; }

    // ---------- перехват ----------

    /// <summary>Вызов из префикса. Возвращает true, если оригинальный метод надо выполнить на месте.</summary>
    public static bool Intercept(Designator designator, byte method, IntVec3 cell, IEnumerable<IntVec3>? cells, Thing? thing)
    {
        if (!CommandBus.CaptureActive)
        {
            return true;
        }
        // Пипетка только выбирает цвет в интерфейсе, состояние игры не меняет.
        if (designator is Designator_Eyedropper || (designator is DesignatorWithEyedropper withEyedropper && eyedropModeRef(withEyedropper)))
        {
            return true;
        }
        Map map = Find.CurrentMap;
        if (map == null)
        {
            return true;
        }
        Type type = designator.GetType();
        if (!CanRebuild(type) || designator is Designator_Plan_Copy || designator is Designator_Plan_CopySelectionPaste)
        {
            // Копирование и вставка планов держат скопированную форму в полях, которые не передаются.
            Messages.Message("RWMLWC: этот инструмент пока не работает в сетевой игре (" + type.Name + ")", MessageTypeDefOf.RejectInput, false);
            return false;
        }

        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(method);
        w.WriteString(type.FullName ?? "");
        WriteState(w, designator, type);
        switch (method)
        {
            case MethodCell:
                ValueCodec.WriteCell(w, cell);
                break;
            case MethodCells:
            {
                List<IntVec3> list = new List<IntVec3>(cells ?? Array.Empty<IntVec3>());
                w.WriteInt32(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    ValueCodec.WriteCell(w, list[i]);
                }
                break;
            }
            case MethodThing:
                w.WriteInt32(thing?.thingIDNumber ?? -1);
                break;
        }
        // Вся разметка требует прав «стройка» (контракт v2, Command.Required).
        CommandBus.Send(map, CommandKinds.Designate, PermissionLevel.Build, w);
        Captured++;
        return false;
    }

    // ---------- исполнение ----------

    public static void Execute(Map map, ref PacketReader r)
    {
        byte method = r.ReadByte();
        string typeName = r.ReadString();
        List<(FieldInfo? field, object? value)> state = ReadState(ref r, map, ValueCodec.FindType(typeName));

        IntVec3 cell = default;
        List<IntVec3>? cells = null;
        int thingId = -1;
        switch (method)
        {
            case MethodCell:
                cell = ValueCodec.ReadCell(ref r);
                break;
            case MethodCells:
            {
                int count = r.ReadCount(MaxCells, 12);
                cells = new List<IntVec3>(count);
                for (int i = 0; i < count; i++)
                {
                    cells.Add(ValueCodec.ReadCell(ref r));
                }
                break;
            }
            case MethodThing:
                thingId = r.ReadInt32();
                break;
            default:
                throw new ProtocolException("разметка: неизвестный метод " + method);
        }

        Type? type = ValueCodec.FindType(typeName);
        if (type == null || !typeof(Designator).IsAssignableFrom(type))
        {
            throw new InvalidOperationException("разметка: нет типа " + typeName);
        }
        Designator? designator = Create(type, state);
        if (designator == null)
        {
            throw new InvalidOperationException("разметка: не удалось создать " + typeName);
        }

        // Статические поля инструмента (например, выбранная область) — общие для интерфейса этого клиента:
        // после исполнения возвращаем их как были.
        List<(FieldInfo field, object? old)> restore = new List<(FieldInfo, object?)>();
        try
        {
            foreach ((FieldInfo? field, object? value) in state)
            {
                if (field == null || !Assignable(field.FieldType, value))
                {
                    continue;
                }
                if (field.IsStatic)
                {
                    restore.Add((field, field.GetValue(null)));
                    field.SetValue(null, value);
                }
                else
                {
                    field.SetValue(designator, value);
                }
            }

            // Состояние карты могло измениться с момента действия игрока — проверяем заново.
            switch (method)
            {
                case MethodCell:
                    if (designator.CanDesignateCell(cell).Accepted)
                    {
                        designator.DesignateSingleCell(cell);
                    }
                    break;
                case MethodCells:
                    designator.DesignateMultiCell(cells!);
                    break;
                case MethodThing:
                {
                    Thing? thing = ValueCodec.FindThing(map, thingId);
                    if (thing != null && designator.CanDesignateThing(thing).Accepted)
                    {
                        designator.DesignateThing(thing);
                    }
                    break;
                }
            }
        }
        finally
        {
            for (int i = restore.Count - 1; i >= 0; i--)
            {
                restore[i].field.SetValue(null, restore[i].old);
            }
        }
    }

    // ---------- состояние инструмента ----------

    private static List<(byte depth, FieldInfo field)> FieldsOf(Type type)
    {
        if (fieldsByType.TryGetValue(type, out List<(byte, FieldInfo)>? cached))
        {
            return cached;
        }
        List<(byte depth, FieldInfo field)> list = new List<(byte, FieldInfo)>();
        byte depth = 0;
        for (Type? t = type; t != null && t != typeof(Designator) && typeof(Designator).IsAssignableFrom(t); t = t.BaseType)
        {
            FieldInfo[] fields = t.GetFields(Declared);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (FieldInfo field in fields)
            {
                if (!field.IsLiteral && !field.IsInitOnly && ValueCodec.Supports(field.FieldType))
                {
                    list.Add((depth, field));
                }
            }
            depth++;
        }
        fieldsByType[type] = list;
        return list;
    }

    private static void WriteState(PacketWriter w, Designator designator, Type type)
    {
        List<(byte depth, FieldInfo field)> fields = FieldsOf(type);
        int count = Math.Min(fields.Count, MaxFields);
        w.WriteInt32(count);
        for (int i = 0; i < count; i++)
        {
            (byte depth, FieldInfo field) = fields[i];
            w.WriteByte(depth);
            w.WriteString(field.Name);
            ValueCodec.Write(w, field.GetValue(field.IsStatic ? null : designator));
        }
    }

    private static List<(FieldInfo? field, object? value)> ReadState(ref PacketReader r, Map map, Type? type)
    {
        int count = r.ReadCount(MaxFields, 6);
        List<(FieldInfo?, object?)> state = new List<(FieldInfo?, object?)>(count);
        for (int i = 0; i < count; i++)
        {
            byte depth = r.ReadByte();
            string name = r.ReadString();
            object? value = ValueCodec.Read(ref r, map);
            Type? declaring = type;
            for (int d = 0; d < depth && declaring != null; d++)
            {
                declaring = declaring.BaseType;
            }
            state.Add((declaring?.GetField(name, Declared), value));
        }
        return state;
    }

    private static bool Assignable(Type fieldType, object? value)
    {
        Type? underlying = Nullable.GetUnderlyingType(fieldType);
        if (value == null)
        {
            return !fieldType.IsValueType || underlying != null;
        }
        return (underlying ?? fieldType).IsInstanceOfType(value);
    }

    // ---------- создание инструмента ----------

    /// <summary>
    /// Инструмент можно создать на другом клиенте: есть конструктор без параметров либо конструктор,
    /// каждый параметр которого берётся из одноимённого передаваемого поля (Designator_Build(entDef)).
    /// </summary>
    private static bool CanRebuild(Type type)
    {
        if (rebuildable.TryGetValue(type, out bool known))
        {
            return known;
        }
        bool result = false;
        List<(byte depth, FieldInfo field)> fields = FieldsOf(type);
        foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            bool ok = true;
            foreach (ParameterInfo parameter in ctor.GetParameters())
            {
                bool found = false;
                foreach ((byte _, FieldInfo field) in fields)
                {
                    if (!field.IsStatic && string.Equals(field.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
                        && parameter.ParameterType.IsAssignableFrom(field.FieldType))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                result = true;
                break;
            }
        }
        rebuildable[type] = result;
        return result;
    }

    private static Designator? Create(Type type, List<(FieldInfo? field, object? value)> state)
    {
        ConstructorInfo[] ctors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Array.Sort(ctors, (a, b) => a.GetParameters().Length.CompareTo(b.GetParameters().Length));
        foreach (ConstructorInfo ctor in ctors)
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            object?[] args = new object?[parameters.Length];
            bool ok = true;
            for (int i = 0; i < parameters.Length && ok; i++)
            {
                ok = false;
                foreach ((FieldInfo? field, object? value) in state)
                {
                    if (field != null && !field.IsStatic && value != null
                        && string.Equals(field.Name, parameters[i].Name, StringComparison.OrdinalIgnoreCase)
                        && parameters[i].ParameterType.IsInstanceOfType(value))
                    {
                        args[i] = value;
                        ok = true;
                        break;
                    }
                }
            }
            if (ok)
            {
                return (Designator)ctor.Invoke(args);
            }
        }
        return null;
    }

    // ---------- установка перехвата ----------

    /// <summary>Ставит префиксы на все объявленные реализации трёх методов во всех классах-наследниках Designator.</summary>
    public static void PatchAll(Harmony harmony)
    {
        HarmonyMethod cellPrefix = new HarmonyMethod(typeof(DesignatorCommands), nameof(PrefixCell));
        HarmonyMethod cellsPrefix = new HarmonyMethod(typeof(DesignatorCommands), nameof(PrefixCells));
        HarmonyMethod thingPrefix = new HarmonyMethod(typeof(DesignatorCommands), nameof(PrefixThing));
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        int patched = 0;
        int failed = 0;
        foreach (Type type in GenTypes.AllTypes)
        {
            if (!typeof(Designator).IsAssignableFrom(type) || type.ContainsGenericParameters)
            {
                continue;
            }
            (string name, Type arg, HarmonyMethod prefix)[] targets =
            {
                (nameof(Designator.DesignateSingleCell), typeof(IntVec3), cellPrefix),
                (nameof(Designator.DesignateMultiCell), typeof(IEnumerable<IntVec3>), cellsPrefix),
                (nameof(Designator.DesignateThing), typeof(Thing), thingPrefix),
            };
            foreach ((string name, Type arg, HarmonyMethod prefix) in targets)
            {
                MethodInfo? method = type.GetMethod(name, flags, null, new[] { arg }, null);
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
                    Log.Warning($"[RWMLWC] разметка: не удалось перехватить {type.FullName}.{name}: {ex.Message}");
                }
            }
        }
        Log.Message($"[RWMLWC] разметка: перехвачено методов {patched}, не удалось {failed}");
    }

    private static bool PrefixCell(Designator __instance, IntVec3 __0)
    {
        return Intercept(__instance, MethodCell, __0, null, null);
    }

    private static bool PrefixCells(Designator __instance, IEnumerable<IntVec3> __0)
    {
        return Intercept(__instance, MethodCells, default, __0, null);
    }

    private static bool PrefixThing(Designator __instance, Thing __0)
    {
        return Intercept(__instance, MethodThing, default, null, __0);
    }
}

/// <summary>Перехват ставится после загрузки всех модов и определений.</summary>
[StaticConstructorOnStartup]
internal static class DesignatorCommandsStartup
{
    static DesignatorCommandsStartup()
    {
        try
        {
            DesignatorCommands.PatchAll(RwmlwcMod.HarmonyInstance);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] разметка: установка перехвата упала: " + ex);
        }
    }
}

// Пока исполняется команда другого игрока, его действие не должно менять интерфейс этого клиента:
// выбранный инструмент остаётся, сообщения и звуки интерфейса не появляются.
[HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.Select))]
internal static class Patch_DesignatorManager_Select
{
    private static bool Prefix() => !CommandBus.ExecutingRemote;
}

[HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.Deselect))]
internal static class Patch_DesignatorManager_Deselect
{
    private static bool Prefix() => !CommandBus.ExecutingRemote;
}

[HarmonyPatch]
internal static class Patch_Messages_Message
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in typeof(Messages).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (method.Name == nameof(Messages.Message))
            {
                yield return method;
            }
        }
    }

    private static bool Prefix() => !CommandBus.ExecutingRemote;
}

[HarmonyPatch(typeof(Verse.Sound.SoundStarter), nameof(Verse.Sound.SoundStarter.PlayOneShotOnCamera))]
internal static class Patch_SoundStarter_PlayOneShotOnCamera
{
    private static bool Prefix() => !CommandBus.ExecutingRemote;
}
