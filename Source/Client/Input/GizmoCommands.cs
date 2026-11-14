using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Кнопки выделенных объектов (гизмо) общим механизмом (T-021), без перечисления конкретных кнопок.
///
/// Кнопка — объект, который игра создаёт заново каждый кадр, часто с замыканием внутри; передать её
/// по сети нельзя. Передаётся адрес: «у объекта X кнопка номер N». Каждый клиент заново получает
/// кнопки тех же выделенных объектов и нажимает ту же — так, как это делает сама игра
/// (GizmoGridDrawer): с группировкой одинаковых кнопок нескольких объектов.
///
/// Если нажатие открывает меню или прицеливание, они продолжаются командами MenuOption и
/// TargetPicked (см. VirtualUi).
/// </summary>
public static class GizmoCommands
{
    private const byte SourceOwn = 0;      // кнопка из GetGizmos() объекта
    private const byte SourceReverse = 1;  // «обратная разметка»: кнопка инструмента разметки для вещи

    private static readonly AccessTools.FieldRef<List<object>> gridObjects =
        AccessTools.StaticFieldRefAccess<List<object>>(AccessTools.Field(typeof(GizmoGridDrawer), "objList"));
    private static readonly AccessTools.FieldRef<List<Gizmo>> gridGizmos =
        AccessTools.StaticFieldRefAccess<List<Gizmo>>(AccessTools.Field(typeof(GizmoGridDrawer), "gizmoList"));
    private static readonly AccessTools.FieldRef<List<List<Gizmo>>> gridGroups =
        AccessTools.StaticFieldRefAccess<List<List<Gizmo>>>(AccessTools.Field(typeof(GizmoGridDrawer), "gizmoGroups"));
    private static readonly AccessTools.FieldRef<List<Gizmo>> gridFirst =
        AccessTools.StaticFieldRefAccess<List<Gizmo>>(AccessTools.Field(typeof(GizmoGridDrawer), "firstGizmos"));

    private static int capturedFrame = -1;
    private static List<Gizmo>? capturedGroup;
    private static Event? replayEvent;
    private static bool replayEventTried;

    public static int Captured { get; private set; }

    private struct Entry
    {
        public Gizmo gizmo;
        public object owner;
        public byte source;
        public int index;
    }

    // ---------- перехват ----------

    /// <summary>Из префиксов ProcessInput и ProcessGroupInput. true — выполнить оригинал на месте.</summary>
    public static bool Intercept(Gizmo gizmo)
    {
        if (!CommandBus.CaptureActive || gizmo is Designator)
        {
            // Выбор инструмента разметки — только интерфейс; сама разметка перехватывается отдельно.
            return true;
        }
        int frame = Time.frameCount;
        if (frame == capturedFrame && capturedGroup != null && ContainsRef(capturedGroup, gizmo))
        {
            return false; // то же нажатие: игра обходит остальные кнопки группы
        }

        List<Gizmo>? group = null;
        List<List<Gizmo>> groups = gridGroups();
        for (int i = 0; i < groups.Count && group == null; i++)
        {
            if (ContainsRef(groups[i], gizmo))
            {
                group = groups[i];
            }
        }
        if (group == null)
        {
            // Кнопка не из панели выделенного объекта (другое окно, мод). Механизм её не знает.
            Log.WarningOnce("[RWMLWC] кнопка вне панели выделения исполнена локально: " + gizmo.GetType().FullName, gizmo.GetType().GetHashCode() ^ 0x52574D);
            return true;
        }

        // Нажата та кнопка группы, которую игра нарисовала.
        Gizmo interacted = group[0];
        List<Gizmo> first = gridFirst();
        for (int i = 0; i < first.Count; i++)
        {
            if (ContainsRef(group, first[i]))
            {
                interacted = first[i];
                break;
            }
        }

        capturedFrame = frame;
        capturedGroup = group;

        Map map = Find.CurrentMap;
        if (map == null || !Locate(interacted, map, out Entry entry))
        {
            Messages.Message("RWMLWC: эта кнопка пока не работает в сетевой игре", MessageTypeDefOf.RejectInput, false);
            return false;
        }

        PacketWriter w = CommandBus.Begin(map);
        ValueCodec.Write(w, entry.owner);
        w.WriteByte(entry.source);
        w.WriteInt32(entry.index);
        w.WriteUInt64(Signature(interacted));
        CommandBus.Send(map, CommandKinds.GizmoClick, PermissionLevel.Full, w);
        Captured++;
        return false;
    }

    /// <summary>
    /// Отправить нажатие кнопки объекта без интерфейса (тестовые сценарии): первая кнопка владельца,
    /// для которой pick вернул true. Исполнение — тем же путём, что и у настоящего нажатия.
    /// </summary>
    public static bool SendClick(Map map, object owner, Func<Gizmo, bool> pick)
    {
        List<object> selection = new List<object>(Find.Selector.SelectedObjects);
        if (!ContainsRef(selection, owner))
        {
            selection.Add(owner);
        }
        foreach (Entry entry in BuildEntries(selection))
        {
            if (ReferenceEquals(entry.owner, owner) && entry.source == SourceOwn && pick(entry.gizmo))
            {
                PacketWriter w = CommandBus.Begin(map);
                ValueCodec.Write(w, entry.owner);
                w.WriteByte(entry.source);
                w.WriteInt32(entry.index);
                w.WriteUInt64(Signature(entry.gizmo));
                CommandBus.Send(map, CommandKinds.GizmoClick, PermissionLevel.Full, w);
                return true;
            }
        }
        return false;
    }

