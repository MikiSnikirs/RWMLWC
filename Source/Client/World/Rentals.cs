using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Client.Maps;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>Одна аренда: пешка одолжена игроком lender игроку borrower до тика untilTick.</summary>
public sealed class Rental : IExposable
{
    public int pawnId;
    public int lender;
    public int borrower;
    public int untilTick;
    public int prevSquad;  // отряд пешки до аренды (0 — без отряда)
    public int squadId;    // отряд аренды, в котором пешка сейчас

    public void ExposeData()
    {
        Scribe_Values.Look(ref pawnId, "pawn");
        Scribe_Values.Look(ref lender, "lender");
        Scribe_Values.Look(ref borrower, "borrower");
        Scribe_Values.Look(ref untilTick, "until");
        Scribe_Values.Look(ref prevSquad, "prevSquad");
        Scribe_Values.Look(ref squadId, "squad");
    }
}

/// <summary>
/// Аренда пешек (T-053, решение владельца 19.11 — «отряд на срок»). Игрок, который может приказывать пешке (она в его
/// отряде или без отряда), одалживает её другому игроку на N игровых дней: пешка переходит в отряд аренды
/// «Аренда A→B», где участник — только заёмщик (правила отрядов, T-061: приказывает только он). По истечении срока
/// или по отзыву (отзывает владелец; заёмщик может вернуть раньше) пешка возвращается в прежний отряд, где бы она ни
/// была; домой её ведут вручную (караван, капсула). Выдача и отзыв — команды мира (операции 25, 26), срок
/// проверяется в тике; состояние в сейве — у всех одинаково.
/// </summary>
public sealed class RentalsComponent : GameComponent
{
    private const int CheckIntervalTicks = 250;

    public List<Rental> rentals = new List<Rental>();

    public RentalsComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref rentals, "rwmlwcRentals", LookMode.Deep);
        rentals ??= new List<Rental>();
    }

    public Rental? Of(int pawnId) => rentals.FirstOrDefault(r => r.pawnId == pawnId);

    public override void GameComponentTick()
    {
        if (!LockstepClient.InSession || rentals.Count == 0 || Find.TickManager.TicksGame % CheckIntervalTicks != 0)
        {
            return;
        }
        int now = Find.TickManager.TicksGame;
        foreach (Rental r in rentals.Where(r => now >= r.untilTick).ToList())
        {
            Rentals.End(this, r, "срок аренды вышел");
        }
    }

    public string Digest() => string.Join(",", rentals.OrderBy(r => r.pawnId).Select(r => $"{r.pawnId}:{r.lender}>{r.borrower}@{r.untilTick}/s{r.squadId}"));
}

public static class Rentals
{
    internal const byte OpLend = 25;
    internal const byte OpRecall = 26;
    public const int MaxDays = 60;

    public static RentalsComponent? Component => Current.Game?.GetComponent<RentalsComponent>();

