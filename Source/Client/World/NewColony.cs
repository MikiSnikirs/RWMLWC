using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Своя колония нового игрока (решение владельца 15.11): новая стартовая группа, как при новой игре, — колонисты
/// по сценарию мира и стартовые вещи и животные сценария — появляется караваном на выбранной клетке и сразу
/// основывает поселение тем же путём, что кнопка «Основать» (<see cref="SettleInEmptyTileUtility.Settle"/>).
/// Вызывается при исполнении команды — у всех одинаково (Rand — глобальный, зерно общее).
/// </summary>
public static class NewColony
{
    public const int DefaultPawnCount = 3;

    public static void Found(PlanetTile tile, int player)
    {
        int count = StartingPawnCount();
        List<Pawn> pawns = new List<Pawn>(count);
        PawnKindDef kind = Faction.OfPlayer.def.basicMemberKind;
        for (int i = 0; i < count; i++)
        {
            PawnGenerationRequest request = new PawnGenerationRequest(kind, Faction.OfPlayer, PawnGenerationContext.PlayerStarter,
                forceGenerateNewPawn: true, colonistRelationChanceFactor: 20f, allowPregnant: true,
                forcedXenotype: ModsConfig.BiotechActive ? XenotypeDefOf.Baseliner : null, developmentalStages: DevelopmentalStage.Adult);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.relations.everSeenByPlayer = true;
            PawnComponentsUtility.AddComponentsForSpawn(pawn);
            pawns.Add(pawn);
        }
        Caravan caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
        int items = 0;
        // Части сценария (стартовое животное и др.) читают данные экрана новой игры — в идущей игре их нет.
        // На время сбора стартовых вещей подставляем их: новые колонисты и их число.
        GameInitData? savedInit = Current.Game.InitData;
        Current.Game.InitData = new GameInitData { startingAndOptionalPawns = new List<Pawn>(pawns), startingPawnCount = pawns.Count, playerFaction = Faction.OfPlayer, startingTile = tile };
        try
        {
            foreach (ScenPart part in Find.Scenario.AllParts)
            {
                foreach (Thing thing in part.PlayerStartingThings())
                {
                    if (thing is Pawn animal)
                    {
                        caravan.AddPawn(animal, addCarriedPawnToWorldPawnsIfAny: true);
                    }
                    else
                    {
                        CaravanInventoryUtility.GiveThing(caravan, thing);
                        items++;
                    }
                }
            }
        }
        finally
        {
            Current.Game.InitData = savedInit;
        }
        TestAutomation.Note($"colonies: new colony for player {player} at {tile}: {count} colonists, {items} item stacks");
        // Основание — как кнопкой: поселение станет колонией автора команды (Patch_SettleUtility_AddNewHome_Owner).
        SettleInEmptyTileUtility.Settle(caravan);
    }

    private static int StartingPawnCount()
    {
        foreach (ScenPart part in Find.Scenario.AllParts)
        {
            if (part is ScenPart_ConfigPage_ConfigureStartingPawnsBase config)
            {
                try
                {
                    int n = (int)AccessTools.PropertyGetter(config.GetType(), "TotalPawnCount").Invoke(config, null)!;
                    if (n > 0)
                    {
                        return Math.Min(n, 10);
                    }
                }
                catch (Exception)
                {
                    // ниже — значение по умолчанию
                }
            }
        }
        return DefaultPawnCount;
    }
}

/// <summary>
/// Выбор при входе: присоединиться к колонии соавтором или основать свою. Хост (первый игрок с миром)
/// забирает первую колонию сам. Вызывается раз в кадр из интерфейса сессии.
/// </summary>
public static class ColonyChoice
{
    private static bool _claimSent;
    private static bool _shown;
    private static bool _choiceSent;
    private static string? _auto;
    private static bool _autoRead;
    private static int _firstTick = -1;
    private static Game? _game;

