using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using Verse;

// Сервисы сессии Multiplayer, через которые модули Compatibility (SessionServices) держат общие настройки
// модов в сейве, изолируют визуальные эффекты и выбирают, кто из игроков отвечает на окно. Повторяют смысл
// одноимённых классов Multiplayer 1.7 (Multiplayer.Client.Compatibility): без них модули Gun Recoil,
// DMS Expedition (настройки боя), Alpha Genes, Hunters Use Melee и другие не включались целиком.

namespace Multiplayer.Client.Compatibility
{
    /// <summary>Кто исполняет команду и кто этот клиент; уборка при конце сессии.</summary>
    public static class SessionLifecycle
    {
        public const int Version = 1;

        private sealed record Hooks(Action<int>? Retired, Action? Stopping);

        private static readonly SortedDictionary<string, Hooks> hooks = new SortedDictionary<string, Hooks>(StringComparer.Ordinal);
        private static bool subscribed;

        /// <summary>Автор исполняемой команды; вне исполнения команды — -1.</summary>
        public static int ExecutingPlayerId => CommandBus.Executing ? CommandBus.CurrentPlayerId : -1;

        public static int LocalPlayerId => LockstepClient.InSession ? LockstepClient.PlayerId : -1;

        public static void Register(string key, Action<int>? orderedPlayerRetired, Action? localSessionStopping)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A stable registration key is required", nameof(key));
            }
            if (orderedPlayerRetired == null && localSessionStopping == null)
            {
                throw new ArgumentException("At least one callback is required");
            }
            hooks.Add(key, new Hooks(orderedPlayerRetired, localSessionStopping));
            Subscribe();
        }

        internal static void Subscribe()
        {
            if (!subscribed)
            {
                subscribed = true;
                LockstepClient.SessionStopping += Stopping;
            }
        }

        // «Игрок ушёл насовсем» (PlayerRetired) в RWMLWC не наступает: ушедший остаётся в составе и может вернуться.
        // Решения, закреплённые за ним (DecisionOwnership), остаются за ним.

        private static void Stopping()
        {
            RoomSettings.Stopping();
            LocalOnlyFrames.Stopping();
            foreach (KeyValuePair<string, Hooks> hook in hooks)
            {
                try
                {
                    hook.Value.Stopping?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Error($"[RWMLWC] уборка после сессии для {hook.Key} упала: {ex}");
                }
            }
        }
    }

    /// <summary>Чисто визуальный код: свой Rand, общий не сдвигается (как VisualRandIsolation).</summary>
    public static class Presentation
    {
        public const int Version = 2;

        private sealed class Scope : IDisposable
        {
            private readonly long token;

            public Scope(long token) => this.token = token;

            public void Dispose() => EndLocalOnly(token);
        }

        private sealed class Empty : IDisposable
        {
            public static readonly Empty Instance = new Empty();

            public void Dispose() { }
        }

        public static IDisposable EnterLocalOnly() => BeginLocalOnly(out long token) ? new Scope(token) : Empty.Instance;

        public static bool BeginLocalOnly(out long token) => LocalOnlyFrames.Begin(out token);

        public static void EndLocalOnly(long token) => LocalOnlyFrames.End(token);

        public static void RunLocal(Action presentation)
        {
            if (presentation == null)
            {
                throw new ArgumentNullException(nameof(presentation));
            }
            using (EnterLocalOnly())
            {
                presentation();
            }
        }
    }

    /// <summary>
    /// Рамки «только у себя»: Rand.PushState при входе, PopState при выходе. Выходы могут прийти не по порядку
    /// (префикс и финализатор разных патчей) — рамка снимается, когда она верхняя и стек Rand вернулся к её глубине.
    /// </summary>
    internal static class LocalOnlyFrames
    {
        private struct Frame
        {
            internal Game? Game;
            internal long Token;
            internal int RandDepth;
            internal bool ReleaseRequested;
        }

        private static readonly AccessTools.FieldRef<Stack<ulong>> RandStack =
            AccessTools.StaticFieldRefAccess<Stack<ulong>>(AccessTools.Field(typeof(Rand), "stateStack"));

        private static readonly List<Frame> frames = new List<Frame>(4);
        private static long nextToken;

        internal static bool Begin(out long token)
        {
            token = 0;
            if (!LockstepClient.InSession || !UnityData.IsInMainThread)
            {
                return false;
            }
            SessionLifecycle.Subscribe();
            PruneStale();
            Drain();
            token = ++nextToken;
            frames.Add(new Frame { Game = Current.Game, Token = token, RandDepth = RandStack().Count + 1 });
            Rand.PushState();
            return true;
        }

        internal static void End(long token)
        {
            if (token == 0 || !UnityData.IsInMainThread)
            {
                return;
            }
            PruneStale();
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                if (frames[i].Token == token)
                {
                    Frame f = frames[i];
                    f.ReleaseRequested = true;
                    frames[i] = f;
                    Drain();
                    break;
                }
            }
        }

        private static void PruneStale()
        {
            if (frames.Count > 0 && frames[frames.Count - 1].Game != Current.Game)
            {
                frames.Clear();
            }
        }

        private static void Drain()
        {
            while (frames.Count > 0)
            {
                Frame top = frames[frames.Count - 1];
                if (!top.ReleaseRequested || RandStack().Count > top.RandDepth)
                {
                    break;
                }
                frames.RemoveAt(frames.Count - 1);
                if (RandStack().Count == top.RandDepth)
                {
                    Rand.PopState();
                }
            }
        }

        internal static void Stopping()
        {
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                f.ReleaseRequested = true;
                frames[i] = f;
            }
            Drain();
            frames.Clear();
        }
    }

    /// <summary>Кто из игроков отвечает на решение (окно мода): владелец и номер версии решения, лежат в сейве.</summary>
    public static class DecisionOwnership
    {
        public const int Version = 1;

        private sealed class State : IExposable
        {
            internal int Owner = -1;
            internal int Revision;

            public void ExposeData()
            {
                Scribe_Values.Look(ref Revision, "revision");
                Scribe_Values.Look(ref Owner, "owner", -1);
            }
        }

        private static State Require(IExposable state) => state as State ?? throw new ArgumentException("Unknown ownership state", nameof(state));

        public static IExposable Create() => new State();

        public static int GetOwner(IExposable state) => Require(state).Owner;

        public static int GetRevision(IExposable state) => Require(state).Revision;

        public static void SetOwner(IExposable state, int owner) => Require(state).Owner = owner;

        public static void SetRevision(IExposable state, int revision) => Require(state).Revision = revision;

        public static bool Matches(IExposable state, int owner, int revision)
        {
            State s = Require(state);
            return s.Owner == owner && s.Revision == revision;
        }

        public static bool CanChoose(IExposable state, int owner, int revision) =>
            owner >= 0 && SessionLifecycle.ExecutingPlayerId == owner && Matches(state, owner, revision);

        public static bool TryClaim(IExposable state, int owner, int revision)
        {
            int executing = SessionLifecycle.ExecutingPlayerId;
            if (executing < 0 || !Matches(state, owner, revision))
            {
                return false;
            }
            State s = Require(state);
            s.Owner = executing;
            s.Revision++;
            return true;
        }

        public static void Retire(IExposable state, int playerId)
        {
            State s = Require(state);
            if (s.Owner == playerId)
            {
                s.Owner = -1;
                s.Revision++;
            }
        }
    }

    /// <summary>
    /// Общие настройки мода на игру («настройки комнаты»): в сессии мод работает на значениях из сейва (они у всех
    /// одинаковые), личные настройки игрока запоминаются и возвращаются в конце сессии. Пока идёт сессия, такие
    /// настройки мода не пишутся в личный файл (патч на ModSettings.Write).
    /// </summary>
    public static class RoomSettings
    {
        public const int Version = 1;

        private abstract class Registration
        {
            internal abstract IExposable ForGame(Game game);

            internal abstract bool Owns(ModSettings settings);

            internal abstract void Stop();
        }

        private abstract class SettingsState : IExposable
        {
            public abstract void ExposeData();

            internal abstract void Ensure();

            internal abstract bool Apply(IExposable selected);
        }

        private sealed class Registration<T> : Registration where T : class, IExposable
        {
            internal readonly Func<IExposable> Capture;
            internal readonly Func<IExposable> Defaults;
            internal readonly Action<IExposable> ApplyValue;
            internal readonly Func<IExposable, bool> Validate;
            internal readonly Func<ModSettings>? Owner;
            internal readonly string Label;
            private readonly ConditionalWeakTable<Game, State<T>> states = new ConditionalWeakTable<Game, State<T>>();
            internal T? Personal;
            internal State<T>? Current;

            public Registration(Func<IExposable> capture, Func<IExposable> defaults, Action<IExposable> apply, Func<IExposable, bool> validate, Func<ModSettings>? owner, string label)
            {
                Capture = capture;
                Defaults = defaults;
                ApplyValue = apply;
                Validate = validate;
                Owner = owner;
                Label = label;
                if (!(defaults() is T value) || !validate(value))
                {
                    throw new ArgumentException("Invalid default room-settings schema");
                }
            }

            internal override IExposable ForGame(Game game) => states.GetValue(game, _ => new State<T>(this));

            internal override bool Owns(ModSettings settings) => Owner?.Invoke() == settings;

            internal void RememberPersonal()
            {
                Personal ??= (T)Capture();
            }

            internal override void Stop()
            {
                if (Personal == null)
                {
                    return;
                }
                try
                {
                    if (Current != null)
                    {
                        Current.Snapshot = (T)Capture();
                        Current.Initialized = false;
                    }
                }
                finally
                {
                    try
                    {
                        ApplyValue(Personal);
                    }
                    finally
                    {
                        Personal = null;
                        Current = null;
                    }
                }
            }
        }

        private sealed class State<T> : SettingsState where T : class, IExposable
        {
            private readonly Registration<T> registration;
            internal T? Snapshot;
            internal bool Initialized;

            public State(Registration<T> registration) => this.registration = registration;

            internal override void Ensure()
            {
                if (!LockstepClient.InSession || Initialized)
                {
                    return;
                }
                registration.RememberPersonal();
                if (Snapshot != null)
                {
                    registration.ApplyValue(registration.Validate(Snapshot) ? Snapshot : registration.Defaults());
                }
                Initialized = true;
                registration.Current = this;
            }

            internal override bool Apply(IExposable selected)
            {
                if (!LockstepClient.InSession || !(selected is T value) || !registration.Validate(value))
                {
                    return false;
                }
                Ensure();
                registration.ApplyValue(value);
                Snapshot = value;
                return true;
            }

            public override void ExposeData()
            {
                if (Scribe.mode == LoadSaveMode.Saving && LockstepClient.InSession)
                {
                    Ensure();
                    Snapshot = (T)registration.Capture();
                }
                Scribe_Deep.Look(ref Snapshot, registration.Label);
                if (Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    Initialized = false;
                    Ensure();
                }
            }
        }

        private static readonly SortedDictionary<string, Registration> registrations = new SortedDictionary<string, Registration>(StringComparer.Ordinal);
        private static bool writePatched;

        public static void Register(string key, Type snapshotType, Func<IExposable> capture, Func<IExposable> defaults, Action<IExposable> apply, Func<IExposable, bool> validate, Func<ModSettings>? personalSettings, string saveLabel)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(saveLabel))
            {
                throw new ArgumentException("Stable settings key and save label are required");
            }
            if (snapshotType == null || !snapshotType.IsClass || !typeof(IExposable).IsAssignableFrom(snapshotType))
            {
                throw new ArgumentException("Snapshot must be an exposable reference type");
            }
            ArgumentNullException.ThrowIfNull(capture);
            ArgumentNullException.ThrowIfNull(defaults);
            ArgumentNullException.ThrowIfNull(apply);
            ArgumentNullException.ThrowIfNull(validate);
            Registration value = (Registration)Activator.CreateInstance(typeof(Registration<>).MakeGenericType(snapshotType), capture, defaults, apply, validate, personalSettings, saveLabel)!;
            registrations.Add(key, value);
            SessionLifecycle.Subscribe();
            if (!writePatched)
            {
                writePatched = true;
                new Harmony("rwmlwc.mpbridge.roomsettings").Patch(
                    AccessTools.Method(typeof(ModSettings), nameof(ModSettings.Write)),
                    prefix: new HarmonyMethod(typeof(RoomSettings), nameof(WritePrefix)));
            }
        }

        public static IExposable ForGame(string key, Game game) => registrations[key].ForGame(game ?? throw new ArgumentNullException(nameof(game)));

        public static void Ensure(IExposable state) => ((SettingsState)state).Ensure();

        public static bool Apply(IExposable state, IExposable selected) => ((SettingsState)state).Apply(selected);

        public static bool AllowPersonalWrite(ModSettings settings)
        {
            if (!LockstepClient.InSession)
            {
                return true;
            }
            foreach (Registration r in registrations.Values)
            {
                if (r.Owns(settings))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool WritePrefix(ModSettings __instance) => AllowPersonalWrite(__instance);

        internal static void Stopping()
        {
            foreach (KeyValuePair<string, Registration> r in registrations)
            {
                try
                {
                    r.Value.Stop();
                }
                catch (Exception ex)
                {
                    Log.Error($"[RWMLWC] возврат личных настроек {r.Key} упал: {ex}");
                }
            }
        }
    }
}

