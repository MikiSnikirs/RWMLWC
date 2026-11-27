using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Input.MpApi;

/// <summary>
/// Синхронные вызовы Multiplayer API поверх шины команд RWMLWC (T-085). Мод описывает метод, замыкание
/// или поле (RegisterSyncMethod / RegisterSyncDelegate / RegisterSyncField); в сессии вызов из интерфейса
/// не исполняется, а уходит командой CommandKinds.ApiCall и исполняется у всех между одними и теми же тиками —
/// так же, как наши команды окон (MethodCommands, FieldWatch).
///
/// Порядок регистрации — часть формата команды: у игроков с одной сборкой модов он одинаков.
/// </summary>
internal static class MpSync
{
    internal static readonly List<ApiCall> Calls = new List<ApiCall>();
    internal static readonly List<ApiField> Fields = new List<ApiField>();
    private static readonly Dictionary<MethodBase, ApiCall> byMethod = new Dictionary<MethodBase, ApiCall>();
    private static Harmony? harmony;

    public static int Sent { get; private set; }

    internal static Harmony Harmony => harmony ??= new Harmony("MikiSnikirs.RWMLWC.MpApi");

    private const byte KindCall = 1;
    private const byte KindField = 2;

    public static T Add<T>(T call) where T : ApiCall
    {
        if (byMethod.TryGetValue(call.method, out ApiCall? existing))
        {
            return (existing as T) ?? call;
        }
        call.index = Calls.Count;
        Calls.Add(call);
        byMethod[call.method] = call;
        try
        {
            HarmonyMethod prefix = call.method.IsStatic
                ? new HarmonyMethod(typeof(MpSync), nameof(PrefixStatic)) { priority = Priority.First }
                : new HarmonyMethod(typeof(MpSync), nameof(PrefixInstance)) { priority = Priority.First };
            Harmony.Patch(call.method, prefix: prefix);
        }
        catch (Exception ex)
        {
            Log.Warning($"[RWMLWC] Multiplayer API: не удалось перехватить {call.key}: {ex.Message}");
        }
        return call;
    }

    public static ApiField AddField(ApiField field)
    {
        field.index = Fields.Count;
        Fields.Add(field);
        return field;
    }

    private static bool PrefixInstance(MethodBase __originalMethod, object __instance, object[] __args) => Intercept(__originalMethod, __instance, __args);

    private static bool PrefixStatic(MethodBase __originalMethod, object[] __args) => Intercept(__originalMethod, null, __args);

