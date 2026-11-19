using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// Торговля в сессии (T-036, этап 1: с торговцами игры). Окно торговли открывается командой (кнопка «Торговать»
/// каравана, пункт меню торговца на карте, консоль связи) — значит, у всех клиентов в одной точке, и склад
/// торговца, который игра создаёт лениво при первом чтении, у всех одинаков. Окно видит только автор.
/// «Принять» не выполняет сделку на месте, а отправляет команду: торговец, переговорщик, режим подарка и
/// количества по номерам вещей. У всех сделка собирается заново и выполняется тем же <c>TradeDeal.TryExecute</c>.
/// </summary>
public static class TradeCommands
{
    internal const byte OpTrade = 17;

    private const byte KindPawn = 1;
    private const byte KindSettlement = 2;
    private const byte KindCaravan = 3;
    private const byte KindShip = 4;

    private static readonly FieldInfo ShipLoadId = AccessTools.Field(typeof(PassingShip), "loadID");

    internal static bool Capture(TradeDeal deal, ref bool actuallyTraded)
    {
        ITrader? trader = TradeSession.trader;
        Pawn? negotiator = TradeSession.playerNegotiator;
        if (trader == null || negotiator == null)
        {
            return true;
        }
        Map map = Find.CurrentMap ?? Find.Maps[0];
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(OpTrade);
        if (!WriteTrader(w, trader))
        {
            Messages.Message("RWMLWC: торговля с этим торговцем пока не работает в сетевой игре", MessageTypeDefOf.RejectInput, false);
            actuallyTraded = false;
            return false;
        }
        w.WriteInt32(negotiator.thingIDNumber);
        w.WriteBool(TradeSession.giftMode);
        List<Tradeable> moving = deal.AllTradeables.Where(t => !t.IsCurrency && t.CountToTransfer != 0 && t.HasAnyThing).ToList();
        w.WriteInt32(moving.Count);
        foreach (Tradeable t in moving)
        {
            w.WriteInt32(t.AnyThing.thingIDNumber);
            w.WriteInt32(t.CountToTransfer);
        }
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
        TestAutomation.Note($"trade: sent, {moving.Count} lines, gift {TradeSession.giftMode}");
        actuallyTraded = true; // окно закрывается; сделка выполнится, когда команда вернётся в общем порядке
        return false;
    }

    private static bool WriteTrader(PacketWriter w, ITrader trader)
    {
        switch (trader)
        {
            case Pawn p:
                w.WriteByte(KindPawn);
                w.WriteInt32(p.thingIDNumber);
                return true;
            case Settlement s:
                w.WriteByte(KindSettlement);
                w.WriteInt32(s.ID);
                return true;
            case Caravan c:
                w.WriteByte(KindCaravan);
                w.WriteInt32(c.ID);
                return true;
            case PassingShip ship when ship.Map != null:
                w.WriteByte(KindShip);
                w.WriteInt32(ship.Map.uniqueID);
                w.WriteInt32((int)ShipLoadId.GetValue(ship)!);
                return true;
            default:
                return false;
        }
    }