namespace Multiplayer.Client
{
    public static partial class Extensions
    {
        /// <summary>Замер времени патчей Multiplayer (Alpha Animals исключает из него свой метод) — у нас замера нет.</summary>
        public static void PatchMeasure(System.Reflection.MethodBase original) { }
    }
}

namespace Multiplayer.Client
{
    /// <summary>
    /// Описание синхронизируемого замыкания Multiplayer. Compatibility (VEF, поведение животных) кладёт такое
    /// описание прямо во внутренний список <see cref="Sync.handlers"/> — для получателя состояния переключателя кнопки
    /// пешки. В RWMLWC нажатие кнопки и так идёт командой GizmoClick, поэтому описание только принимается.
    /// </summary>
    public class SyncDelegate
    {
        public readonly System.Type delegateType;
        public readonly System.Reflection.MethodInfo method;
        public readonly string[]? fields;

        public SyncDelegate(System.Type delegateType, System.Reflection.MethodInfo method, string[]? fields)
        {
            this.delegateType = delegateType;
            this.method = method;
            this.fields = fields;
        }
    }

    public static class Sync
    {
        public static System.Collections.Generic.List<object> handlers = new System.Collections.Generic.List<object>();
    }
}

namespace Multiplayer.Client
{
    /// <summary>
    /// Правка Multiplayer к ванильному коду: рамку для постройки без материалов (PlaceNoCostFrame) ставит только
    /// работа «строительство», а не любая выдача заданий «доставить материалы к чертежу». Compatibility (VEF,
    /// «постройки, требующие не строительного навыка») дописывает к <see cref="IsConstruction"/> вид работы, который
    /// мод задал постройке. Ставится из <see cref="AsyncTimeShimPatches.Install"/>, как остальные патчи моста.
    /// </summary>
    public static class OnlyConstructorsPlaceNoCostFrames
    {
        private static readonly System.Reflection.MethodInfo IsConstructionMethod =
            HarmonyLib.AccessTools.Method(typeof(OnlyConstructorsPlaceNoCostFrames), nameof(IsConstruction));

        internal static void Install(HarmonyLib.Harmony h)
        {
            System.Reflection.MethodInfo? target = HarmonyLib.AccessTools.Method(typeof(RimWorld.WorkGiver_ConstructDeliverResourcesToBlueprints), "NoCostFrameMakeJobFor");
            if (target != null)
            {
                h.Patch(target, transpiler: new HarmonyLib.HarmonyMethod(typeof(OnlyConstructorsPlaceNoCostFrames), nameof(Transpiler)));
            }
        }

        private static System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> insts)
        {
            int found = 0;
            foreach (HarmonyLib.CodeInstruction inst in insts)
            {
                yield return inst;
                if (inst.opcode == System.Reflection.Emit.OpCodes.Isinst && inst.operand as System.Type == typeof(RimWorld.Blueprint))
                {
                    found++;
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldnull);
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Cgt_Un);
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0);
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, IsConstructionMethod);
                    yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.And);
                }
            }
            if (found != 1)
            {
                Verse.Log.Warning($"[RWMLWC] OnlyConstructorsPlaceNoCostFrames: найдено {found} проверок «чертёж» вместо одной");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static bool IsConstruction(RimWorld.WorkGiver w) => w.def.workType == RimWorld.WorkTypeDefOf.Construction;
    }
}
