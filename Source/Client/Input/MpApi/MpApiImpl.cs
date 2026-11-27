using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Input.MpApi;

/// <summary>
/// Реализация Multiplayer API (rwmt, MIT) для RWMLWC (T-085). Моды и Multiplayer Compatibility ищут сборку
/// «Multiplayer» с полем Multiplayer.Common.MultiplayerAPIBridge.Instance; её кладёт наш мод (Source/MpBridge),
/// и поле отдаёт этот объект. Описания синхронизации модов становятся командами нашей шины (MpSync).
///
/// Не поддержано (заглушки): сессии (общие окна модов), деревья диалогов, «пауза по требованию окна»,
/// ExposeParameter/ExposeFields (передаются обычными сериализаторами).
/// </summary>
public sealed class MpApiImpl : IAPI
{
    public static IAPI Create()
    {
        Log.Message("[RWMLWC] Multiplayer API: мост включён — моды и Multiplayer Compatibility описывают синхронизацию через RWMLWC");
        return new MpApiImpl();
    }

    public bool IsHosting => LockstepClient.InSession && LockstepClient.PlayerId == 1;
    public bool IsInMultiplayer => LockstepClient.InSession;
    public string PlayerName
    {
        get
        {
            foreach (var p in RWMLWC.Client.SessionView.SessionUi.Players)
            {
                if (p.PlayerId == LockstepClient.PlayerId)
                {
                    return p.Name ?? "";
                }
            }
            return "";
        }
    }
    public bool IsExecutingSyncCommand => CommandBus.Executing;
    public bool IsExecutingSyncCommandIssuedBySelf => CommandBus.Executing && !CommandBus.ExecutingRemote;
    public bool CanUseDevMode => Prefs.DevMode;
    public bool InInterface => CommandBus.CaptureActive;
    public Faction RealPlayerFaction => Faction.OfPlayer;

    public void SetThingFilterContext(ThingFilterContext context) { }

    // ---------- поля ----------

    public void WatchBegin() => MpSync.WatchBegin();

    public void Watch(Type targetType, string fieldName, object? target = null, object? index = null) =>
        FindField(targetType?.FullName + "/" + fieldName)?.Watch(target, index);

    public void Watch(object target, string fieldName, object? index = null) =>
        FindField(target.GetType().FullName + "/" + fieldName)?.Watch(target, index);

    public void Watch(string memberPath, object? target = null, object? index = null) =>
        (FindField("/" + memberPath) ?? FindField((target?.GetType().FullName ?? "") + "/" + memberPath))?.Watch(target, index);

    public void WatchEnd() => MpSync.WatchEnd();

    private static ApiField? FindField(string key) => MpSync.Fields.FirstOrDefault(f => f.key == key);

    public ISyncField RegisterSyncField(Type targetType, string memberPath) => MpSync.AddField(new ApiField(targetType, memberPath));

    public ISyncField RegisterSyncField(FieldInfo field) =>
        MpSync.AddField(field.IsStatic ? new ApiField(null, field.DeclaringType!.FullName + "/" + field.Name) : new ApiField(field.DeclaringType, field.Name));

    // ---------- методы и замыкания ----------

    public ISyncMethod RegisterSyncMethod(Type type, string methodOrPropertyName, SyncType[]? argTypes = null)
    {
        MethodBase? method = argTypes != null
            ? AccessTools.Method(type, methodOrPropertyName, argTypes.Select(t => t.type).ToArray())
            : AccessTools.Method(type, methodOrPropertyName);
        method ??= AccessTools.PropertySetter(type, methodOrPropertyName);
        if (method == null)
        {
            throw new MissingMethodException(type.FullName, methodOrPropertyName);
        }
        return RegisterSyncMethod((MethodInfo)method, argTypes!);
    }

    public ISyncMethod RegisterSyncMethod(MethodInfo method, SyncType[] argTypes) => MpSync.Add(new ApiMethod(method, argTypes));

    public ISyncMethod RegisterSyncMethodLambda(Type parentType, string parentMethod, int lambdaOrdinal, Type[]? parentArgs = null, ParentMethodType parentMethodType = ParentMethodType.Normal) =>
        MpSync.Add(new ApiMethod(Lambdas.GetLambda(parentType, parentMethod, ToHarmony(parentMethodType), parentArgs, lambdaOrdinal), null));

