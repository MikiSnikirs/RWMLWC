using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RWMLWC.Client.Sim;
using RWMLWC.Shared.Protocol;
using Verse;

namespace RWMLWC.Client.Input;

/// <summary>
/// Выбор в письме (ChoiceLetter: «принять странника», «впустить беженцев», «принять задание»…) в сессии — командой
/// (операция 51). В игре действие варианта (сигнал задания, принятие пешки) исполнялось только у нажавшего — в сети
/// это расхождение. Варианты окна письма запоминают (номер письма, порядковый номер варианта); нажатие закрывает окно
/// на месте, а действие уходит командой, и у всех исполняется действие того же варианта того же письма.
/// </summary>
public static class LetterChoices
{
    public const byte OpChoice = 51;
    private static readonly ConditionalWeakTable<DiaOption, Tag> tags = new ConditionalWeakTable<DiaOption, Tag>();
    internal static bool Executing;

    private sealed class Tag
    {
        public int letterId;
        public int index;
    }

    internal static void Remember(ChoiceLetter letter, DiaNode node)
    {
        if (letter.GetType() == typeof(StandardLetter))
        {
            return; // только «закрыть» и «перейти» — интерфейс, не симуляция
        }
        for (int i = 0; i < node.options.Count; i++)
        {
            tags.AddOrUpdate(node.options[i], new Tag { letterId = letter.ID, index = i });
        }
    }

