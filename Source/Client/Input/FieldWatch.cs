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
/// Поля, которые интерфейс меняет напрямую (T-023). Часть окон не вызывает метод, а пишет прямо
/// в поле объекта: «приостановить счёт», число повторов, «лечиться самому», уровень медпомощи.
/// Перехватить запись в поле нельзя, поэтому для перечисленных здесь методов отрисовки делается так:
/// до вызова запоминаются поля объекта, после — сравниваются; что изменилось, возвращается назад
/// и уходит командой «поле F объекта X = значение».
///
/// То же для выпадающих меню, открытых из такого метода: действие строки меню исполняется
/// под тем же наблюдением.
/// </summary>
public static class FieldWatch
{
    private sealed class Watch
    {
        public string key = "";
        public Func<object?, object[], object?> target = (_, _) => null;
    }

    public struct State
    {
        public object? target;
        public object?[]? before;
        public object? previousAmbient;
        public bool active;
    }

    private static readonly Dictionary<MethodBase, Watch> watches = new Dictionary<MethodBase, Watch>();
    private static readonly Dictionary<Type, List<(byte depth, FieldInfo field)>> fieldsByType = new Dictionary<Type, List<(byte, FieldInfo)>>();
    private static object? ambientTarget;

    public static int Captured { get; private set; }

    /// <summary>Объект, за которым сейчас наблюдает метод отрисовки (для меню, открытых из него).</summary>
    public static object? AmbientTarget => ambientTarget;

    private static void Add(Type type, string method, Func<object?, object[], object?> target)
    {
        MethodInfo? found = AccessTools.Method(type, method);
        if (found == null)
        {
            Log.Warning($"[RWMLWC] поля: не найден метод {type.FullName}.{method}");
            return;
        }
        watches[found] = new Watch { key = type.FullName + "." + method, target = target };
    }

    private static void RegisterAll()
    {
        // Строка счёта в списке верстака: «приостановить», число повторов; меню режима повтора.
        Add(typeof(Bill), nameof(Bill.DoInterface), (instance, _) => instance);
        // Окно настройки счёта.
        Add(typeof(Dialog_BillConfig), nameof(Dialog_BillConfig.DoWindowContents),
            (instance, _) => instance == null ? null : AccessTools.Field(typeof(Dialog_BillConfig), "bill")?.GetValue(instance));
        // Вкладка «Здоровье»: «лечиться самому», уровень медпомощи.
        Add(typeof(HealthCardUtility), "DrawOverviewTab", (_, args) => (args[1] as Pawn)?.playerSettings);
        Add(typeof(MedicalCareUtility), nameof(MedicalCareUtility.MedicalCareSelectButton), (_, args) => (args[1] as Pawn)?.playerSettings);
        // Реакция на угрозу.
        Add(typeof(HostilityResponseModeUtility), nameof(HostilityResponseModeUtility.DrawResponseButton), (_, args) => (args[1] as Pawn)?.playerSettings);
        // Вкладка пленника/гостя: уровень медпомощи.
        Add(typeof(ITab_Pawn_Visitor), "FillTab", (_, _) => (Find.Selector.SingleSelectedThing as Pawn)?.playerSettings);
    }

    // ---------- наблюдение ----------