    public ISyncMethod RegisterSyncMethodLambdaInGetter(Type parentType, string parentMethod, int lambdaOrdinal) =>
        MpSync.Add(new ApiMethod(Lambdas.GetLambda(parentType, parentMethod, MethodType.Getter, null, lambdaOrdinal), null));

    public ISyncDelegate RegisterSyncDelegate(Type type, string nestedType, string method) =>
        RegisterSyncDelegate(type, nestedType, method, null!, null);

    public ISyncDelegate RegisterSyncDelegate(Type inType, string nestedType, string methodName, string[] fields, Type[]? args = null)
    {
        Type nested = AccessTools.Inner(inType, nestedType) ?? throw new MissingMemberException(inType.FullName, nestedType);
        MethodInfo method = (args != null ? AccessTools.Method(nested, methodName, args) : AccessTools.Method(nested, methodName))
            ?? throw new MissingMethodException(nested.FullName, methodName);
        return MpSync.Add(new ApiDelegate(method, fields));
    }

    public ISyncDelegate RegisterSyncDelegateLambda(Type parentType, string parentMethod, int lambdaOrdinal, Type[]? parentArgs = null, ParentMethodType parentMethodType = ParentMethodType.Normal) =>
        MpSync.Add(new ApiDelegate(Lambdas.GetLambda(parentType, parentMethod, ToHarmony(parentMethodType), parentArgs, lambdaOrdinal), null));

    public ISyncDelegate RegisterSyncDelegateLambdaInGetter(Type parentType, string parentMethod, int lambdaOrdinal) =>
        MpSync.Add(new ApiDelegate(Lambdas.GetLambda(parentType, parentMethod, MethodType.Getter, null, lambdaOrdinal), null));

    public ISyncDelegate RegisterSyncDelegateLocalFunc(Type parentType, string parentMethod, string localFuncName, Type[]? parentArgs = null) =>
        MpSync.Add(new ApiDelegate(Lambdas.GetLocalFunc(parentType, parentMethod, MethodType.Normal, parentArgs, localFuncName), null));

    public void RegisterSyncWorker<T>(SyncWorkerDelegate<T> syncWorkerDelegate, Type? targetType = null, bool isImplicit = false, bool shouldConstruct = false) =>
        MpSerialization.RegisterWorker(syncWorkerDelegate, targetType, isImplicit, shouldConstruct);

    public void RegisterAll(Assembly assembly)
    {
        foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.IsDefined(typeof(SyncMethodAttribute), false))
                {
                    RegisterSyncMethod(method, null!);
                }
            }
            foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
            {
                if (field.IsDefined(typeof(SyncFieldAttribute), false))
                {
                    RegisterSyncField(field);
                }
            }
        }
    }

    private static MethodType ToHarmony(ParentMethodType t) => t switch
    {
        ParentMethodType.Getter => MethodType.Getter,
        ParentMethodType.Setter => MethodType.Setter,
        ParentMethodType.Constructor => MethodType.Constructor,
        ParentMethodType.StaticConstructor => MethodType.StaticConstructor,
        ParentMethodType.Enumerator => MethodType.Enumerator,
        _ => MethodType.Normal,
    };

    // ---------- то, что пока не поддержано ----------

    public void RegisterDialogNodeTree(Type type, string methodOrPropertyName, SyncType[]? argTypes = null) { }
    public void RegisterDialogNodeTree(MethodInfo method) { }
#pragma warning disable CS0618
    public void RegisterPauseLock(PauseLockDelegate pauseLock) { }
