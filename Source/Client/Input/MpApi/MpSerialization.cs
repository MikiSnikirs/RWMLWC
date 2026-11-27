using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Input.MpApi;

/// <summary>
/// Значения вызовов Multiplayer API (T-085): аргументы синхронных методов, поля замыканий, значения полей.
/// Формат свой, самоописывающий: метка вида + данные. Объекты игры — ссылкой (номер вещи, номер карты,
/// имя определения). Типы модов — их собственными сериализаторами (RegisterSyncWorker), как в Multiplayer.
/// </summary>
internal static class MpSerialization
{
    private sealed class WorkerEntry
    {
        public Type type = null!;
        public bool isImplicit;
        public bool shouldConstruct;
        public List<Delegate> delegates = new List<Delegate>();
    }

    private static readonly Dictionary<Type, WorkerEntry> workers = new Dictionary<Type, WorkerEntry>();

    private const byte TNull = 0, TBool = 1, TByte = 2, TSByte = 3, TShort = 4, TUShort = 5, TInt = 6, TUInt = 7, TLong = 8,
        TULong = 9, TFloat = 10, TDouble = 11, TString = 12, TEnum = 13, TDef = 14, TThing = 15, TMap = 16, TFaction = 17,
        TWorldObject = 18, TCell = 19, TRot = 20, TVector3 = 21, TIntRange = 22, TFloatRange = 23, TList = 24, TArray = 25,
        TType = 26, TWorker = 27, TComp = 28, THediff = 29, TLocalTarget = 30, TGlobalTarget = 31, TZone = 32, TArea = 33,
        TIdeo = 34, TAbility = 35, TCodec = 36, TSimple = 37, TVector2 = 38, TColor = 39, TQuest = 40, TSettlementCell = 41;

    public static void RegisterWorker<T>(SyncWorkerDelegate<T> del, Type? targetType, bool isImplicit, bool shouldConstruct)
    {
        Type type = targetType ?? typeof(T);
        if (!workers.TryGetValue(type, out WorkerEntry? entry))
        {
            entry = new WorkerEntry { type = type };
            workers[type] = entry;
        }
        entry.isImplicit |= isImplicit;
        entry.shouldConstruct |= shouldConstruct;
        entry.delegates.Add(del);
    }

    public static bool HasWorkerFor(Type type) => FindWorker(type) != null;

    private static WorkerEntry? FindWorker(Type type)
    {
        if (workers.TryGetValue(type, out WorkerEntry? exact))
        {
            return exact;
        }
        for (Type? t = type.BaseType; t != null; t = t.BaseType)
        {
            if (workers.TryGetValue(t, out WorkerEntry? baseEntry) && baseEntry.isImplicit)
            {
                return baseEntry;
            }
        }
        foreach (Type i in type.GetInterfaces())
        {
            if (workers.TryGetValue(i, out WorkerEntry? ifaceEntry) && ifaceEntry.isImplicit)
            {
                return ifaceEntry;
            }
        }
        return null;
    }

    // ---------- запись ----------

