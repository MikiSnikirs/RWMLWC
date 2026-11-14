using System;
using System.Collections.Generic;
using RimWorld;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Запись значений в команду и чтение их на другом клиенте. Объекты игры передаются ссылкой:
/// Def — по имени, вещь, зона, область, принцип идеологии — по номеру.
/// </summary>
public static class ValueCodec
{
    private const byte KNull = 0;
    private const byte KBool = 1;
    private const byte KInt = 2;
    private const byte KFloat = 3;
    private const byte KString = 4;
    private const byte KEnum = 5;
    private const byte KCell = 6;
    private const byte KRot = 7;
    private const byte KDef = 8;
    private const byte KThing = 9;
    private const byte KZone = 10;
    private const byte KArea = 11;
    private const byte KPrecept = 12;
    private const byte KColorInt = 13;
    private const byte KByte = 14;

    private static Dictionary<string, Type>? typesByName;

    /// <summary>Значения такого типа умеем передавать.</summary>
    public static bool Supports(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(byte)
            || type == typeof(string) || type.IsEnum || type == typeof(IntVec3) || type == typeof(Rot4)
            || type == typeof(ColorInt)
            || typeof(Def).IsAssignableFrom(type) || typeof(Thing).IsAssignableFrom(type)
            || typeof(Zone).IsAssignableFrom(type) || typeof(Area).IsAssignableFrom(type)
            || typeof(Precept).IsAssignableFrom(type);
    }

    public static void Write(PacketWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteByte(KNull);
                break;
            case bool b:
                w.WriteByte(KBool);
                w.WriteBool(b);
                break;
            case int i:
                w.WriteByte(KInt);
                w.WriteInt32(i);
                break;
            case float f:
                w.WriteByte(KFloat);
                w.WriteInt32(BitConverter.SingleToInt32Bits(f));
                break;
            case byte bt:
                w.WriteByte(KByte);
                w.WriteByte(bt);
                break;
            case string s:
                w.WriteByte(KString);
                w.WriteString(s);
                break;
            case IntVec3 cell:
                w.WriteByte(KCell);
                WriteCell(w, cell);
                break;
            case Rot4 rot:
                w.WriteByte(KRot);
                w.WriteByte((byte)rot.AsInt);
                break;
            case ColorInt color:
                w.WriteByte(KColorInt);
                w.WriteInt32(color.r);
                w.WriteInt32(color.g);
                w.WriteInt32(color.b);
                w.WriteInt32(color.a);
                break;
            case Def def:
                w.WriteByte(KDef);
                w.WriteString(def.GetType().FullName ?? "");
                w.WriteString(def.defName ?? "");
                break;
            case Thing thing:
                w.WriteByte(KThing);
                w.WriteInt32(thing.thingIDNumber);
                break;
            case Zone zone:
                w.WriteByte(KZone);
                w.WriteInt32(zone.ID);
                break;
            case Area area:
                w.WriteByte(KArea);
                w.WriteInt32(area.ID);
                break;
            case Precept precept:
                w.WriteByte(KPrecept);
                w.WriteInt32(precept.Id);
                break;
            case Enum e:
                w.WriteByte(KEnum);
                w.WriteString(e.GetType().FullName ?? "");
                w.WriteInt64(Convert.ToInt64(e));
                break;
            default:
                throw new NotSupportedException("ValueCodec: тип " + value.GetType().FullName);
        }
    }

    /// <summary>Читает значение. Ссылка на объект, которого здесь нет, даёт null.</summary>
    public static object? Read(ref PacketReader r, Map map)
    {
        byte kind = r.ReadByte();
        switch (kind)
        {
            case KNull:
                return null;
            case KBool:
                return r.ReadBool();
            case KInt:
                return r.ReadInt32();
            case KFloat:
                return BitConverter.Int32BitsToSingle(r.ReadInt32());
            case KByte:
                return r.ReadByte();
            case KString:
                return r.ReadString();
            case KCell:
                return ReadCell(ref r);
            case KRot:
                return new Rot4(r.ReadByte());
            case KColorInt:
            {
                int red = r.ReadInt32();
                int green = r.ReadInt32();
                int blue = r.ReadInt32();
                int alpha = r.ReadInt32();
                return new ColorInt(red, green, blue, alpha);
            }
            case KDef:
            {
                Type? type = FindType(r.ReadString());
                string defName = r.ReadString();
                return type == null ? null : GenDefDatabase.GetDef(type, defName, false);
            }
            case KThing:
                return FindThing(map, r.ReadInt32());
            case KZone:
                return FindZone(map, r.ReadInt32());
            case KArea:
            {
                int id = r.ReadInt32();
                List<Area> areas = map.areaManager.AllAreas;
                for (int i = 0; i < areas.Count; i++)
                {
                    if (areas[i].ID == id)
                    {
                        return areas[i];
                    }
                }
                return null;
            }
            case KPrecept:
            {
                int id = r.ReadInt32();
                foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
                {
                    foreach (Precept precept in ideo.PreceptsListForReading)
                    {
                        if (precept.Id == id)
                        {
                            return precept;
                        }
                    }
                }
                return null;
            }
            case KEnum:
            {
                Type? type = FindType(r.ReadString());
                long number = r.ReadInt64();
                return type == null || !type.IsEnum ? null : Enum.ToObject(type, number);
            }
            default:
                throw new ProtocolException("ValueCodec: неизвестный вид значения " + kind);
        }
    }

    public static void WriteCell(PacketWriter w, IntVec3 cell)
    {
        w.WriteInt32(cell.x);
        w.WriteInt32(cell.y);
        w.WriteInt32(cell.z);
    }

    public static IntVec3 ReadCell(ref PacketReader r)
    {
        int x = r.ReadInt32();
        int y = r.ReadInt32();
        int z = r.ReadInt32();
        return new IntVec3(x, y, z);
    }

    public static Thing? FindThing(Map map, int thingIdNumber)
    {
        List<Thing> things = map.listerThings.AllThings;
        for (int i = 0; i < things.Count; i++)
        {
            if (things[i].thingIDNumber == thingIdNumber)
            {
                return things[i];
            }
        }
        return null;
    }

    public static Zone? FindZone(Map map, int id)
    {
        List<Zone> zones = map.zoneManager.AllZones;
        for (int i = 0; i < zones.Count; i++)
        {
            if (zones[i].ID == id)
            {
                return zones[i];
            }
        }
        return null;
    }

    /// <summary>Тип по полному имени среди всех сборок игры и модов.</summary>
    public static Type? FindType(string fullName)
    {
        if (typesByName == null)
        {
            Dictionary<string, Type> index = new Dictionary<string, Type>();
            foreach (Type type in GenTypes.AllTypes)
            {
                string? name = type.FullName;
                if (name != null && !index.ContainsKey(name))
                {
                    index.Add(name, type);
                }
            }
            typesByName = index;
        }
        return typesByName.TryGetValue(fullName, out Type? found) ? found : null;
    }
}
