using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RWMLWC.Client.World;

/// <summary>
/// Окно парикмахерской (станция стиля, Ideology) в сессии. Без этого окно меняло бы пешку только у одного игрока:
/// пока окно открыто, игра для предпросмотра пишет прическу прямо в пешку, а «Принять» записывает «следующий образ»
/// (<c>SetupNextLookChangeData</c>), цвета одежды и отдаёт приказ — всё у себя, мимо общего порядка.
/// Сделано как в RimWorld Multiplayer (те же имена классов — по ним модуль Compatibility для HAR досылает цвета
/// и части тела инопланетных рас):
/// <list type="bullet">
/// <item>окно открывается в тике у всех (пешка дошла до станции) — показывается только тому, кто отдал приказ;</item>
/// <item>в окне — копия пешки (<see cref="Multiplayer.Client.Patches.StylingDialog_DummyPawn"/>), настоящая не меняется;</item>
/// <item>«Принять» — команда: пешка, станция, прическа, борода, татуировки, цвет волос, цвета одежды по номерам вещей;
/// у всех исполняется то же, что делает «Принять» игры, над настоящей пешкой.</item>
/// </list>
/// </summary>
public static class StylingStationSync
{
    internal const byte OpTestStyling = 54;
    internal const byte OpStyling = 55;

    internal static readonly AccessTools.FieldRef<Dialog_StylingStation, Pawn> DialogPawn = AccessTools.FieldRefAccess<Dialog_StylingStation, Pawn>("pawn");
    internal static readonly AccessTools.FieldRef<Dialog_StylingStation, Thing> DialogStation = AccessTools.FieldRefAccess<Dialog_StylingStation, Thing>("stylingStation");
    private static readonly AccessTools.FieldRef<Dialog_StylingStation, Color> DesiredHairColor = AccessTools.FieldRefAccess<Dialog_StylingStation, Color>("desiredHairColor");
    private static readonly AccessTools.FieldRef<Dialog_StylingStation, Dictionary<Apparel, Color>> ApparelColors = AccessTools.FieldRefAccess<Dialog_StylingStation, Dictionary<Apparel, Color>>("apparelColors");
    private static readonly AccessTools.FieldRef<Dialog_StylingStation, HairDef> InitialHair = AccessTools.FieldRefAccess<Dialog_StylingStation, HairDef>("initialHairDef");
    private static readonly AccessTools.FieldRef<Dialog_StylingStation, BeardDef> InitialBeard = AccessTools.FieldRefAccess<Dialog_StylingStation, BeardDef>("initialBeardDef");
    private static readonly AccessTools.FieldRef<Dialog_StylingStation, TattooDef> InitialFace = AccessTools.FieldRefAccess<Dialog_StylingStation, TattooDef>("initialFaceTattoo");
    private static readonly AccessTools.FieldRef<Dialog_StylingStation, TattooDef> InitialBody = AccessTools.FieldRefAccess<Dialog_StylingStation, TattooDef>("initialBodyTattoo");
    private static readonly MethodInfo ResetMethod = AccessTools.Method(typeof(Dialog_StylingStation), "Reset");
    private static readonly MethodInfo ApplyApparelColorsMethod = AccessTools.Method(typeof(Dialog_StylingStation), "ApplyApparelColors");

    // ---------- окно ----------

