using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Общие чертежи с утверждением (T-052, docs/ARCHITECTURE.md, раздел «Удобства»). Чертёж, который поставил на
/// карту колонии игрок не из неё (не владелец и не соавтор; права «стройка» у него есть — иначе команда
/// разметки не прошла бы), — предложение: колонисты его не строят и не расчищают под него место, пока владелец
/// или соавтор колонии не нажмёт «Утвердить». «Отклонить» убирает чертёж. Отменить своё предложение автор
/// может обычной отменой разметки.
///
/// Всё решается при исполнении команд, из состояния игры (колонии и их соавторы — <c>ColoniesComponent</c>,
/// владелец режима отсутствия — <c>AbsenceModesComponent</c>), поэтому одинаково у всех; список предложений —
/// в сейве. Владелец карты неизвестен — утверждение не нужно. Вне сессии — как в игре.
/// </summary>
public sealed class BlueprintApprovalComponent : GameComponent
{
    /// <summary>Номер чертежа → игрок, предложивший его.</summary>
    private Dictionary<int, int> pending = new Dictionary<int, int>();
    private List<int>? tmpK;
    private List<int>? tmpV;

    public BlueprintApprovalComponent(Game game)
    {
    }

    public static BlueprintApprovalComponent? Component => Current.Game?.GetComponent<BlueprintApprovalComponent>();

    public int Count => pending.Count;

    public int AuthorOf(Thing t) => pending.TryGetValue(t.thingIDNumber, out int p) ? p : 0;

    public bool IsPending(Thing t) => pending.ContainsKey(t.thingIDNumber);

    public void Add(Thing t, int author) => pending[t.thingIDNumber] = author;

    public bool Remove(Thing t) => pending.Remove(t.thingIDNumber);

    public string Digest() => string.Join(",", pending.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value));

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref pending, "rwmlwcPendingBlueprints", LookMode.Value, LookMode.Value, ref tmpK, ref tmpV);
        pending ??= new Dictionary<int, int>();
    }
}

public static class BlueprintApproval
{
    /// <summary>Игрок, чьи чертежи сейчас создаёт исполняемая команда разметки и требуют утверждения, или 0.</summary>
    internal static int PendingAuthor;

    /// <summary>Игрок утверждает чертежи на этой карте: владелец или соавтор колонии. Владелец неизвестен — все.</summary>
    public static bool IsApprover(Map map, int player)
    {
        int owner = 0;
        if (map.Parent is Settlement settlement)
        {
            RWMLWC.Client.World.ColoniesComponent? colonies = RWMLWC.Client.World.Colonies.Component;
            owner = colonies?.OwnerOf(settlement) ?? 0;
            if (owner != 0 && colonies!.MembersOf(settlement).Contains(player))
            {
                return true;
            }
        }
        if (owner == 0)
        {
            owner = AbsenceModes.Component?.OwnerOf(map.uniqueID) ?? 0;
        }
        return owner == 0 || owner == player;
    }

    /// <summary>Перед исполнением команды разметки: чертежи автора не из колонии — предложения.</summary>
    internal static void BeginDesignate(Map map, Designator designator)
    {
        PendingAuthor = 0;
        if (LockstepClient.InSession && designator is Designator_Place && CommandBus.CurrentPlayerId != 0 && !IsApprover(map, CommandBus.CurrentPlayerId))
        {
            PendingAuthor = CommandBus.CurrentPlayerId;
        }
    }

    internal static void EndDesignate() => PendingAuthor = 0;

    public static bool IsPending(Thing t) => LockstepClient.InSession && t is Blueprint && BlueprintApprovalComponent.Component?.IsPending(t) == true;