#pragma warning restore CS0618
    public void RegisterDefaultLetterChoice(MethodInfo method, Type? letterType = null) { }

    public Thing GetThingById(int id) => MpSerialization.FindThingById(id, Find.CurrentMap)!;

    public bool TryGetThingById(int id, out Thing value)
    {
        value = MpSerialization.FindThingById(id, Find.CurrentMap)!;
        return value != null;
    }

    public IReadOnlyList<IPlayerInfo> GetPlayers() =>
        RWMLWC.Client.SessionView.SessionUi.Players.Select(p => (IPlayerInfo)new ApiPlayer(p.PlayerId, p.Name ?? "")).ToList();

    public IPlayerInfo GetPlayerById(int id) => GetPlayers().FirstOrDefault(p => p.Id == id)!;

    private static readonly EmptySessions sessions = new EmptySessions();
    public ISessionManager GetGlobalSessionManager() => sessions;
    public ISessionManager GetLocalSessionManager(Map map) => sessions;
    public void SetCurrentSessionWithTransferables(ISessionWithTransferables session) { }

    private sealed class ApiPlayer : IPlayerInfo
    {
        public ApiPlayer(int id, string name)
        {
            Id = id;
            Username = name;
        }

        public int Id { get; }
        public string Username { get; }
        public int CurrentMapIndex => -1;
        public Map CurrentMap => null!;
        public IReadOnlyList<int> SelectedThingsByIds => Array.Empty<int>();
        public IReadOnlyList<Thing> SelectedThings => Array.Empty<Thing>();
    }

    /// <summary>Сессии модов (общие окна) не поддержаны: менеджер всегда пуст и сессий не принимает.</summary>
    private sealed class EmptySessions : ISessionManager
    {
        public IReadOnlyList<Session> AllSessions => Array.Empty<Session>();
        public IReadOnlyList<ExposableSession> ExposableSessions => Array.Empty<ExposableSession>();
        public IReadOnlyList<SemiPersistentSession> SemiPersistentSessions => Array.Empty<SemiPersistentSession>();
        public IReadOnlyList<ITickingSession> TickingSessions => Array.Empty<ITickingSession>();
        public bool AnySessionActive => false;
        public bool AddSession(Session session) => false;
        public Session GetOrAddSessionAnyConflict(Session session) => session;
        public T GetOrAddSession<T>(T session) where T : Session => session;
        public bool RemoveSession(Session session) => false;
        public T GetFirstOfType<T>() where T : Session => null!;
        public T GetFirstWithId<T>(int id) where T : Session => null!;
        public Session GetFirstWithId(int id) => null!;
        public bool IsAnySessionCurrentlyPausing(Map map) => false;
    }
}

/// <summary>
/// Поиск лямбд и локальных функций по родительскому методу и номеру — как MpMethodUtil в RimWorld Multiplayer
/// (rwmt, MIT, Copyright (c) 2018 Zetrith): имена, которые компилятор C# даёт замыканиям.
/// </summary>
internal static class Lambdas
{
    private const string DisplayClassPrefix = "<>c__DisplayClass";
    private const string SharedDisplayClass = "<>c";
    private const string LambdaInfix = "b__";
    private const string LocalFuncInfix = "g__";
    private const string StateMachineInfix = "d__";

    public static MethodInfo GetLambda(Type parentType, string? parentMethod, MethodType parentMethodType, Type[]? parentArgs, int ordinal)
    {
        MethodBase parent = GetMethod(parentType, parentMethod, parentMethodType, parentArgs)
            ?? throw new Exception($"Multiplayer API: нет метода ({parentMethodType}) {parentType}::{parentMethod}");
        int id = DebugId(parent);
        string displayPrefix = $"{DisplayClassPrefix}{id}_";
        string shortName = $"<{parent.Name}>{LambdaInfix}{ordinal}";
        MethodInfo? lambda = parentType.GetNestedTypes(AccessTools.all)
            .Where(t => t.Name.StartsWith(displayPrefix))
            .SelectMany(t => AccessTools.GetDeclaredMethods(t))
            .FirstOrDefault(m => m.Name == shortName);
        string fullName = $"<{parent.Name}>{LambdaInfix}{id}_{ordinal}";
        lambda ??= AccessTools.Method(parentType, fullName);
        if (lambda == null && AccessTools.Inner(parentType, SharedDisplayClass) is Type shared)
        {
            lambda = AccessTools.Method(shared, fullName);
        }
        return lambda ?? throw new Exception($"Multiplayer API: нет лямбды {ordinal} в {parentType}::{parent.Name} (номер {id})");
    }

