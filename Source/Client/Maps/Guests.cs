using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Гости в чужой колонии (T-036, T-043): чьи пешки вошли караваном в колонию другого игрока. Пешки — общей фракции
/// игрока, поэтому «чей гость» узнаётся по команде: последний игрок, который командой проложил каравану маршрут,
/// отправил его. Когда такой караван входит на карту колонии, где этот игрок не владелец и не соавтор, его пешки
/// записываются гостями этого игрока; когда пешка входит в свою колонию — запись снимается. Гость в сети
/// запрещает стазис колонии (<see cref="StasisComponent.GuestsPresent"/>), как пешка из отряда. Решения — в тике
/// и в исполнении команд, у всех одинаково; состояние в сейве (снимок для вернувшегося игрока).
/// </summary>
public sealed class GuestsComponent : GameComponent
{
    /// <summary>Караван → игрок, который последним командой проложил ему маршрут.</summary>
    public Dictionary<int, int> caravanSender = new Dictionary<int, int>();

    /// <summary>Пешка → игрок, чьим гостем она стоит в чужой колонии.</summary>
    public Dictionary<int, int> guestOf = new Dictionary<int, int>();

    /// <summary>Пешка в летящей капсуле → игрок, который запустил капсулы (T-053).</summary>
    public Dictionary<int, int> podSender = new Dictionary<int, int>();

    public GuestsComponent(Game game)
    {
    }

    public static GuestsComponent? Component => Current.Game?.GetComponent<GuestsComponent>();

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref caravanSender, "rwmlwcCaravanSender", LookMode.Value, LookMode.Value);
        Scribe_Collections.Look(ref guestOf, "rwmlwcGuestOf", LookMode.Value, LookMode.Value);
        Scribe_Collections.Look(ref podSender, "rwmlwcPodSender", LookMode.Value, LookMode.Value);
        caravanSender ??= new Dictionary<int, int>();
        guestOf ??= new Dictionary<int, int>();
        podSender ??= new Dictionary<int, int>();
    }

    /// <summary>Игрок, чьим гостем стоит пешка (0 — не гость).</summary>
    public int GuestPlayer(Pawn pawn) => guestOf.TryGetValue(pawn.thingIDNumber, out int p) ? p : 0;

    /// <summary>Из исполнения команды маршрута: караван отправил этот игрок.</summary>
    public static void NoteRoute(Caravan caravan, int player)
    {
        if (player > 0 && Component is GuestsComponent c)
        {
            c.caravanSender[caravan.ID] = player;
        }
    }

    /// <summary>Караван входит на карту: его пешки — гости отправителя в чужой колонии, иначе запись снимается.</summary>
    internal static void OnEnter(Caravan caravan, Map map)
    {
        GuestsComponent? c = Component;
        if (c == null)
        {
            return;
        }
        int sender = c.caravanSender.TryGetValue(caravan.ID, out int s) ? s : 0;
        c.caravanSender.Remove(caravan.ID);
        int guestOfPlayer = c.Mark(caravan.PawnsListForReading, sender, map);
        if (guestOfPlayer != 0)
        {
            TestAutomation.Note($"guests: caravan {caravan.ID} entered map {map.uniqueID} — {caravan.PawnsListForReading.Count} pawns are guests of player {guestOfPlayer}");
        }
    }

    /// <summary>Из исполнения команды запуска капсул: пешки в них отправил этот игрок.</summary>
    public static void NotePodPawns(IEnumerable<Pawn> pawns, int player)
    {
        if (player > 0 && Component is GuestsComponent c)
        {
            foreach (Pawn p in pawns)
            {
                c.podSender[p.thingIDNumber] = player;
            }
        }
    }

    /// <summary>Капсулы высаживаются на карту: пешки — гости запустившего в чужой колонии, иначе запись снимается.</summary>
    internal static void OnPodsDrop(List<Pawn> pawns, Map map)
    {
        GuestsComponent? c = Component;
        if (c == null || pawns.Count == 0)
        {
            return;
        }
        foreach (IGrouping<int, Pawn> bySender in pawns.GroupBy(p => c.podSender.TryGetValue(p.thingIDNumber, out int s) ? s : 0))
        {
            foreach (Pawn p in bySender)
            {
                c.podSender.Remove(p.thingIDNumber);
            }
            int guestOfPlayer = c.Mark(bySender.ToList(), bySender.Key, map);
            if (guestOfPlayer != 0)
            {
                TestAutomation.Note($"guests: pods landed on map {map.uniqueID} — {bySender.Count()} pawns are guests of player {guestOfPlayer}");
            }
        }
    }

    /// <summary>Пешки входят на карту: гости отправителя, если это чужая колония; иначе запись снимается.</summary>
    private int Mark(IEnumerable<Pawn> pawns, int sender, Map map)
    {
        int guestOfPlayer = 0;
        if (sender > 0 && map.Parent is Settlement settlement && settlement.Faction == Faction.OfPlayer
            && RWMLWC.Client.World.Colonies.Component is RWMLWC.Client.World.ColoniesComponent colonies)
        {
            int owner = colonies.OwnerOf(settlement);
            if (owner != 0 && owner != sender && !colonies.MembersOf(settlement).Contains(sender))
            {
                guestOfPlayer = sender;
            }
        }
        foreach (Pawn p in pawns)
        {
            if (guestOfPlayer != 0)
            {
                guestOf[p.thingIDNumber] = guestOfPlayer;
            }
            else
            {
                guestOf.Remove(p.thingIDNumber);
            }
        }
        return guestOfPlayer;
    }

    /// <summary>Для сравнения у игроков.</summary>
    public string Digest() => string.Join(",", guestOf.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value));
}

/// <summary>Вход каравана на карту (все пути — через эту перегрузку).</summary>
[HarmonyPatch(typeof(CaravanEnterMapUtility), nameof(CaravanEnterMapUtility.Enter),
    new[] { typeof(Caravan), typeof(Map), typeof(Func<Pawn, IntVec3>), typeof(CaravanDropInventoryMode), typeof(bool) })]
internal static class Patch_CaravanEnterMapUtility_Enter_Guests
{
    private static void Prefix(Caravan caravan, Map map)
    {
        if (LockstepClient.InSession && caravan != null && map != null && caravan.Faction == Faction.OfPlayer)
        {
            GuestsComponent.OnEnter(caravan, map);
        }
    }
}