    private static ITrader? ReadTrader(ref PacketReader r, out Map? map)
    {
        map = null;
        byte kind = r.ReadByte();
        switch (kind)
        {
            case KindPawn:
                {
                    Pawn? p = ValueCodec.FindPawnAnywhere(r.ReadInt32());
                    map = p?.MapHeld;
                    return p;
                }
            case KindSettlement:
                {
                    int id = r.ReadInt32();
                    return Find.WorldObjects.Settlements.FirstOrDefault(s => s.ID == id);
                }
            case KindCaravan:
                {
                    int id = r.ReadInt32();
                    return Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == id);
                }
            case KindShip:
                {
                    int mapId = r.ReadInt32();
                    int loadId = r.ReadInt32();
                    map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                    return map?.passingShipManager.passingShips.FirstOrDefault(s => (int)ShipLoadId.GetValue(s)! == loadId) as ITrader;
                }
            default:
                return null;
        }
    }

    public static void Execute(ref PacketReader r)
    {
        ITrader? trader = ReadTrader(ref r, out Map? map);
        Pawn? negotiator = ValueCodec.FindPawnAnywhere(r.ReadInt32());
        bool gift = r.ReadBool();
        int count = r.ReadCount(4096, 8);
        var lines = new List<(int id, int count)>(count);
        for (int i = 0; i < count; i++)
        {
            lines.Add((r.ReadInt32(), r.ReadInt32()));
        }
        if (trader == null || negotiator == null)
        {
            TestAutomation.Note("trade: trader or negotiator is gone, dropped");
            return;
        }
        if (RWMLWC.Client.Maps.SquadRules.RejectAtExecution(
                RWMLWC.Client.Maps.SquadRules.ForbiddenAny(new object[] { negotiator }, CommandBus.CurrentPlayerId), CommandBus.CurrentSeq))
        {
            return;
        }
        MapRandContext.Enter(map ?? negotiator.MapHeld);
        // Прежняя торговая сессия этого клиента (окно автора, скрытое окно у остальных) — заменяется.
        TradeSession.SetupWith(trader, negotiator, gift);
        try
        {
            int applied = 0;
            foreach ((int id, int value) in lines)
            {
                Tradeable? t = TradeSession.deal.AllTradeables.FirstOrDefault(x =>
                    x.thingsColony.Any(th => th.thingIDNumber == id) || x.thingsTrader.Any(th => th.thingIDNumber == id));
                if (t != null && !t.IsCurrency)
                {
                    t.ForceTo(value);
                    applied++;
                }
            }
            bool ok = TradeSession.deal.TryExecute(out bool traded);
            negotiator.GetCaravan()?.RecacheInventory();
            TestAutomation.Note($"trade: executed by player {CommandBus.CurrentPlayerId}: {applied}/{lines.Count} lines, ok {ok}, traded {traded}");
        }
        finally
        {
            TradeSession.Close();
        }
    }
}

/// <summary>Кто последним отдал пешке приказ (команда): окно торговли, открытое её заданием в тике, видит только он.</summary>
public static class OrderedBy
{
    private static readonly Dictionary<int, int> last = new Dictionary<int, int>();
    private static Game? game;

    public static void Set(Pawn pawn, int player)
    {
        Check();
        last[pawn.thingIDNumber] = player;
    }

    public static int Get(Pawn pawn)
    {
        Check();
        return last.TryGetValue(pawn.thingIDNumber, out int p) ? p : 0;
    }

    private static void Check()
    {
        if (!ReferenceEquals(game, Current.Game))
        {
            game = Current.Game;
            last.Clear();
        }
    }

    /// <summary>Окно торговли открылось в тике (колонист дошёл до торговца): показывать ли его этому игроку.</summary>
    public static bool ShowTickTrade()
    {
        Pawn? negotiator = TradeSession.playerNegotiator;
        int who = negotiator != null ? Get(negotiator) : 0;
        bool show = who == 0 || who == LockstepClient.PlayerId; // неизвестно, кто приказал (после загрузки) — показываем всем
        TestAutomation.Note($"trade: window opened in tick for negotiator {negotiator?.thingIDNumber}, ordered by {who}, shown {show}");
        return show;
    }
}

[HarmonyPatch(typeof(Verse.AI.Pawn_JobTracker), nameof(Verse.AI.Pawn_JobTracker.TryTakeOrderedJob))]
internal static class Patch_TryTakeOrderedJob_Who
{
    private static void Postfix(Verse.AI.Pawn_JobTracker __instance, Pawn ___pawn)
    {
        if (CommandBus.Executing && ___pawn != null)
        {
            OrderedBy.Set(___pawn, CommandBus.CurrentPlayerId);
        }
    }
}

[HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
internal static class Patch_TradeDeal_TryExecute
{
    private static bool Prefix(TradeDeal __instance, ref bool actuallyTraded, ref bool __result)
    {
        if (!CommandBus.CaptureActive)
        {
            return true;
        }
        bool run = TradeCommands.Capture(__instance, ref actuallyTraded);
        if (!run)
        {
            __result = actuallyTraded;
        }
        return run;
    }
}

/// <summary>В сессии окно торговли не ставит игру на паузу всем: торгует один, остальные играют.</summary>
[HarmonyPatch(typeof(Dialog_Trade), MethodType.Constructor, new[] { typeof(Pawn), typeof(ITrader), typeof(bool) })]
internal static class Patch_Dialog_Trade_NoPause
{
    private static void Postfix(Dialog_Trade __instance)
    {
        if (LockstepClient.InSession)
        {
            __instance.forcePause = false;
        }
    }
}
