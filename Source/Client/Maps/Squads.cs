using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RWMLWC.Client.Input;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>Отряд: имя, игроки-участники, пешки (по номеру вещи). docs/ARCHITECTURE.md, раздел 12.</summary>
public sealed class Squad : IExposable
{
    public int id;
    public string name = "";
    public List<int> players = new List<int>();
    public List<int> pawnIds = new List<int>();

    public void ExposeData()
    {
        Scribe_Values.Look(ref id, "id");
        Scribe_Values.Look(ref name, "name", "");
        Scribe_Collections.Look(ref players, "players", LookMode.Value);
        Scribe_Collections.Look(ref pawnIds, "pawns", LookMode.Value);
        players ??= new List<int>();
        pawnIds ??= new List<int>();
    }
}

/// <summary>
/// Отряды игры (T-060). Хранятся в сейве, меняются только командами из общего порядка, поэтому
/// одинаковы у всех игроков. Пешка состоит не больше чем в одном отряде.
/// </summary>
public sealed class SquadsComponent : GameComponent
{
    public List<Squad> squads = new List<Squad>();
    public int nextId = 1;

    public SquadsComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref squads, "rwmlwcSquads", LookMode.Deep);
        Scribe_Values.Look(ref nextId, "rwmlwcNextSquadId", 1);
        squads ??= new List<Squad>();
    }

    public Squad? Get(int id) => squads.FirstOrDefault(s => s.id == id);

    public Squad? SquadOf(int pawnId) => squads.FirstOrDefault(s => s.pawnIds.Contains(pawnId));

    /// <summary>Короткая сводка для сравнения у игроков: «1:Имя[игроки|пешки]; …».</summary>
    public string Digest()
    {
        return string.Join("; ", squads.Select(s =>
            $"{s.id}:{s.name}[{string.Join(",", s.players)}|{string.Join(",", s.pawnIds)}]"));
    }
}

/// <summary>Изменения отрядов: отправка командой и исполнение (у всех клиентов в одной точке общего порядка).</summary>
public static class Squads
{
    private const byte OpCreate = 1;
    private const byte OpRename = 2;
    private const byte OpDelete = 3;
    private const byte OpSetMember = 4;
    private const byte OpAssignPawn = 5;
    private const int MaxName = 40;

    public static SquadsComponent? Component => Current.Game?.GetComponent<SquadsComponent>();

    /// <summary>Может ли игрок приказывать пешке: она без отряда или он участник её отряда (раздел 12.1).</summary>
    public static bool CanCommand(int playerId, Pawn pawn)
    {
        Squad? squad = Component?.SquadOf(pawn.thingIDNumber);
        return squad == null || squad.players.Contains(playerId);
    }

    /// <summary>Этот игрок может создавать отряды и переводить пешек на карте (интерфейс; сервер проверяет уровень Full).</summary>
    public static bool CanManage(Map map)
    {
        if (!LockstepClient.InSession)
        {
            return true;
        }
        int owner = MapAccess.OwnerOf(map.uniqueID);
        return MapAccess.MyLevel(map.uniqueID) >= PermissionLevel.Full && (owner <= 0 || owner == LockstepClient.PlayerId);
    }

    public static void Create(Map map, string name) => Send(map, OpCreate, w => w.WriteString(Clip(name)));

    public static void Rename(Map map, int squadId, string name) => Send(map, OpRename, w => { w.WriteInt32(squadId); w.WriteString(Clip(name)); });

    public static void Delete(Map map, int squadId) => Send(map, OpDelete, w => w.WriteInt32(squadId));

    public static void SetMember(Map map, int squadId, int playerId, bool member) =>
        Send(map, OpSetMember, w => { w.WriteInt32(squadId); w.WriteInt32(playerId); w.WriteBool(member); });