    // Найти, чья это кнопка и какая по счёту: тем же обходом, которым игра собрала список кнопок в этом кадре.
    private static bool Locate(Gizmo gizmo, Map map, out Entry entry)
    {
        entry = default;
        List<Gizmo> flat = gridGizmos();
        int position = -1;
        for (int i = 0; i < flat.Count; i++)
        {
            if (ReferenceEquals(flat[i], gizmo))
            {
                position = i;
                break;
            }
        }
        if (position < 0)
        {
            return false;
        }
        List<Entry> rebuilt = BuildEntries(gridObjects());
        if (position >= rebuilt.Count || rebuilt.Count != flat.Count)
        {
            return false;
        }
        entry = rebuilt[position];
        // Владелец должен передаваться по сети (вещь или зона этой карты), а кнопка — быть той же.
        bool ownerOk = (entry.owner is Thing thing && thing.Spawned && thing.Map == map) || (entry.owner is Zone zone && zone.Map == map);
        return ownerOk && Signature(entry.gizmo) == Signature(gizmo);
    }

    // Повторяет сбор кнопок из GizmoGridDrawer.DrawGizmoGridFor.
    private static List<Entry> BuildEntries(List<object> objects)
    {
        List<Entry> entries = new List<Entry>();
        for (int i = 0; i < objects.Count; i++)
        {
            object obj = objects[i];
            if (obj is ISelectable selectable)
            {
                int index = 0;
                foreach (Gizmo gizmo in selectable.GetGizmos())
                {
                    entries.Add(new Entry { gizmo = gizmo, owner = obj, source = SourceOwn, index = index++ });
                }
            }
            if (obj is Gizmo self)
            {
                entries.Add(new Entry { gizmo = self, owner = obj, source = 255, index = 0 });
            }
        }
        for (int i = 0; i < objects.Count; i++)
        {
            if (!(objects[i] is Thing thing))
            {
                continue;
            }
            List<Designator> designators = Find.ReverseDesignatorDatabase.AllDesignators;
            for (int d = 0; d < designators.Count; d++)
            {
                Command_Action? command = designators[d].CreateReverseDesignationGizmo(thing);
                if (command != null)
                {
                    entries.Add(new Entry { gizmo = command, owner = thing, source = SourceReverse, index = d });
                }
            }
        }
        return entries;
    }

    // ---------- исполнение ----------

