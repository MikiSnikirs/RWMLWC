using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
    private const byte KPawnPart = 15;
    private const byte KPolicy = 16;
    private const byte KPawnAnywhere = 17;
    private const byte KIntRange = 18;
    private const byte KFloatRange = 19;
    private const byte KQualityRange = 20;
    private const byte KBill = 21;
    private const byte KBillStack = 22;
    private const byte KNewBill = 23;
    private const byte KResearchManager = 24;
    private const byte KFilter = 25;
    private const byte KStoreSettings = 26;
    private const byte KDefList = 27;
    private const byte KSlotGroup = 28;

    // Чей это фильтр: зоны-склада, вещи с настройками хранения, счёта, политики.
    private const byte OwnerZone = 1;
    private const byte OwnerThing = 2;
    private const byte OwnerBill = 3;
    private const byte OwnerPolicy = 4;

    private static readonly AccessTools.FieldRef<Bill, int> billLoadId = AccessTools.FieldRefAccess<Bill, int>("loadID");

    private static Dictionary<string, Type>? typesByName;

    /// <summary>Значения такого типа умеем передавать.</summary>
    public static bool Supports(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(byte)
            || type == typeof(string) || type.IsEnum || type == typeof(IntVec3) || type == typeof(Rot4)
            || type == typeof(ColorInt) || type == typeof(IntRange) || type == typeof(FloatRange) || type == typeof(QualityRange)
            || typeof(Def).IsAssignableFrom(type) || typeof(Thing).IsAssignableFrom(type)
            || typeof(Zone).IsAssignableFrom(type) || typeof(Area).IsAssignableFrom(type)
            || typeof(Precept).IsAssignableFrom(type) || typeof(ISlotGroup).IsAssignableFrom(type);
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
            case IntRange intRange:
                w.WriteByte(KIntRange);
                w.WriteInt32(intRange.min);
                w.WriteInt32(intRange.max);
                break;
            case FloatRange floatRange:
                w.WriteByte(KFloatRange);
                w.WriteInt32(BitConverter.SingleToInt32Bits(floatRange.min));
                w.WriteInt32(BitConverter.SingleToInt32Bits(floatRange.max));
                break;
            case QualityRange qualityRange:
                w.WriteByte(KQualityRange);
                w.WriteByte((byte)qualityRange.min);
                w.WriteByte((byte)qualityRange.max);
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
            case SlotGroup slotGroup when slotGroup.parent is Zone || slotGroup.parent is Thing:
                // Место хранения (склад-зона или стеллаж) — по его владельцу.
                w.WriteByte(KSlotGroup);
                w.WriteBool(slotGroup.parent is Zone);
                w.WriteInt32(slotGroup.parent is Zone slotZone ? slotZone.ID : ((Thing)slotGroup.parent).thingIDNumber);
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
            case KIntRange:
            {
                int min = r.ReadInt32();
                return new IntRange(min, r.ReadInt32());
            }
            case KFloatRange:
            {
                float min = BitConverter.Int32BitsToSingle(r.ReadInt32());
                return new FloatRange(min, BitConverter.Int32BitsToSingle(r.ReadInt32()));
            }
            case KQualityRange:
            {
                QualityCategory min = (QualityCategory)r.ReadByte();
                return new QualityRange(min, (QualityCategory)r.ReadByte());
            }
            case KBill:
            {
                BillStack? stack = FindBillStack(map, r.ReadInt32());
                int loadId = r.ReadInt32();
                if (stack != null)
                {
                    foreach (Bill bill in stack.Bills)
                    {
                        if (billLoadId(bill) == loadId)
                        {
                            return bill;
                        }
                    }
                }
                return null;
            }
            case KBillStack:
                return FindBillStack(map, r.ReadInt32());
            case KNewBill:
            {
                Type? type = FindType(r.ReadString());
                string defName = r.ReadString();
                Precept_ThingStyle? precept = FindPrecept(r.ReadInt32()) as Precept_ThingStyle;
                RecipeDef? recipe = type == null ? null : GenDefDatabase.GetDef(type, defName, false) as RecipeDef;
                return recipe == null ? null : BillUtility.MakeNewBill(recipe, precept);
            }
            case KResearchManager:
                return Find.ResearchManager;
            case KSlotGroup:
            {
                bool isZone = r.ReadBool();
                int id = r.ReadInt32();
                object? owner = isZone ? FindZone(map, id) : FindThing(map, id);
                return (owner as ISlotGroupParent)?.GetSlotGroup();
            }
            case KFilter:
                return ReadFilter(ref r, map);
            case KStoreSettings:
                return ReadStoreOwner(ref r, map)?.GetStoreSettings();
            case KDefList:
            {
                int count = r.ReadCount(100_000, 8);
                List<Def> list = new List<Def>(count);
                for (int i = 0; i < count; i++)
                {
                    Type? type = FindType(r.ReadString());
                    string defName = r.ReadString();
                    Def? def = type == null ? null : GenDefDatabase.GetDef(type, defName, false);
                    if (def != null)
                    {
                        list.Add(def);
                    }
                }
                return list;
            }
            case KPawnAnywhere:
                return FindPawnAnywhere(r.ReadInt32());
            case KPolicy:
            {
                string typeName = r.ReadString();
                return FindPolicy(typeName, r.ReadInt32());
            }
            case KPawnPart:
            {
                Pawn? owner = FindPawnAnywhere(r.ReadInt32());
                string fieldName = r.ReadString();
                return owner == null ? null : typeof(Pawn).GetField(fieldName, InstanceFields)?.GetValue(owner);
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

    // ---------- ссылки для команд-вызовов (T-023) ----------

    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Как Write, но понимает ещё: часть пешки (объект с полем pawn, на который ссылается поле самой пешки:
    /// настройки работ, расписание, политики…), политику (одежда, препараты, еда, чтение) и пешку,
    /// которая может быть не на карте команды.
    /// </summary>
    public static bool TryWriteAny(PacketWriter w, object? value)
    {
        switch (value)
        {
            case Pawn pawn:
                w.WriteByte(KPawnAnywhere);
                w.WriteInt32(pawn.thingIDNumber);
                return true;
            case Policy policy:
                w.WriteByte(KPolicy);
                w.WriteString(policy.GetType().FullName ?? "");
                w.WriteInt32(policy.id);
                return true;
            case ResearchManager:
                w.WriteByte(KResearchManager);
                return true;
            case ThingFilter filter:
                return TryWriteFilter(w, filter);
            case StorageSettings settings:
            {
                PacketWriter probe = new PacketWriter(16);
                if (!TryWriteStoreOwner(probe, settings.owner))
                {
                    return false;
                }
                w.WriteByte(KStoreSettings);
                TryWriteStoreOwner(w, settings.owner);
                return true;
            }
            case System.Collections.IEnumerable defs when !(value is string) && IsDefSequence(defs):
            {
                List<Def> list = new List<Def>();
                foreach (object item in defs)
                {
                    list.Add((Def)item);
                }
                w.WriteByte(KDefList);
                w.WriteInt32(list.Count);
                foreach (Def def in list)
                {
                    w.WriteString(def.GetType().FullName ?? "");
                    w.WriteString(def.defName ?? "");
                }
                return true;
            }
            case BillStack stack:
                if (!(stack.billGiver is Thing stackOwner))
                {
                    return false;
                }
                w.WriteByte(KBillStack);
                w.WriteInt32(stackOwner.thingIDNumber);
                return true;
            case Bill bill:
                if (bill.billStack == null)
                {
                    // Счёт только что создан интерфейсом и ещё не добавлен: передаём, из чего его создать.
                    // Операции (Bill_Medical) несут часть тела и так не передаются — пока не поддержаны.
                    if (bill is Bill_Medical || bill.recipe == null)
                    {
                        return false;
                    }
                    w.WriteByte(KNewBill);
                    w.WriteString(bill.recipe.GetType().FullName ?? "");
                    w.WriteString(bill.recipe.defName ?? "");
                    w.WriteInt32(bill.precept != null ? bill.precept.Id : -1);
                    return true;
                }
                if (!(bill.billStack.billGiver is Thing billOwner))
                {
                    return false;
                }
                w.WriteByte(KBill);
                w.WriteInt32(billOwner.thingIDNumber);
                w.WriteInt32(billLoadId(bill));
                return true;
        }
        if (value != null && !Supports(value.GetType()) && TryPawnPart(value, out Pawn? owner, out string? fieldName))
        {
            w.WriteByte(KPawnPart);
            w.WriteInt32(owner!.thingIDNumber);
            w.WriteString(fieldName!);
            return true;
        }
        if (value != null && !Supports(value.GetType()))
        {
            return false;
        }
        Write(w, value);
        return true;
    }

    /// <summary>Пешка, которой принадлежит объект-часть (поле pawn), и имя поля пешки, которое на него ссылается.</summary>
    public static bool TryPawnPart(object part, out Pawn? owner, out string? fieldName)
    {
        owner = null;
        fieldName = null;
        for (Type? t = part.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo? pawnField = t.GetField("pawn", InstanceFields | BindingFlags.DeclaredOnly);
            if (pawnField != null && typeof(Pawn).IsAssignableFrom(pawnField.FieldType))
            {
                owner = pawnField.GetValue(part) as Pawn;
                break;
            }
        }
        if (owner == null)
        {
            return false;
        }
        foreach (FieldInfo field in typeof(Pawn).GetFields(InstanceFields))
        {
            if (!field.FieldType.IsValueType && ReferenceEquals(field.GetValue(owner), part))
            {
                fieldName = field.Name;
                return true;
            }
        }
        return false;
    }

    public static Pawn? FindPawnAnywhere(int thingIdNumber)
    {
        foreach (Map map in Find.Maps)
        {
            foreach (Pawn pawn in map.mapPawns.AllPawns)
            {
                if (pawn.thingIDNumber == thingIdNumber)
                {
                    return pawn;
                }
            }
        }
        foreach (Pawn pawn in Find.WorldPawns.AllPawnsAlive)
        {
            if (pawn.thingIDNumber == thingIdNumber)
            {
                return pawn;
            }
        }
        return null;
    }

    // ---------- фильтры вещей и настройки хранения ----------

    private static bool IsDefSequence(System.Collections.IEnumerable sequence)
    {
        foreach (Type implemented in sequence.GetType().GetInterfaces())
        {
            if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && typeof(Def).IsAssignableFrom(implemented.GetGenericArguments()[0]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryWriteStoreOwner(PacketWriter w, IStoreSettingsParent? owner)
    {
        if (owner is Zone zone)
        {
            w.WriteByte(OwnerZone);
            w.WriteInt32(zone.ID);
            return true;
        }
        if (owner is Thing thing)
        {
            w.WriteByte(OwnerThing);
            w.WriteInt32(thing.thingIDNumber);
            return true;
        }
        return false;
    }

    private static IStoreSettingsParent? ReadStoreOwner(ref PacketReader r, Map map)
    {
        byte kind = r.ReadByte();
        int id = r.ReadInt32();
        if (kind == OwnerZone)
        {
            return FindZone(map, id) as IStoreSettingsParent;
        }
        return FindThing(map, id) as IStoreSettingsParent;
    }

    /// <summary>
    /// Фильтр вещей сам не знает, чей он. Ищем владельца: настройки хранения зон и вещей карты,
    /// счета верстаков, политики. Если фильтр — «родительский» (то, что владельцу вообще разрешено),
    /// передаётся владелец и признак. Фильтр без владельца (временный объект интерфейса) не передаётся.
    /// </summary>
    private static bool TryWriteFilter(PacketWriter w, ThingFilter filter)
    {
        foreach (Map map in Find.Maps)
        {
            foreach (Zone zone in map.zoneManager.AllZones)
            {
                if (zone is IStoreSettingsParent parent && MatchStore(w, parent, filter))
                {
                    return true;
                }
            }
            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing is IStoreSettingsParent parent && MatchStore(w, parent, filter))
                {
                    return true;
                }
                if (thing is IBillGiver giver && giver.BillStack != null)
                {
                    foreach (Bill bill in giver.BillStack.Bills)
                    {
                        bool own = ReferenceEquals(bill.ingredientFilter, filter);
                        if (own || ReferenceEquals(bill.recipe?.fixedIngredientFilter, filter))
                        {
                            w.WriteByte(KFilter);
                            w.WriteByte(OwnerBill);
                            w.WriteBool(!own);
                            w.WriteInt32(thing.thingIDNumber);
                            w.WriteInt32(billLoadId(bill));
                            return true;
                        }
                    }
                }
            }
        }
        Game game = Current.Game;
        IEnumerable<Policy>[] databases =
        {
            game.outfitDatabase.AllOutfits, game.drugPolicyDatabase.AllPolicies,
            game.foodRestrictionDatabase.AllFoodRestrictions, game.readingPolicyDatabase.AllReadingPolicies,
        };
        foreach (IEnumerable<Policy> database in databases)
        {
            foreach (Policy policy in database)
            {
                foreach (FieldInfo field in policy.GetType().GetFields(InstanceFields))
                {
                    if (field.FieldType == typeof(ThingFilter) && ReferenceEquals(field.GetValue(policy), filter))
                    {
                        w.WriteByte(KFilter);
                        w.WriteByte(OwnerPolicy);
                        w.WriteBool(false);
                        w.WriteString(policy.GetType().FullName ?? "");
                        w.WriteInt32(policy.id);
                        w.WriteString(field.Name);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool MatchStore(PacketWriter w, IStoreSettingsParent parent, ThingFilter filter)
    {
        bool own = ReferenceEquals(parent.GetStoreSettings()?.filter, filter);
        if (!own && !ReferenceEquals(parent.GetParentStoreSettings()?.filter, filter))
        {
            return false;
        }
        w.WriteByte(KFilter);
        w.WriteByte(parent is Zone ? OwnerZone : OwnerThing);
        w.WriteBool(!own);
        w.WriteInt32(parent is Zone zone ? zone.ID : ((Thing)parent).thingIDNumber);
        return true;
    }

    private static ThingFilter? ReadFilter(ref PacketReader r, Map map)
    {
        byte kind = r.ReadByte();
        bool parentFilter = r.ReadBool();
        switch (kind)
        {
            case OwnerZone:
            case OwnerThing:
            {
                int id = r.ReadInt32();
                IStoreSettingsParent? owner = null;
                foreach (Map candidate in Find.Maps)
                {
                    owner = (kind == OwnerZone ? (object?)FindZone(candidate, id) : FindThing(candidate, id)) as IStoreSettingsParent;
                    if (owner != null)
                    {
                        break;
                    }
                }
                if (owner == null)
                {
                    return null;
                }
                return parentFilter ? owner.GetParentStoreSettings()?.filter : owner.GetStoreSettings()?.filter;
            }
            case OwnerBill:
            {
                int ownerId = r.ReadInt32();
                int loadId = r.ReadInt32();
                foreach (Map candidate in Find.Maps)
                {
                    BillStack? stack = (FindThing(candidate, ownerId) as IBillGiver)?.BillStack;
                    if (stack == null)
                    {
                        continue;
                    }
                    foreach (Bill bill in stack.Bills)
                    {
                        if (billLoadId(bill) == loadId)
                        {
                            return parentFilter ? bill.recipe?.fixedIngredientFilter : bill.ingredientFilter;
                        }
                    }
                }
                return null;
            }
            case OwnerPolicy:
            {
                string typeName = r.ReadString();
                int id = r.ReadInt32();
                string fieldName = r.ReadString();
                Policy? policy = FindPolicy(typeName, id);
                return policy?.GetType().GetField(fieldName, InstanceFields)?.GetValue(policy) as ThingFilter;
            }
            default:
                throw new ProtocolException("ValueCodec: неизвестный владелец фильтра " + kind);
        }
    }

    private static BillStack? FindBillStack(Map map, int ownerThingId)
    {
        Thing? owner = FindThing(map, ownerThingId) ?? FindPawnAnywhere(ownerThingId);
        return (owner as IBillGiver)?.BillStack;
    }

    private static Precept? FindPrecept(int id)
    {
        if (id < 0)
        {
            return null;
        }
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

    private static Policy? FindPolicy(string typeName, int id)
    {
        Game game = Current.Game;
        foreach (Policy policy in game.outfitDatabase.AllOutfits)
        {
            if (policy.id == id && policy.GetType().FullName == typeName)
            {
                return policy;
            }
        }
        foreach (Policy policy in game.drugPolicyDatabase.AllPolicies)
        {
            if (policy.id == id && policy.GetType().FullName == typeName)
            {
                return policy;
            }
        }
        foreach (Policy policy in game.foodRestrictionDatabase.AllFoodRestrictions)
        {
            if (policy.id == id && policy.GetType().FullName == typeName)
            {
                return policy;
            }
        }
        foreach (Policy policy in game.readingPolicyDatabase.AllReadingPolicies)
        {
            if (policy.id == id && policy.GetType().FullName == typeName)
            {
                return policy;
            }
        }
        return null;
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
