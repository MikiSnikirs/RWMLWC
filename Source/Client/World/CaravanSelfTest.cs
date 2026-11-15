using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Sim;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RWMLWC.Client.World;

/// <summary>
/// Проверка T-035 в сессии (ключ метки <c>caravantest=1</c>): игрок 1 делает то, что делает интерфейс,
/// а оба клиента пишут, что видят. Если действие не уходит командой, оно исполнится только у игрока 1 —
/// сводки разойдутся, сервер заметит расхождение хешей.
/// <list type="number">
/// <item>+300 тиков: «Сформировать караван» — то же, что кнопка «Отправить» окна <see cref="Dialog_FormCaravan"/>
/// (один колонист, маршрут на тайл в двух шагах, где можно основать поселение), без открытия окна (оно уводит камеру на карту мира).</item>
/// <item>Когда караван дошёл до цели: «Основать поселение» — то же, что кнопка каравана (её проверки, затем
/// <see cref="SettleInEmptyTileUtility.Settle"/>).</item>
/// <item>Раз в 500 тиков и в конце — сводка: ходят ли пешки формировать караван, сколько караванов и поселений игрока.</item>
/// </list>
/// </summary>
public static class CaravanSelfTest
{
    private static bool? _enabled;
    private static int _startTick = -1;
    private static bool _formed;
    private static bool _settled;
    private static int _nextSummary;
    private static PlanetTile _destination = PlanetTile.Invalid;
    private static bool _formed2;
    private static bool _camped;
    private static bool _formed3;
    private static bool _rerouted;
    private static int _step2; // второй караван: 0 — идёт, 1 — разделён, 2 — объединён, 3 — лагерь
    private static PlanetTile _destination2 = PlanetTile.Invalid;
    private static int _dumpedMaps = int.MaxValue;

    private static bool? _abandonTest;
    private static bool _abandonSent;

    /// <summary>Ключ метки abandontest=1: проверка удаления брошенной колонии (тестовая команда разрешена).</summary>
    public static bool AbandonTestArmed
    {
        get
        {
            if (_abandonTest == null)
            {
                try
                {
                    string arm = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");
                    _abandonTest = System.IO.File.Exists(arm) && System.IO.File.ReadAllLines(arm).Any(l => l.Trim() == "abandontest=1");
                }
                catch (Exception)
                {
                    _abandonTest = false;
                }
            }
            return _abandonTest.Value;
        }
    }

    private static bool Enabled
    {
        get
        {
            if (_enabled == null)
            {
                try
                {
                    string arm = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");
                    _enabled = System.IO.File.Exists(arm) && System.IO.File.ReadAllLines(arm).Any(l => l.Trim() == "caravantest=1");
                }
                catch (Exception)
                {
                    _enabled = false;
                }
            }
            return _enabled.Value;
        }
    }

    /// <summary>Раз в кадр в загруженной игре (между тиками, как ввод игрока).</summary>
    public static void Frame()
    {
        if (!Enabled || !LockstepClient.InSession || Current.Game == null) return;
        int tick = Find.TickManager.TicksGame;
        if (_startTick < 0)
        {
            _startTick = tick;
            CaptureErrors();
            _dumpedMaps = Find.Maps.Count;
            _nextSummary = tick + 500;
        }
        // Действует игрок 2: игрок 1 — хост, у него первая колония, а основать поселение может только игрок
        // без своей колонии (соавтор — ключ colonychoice=join).
        bool issuer = LockstepClient.PlayerId == 2;

        if (issuer && !_formed && tick >= _startTick + 300)
        {
            _formed = true;
            TestAutomation.Note("caravan: form — " + TryForm(second: false));
        }
        if (issuer && !_formed2 && tick >= _startTick + 600)
        {
            _formed2 = true;
            TestAutomation.Note("caravan: form 2 — " + TryForm(second: true));
        }

        Caravan? caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer && !c.pather.Moving && c.Tile == _destination);
        if (issuer && !_settled && caravan != null)
        {
            _settled = true;
            TestAutomation.Note("caravan: settle — " + TrySettle(caravan));
        }
        if (issuer && !_camped && _destination2.Valid)
        {
            List<Caravan> there = Find.WorldObjects.Caravans.Where(c => c.Faction == Faction.OfPlayer && !c.pather.Moving && c.Tile == _destination2).ToList();
            if (_step2 == 0 && there.Count == 1)
            {
                _step2 = 1;
                // Окно «Разделить» — кнопкой каравана (команда); игрок 1 присоединится к окну и сам нажмёт «Принять».
                TestAutomation.Note("caravan: split window — " + TryPress(there[0], new List<Caravan> { there[0] }, "SplitCaravan"));
            }
            else if (_step2 == 1 && there.Count == 2)
            {
                _step2 = 2;
                TestAutomation.Note("caravan: merge — " + TryPress(there[0], there, "MergeCaravans"));
            }
            else if (_step2 == 2 && there.Count == 1)
            {
                _step2 = 3;
                _camped = true;
                TestAutomation.Note("caravan: camp — " + TryPress(there[0], new List<Caravan> { there[0] }, "CreateCamp"));
            }
        }

