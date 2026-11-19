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

    private static readonly Dictionary<string, bool> _armKeys = new Dictionary<string, bool>();

    /// <summary>Есть ли строка в файле-метке тестового прогона.</summary>
    public static bool ArmKey(string line)
    {
        if (!_armKeys.TryGetValue(line, out bool has))
        {
            try
            {
                string arm = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");
                has = System.IO.File.Exists(arm) && System.IO.File.ReadAllLines(arm).Any(l => l.Trim() == line);
            }
            catch (Exception)
            {
                has = false;
            }
            _armKeys[line] = has;
        }
        return has;
    }

    private static int _stasisStep;
    private static bool _killSent;
    private static int _tradeStep;
    private static int _tradeTick;
    private static int _clockStep;
    private static int _clockTick;
    private static Game? _tracedGame;
    private static int _bannerStep;
    private static Map? _bannerBack;
    private static float _bannerTime;
    private static int _stasisTick;

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
        // Общее время, не часы карты под камерой (камера может стоять на карте в стазисе).
        int tick = Find.TickManager.TicksGame + RWMLWC.Client.Sim.StasisClock.Offset;
        if (_startTick < 0)
        {
            _startTick = tick;
            CaptureErrors();
            _dumpedMaps = Find.Maps.Count;
            _nextSummary = tick + 500;
        }
        // Действует игрок 2: игрок 1 — хост, у него первая колония, а основать поселение может только игрок
        // без своей колонии (соавтор — ключ colonychoice=join).
        // viewmap=N (T-076): оба клиента всё время смотрят на карту N — проверка, зависит ли расхождение от того,
        // какую карту рисует клиент.
        foreach (string key in new[] { "viewmap=0", "viewmap=1" })
        {
            if (ArmKey(key))
            {
                int want = key[key.Length - 1] - '0';
                Map? m = Find.Maps.FirstOrDefault(x => x.uniqueID == want);
                if (m != null && Find.CurrentMap != m && !WorldRendererUtility.WorldSelected)
                {
                    Current.Game.CurrentMap = m;
                }
            }
        }

        // tracepostload: после каждой загрузки снимка — трассировка Rand со стеком на 400 тиков (оба клиента
        // стартуют с одного снимка, первая отличающаяся строка — код, который ведёт себя по-разному).
        if (!ReferenceEquals(_tracedGame, Current.Game))
        {
            _tracedGame = Current.Game;
            if (ArmKey("tracepostload=1") && tick > 20000)
            {
                string file = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", $"randtrace-post-{tick}.txt");
                RandTracer.Start(file, tick, tick + 400);
                TestAutomation.Note($"caravan: rand trace after snapshot load, ticks {tick}..{tick + 400}");
            }
            if (ArmKey("dumppostload=1") && tick > 20000)
            {
                _dumpUntil = tick + 300;
                TestAutomation.Note($"caravan: thing dumps after snapshot load, ticks {tick}..{_dumpUntil}");
            }
        }

        bool issuer = LockstepClient.PlayerId == 2 && !ArmKey("adapttest=1") && !ArmKey("exchangetest=1"); // adapttest, exchangetest — свои шаги

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

        // adapttest: хост, когда у обоих свои колонии, — гибель колониста в колонии хоста (карта 0).
        if (ArmKey("adapttest=1") && !_killSent && LockstepClient.PlayerId == 1 && Colonies.Component?.OwnsColony(2) == true
            && Colonies.Component.OwnedBy(1)?.Map is Map hostMap)
        {
            _killSent = true;
            RWMLWC.Client.Input.WorldCommands.SendTestKill(hostMap.uniqueID);
        }

        // Проверка надписи стазиса: один раз показать замершую карту (только интерфейс), через 3 с вернуться.
        if (ArmKey("adapttest=1") && _bannerStep == 0 && Find.Maps.FirstOrDefault(m => PerMapSim.IsFrozen(m)) is Map frozenMap && Find.CurrentMap != frozenMap)
        {
            _bannerStep = 1;
            _bannerBack = Find.CurrentMap;
            _bannerTime = Time.realtimeSinceStartup;
            Current.Game.CurrentMap = frozenMap;
            TestAutomation.Note("caravan: viewing frozen map " + frozenMap.uniqueID);
        }
        else if (_bannerStep == 1 && Time.realtimeSinceStartup > _bannerTime + 3f)
        {
            _bannerStep = 2;
            if (_bannerBack != null && Find.Maps.Contains(_bannerBack)) Current.Game.CurrentMap = _bannerBack;
        }

        // clocktest: после разморозки — колонист с размороженной карты на другую, через 600 тиков — обратно.
        if (ArmKey("clocktest=1") && LockstepClient.PlayerId == 1 && _clockStep < 2)
        {
            Map? thawed = Find.Maps.FirstOrDefault(m => !PerMapSim.IsFrozen(m) && (MapSimState.For(m)?.stasisTicks ?? 0) > 0);
            Map? other = thawed == null ? null : Find.Maps.FirstOrDefault(m => m != thawed && m.IsPlayerHome);
            if (thawed != null && other != null)
            {
                if (_clockStep == 0)
                {
                    _clockStep = 1;
                    _clockTick = tick;
                    RWMLWC.Client.Input.WorldCommands.SendTestTransfer(thawed.uniqueID, other.uniqueID);
                }
                else if (_clockStep == 1 && tick >= _clockTick + 600)
                {
                    _clockStep = 2;
                    RWMLWC.Client.Input.WorldCommands.SendTestTransfer(other.uniqueID, thawed.uniqueID);
                }
            }
        }

        // tradetest: игрок 2 — караван торговцев на карту хоста (карта 0), колонист идёт торговать, в окне — купить одну вещь.
        if (ArmKey("tradetest=1") && LockstepClient.PlayerId == 2)
        {
            if (_tradeStep == 0 && tick >= _startTick + 200)
            {
                _tradeStep = 1;
                _tradeTick = tick;
                RWMLWC.Client.Input.WorldCommands.SendTestOp(RWMLWC.Client.Input.WorldCommands.TestTraderOp, 0, 0);
            }
            else if (_tradeStep == 1 && tick >= _tradeTick + 2500)
            {
                _tradeStep = 2;
                RWMLWC.Client.Input.WorldCommands.SendTestOp(RWMLWC.Client.Input.WorldCommands.TestGoTradeOp, 0, 0);
            }
            else if (_tradeStep == 2 && TradeSession.Active && Find.WindowStack.IsOpen<Dialog_Trade>())
            {
                _tradeStep = 3;
                // Продать торговцу одну вещь колонии (серебро не нужно): направление — то, при котором действие «игрок продаёт».
                Tradeable? buy = null;
                foreach (Tradeable t in TradeSession.deal.AllTradeables.Where(t => !t.IsCurrency && t.CountHeldBy(Transactor.Colony) > 0 && t.TraderWillTrade)
                    .OrderBy(t => t.AnyThing.thingIDNumber))
                {
                    foreach (int d in new[] { 1, -1 })
                    {
                        if (t.CanAdjustBy(d).Accepted)
                        {
                            t.AdjustBy(d);
                            if (t.ActionToDo == TradeAction.PlayerSells) { buy = t; break; }
                            t.AdjustBy(-d);
                        }
                    }
                    if (buy != null) break;
                }
                bool ok = TradeSession.deal.TryExecute(out bool traded);
                Find.WindowStack.TryRemove(typeof(Dialog_Trade));
                var all = TradeSession.deal.AllTradeables; TestAutomation.Note($"caravan: trade lines {all.Count}, colony>0 {all.Count(t => t.CountHeldBy(Transactor.Colony) > 0)}, trader>0 {all.Count(t => t.CountHeldBy(Transactor.Trader) > 0)}, willTrade {all.Count(t => t.TraderWillTrade)}, currency {all.Count(t => t.IsCurrency)}"); TestAutomation.Note($"caravan: trade window — sold {buy?.ThingDef?.defName ?? "nothing"} x1 (count {buy?.CountToTransfer}), accept {ok}/{traded}");
            }
        }

        // exchangetest (T-036): хост — караван «колонист + стопка вещей» в колонию игрока 2 с действием «Войти».
        if (ArmKey("exchangetest=1"))
        {
            ExchangeFrame(tick);
        }

        // stasistest: хост объявляет игрока 2 (у него своя колония) «не в сети», через 6000 тиков — «снова в сети».
        if (ArmKey("stasistest=1") && LockstepClient.PlayerId == 1 && Colonies.Component?.OwnsColony(2) == true)
        {
            if (_stasisStep == 0)
            {
                _stasisStep = 1;
                _stasisTick = tick;
                RWMLWC.Client.Input.WorldCommands.SendTestPresence(2, false);
            }
            else if (_stasisStep == 1 && tick >= _stasisTick + 6000)
            {
                _stasisStep = 2;
                RWMLWC.Client.Input.WorldCommands.SendTestPresence(2, true);
            }
        }

        if (tick >= _nextSummary)
        {
            _nextSummary = tick + 500;
            TestAutomation.Note("caravan summary: " + Summary()); SimReadsProbe.Write();
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
            harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                prefix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(BeforeTick)),
                postfix: new HarmonyMethod(typeof(CaravanSelfTest), nameof(AfterTick)));
            if (ArmKey("noclockpatch=1"))
            {
                // Замер: без поправки локальных часов в TicksGame (сравнение нагрузки).
                RwmlwcMod.HarmonyInstance.Unpatch(AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TicksGame)), HarmonyPatchType.Postfix, RwmlwcMod.HarmonyInstance.Id);
                TestAutomation.Note("caravan: TicksGame postfix removed for the perf comparison");
            }
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

    // Замер нагрузки: время одного тика игры (DoSingleTick), сумма за отрезок между сводками.
    private static readonly System.Diagnostics.Stopwatch _tickWatch = new System.Diagnostics.Stopwatch();
    private static long _tickTicks;
    private static int _tickCount;

    private static void BeforeTick() => _tickWatch.Restart();

    private static int _dumpUntil = -1;

    // ringdump: каждые 10 тиков — компактный список вещей карты 0 в памяти (последние 30), при сообщении сервера
    // о расхождении — в файл. Почти не замедляет тики (поиск T-076 без смены темпа).
    private static readonly Queue<(int tick, int[] data)> _ring = new Queue<(int, int[])>();

    private static void RingCapture(int t)
    {
        foreach (Map m in Find.Maps)
        {
            RingCaptureMap(t, m);
        }
        while (_ring.Count > 30 * Math.Max(1, Find.Maps.Count))
        {
            _ring.Dequeue();
        }
    }

    private static void RingCaptureMap(int t, Map map)
    {
        List<Thing> things = map.listerThings.AllThings;
        int[] data = new int[things.Count * 6 + 6];
        // Первая строка — состояние Rand карты и глобальное (что разошлось первым: числа или вещи).
        ulong rs = MapSimState.For(map)?.randState ?? 0;
        ulong gs = MapRandContext.GlobalState;
        data[things.Count * 6] = -1;
        data[things.Count * 6 + 1] = (int)(rs & 0xFFFFFFFF);
        data[things.Count * 6 + 2] = (int)(rs >> 32);
        data[things.Count * 6 + 3] = (int)(gs & 0xFFFFFFFF);
        data[things.Count * 6 + 4] = (int)(gs >> 32);
        data[things.Count * 6 + 5] = 0;
        for (int i = 0; i < things.Count; i++)
        {
            Thing th = things[i];
            data[i * 6] = th.thingIDNumber;
            data[i * 6 + 1] = th.def.shortHash;
            data[i * 6 + 2] = th.Position.x;
            data[i * 6 + 3] = th.Position.z;
            data[i * 6 + 4] = th.stackCount;
            data[i * 6 + 5] = th.HitPoints;
        }
        _ring.Enqueue((t * 16 + map.uniqueID, data)); // тик и номер карты в одном числе
    }

    /// <summary>Сервер сообщил о расхождении: записать буфер вещей (ключ ringdump=1).</summary>
    public static void OnDesyncNotice()
    {
        if (!ArmKey("ringdump=1") || _ring.Count == 0)
        {
            return;
        }
        try
        {
            string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC");
            foreach ((int tick, int[] data) in _ring)
            {
                var sb = new System.Text.StringBuilder();
                var rows = new List<string>(data.Length / 6);
                for (int i = 0; i < data.Length; i += 6)
                {
                    rows.Add($"{data[i]};{data[i + 1]};{data[i + 2]};{data[i + 3]};{data[i + 4]};{data[i + 5]}");
                }
                rows.Sort(StringComparer.Ordinal);
                foreach (string r in rows) sb.AppendLine(r);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"ring-map{tick % 16}-{tick / 16}.txt"), sb.ToString());
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"fine-{Find.TickManager.TicksGame}.txt"), string.Concat(_fine));
            _fine.Clear();
            TestAutomation.Note($"caravan: ring dump written, {_ring.Count} snapshots, ticks {_ring.Peek().tick}..");
            _ring.Clear();
        }
        catch (Exception e)
        {
            TestAutomation.Note("caravan: ring dump failed: " + e.Message);
        }
    }

    // Тонкий буфер каждого тика: Rand карт, пешки (клетка), грязь (клетка, толщина) — последние 200 тиков.
    private static readonly Queue<string> _fine = new Queue<string>();

    private static void FineCapture(int t)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Map m in Find.Maps)
        {
            sb.Append("T").Append(t).Append(" map").Append(m.uniqueID).Append(" rand ").Append((MapSimState.For(m)?.randState ?? 0).ToString("X16")).Append('|');
            foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
            {
                sb.Append('p').Append(p.thingIDNumber).Append('@').Append(p.Position.x).Append(',').Append(p.Position.z).Append(' ');
            }
            sb.Append('|');
            foreach (Thing f in m.listerThings.ThingsInGroup(ThingRequestGroup.Filth))
            {
                sb.Append('f').Append(f.thingIDNumber).Append('@').Append(f.Position.x).Append(',').Append(f.Position.z)
                  .Append('x').Append((f as Filth)?.thickness ?? 0).Append(' ');
            }
            sb.Append(Environment.NewLine);
        }
        _fine.Enqueue(sb.ToString());
        while (_fine.Count > 200)
        {
            _fine.Dequeue();
        }
    }

    private static void AfterTick()
    {
        int t = Find.TickManager.TicksGame;
        if (ArmKey("ringdump=1") && Find.Maps.Count > 0)
        {
            FineCapture(t);
        }
        if (t % 10 == 0 && Find.Maps.Count > 0 && ArmKey("ringdump=1"))
        {
            RingCapture(t);
        }
        if (t <= _dumpUntil && t % 10 == 0 && Find.Maps.Count > 0)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                MapStateHasher.DumpThings(Find.Maps[0], sb);
                System.IO.File.WriteAllText(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", $"postdump-{t}.txt"), sb.ToString());
            }
            catch (Exception)
            {
            }
        }
        _tickTicks += _tickWatch.ElapsedTicks;
        _tickCount++;
    }

    private static string TakePerf()
    {
        if (_tickCount == 0) return "n/a";
        double us = _tickTicks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / _tickCount;
        _tickTicks = 0;
        _tickCount = 0;
        return us.ToString("F0") + " us/tick";
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
            bool ok = (bool)AccessTools.Method(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan").Invoke(dialog, null)!;
            return "отправка " + (ok ? "принята" : "отклонена") + ", колонист " + ((Pawn)colonist.AnyThing).thingIDNumber + ", цель " + dest;
        }
        catch (Exception e)
        {
            return "ошибка: " + (e.InnerException ?? e).Message;
        }
    }

    /// <summary>Тайл в двух шагах от колонии, на котором кнопка «Основать поселение» разрешена (не рядом с поселением).</summary>
    internal static PlanetTile TileTwoAway(PlanetTile home, PlanetTile exclude)
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
            bool ok = (bool)RWMLWC.Client.Input.WorldCommands.ReformMethod.Invoke(dialog, null)!;
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
        else if (_joinStep == 1 && Time.frameCount > _joinFrame + 30 && ArmKey("pausetest=1"))
        {
            // pausetest: окно открыто у обоих (принудительная пауза) — просим общий снимок, как при лечении
            // расхождения. После загрузки игра должна пойти дальше сама, без ручного снятия паузы.
            _joinStep = 9;
            RWMLWC.Client.Sim.LockstepClient.RequestCommonSnapshot();
            TestAutomation.Note("caravan: pausetest — common snapshot requested while the shared window is open");
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
            bool ok = (bool)RWMLWC.Client.Input.WorldCommands.SplitMethod.Invoke(e.window, null)!;
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
            var transferables = (List<TransferableOneWay>)AccessTools.Field(typeof(Dialog_SplitCaravan), "transferables").GetValue(dialog)!;
            TransferableOneWay? colonist = transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            if (colonist == null) return "нет колониста";
            colonist.AdjustTo(1);
            bool ok = (bool)RWMLWC.Client.Input.WorldCommands.SplitMethod.Invoke(dialog, null)!;
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

    private static int _exchangeStep;
    private static ThingDef? _exchangeDef;
    private static int _exchangeCount;
    private static int _exchangePawn = -1;
    private static int _exchangeDefTick;
    private static int _exchangeFormTick;

    private static void ExchangeFrame(int tick)
    {
        // Отправитель — игрок 1; с ключом exchangefrom=stay — тот, кто остаётся в сети в сценарии rejoin (c1),
        // а уходит получатель. Игроков двое: второй — 3 - мой номер.
        int me = LockstepClient.PlayerId;
        bool amSender = ArmKey("exchangefrom=stay") ? ArmKey("name=c1") : me == 1;
        int sender = amSender ? me : 3 - me;
        ColoniesComponent? colonies = Colonies.Component;
        Settlement? target = colonies?.OwnedBy(3 - sender);
        Map? home = colonies?.OwnedBy(sender)?.Map;
        if (target?.Map == null || home == null)
        {
            return;
        }
        if (_exchangeStep == 0 && amSender && !ArmKey("save=") && tick >= _startTick + 150)
        {
            _exchangeStep = 1;
            _exchangeFormTick = tick + 300;
            RWMLWC.Client.Input.WorldCommands.SendTestOp(RWMLWC.Client.Input.WorldCommands.TestHomeAreaOp, home.uniqueID, 0);
            if (ArmKey("podtest=1"))
            {
                RWMLWC.Client.Input.WorldCommands.SendTestOp(RWMLWC.Client.Input.WorldCommands.TestPodKitOp, home.uniqueID, 0);
            }
        }
        if (_exchangeDef == null && tick >= _exchangeDefTick)
        {
            _exchangeDefTick = tick + 250;
            // Что везём, должны знать оба клиента (для сводки): первая стопка стали или серебра на карте хоста.
            // Только то, что окно «Сформировать караван» предлагает (дом, склад, не в тумане).
            List<Thing> offered = CaravanFormingUtility.AllReachableColonyItems(home).ToList();
            _exchangeDef = new[] { ThingDefOf.Steel, ThingDefOf.Silver, ThingDefOf.WoodLog, ThingDefOf.ComponentIndustrial, ThingDefOf.MedicineIndustrial, ThingDefOf.MealSurvivalPack }
                .FirstOrDefault(d => offered.Any(t => t.def == d));
            TestAutomation.Note($"exchange: cargo {_exchangeDef?.defName ?? "none"}, target settlement {target.ID} map {target.Map.uniqueID}, home map {home.uniqueID}");
        }
        // Вернувшийся после обрыва клиент (вход без сейва, сценарий rejoin) только наблюдает: груз уже отправлен.
        if (!amSender || _exchangeDef == null || ArmKey("save="))
        {
            return;
        }
        if (ArmKey("podtest=1"))
        {
            PodFrame(tick, home, target);
            return;
        }
        if (_exchangeStep == 1 && tick >= _exchangeFormTick)
        {
            _exchangeStep = 2;
            TestAutomation.Note("exchange: form — " + TryFormCargo(home, target.Tile));
        }
        else if (_exchangeStep == 2 && Find.WorldObjects.Caravans.FirstOrDefault(c => c.PawnsListForReading.Any(p => p.thingIDNumber == _exchangePawn)) is Caravan c)
        {
            _exchangeStep = 3;
            try
            {
                c.pather.StartPath(target.Tile, new CaravanArrivalAction_Enter(target), repathImmediately: true);
                TestAutomation.Note($"exchange: caravan {c.ID} ({CaravanInventoryUtility.AllInventoryItems(c).Where(t => t.def == _exchangeDef).Sum(t => t.stackCount)} {_exchangeDef.defName}) → settlement {target.ID}, Enter");
            }
            catch (Exception e)
            {
                TestAutomation.Note("exchange: route ERROR " + e);
            }
        }
    }

    private static int _podLogTick;

    /// <summary>podtest (T-053): тот же груз, но капсулой — окно загрузки, погрузка, запуск с посадкой в колонию игрока 2.</summary>
    private static void PodFrame(int tick, Map home, Settlement target)
    {
        CompTransporter? pod = home.listerThings.ThingsOfDef(ThingDefOf.TransportPod).Select(t => t.TryGetComp<CompTransporter>()).FirstOrDefault(c => c != null);
        if (_exchangeStep == 1 && tick >= _exchangeFormTick && pod != null)
        {
            _exchangeStep = 2;
            TestAutomation.Note("pods: load — " + TryLoadPod(home, pod));
        }
        // С exchangefrom=stay запуск — когда получатель уже не в сети (посадка на замороженную карту).
        else if (_exchangeStep == 2 && pod != null && pod.LoadingInProgressOrReadyToLaunch && !pod.AnythingLeftToLoad
            && (!ArmKey("exchangefrom=stay") || !RWMLWC.Client.Maps.PlayerPresence.IsOnline(Colonies.Component!.OwnerOf(target))))
        {
            _exchangeStep = 3;
            try
            {
                int inside = pod.innerContainer.Where(t => t.def == _exchangeDef).Sum(t => t.stackCount);
                pod.Launchable.TryLaunch(target.Tile, new TransportersArrivalAction_LandInSpecificCell(target, target.Map!.Center));
                TestAutomation.Note($"pods: launch pressed, pod {pod.parent.thingIDNumber} ({inside} {_exchangeDef!.defName}, pawns {pod.innerContainer.OfType<Pawn>().Count()}) → settlement {target.ID}");
            }
            catch (Exception e)
            {
                TestAutomation.Note("pods: launch ERROR " + e);
            }
        }
        else if (_exchangeStep == 2 && pod != null && tick >= _podLogTick)
        {
            _podLogTick = tick + 1000;
            Pawn? any = home.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            string why = string.Join("; ", (pod.leftToLoad ?? new List<TransferableOneWay>()).SelectMany(t => t.things).Take(4).Select(t =>
                $"{t.LabelShort} spawned {t.Spawned} at {t.PositionHeld} forbidden {t.IsForbidden(Faction.OfPlayer)} reach {(any != null && t.Spawned ? any.CanReach(t, Verse.AI.PathEndMode.Touch, Danger.Deadly) : false)}"));
            string jobs = string.Join(", ", home.mapPawns.FreeColonistsSpawned.Select(p => p.thingIDNumber + ":" + (p.CurJobDef?.defName ?? "-") + (p.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) == true ? "" : "(no haul)")));
            TestAutomation.Note($"pods: loading, left {pod.leftToLoad?.Sum(t => t.CountToTransfer) ?? -1}, inside {pod.innerContainer.Count}; {why}; colonists {jobs}");
        }
    }

    private static string TryLoadPod(Map map, CompTransporter pod)
    {
        try
        {
            var dialog = new Dialog_LoadTransporters(map, new List<CompTransporter> { pod });
            AccessTools.Method(typeof(Dialog_LoadTransporters), "CalculateAndRecacheTransferables").Invoke(dialog, null);
            var transferables = (List<TransferableOneWay>)AccessTools.Field(typeof(Dialog_LoadTransporters), "transferables").GetValue(dialog)!;
            TransferableOneWay? colonist = transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            TransferableOneWay? cargo = transferables.FirstOrDefault(t => t.ThingDef == _exchangeDef);
            if (colonist == null || cargo == null) return $"нет колониста или груза ({colonist != null}/{cargo != null})";
            colonist.AdjustTo(1);
            _exchangePawn = colonist.AnyThing.thingIDNumber;
            _exchangeCount = Math.Min(20, cargo.MaxCount);
            cargo.AdjustTo(_exchangeCount);
            bool ok = (bool)TransportPods.DialogTryAccept.Invoke(dialog, null)!;
            return $"погрузка {(ok ? "принята" : "отклонена")}, капсула {pod.parent.thingIDNumber}, колонист {_exchangePawn}, груз {_exchangeDef!.defName} x{_exchangeCount}";
        }
        catch (Exception e)
        {
            return "ошибка: " + (e.InnerException ?? e).Message;
        }
    }

    private static string TryFormCargo(Map map, PlanetTile dest)
    {
        try
        {
            var dialog = new Dialog_FormCaravan(map);
            AccessTools.Method(typeof(Dialog_FormCaravan), "CalculateAndRecacheTransferables").Invoke(dialog, null);
            TransferableOneWay? colonist = dialog.transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            TransferableOneWay? cargo = dialog.transferables.FirstOrDefault(t => t.ThingDef == _exchangeDef);
            if (colonist == null || cargo == null) return $"нет колониста или груза ({colonist != null}/{cargo != null})";
            colonist.AdjustTo(1);
            _exchangePawn = colonist.AnyThing.thingIDNumber;
            _exchangeCount = Math.Min(20, cargo.MaxCount);
            cargo.AdjustTo(_exchangeCount);
            dialog.Notify_ChoseRoute(dest);
            bool ok = (bool)AccessTools.Method(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan").Invoke(dialog, null)!;
            return $"отправка {(ok ? "принята" : "отклонена")}, колонист {((Pawn)colonist.AnyThing).thingIDNumber}, груз {_exchangeDef!.defName} x{_exchangeCount}, цель {dest}";
        }
        catch (Exception e)
        {
            return "ошибка: " + (e.InnerException ?? e).Message;
        }
    }

    /// <summary>Сколько вещей груза на каждой карте (с инвентарями пешек) и в караванах — сумма не должна меняться.</summary>
    private static string ExchangeCounts()
    {
        if (_exchangeDef == null) return "";
        var list = new List<Thing>();
        var parts = new List<string>();
        int total = 0;
        foreach (Map m in Find.Maps)
        {
            list.Clear();
            ThingOwnerUtility.GetAllThingsRecursively(m, ThingRequest.ForDef(_exchangeDef), list, allowUnreal: false); // без товара кораблей на орбите
            int n = list.Sum(t => t.stackCount);
            total += n;
            int onGround = m.listerThings.ThingsOfDef(_exchangeDef).Sum(t => t.stackCount);
            parts.Add(m.uniqueID + ":" + n + "(ground " + onGround + ")");
        }
        int inCaravans = Find.WorldObjects.Caravans.Sum(c => CaravanInventoryUtility.AllInventoryItems(c).Where(t => t.def == _exchangeDef).Sum(t => t.stackCount));
        total += inCaravans;
        int inFlight = Find.WorldObjects.AllWorldObjects.OfType<TravellingTransporters>()
            .Sum(o => { var l = new List<Thing>(); ThingOwnerUtility.GetAllThingsRecursively(o, l, allowUnreal: false); return l.Where(t => t.def == _exchangeDef).Sum(t => t.stackCount); });
        total += inFlight;
        return $", cargo {_exchangeDef.defName} [{string.Join(",", parts)}] caravans {inCaravans} pods {inFlight} total {total}";
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
        return $"tick {Find.TickManager.TicksGame}, forming lords {forming}, caravans {caravans}, player settlements {settlements}, maps {maps}, colonists by map {colonists}{where}, colonies {Colonies.Component?.Summary()}, adapt {SettlementThreats.Component?.Summary()}, factors {SettlementThreats.Component?.Factors()}, perf {TakePerf()}, stasis [{string.Join(",", Find.Maps.Select(m => m.uniqueID + ":" + (MapSimState.For(m)?.frozen == true ? "F" : "-") + (MapSimState.For(m)?.stasisTicks ?? 0)))}] (game: d{GenDate.DaysPassedSinceSettle}×a{Find.StoryWatcher.watcherAdaptation.AdaptDays:F2}){ExchangeCounts()}, guests [{RWMLWC.Client.Maps.GuestsComponent.Component?.Digest()}]";
    }
}
