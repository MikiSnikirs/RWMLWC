using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Client.Maps;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.World;

/// <summary>
/// T-036, этап 3: обмен между игроками «с оплатой» (сделал A по поручению владельца 25.11). Ванильная торговля работает
/// только с чужой фракцией, а колонии игроков — одна фракция. Поэтому своя сделка:
///  - игрок A, чьи пешки гостят в колонии игрока B (GuestsComponent), предлагает: вещи из инвентаря своих гостей
///    в обмен на вещи, лежащие на карте B (номер вещи и количество);
///  - B (владелец или соавтор колонии) принимает или отклоняет;
///  - принятие исполняется у всех одной командой: всё проверяется заново (вещи на месте, количества есть), затем вещи A
///    ложатся на карту B рядом с гостем, вещи B — в инвентарь гостя A. Либо целиком, либо никак — ровно один раз.
/// Предложения — в сейве. Все изменения — только при исполнении команд, одинаково у всех.
/// </summary>
public sealed class PlayerTradeComponent : GameComponent
{
    public sealed class Offer : IExposable
    {
        public int id;
        public int from;
        public int mapId;
        public int guestPawn;
        public List<int> giveIds = new List<int>();
        public List<int> giveCounts = new List<int>();
        public List<int> wantIds = new List<int>();
        public List<int> wantCounts = new List<int>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref mapId, "mapId");
            Scribe_Values.Look(ref guestPawn, "guestPawn");
            Scribe_Collections.Look(ref giveIds, "giveIds", LookMode.Value);
            Scribe_Collections.Look(ref giveCounts, "giveCounts", LookMode.Value);
            Scribe_Collections.Look(ref wantIds, "wantIds", LookMode.Value);
            Scribe_Collections.Look(ref wantCounts, "wantCounts", LookMode.Value);
            giveIds ??= new List<int>(); giveCounts ??= new List<int>(); wantIds ??= new List<int>(); wantCounts ??= new List<int>();
        }
    }

    public List<Offer> offers = new List<Offer>();
    public int nextId = 1;

    public PlayerTradeComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref offers, "rwmlwcTradeOffers", LookMode.Deep);
        Scribe_Values.Look(ref nextId, "rwmlwcTradeNextId", 1);
        offers ??= new List<Offer>();
    }

    public string Digest() => string.Join(";", offers.Select(o => $"{o.id}:{o.from}@{o.mapId} give[{string.Join(",", o.giveIds.Zip(o.giveCounts, (a, b) => a + "x" + b))}] want[{string.Join(",", o.wantIds.Zip(o.wantCounts, (a, b) => a + "x" + b))}]"));
}

public static class PlayerTrade
{
    public const byte OpPropose = 45;
    public const byte OpAccept = 46;
    public const byte OpDecline = 47;
    public const byte OpTestGive = 48; // только тестовые прогоны (ключ playertrade=1): положить гостю вещи в инвентарь

    public static PlayerTradeComponent? Component => Current.Game?.GetComponent<PlayerTradeComponent>();

    /// <summary>Игрок может принять сделку на этой карте: владелец или соавтор колонии.</summary>
    public static bool MayAccept(Map map, int player)
    {
        if (!(map.Parent is RimWorld.Planet.Settlement st))
        {
            return false;
        }
        ColoniesComponent? c = Colonies.Component;
        return c != null && (c.OwnerOf(st) == player || c.MembersOf(st).Contains(player));
    }

    // ---------- отправка ----------

    public static void SendPropose(Pawn guest, IList<(Thing thing, int count)> give, IList<(Thing thing, int count)> want)
    {
        WorldCommands.SendOp(OpPropose, w =>
        {
            w.WriteInt32(guest.MapHeld.uniqueID);
            w.WriteInt32(guest.thingIDNumber);
            w.WriteInt32(give.Count);
            foreach ((Thing t, int n) in give) { w.WriteInt32(t.thingIDNumber); w.WriteInt32(n); }
            w.WriteInt32(want.Count);
            foreach ((Thing t, int n) in want) { w.WriteInt32(t.thingIDNumber); w.WriteInt32(n); }
        });
    }

    public static void SendAccept(int offerId) => WorldCommands.SendOp(OpAccept, w => w.WriteInt32(offerId));

    public static void SendDecline(int offerId) => WorldCommands.SendOp(OpDecline, w => w.WriteInt32(offerId));

    // ---------- исполнение (у всех, в общем порядке) ----------