    public static MethodInfo GetLocalFunc(Type parentType, string? parentMethod, MethodType parentMethodType, Type[]? parentArgs, string localFunc)
    {
        MethodBase parent = GetMethod(parentType, parentMethod, parentMethodType, parentArgs)
            ?? throw new Exception($"Multiplayer API: нет метода ({parentMethodType}) {parentType}::{parentMethod}");
        int id = DebugId(parent);
        string displayPrefix = $"{DisplayClassPrefix}{id}_";
        string prefix = $"<{parent.Name}>{LocalFuncInfix}{localFunc}|";
        string prefixWithId = $"<{parent.Name}>{LocalFuncInfix}{localFunc}|{id}";
        MethodInfo[] candidates = parentType.GetNestedTypes(AccessTools.all)
            .Where(t => t.Name.StartsWith(displayPrefix))
            .SelectMany(t => AccessTools.GetDeclaredMethods(t))
            .Where(m => m.Name.StartsWith(prefix))
            .Concat(AccessTools.GetDeclaredMethods(parentType).Where(m => m.Name.StartsWith(prefixWithId)))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new Exception($"Multiplayer API: локальная функция {localFunc} в {parentType}::{parent.Name}: найдено {candidates.Length}");
        }
        return candidates[0];
    }

    private static int DebugId(MethodBase method)
    {
        foreach (CodeInstruction inst in PatchProcessor.GetOriginalInstructions(method))
        {
            if (inst.opcode == OpCodes.Newobj && inst.operand is MethodBase ctor && ctor.DeclaringType?.Name is string cls)
            {
                if (cls.StartsWith(DisplayClassPrefix))
                {
                    return int.Parse(Until(cls.Substring(DisplayClassPrefix.Length), '_'));
                }
                if (cls.Contains(StateMachineInfix))
                {
                    return int.Parse(After(cls, '>').Substring(StateMachineInfix.Length));
                }
            }
            else if ((inst.opcode == OpCodes.Ldftn || inst.opcode == OpCodes.Call) && inst.operand is MethodBase f
                && f.Name.StartsWith("<") && After(f.Name, '>').Count(c => c == '_') == 3)
            {
                if (f.Name.Contains(LambdaInfix))
                {
                    return int.Parse(Until(After(f.Name, '>').Substring(LambdaInfix.Length), '_'));
                }
                if (f.Name.Contains(LocalFuncInfix))
                {
                    return int.Parse(Until(After(f.Name, '|'), '_'));
                }
            }
        }
        throw new Exception($"Multiplayer API: не найден номер метода {method.DeclaringType}::{method.Name}");
    }

    private static MethodBase? GetMethod(Type type, string? name, MethodType methodType, Type[]? args) => methodType switch
    {
        MethodType.Normal => name == null ? null : AccessTools.DeclaredMethod(type, name, args),
        MethodType.Getter => name == null ? AccessTools.DeclaredIndexer(type, args)?.GetGetMethod(true) : AccessTools.DeclaredProperty(type, name)?.GetGetMethod(true),
        MethodType.Setter => name == null ? AccessTools.DeclaredIndexer(type, args)?.GetSetMethod(true) : AccessTools.DeclaredProperty(type, name)?.GetSetMethod(true),
        MethodType.Constructor => AccessTools.DeclaredConstructor(type, args),
        MethodType.StaticConstructor => AccessTools.GetDeclaredConstructors(type).FirstOrDefault(c => c.IsStatic),
        MethodType.Enumerator => name == null ? null : AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(type, name, args)),
        _ => null,
    };

    private static string After(string s, char c) => s.Substring(s.IndexOf(c) + 1);
    private static string Until(string s, char c) => s.IndexOf(c) < 0 ? s : s.Substring(0, s.IndexOf(c));
}

/// <summary>Диагностика (T-085): что моды зарегистрировали через Multiplayer API — RWMLWC/mpapi-registry.txt.</summary>
[StaticConstructorOnStartup]
internal static class MpApiRegistryDump
{
    static MpApiRegistryDump()
    {
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            try
            {
                System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>
                {
                    $"# вызовов {MpSync.Calls.Count}, полей {MpSync.Fields.Count}"
                };
                foreach (ApiCall call in MpSync.Calls)
                {
                    lines.Add((call is ApiDelegate ? "delegate " : "method ") + call.index + " " + call.key);
                }
                foreach (ApiField field in MpSync.Fields)
                {
                    lines.Add("field " + field.index + " " + field.key);
                }
                string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "mpapi-registry.txt"), lines);
                Log.Message($"[RWMLWC] Multiplayer API: моды зарегистрировали вызовов {MpSync.Calls.Count}, полей {MpSync.Fields.Count}");
            }
            catch (System.Exception ex)
            {
                Log.Warning("[RWMLWC] Multiplayer API: выгрузка списка не удалась: " + ex.Message);
            }
        });
    }
}
