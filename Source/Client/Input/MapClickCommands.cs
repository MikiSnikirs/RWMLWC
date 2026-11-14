using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Правый щелчок по карте (T-022). Игра по щелчку строит список приказов для выделенных пешек
/// (FloatMenuMakerMap.GetOptions) и либо сразу исполняет единственный очевидный приказ, либо
/// показывает меню. В сессии щелчок уходит командой «щелчок в такой-то точке» вместе с выделением
/// автора; каждый клиент строит список сам и делает то же, что сделала бы игра. Меню продолжается
/// через VirtualUi (у автора — настоящее, у остальных — запомненные действия).
///
/// Перемещение группы призванных пешек — отдельный случай: игра ведёт его интерактивно
/// (расстановка перетаскиванием), поэтому оно остаётся в интерфейсе автора, а перехватывается
/// итог — приказ каждой пешке идти в свою клетку.
/// </summary>
public static class MapClickCommands
{
    public static int Captured { get; private set; }

    // ---------- перехват ----------

    /// <summary>Из префикса Selector.HandleMapClicks. true — выполнить оригинал.</summary>
    public static bool InterceptMapClick(Selector selector)
    {
        if (!CommandBus.CaptureActive)
        {
            return true;
        }
        Event ev = Event.current;
        if (ev == null || ev.type != EventType.mouseDown || ev.button != 1)
        {
            return true; // выделение рамкой, отпускание кнопки и прочее — как обычно
        }
        List<Pawn> pawns = selector.SelectedPawns;
        Map map = Find.CurrentMap;
        if (pawns.Count == 0 || map == null)
        {
            return true;
        }
        Vector3 clickPos = UI.MouseMapPosition();
        List<FloatMenuOption>? options = null;
        FloatMenuContext? context = null;
        try
        {
            options = FloatMenuMakerMap.GetOptions(pawns, clickPos, out context);
        }
        catch (Exception ex)
        {
            Log.Error("[RWMLWC] правый щелчок: ошибка построения приказов: " + ex);
        }
        bool empty = options == null || options.Count == 0;
        if (IsGroupGoto(options, context))
        {
            return true; // интерактивная расстановка группы; итоговые приказы перехватит PawnGotoAction
        }
        ev.Use();
        if (empty)
        {
            return false;
        }
        Send(map, clickPos);
        return false;
    }

    private static bool IsGroupGoto(List<FloatMenuOption>? options, FloatMenuContext? context)
    {
        if (context == null || !context.IsMultiselect)
        {
            return false;
        }
        if (options == null || options.Count == 0)
        {
            return true;
        }
        return context.ValidSelectedPawns.Count() > 1 && options.Count == 1 && options[0].isGoto;
    }

    /// <summary>Отправить правый щелчок в точке карты (также для тестовых сценариев).</summary>
    public static void Send(Map map, Vector3 clickPos)
    {
        PacketWriter w = CommandBus.Begin(map, clickPos);
        WriteFloat(w, clickPos.x);
        WriteFloat(w, clickPos.y);
        WriteFloat(w, clickPos.z);
        CommandBus.Send(map, CommandKinds.MapRightClick, PermissionLevel.Full, w);
        Captured++;
    }

    /// <summary>Из префикса FloatMenuOptionProvider_DraftedMove.PawnGotoAction. true — выполнить оригинал.</summary>
    public static bool InterceptGoto(IntVec3 clickCell, Pawn pawn, IntVec3 gotoLoc)
    {
        if (!CommandBus.CaptureActive || pawn == null || !pawn.Spawned)
        {
            return true;
        }
        Map map = pawn.Map;
        PacketWriter w = CommandBus.Begin(map);
        w.WriteInt32(pawn.thingIDNumber);
        ValueCodec.WriteCell(w, clickCell);
        ValueCodec.WriteCell(w, gotoLoc);
        CommandBus.Send(map, CommandKinds.PawnGoto, PermissionLevel.Full, w);
        Captured++;
        return false;
    }

    // ---------- исполнение ----------

    public static void ExecuteRightClick(Map map, ref PacketReader r)
    {
        float x = ReadFloat(ref r);
        float y = ReadFloat(ref r);
        float z = ReadFloat(ref r);
        Vector3 clickPos = new Vector3(x, y, z);

        // Выделение автора уже подставлено (CommandBus).
        List<Pawn> pawns = new List<Pawn>(Find.Selector.SelectedPawns);
        if (pawns.Count == 0)
        {
            return;
        }
        List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions(pawns, clickPos, out FloatMenuContext context);
        if (options == null || options.Count == 0 || IsGroupGoto(options, context))
        {
            return; // пока команда шла, приказ стал невозможен
        }
        FloatMenuOption autoTake = FloatMenuMakerMap.GetAutoTakeOption(options);
        if (autoTake != null)
        {
            autoTake.Chosen(true, null);
            return;
        }
        string? title = !context.IsMultiselect ? (string)context.FirstSelectedPawn.LabelCap : null;
        FloatMenuMap menu = new FloatMenuMap(options, title, clickPos);
        menu.givesColonistOrders = true;
        Find.WindowStack.Add(menu); // у автора — настоящее меню, у остальных — запомненные действия (VirtualUi)
    }

    public static void ExecuteGoto(Map map, ref PacketReader r)
    {
        int pawnId = r.ReadInt32();
        IntVec3 clickCell = ValueCodec.ReadCell(ref r);
        IntVec3 gotoLoc = ValueCodec.ReadCell(ref r);
        if (!(ValueCodec.FindThing(map, pawnId) is Pawn pawn) || !pawn.Drafted || !gotoLoc.InBounds(map))
        {
            return;
        }
        FloatMenuOptionProvider_DraftedMove.PawnGotoAction(clickCell, pawn, gotoLoc);
    }

    private static void WriteFloat(PacketWriter w, float value) => w.WriteInt32(BitConverter.SingleToInt32Bits(value));

    private static float ReadFloat(ref PacketReader r) => BitConverter.Int32BitsToSingle(r.ReadInt32());
}

[HarmonyPatch(typeof(Selector), "HandleMapClicks")]
internal static class Patch_Selector_HandleMapClicks
{
    private static bool Prefix(Selector __instance) => MapClickCommands.InterceptMapClick(__instance);
}

[HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
internal static class Patch_DraftedMove_PawnGotoAction
{
    private static bool Prefix(IntVec3 clickCell, Pawn pawn, IntVec3 gotoLoc) => MapClickCommands.InterceptGoto(clickCell, pawn, gotoLoc);
}
