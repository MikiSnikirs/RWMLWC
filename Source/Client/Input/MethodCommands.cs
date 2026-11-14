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
/// Окна и вкладки (T-023). Окно с собственным содержимым (приоритеты работ, расписание, политики)
/// меняет игру не кнопкой с адресом, а прямым вызовом метода из кода отрисовки. Здесь такие методы
/// перечислены поимённо: в сессии вызов из интерфейса уходит командой «метод M у объекта X с такими
/// аргументами» и исполняется у всех.
///
/// Это единственное место, где мод знает конкретные методы игры. Окна модов, которых в списке нет,
/// меняют игру только у одного игрока — расхождение ловит сверка хешей и лечит общий снимок (T-034).
///
/// Список растёт по мере разбора окон; чего в нём ещё нет — см. docs/INPUT.md.
/// </summary>
public static class MethodCommands
{
    private sealed class Entry
    {
        public MethodBase method = null!;
        public PermissionLevel required;
        public string key = "";

        /// <summary>Для свойств: чтение текущего значения (запись того же значения командой не шлём).</summary>
        public MethodInfo? getter;

        /// <summary>Объект, который не удалось опознать, — временный объект интерфейса: вызов исполняется на месте.</summary>
        public bool localIfUnknown;
    }

    private static readonly List<Entry> entries = new List<Entry>();
    private static readonly Dictionary<MethodBase, int> indexByMethod = new Dictionary<MethodBase, int>();

    public static int Captured { get; private set; }