    private static List<(byte depth, FieldInfo field)> FieldsOf(Type type)
    {
        if (fieldsByType.TryGetValue(type, out List<(byte, FieldInfo)>? cached))
        {
            return cached;
        }
        List<(byte depth, FieldInfo field)> list = new List<(byte, FieldInfo)>();
        byte depth = 0;
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo[] fields = t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (FieldInfo field in fields)
            {
                if (!field.IsInitOnly && ValueCodec.Supports(field.FieldType))
                {
                    list.Add((depth, field));
                }
            }
            depth++;
        }
        fieldsByType[type] = list;
        return list;
    }

    private static object?[] Snapshot(object target)
    {
        List<(byte depth, FieldInfo field)> fields = FieldsOf(target.GetType());
        object?[] values = new object?[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            values[i] = fields[i].field.GetValue(target);
        }
        return values;
    }

    // Сравнить с запомненным; изменения вернуть назад и отправить командами.
    // Все изменения одного объекта уходят вместе или не уходят вовсе: поля бывают связаны
    // (способ хранения в счёте и склад для него).
    private static void SendChanges(object target, object?[] before)
    {
        List<(byte depth, FieldInfo field)> fields = FieldsOf(target.GetType());
        List<PacketWriter>? commands = null;
        Map? map = null;
        string? failed = null;
        for (int i = 0; i < fields.Count && i < before.Length; i++)
        {
            (byte depth, FieldInfo field) = fields[i];
            object? now = field.GetValue(target);
            if (Equals(now, before[i]))
            {
                continue;
            }
            field.SetValue(target, before[i]); // игру меняет только команда
            if (failed != null)
            {
                continue;
            }
            map ??= MethodCommands.MapFor(target);
            if (map == null)
            {
                failed = field.Name;
                continue;
            }
            PacketWriter w = CommandBus.Begin(map);
            try
            {
                if (!ValueCodec.TryWriteAny(w, target))
                {
                    failed = field.Name;
                    continue;
                }
                w.WriteByte(depth);
                w.WriteString(field.Name);
                ValueCodec.Write(w, now);
            }
            catch (NotSupportedException)
            {
                failed = field.Name; // значение, которое не умеем передавать (например, группа складов)
                continue;
            }
            (commands ??= new List<PacketWriter>()).Add(w);
        }
        if (failed != null)
        {
            Messages.Message("RWMLWC: эта настройка пока не работает в сетевой игре (" + target.GetType().Name + "." + failed + ")", MessageTypeDefOf.RejectInput, false);
            return;
        }
        if (commands == null || map == null)
        {
            return;
        }
        foreach (PacketWriter w in commands)
        {
            CommandBus.Send(map, CommandKinds.FieldSet, PermissionLevel.Full, w);
            Captured++;
        }
    }

    private static void Prefix(MethodBase __originalMethod, object __instance, object[] __args, out State __state)
    {
        __state = default;
        if (!CommandBus.CaptureActive || !watches.TryGetValue(__originalMethod, out Watch? watch))
        {
            return;
        }
        object? target;
        try
        {
            target = watch.target(__instance, __args);
        }
        catch (Exception)
        {
            target = null;
        }
        if (target == null)
        {
            return;
        }
        __state.active = true;
        __state.target = target;
        __state.before = Snapshot(target);
        __state.previousAmbient = ambientTarget;
        ambientTarget = target;
    }

    private static void Finalizer(State __state)
    {
        if (!__state.active || __state.target == null || __state.before == null)
        {
            return;
        }
        ambientTarget = __state.previousAmbient;
        try
        {
            SendChanges(__state.target, __state.before);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] поля: " + ex);
        }
    }

    /// <summary>Действие строки меню, открытого из наблюдаемого метода: исполнить под тем же наблюдением.</summary>
    public static Action WrapMenuAction(object target, Action original)
    {
        return () =>
        {
            if (!CommandBus.CaptureActive)
            {
                original();
                return;
            }
            object?[] before = Snapshot(target);
            try
            {
                original();
            }
            finally
            {
                SendChanges(target, before);
            }
        };
    }

    /// <summary>Для тестовых сценариев: выполнить действие так, как его выполнил бы наблюдаемый метод отрисовки.</summary>
    public static void RunWatched(object target, Action body)
    {
        WrapMenuAction(target, body)();
    }

    // ---------- исполнение ----------

    public static void Execute(Map map, ref PacketReader r)
    {
        object? target = ValueCodec.Read(ref r, map);
        byte depth = r.ReadByte();
        string name = r.ReadString();
        object? value = ValueCodec.Read(ref r, map);
        if (target == null)
        {
            TestAutomation.Note("field: " + name + ": object is gone, dropped");
            return;
        }
        Type? declaring = target.GetType();
        for (int i = 0; i < depth && declaring != null; i++)
        {
            declaring = declaring.BaseType;
        }
        FieldInfo? field = declaring?.GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null || field.IsInitOnly || !ValueCodec.Supports(field.FieldType))
        {
            throw new InvalidOperationException("поля: нет поля " + name + " у " + target.GetType().FullName);
        }
        Type underlying = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
        if (value == null ? (field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null) : !underlying.IsInstanceOfType(value))
        {
            throw new InvalidOperationException("поля: значение не того типа для " + name);
        }
        field.SetValue(target, value);
    }

    public static void PatchAll(Harmony harmony)
    {
        RegisterAll();
        HarmonyMethod prefix = new HarmonyMethod(typeof(FieldWatch), nameof(Prefix));
        HarmonyMethod finalizer = new HarmonyMethod(typeof(FieldWatch), nameof(Finalizer));
        int failed = 0;
        foreach (KeyValuePair<MethodBase, Watch> pair in watches)
        {
            try
            {
                harmony.Patch(pair.Key, prefix: prefix, finalizer: finalizer);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"[RWMLWC] поля: не удалось перехватить {pair.Value.key}: {ex.Message}");
            }
        }
        Log.Message($"[RWMLWC] поля: под наблюдением методов {watches.Count - failed}, не удалось {failed}");
    }
}

[StaticConstructorOnStartup]
internal static class FieldWatchStartup
{
    static FieldWatchStartup()
    {
        try
        {
            FieldWatch.PatchAll(RwmlwcMod.HarmonyInstance);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] поля: установка наблюдения упала: " + ex);
        }
    }
}

// Номера объектов вне симуляции. Интерфейс создаёт объекты до того, как действие уйдёт командой
// (счёт создаётся при выборе рецепта, потом вызывается AddBill), и каждый такой объект берёт номер
// из общего счётчика игры — только у автора. В сессии вне тика и вне исполнения команды номера
// выдаются из отдельного, отрицательного ряда: общий счётчик у всех остаётся одинаковым, а объект,
// созданный интерфейсом, всё равно пересоздаётся при исполнении команды.
[HarmonyPatch(typeof(UniqueIDsManager), "GetNextID")]
internal static class Patch_UniqueIDsManager_GetNextID
{
    private static int presentationId = -1000;

    private static bool Prefix(ref int __result)
    {
        if (!LockstepClient.InSession || MapRandContext.InTick)
        {
            return true;
        }
        __result = presentationId--;
        return false;
    }
}