    public static void Execute(Map map, ref PacketReader r)
    {
        object? owner = ValueCodec.Read(ref r, map);
        byte source = r.ReadByte();
        int index = r.ReadInt32();
        ulong signature = r.ReadUInt64();
        if (owner == null)
        {
            TestAutomation.Note("gizmo: owner is gone, command dropped");
            return;
        }

        // Выделение автора уже подставлено (CommandBus): собираем кнопки так же, как панель автора.
        List<object> selection = new List<object>(Find.Selector.SelectedObjects);
        if (!ContainsRef(selection, owner))
        {
            selection.Add(owner);
        }
        List<Entry> entries = BuildEntries(selection);

        Gizmo? target = null;
        int sameSignature = 0;
        Gizmo? bySignature = null;
        foreach (Entry entry in entries)
        {
            if (!ReferenceEquals(entry.owner, owner) || entry.source != source)
            {
                continue;
            }
            if (entry.index == index && Signature(entry.gizmo) == signature)
            {
                target = entry.gizmo;
                break;
            }
            if (Signature(entry.gizmo) == signature)
            {
                sameSignature++;
                bySignature = entry.gizmo;
            }
        }
        if (target == null && sameSignature == 1)
        {
            target = bySignature; // набор кнопок объекта изменился между нажатием и исполнением
        }
        if (target == null)
        {
            TestAutomation.Note($"gizmo: button {index} of {owner} not found, command dropped");
            return;
        }

        // Группировка одинаковых кнопок — как в GizmoGridDrawer.DrawGizmoGrid.
        List<Gizmo> sorted = entries.Select(e => e.gizmo).OrderBy(g => g.Order).ToList();
        List<List<Gizmo>> groups = new List<List<Gizmo>>();
        foreach (Gizmo gizmo in sorted)
        {
            bool placed = false;
            for (int j = 0; j < groups.Count; j++)
            {
                if (groups[j][0].GroupsWith(gizmo))
                {
                    groups[j].Add(gizmo);
                    groups[j][0].MergeWith(gizmo);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                groups.Add(new List<Gizmo> { gizmo });
            }
        }
        List<Gizmo> group = groups.First(g => ContainsRef(g, target));

        if (target.Disabled)
        {
            return; // пока команда шла, кнопка стала недоступной
        }
        Event ev = ReplayEvent();
        for (int i = 0; i < group.Count; i++)
        {
            Gizmo other = group[i];
            if (!ReferenceEquals(other, target) && !other.Disabled && target.InheritInteractionsFrom(other))
            {
                other.ProcessInput(ev);
            }
        }
        target.ProcessInput(ev);
        target.ProcessGroupInput(ev, group);
    }

    // Исполнение идёт вне обработки событий интерфейса; кнопкам нужен хоть какой-то Event.
    private static Event ReplayEvent()
    {
        Event? current = Event.current;
        if (current != null)
        {
            return current;
        }
        if (!replayEventTried)
        {
            replayEventTried = true;
            try
            {
                replayEvent = (Event?)Activator.CreateInstance(typeof(Event), true);
            }
            catch (Exception)
            {
                replayEvent = null;
            }
        }
        return replayEvent!;
    }

    // ---------- вспомогательное ----------

    /// <summary>
    /// Признак кнопки, не зависящий от языка игры: тип, клавиша, имя значка. Нужен, чтобы не нажать
    /// другую кнопку, если набор кнопок объекта изменился за время доставки команды.
    /// </summary>
    private static ulong Signature(Gizmo gizmo)
    {
        Fnv64 hash = Fnv64.Create();
        AddString(ref hash, gizmo.GetType().FullName);
        if (gizmo is Command command)
        {
            AddString(ref hash, command.hotKey?.defName);
            AddString(ref hash, IconName(command.icon));
        }
        return hash.Value;
    }

    private static string? IconName(object? icon)
    {
        if (icon == null)
        {
            return null;
        }
        try
        {
            return icon.GetType().GetProperty("name")?.GetValue(icon) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddString(ref Fnv64 hash, string? text)
    {
        if (text == null)
        {
            hash.Add(-1);
            return;
        }
        for (int i = 0; i < text.Length; i++)
        {
            hash.Add((int)text[i]);
        }
        hash.Add(-2);
    }

    private static bool ContainsRef<T>(List<T> list, object item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return true;
            }
        }
        return false;
    }

    // ---------- установка перехвата ----------

    public static void PatchAll(Harmony harmony)
    {
        HarmonyMethod prefix = new HarmonyMethod(typeof(GizmoCommands), nameof(Prefix));
        HarmonyMethod finalizer = new HarmonyMethod(typeof(GizmoCommands), nameof(Finalizer));
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        int patched = 0;
        int failed = 0;
        foreach (Type type in GenTypes.AllTypes)
        {
            if (!typeof(Gizmo).IsAssignableFrom(type) || typeof(Designator).IsAssignableFrom(type) || type.ContainsGenericParameters)
            {
                continue;
            }
            MethodInfo?[] methods =
            {
                type.GetMethod(nameof(Gizmo.ProcessInput), flags, null, new[] { typeof(Event) }, null),
                type.GetMethod(nameof(Gizmo.ProcessGroupInput), flags, null, new[] { typeof(Event), typeof(List<Gizmo>) }, null),
            };
            foreach (MethodInfo? method in methods)
            {
                if (method == null || method.IsAbstract || method.GetMethodBody() == null)
                {
                    continue;
                }
                try
                {
                    harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    patched++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Warning($"[RWMLWC] кнопки: не удалось перехватить {type.FullName}.{method.Name}: {ex.Message}");
                }
            }
        }
        Log.Message($"[RWMLWC] кнопки: перехвачено методов {patched}, не удалось {failed}");
    }

    private static bool Prefix(Gizmo __instance, out bool __state)
    {
        bool runOriginal = Intercept(__instance);
        // Кнопка исполняется на месте (выбор инструмента разметки и т. п.): меню, которое она откроет, — её собственное.
        __state = runOriginal;
        if (__state)
        {
            LocalInputDepth++;
        }
        return runOriginal;
    }

    private static void Finalizer(bool __state)
    {
        if (__state)
        {
            LocalInputDepth--;
        }
    }

    /// <summary>Сейчас на месте исполняется ProcessInput кнопки, которую мы не перехватываем.</summary>
    public static int LocalInputDepth { get; private set; }

    /// <summary>Сейчас рисуется панель кнопок (GizmoGridDrawer.DrawGizmoGrid).</summary>
    public static int GridDepth { get; set; }
}

[StaticConstructorOnStartup]
internal static class GizmoCommandsStartup
{
    static GizmoCommandsStartup()
    {
        try
        {
            GizmoCommands.PatchAll(RwmlwcMod.HarmonyInstance);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] кнопки: установка перехвата упала: " + ex);
        }
    }
}

// Панель кнопок рисуется здесь; нужно знать, что меню открыто именно из неё (см. VirtualUi.OnWindowAdd).
[HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid))]
internal static class Patch_GizmoGridDrawer_DrawGizmoGrid
{
    private static void Prefix()
    {
        GizmoCommands.GridDepth++;
    }

    private static void Finalizer()
    {
        GizmoCommands.GridDepth--;
    }
}