    /// <summary>Из префикса WindowStack.Add: окно станции из тика — только автору приказа и с копией пешки.</summary>
    internal static bool OnWindowAdd(ref Window window)
    {
        if (!LockstepClient.InSession || !(window is Dialog_StylingStation dialog))
        {
            return true;
        }
        Pawn pawn = DialogPawn(dialog);
        if (pawn is Multiplayer.Client.Patches.StylingDialog_DummyPawn)
        {
            return true;
        }
        int who = OrderedBy.Get(pawn);
        bool show = who == 0 || who == LockstepClient.PlayerId; // неизвестно, кто приказал (после загрузки) — показываем всем
        TestAutomation.Note($"styling: window for pawn {pawn.thingIDNumber}, ordered by {who}, shown {show}");
        if (!show)
        {
            return false;
        }
        Dialog_StylingStation replaced;
        Rand.PushState();
        try
        {
            replaced = new Dialog_StylingStation(DummyPawn.Create(pawn), DialogStation(dialog));
        }
        catch (Exception e)
        {
            // Окно открывается в тике: исключение здесь сломало бы тик только у этого игрока (расхождение).
            // Окно с копией не собралось (мод не принимает копию пешки) — окна нет, игра идёт дальше.
            Log.Warning("[RWMLWC] styling station window with a preview pawn failed: " + e);
            TestAutomation.Note("styling: preview window failed: " + e.GetType().Name + ": " + e.Message);
            Messages.Message("RWMLWC: окно станции стиля не открылось в сетевой игре (см. журнал)", MessageTypeDefOf.RejectInput, false);
            return false;
        }
        finally
        {
            Rand.PopState();
        }
        replaced.forcePause = false; // стилизует один, остальные играют (как окно торговли)
        window = replaced;
        return true;
    }

    /// <summary>«Принять» в окне с копией пешки: команда вместо изменений на месте.</summary>
    internal static void Accept(Dialog_StylingStation dialog)
    {
        Pawn preview = DialogPawn(dialog);
        if (!(preview is Multiplayer.Client.Patches.StylingDialog_DummyPawn dummy) || dummy.origPawn == null)
        {
            return;
        }
        Pawn pawn = dummy.origPawn;
        Thing? station = DialogStation(dialog);
        Map map = pawn.MapHeld ?? Find.CurrentMap ?? Find.Maps[0];
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(OpStyling);
        w.WriteInt32(pawn.thingIDNumber);
        w.WriteBool(station != null);
        w.WriteInt32(station?.thingIDNumber ?? 0);
        w.WriteString(preview.story.hairDef?.defName ?? "");
        w.WriteString(preview.style.beardDef?.defName ?? "");
        w.WriteString(preview.style.FaceTattoo?.defName ?? "");
        w.WriteString(preview.style.BodyTattoo?.defName ?? "");
        WriteColor(w, DesiredHairColor(dialog));
        Dictionary<Apparel, Color> colors = ApparelColors(dialog);
        w.WriteInt32(colors.Count);
        foreach (KeyValuePair<Apparel, Color> kv in colors.OrderBy(kv => kv.Key.thingIDNumber))
        {
            w.WriteInt32(kv.Key.thingIDNumber);
            WriteColor(w, kv.Value);
        }
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
        TestAutomation.Note($"styling: accept sent for pawn {pawn.thingIDNumber}: hair {preview.story.hairDef?.defName}, beard {preview.style.beardDef?.defName}");
    }

