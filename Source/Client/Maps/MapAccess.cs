using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Владельцы карт и права игроков (T-033, клиентская часть T-031). Таблицу ведёт сервер и присылает
/// целиком (Permissions); здесь она хранится для интерфейса и для предварительной проверки:
/// команду без прав клиент не отправляет, а сразу говорит игроку. Решает всё равно сервер.
///
/// Все карты загружены у всех игроков, поэтому «перейти в поселение друга» — это просто
/// переключить вид на его карту; управлять на ней можно в пределах выданных прав.
/// </summary>
public static class MapAccess
{
    private sealed class MapEntry
    {
        public int owner;
        public Dictionary<int, PermissionLevel> grants = new Dictionary<int, PermissionLevel>();
        /// <summary>Права «управляющего» (v7): действуют, только пока владельца нет в сети.</summary>
        public HashSet<int> untilReturn = new HashSet<int>();
    }

    private static readonly Dictionary<int, MapEntry> table = new Dictionary<int, MapEntry>();
    private static readonly SortedSet<int> knownPlayers = new SortedSet<int>();

    /// <summary>Таблица получена хотя бы раз (сервер с протоколом v2).</summary>
    public static bool HasTable { get; private set; }

    public static IEnumerable<int> KnownPlayers => knownPlayers;

    public static void Reset()
    {
        table.Clear();
        knownPlayers.Clear();
        HasTable = false;
    }

    /// <summary>Запомнить игрока, о котором стало известно (списка игроков сервер пока не шлёт).</summary>
    public static void NotePlayer(int playerId)
    {
        if (playerId > 0)
        {
            knownPlayers.Add(playerId);
        }
    }

    public static void OnPermissions(PermissionsMessage message)
    {
        table.Clear();
        foreach (MapPermissions map in message.Maps)
        {
            MapEntry entry = new MapEntry { owner = map.OwnerPlayerId };
            NotePlayer(map.OwnerPlayerId);
            foreach (PermissionGrant grant in map.Grants)
            {
                entry.grants[grant.PlayerId] = grant.Level;
                if (grant.UntilReturn)
                {
                    entry.untilReturn.Add(grant.PlayerId);
                }
                NotePlayer(grant.PlayerId);
            }
            table[map.MapId] = entry;
        }
        HasTable = true;
    }

    /// <summary>Владелец карты; 0 — карта свободна.</summary>
    public static int OwnerOf(int mapId)
    {
        return table.TryGetValue(mapId, out MapEntry? entry) ? entry.owner : 0;
    }

    /// <summary>Уровень игрока на карте по правилам сервера: мир и свободная карта открыты всем.</summary>
    public static PermissionLevel LevelOf(int mapId, int playerId)
    {
        if (mapId == ProtocolIds.WorldMapId || !table.TryGetValue(mapId, out MapEntry? entry) || entry.owner <= 0)
        {
            return PermissionLevel.Full;
        }
        if (entry.owner == playerId)
        {
            return PermissionLevel.Full;
        }
        if (!entry.grants.TryGetValue(playerId, out PermissionLevel level))
        {
            return PermissionLevel.None;
        }
        // Управляющий (v7): права «пока меня нет» действуют, только пока владелец не в сети.
        if (entry.untilReturn.Contains(playerId) && SessionView.SessionUi.Players.Any(p => p.PlayerId == entry.owner && p.Online))
        {
            return PermissionLevel.None;
        }
        return level;
    }

    /// <summary>Выданный уровень, как в таблице, без учёта «пока меня нет» (для окна владельца).</summary>
    public static PermissionLevel GrantedLevel(int mapId, int playerId) =>
        table.TryGetValue(mapId, out MapEntry? entry) && entry.grants.TryGetValue(playerId, out PermissionLevel level) ? level : PermissionLevel.None;

    public static PermissionLevel MyLevel(int mapId) => LevelOf(mapId, LockstepClient.PlayerId);

    public static void Claim(Map map)
    {
        LockstepClient.SendRaw(new MapClaimMessage { MapId = map.uniqueID });
    }

    public static void Grant(int mapId, int playerId, PermissionLevel level, bool untilReturn = false)
    {
        LockstepClient.SendRaw(new GrantPermissionMessage { MapId = mapId, PlayerId = playerId, Level = level, UntilReturn = untilReturn });
    }

    /// <summary>Права игрока на карте выданы «только пока владельца нет» (управляющий, v7).</summary>
    public static bool IsUntilReturn(int mapId, int playerId) =>
        table.TryGetValue(mapId, out MapEntry? entry) && entry.untilReturn.Contains(playerId);

    public static string LevelLabel(PermissionLevel level)
    {
        switch (level)
        {
            case PermissionLevel.None: return "нет доступа";
            case PermissionLevel.Observer: return "наблюдатель";
            case PermissionLevel.Build: return "стройка";
            default: return "полный";
        }
    }
}

/// <summary>Окно «Карты и права»: чья карта, мой уровень, переход на карту, заявка, выдача прав.</summary>
public sealed class Dialog_MapAccess : Window
{
    private Vector2 scroll;

    public override Vector2 InitialSize => new Vector2(820f, 480f);

