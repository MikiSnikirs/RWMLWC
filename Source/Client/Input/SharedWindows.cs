using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using UnityEngine;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Общие окна (решение владельца 15.11). Окно, которое открывается при исполнении команды (кнопка «Разделить
/// караван», «Переформировать», «Сформировать караван», подтверждения, ползунок количества), создаётся у всех
/// клиентов под одним номером «автор, номер». Автор его видит; остальным предлагается «Присоединиться» —
/// и окно показывается и у них. Дальше оба меняют и оба могут нажать:
///  - количества в окнах со списком (поле <c>transferables</c>) расходятся командой с точным значением
///    по номеру вещи — порядок не важен, у всех одно и то же;
///  - кнопки подтверждения и ползунка — командой: у всех исполняется действие кнопки их собственного окна;
///  - «Принять» в окнах со списком уже идёт командой (разделить, переформировать, сформировать караван);
///  - закрыл автор — окно закрывается у всех; закрыл присоединившийся — только у него (можно вернуться).
/// Окно торговли сюда не входит: торговля в сессии — T-036.
/// </summary>
public static class SharedWindows
{
    internal const byte OpCount = 9;
    internal const byte OpButton = 10;
    internal const byte OpClose = 11;
    internal const byte OpSlider = 12;

    public sealed class Entry
    {
        public int author;
        public int serial;
        public Window window = null!;
        public int mapId = -1;
        public FieldInfo? transferablesField;
        public Action?[] buttons = new Action?[5]; // A, B, C, принять (Enter), отмена (Esc)
        public Action<int>? slider;
        public bool closed;
        public bool shownHere;
        public readonly Dictionary<int, int> counts = new Dictionary<int, int>(); // номер вещи → количество

        public string Title => window is Dialog_MessageBox box && !box.title.NullOrEmpty() ? box.title : WindowName(window);

        public List<TransferableOneWay>? Transferables => transferablesField?.GetValue(window) as List<TransferableOneWay>;
    }

    private static readonly Dictionary<(int, int), Entry> entries = new Dictionary<(int, int), Entry>();
    private static readonly Dictionary<int, int> nextSerial = new Dictionary<int, int>();
    private static readonly FieldInfo SliderAction = AccessTools.Field(typeof(Dialog_Slider), "confirmAction");
    private static readonly FieldInfo SliderValue = AccessTools.Field(typeof(Dialog_Slider), "curValue");
    private static bool applying;
    private static Game? game;

    // Номера окон у всех одинаковы, пока все исполняют одни и те же команды одной игры. Новая игра (загрузка
    // общего снимка — у всех сразу) — реестр с нуля.
    private static void EnsureGame()
    {
        if (!ReferenceEquals(game, Current.Game))
        {
            game = Current.Game;
            entries.Clear();
            nextSerial.Clear();
        }
    }

    public static IEnumerable<Entry> Open => entries.Values.Where(e => !e.closed);

    private static FieldInfo? TransferablesFieldOf(Window window)
    {
        for (Type? t = window.GetType(); t != null && t != typeof(Window); t = t.BaseType)
        {
            FieldInfo? f = t.GetField("transferables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null && f.FieldType == typeof(List<TransferableOneWay>))
            {
                return f;
            }
        }
        return null;
    }

    private static bool Supported(Window window) =>
        window is Dialog_MessageBox || window is Dialog_Slider || (!(window is Dialog_Trade) && TransferablesFieldOf(window) != null);

    /// <summary>
    /// Из VirtualUi.OnWindowAdd при исполнении команды. null — окно не из общих (как раньше); иначе — показывать ли его здесь.
    /// </summary>
    public static bool? Register(Window window)
    {
        if (!CommandBus.Executing || !LockstepClient.InSession || !Supported(window))
        {
            return null;
        }
        EnsureGame();
        int author = CommandBus.CurrentPlayerId;
        int serial = (nextSerial.TryGetValue(author, out int s) ? s : 0) + 1;
        nextSerial[author] = serial;
        Entry e = new Entry
        {
            author = author,
            serial = serial,
            window = window,
            mapId = MapRandContext.Current?.uniqueID ?? -1,
            transferablesField = TransferablesFieldOf(window),
        };
        if (window is Dialog_MessageBox box)
        {
            e.buttons[0] = box.buttonAAction;
            e.buttons[1] = box.buttonBAction;
            e.buttons[2] = box.buttonCAction;
            e.buttons[3] = box.acceptAction;
            e.buttons[4] = box.cancelAction;
            // Кнопка у любого, кто видит окно, уходит командой; у всех исполняется действие кнопки своего окна.
            if (box.buttonAAction != null) box.buttonAAction = () => SendButton(e, 0);
            if (box.buttonBAction != null) box.buttonBAction = () => SendButton(e, 1);
            if (box.buttonCAction != null) box.buttonCAction = () => SendButton(e, 2);
            if (box.acceptAction != null) box.acceptAction = () => SendButton(e, 3);
            if (box.cancelAction != null) box.cancelAction = () => SendButton(e, 4);
        }
        else if (window is Dialog_Slider slider)
        {
            e.slider = (Action<int>?)SliderAction.GetValue(slider);
            SliderAction.SetValue(slider, (Action<int>)(v => SendSlider(e, v)));
        }
        entries[(author, serial)] = e;
        bool own = author == LockstepClient.PlayerId;
        e.shownHere = own;
        TestAutomation.Note($"shared window: {WindowName(window)} #{author}.{serial} opened by player {author}{(own ? " (here)" : ", can join")}");
        return own;
    }

    /// <summary>Присоединиться: показать окно здесь, с уже выбранными количествами.</summary>
    public static void Join(Entry e)
    {
        if (e.closed || e.shownHere)
        {
            return;
        }
        e.shownHere = true;
        Find.WindowStack.Add(e.window);
        ApplyCounts(e);
        TestAutomation.Note($"shared window: joined {WindowName(e.window)} #{e.author}.{e.serial}");
    }

    // ---------- отправка ----------

    private static PacketWriter Begin(Entry e, byte op)
    {
        Map map = Find.CurrentMap ?? Find.Maps[0];
        PacketWriter w = CommandBus.Begin(map);
        w.WriteByte(op);
        w.WriteInt32(e.author);
        w.WriteInt32(e.serial);
        return w;
    }

    private static void Send(PacketWriter w) =>
        LockstepClient.SendCommand(ProtocolIds.WorldMapId, CommandKinds.WorldAction, PermissionLevel.Full, w.ToArray());

    private static void SendButton(Entry e, int which)
    {
        PacketWriter w = Begin(e, OpButton);
        w.WriteInt32(which);
        Send(w);
    }

    private static void SendSlider(Entry e, int value)
    {
        PacketWriter w = Begin(e, OpSlider);
        w.WriteInt32(value);
        Send(w);
    }

    /// <summary>Из сеттера количества: окно общее и видно здесь — значение уходит всем.</summary>
    internal static void OnCountSet(TransferableOneWay t, int value)
    {
        if (applying || CommandBus.Executing || !LockstepClient.InSession || !t.HasAnyThing || value == t.CountToTransfer)
        {
            return;
        }
        foreach (Entry e in entries.Values)
        {
            if (e.closed || !e.shownHere || e.Transferables is not List<TransferableOneWay> list || !list.Contains(t))
            {
                continue;
            }
            PacketWriter w = Begin(e, OpCount);
            w.WriteInt32(t.AnyThing.thingIDNumber);
            w.WriteInt32(value);
            Send(w);
            return;
        }
    }

    /// <summary>Из Window.PostClose: автор закрыл — у всех; присоединившийся — только у себя.</summary>
    internal static void OnClosed(Window window)
    {
        if (CommandBus.Executing)
        {
            return;
        }
        foreach (Entry e in entries.Values)
        {
            if (!ReferenceEquals(e.window, window) || e.closed)
            {
                continue;
            }
            e.shownHere = false;
            if (e.author == LockstepClient.PlayerId)
            {
                Send(Begin(e, OpClose));
            }
            return;
        }
    }

    // ---------- исполнение (у всех, в общем порядке) ----------

    public static void Execute(byte op, ref PacketReader r)
    {
        EnsureGame();
        int author = r.ReadInt32();
        int serial = r.ReadInt32();
        entries.TryGetValue((author, serial), out Entry? e);
        switch (op)
        {
            case OpCount:
                {
                    int thingId = r.ReadInt32();
                    int value = r.ReadInt32();
                    if (e == null || e.closed) return;
                    e.counts[thingId] = value;
                    ApplyCounts(e);
                    break;
                }
            case OpButton:
                {
                    int which = r.ReadInt32();
                    if (e == null || e.closed || which < 0 || which >= e.buttons.Length) return;
                    Close(e);
                    Run(e, () => e.buttons[which]?.Invoke());
                    TestAutomation.Note($"shared window: button {which} of #{author}.{serial} pressed by player {CommandBus.CurrentPlayerId}");
                    break;
                }
            case OpSlider:
                {
                    int value = r.ReadInt32();
                    if (e == null || e.closed) return;
                    Close(e);
                    Run(e, () => e.slider?.Invoke(value));
                    TestAutomation.Note($"shared window: slider #{author}.{serial} = {value} by player {CommandBus.CurrentPlayerId}");
                    break;
                }
            case OpClose:
                if (e == null || e.closed) return;
                Close(e);
                TestAutomation.Note($"shared window: #{author}.{serial} closed by player {CommandBus.CurrentPlayerId}");
                break;
        }
    }

    // Действие кнопки — в обстановке, где окно открылось: Rand и номера вещей карты (или глобальные).
    private static void Run(Entry e, Action action)
    {
        Map? map = Find.Maps.FirstOrDefault(m => m.uniqueID == e.mapId);
        MapRandContext.Enter(map);
        action();
    }

    private static void Close(Entry e)
    {
        e.closed = true;
        if (e.shownHere)
        {
            e.shownHere = false;
            applying = true;
            try
            {
                Find.WindowStack.TryRemove(e.window, doCloseSound: false);
            }
            finally
            {
                applying = false;
            }
        }
    }

    private static void ApplyCounts(Entry e)
    {
        if (e.Transferables is not List<TransferableOneWay> list)
        {
            return;
        }
        applying = true;
        try
        {
            foreach (KeyValuePair<int, int> kv in e.counts)
            {
                TransferableOneWay? t = list.FirstOrDefault(x => x.things.Any(th => th.thingIDNumber == kv.Key));
                if (t != null && t.CountToTransfer != kv.Value && t.CanAdjustTo(kv.Value).Accepted)
                {
                    t.AdjustTo(kv.Value);
                }
            }
        }
        finally
        {
            applying = false;
        }
    }

    internal static bool Applying => applying;

    /// <summary>
    /// Действие окна исполнено командой (караван разделён, сформирован…): окно закрывается у всех, кто его видит.
    /// Вызывается при исполнении — у всех клиентов.
    /// </summary>
    public static void CloseMatching(Func<Window, bool> match)
    {
        EnsureGame();
        foreach (Entry e in entries.Values.ToList())
        {
            if (!e.closed && match(e.window))
            {
                Close(e);
                TestAutomation.Note($"shared window: #{e.author}.{e.serial} closed — its action was executed");
            }
        }
    }

    /// <summary>Сбросить при выходе из сессии/загрузке снимка.</summary>
    public static void Reset()
    {
        entries.Clear();
        nextSerial.Clear();
    }

    public static string WindowName(Window w) => w switch
    {
        RimWorld.Planet.Dialog_SplitCaravan => "Разделить караван",
        Dialog_FormCaravan => "Караван",
        Dialog_MessageBox => "Подтверждение",
        Dialog_Slider => "Количество",
        _ => w.GetType().Name,
    };

    /// <summary>Раз в кадр: приглашения в чужие окна.</summary>
    public static void Frame()
    {
        if (!LockstepClient.InSession || Current.Game == null)
        {
            return;
        }
        EnsureGame();
        bool any = Open.Any(e => !e.shownHere && e.author != LockstepClient.PlayerId);
        if (any && !Find.WindowStack.IsOpen<Dialog_SharedInvite>())
        {
            Find.WindowStack.Add(new Dialog_SharedInvite());
        }
    }
}

/// <summary>Небольшое окно-приглашение: «Игрок X открыл окно … — Присоединиться».</summary>
public sealed class Dialog_SharedInvite : Window
{
    public override Vector2 InitialSize => new Vector2(420f, 120f);

    public Dialog_SharedInvite()
    {
        layer = WindowLayer.GameUI;
        preventCameraMotion = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        doCloseX = true;
        draggable = true;
    }

    protected override void SetInitialSizeAndPosition()
    {
        windowRect = new Rect((UI.screenWidth - InitialSize.x) / 2f, 60f, InitialSize.x, InitialSize.y);
    }

    public override void DoWindowContents(Rect inRect)
    {
        List<SharedWindows.Entry> invites = SharedWindows.Open.Where(e => !e.shownHere && e.author != LockstepClient.PlayerId).ToList();
        if (invites.Count == 0)
        {
            Close(false);
            return;
        }
        float y = inRect.y;
        foreach (SharedWindows.Entry e in invites.Take(3))
        {
            Widgets.Label(new Rect(inRect.x, y + 4f, inRect.width - 150f, 26f),
                $"{RWMLWC.Client.SessionView.SessionUi.NameOf(e.author)}: «{e.Title}»");
            if (Widgets.ButtonText(new Rect(inRect.xMax - 140f, y, 140f, 28f), "Присоединиться"))
            {
                SharedWindows.Join(e);
            }
            y += 32f;
        }
    }
}

[HarmonyPatch(typeof(TransferableOneWay), nameof(TransferableOneWay.CountToTransfer), MethodType.Setter)]
internal static class Patch_TransferableOneWay_CountToTransfer
{
    private static void Prefix(TransferableOneWay __instance, int value)
    {
        SharedWindows.OnCountSet(__instance, value);
    }
}

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.TryRemove), new[] { typeof(Window), typeof(bool) })]
internal static class Patch_WindowStack_TryRemove_Shared
{
    private static void Postfix(Window window, bool __result)
    {
        if (__result && !SharedWindows.Applying)
        {
            SharedWindows.OnClosed(window);
        }
    }
}