    public static void Execute(ref PacketReader r)
    {
        Pawn? pawn = ValueCodec.FindPawnAnywhere(r.ReadInt32());
        bool hadStation = r.ReadBool();
        int stationId = r.ReadInt32();
        HairDef? hair = DefDatabase<HairDef>.GetNamedSilentFail(r.ReadString());
        BeardDef? beard = DefDatabase<BeardDef>.GetNamedSilentFail(r.ReadString());
        TattooDef? face = DefDatabase<TattooDef>.GetNamedSilentFail(r.ReadString());
        TattooDef? body = DefDatabase<TattooDef>.GetNamedSilentFail(r.ReadString());
        Color hairColor = ReadColor(ref r);
        int count = r.ReadCount(256, 20);
        var colors = new List<(int id, Color color)>(count);
        for (int i = 0; i < count; i++)
        {
            colors.Add((r.ReadInt32(), ReadColor(ref r)));
        }
        if (pawn == null || pawn.Destroyed)
        {
            TestAutomation.Note("styling: pawn is gone, dropped");
            return;
        }
        Thing? station = hadStation && pawn.MapHeld != null ? ValueCodec.FindThing(pawn.MapHeld, stationId) : null;
        if (hadStation && (station == null || !station.Spawned))
        {
            TestAutomation.Note("styling: station is gone, dropped");
            return;
        }
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(new object[] { pawn }, CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        MapRandContext.Enter(pawn.MapHeld);
        // То же, что «Принять» окна игры (Dialog_StylingStation.DrawBottomButtons), над настоящей пешкой.
        var dialog = new Dialog_StylingStation(pawn, station);
        DesiredHairColor(dialog) = hairColor;
        Dictionary<Apparel, Color> apparelColors = ApparelColors(dialog);
        apparelColors.Clear();
        foreach ((int id, Color color) in colors)
        {
            Apparel? a = pawn.apparel?.WornApparel.FirstOrDefault(x => x.thingIDNumber == id);
            if (a != null)
            {
                apparelColors[a] = color;
            }
        }
        pawn.story.hairDef = hair;
        pawn.style.beardDef = beard;
        pawn.style.FaceTattoo = face;
        pawn.style.BodyTattoo = body;
        bool changed = pawn.story.hairDef != InitialHair(dialog) || pawn.style.beardDef != InitialBeard(dialog)
            || pawn.style.FaceTattoo != InitialFace(dialog) || pawn.style.BodyTattoo != InitialBody(dialog) || pawn.story.HairColor != hairColor;
        if (changed)
        {
            if (station != null)
            {
                pawn.style.SetupNextLookChangeData(pawn.story.hairDef, pawn.style.beardDef, pawn.style.FaceTattoo, pawn.style.BodyTattoo, hairColor);
                ResetMethod.Invoke(dialog, new object[] { true });
                pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.UseStylingStation, station), JobTag.Misc);
            }
            else
            {
                pawn.story.HairColor = hairColor;
                pawn.style.Notify_StyleItemChanged();
            }
        }
        ApplyApparelColorsMethod.Invoke(dialog, null);
        TestAutomation.Note($"styling: executed by player {CommandBus.CurrentPlayerId} for pawn {pawn.thingIDNumber}: changed {changed}, colors {apparelColors.Count}");
    }

    private static void WriteColor(PacketWriter w, Color c)
    {
        w.WriteInt32(BitConverter.SingleToInt32Bits(c.r));
        w.WriteInt32(BitConverter.SingleToInt32Bits(c.g));
        w.WriteInt32(BitConverter.SingleToInt32Bits(c.b));
        w.WriteInt32(BitConverter.SingleToInt32Bits(c.a));
    }

    private static Color ReadColor(ref PacketReader r) => new Color(
        BitConverter.Int32BitsToSingle(r.ReadInt32()), BitConverter.Int32BitsToSingle(r.ReadInt32()),
        BitConverter.Int32BitsToSingle(r.ReadInt32()), BitConverter.Int32BitsToSingle(r.ReadInt32()));

    // ---------- проверка (ключ метки stylingtest=1) ----------

    private static int _step;
    private static int _pawnId;
    private static string? _lastLook;
    private static Game? _testGame;

    /// <summary>
    /// Игрок 1: на +300 тестовая команда ставит станцию стиля рядом с первым колонистом и отдаёт ему приказ «Сменить стиль»
    /// (как пункт меню станции — командой). Когда окно открылось у автора, меняет прическу, бороду и цвет волос
    /// копии и нажимает «Принять» (тот же метод, что кнопка). Оба клиента пишут образ пешки при каждом изменении.
    /// </summary>
    internal static void TestFrame(int tick, int startTick)
    {
        if (!ReferenceEquals(_testGame, Current.Game))
        {
            _testGame = Current.Game;
            _lastLook = null;
        }
        if (_step == 0 && LockstepClient.PlayerId == 1 && tick >= startTick + 300)
        {
            _step = 1;
            Map map = Find.CurrentMap ?? Find.Maps[0];
            PacketWriter w = CommandBus.Begin(map);
            w.WriteByte(OpTestStyling);
            LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
        }
        if (_step == 1 && LockstepClient.PlayerId == 1)
        {
            var dialog = Find.WindowStack.WindowOfType<Dialog_StylingStation>();
            if (dialog != null)
            {
                _step = 2;
                Pawn preview = DialogPawn(dialog);
                HairDef? hair = DefDatabase<HairDef>.AllDefsListForReading.Where(h => h != preview.story.hairDef).OrderBy(h => h.defName).FirstOrDefault();
                BeardDef? beard = DefDatabase<BeardDef>.AllDefsListForReading.Where(b => b != preview.style.beardDef && !b.noGraphic).OrderBy(b => b.defName).FirstOrDefault();
                preview.story.hairDef = hair;
                if (beard != null) preview.style.beardDef = beard;
                DesiredHairColor(dialog) = new Color(0.2f, 0.6f, 0.3f, 1f);
                if (CaravanSelfTest.ArmKey("stylingrace=1"))
                {
                    TestAutomation.Note("styling test: preview skin set " + SetFirstChannel(preview, new Color(0.9f, 0.1f, 0.1f, 1f)));
                }
                TestAutomation.Note($"styling test: preview pawn is dummy {preview is Multiplayer.Client.Patches.StylingDialog_DummyPawn}, real hair {(preview as Multiplayer.Client.Patches.StylingDialog_DummyPawn)?.origPawn.story.hairDef?.defName}");
                Multiplayer.Client.Patches.StylingDialog_Marker.drawing = dialog;
                try
                {
                    Multiplayer.Client.Patches.StylingDialog_HandleAccept.Press();
                }
                finally
                {
                    Multiplayer.Client.Patches.StylingDialog_Marker.drawing = null;
                }
            }
        }
        Pawn? pawn = _pawnId != 0 ? ValueCodec.FindPawnAnywhere(_pawnId) : null;
        if (pawn != null)
        {
            string look = $"hair {pawn.story.hairDef?.defName}, beard {pawn.style.beardDef?.defName}, color {pawn.story.HairColor}, next {pawn.style.nextHairDef?.defName}, job {pawn.CurJobDef?.defName}, channel {FirstChannel(pawn)}";
            if (look != _lastLook)
            {
                _lastLook = look;
                TestAutomation.Note($"styling test: tick {Find.TickManager.TicksGame} pawn {pawn.thingIDNumber}: {look}");
            }
        }
    }

    private static ThingComp? AlienComp(Pawn pawn) =>
        pawn.AllComps?.FirstOrDefault(c => c.GetType().FullName == "AlienRace.AlienPartGenerator+AlienComp");

    private static System.Collections.IDictionary? Channels(ThingComp comp) =>
        AccessTools.Field(comp.GetType(), "colorChannels")?.GetValue(comp) as System.Collections.IDictionary;

    // Каналы, которые HAR пересчитывает сам (кожа из генов, волосы из пешки и т. п.), для проверки не годятся.
    private static readonly HashSet<string> Derived = new HashSet<string> { "skin", "skinBase", "hair", "tattoo", "favorite", "ideo", "mech" };

    private static string? ChannelKey(System.Collections.IDictionary channels) =>
        channels.Keys.Cast<string>().Where(k => !Derived.Contains(k) && k != "base").OrderBy(k => k).FirstOrDefault()
        ?? (channels.Contains("base") ? "base" : null);

    /// <summary>Первый цвет канала «skin» (или первого по имени) компонента расы HAR; пусто — не раса HAR.</summary>
    private static string FirstChannel(Pawn pawn)
    {
        ThingComp? comp = AlienComp(pawn);
        System.Collections.IDictionary? channels = comp == null ? null : Channels(comp);
        string? key = channels == null ? null : ChannelKey(channels);
        if (key == null) return "-";
        return string.Join("; ", channels!.Keys.Cast<string>().OrderBy(k => k).Select(k =>
        {
            object v = channels[k]!;
            Color c = (Color)v.GetType().GetField("first")!.GetValue(v)!;
            return $"{k}={c.r:F2},{c.g:F2},{c.b:F2}";
        }));
    }

    private static string SetFirstChannel(Pawn preview, Color color)
    {
        ThingComp? comp = AlienComp(preview);
        System.Collections.IDictionary? channels = comp == null ? null : Channels(comp);
        string? key = channels == null ? null : ChannelKey(channels);
        if (key == null) return "no channel";
        object v = channels![key]!;
        object? second = v.GetType().GetField("second")?.GetValue(v);
        channels[key] = Activator.CreateInstance(v.GetType(), color, second);
        return key;
    }

    internal static void ExecuteTest(ref PacketReader r)
    {
        if (!CaravanSelfTest.ArmKey("stylingtest=1"))
        {
            return;
        }
        Map? map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome);
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail("StylingStation");
        Pawn? pawn = map?.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault();
        if (map != null && pawn != null && CaravanSelfTest.ArmKey("stylingrace=1"))
        {
            // Колонист расы HAR (первая по имени человекоподобная раса, у которой есть вид пешки).
            ThingDef? race = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.GetType().FullName == "AlienRace.ThingDef_AlienRace" && d.race != null && d.race.Humanlike)
                .OrderBy(d => d.defName)
                .FirstOrDefault(d => DefDatabase<PawnKindDef>.AllDefsListForReading.Any(k => k.race == d));
            PawnKindDef? kind = race == null ? null : DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.race == race).OrderBy(k => k.defName).First();
            if (kind == null)
            {
                TestAutomation.Note("styling test: no HAR race with a pawn kind");
                return;
            }
            Pawn alien = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true));
            GenSpawn.Spawn(alien, CellFinder.StandableCellNear(pawn.Position, map, 5f), map);
            TestAutomation.Note($"styling test: spawned {kind.defName} ({race!.defName}) pawn {alien.thingIDNumber}");
            pawn = alien;
        }
        if (map == null || def == null || pawn == null)
        {
            TestAutomation.Note($"styling test: no map ({map != null}), def ({def != null}) or colonist ({pawn != null})");
            return;
        }
        IntVec3 cell = IntVec3.Invalid;
        foreach (IntVec3 c in GenRadial.RadialCellsAround(pawn.Position, 10f, false).OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ThenBy(c => c.z))
        {
            if (c.InBounds(map) && GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted && pawn.CanReach(c, PathEndMode.Touch, Danger.Deadly))
            {
                cell = c;
                break;
            }
        }
        if (!cell.IsValid)
        {
            TestAutomation.Note("styling test: no place for a station");
            return;
        }
        Thing station = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
        station.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(station, cell, map, Rot4.North);
        _pawnId = pawn.thingIDNumber;
        _step = Math.Max(_step, 1);
        pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.OpenStylingStationDialog, station), JobTag.Misc);
        TestAutomation.Note($"styling test: station {station.thingIDNumber} at {cell}, pawn {pawn.thingIDNumber} ordered by player {CommandBus.CurrentPlayerId}");
    }
}

