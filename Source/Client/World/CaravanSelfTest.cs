using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Sim;
using Verse;

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
            _nextSummary = tick + 500;
        }
        bool issuer = LockstepClient.PlayerId == 1;

        if (issuer && !_formed && tick >= _startTick + 300)
        {
            _formed = true;
            TestAutomation.Note("caravan: form — " + TryForm());
        }

        Caravan? caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer);
        if (issuer && !_settled && caravan != null && !caravan.pather.Moving && caravan.Tile == _destination)
        {
            _settled = true;
            TestAutomation.Note("caravan: settle — " + TrySettle(caravan));
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
            AppDomain.CurrentDomain.UnhandledException += (_, e) => TestAutomation.Note("caravan: UNHANDLED " + e.ExceptionObject);
        }
        catch (Exception e)
        {
            TestAutomation.Note("caravan: error capture not installed: " + e.Message);
        }
    }

    private static void AfterError(string text)
    {
        TestAutomation.Note("caravan: ERROR " + text);
    }

    private static string TryForm()
    {
        try
        {
            Map map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
            var dialog = new Dialog_FormCaravan(map);
            AccessTools.Method(typeof(Dialog_FormCaravan), "CalculateAndRecacheTransferables").Invoke(dialog, null);
            TransferableOneWay? colonist = dialog.transferables.FirstOrDefault(t => t.AnyThing is Pawn p && p.IsColonist && !p.Downed);
            if (colonist == null) return "нет колониста";
            colonist.AdjustTo(1);
            PlanetTile dest = TileTwoAway(map.Tile);
            if (!dest.Valid) return "нет тайла для поселения в двух шагах";
            _destination = dest;
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
    private static PlanetTile TileTwoAway(PlanetTile home)
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
                if (t != home && !first.Contains(t) && !Find.World.Impassable(t) && !Find.WorldObjects.AnyWorldObjectAt(t)
                    && TileFinder.IsValidTileForNewSettlement(t) && Find.WorldReachability.CanReach(home, t))
                {
                    return t;
                }
            }
        }
        return PlanetTile.Invalid;
    }

    /// <summary>То же, что кнопка каравана: её проверки, затем <see cref="SettleInEmptyTileUtility.Settle"/>.</summary>
    private static string TrySettle(Caravan caravan)
    {
        try
        {
            var reason = new System.Text.StringBuilder();
            if (!TileFinder.IsValidTileForNewSettlement(caravan.Tile, reason)) return "кнопка выключена: " + reason;
            string limit = "";
            if (SettleUtility.PlayerSettlementsCountLimitReached)
            {
                // Как игрок в настройках игры: по умолчанию одно поселение, кнопка второго выключена.
                limit = $" (лимит поселений {Prefs.MaxNumberOfPlayerSettlements} достигнут — кнопка была бы выключена; поднят до 5 локально)";
                Prefs.MaxNumberOfPlayerSettlements = 5;
            }
            SettleInEmptyTileUtility.Settle(caravan);
            return "вызвано на тайле " + caravan.Tile + limit;
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
        string where = c == null ? "" : $", caravan at {c.Tile}{(c.pather.Moving ? " moving to " + c.pather.Destination : "")}";
        return $"tick {Find.TickManager.TicksGame}, forming lords {forming}, caravans {caravans}, player settlements {settlements}, maps {maps}{where}";
    }
}