    public static void Execute(byte op, ref PacketReader r)
    {
        PlayerTradeComponent? c = Component;
        int by = CommandBus.CurrentPlayerId;
        switch (op)
        {
            case OpPropose:
            {
                int mapId = r.ReadInt32();
                int guestId = r.ReadInt32();
                var offer = new PlayerTradeComponent.Offer { from = by, mapId = mapId, guestPawn = guestId };
                int ng = r.ReadCount(256, 8);
                for (int i = 0; i < ng; i++) { offer.giveIds.Add(r.ReadInt32()); offer.giveCounts.Add(r.ReadInt32()); }
                int nw = r.ReadCount(256, 8);
                for (int i = 0; i < nw; i++) { offer.wantIds.Add(r.ReadInt32()); offer.wantCounts.Add(r.ReadInt32()); }
                Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                Pawn? guest = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == guestId);
                if (c == null || map == null || guest == null || GuestsComponent.Component?.GuestPlayer(guest) != by)
                {
                    TestAutomation.Note($"trade: offer from player {by} rejected — not a guest of this player on map {mapId}");
                    return;
                }
                offer.id = c.nextId++;
                c.offers.Add(offer);
                TestAutomation.Note($"trade: offer #{offer.id} from player {by} on map {mapId}: {c.Digest()}");
                if (MayAccept(map, LockstepClient.PlayerId))
                {
                    Messages.Message($"RWMLWC: игрок {SessionView.SessionUi.NameOf(by)} предлагает обмен (вкладка гостя: «Сделки»)", MessageTypeDefOf.NeutralEvent, false);
                }
                return;
            }
            case OpAccept:
            case OpDecline:
            {
                int id = r.ReadInt32();
                PlayerTradeComponent.Offer? offer = c?.offers.FirstOrDefault(o => o.id == id);
                Map? map = offer == null ? null : Find.Maps.FirstOrDefault(m => m.uniqueID == offer.mapId);
                if (c == null || offer == null || map == null)
                {
                    return;
                }
                bool owner = MayAccept(map, by);
                if (op == OpDecline)
                {
                    if (owner || by == offer.from)
                    {
                        c.offers.Remove(offer);
                        TestAutomation.Note($"trade: offer #{id} declined by player {by}");
                    }
                    return;
                }
                if (!owner)
                {
                    TestAutomation.Note($"trade: player {by} may not accept offer #{id}");
                    return;
                }
                c.offers.Remove(offer);
                string result = TryApply(map, offer);
                TestAutomation.Note($"trade: offer #{id} accepted by player {by} at tick {Find.TickManager.TicksGame}: {result}");
                Messages.Message("RWMLWC: обмен " + (result == "done" ? "состоялся" : "не состоялся — " + result), MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            case OpTestGive:
            {
                int mapId = r.ReadInt32();
                int count = r.ReadInt32();
                if (!CaravanSelfTest.ArmKey("playertrade=1"))
                {
                    return;
                }
                Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                Pawn? guest = map?.mapPawns.AllPawnsSpawned.Where(p => GuestsComponent.Component?.GuestPlayer(p) == by).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (map == null || guest == null)
                {
                    return;
                }
                MapRandContext.Enter(map);
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = count;
                guest.inventory.innerContainer.TryAdd(steel);
                TestAutomation.Note($"trade test: guest {guest.thingIDNumber} of player {by} got {count} steel ({steel.thingIDNumber}) in inventory");
                return;
            }
        }
    }

    /// <summary>Перенос по сделке: сначала проверка всего, потом перенос всего.</summary>
    private static string TryApply(Map map, PlayerTradeComponent.Offer offer)
    {
        Pawn? guest = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == offer.guestPawn);
        if (guest == null)
        {
            return "гость ушёл с карты";
        }
        var give = new List<(Thing, int)>();
        for (int i = 0; i < offer.giveIds.Count; i++)
        {
            // Отдавать можно из инвентаря любого гостя этого игрока на карте.
            Thing? t = map.mapPawns.AllPawnsSpawned.Where(p => GuestsComponent.Component?.GuestPlayer(p) == offer.from)
                .SelectMany(p => p.inventory.innerContainer).FirstOrDefault(x => x.thingIDNumber == offer.giveIds[i]);
            if (t == null || t.stackCount < offer.giveCounts[i] || offer.giveCounts[i] <= 0)
            {
                return $"нет {offer.giveCounts[i]} шт. вещи {offer.giveIds[i]} у гостей";
            }
            give.Add((t, offer.giveCounts[i]));
        }
        var want = new List<(Thing, int)>();
        for (int i = 0; i < offer.wantIds.Count; i++)
        {
            Thing? t = map.listerThings.AllThings.FirstOrDefault(x => x.thingIDNumber == offer.wantIds[i] && x.Spawned && x.def.category == ThingCategory.Item);
            if (t == null || t.stackCount < offer.wantCounts[i] || offer.wantCounts[i] <= 0)
            {
                return $"нет {offer.wantCounts[i]} шт. вещи {offer.wantIds[i]} на карте";
            }
            want.Add((t, offer.wantCounts[i]));
        }
        MapRandContext.Enter(map);
        foreach ((Thing t, int n) in give)
        {
            ThingOwner holder = t.holdingOwner;
            Thing part = n >= t.stackCount ? t : t.SplitOff(n);
            if (part == t)
            {
                holder.Remove(t);
            }
            GenPlace.TryPlaceThing(part, guest.Position, map, ThingPlaceMode.Near);
        }
        foreach ((Thing t, int n) in want)
        {
            Thing part = n >= t.stackCount ? t : t.SplitOff(n);
            if (part.Spawned)
            {
                part.DeSpawn();
            }
            guest.inventory.innerContainer.TryAdd(part, true);
        }
        return "done";
    }
}