    private static bool Intercept(MethodBase method, object? instance, object[] args)
    {
        if (!CommandBus.CaptureActive || !byMethod.TryGetValue(method, out ApiCall? call))
        {
            return true; // не сессия, исполнение команды или тик — как в игре
        }
        try
        {
            return !call.TrySend(instance, args);
        }
        catch (Exception ex)
        {
            Log.Error($"[RWMLWC] Multiplayer API: {call.key}: {ex}");
            Messages.Message("RWMLWC: это действие мода пока не работает в сетевой игре (" + call.key + ")", RimWorld.MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }

    internal static void Send(object? target, byte kind, int index, string key, byte[] blob)
    {
        Map? map = target != null ? MethodCommands.MapFor(target) : Find.CurrentMap ?? (Find.Maps.Count > 0 ? Find.Maps[0] : null);
        if (map == null)
        {
            return;
        }
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(kind);
        w.WriteInt32(index);
        w.WriteString(key);
        w.WriteBytes(blob);
        CommandBus.Send(map, CommandKinds.ApiCall, PermissionLevel.Full, w);
        Sent++;
    }

    public static void Execute(Map map, ref PacketReader r)
    {
        byte kind = r.ReadByte();
        int index = r.ReadInt32();
        string key = r.ReadString();
        byte[] blob = r.ReadBytes();
        using BinaryReader br = new BinaryReader(new MemoryStream(blob));
        if (kind == KindCall)
        {
            if (index < 0 || index >= Calls.Count || Calls[index].key != key)
            {
                throw new InvalidOperationException($"Multiplayer API: вызов {index} «{key}» не совпадает со списком этого клиента");
            }
            Calls[index].Execute(br, map);
        }
        else if (kind == KindField)
        {
            if (index < 0 || index >= Fields.Count || Fields[index].key != key)
            {
                throw new InvalidOperationException($"Multiplayer API: поле {index} «{key}» не совпадает со списком этого клиента");
            }
            Fields[index].Execute(br, map);
        }
        else
        {
            throw new InvalidDataException("Multiplayer API: вид " + kind);
        }
    }

    internal const byte CallKind = KindCall;
    internal const byte FieldKind = KindField;

    // ---------- наблюдение за полями (WatchBegin / Watch / WatchEnd) ----------

    private sealed class Watched
    {
        public ApiField field = null!;
        public object? target;
        public object? index;
        public object? before;
    }

    private static readonly Stack<List<Watched>> watchStack = new Stack<List<Watched>>();

    public static void WatchBegin() => watchStack.Push(new List<Watched>());

    public static void Watch(ApiField field, object? target, object? index)
    {
        if (watchStack.Count == 0 || !CommandBus.CaptureActive)
        {
            return;
        }
        watchStack.Peek().Add(new Watched { field = field, target = target, index = index, before = field.GetValue(target, index) });
    }

    public static void WatchEnd()
    {
        if (watchStack.Count == 0)
        {
            return;
        }
        List<Watched> list = watchStack.Pop();
        if (!CommandBus.CaptureActive)
        {
            return;
        }
        foreach (Watched watched in list)
        {
            object? now = watched.field.GetValue(watched.target, watched.index);
            if (Equals(now, watched.before))
            {
                continue;
            }
            watched.field.SetValue(watched.target, watched.index, watched.before); // игру меняет только команда
            watched.field.SendValue(watched.target, watched.index, now);
        }
    }
}

/// <summary>Метод или замыкание, которое мод попросил синхронизировать.</summary>
internal abstract class ApiCall
{
    public int index;
    public string key = "";
    public MethodBase method = null!;
    public Type[] argTypes = Type.EmptyTypes;
    public SyncContext context;
    public bool cancelIfAnyArgNull, cancelIfNoSelectedMap, cancelIfNoSelectedWorld, debugOnly, hostOnly;
    public int minTimeMs;
    private float lastSentAt = -1f;
    public Action<object, object[]>? preInvoke, postInvoke;
    public readonly Dictionary<int, (Func<object?, object?, object?[], object?> write, Func<object?, object?> read)> argTransforms = new();
    public (Func<object?, object?, object?[], object?> write, Func<object?, object?> read)? targetTransform;

    protected static (Func<object?, object?, object?[], object?>, Func<object?, object?>) Wrap<Live, Networked>(Serializer<Live, Networked> s) =>
        ((live, target, args) => s.Writer((Live)live!, target!, args!), net => s.Reader((Networked)net!));

    protected bool CommonCancel(object?[] args)
    {
        if (cancelIfAnyArgNull && args.Any(a => a == null))
        {
            return true;
        }
        if (cancelIfNoSelectedMap && Find.Selector.NumSelected == 0)
        {
            return true;
        }
        if (cancelIfNoSelectedWorld && Find.WorldSelector.NumSelectedObjects == 0)
        {
            return true;
        }
        if (minTimeMs > 0)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (lastSentAt >= 0 && (now - lastSentAt) * 1000f < minTimeMs)
            {
                return true;
            }
            lastSentAt = now;
        }
        return false;
    }

    /// <summary>true — отправлено (оригинал не исполнять).</summary>
    public abstract bool TrySend(object? instance, object?[] args);

    public abstract void Execute(BinaryReader r, Map map);

    protected void WriteArgs(BinaryWriter w, object? target, object?[] args)
    {
        w.Write(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            object? value = args[i];
            if (argTransforms.TryGetValue(i, out var t))
            {
                value = t.write(value, target, args);
            }
            MpSerialization.Write(w, value, i < argTypes.Length && !argTransforms.ContainsKey(i) ? argTypes[i] : typeof(object));
        }
    }

    protected object?[] ReadArgs(BinaryReader r, Map map)
    {
        int count = r.ReadInt32();
        object?[] args = new object?[count];
        for (int i = 0; i < count; i++)
        {
            object? value = MpSerialization.Read(r, map);
            if (argTransforms.TryGetValue(i, out var t))
            {
                value = t.read(value);
            }
            args[i] = value;
        }
        return args;
    }

    protected void Invoke(object? target, object?[] args)
    {
        preInvoke?.Invoke(target!, args!);
        method.Invoke(target, args);
        postInvoke?.Invoke(target!, args!);
    }
}

/// <summary>Синхронный метод (RegisterSyncMethod, …Lambda без захвата).</summary>
internal sealed class ApiMethod : ApiCall, ISyncMethod
{
    private bool SharedDisplayClass => method.DeclaringType != null && method.DeclaringType.Name == "<>c";

