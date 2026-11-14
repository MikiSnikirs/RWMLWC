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
/// (один колонист, маршрут на соседний тайл), без открытия окна (оно уводит камеру на карту мира).</item>
/// <item>Когда караван игрока появился на карте мира: «Основать поселение» — то же, что кнопка каравана
/// (<see cref="SettleInEmptyTileUtility.Settle"/> после подтверждения).</item>
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
            _nextSummary = tick + 500;
        }
        bool issuer = LockstepClient.PlayerId == 1;

        if (issuer && !_formed && tick >= _startTick + 300)
        {
            _formed = true;
            TestAutomation.Note("caravan: form — " + TryForm());
        }

        Caravan? caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer);
        if (issuer && !_settled && caravan != null)
        {
            _settled = true;
            try
            {
                SettleInEmptyTileUtility.Settle(caravan);
                TestAutomation.Note("caravan: settle called on tile " + caravan.Tile);
            }
            catch (Exception e)
            {
                TestAutomation.Note("caravan: settle FAILED: " + e.Message);
            }
        }

        if (tick >= _nextSummary)
        {
            _nextSummary = tick + 500;
            TestAutomation.Note("caravan summary: " + Summary());
        }
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
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
            PlanetTile dest = neighbors.FirstOrDefault(t => !Find.World.Impassable(t));
            if (!dest.Valid) return "нет проходимого соседнего тайла";
            dialog.Notify_ChoseRoute(dest);
            bool ok = (bool)AccessTools.Method(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan").Invoke(dialog, null);
            return "отправка " + (ok ? "принята" : "отклонена") + ", колонист " + ((Pawn)colonist.AnyThing).thingIDNumber;
        }
        catch (Exception e)
        {
            return "ошибка: " + (e.InnerException ?? e).Message;
        }
    }

    private static string Summary()
    {
        int forming = Find.Maps.Sum(m => m.lordManager.lords.Count(l => l.LordJob is LordJob_FormAndSendCaravan));
        int caravans = Find.WorldObjects.Caravans.Count(c => c.Faction == Faction.OfPlayer);
        int settlements = Find.WorldObjects.Settlements.Count(s => s.Faction == Faction.OfPlayer);
        int maps = Find.Maps.Count;
        return $"tick {Find.TickManager.TicksGame}, forming lords {forming}, caravans {caravans}, player settlements {settlements}, maps {maps}";
    }
}