    private static void Method(Type type, string name, PermissionLevel required = PermissionLevel.Full, Type[]? parameters = null)
    {
        MethodInfo? method = parameters == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, parameters);
        Add(method, type.FullName + "." + name, required);
    }

    private static void Setter(Type type, string property, PermissionLevel required = PermissionLevel.Full, bool localIfUnknown = false)
    {
        Add(AccessTools.PropertySetter(type, property), type.FullName + ".set_" + property, required, AccessTools.PropertyGetter(type, property), localIfUnknown);
    }

    // Метод временного объекта интерфейса (фильтр без владельца) исполняется на месте.
    private static void FilterMethod(string name, params Type[] parameters)
    {
        string key = typeof(ThingFilter).FullName + "." + name + "(" + parameters.Length + ")";
        Add(AccessTools.Method(typeof(ThingFilter), name, parameters), key, PermissionLevel.Build, null, true);
    }

    private static void Add(MethodBase? method, string key, PermissionLevel required, MethodInfo? getter = null, bool localIfUnknown = false)
    {
        if (method == null)
        {
            Log.Warning("[RWMLWC] окна: не найден метод " + key);
            return;
        }
        indexByMethod[method] = entries.Count;
        entries.Add(new Entry { method = method, required = required, key = key, getter = getter, localIfUnknown = localIfUnknown });
    }

    // Порядок записей — часть формата команды: новые добавлять только в конец.
    private static void RegisterAll()
    {
        // Вкладка «Работа».
        Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority));
        // Вкладка «Расписание».
        Method(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment));
        // Вкладка «Назначения»: политики одежды, препаратов, еды, чтения.
        Setter(typeof(Pawn_OutfitTracker), nameof(Pawn_OutfitTracker.CurrentApparelPolicy));
        Setter(typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.CurrentPolicy));
        Setter(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy));
        Setter(typeof(Pawn_ReadingTracker), nameof(Pawn_ReadingTracker.CurrentPolicy));
        // Разрешённая область пешки.
        Setter(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap));
        // Исследования.
        Method(typeof(ResearchManager), nameof(ResearchManager.SetCurrentProject));
        Method(typeof(ResearchManager), nameof(ResearchManager.StopProject));
        // Счета верстака: добавить, удалить, переставить. Поля самого счёта — FieldWatch.
        Method(typeof(BillStack), nameof(BillStack.AddBill));
        Method(typeof(BillStack), nameof(BillStack.Delete));
        Method(typeof(BillStack), nameof(BillStack.Reorder));
        // Фильтры вещей: склады, счета, политики (вкладка «Хранение», окно счёта, окна политик).
        FilterMethod(nameof(ThingFilter.SetAllow), typeof(ThingDef), typeof(bool));
        FilterMethod(nameof(ThingFilter.SetAllow), typeof(SpecialThingFilterDef), typeof(bool));
        FilterMethod(nameof(ThingFilter.SetAllow), typeof(ThingCategoryDef), typeof(bool), typeof(IEnumerable<ThingDef>), typeof(IEnumerable<SpecialThingFilterDef>));
        FilterMethod(nameof(ThingFilter.SetDisallowAll), typeof(IEnumerable<ThingDef>), typeof(IEnumerable<SpecialThingFilterDef>));
        FilterMethod(nameof(ThingFilter.SetAllowAll), typeof(ThingFilter), typeof(bool));
        Setter(typeof(ThingFilter), nameof(ThingFilter.AllowedHitPointsPercents), PermissionLevel.Build, true);
        Setter(typeof(ThingFilter), nameof(ThingFilter.AllowedQualityLevels), PermissionLevel.Build, true);
        // Приоритет склада.
        Setter(typeof(StorageSettings), nameof(StorageSettings.Priority), PermissionLevel.Build, true);
    }

    // ---------- перехват ----------

    private static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        if (!CommandBus.CaptureActive || !indexByMethod.TryGetValue(__originalMethod, out int index))
        {
            return true;
        }
        Entry entry = entries[index];
        if (entry.getter != null && __args.Length == 1 && Equals(entry.getter.Invoke(__instance, null), __args[0]))
        {
            return true; // интерфейс каждый кадр записывает то же значение: не команда
        }
        if (entry.localIfUnknown && !ValueCodec.TryWriteAny(new PacketWriter(64), __instance))
        {
            return true; // временный объект интерфейса (фильтр нового счёта, буфер копирования)
        }
        Map? map = MapFor(__instance);
        if (map == null)
        {
            return true; // игры с картами нет (не должно случаться в сессии)
        }
        PacketWriter w = CommandBus.Begin(map);
        w.WriteInt32(index);
        w.WriteString(entry.key);
        bool ok = ValueCodec.TryWriteAny(w, __instance);
        w.WriteInt32(__args.Length);
        for (int i = 0; i < __args.Length && ok; i++)
        {
            ok = ValueCodec.TryWriteAny(w, __args[i]);
        }
        if (!ok)
        {
            Messages.Message("RWMLWC: это действие пока не работает в сетевой игре (" + entry.key + ")", MessageTypeDefOf.RejectInput, false);
            return false;
        }
        CommandBus.Send(map, CommandKinds.MethodCall, entry.required, w);
        Captured++;
        return false;
    }

    // Карта команды: где находится пешка-владелец; если она не на карте (караван) — любая карта игрока.
    internal static Map? MapFor(object instance)
    {
        Pawn? pawn = instance as Pawn;
        if (pawn == null)
        {
            ValueCodec.TryPawnPart(instance, out pawn, out _);
        }
        Map? map = pawn?.MapHeld ?? (instance as BillStack)?.billGiver?.Map ?? (instance as Bill)?.Map ?? Find.CurrentMap;
        if (map == null && Find.Maps.Count > 0)
        {
            map = Find.Maps[0];
        }
        return map;
    }

    // ---------- исполнение ----------

    public static void Execute(Map map, ref PacketReader r)
    {
        int index = r.ReadInt32();
        string key = r.ReadString();
        if (index < 0 || index >= entries.Count || entries[index].key != key)
        {
            throw new InvalidOperationException($"окна: метод {index} «{key}» не совпадает со списком этого клиента");
        }
        Entry entry = entries[index];
        object? instance = ValueCodec.Read(ref r, map);
        int count = r.ReadCount(16, 1);
        object?[] args = new object?[count];
        for (int i = 0; i < count; i++)
        {
            args[i] = ValueCodec.Read(ref r, map);
        }
        if (instance == null)
        {
            TestAutomation.Note("method: " + key + ": object is gone, dropped");
            return;
        }
        ParameterInfo[] parameters = entry.method.GetParameters();
        if (parameters.Length != count)
        {
            throw new InvalidOperationException("окна: " + key + ": число аргументов " + count);
        }
        for (int i = 0; i < count; i++)
        {
            // Ссылка, которая здесь не нашлась, допустима только там, где метод принимает null («без области»).
            if (args[i] is List<Def> defs && parameters[i].ParameterType.IsGenericType)
            {
                // Список определений пришёл общим списком; метод ждёт список своего типа.
                Type item = parameters[i].ParameterType.GetGenericArguments()[0];
                System.Collections.IList typed = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(item))!;
                foreach (Def def in defs)
                {
                    if (item.IsInstanceOfType(def))
                    {
                        typed.Add(def);
                    }
                }
                args[i] = typed;
            }
            if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]))
            {
                throw new InvalidOperationException("окна: " + key + ": аргумент " + i + " не того типа");
            }
        }
        entry.method.Invoke(instance, args);
    }

    public static void PatchAll(Harmony harmony)
    {
        RegisterAll();
        HarmonyMethod prefix = new HarmonyMethod(typeof(MethodCommands), nameof(Prefix));
        int failed = 0;
        foreach (Entry entry in entries)
        {
            try
            {
                harmony.Patch(entry.method, prefix: prefix);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"[RWMLWC] окна: не удалось перехватить {entry.key}: {ex.Message}");
            }
        }
        Log.Message($"[RWMLWC] окна: перехвачено методов {entries.Count - failed}, не удалось {failed}");
    }
}

[StaticConstructorOnStartup]
internal static class MethodCommandsStartup
{
    static MethodCommandsStartup()
    {
        try
        {
            MethodCommands.PatchAll(RwmlwcMod.HarmonyInstance);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] окна: установка перехвата упала: " + ex);
        }
    }
}