/// <summary>Окно предложения обмена — у игрока, чей гость стоит в чужой колонии.</summary>
public sealed class Dialog_PlayerTrade : Window
{
    private readonly Pawn _guest;
    private readonly Dictionary<int, int> _give = new Dictionary<int, int>();
    private readonly Dictionary<int, int> _want = new Dictionary<int, int>();
    private UnityEngine.Vector2 _scrollA, _scrollB;

    public override UnityEngine.Vector2 InitialSize => new UnityEngine.Vector2(760f, 560f);

    public Dialog_PlayerTrade(Pawn guest)
    {
        _guest = guest;
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
    }

    private List<Thing> Mine() => _guest.inventory.innerContainer.OrderBy(t => t.thingIDNumber).ToList();

    private List<Thing> Theirs() => _guest.MapHeld.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
        .Where(t => t.Spawned && t.def.category == ThingCategory.Item && t.Position.InHorDistOf(_guest.Position, 20f)
            && !t.def.IsWithinCategory(ThingCategoryDefOf.Chunks)) // обломки камня забивали список (снимок t036-pt2)
        .OrderByDescending(t => t.MarketValue * t.stackCount).ThenBy(t => t.thingIDNumber).Take(200).ToList();

    public override void DoWindowContents(UnityEngine.Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new UnityEngine.Rect(inRect.x, inRect.y, inRect.width, 32f), "Обмен с колонией игрока");
        Text.Font = GameFont.Small;
        float half = (inRect.width - 20f) / 2f;
        Widgets.Label(new UnityEngine.Rect(inRect.x, inRect.y + 36f, half, 24f), "Отдаю (у " + _guest.LabelShort + ")");
        Widgets.Label(new UnityEngine.Rect(inRect.x + half + 20f, inRect.y + 36f, half, 24f), "Прошу (рядом на их карте)");
        float listY = inRect.y + 62f, listH = inRect.height - 62f - 44f;
        DrawList(new UnityEngine.Rect(inRect.x, listY, half, listH), Mine(), _give, ref _scrollA);
        DrawList(new UnityEngine.Rect(inRect.x + half + 20f, listY, half, listH), Theirs(), _want, ref _scrollB);
        if (Widgets.ButtonText(new UnityEngine.Rect(inRect.xMax - 180f, inRect.yMax - 36f, 180f, 34f), "Предложить"))
        {
            var give = Mine().Where(t => _give.TryGetValue(t.thingIDNumber, out int n) && n > 0).Select(t => (t, _give[t.thingIDNumber])).ToList();
            var want = Theirs().Where(t => _want.TryGetValue(t.thingIDNumber, out int n) && n > 0).Select(t => (t, _want[t.thingIDNumber])).ToList();
            if (give.Count + want.Count == 0)
            {
                Messages.Message("RWMLWC: выберите, что отдать и что попросить", MessageTypeDefOf.RejectInput, false);
                return;
            }
            PlayerTrade.SendPropose(_guest, give, want);
            Close();
        }
    }

    private static void DrawList(UnityEngine.Rect rect, List<Thing> things, Dictionary<int, int> counts, ref UnityEngine.Vector2 scroll)
    {
        UnityEngine.Rect view = new UnityEngine.Rect(0f, 0f, rect.width - 16f, things.Count * 28f);
        Widgets.BeginScrollView(rect, ref scroll, view);
        for (int i = 0; i < things.Count; i++)
        {
            Thing t = things[i];
            counts.TryGetValue(t.thingIDNumber, out int n);
            UnityEngine.Rect row = new UnityEngine.Rect(0f, i * 28f, view.width, 26f);
            Widgets.Label(new UnityEngine.Rect(row.x, row.y + 3f, row.width - 120f, 24f), $"{t.LabelCapNoCount} ({t.stackCount})");
            if (Widgets.ButtonText(new UnityEngine.Rect(row.xMax - 116f, row.y, 34f, 26f), "−") && n > 0) counts[t.thingIDNumber] = n - 1;
            Widgets.Label(new UnityEngine.Rect(row.xMax - 78f, row.y + 3f, 40f, 24f), n.ToString());
            if (Widgets.ButtonText(new UnityEngine.Rect(row.xMax - 38f, row.y, 34f, 26f), "+") && n < t.stackCount) counts[t.thingIDNumber] = n + 1;
        }
        Widgets.EndScrollView();
    }
}