    public ApiMethod(MethodBase method, SyncType[]? types)
    {
        this.method = method;
        key = (method.DeclaringType?.FullName ?? "?") + "." + method.Name + "(" + method.GetParameters().Length + ")";
        argTypes = types != null && types.Length > 0 ? types.Select(t => t.type).ToArray() : method.GetParameters().Select(p => p.ParameterType).ToArray();
    }

    public override bool TrySend(object? instance, object?[] args)
    {
        if (CommonCancel(args))
        {
            return true;
        }
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        object? target = instance;
        if (!method.IsStatic && !SharedDisplayClass)
        {
            object? net = targetTransform.HasValue ? targetTransform.Value.write(instance, instance, args) : instance;
            MpSerialization.Write(w, net, targetTransform.HasValue ? typeof(object) : method.DeclaringType!);
        }
        WriteArgs(w, target, args);
        w.Flush();
        MpSync.Send(target is Thing || target is ThingComp || target is Zone ? target : (args.FirstOrDefault(a => a is Thing) ?? target), MpSync.CallKind, index, key, ms.ToArray());
        return true;
    }

    public override void Execute(BinaryReader r, Map map)
    {
        object? target = null;
        if (SharedDisplayClass)
        {
            target = AccessTools.Field(method.DeclaringType, "<>9")?.GetValue(null);
        }
        else if (!method.IsStatic)
        {
            target = MpSerialization.Read(r, map);
            if (targetTransform.HasValue)
            {
                target = targetTransform.Value.read(target);
            }
            if (target == null)
            {
                TestAutomation.Note("mpapi: " + key + ": target is gone, dropped");
                return;
            }
        }
        object?[] args = ReadArgs(r, map);
        Invoke(target, args);
    }

    public bool DoSync(object target, params object[] args) => !CommandBus.CaptureActive || TrySend(target, args);
    public ISyncMethod CancelIfAnyArgNull() { cancelIfAnyArgNull = true; return this; }
    public ISyncMethod CancelIfNoSelectedMapObjects() { cancelIfNoSelectedMap = true; return this; }
    public ISyncMethod CancelIfNoSelectedWorldObjects() { cancelIfNoSelectedWorld = true; return this; }
    public ISyncMethod ExposeParameter(int index) => this;
    public ISyncMethod MinTime(int time) { minTimeMs = time; return this; }
    public ISyncMethod SetContext(SyncContext context) { this.context = context; return this; }
    public ISyncMethod SetDebugOnly() { debugOnly = true; return this; }
    public ISyncMethod SetHostOnly() { hostOnly = true; return this; }
    public ISyncMethod SetPreInvoke(Action<object, object[]> action) { preInvoke = action; return this; }
    public ISyncMethod SetPostInvoke(Action<object, object[]> action) { postInvoke = action; return this; }
    public ISyncMethod SetVersion(int version) => this;
    public ISyncMethod TransformArgument<Live, Networked>(int index, Serializer<Live, Networked> serializer, bool skipTypeCheck = false) { argTransforms[index] = Wrap(serializer); return this; }
    public ISyncMethod TransformTarget<Live, Networked>(Serializer<Live, Networked> serializer, bool skipTypeCheck = false) { targetTransform = Wrap(serializer); return this; }
    public override string ToString() => "RWMLWC SyncMethod " + key;
}

/// <summary>Замыкание (RegisterSyncDelegate, …Lambda с захватом): передаются поля объекта-замыкания и аргументы.</summary>
internal sealed class ApiDelegate : ApiCall, ISyncDelegate, ISyncMethod
{
    private readonly Type closure;
    private readonly string[]? fieldNames;
    private string[]? cancelIfAnyNullExcept, cancelIfNull, removeNulls;
    private readonly Dictionary<string, (Func<object?, object?, object?[], object?> write, Func<object?, object?> read)> fieldTransforms = new();