    public static void Write(BinaryWriter w, object? value, Type declared)
    {
        if (value == null)
        {
            w.Write(TNull);
            return;
        }
        Type type = value.GetType();
        WorkerEntry? worker = FindWorker(type) ?? (declared != typeof(object) ? FindWorker(declared) : null);
        if (worker != null && !IsBuiltin(type))
        {
            w.Write(TWorker);
            w.Write(worker.type.AssemblyQualifiedName ?? worker.type.FullName ?? "");
            w.Write(type.AssemblyQualifiedName ?? "");
            ApiSyncWriter sw = new ApiSyncWriter(w);
            foreach (Delegate del in worker.delegates)
            {
                object boxed = value;
                InvokeWorker(del, worker.type, sw, ref boxed);
            }
            return;
        }
        switch (value)
        {
            case bool b: w.Write(TBool); w.Write(b); return;
            case byte bt: w.Write(TByte); w.Write(bt); return;
            case sbyte sb: w.Write(TSByte); w.Write(sb); return;
            case short s: w.Write(TShort); w.Write(s); return;
            case ushort us: w.Write(TUShort); w.Write(us); return;
            case int i: w.Write(TInt); w.Write(i); return;
            case uint ui: w.Write(TUInt); w.Write(ui); return;
            case long l: w.Write(TLong); w.Write(l); return;
            case ulong ul: w.Write(TULong); w.Write(ul); return;
            case float f: w.Write(TFloat); w.Write(f); return;
            case double d: w.Write(TDouble); w.Write(d); return;
            case string str: w.Write(TString); w.Write(str); return;
            case Enum e: w.Write(TEnum); w.Write(type.AssemblyQualifiedName ?? ""); w.Write(Convert.ToInt64(e)); return;
            case Def def: w.Write(TDef); w.Write(def.GetType().AssemblyQualifiedName ?? ""); w.Write(def.defName ?? ""); return;
            case Type t: w.Write(TType); w.Write(t.AssemblyQualifiedName ?? ""); return;
            case Thing thing: w.Write(TThing); WriteThing(w, thing); return;
            case Map map: w.Write(TMap); w.Write(map.uniqueID); return;
            case Faction faction: w.Write(TFaction); w.Write(faction.loadID); return;
            case Ideo ideo: w.Write(TIdeo); w.Write(ideo.id); return;
            case Quest quest: w.Write(TQuest); w.Write(quest.id); return;
            case WorldObject wo: w.Write(TWorldObject); w.Write(wo.ID); return;
            case IntVec3 c: w.Write(TCell); w.Write(c.x); w.Write(c.y); w.Write(c.z); return;
            case Rot4 r: w.Write(TRot); w.Write((byte)r.AsInt); return;
            case Vector3 v: w.Write(TVector3); w.Write(v.x); w.Write(v.y); w.Write(v.z); return;
            case Vector2 v2: w.Write(TVector2); w.Write(v2.x); w.Write(v2.y); return;
            case Color col: w.Write(TColor); w.Write(col.r); w.Write(col.g); w.Write(col.b); w.Write(col.a); return;
            case IntRange ir: w.Write(TIntRange); w.Write(ir.min); w.Write(ir.max); return;
            case FloatRange fr: w.Write(TFloatRange); w.Write(fr.min); w.Write(fr.max); return;
            case PlanetTile tile: w.Write(TSettlementCell); w.Write(tile.tileId); w.Write(tile.Layer?.LayerID ?? 0); return;
            case ThingComp comp:
            {
                w.Write(TComp);
                WriteThing(w, comp.parent);
                int index = comp.parent is ThingWithComps twc ? twc.AllComps.IndexOf(comp) : -1;
                w.Write(index);
                return;
            }
            case Hediff hediff:
                w.Write(THediff);
                WriteThing(w, hediff.pawn);
                w.Write(hediff.loadID);
                return;
            case Ability ability:
                w.Write(TAbility);
                WriteThing(w, ability.pawn);
                w.Write(ability.def?.defName ?? "");
                return;
            case LocalTargetInfo lt:
                w.Write(TLocalTarget);
                w.Write(lt.HasThing);
                if (lt.HasThing)
                {
                    WriteThing(w, lt.Thing);
                }
                else
                {
                    w.Write(lt.Cell.x); w.Write(lt.Cell.y); w.Write(lt.Cell.z);
                }
                return;
            case GlobalTargetInfo gt:
                w.Write(TGlobalTarget);
                w.Write(gt.HasThing ? (byte)1 : gt.HasWorldObject ? (byte)2 : gt.IsMapTarget ? (byte)3 : (byte)0);
                if (gt.HasThing)
                {
                    WriteThing(w, gt.Thing);
                }
                else if (gt.HasWorldObject)
                {
                    w.Write(gt.WorldObject.ID);
                }
                else if (gt.IsMapTarget)
                {
                    w.Write(gt.Map.uniqueID); w.Write(gt.Cell.x); w.Write(gt.Cell.y); w.Write(gt.Cell.z);
                }
                return;
            case Zone zone: w.Write(TZone); w.Write(zone.Map?.uniqueID ?? -1); w.Write(zone.ID); return;
            case Area area: w.Write(TArea); w.Write(area.Map?.uniqueID ?? -1); w.Write(area.ID); return;
            case ISyncSimple simple:
                w.Write(TSimple);
                w.Write(type.AssemblyQualifiedName ?? "");
                foreach (FieldInfo field in SimpleFields(type))
                {
                    Write(w, field.GetValue(simple), field.FieldType);
                }
                return;
            case Array array:
                w.Write(TArray);
                w.Write(type.GetElementType()!.AssemblyQualifiedName ?? "");
                w.Write(array.Length);
                foreach (object? item in array)
                {
                    Write(w, item, type.GetElementType()!);
                }
                return;
            case IList list when type.IsGenericType:
            {
                w.Write(TList);
                w.Write(type.AssemblyQualifiedName ?? "");
                Type item = type.GetGenericArguments()[0];
                w.Write(list.Count);
                foreach (object? element in list)
                {
                    Write(w, element, item);
                }
                return;
            }
        }
        // Остальное — тем, что умеет команда окон (счета, политики, фильтры, части пешек…).
        PacketWriter codec = new PacketWriter(64);
        if (ValueCodec.TryWriteAny(codec, value))
        {
            w.Write(TCodec);
            byte[] bytes = codec.ToArray();
            w.Write(bytes.Length);
            w.Write(bytes);
            return;
        }
        throw new NotSupportedException("Multiplayer API: не умею передать " + type.FullName);
    }