/// <summary>Окно предложений — у владельца и соавторов колонии, где гостит пешка.</summary>
public sealed class Dialog_TradeOffers : Window
{
    private readonly Map _map;

    public override UnityEngine.Vector2 InitialSize => new UnityEngine.Vector2(640f, 420f);

    public Dialog_TradeOffers(Map map)
    {
        _map = map;
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = false;
    }

    private static string Describe(List<int> ids, List<int> counts, Map map)
    {
        var all = map.listerThings.AllThings.Concat(map.mapPawns.AllPawnsSpawned.SelectMany(p => p.inventory.innerContainer));
        return string.Join(", ", ids.Select((id, i) => $"{all.FirstOrDefault(t => t.thingIDNumber == id)?.LabelCapNoCount ?? ("вещь " + id)} ×{counts[i]}"));
    }

    public override void DoWindowContents(UnityEngine.Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new UnityEngine.Rect(inRect.x, inRect.y, inRect.width, 32f), "Предложения обмена");
        Text.Font = GameFont.Small;
        bool may = PlayerTrade.MayAccept(_map, LockstepClient.PlayerId);
        float y = inRect.y + 40f;
        foreach (PlayerTradeComponent.Offer o in PlayerTrade.Component?.offers.Where(o => o.mapId == _map.uniqueID).ToList() ?? new List<PlayerTradeComponent.Offer>())
        {
            Widgets.Label(new UnityEngine.Rect(inRect.x, y, inRect.width, 22f), $"#{o.id} от {SessionView.SessionUi.NameOf(o.from)}");
            Widgets.Label(new UnityEngine.Rect(inRect.x + 10f, y + 22f, inRect.width - 10f, 22f), "Даёт: " + Describe(o.giveIds, o.giveCounts, _map));
            Widgets.Label(new UnityEngine.Rect(inRect.x + 10f, y + 44f, inRect.width - 10f, 22f), "Просит: " + Describe(o.wantIds, o.wantCounts, _map));
            if (may && Widgets.ButtonText(new UnityEngine.Rect(inRect.xMax - 220f, y, 100f, 28f), "Принять"))
            {
                PlayerTrade.SendAccept(o.id);
            }
            if ((may || o.from == LockstepClient.PlayerId) && Widgets.ButtonText(new UnityEngine.Rect(inRect.xMax - 110f, y, 100f, 28f), may ? "Отклонить" : "Отозвать"))
            {
                PlayerTrade.SendDecline(o.id);
            }
            y += 74f;
        }
        if (y == inRect.y + 40f)
        {
            Widgets.Label(new UnityEngine.Rect(inRect.x, y, inRect.width, 24f), "Предложений нет.");
        }
    }
}

[HarmonyLib.HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
internal static class Patch_Pawn_GetGizmos_PlayerTrade
{
    // Кнопки — у всех клиентов одинаковые (зависят только от состояния игры: номер кнопки у всех один);
    // нажатие идёт командой, окно открывается только у нажавшего и только если ему можно.
    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
    {
        foreach (Gizmo g in __result)
        {
            yield return g;
        }
        Pawn pawn = __instance;
        if (!LockstepClient.InSession || !pawn.Spawned || (GuestsComponent.Component?.GuestPlayer(pawn) ?? 0) == 0)
        {
            yield break;
        }
        yield return new Command_Action
        {
            defaultLabel = "Предложить обмен",
            defaultDesc = "RWMLWC: обмен с колонией, где гостит пешка: вещи из её инвентаря на вещи этой колонии. Хозяин колонии примет или отклонит.",
            icon = LetterDefOf.PositiveEvent.Icon,
            action = () =>
            {
                if (!CommandBus.ExecutingRemote && GuestsComponent.Component?.GuestPlayer(pawn) == LockstepClient.PlayerId)
                {
                    Find.WindowStack.Add(new Dialog_PlayerTrade(pawn));
                }
            },
        };
        yield return new Command_Action
        {
            defaultLabel = "Сделки",
            defaultDesc = "RWMLWC: предложения обмена на этой карте — принять, отклонить или отозвать.",
            icon = LetterDefOf.NeutralEvent.Icon,
            action = () =>
            {
                if (!CommandBus.ExecutingRemote)
                {
                    Find.WindowStack.Add(new Dialog_TradeOffers(pawn.MapHeld));
                }
            },
        };
    }
}