    public ApiDelegate(MethodBase method, string[]? fields)
    {
        this.method = method;
        closure = method.DeclaringType!;
        fieldNames = fields;
        key = (closure.FullName ?? "?") + "." + method.Name + "(" + method.GetParameters().Length + ")";
        argTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
    }

    private static bool IsClosureType(Type t) => t.Name.Contains("<>c__DisplayClass") || (t.IsDefined(typeof(CompilerGeneratedAttribute), false) && t.Name.StartsWith("<>"));

    private List<FieldInfo> FieldsOf(Type type, bool top)
    {
        List<FieldInfo> fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).ToList();
        if (top && fieldNames != null)
        {
            fields = fields.Where(f => fieldNames.Contains(f.Name) || IsClosureType(f.FieldType)).ToList();
        }
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    private void WriteClosure(BinaryWriter w, object obj, bool top, object?[] args)
    {
        foreach (FieldInfo field in FieldsOf(obj.GetType(), top))
        {
            object? value = field.GetValue(obj);
            if (value != null && IsClosureType(field.FieldType))
            {
                w.Write(true);
                WriteClosure(w, value, false, args);
                continue;
            }
            if (IsClosureType(field.FieldType))
            {
                w.Write(false);
                continue;
            }
            if (fieldTransforms.TryGetValue(field.Name, out var t))
            {
                value = t.write(value, obj, args);
                MpSerialization.Write(w, value, typeof(object));
            }
            else
            {
                MpSerialization.Write(w, value, field.FieldType);
            }
        }
    }