    /// <summary>Нажатие «Утвердить» / «Отклонить» (исполняется у всех кнопкой через GizmoCommands).</summary>
    internal static void Decide(Blueprint blueprint, bool approve)
    {
        BlueprintApprovalComponent? c = BlueprintApprovalComponent.Component;
        Map? map = blueprint.MapHeld;
        int by = LockstepClient.InSession ? CommandBus.CurrentPlayerId : LockstepClient.PlayerId;
        if (c == null || map == null || !c.IsPending(blueprint))
        {
            return;
        }
        if (!IsApprover(map, by))
        {
            TestAutomation.Note($"blueprints: player {by} may not decide on {blueprint.thingIDNumber}");
            return;
        }
        c.Remove(blueprint);
        TestAutomation.Note($"blueprints: {blueprint.thingIDNumber} ({blueprint.def.defName}) {(approve ? "approved" : "rejected")} by player {by} at tick {Find.TickManager.TicksGame}");
        if (!approve && !blueprint.Destroyed)
        {
            blueprint.Destroy(DestroyMode.Cancel);
        }
    }
}

[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.SpawnSetup))]
internal static class Patch_Blueprint_SpawnSetup_Pending
{
    private static void Postfix(Blueprint __instance, bool respawningAfterLoad)
    {
        if (!respawningAfterLoad && BlueprintApproval.PendingAuthor != 0)
        {
            BlueprintApprovalComponent.Component?.Add(__instance, BlueprintApproval.PendingAuthor);
            TestAutomation.Note($"blueprints: {__instance.thingIDNumber} ({__instance.def.defName}) proposed by player {BlueprintApproval.PendingAuthor} at tick {Find.TickManager.TicksGame}");
        }
    }
}

[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.DeSpawn))]
internal static class Patch_Blueprint_DeSpawn_Pending
{
    // Чертёж убран (отменён, заменён каркасом, уничтожен) — предложения больше нет.
    private static void Prefix(Blueprint __instance)
    {
        BlueprintApprovalComponent.Component?.Remove(__instance);
    }
}

[HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToBlueprints), nameof(WorkGiver_ConstructDeliverResourcesToBlueprints.JobOnThing))]
internal static class Patch_DeliverToBlueprints_Pending
{
    // Не строить и не расчищать место под неутверждённый чертёж (расчистка идёт в этом методе раньше CanConstruct).
    private static bool Prefix(Thing t, ref Job? __result)
    {
        if (BlueprintApproval.IsPending(t))
        {
            __result = null;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.GetGizmos))]
internal static class Patch_Blueprint_Gizmos_Approval
{
    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Blueprint __instance)
    {
        foreach (Gizmo g in gizmos)
        {
            yield return g;
        }
        if (!BlueprintApproval.IsPending(__instance))
        {
            yield break;
        }
        Map? map = __instance.MapHeld;
        bool may = map != null && BlueprintApproval.IsApprover(map, LockstepClient.PlayerId);
        Blueprint bp = __instance;
        var approve = new Command_Action
        {
            defaultLabel = "Утвердить",
            defaultDesc = $"Чертёж предложил игрок {BlueprintApprovalComponent.Component?.AuthorOf(bp)}. После утверждения колонисты начнут его строить.",
            icon = TexCommand.ForbidOff,
            action = () => BlueprintApproval.Decide(bp, true),
        };
        var reject = new Command_Action
        {
            defaultLabel = "Отклонить",
            defaultDesc = "Убрать предложенный чертёж.",
            icon = TexCommand.ForbidOn,
            action = () => BlueprintApproval.Decide(bp, false),
        };
        if (!may)
        {
            approve.Disable("Утверждает владелец или соавтор колонии");
            reject.Disable("Отклоняет владелец или соавтор колонии");
        }
        yield return approve;
        yield return reject;
    }
}

[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.GetInspectString))]
internal static class Patch_Blueprint_Inspect_Approval
{
    private static void Postfix(Blueprint __instance, ref string __result)
    {
        if (BlueprintApproval.IsPending(__instance))
        {
            string line = $"Ждёт утверждения владельцем колонии (предложил игрок {BlueprintApprovalComponent.Component?.AuthorOf(__instance)})";
            __result = string.IsNullOrEmpty(__result) ? line : __result + "\n" + line;
        }
    }
}