/// <summary>Копия пешки для окна станции: те же трекеры внешности и одежды, но свои объекты.</summary>
internal static class DummyPawn
{
    private static readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();

    private static T ShallowCopy<T>(T from, T to) where T : class
    {
        Type type = from.GetType();
        if (!fields.TryGetValue(type, out FieldInfo[]? list))
        {
            var all = new List<FieldInfo>();
            for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                all.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            }
            list = all.ToArray();
            fields[type] = list;
        }
        foreach (FieldInfo f in list)
        {
            f.SetValue(to, f.GetValue(from));
        }
        return to;
    }

    private static readonly AccessTools.FieldRef<Pawn_StoryTracker, Pawn> StoryPawn = AccessTools.FieldRefAccess<Pawn_StoryTracker, Pawn>("pawn");
    private static readonly AccessTools.FieldRef<Pawn, Name> NameInt = AccessTools.FieldRefAccess<Pawn, Name>("nameInt");
    private static readonly FieldInfo MapIndex = AccessTools.Field(typeof(Thing), "mapIndexOrState");
    private static readonly AccessTools.FieldRef<Pawn_ApparelTracker, ThingOwner<Apparel>> Worn = AccessTools.FieldRefAccess<Pawn_ApparelTracker, ThingOwner<Apparel>>("wornApparel");
    private static readonly AccessTools.FieldRef<Pawn_ApparelTracker, List<Apparel>> Locked = AccessTools.FieldRefAccess<Pawn_ApparelTracker, List<Apparel>>("lockedApparel");
    private static readonly FieldInfo InnerList = AccessTools.Field(typeof(ThingOwner<Apparel>), "innerList");
    private static readonly AccessTools.FieldRef<Pawn_IdeoTracker, Ideo> IdeoField = AccessTools.FieldRefAccess<Pawn_IdeoTracker, Ideo>("ideo");
    private static readonly FieldInfo LifeStage = AccessTools.Field(typeof(Pawn_AgeTracker), "cachedLifeStageIndex");

    internal static Pawn Create(Pawn pawn)
    {
        var dummy = new Multiplayer.Client.Patches.StylingDialog_DummyPawn
        {
            origPawn = pawn,
            def = pawn.def,
            gender = pawn.gender,
        };
        MapIndex.SetValue(dummy, MapIndex.GetValue(pawn));
        NameInt(dummy) = pawn.Name;
        dummy.story = ShallowCopy(pawn.story, new Pawn_StoryTracker(dummy));
        StoryPawn(dummy.story) = dummy;
        dummy.style = ShallowCopy(pawn.style, new Pawn_StyleTracker(dummy));
        dummy.style.pawn = dummy;
        dummy.apparel = ShallowCopy(pawn.apparel, new Pawn_ApparelTracker(dummy));
        dummy.apparel.pawn = dummy;
        Locked(dummy.apparel) = Locked(pawn.apparel)?.ToList() ?? new List<Apparel>();
        ThingOwner<Apparel> worn = ShallowCopy(Worn(pawn.apparel), new ThingOwner<Apparel>());
        if (InnerList.GetValue(worn) is List<Apparel> inner)
        {
            InnerList.SetValue(worn, inner.ToList());
        }
        Worn(dummy.apparel) = worn;
        dummy.health = new Pawn_HealthTracker(dummy);
        dummy.stances = new Pawn_StanceTracker(dummy);
        dummy.pather = new Pawn_PathFollower(dummy);
        dummy.roping = new Pawn_RopeTracker(dummy);
        dummy.mindState = new Pawn_MindState(dummy);
        dummy.ideo = new Pawn_IdeoTracker(dummy);
        if (pawn.ideo != null) IdeoField(dummy.ideo) = IdeoField(pawn.ideo);
        dummy.ageTracker = new Pawn_AgeTracker(dummy);
        LifeStage.SetValue(dummy.ageTracker, LifeStage.GetValue(pawn.ageTracker));
        return dummy;
    }
}

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
internal static class Patch_WindowStack_Add_Styling
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref Window window) => StylingStationSync.OnWindowAdd(ref window);
}