        // Третий караван: после лагеря — из дома, затем маршрут к новому поселению с действием «Войти» (T-073).
        if (issuer && _camped && !_formed3 && Find.Maps.Count >= 3)
        {
            _formed3 = true;
            TestAutomation.Note("caravan: reform — " + TryReform());
        }
        if (issuer && _formed3 && !_rerouted)
        {
            Caravan? third = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer);
            if (third != null)
            {
                _rerouted = true;
                TestAutomation.Note("caravan: route with Enter — " + TryRouteEnter(third));
            }
        }

        if (!issuer)
        {
            JoinSharedSplit();
        }

        if (_dumpedMaps < Find.Maps.Count)
        {
            DumpNewMaps(tick);
        }

        // abandontest: хост (игрок 1) объявляет игрока 2 «не в сети 10 лет», когда у того есть своя колония.
        if (AbandonTestArmed && !_abandonSent && LockstepClient.PlayerId == 1 && Colonies.Component?.OwnsColony(2) == true)
        {
            _abandonSent = true;
            RWMLWC.Client.Input.WorldCommands.SendColonyTestAbsent(2);
            TestAutomation.Note("caravan: abandon test — player 2 marked absent");
        }

        if (tick >= _nextSummary)
        {
            _nextSummary = tick + 500;
            TestAutomation.Note("caravan summary: " + Summary());
        }
    }

    /// <summary>
    /// Ошибки игры — ещё и в свой журнал: <c>Player.log</c> у двух копий игры общий, строки затирают друг друга.
    /// </summary>
    private static void CaptureErrors()
    {
        try
        {
            var harmony = new Harmony("rwmlwc.caravantest");
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(AfterError)));
            harmony.Patch(AccessTools.Method(typeof(GetOrGenerateMapUtility), nameof(GetOrGenerateMapUtility.GetOrGenerateMap),
                    new[] { typeof(PlanetTile), typeof(IntVec3), typeof(WorldObjectDef), typeof(IEnumerable<GenStepWithParams>), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(BeforeGenerate)),
                postfix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(AfterGenerate)));
            harmony.Patch(AccessTools.Method(typeof(ThingMaker), nameof(ThingMaker.MakeThing)),
                postfix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(AfterMakeThing)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Value)),
                prefix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(OnRand)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Int)),
                prefix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(OnRand)));
            AppDomain.CurrentDomain.UnhandledException += (_, e) => TestAutomation.Note("caravan: UNHANDLED " + e.ExceptionObject);
        }
        catch (Exception e)
        {
            TestAutomation.Note("caravan: error capture not installed: " + e.Message);
        }
    }

    // Узкая трассировка Rand во время генерации: после появления вещи-метки — следующие TraceCalls вызовов со стеком.
    private const string TraceTrigger = "AncientMechDropBeacon";
    private const int TraceCalls = 4000;
    private static bool _generating;
    private static int _traceLeft;
    private static readonly System.Text.StringBuilder _trace = new();

    private static void BeforeGenerate()
    {
        _generating = true;
        _traceLeft = 0;
        _trace.Clear();
    }

    private static void AfterMakeThing(Thing __result)
    {
        if (_generating && _traceLeft == 0 && _trace.Length == 0 && __result?.def?.defName == TraceTrigger)
        {
            _traceLeft = TraceCalls;
            _trace.Append("# trigger ").Append(TraceTrigger).Append(" id ").Append(__result.thingIDNumber).AppendLine();
        }
    }

    private static void OnRand()
    {
        if (_traceLeft <= 0) return;
        _traceLeft--;
        var st = new System.Diagnostics.StackTrace(2, false);
        int n = Math.Min(st.FrameCount, 9);
        for (int i = 0; i < n; i++)
        {
            System.Reflection.MethodBase? m = st.GetFrame(i)?.GetMethod();
            if (i > 0) _trace.Append(" < ");
            _trace.Append(m?.DeclaringType?.Name).Append('.').Append(m?.Name);
        }
        _trace.AppendLine();
    }

    /// <summary>Сразу после генерации, до входа каравана: только то, что сделал генератор.</summary>
    private static void AfterGenerate(Map __result)
    {
        _generating = false;
        _traceLeft = 0;
        if (__result == null) return;
        try
        {
            if (_trace.Length > 0)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", $"genrand-{__result.uniqueID}.txt"), _trace.ToString());
                _trace.Clear();
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("# generated, tick ").Append(Find.TickManager.TicksGame).Append(", map ").Append(__result.uniqueID)
              .Append(", inTick ").Append(MapRandContext.InTick).Append(", ctx ").Append(MapRandContext.Current?.uniqueID.ToString() ?? "global")
              .Append(", global rand ").Append(MapRandContext.GlobalState.ToString("X16")).AppendLine();
            MapStateHasher.DumpThings(__result, sb);
            string file = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", $"genmap-{__result.uniqueID}.txt");
            System.IO.File.WriteAllText(file, sb.ToString());
            TestAutomation.Note($"caravan: map {__result.uniqueID} generated at tick {Find.TickManager.TicksGame}: {__result.listerThings.AllThings.Count} things, {__result.mapPawns.AllPawnsSpawnedCount} pawns, global rand {MapRandContext.GlobalState:X16}");
        }
        catch (Exception e)
        {
            TestAutomation.Note("caravan: generated map dump failed: " + e.Message);
        }
    }

    private static void AfterError(string text)
    {
        TestAutomation.Note("caravan: ERROR " + text);
    }

    /// <summary>
    /// Вещи новой карты (поселения) — в файл в первом кадре после её появления: сравнив файлы двух клиентов,
    /// видно, чем отличается генерация. Генерация в сессии идёт сразу после тика команды, у всех на одном тике.
    /// </summary>
    private static void DumpNewMaps(int tick)
    {
        try
        {
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
            for (int i = _dumpedMaps; i < Find.Maps.Count; i++)
            {
                Map map = Find.Maps[i];
                var sb = new System.Text.StringBuilder();
                sb.Append("# tick ").Append(tick).Append(", map ").Append(map.uniqueID).Append(", tile ").Append(map.Tile)
                  .Append(", size ").Append(map.Size).Append(", global rand ").Append(MapRandContext.GlobalState.ToString("X16")).AppendLine();
                MapStateHasher.DumpThings(map, sb);
                string file = System.IO.Path.Combine(dir, $"newmap-{map.uniqueID}-tick{tick}.txt");
                System.IO.File.WriteAllText(file, sb.ToString());
                TestAutomation.Note($"caravan: new map {map.uniqueID} dumped at tick {tick}: {map.listerThings.AllThings.Count} things, {map.mapPawns.AllPawnsSpawnedCount} pawns");
            }
        }
        catch (Exception e)
        {
            TestAutomation.Note("caravan: new map dump failed: " + e.Message);
        }
        _dumpedMaps = Find.Maps.Count;
    }

    private static string TryForm(bool second)
    {
        try
        {
            Map map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
            var dialog = new Dialog_FormCaravan(map);
            AccessTools.Method(typeof(Dialog_FormCaravan), "CalculateAndRecacheTransferables").Invoke(dialog, null);
            TransferableOneWay? colonist = dialog.transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed
                && !(p.GetLord()?.LordJob is LordJob_FormAndSendCaravan));
            if (colonist == null) return "нет колониста";
            colonist.AdjustTo(1);
            if (second)
            {
                // Второму каравану — два колониста: его будем делить и объединять.
                TransferableOneWay? other = dialog.transferables.FirstOrDefault(t => t != colonist && t.AnyThing is Pawn p && p.IsColonist && !p.Downed
                    && !(p.GetLord()?.LordJob is LordJob_FormAndSendCaravan));
                other?.AdjustTo(1);
            }
            PlanetTile dest = TileTwoAway(map.Tile, second ? _destination : PlanetTile.Invalid);
            if (!dest.Valid) return "нет тайла для поселения в двух шагах";
            if (second) _destination2 = dest; else _destination = dest;
            dialog.Notify_ChoseRoute(dest);
            bool ok = (bool)AccessTools.Method(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan").Invoke(dialog, null);
            return "отправка " + (ok ? "принята" : "отклонена") + ", колонист " + ((Pawn)colonist.AnyThing).thingIDNumber + ", цель " + dest;
        }
        catch (Exception e)
        {
            return "ошибка: " + (e.InnerException ?? e).Message;
        }
    }

    /// <summary>Тайл в двух шагах от колонии, на котором кнопка «Основать поселение» разрешена (не рядом с поселением).</summary>
    private static PlanetTile TileTwoAway(PlanetTile home, PlanetTile exclude)
    {
        var first = new List<PlanetTile>();
        var second = new List<PlanetTile>();
        Find.WorldGrid.GetTileNeighbors(home, first);
        foreach (PlanetTile n in first)
        {
            second.Clear();
            Find.WorldGrid.GetTileNeighbors(n, second);
            foreach (PlanetTile t in second)
            {
                if (t != home && t != exclude && !first.Contains(t) && !Find.World.Impassable(t) && !Find.WorldObjects.AnyWorldObjectAt(t)
                    && TileFinder.IsValidTileForNewSettlement(t) && Find.WorldReachability.CanReach(home, t))
                {
                    return t;
                }
            }
        }
        return PlanetTile.Invalid;
    }

    /// <summary>«Переформировать караван» на карте лагеря: один колонист, «Отправить» — уходит командой (T-073).</summary>
    private static string TryReform()
    {
        try
        {
            Map? camp = Find.Maps.FirstOrDefault(m => m.Tile == _destination2);
            if (camp == null) return "карты лагеря нет";
            var dialog = new Dialog_FormCaravan(camp, reform: true);
            AccessTools.Method(typeof(Dialog_FormCaravan), "CalculateAndRecacheTransferables").Invoke(dialog, null);
            TransferableOneWay? colonist = dialog.transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            if (colonist == null) return "нет колониста";
            colonist.AdjustTo(1);
            bool ok = (bool)RWMLWC.Client.Input.WorldCommands.ReformMethod.Invoke(dialog, null);
            return $"«Отправить» — {(ok ? "принято" : "отклонено")}, карта {camp.uniqueID}, колонист {((Pawn)colonist.AnyThing).thingIDNumber}";
        }
        catch (Exception e)
        {
            return "ОШИБКА " + e;
        }
    }

    /// <summary>Маршрут к новому поселению игрока с действием «Войти» — как выбор в меню карты мира; уходит командой.</summary>
    private static string TryRouteEnter(Caravan caravan)
    {
        try
        {
            Settlement? home = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == Faction.OfPlayer && x.Tile == _destination);
            if (home == null) return "нового поселения нет";
            caravan.pather.StartPath(home.Tile, new CaravanArrivalAction_Enter(home), repathImmediately: true);
            return $"отправлено: караван {caravan.ID} → поселение {home.ID} на тайле {home.Tile}";
        }
        catch (Exception e)
        {
            return "ОШИБКА " + e;
        }
    }

    private static int _joinStep;
    private static int _joinFrame;

    /// <summary>
    /// Игрок 1: присоединиться к общему окну «Разделить караван», открытому игроком 2, выбрать в нём колониста
    /// (количество уходит командой) и нажать «Принять» (тоже командой) — проверка общих окон.
    /// </summary>
    private static void JoinSharedSplit()
    {
        RWMLWC.Client.Input.SharedWindows.Entry? e = RWMLWC.Client.Input.SharedWindows.Open
            .FirstOrDefault(x => x.window is Dialog_SplitCaravan && x.author != LockstepClient.PlayerId);
        if (e == null)
        {
            return;
        }
        if (_joinStep == 0)
        {
            RWMLWC.Client.Input.SharedWindows.Join(e);
            _joinStep = 1;
            _joinFrame = Time.frameCount;
            TestAutomation.Note("caravan: joined shared split window #" + e.author + "." + e.serial);
        }
        else if (_joinStep == 1 && Time.frameCount > _joinFrame + 30)
        {
            TransferableOneWay? colonist = e.Transferables?.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            colonist?.AdjustTo(1);
            _joinStep = 2;
            _joinFrame = Time.frameCount;
            TestAutomation.Note("caravan: joiner set colonist " + (colonist?.AnyThing?.thingIDNumber ?? -1) + " to 1");
        }
        else if (_joinStep == 2 && Time.frameCount > _joinFrame + 120)
        {
            bool ok = (bool)RWMLWC.Client.Input.WorldCommands.SplitMethod.Invoke(e.window, null);
            _joinStep = 3;
            TestAutomation.Note("caravan: joiner pressed Accept — " + (ok ? "sent" : "rejected"));
        }
    }

    /// <summary>«Разделить караван»: окно, один колонист в новый караван, «Принять» — уходит командой (T-073).</summary>
    private static string TrySplit(Caravan caravan)
    {
        try
        {
            var dialog = new Dialog_SplitCaravan(caravan);
            AccessTools.Method(typeof(Dialog_SplitCaravan), "CalculateAndRecacheTransferables").Invoke(dialog, null);
            var transferables = (List<TransferableOneWay>)AccessTools.Field(typeof(Dialog_SplitCaravan), "transferables").GetValue(dialog);
            TransferableOneWay? colonist = transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            if (colonist == null) return "нет колониста";
            colonist.AdjustTo(1);
            bool ok = (bool)RWMLWC.Client.Input.WorldCommands.SplitMethod.Invoke(dialog, null);
            return $"«Принять» — {(ok ? "принято" : "отклонено")}, караван {caravan.ID} ({caravan.PawnsListForReading.Count} пешек), колонист {((Pawn)colonist.AnyThing).thingIDNumber}";
        }
        catch (Exception e)
        {
            return "ОШИБКА " + e;
        }
    }

    /// <summary>
    /// Нажать кнопку каравана с значком <paramref name="iconName"/> при выделении <paramref name="selection"/> на карте мира —
    /// общим механизмом кнопок объектов мира (T-073): уходит команда с номером кнопки, как при настоящем щелчке.
    /// </summary>
    private static string TryPress(Caravan caravan, List<Caravan> selection, string iconName)
    {
        List<WorldObject> selected = Find.WorldSelector.SelectedObjects;
        var saved = new List<WorldObject>(selected);
        try
        {
            selected.Clear();
            selected.AddRange(selection);
            int index = 0;
            foreach (Gizmo g in caravan.GetGizmos())
            {
                if (g is Command c && (c.icon as UnityEngine.Texture2D)?.name == iconName)
                {
                    if (c.Disabled) return "кнопка выключена: " + c.disabledReason;
                    RWMLWC.Client.Input.WorldCommands.CaptureWorldGizmo(caravan, index, RWMLWC.Client.Input.GizmoCommands.Signature(g));
                    return $"кнопка {index} ({c.defaultLabel}) отправлена, караван {caravan.ID}, выделено {selection.Count}";
                }
                index++;
            }
            return $"кнопки {iconName} нет";
        }
        catch (Exception e)
        {
            return "ОШИБКА " + e;
        }
        finally
        {
            selected.Clear();
            selected.AddRange(saved);
        }
    }

    /// <summary>То же, что кнопка каравана: её проверки, затем <see cref="SettleInEmptyTileUtility.Settle"/>.</summary>
    private static string TrySettle(Caravan caravan)
    {
        try
        {
            var reason = new System.Text.StringBuilder();
            if (!TileFinder.IsValidTileForNewSettlement(caravan.Tile, reason)) return "кнопка выключена: " + reason;
            // В сессии: одна своя колония на игрока (Colonies).
            if (SettleUtility.PlayerSettlementsCountLimitReached) return "кнопка выключена: у игрока уже есть своя колония";
            SettleInEmptyTileUtility.Settle(caravan);
            return "вызвано на тайле " + caravan.Tile;
        }
        catch (Exception e)
        {
            return "ОШИБКА " + e;
        }
    }

    private static string Summary()
    {
        int forming = Find.Maps.Sum(m => m.lordManager.lords.Count(l => l.LordJob is LordJob_FormAndSendCaravan));
        int caravans = Find.WorldObjects.Caravans.Count(c => c.Faction == Faction.OfPlayer);
        int settlements = Find.WorldObjects.Settlements.Count(s => s.Faction == Faction.OfPlayer);
        int maps = Find.Maps.Count;
        Caravan? c = Find.WorldObjects.Caravans.FirstOrDefault(x => x.Faction == Faction.OfPlayer);
        string colonists = string.Join("/", Find.Maps.Select(m => m.mapPawns.FreeColonistsSpawnedCount));
        string where = c == null ? "" : $", caravan at {c.Tile}{(c.pather.Moving ? " moving to " + c.pather.Destination : "")}";
        return $"tick {Find.TickManager.TicksGame}, forming lords {forming}, caravans {caravans}, player settlements {settlements}, maps {maps}, colonists by map {colonists}{where}, colonies {Colonies.Component?.Summary()}, adapt {SettlementThreats.Component?.Summary()}, factors {SettlementThreats.Component?.Factors()} (game: d{GenDate.DaysPassedSinceSettle}×a{Find.StoryWatcher.watcherAdaptation.AdaptDays:F2})";
    }
}