    private static void Send(byte op, Action<PacketWriter> body)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(op);
        body(w);
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
    }

    public static void SendLend(Pawn pawn, int borrower, int days) => Send(OpLend, w =>
    {
        w.WriteInt32(pawn.thingIDNumber);
        w.WriteInt32(borrower);
        w.WriteInt32(days);
    });

    public static void SendRecall(int pawnId) => Send(OpRecall, w => w.WriteInt32(pawnId));

    internal static void Execute(byte op, ref PacketReader r)
    {
        MapRandContext.Enter(null);
        RentalsComponent? comp = Component;
        SquadsComponent? squads = Squads.Component;
        int author = CommandBus.CurrentPlayerId;
        if (op == OpLend)
        {
            int pawnId = r.ReadInt32();
            int borrower = r.ReadInt32();
            int days = r.ReadInt32();
            Pawn? pawn = ValueCodec.FindPawnAnywhere(pawnId);
            string? why = comp == null || squads == null ? "нет состояния аренды в игре"
                : pawn == null || pawn.Dead || !pawn.IsColonist ? "пешки нет или она не колонист"
                : borrower <= 0 || borrower == author ? "заёмщик — тот же игрок"
                : days < 1 || days > MaxDays ? "срок вне 1.." + MaxDays
                : comp.Of(pawnId) != null ? "пешка уже одолжена"
                : !Squads.CanCommand(author, pawn) ? "пешка в чужом отряде"
                : null;
            if (why != null)
            {
                TestAutomation.Note($"rentals: lend of pawn {pawnId} by player {author} rejected: {why}");
                if (author == LockstepClient.PlayerId)
                {
                    Messages.Message("RWMLWC: одолжить нельзя — " + why, MessageTypeDefOf.RejectInput, false);
                }
                return;
            }
            Squad squad = RentalSquad(comp!, squads!, author, borrower);
            Rental rental = new Rental
            {
                pawnId = pawnId,
                lender = author,
                borrower = borrower,
                untilTick = Find.TickManager.TicksGame + days * GenDate.TicksPerDay,
                prevSquad = squads!.SquadOf(pawnId)?.id ?? 0,
                squadId = squad.id,
            };
            foreach (Squad s in squads.squads)
            {
                s.pawnIds.Remove(pawnId);
            }
            squad.pawnIds.Add(pawnId);
            comp!.rentals.Add(rental);
            TestAutomation.Note($"rentals: pawn {pawnId} lent by player {author} to player {borrower} for {days} days, until {rental.untilTick}, squad {squad.id}, was in {rental.prevSquad}");
            Messages.Message($"RWMLWC: {pawn!.LabelShort} одолжен(а) игроку {SessionView.SessionUi.NameOf(borrower)} на {days} дн.", pawn, MessageTypeDefOf.NeutralEvent, false);
        }
        else if (op == OpRecall)
        {
            int pawnId = r.ReadInt32();
            Rental? rental = comp?.Of(pawnId);
            if (rental == null || (author != rental.lender && author != rental.borrower))
            {
                TestAutomation.Note($"rentals: recall of pawn {pawnId} by player {author} rejected");
                return;
            }
            End(comp!, rental, author == rental.lender ? "отозвана владельцем" : "возвращена заёмщиком");
        }
    }

    /// <summary>Отряд аренды для пары «кто одолжил → кому»: существующий или новый.</summary>
    private static Squad RentalSquad(RentalsComponent comp, SquadsComponent squads, int lender, int borrower)
    {
        foreach (Rental other in comp.rentals)
        {
            if (other.lender == lender && other.borrower == borrower && squads.Get(other.squadId) is Squad existing)
            {
                return existing;
            }
        }
        Squad squad = new Squad { id = squads.nextId++ };
        squad.color = Squads.Palette[(squad.id - 1) % Squads.Palette.Length];
        squad.name = $"Аренда {lender}→{borrower}";
        squad.players.Add(borrower);
        squads.squads.Add(squad);
        return squad;
    }

    /// <summary>Конец аренды (в тике или командой): пешка — в прежний отряд, пустой отряд аренды удаляется.</summary>
    internal static void End(RentalsComponent comp, Rental rental, string why)
    {
        comp.rentals.Remove(rental);
        SquadsComponent? squads = Squads.Component;
        if (squads != null)
        {
            Squad? rentSquad = squads.Get(rental.squadId);
            rentSquad?.pawnIds.Remove(rental.pawnId);
            if (squads.Get(rental.prevSquad) is Squad prev && squads.SquadOf(rental.pawnId) == null)
            {
                prev.pawnIds.Add(rental.pawnId);
            }
            if (rentSquad != null && rentSquad.pawnIds.Count == 0 && !comp.rentals.Any(x => x.squadId == rentSquad.id))
            {
                squads.squads.Remove(rentSquad);
            }
        }
        Pawn? pawn = ValueCodec.FindPawnAnywhere(rental.pawnId);
        TestAutomation.Note($"rentals: pawn {rental.pawnId} back from player {rental.borrower} to player {rental.lender} ({why}), squad now {squads?.SquadOf(rental.pawnId)?.id ?? 0}");
        Messages.Message($"RWMLWC: аренда закончилась — {(pawn != null ? pawn.LabelShort : "пешка")} снова у игрока {SessionView.SessionUi.NameOf(rental.lender)} ({why})",
            pawn != null ? new LookTargets(pawn) : LookTargets.Invalid, MessageTypeDefOf.NeutralEvent, false);
    }
}

/// <summary>Окно аренды — только у того, кто нажал кнопку: кому и на сколько; свои одолженные пешки с «Отозвать».</summary>
public sealed class Dialog_Lend : Window
{
    private readonly Pawn _pawn;
    private int _borrower;
    private int _days = 3;
    private Vector2 _scroll;

    public override Vector2 InitialSize => new Vector2(460f, 440f);