    /// <summary>Перевести пешек в отряд; squadId = 0 — убрать из отряда (пешка становится общей).</summary>
    public static void AssignPawns(Map map, int squadId, IEnumerable<Pawn> pawns)
    {
        List<int> ids = pawns.Select(p => p.thingIDNumber).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }
        Send(map, OpAssignPawn, w =>
        {
            w.WriteInt32(squadId);
            w.WriteInt32(ids.Count);
            foreach (int id in ids)
            {
                w.WriteInt32(id);
            }
        });
    }

    private static string Clip(string name)
    {
        name = (name ?? "").Trim();
        return name.Length > MaxName ? name.Substring(0, MaxName) : name;
    }

    private static void Send(Map map, byte op, Action<PacketWriter> body)
    {
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(op);
        body(w);
        if (LockstepClient.InSession)
        {
            CommandBus.Send(map, CommandKinds.SquadEdit, PermissionLevel.Full, w);
            return;
        }
        // Одиночная игра: сразу, тем же кодом.
        PacketReader r = new PacketReader(w.ToArray());
        CommandContext.Read(ref r);
        Execute(map, ref r);
    }

    /// <summary>Исполнение команды SquadEdit. Только состояние отрядов; Rand не трогает.</summary>
    public static void Execute(Map map, ref PacketReader r)
    {
        SquadsComponent? comp = Component;
        if (comp == null)
        {
            TestAutomation.Note("squads: no component in this game");
            return;
        }
        byte op = r.ReadByte();
        switch (op)
        {
            case OpCreate:
                {
                    string name = Clip(r.ReadString());
                    Squad squad = new Squad { id = comp.nextId++ };
                    squad.name = name.Length > 0 ? name : "Отряд " + squad.id;
                    comp.squads.Add(squad);
                    TestAutomation.Note($"squads: created {squad.id} '{squad.name}' by player {CommandBus.CurrentPlayerId}");
                    break;
                }
            case OpRename:
                {
                    int id = r.ReadInt32();
                    string name = Clip(r.ReadString());
                    Squad? squad = comp.Get(id);
                    if (squad != null && name.Length > 0)
                    {
                        squad.name = name;
                    }
                    break;
                }
            case OpDelete:
                {
                    int id = r.ReadInt32();
                    comp.squads.RemoveAll(s => s.id == id); // пешки отряда становятся общими
                    break;
                }
            case OpSetMember:
                {
                    int id = r.ReadInt32();
                    int playerId = r.ReadInt32();
                    bool member = r.ReadBool();
                    Squad? squad = comp.Get(id);
                    if (squad == null || playerId <= 0)
                    {
                        break;
                    }
                    if (member && !squad.players.Contains(playerId))
                    {
                        squad.players.Add(playerId);
                        squad.players.Sort();
                    }
                    else if (!member)
                    {
                        squad.players.Remove(playerId);
                    }
                    break;
                }
            case OpAssignPawn:
                {
                    int id = r.ReadInt32();
                    int count = r.ReadCount(1024, 4);
                    Squad? target = id > 0 ? comp.Get(id) : null;
                    if (id > 0 && target == null)
                    {
                        TestAutomation.Note($"squads: assign to missing squad {id} skipped");
                        for (int i = 0; i < count; i++)
                        {
                            r.ReadInt32();
                        }
                        break;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        int pawnId = r.ReadInt32();
                        // Пешка не больше чем в одном отряде: сначала убрать отовсюду.
                        foreach (Squad s in comp.squads)
                        {
                            s.pawnIds.Remove(pawnId);
                        }
                        if (target != null)
                        {
                            target.pawnIds.Add(pawnId);
                        }
                    }
                    target?.pawnIds.Sort();
                    break;
                }
            default:
                TestAutomation.Note($"squads: unknown op {op}");
                break;
        }
    }

    public static string PawnLabel(int pawnId)
    {
        Pawn? pawn = ValueCodec.FindPawnAnywhere(pawnId);
        return pawn != null ? pawn.LabelShortCap : "#" + pawnId;
    }
}

/// <summary>Окно «Отряды»: список отрядов карты, участники, пешки; перевод выделенных пешек.</summary>
public sealed class Dialog_Squads : Window
{
    private Vector2 scroll;

    public override Vector2 InitialSize => new Vector2(720f, 560f);

    public Dialog_Squads()
    {
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
        draggable = true;
    }