/// <summary>В окне с копией пешки «Принять» — команда (см. <see cref="Multiplayer.Client.Patches.StylingDialog_HandleAccept"/>).</summary>
[HarmonyPatch(typeof(Widgets), "ButtonTextWorker")]
internal static class Patch_Widgets_ButtonTextWorker_Styling
{
    private static string? accept;

    private static void Postfix(string label, ref Widgets.DraggableResult __result)
    {
        Dialog_StylingStation? drawing = Multiplayer.Client.Patches.StylingDialog_Marker.drawing;
        if (drawing == null || !LockstepClient.InSession || !(__result == Widgets.DraggableResult.Pressed || __result == Widgets.DraggableResult.DraggedThenPressed))
        {
            return;
        }
        accept ??= "Accept".Translate();
        if (label == accept && StylingStationSync.DialogPawn(drawing!) is Multiplayer.Client.Patches.StylingDialog_DummyPawn)
        {
            Multiplayer.Client.Patches.StylingDialog_HandleAccept.Press();
            __result = Widgets.DraggableResult.Idle;
        }
    }
}

[HarmonyPatch(typeof(Dialog_StylingStation), nameof(Dialog_StylingStation.DoWindowContents))]
internal static class Patch_StylingStation_Marker
{
    private static bool Prefix(Dialog_StylingStation __instance)
    {
        // Станцию снесли, пока окно открыто, — закрыть (иначе «Принять» ушло бы к несуществующей станции).
        Thing station = StylingStationSync.DialogStation(__instance);
        if (LockstepClient.InSession && station != null && !station.Spawned)
        {
            __instance.Close();
            return false;
        }
        Multiplayer.Client.Patches.StylingDialog_Marker.drawing = __instance;
        return true;
    }

    private static void Finalizer() => Multiplayer.Client.Patches.StylingDialog_Marker.drawing = null;
}
