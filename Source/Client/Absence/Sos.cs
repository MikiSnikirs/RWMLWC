using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RWMLWC.Client.Absence;

/// <summary>
/// Кнопка «SOS» (T-051): позвать других игроков на помощь. Значок — в ряду переключателей справа внизу, только в
/// сетевой игре. Нажатие — команда мира (операция 24): кто зовёт, карта и клетка, на которую смотрит камера (на
/// карте мира — клетка мира). У всех она исполняется одинаково: письмо «игрок просит помощи» со ссылкой на место
/// (письмо — часть сейва, поэтому у всех и в снимке); у остальных игроков ещё звук и мигание окна в панели задач.
/// Не чаще раза в минуту реального времени от одного игрока (проверка в интерфейсе).
/// </summary>
public static class Sos
{
    internal const byte OpSos = 24;
    private const float CooldownSeconds = 60f;

    private static float _lastSent = -1000f;

    public static int Received { get; private set; }

    /// <summary>Нажатие кнопки (и самопроверка).</summary>
    public static void Send()
    {
        if (!LockstepClient.InSession)
        {
            return;
        }
        float left = _lastSent + CooldownSeconds - Time.realtimeSinceStartup;
        if (left > 0f)
        {
            Messages.Message($"RWMLWC: SOS уже отправлен, повторить можно через {Mathf.CeilToInt(left)} с", MessageTypeDefOf.RejectInput, false);
            return;
        }
        _lastSent = Time.realtimeSinceStartup;
        Map? map = Find.CurrentMap;
        bool world = WorldRendererUtility.WorldSelected || map == null;
        Map anyMap = map ?? Find.Maps[0];
        PacketWriter w = CommandBus.Begin(anyMap);
        w.WriteByte(OpSos);
        if (world)
        {
            PlanetTile tile = Find.WorldSelector.SelectedTile;
            if (!tile.Valid)
            {
                tile = RWMLWC.Client.World.Colonies.Component?.OwnedBy(LockstepClient.PlayerId)?.Tile ?? PlanetTile.Invalid;
            }
            w.WriteInt32(-1);
            ValueCodec.Write(w, IntVec3.Invalid);
            WorldCommands.WriteTile(w, tile);
        }
        else
        {
            w.WriteInt32(map!.uniqueID);
            ValueCodec.Write(w, Find.CameraDriver.MapPosition);
            WorldCommands.WriteTile(w, PlanetTile.Invalid);
        }
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());
        Messages.Message("RWMLWC: SOS отправлен остальным игрокам", MessageTypeDefOf.NeutralEvent, false);
    }

    internal static void Execute(ref PacketReader r)
    {
        int player = CommandBus.CurrentPlayerId;
        int mapId = r.ReadInt32();
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
        IntVec3 cell = (IntVec3)(ValueCodec.Read(ref r, map ?? Find.Maps[0]) ?? IntVec3.Invalid);
        PlanetTile tile = WorldCommands.ReadTile(ref r);
        MapRandContext.Enter(null);

        LookTargets? look = null;
        string where;
        if (map != null && cell.IsValid && cell.InBounds(map))
        {
            look = new LookTargets(new GlobalTargetInfo(cell, map));
            where = map.Parent?.LabelCap ?? ("карта " + map.uniqueID);
        }
        else if (tile.Valid)
        {
            look = new LookTargets(new GlobalTargetInfo(tile));
            where = Find.WorldObjects.WorldObjectAt<WorldObject>(tile)?.LabelCap ?? "карта мира";
        }
        else
        {
            where = "место не указано";
        }
        string name = RWMLWC.Client.SessionView.SessionUi.NameOf(player);
        Find.LetterStack.ReceiveLetter("SOS: " + name, $"Игрок {name} просит помощи ({where}).", LetterDefOf.ThreatSmall, look ?? LookTargets.Invalid, playSound: false);
        Received++;
        TestAutomation.Note($"sos: from player {player}, map {mapId}, cell {cell}, tile {tile}, letters {Find.LetterStack.LettersListForReading.Count}");

        if (player != LockstepClient.PlayerId)
        {
            // Интерфейс этого игрока, не игра: звук и мигание окна.
            try
            {
                LetterDefOf.ThreatBig.arriveSound?.PlayOneShotOnCamera();
            }
            catch (Exception)
            {
            }
            IdleAlarm.FlashWindow();
        }
    }
}

// Значок «SOS» в ряду переключателей справа внизу (рядом с кнопкой окна сессии), только в сетевой игре.
[HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
internal static class Patch_PlaySettings_SosButton
{
    private static void Postfix(WidgetRow row)
    {
        if (row == null || !LockstepClient.InSession)
        {
            return;
        }
        if (row.ButtonIcon(LetterDefOf.ThreatBig.Icon, "RWMLWC: SOS — позвать остальных игроков на помощь сюда (туда, куда смотрит камера)"))
        {
            Sos.Send();
        }
    }
}