    private static List<int> PlayerIds()
    {
        List<int> ids = SessionView.SessionUi.Players.Select(p => p.PlayerId).ToList();
        foreach (int id in MapAccess.KnownPlayers)
        {
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        if (LockstepClient.PlayerId > 0 && !ids.Contains(LockstepClient.PlayerId))
        {
            ids.Add(LockstepClient.PlayerId);
        }
        ids.Sort();
        return ids;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Map? map = Find.CurrentMap;
        SquadsComponent? comp = Squads.Component;
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "Отряды");
        Text.Font = GameFont.Small;
        if (map == null || comp == null)
        {
            Widgets.Label(new Rect(0f, 40f, inRect.width, 30f), "Нет открытой карты.");
            return;
        }
        bool manage = Squads.CanManage(map);
        List<Pawn> selected = Find.Selector.SelectedPawns.Where(p => p.IsColonist).ToList();
        float y = 40f;
        if (manage && Widgets.ButtonText(new Rect(0f, y, 160f, 30f), "Новый отряд"))
        {
            Squads.Create(map, "");
        }
        Widgets.Label(new Rect(170f, y + 5f, inRect.width - 170f, 30f),
            manage ? $"Выделено колонистов: {selected.Count}. Пешка состоит не больше чем в одном отряде."
                   : "Создавать отряды и переводить пешек может владелец карты.");
        y += 38f;

        List<int> players = PlayerIds();
        float rowHeight = 30f;
        float height = 0f;
        foreach (Squad s in comp.squads)
        {
            height += 4 * rowHeight + Math.Max(1, s.pawnIds.Count) * 24f + 12f;
        }
        height += 2 * rowHeight + 24f * map.mapPawns.FreeColonists.Count;
        Rect outRect = new Rect(0f, y, inRect.width, inRect.height - y);
        Rect view = new Rect(0f, 0f, outRect.width - 16f, height);
        Widgets.BeginScrollView(outRect, ref scroll, view);
        float cy = 0f;
        foreach (Squad s in comp.squads.ToList())
        {
            Widgets.DrawMenuSection(new Rect(0f, cy, view.width, 4 * rowHeight + Math.Max(1, s.pawnIds.Count) * 24f + 4f));
            cy += 4f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(8f, cy, 300f, rowHeight), s.name);
            Text.Font = GameFont.Small;
            if (manage)
            {
                if (Widgets.ButtonText(new Rect(view.width - 330f, cy, 160f, 26f), $"Добавить выделенных ({selected.Count})") && selected.Count > 0)
                {
                    Squads.AssignPawns(map, s.id, selected);
                }
                if (Widgets.ButtonText(new Rect(view.width - 165f, cy, 75f, 26f), "Имя"))
                {
                    Find.WindowStack.Add(new Dialog_SquadName(map, s.id, s.name));
                }
                if (Widgets.ButtonText(new Rect(view.width - 85f, cy, 75f, 26f), "Удалить"))
                {
                    Squads.Delete(map, s.id);
                }
            }
            cy += rowHeight + 4f;
            Widgets.Label(new Rect(8f, cy, 100f, rowHeight), "Игроки:");
            float x = 100f;
            foreach (int playerId in players)
            {
                bool member = s.players.Contains(playerId);
                bool now = member;
                string label = SessionView.SessionUi.NameOf(playerId);
                float w = Math.Min(160f, Text.CalcSize(label).x + 34f);
                Widgets.CheckboxLabeled(new Rect(x, cy, w, 26f), label, ref now, !manage);
                if (now != member && manage)
                {
                    Squads.SetMember(map, s.id, playerId, now);
                }
                x += w + 10f;
            }
            cy += rowHeight;
            Widgets.Label(new Rect(8f, cy, 200f, rowHeight), "Пешки:");
            cy += 24f;
            if (s.pawnIds.Count == 0)
            {
                Widgets.Label(new Rect(24f, cy, 300f, 24f), "нет");
                cy += 24f;
            }
            foreach (int pawnId in s.pawnIds.ToList())
            {
                Widgets.Label(new Rect(24f, cy, 300f, 24f), Squads.PawnLabel(pawnId));
                Pawn? pawn = ValueCodec.FindPawnAnywhere(pawnId);
                if (manage && pawn != null && Widgets.ButtonText(new Rect(330f, cy, 120f, 22f), "Сделать общей"))
                {
                    Squads.AssignPawns(map, 0, new[] { pawn });
                }
                cy += 24f;
            }
            cy += rowHeight + 8f;
        }
        Widgets.Label(new Rect(0f, cy, view.width, rowHeight), "Общие пешки (без отряда) — им приказывает любой игрок с полными правами на карту:");
        cy += rowHeight;
        foreach (Pawn pawn in map.mapPawns.FreeColonists)
        {
            if (comp.SquadOf(pawn.thingIDNumber) == null)
            {
                Widgets.Label(new Rect(24f, cy, 300f, 24f), pawn.LabelShortCap);
                cy += 24f;
            }
        }
        Widgets.EndScrollView();
    }
}

/// <summary>Ввод нового имени отряда.</summary>
public sealed class Dialog_SquadName : Window
{
    private readonly Map map;
    private readonly int squadId;
    private string name;

    public override Vector2 InitialSize => new Vector2(360f, 150f);

    public Dialog_SquadName(Map map, int squadId, string name)
    {
        this.map = map;
        this.squadId = squadId;
        this.name = name;
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnAccept = false;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), "Имя отряда");
        name = Widgets.TextField(new Rect(0f, 32f, inRect.width, 30f), name);
        if (Widgets.ButtonText(new Rect(inRect.width - 120f, inRect.height - 34f, 120f, 32f), "Готово")
            || (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)))
        {
            Squads.Rename(map, squadId, name);
            Close();
        }
    }
}
