using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Отряды, часть 2 (T-061): игрок приказывает только пешкам без отряда и пешкам своих отрядов.
///
/// Проверка дважды:
///  - при исполнении команды — у всех клиентов одинаково, по состоянию отрядов в сейве и номеру автора;
///    команда с чужой пешкой пропускается у всех, состояние не расходится;
///  - в интерфейсе — кнопки чужих пешек серые, а приказ не уходит на сервер (сообщение игроку).
/// </summary>
public static class SquadRules
{
    private static readonly Dictionary<Type, FieldInfo?> pawnFields = new Dictionary<Type, FieldInfo?>();

    /// <summary>Сколько команд пропущено из-за отрядов (для сводки теста).</summary>
    public static int Rejected { get; private set; }

    /// <summary>Только для тестов: отправлять команды без проверки в интерфейсе, чтобы проверить исполнение.</summary>
    public static bool BypassLocalCheck;

    public static void Reset()
    {
        Rejected = 0;
    }

    /// <summary>Пешка, которой принадлежит объект: сама пешка или её часть (настройки работ, расписание, политики…).</summary>
    public static Pawn? PawnOf(object? obj)
    {
        if (obj is Pawn pawn)
        {
            return pawn;
        }
        if (obj == null || obj is Thing)
        {
            return null;
        }
        Type type = obj.GetType();
        if (!pawnFields.TryGetValue(type, out FieldInfo? field))
        {
            field = AccessTools.Field(type, "pawn");
            if (field != null && !typeof(Pawn).IsAssignableFrom(field.FieldType))
            {
                field = null;
            }
            pawnFields[type] = field;
        }
        return field?.GetValue(obj) as Pawn;
    }

    /// <summary>Почему игрок не может приказывать этой пешке; null — может.</summary>
    public static string? Forbidden(Pawn? pawn, int playerId)
    {
        if (pawn == null)
        {
            return null;
        }
        Squad? squad = Squads.Component?.SquadOf(pawn.thingIDNumber);
        if (squad == null || squad.players.Contains(playerId))
        {
            return null;
        }
        return $"{pawn.LabelShortCap} — пешка отряда «{squad.name}»";
    }

    /// <summary>Первая чужая для игрока пешка среди объектов (выделение автора).</summary>
    public static string? ForbiddenAny(IEnumerable<object> objects, int playerId)
    {
        foreach (object obj in objects)
        {
            string? reason = Forbidden(PawnOf(obj), playerId);
            if (reason != null)
            {
                return reason;
            }
        }
        return null;
    }

    /// <summary>
    /// При исполнении: пропустить команду, если она касается чужой для автора пешки. Вызывается у всех клиентов
    /// в одной точке общего порядка; результат зависит только от сейва и номера автора.
    /// </summary>
    public static bool RejectAtExecution(string? reason, long seqForLog)
    {
        if (reason == null)
        {
            return false;
        }
        Rejected++;
        TestAutomation.Note($"command #{seqForLog}: rejected: squad ({reason}), author {CommandBus.CurrentPlayerId}");
        if (!CommandBus.ExecutingRemote && Current.ProgramState == ProgramState.Playing)
        {
            Messages.Message("RWMLWC: приказ отменён — " + reason, MessageTypeDefOf.RejectInput, false);
        }
        return true;
    }

    /// <summary>В интерфейсе: можно ли отправлять приказ этим пешкам. false — сообщение уже показано.</summary>
    public static bool LocalMay(IEnumerable<object> objects)
    {
        if (BypassLocalCheck || !LockstepClient.InSession)
        {
            return true;
        }
        string? reason = ForbiddenAny(objects, LockstepClient.PlayerId);
        if (reason == null)
        {
            return true;
        }
        TestAutomation.Note("squads: blocked locally: " + reason);
        Messages.Message("RWMLWC: приказывать может только участник отряда — " + reason, MessageTypeDefOf.RejectInput, false);
        return false;
    }

    public static bool LocalMay(object? obj) => LocalMay(obj == null ? Array.Empty<object>() : new[] { obj });
}

/// <summary>Кнопки приказов чужой пешки — серые с подсказкой. Только интерфейс: при исполнении команд не действует.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
internal static class Patch_Pawn_GetGizmos_Squads
{
    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
    {
        string? reason = CommandBus.CaptureActive && LockstepClient.InSession && !SquadRules.BypassLocalCheck
            ? SquadRules.Forbidden(__instance, LockstepClient.PlayerId)
            : null;
        foreach (Gizmo gizmo in gizmos)
        {
            if (reason != null && gizmo is Command && !gizmo.Disabled)
            {
                gizmo.Disable(reason);
            }
            yield return gizmo;
        }
    }
}