    public static void Frame()
    {
        ColoniesComponent? colonies = Colonies.Component;
        if (!LockstepClient.InSession || Current.Game == null || colonies == null || Find.TickManager.TicksGame <= 0)
        {
            return;
        }
        int me = LockstepClient.PlayerId;
        if (me <= 0)
        {
            return;
        }
        if (!ReferenceEquals(_game, Current.Game))
        {
            _game = Current.Game;
            _firstTick = Find.TickManager.TicksGame;
        }
        // Только когда сессия пошла (часы сдвинулись): команды до входа остальных игроков могли бы до них не дойти
        // (тестовый стенд: второй клиент стартует со своего сейва того же тика).
        if (Find.TickManager.TicksGame < _firstTick + 60)
        {
            return;
        }
        // Первая колония — хоста: колонии без владельца забирает игрок с наименьшим номером в сети.
        if (!colonies.AnyOwner)
        {
            if (!_claimSent && Find.WorldObjects.Settlements.Any(s => s.Faction == Faction.OfPlayer) && IsLowestOnline(me))
            {
                _claimSent = true;
                WorldCommands.SendColonyClaim();
                TestAutomation.Note("colonies: claim sent (lowest online player)");
            }
            return;
        }
        if (colonies.HasChosen(me) || _choiceSent)
        {
            return;
        }
        string? auto = AutoChoice();
        if (auto != null)
        {
            _choiceSent = true;
            if (auto == "new")
            {
                // exchangetest: колония рядом с колонией хоста — караван дойдёт за пару игровых часов.
                PlanetTile near = CaravanSelfTest.ArmKey("exchangetest=1") && colonies.OwnedBy(1) is Settlement host
                    ? CaravanSelfTest.TileTwoAway(host.Tile, PlanetTile.Invalid) : PlanetTile.Invalid;
                PlanetTile tile = near.Valid ? near : TileFinder.RandomSettlementTileFor(Faction.OfPlayer);
                TestAutomation.Note("colonies: auto choice — new colony at " + tile);
                WorldCommands.SendColonyNew(tile);
            }
            else
            {
                Settlement? first = colonies.OwnedColonies().Select(x => x.settlement).FirstOrDefault();
                TestAutomation.Note("colonies: auto choice — join " + first?.ID);
                if (first != null)
                {
                    WorldCommands.SendColonyJoin(first);
                }
            }
            return;
        }
        if (!_shown && !Find.WindowStack.IsOpen<Dialog_ColonyChoice>())
        {
            _shown = true;
            Find.WindowStack.Add(new Dialog_ColonyChoice());
        }
    }

    /// <summary>Выбор сделан окном: больше не показывать.</summary>
    public static void MarkSent() => _choiceSent = true;

    /// <summary>Открыть окно снова (из окна «Игроки» или после отмены выбора клетки).</summary>
    public static void Reopen()
    {
        _shown = false;
        _choiceSent = false;
    }

    private static bool IsLowestOnline(int me)
    {
        int lowest = int.MaxValue;
        foreach (var p in RWMLWC.Client.SessionView.SessionUi.Players)
        {
            if (p.Online && p.PlayerId < lowest)
            {
                lowest = p.PlayerId;
            }
        }
        return lowest == int.MaxValue || lowest == me;
    }

    // Тестовые прогоны: ключ метки colonychoice=join|new — выбор без окна.
    private static string? AutoChoice()
    {
        if (!_autoRead)
        {
            _autoRead = true;
            try
            {
                string arm = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "RWMLWC", "arm-determinism-run.txt");
                if (System.IO.File.Exists(arm))
                {
                    string? line = System.IO.File.ReadAllLines(arm).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("colonychoice="));
                    _auto = line?.Substring("colonychoice=".Length);
                }
            }
            catch (Exception)
            {
                _auto = null;
            }
        }
        return _auto;
    }
}

/// <summary>Окно выбора: колонии с владельцами — «Присоединиться», внизу — «Основать свою колонию».</summary>
public sealed class Dialog_ColonyChoice : Window
{
    public override Vector2 InitialSize => new Vector2(520f, 420f);

    public Dialog_ColonyChoice()
    {
        forcePause = false;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        ColoniesComponent? colonies = Colonies.Component;
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "Ваша колония");
        Text.Font = GameFont.Small;
        float y = inRect.y + 40f;
        Widgets.Label(new Rect(inRect.x, y, inRect.width, 44f),
            "Присоединитесь к колонии другого игрока соавтором или основайте свою: новая группа колонистов, как в начале игры, на выбранной клетке карты мира.");
        y += 52f;
        if (colonies != null)
        {
            foreach ((Settlement settlement, int owner) in colonies.OwnedColonies())
            {
                Rect row = new Rect(inRect.x, y, inRect.width, 32f);
                Widgets.Label(new Rect(row.x, row.y + 5f, row.width - 170f, 26f), $"{settlement.LabelCap} — игрок {RWMLWC.Client.SessionView.SessionUi.NameOf(owner)}");
                if (Widgets.ButtonText(new Rect(row.xMax - 160f, row.y, 160f, 30f), "Присоединиться"))
                {
                    WorldCommands.SendColonyJoin(settlement);
                    ColonyChoice.MarkSent();
                    Close();
                }
                y += 36f;
            }
        }
        if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 36f, 240f, 34f), "Основать свою колонию"))
        {
            Close();
            ColonyChoice.MarkSent();
            CameraJumper.TryShowWorld();
            Find.WorldTargeter.BeginTargeting(PickTile, canTargetTiles: true, extraLabelGetter: t => ExplainTile(t.Tile), showCancelButton: true);
        }
    }

    public override void PostClose()
    {
        base.PostClose();
    }

    private static bool PickTile(GlobalTargetInfo target)
    {
        var reason = new System.Text.StringBuilder();
        if (!TileFinder.IsValidTileForNewSettlement(target.Tile, reason))
        {
            Messages.Message(reason.ToString(), MessageTypeDefOf.RejectInput, false);
            return false;
        }
        WorldCommands.SendColonyNew(target.Tile);
        return true;
    }

    private static TaggedString ExplainTile(PlanetTile tile)
    {
        var reason = new System.Text.StringBuilder();
        return TileFinder.IsValidTileForNewSettlement(tile, reason) ? (TaggedString)"Основать колонию здесь" : (TaggedString)reason.ToString();
    }
}