    public Dialog_MapAccess()
    {
        doCloseX = true;
        draggable = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "Карты и права");
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inRect.xMax - 130f, inRect.y, 100f, 30f), "Отряды"))
        {
            Find.WindowStack.Add(new Dialog_Squads());
        }
        float y = inRect.y + 40f;
        if (!LockstepClient.InSession)
        {
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "Сетевая игра не идёт.");
            return;
        }
        Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "Я — " + SessionView.SessionUi.NameOf(LockstepClient.PlayerId) + " (игрок " + LockstepClient.PlayerId + ")" + (MapAccess.HasTable ? "" : " (таблица прав от сервера ещё не получена)"));
        y += 28f;

        List<Map> maps = Find.Maps;
        List<int> others = MapAccess.KnownPlayers.Concat(SessionView.SessionUi.Players.Select(p => p.PlayerId)).Distinct().Where(p => p != LockstepClient.PlayerId).OrderBy(p => p).ToList();
        float viewHeight = 0f;
        foreach (Map map in maps)
        {
            viewHeight += 34f + (MapAccess.OwnerOf(map.uniqueID) == LockstepClient.PlayerId ? others.Count * 30f : 0f) + 8f;
        }
        Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
        Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(viewHeight, outRect.height));
        Widgets.BeginScrollView(outRect, ref scroll, viewRect);
        float cy = 0f;
        foreach (Map map in maps)
        {
            int owner = MapAccess.OwnerOf(map.uniqueID);
            bool mine = owner == LockstepClient.PlayerId;
            string ownerText = owner <= 0 ? "свободна" : (mine ? "моя" : SessionView.SessionUi.NameOf(owner));
            string name = map.Parent != null ? map.Parent.LabelCap : ("Карта " + map.uniqueID);
            Widgets.Label(new Rect(0f, cy + 4f, 250f, 28f), name + (map == Find.CurrentMap ? "  ◄" : ""));
            Widgets.Label(new Rect(255f, cy + 4f, 190f, 28f), ownerText + ", я: " + MapAccess.LevelLabel(MapAccess.MyLevel(map.uniqueID)));
            if (map != Find.CurrentMap && Widgets.ButtonText(new Rect(450f, cy, 75f, 28f), "Перейти"))
            {
                // Переключение вида — только интерфейс этого игрока, на симуляцию не влияет.
                Current.Game.CurrentMap = map;
                Find.CameraDriver.JumpToCurrentMapLoc(map.Center);
            }
            if (owner <= 0 && Widgets.ButtonText(new Rect(530f, cy, 75f, 28f), "Занять"))
            {
                MapAccess.Claim(map);
            }
            cy += 34f;
            if (mine)
            {
                foreach (int player in others)
                {
                    PermissionLevel level = MapAccess.GrantedLevel(map.uniqueID, player);
                    Widgets.Label(new Rect(30f, cy + 4f, 220f, 26f), SessionView.SessionUi.NameOf(player) + ": " + MapAccess.LevelLabel(level));
                    PermissionLevel[] levels = { PermissionLevel.None, PermissionLevel.Observer, PermissionLevel.Build, PermissionLevel.Full };
                    for (int i = 0; i < levels.Length; i++)
                    {
                        Rect button = new Rect(255f + i * 88f, cy, 84f, 26f);
                        if (levels[i] == level)
                        {
                            Widgets.DrawHighlightSelected(button);
                        }
                        if (Widgets.ButtonText(button, MapAccess.LevelLabel(levels[i])) && levels[i] != level)
                        {
                            MapAccess.Grant(map.uniqueID, player, levels[i], MapAccess.IsUntilReturn(map.uniqueID, player));
                        }
                    }
                    // Управляющий: права действуют, только пока меня нет; сервер снимет их, когда я вернусь.
                    bool until = MapAccess.IsUntilReturn(map.uniqueID, player);
                    bool now = until;
                    Rect untilRect = new Rect(255f + levels.Length * 88f + 6f, cy, 150f, 26f);
                    if (level != PermissionLevel.None)
                    {
                        Widgets.CheckboxLabeled(untilRect, "пока меня нет", ref now);
                        TooltipHandler.TipRegion(untilRect, "Права действуют, только пока вас нет в игре; когда вернётесь, сервер их снимет (режим «управляющий»)");
                        if (now != until)
                        {
                            MapAccess.Grant(map.uniqueID, player, level, now);
                        }
                    }
                    cy += 30f;
                }
            }
            cy += 8f;
        }
        Widgets.EndScrollView();
    }
}

// Кнопка в ряду переключателей справа внизу: видна только в сетевой игре.
[HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
internal static class Patch_PlaySettings_DoPlaySettingsGlobalControls
{
    private static void Postfix(WidgetRow row, bool worldView)
    {
        if (worldView || row == null || !LockstepClient.InSession)
        {
            return;
        }
        if (row.ButtonIcon(TexButton.Info, "RWMLWC: карты и права игроков"))
        {
            Dialog_MapAccess? open = Find.WindowStack.WindowOfType<Dialog_MapAccess>();
            if (open != null)
            {
                open.Close();
            }
            else
            {
                Find.WindowStack.Add(new Dialog_MapAccess());
            }
        }
    }
}