    private void ReadClosure(BinaryReader r, Map map, object obj, bool top)
    {
        foreach (FieldInfo field in FieldsOf(obj.GetType(), top))
        {
            if (IsClosureType(field.FieldType))
            {
                if (r.ReadBoolean())
                {
                    object nested = RuntimeHelpers.GetUninitializedObject(field.FieldType);
                    ReadClosure(r, map, nested, false);
                    field.SetValue(obj, nested);
                }
                continue;
            }
            object? value = MpSerialization.Read(r, map);
            if (fieldTransforms.TryGetValue(field.Name, out var t))
            {
                value = t.read(value);
            }
            if (removeNulls != null && removeNulls.Contains(field.Name) && value is IList list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null)
                    {
                        list.RemoveAt(i);
                    }
                }
            }
            field.SetValue(obj, value);
        }
    }

    public override bool TrySend(object? instance, object?[] args)
    {
        if (instance == null || CommonCancel(args))
        {
            return instance != null;
        }
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        WriteClosure(w, instance, true, args);
        WriteArgs(w, instance, args);
        w.Flush();
        // Карта команды — по первой вещи среди полей замыкания, иначе текущая.
        object? anchor = FieldsOf(closure, true).Select(f => f.GetValue(instance)).FirstOrDefault(v => v is Thing || v is ThingComp);
        MpSync.Send(anchor is ThingComp c ? c.parent : anchor, MpSync.CallKind, index, key, ms.ToArray());
        return true;
    }

    public override void Execute(BinaryReader r, Map map)
    {
        object obj = RuntimeHelpers.GetUninitializedObject(closure);
        ReadClosure(r, map, obj, true);
        object?[] args = ReadArgs(r, map);
        foreach (FieldInfo field in FieldsOf(closure, true))
        {
            object? value = field.GetValue(obj);
            if (value == null && ((cancelIfNull != null && cancelIfNull.Contains(field.Name))
                || (cancelIfAnyNullExcept != null && !cancelIfAnyNullExcept.Contains(field.Name) && !IsClosureType(field.FieldType))))
            {
                TestAutomation.Note("mpapi: " + key + ": field " + field.Name + " is null, dropped");
                return;
            }
        }
        Invoke(obj, args);
    }

    public bool DoSync(object target, params object[] args) => !CommandBus.CaptureActive || TrySend(target, args);
    public ISyncDelegate CancelIfAnyFieldNull(params string[] blacklist) { cancelIfAnyNullExcept = blacklist; return this; }
    public ISyncDelegate CancelIfFieldsNull(params string[] whitelist) { cancelIfNull = whitelist; return this; }
    public ISyncDelegate CancelIfNoSelectedObjects() { cancelIfNoSelectedMap = true; return this; }
    public ISyncDelegate CancelIfNoSelectedMapObjects() { cancelIfNoSelectedMap = true; return this; }
    public ISyncDelegate CancelIfNoSelectedWorldObjects() { cancelIfNoSelectedWorld = true; return this; }
    public ISyncDelegate ExposeFields(params string[] fields) => this;
    public ISyncDelegate RemoveNullsFromLists(params string[] listFields) { removeNulls = listFields; return this; }
    public ISyncDelegate SetContext(SyncContext context) { this.context = context; return this; }
    public ISyncDelegate SetDebugOnly() { debugOnly = true; return this; }
    public ISyncDelegate SetHostOnly() { hostOnly = true; return this; }
    public ISyncDelegate SetPreInvoke(Action<object, object[]> action) { preInvoke = action; return this; }
    public ISyncDelegate SetPostInvoke(Action<object, object[]> action) { postInvoke = action; return this; }
    public ISyncDelegate TransformArgument<Live, Networked>(int index, Serializer<Live, Networked> serializer, bool skipTypeCheck = false) { argTransforms[index] = Wrap(serializer); return this; }
    public ISyncDelegate TransformTarget<Live, Networked>(Serializer<Live, Networked> serializer, bool skipTypeCheck = false) { targetTransform = Wrap(serializer); return this; }
    public ISyncDelegate TransformField<Live, Networked>(string field, Serializer<Live, Networked> serializer, bool skipTypeCheck = false) { fieldTransforms[field] = Wrap(serializer); return this; }
    // В Multiplayer замыкание — наследник синхронного метода, и Compatibility приводит одно к другому
    // (например, SetVersion у результата RegisterLambdaDelegate). Те же настройки через ISyncMethod.
    ISyncMethod ISyncMethod.CancelIfAnyArgNull() { cancelIfAnyArgNull = true; return this; }
    ISyncMethod ISyncMethod.CancelIfNoSelectedMapObjects() { cancelIfNoSelectedMap = true; return this; }
    ISyncMethod ISyncMethod.CancelIfNoSelectedWorldObjects() { cancelIfNoSelectedWorld = true; return this; }
    ISyncMethod ISyncMethod.ExposeParameter(int index) => this;
    ISyncMethod ISyncMethod.MinTime(int time) { minTimeMs = time; return this; }
    ISyncMethod ISyncMethod.SetContext(SyncContext context) { this.context = context; return this; }
    ISyncMethod ISyncMethod.SetDebugOnly() { debugOnly = true; return this; }
    ISyncMethod ISyncMethod.SetHostOnly() { hostOnly = true; return this; }
    ISyncMethod ISyncMethod.SetPreInvoke(Action<object, object[]> action) { preInvoke = action; return this; }
    ISyncMethod ISyncMethod.SetPostInvoke(Action<object, object[]> action) { postInvoke = action; return this; }
    ISyncMethod ISyncMethod.SetVersion(int version) => this;
    ISyncMethod ISyncMethod.TransformArgument<Live, Networked>(int index, Serializer<Live, Networked> serializer, bool skipTypeCheck) { argTransforms[index] = Wrap(serializer); return this; }
    ISyncMethod ISyncMethod.TransformTarget<Live, Networked>(Serializer<Live, Networked> serializer, bool skipTypeCheck) { targetTransform = Wrap(serializer); return this; }
    public override string ToString() => "RWMLWC SyncDelegate " + key;
}

/// <summary>Синхронное поле (RegisterSyncField): путь «поле/поле/…» от объекта-цели или статическое поле.</summary>
internal sealed class ApiField : ISyncField
{
    public int index;
    public string key = "";
    private readonly Type? targetType;
    private readonly List<MemberInfo> path = new List<MemberInfo>();
    private Action<object, object>? preApply, postApply;
    private bool cancelIfValueNull;