    internal static bool TryGet(DiaOption option, out int letterId, out int index)
    {
        letterId = index = -1;
        if (tags.TryGetValue(option, out Tag? t))
        {
            letterId = t.letterId;
            index = t.index;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Предложение письма истекло: задание письма уже не идёт (у «Пополнения» срок 60000 тиков, после — провал).
    /// В одиночной игре окно письма ставит игру на паузу и истечь при открытом окне предложение не может; в сети игра
    /// идёт, и нажатое после срока «Принять» отправляло сигнал завершённому заданию — пешка не приходила, а игрок не
    /// понимал почему. Состояние задания общее у всех, поэтому проверка детерминирована.
    /// </summary>
    internal static bool Expired(ChoiceLetter letter)
    {
        Quest? q = letter.quest;
        return q != null && q.State != QuestState.Ongoing && q.State != QuestState.NotYetAccepted;
    }

    internal static ChoiceLetter? Find(int letterId) =>
        Verse.Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>().FirstOrDefault(l => l.ID == letterId)
        ?? Verse.Find.Archive.ArchivablesListForReading.OfType<ChoiceLetter>().FirstOrDefault(l => l.ID == letterId);

    internal static void ExpiredMessage(ChoiceLetter letter) =>
        Messages.Message($"RWMLWC: предложение «{letter.Label}» уже истекло — выбор не принят", MessageTypeDefOf.RejectInput, false);

    internal static void Send(int letterId, int index) => WorldCommands.SendOp(OpChoice, w => { w.WriteInt32(letterId); w.WriteInt32(index); });

    public static void Execute(ref PacketReader r)
    {
        int letterId = r.ReadInt32();
        int index = r.ReadInt32();
        ChoiceLetter? letter = Find(letterId);
        if (letter == null)
        {
            TestAutomation.Note($"letter choice: letter {letterId} not found (option {index}), dropped");
            return;
        }
        if (Expired(letter))
        {
            TestAutomation.Note($"letter choice: letter {letterId} '{letter.Label}' option {index} by player {CommandBus.CurrentPlayerId} at tick {Verse.Find.TickManager.TicksGame}: quest {letter.quest.id} already {letter.quest.State}, dropped");
            if (CommandBus.CurrentPlayerId == LockstepClient.PlayerId)
            {
                ExpiredMessage(letter);
            }
            return;
        }
        // Варианты строим как игра; у игрока без карты письма (режим заглушки) это может упасть — тогда действие
        // писем с сигналами (принять/отклонить: signalAccept/signalReject) исполняется напрямую, как в коде игры.
        // Выключенность варианта не проверяем: её проверил нажавший (у него карта есть).
        System.Action? act = null;
        try
        {
            DiaOption? option = letter.Choices.ElementAtOrDefault(index);
            act = option == null ? null : Traverse.Create(option).Field("action").GetValue<System.Action>();
        }
        catch (System.Exception)
        {
            act = null;
        }
        if (act == null)
        {
            string? field = index == 0 ? "signalAccept" : index == 1 ? "signalReject" : null;
            string? signal = field == null ? null : Traverse.Create(letter).Field(field).GetValue<string>();
            if (signal != null)
            {
                act = () => { Verse.Find.SignalManager.SendSignal(new Signal(signal)); Verse.Find.LetterStack.RemoveLetter(letter); };
            }
        }
        if (act == null)
        {
            TestAutomation.Note($"letter choice: letter {letterId} option {index} has no action here, dropped");
            return;
        }
        Executing = true;
        try
        {
            act.Invoke();
        }
        finally
        {
            Executing = false;
        }
        TestAutomation.Note($"letter choice: letter {letterId} '{letter.Label}' option {index} by player {CommandBus.CurrentPlayerId} at tick {Verse.Find.TickManager.TicksGame}");
        // Окно письма у других закрыть: выбор сделан.
        foreach (Dialog_NodeTree w in Verse.Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList())
        {
            DiaNode? node = Traverse.Create(w).Field("curNode").GetValue<DiaNode>();
            if (node != null && node.options.Any(o => TryGet(o, out int id, out _) && id == letterId))
            {
                w.Close(doCloseSound: false);
            }
        }
    }
}

[HarmonyPatch(typeof(ChoiceLetter), nameof(ChoiceLetter.OpenLetter))]
internal static class Patch_ChoiceLetter_OpenLetter_Remember
{
    private static void Postfix(ChoiceLetter __instance)
    {
        if (!LockstepClient.InSession)
        {
            return;
        }
        Dialog_NodeTree? w = Find.WindowStack.Windows.OfType<Dialog_NodeTree>().LastOrDefault();
        DiaNode? node = w == null ? null : Traverse.Create(w).Field("curNode").GetValue<DiaNode>();
        if (node != null)
        {
            LetterChoices.Remember(__instance, node);
        }
    }
}

[HarmonyPatch(typeof(DiaOption), "Activate")]
internal static class Patch_DiaOption_Activate_Letter
{
    private static void Prefix(DiaOption __instance, out System.Action? __state)
    {
        __state = null;
        if (!LockstepClient.InSession || LetterChoices.Executing || !LetterChoices.TryGet(__instance, out int letterId, out int index))
        {
            return;
        }
        // Окно закрывается на месте, действие — командой у всех.
        Traverse t = Traverse.Create(__instance).Field("action");
        __state = t.GetValue<System.Action>();
        t.SetValue(null);
        ChoiceLetter? letter = LetterChoices.Find(letterId);
        if (letter != null && LetterChoices.Expired(letter))
        {
            LetterChoices.ExpiredMessage(letter);
            return;
        }
        LetterChoices.Send(letterId, index);
    }

    private static void Finalizer(DiaOption __instance, System.Action? __state)
    {
        if (__state != null)
        {
            Traverse.Create(__instance).Field("action").SetValue(__state);
        }
    }
}

[HarmonyPatch(typeof(ChoiceLetter), nameof(ChoiceLetter.OpenLetter))]
internal static class Patch_ChoiceLetter_OpenLetter_MissingMap
{
    // Письмо, чьи варианты не строятся у этого игрока (режим заглушки: письмо задания для чужой колонии, её карты нет —
    // ChoiceLetter_AcceptJoiner.MapToUse падал в LetterStackTick → OpenAutomaticLetters, sr29), не открывается:
    // выбор делает владелец колонии.
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ChoiceLetter __instance)
    {
        if (!LockstepClient.InSession)
        {
            return true;
        }
        try
        {
            _ = __instance.Choices.ToList();
            return true;
        }
        catch (System.Exception)
        {
            TestAutomation.Note($"letter {__instance.ID} '{__instance.Label}' not opened here: its choices need a map this player does not have");
            return false;
        }
    }
}

[HarmonyPatch(typeof(Window), nameof(Window.WindowUpdate))]
internal static class Patch_Window_WindowUpdate_LetterExpired
{
    // Открытое окно письма, чьё предложение истекло, пока окно было открыто (игра в сети не на паузе), закрывается
    // с сообщением — как исчезает само письмо.
    private static void Postfix(Window __instance)
    {
        if (!LockstepClient.InSession || __instance is not Dialog_NodeTree w || Current.Game == null)
        {
            return;
        }
        DiaNode? node = Traverse.Create(w).Field("curNode").GetValue<DiaNode>();
        if (node == null || node.options.Count == 0 || !LetterChoices.TryGet(node.options[0], out int id, out _))
        {
            return;
        }
        ChoiceLetter? letter = LetterChoices.Find(id);
        if (letter != null && LetterChoices.Expired(letter))
        {
            w.Close(doCloseSound: false);
            LetterChoices.ExpiredMessage(letter);
        }
    }
}