    public Dialog_Lend(Pawn pawn)
    {
        _pawn = pawn;
        doCloseX = true;
        closeOnClickedOutside = true;
        forcePause = false;
        absorbInputAroundWindow = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        float y = inRect.y;
        Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), $"Одолжить {_pawn.LabelShort} другому игроку");
        y += 28f;
        if (_borrower == 0)
        {
            // По умолчанию — первый другой игрок в сети (снимок t053-look: ни один не был выбран).
            _borrower = SessionView.SessionUi.Players.Where(p => p.PlayerId != LockstepClient.PlayerId && p.Online).Select(p => p.PlayerId).DefaultIfEmpty(0).Min();
        }
        foreach (PlayerInfo p in SessionView.SessionUi.Players.Where(p => p.PlayerId != LockstepClient.PlayerId))
        {
            bool chosen = _borrower == p.PlayerId;
            if (Widgets.RadioButtonLabeled(new Rect(inRect.x + 8f, y, inRect.width - 16f, 24f), $"{p.PlayerId}. {p.Name}{(p.Online ? "" : " (не в сети)")}", chosen))
            {
                _borrower = p.PlayerId;
            }
            y += 26f;
        }
        y += 4f;
        Widgets.Label(new Rect(inRect.x, y, 160f, 28f), $"Срок: {_days} дн.");
        if (Widgets.ButtonText(new Rect(inRect.x + 170f, y, 40f, 26f), "−")) _days = Math.Max(1, _days - 1);
        if (Widgets.ButtonText(new Rect(inRect.x + 214f, y, 40f, 26f), "+")) _days = Math.Min(Rentals.MaxDays, _days + 1);
        y += 32f;
        if (Widgets.ButtonText(new Rect(inRect.x, y, 160f, 30f), "Одолжить", active: _borrower > 0))
        {
            Rentals.SendLend(_pawn, _borrower, _days);
            Close();
            return;
        }
        y += 40f;
        Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
        y += 6f;
        Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "Ваши одолженные пешки:");
        y += 26f;
        List<Rental> mine = Rentals.Component?.rentals.Where(r => r.lender == LockstepClient.PlayerId || r.borrower == LockstepClient.PlayerId).ToList() ?? new List<Rental>();
        Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
        Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, mine.Count * 28f));
        Widgets.BeginScrollView(outRect, ref _scroll, view);
        float ly = 0f;
        foreach (Rental r in mine)
        {
            string name = ValueCodec.FindPawnAnywhere(r.pawnId)?.LabelShort ?? ("пешка " + r.pawnId);
            float daysLeft = Math.Max(0, r.untilTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay;
            bool lent = r.lender == LockstepClient.PlayerId;
            string who = lent ? "у игрока " + SessionView.SessionUi.NameOf(r.borrower) : "от игрока " + SessionView.SessionUi.NameOf(r.lender);
            Widgets.Label(new Rect(0f, ly, view.width - 110f, 26f), $"{name} — {who}, осталось {daysLeft:F1} дн.");
            if (Widgets.ButtonText(new Rect(view.width - 104f, ly, 104f, 24f), lent ? "Отозвать" : "Вернуть"))
            {
                Rentals.SendRecall(r.pawnId);
            }
            ly += 28f;
        }
        Widgets.EndScrollView();
    }
}

/// <summary>Кнопка «Одолжить» у колониста, которому этот игрок может приказывать (только в сетевой игре).</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
internal static class Patch_Pawn_GetGizmos_Lend
{
    private static Texture2D? _icon;

    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
    {
        foreach (Gizmo g in __result)
        {
            yield return g;
        }
        Pawn pawn = __instance;
        if (!LockstepClient.InSession || !pawn.IsColonist || pawn.Faction != Faction.OfPlayer || Rentals.Component?.Of(pawn.thingIDNumber) != null)
        {
            yield break;
        }
        _icon ??= LetterDefOf.PositiveEvent.Icon;
        yield return new Command_Action
        {
            defaultLabel = "Одолжить",
            defaultDesc = "RWMLWC: одолжить пешку другому игроку на срок — она перейдёт в его отряд аренды, а по сроку или отзыву вернётся в прежний отряд.",
            icon = _icon,
            action = () =>
            {
                // Нажатие идёт командой и исполняется у всех; окно — только у автора (у остальных VirtualUi его не покажет).
                if (!CommandBus.ExecutingRemote)
                {
                    Find.WindowStack.Add(new Dialog_Lend(pawn));
                }
            },
        };
    }
}