    private static bool IsBuiltin(Type type) => type.IsPrimitive || type == typeof(string) || type.IsEnum;

    private static void WriteThing(BinaryWriter w, Thing? thing)
    {
        w.Write(thing?.thingIDNumber ?? -1);
        w.Write(thing?.MapHeld?.uniqueID ?? -1);
    }

    private static IEnumerable<FieldInfo> SimpleFields(Type type)
    {
        List<FieldInfo> fields = new List<FieldInfo>(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    // ---------- чтение ----------

    public static object? Read(BinaryReader r, Map map)
    {
        byte tag = r.ReadByte();
        switch (tag)
        {
            case TNull: return null;
            case TBool: return r.ReadBoolean();
            case TByte: return r.ReadByte();
            case TSByte: return r.ReadSByte();
            case TShort: return r.ReadInt16();
            case TUShort: return r.ReadUInt16();
            case TInt: return r.ReadInt32();
            case TUInt: return r.ReadUInt32();
            case TLong: return r.ReadInt64();
            case TULong: return r.ReadUInt64();
            case TFloat: return r.ReadSingle();
            case TDouble: return r.ReadDouble();
            case TString: return r.ReadString();
            case TEnum:
            {
                Type type = TypeByName(r.ReadString());
                return Enum.ToObject(type, r.ReadInt64());
            }
            case TDef:
            {
                Type type = TypeByName(r.ReadString());
                return GenDefDatabase.GetDef(type, r.ReadString(), false);
            }
            case TType: return TypeByName(r.ReadString());
            case TThing: return ReadThing(r);
            case TMap: { int id = r.ReadInt32(); return Find.Maps.Find(m => m.uniqueID == id); }
            case TFaction: { int id = r.ReadInt32(); return Find.FactionManager.AllFactionsListForReading.Find(f => f.loadID == id); }
            case TIdeo: { int id = r.ReadInt32(); return Find.IdeoManager.IdeosListForReading.Find(i => i.id == id); }
            case TQuest: { int id = r.ReadInt32(); return Find.QuestManager.QuestsListForReading.Find(q => q.id == id); }
            case TWorldObject: { int id = r.ReadInt32(); return Find.WorldObjects.AllWorldObjects.Find(o => o.ID == id); }
            case TCell: return new IntVec3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            case TRot: return new Rot4(r.ReadByte());
            case TVector3: return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            case TVector2: return new Vector2(r.ReadSingle(), r.ReadSingle());
            case TColor: return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            case TIntRange: return new IntRange(r.ReadInt32(), r.ReadInt32());
            case TFloatRange: return new FloatRange(r.ReadSingle(), r.ReadSingle());
            case TSettlementCell:
            {
                int tile = r.ReadInt32();
                int layer = r.ReadInt32();
                return new PlanetTile(tile, layer);
            }
            case TComp:
            {
                Thing? parent = ReadThing(r);
                int index = r.ReadInt32();
                return parent is ThingWithComps twc && index >= 0 && index < twc.AllComps.Count ? twc.AllComps[index] : null;
            }
            case THediff:
            {
                Pawn? pawn = ReadThing(r) as Pawn;
                int id = r.ReadInt32();
                return pawn?.health?.hediffSet?.hediffs.Find(h => h.loadID == id);
            }
            case TAbility:
            {
                Pawn? pawn = ReadThing(r) as Pawn;
                string defName = r.ReadString();
                return pawn?.abilities?.AllAbilitiesForReading.Find(a => a.def?.defName == defName);
            }
            case TLocalTarget:
                return r.ReadBoolean() ? new LocalTargetInfo(ReadThing(r)) : new LocalTargetInfo(new IntVec3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
            case TGlobalTarget:
            {
                byte kind = r.ReadByte();
                if (kind == 1)
                {
                    return new GlobalTargetInfo(ReadThing(r));
                }
                if (kind == 2)
                {
                    int id = r.ReadInt32();
                    return new GlobalTargetInfo(Find.WorldObjects.AllWorldObjects.Find(o => o.ID == id));
                }
                if (kind == 3)
                {
                    int mapId = r.ReadInt32();
                    IntVec3 cell = new IntVec3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    return new GlobalTargetInfo(cell, Find.Maps.Find(m => m.uniqueID == mapId));
                }
                return GlobalTargetInfo.Invalid;
            }
            case TZone:
            {
                Map? zmap = MapById(r.ReadInt32());
                int id = r.ReadInt32();
                return zmap == null ? null : ValueCodec.FindZone(zmap, id);
            }
            case TArea:
            {
                Map? amap = MapById(r.ReadInt32());
                int id = r.ReadInt32();
                return amap?.areaManager.AllAreas.Find(a => a.ID == id);
            }
            case TSimple:
            {
                Type type = TypeByName(r.ReadString());
                object obj = RuntimeHelpers.GetUninitializedObject(type);
                foreach (FieldInfo field in SimpleFields(type))
                {
                    field.SetValue(obj, Read(r, map));
                }
                return obj;
            }
            case TArray:
            {
                Type item = TypeByName(r.ReadString());
                int count = r.ReadInt32();
                Array array = Array.CreateInstance(item, count);
                for (int i = 0; i < count; i++)
                {
                    array.SetValue(Read(r, map), i);
                }
                return array;
            }
            case TList:
            {
                Type type = TypeByName(r.ReadString());
                int count = r.ReadInt32();
                IList list = (IList)Activator.CreateInstance(type)!;
                for (int i = 0; i < count; i++)
                {
                    list.Add(Read(r, map));
                }
                return list;
            }
            case TWorker:
            {
                Type workerType = TypeByName(r.ReadString());
                Type actual = TypeByName(r.ReadString());
                WorkerEntry entry = workers[workerType];
                object? value = entry.shouldConstruct || actual.IsValueType ? CreateInstance(actual) : null;
                ApiSyncReader sr = new ApiSyncReader(r, map);
                foreach (Delegate del in entry.delegates)
                {
                    InvokeWorker(del, entry.type, sr, ref value);
                }
                return value;
            }
            case TCodec:
            {
                byte[] bytes = r.ReadBytes(r.ReadInt32());
                PacketReader pr = new PacketReader(bytes);
                return ValueCodec.Read(ref pr, map);
            }
        }
        throw new InvalidDataException("Multiplayer API: неизвестная метка " + tag);
    }

    private static object? CreateInstance(Type type)
    {
        try
        {
            return Activator.CreateInstance(type, true);
        }
        catch (Exception)
        {
            return RuntimeHelpers.GetUninitializedObject(type);
        }
    }

    private static Map? MapById(int id) => id < 0 ? null : Find.Maps.Find(m => m.uniqueID == id);

    private static Thing? ReadThing(BinaryReader r)
    {
        int id = r.ReadInt32();
        int mapId = r.ReadInt32();
        return id < 0 ? null : FindThingById(id, MapById(mapId));
    }

    /// <summary>Вещь по номеру: на карте, в контейнерах карты, среди пешек мира и караванов.</summary>
    public static Thing? FindThingById(int id, Map? map)
    {
        if (map != null)
        {
            Thing? onMap = ValueCodec.FindThing(map, id);
            if (onMap != null)
            {
                return onMap;
            }
        }
        foreach (Map m in Find.Maps)
        {
            Thing? t = m == map ? null : ValueCodec.FindThing(m, id);
            if (t != null)
            {
                return t;
            }
        }
        Pawn? pawn = ValueCodec.FindPawnAnywhere(id);
        if (pawn != null)
        {
            return pawn;
        }
        // Вещи в контейнерах (инвентарь, снаряжение, хранилища).
        List<Thing> all = new List<Thing>();
        foreach (Map m in Find.Maps)
        {
            all.Clear();
            ThingOwnerUtility.GetAllThingsRecursively(m, ThingRequest.ForGroup(ThingRequestGroup.Everything), all, true, null, false);
            Thing? held = all.Find(t => t.thingIDNumber == id);
            if (held != null)
            {
                return held;
            }
        }
        return null;
    }

    private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

    private static Type TypeByName(string name)
    {
        if (typeCache.TryGetValue(name, out Type? cached))
        {
            return cached;
        }
        Type? type = Type.GetType(name) ?? AccessTools.TypeByName(name.Split(',')[0]);
        if (type == null)
        {
            throw new TypeLoadException("Multiplayer API: нет типа " + name);
        }
        typeCache[name] = type;
        return type;
    }

    private static void InvokeWorker(Delegate del, Type type, SyncWorker sync, ref object? value)
    {
        // SyncWorkerDelegate<T>(SyncWorker sync, ref T obj) — вызов через отражение с ref-аргументом.
        object?[] args = { sync, value };
        del.DynamicInvoke(args);
        value = args[1];
    }
}

/// <summary>Запись для сериализаторов модов (SyncWorker.isWriting = true).</summary>
internal sealed class ApiSyncWriter : SyncWorker
{
    private readonly BinaryWriter w;

    public ApiSyncWriter(BinaryWriter w) : base(true) => this.w = w;

    public override void Bind<T>(ref T obj, SyncType type) => MpSerialization.Write(w, obj, type.type ?? typeof(T));
    public override void Bind<T>(ref T obj) => MpSerialization.Write(w, obj, typeof(T));
    public override void BindType<T>(ref Type type) => w.Write(type?.AssemblyQualifiedName ?? "");
    public override void Bind(ref byte obj) => w.Write(obj);
    public override void Bind(ref sbyte obj) => w.Write(obj);
    public override void Bind(ref short obj) => w.Write(obj);
    public override void Bind(ref ushort obj) => w.Write(obj);
    public override void Bind(ref int obj) => w.Write(obj);
    public override void Bind(ref uint obj) => w.Write(obj);
    public override void Bind(ref long obj) => w.Write(obj);
    public override void Bind(ref ulong obj) => w.Write(obj);
    public override void Bind(ref float obj) => w.Write(obj);
    public override void Bind(ref double obj) => w.Write(obj);
    public override void Bind(ref string obj) { w.Write(obj != null); if (obj != null) { w.Write(obj); } }
    public override void Bind(ref bool obj) => w.Write(obj);

    public override void Bind(object obj, string name)
    {
        FieldInfo field = AccessTools.Field(obj.GetType(), name) ?? throw new MissingFieldException(obj.GetType().FullName, name);
        MpSerialization.Write(w, field.GetValue(obj), field.FieldType);
    }
}

/// <summary>Чтение для сериализаторов модов (SyncWorker.isWriting = false).</summary>
internal sealed class ApiSyncReader : SyncWorker
{
    private readonly BinaryReader r;
    private readonly Map map;

    public ApiSyncReader(BinaryReader r, Map map) : base(false)
    {
        this.r = r;
        this.map = map;
    }

    public override void Bind<T>(ref T obj, SyncType type) => obj = (T)MpSerialization.Read(r, map)!;
    public override void Bind<T>(ref T obj) => obj = (T)MpSerialization.Read(r, map)!;

    public override void BindType<T>(ref Type type)
    {
        string name = r.ReadString();
        type = (name.Length == 0 ? null : Type.GetType(name) ?? AccessTools.TypeByName(name.Split(',')[0]))!;
    }

    public override void Bind(ref byte obj) => obj = r.ReadByte();
    public override void Bind(ref sbyte obj) => obj = r.ReadSByte();
    public override void Bind(ref short obj) => obj = r.ReadInt16();
    public override void Bind(ref ushort obj) => obj = r.ReadUInt16();
    public override void Bind(ref int obj) => obj = r.ReadInt32();
    public override void Bind(ref uint obj) => obj = r.ReadUInt32();
    public override void Bind(ref long obj) => obj = r.ReadInt64();
    public override void Bind(ref ulong obj) => obj = r.ReadUInt64();
    public override void Bind(ref float obj) => obj = r.ReadSingle();
    public override void Bind(ref double obj) => obj = r.ReadDouble();
    public override void Bind(ref string obj) => obj = (r.ReadBoolean() ? r.ReadString() : null)!;
    public override void Bind(ref bool obj) => obj = r.ReadBoolean();

    public override void Bind(object obj, string name)
    {
        FieldInfo field = AccessTools.Field(obj.GetType(), name) ?? throw new MissingFieldException(obj.GetType().FullName, name);
        field.SetValue(obj, MpSerialization.Read(r, map));
    }
}