    public ApiField(Type? targetType, string memberPath)
    {
        this.targetType = targetType;
        key = (targetType?.FullName ?? "") + "/" + memberPath;
        Type? current = targetType;
        string[] parts = memberPath.Split('/');
        int start = 0;
        if (current == null)
        {
            // «Полное.Имя.Типа/поле» или «Полное.Имя.Типа.поле»
            current = AccessTools.TypeByName(parts[0]);
            start = 1;
            if (current == null && parts.Length == 1)
            {
                int dot = memberPath.LastIndexOf('.');
                current = AccessTools.TypeByName(memberPath.Substring(0, dot));
                parts = new[] { memberPath.Substring(0, dot), memberPath.Substring(dot + 1) };
            }
        }
        for (int i = start; i < parts.Length && current != null; i++)
        {
            MemberInfo? m = (MemberInfo?)AccessTools.Field(current, parts[i]) ?? AccessTools.Property(current, parts[i]);
            if (m == null)
            {
                throw new MissingMemberException(current.FullName, parts[i]);
            }
            path.Add(m);
            current = m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
        }
    }

    private static object? Get(MemberInfo m, object? obj) => m is FieldInfo f ? f.GetValue(obj) : ((PropertyInfo)m).GetValue(obj);

    private static void Set(MemberInfo m, object? obj, object? value)
    {
        if (m is FieldInfo f)
        {
            f.SetValue(obj, value);
        }
        else
        {
            ((PropertyInfo)m).SetValue(obj, value);
        }
    }

    private object? Owner(object? target)
    {
        object? obj = target;
        for (int i = 0; i < path.Count - 1; i++)
        {
            obj = Get(path[i], obj);
        }
        return obj;
    }

    public object? GetValue(object? target, object? index)
    {
        object? value = Get(path[path.Count - 1], Owner(target));
        if (index != null)
        {
            return value is IDictionary dict ? dict[index] : value is IList list ? list[(int)index] : null;
        }
        return value;
    }

    public void SetValue(object? target, object? index, object? value)
    {
        object? owner = Owner(target);
        MemberInfo last = path[path.Count - 1];
        if (index != null)
        {
            object? container = Get(last, owner);
            if (container is IDictionary dict)
            {
                dict[index] = value;
            }
            else if (container is IList list)
            {
                list[(int)index] = value;
            }
            return;
        }
        Set(last, owner, value);
    }

    public void SendValue(object? target, object? index, object? value)
    {
        if (cancelIfValueNull && value == null)
        {
            return;
        }
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        MpSerialization.Write(w, target, targetType ?? typeof(object));
        MpSerialization.Write(w, index, typeof(object));
        MpSerialization.Write(w, value, typeof(object));
        w.Flush();
        MpSync.Send(target, MpSync.FieldKind, this.index, key, ms.ToArray());
    }

    public void Execute(BinaryReader r, Map map)
    {
        object? target = MpSerialization.Read(r, map);
        object? idx = MpSerialization.Read(r, map);
        object? value = MpSerialization.Read(r, map);
        if (targetType != null && target == null)
        {
            TestAutomation.Note("mpapi: field " + key + ": target is gone, dropped");
            return;
        }
        preApply?.Invoke(target!, value!);
        SetValue(target, idx, value);
        postApply?.Invoke(target!, value!);
    }

    public ISyncField CancelIfValueNull() { cancelIfValueNull = true; return this; }
    public ISyncField InGameLoop() => this;
    public ISyncField PostApply(Action<object, object> action) { postApply = action; return this; }
    public ISyncField PreApply(Action<object, object> action) { preApply = action; return this; }
    public ISyncField SetBufferChanges() => this;
    public ISyncField SetDebugOnly() => this;
    public ISyncField SetHostOnly() => this;
    public ISyncField SetVersion(int version) => this;
    public void Watch(object? target = null, object? index = null) => MpSync.Watch(this, target, index);

    public bool DoSync(object target, object value, object? index = null)
    {
        if (!CommandBus.CaptureActive)
        {
            return false;
        }
        SendValue(target, index, value);
        return true;
    }

    public override string ToString() => "RWMLWC SyncField " + key;
}
