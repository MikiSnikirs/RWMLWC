using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RWMLWC.Client.Sim;
using Verse;

namespace RWMLWC.Client.Maps;

/// <summary>
/// Самопроверка паузы заданий в стазисе (T-043, TST-043; ключ <c>questtest=1</c>). Когда карта поселения замёрзла,
/// в тике у всех создаются два задания с задержкой <see cref="DelayTicks"/>: «map» — со ссылкой на эту карту
/// (часть <c>QuestPart_Filter_AnyColonistAlive</c> с её <c>MapParent</c>), «control» — без ссылки.
/// Ожидание: контрольное срабатывает через <see cref="DelayTicks"/> после создания, «map» — через
/// <see cref="DelayTicks"/> минус время до заморозки после разморозки, а не сразу при ней.
/// Задания находятся по имени, поэтому вернувшийся снимком клиент видит то же.
/// </summary>
internal static class StasisQuestSelfTest
{
    public const int DelayTicks = 3000;
    private const string MapName = "RWMLWC stasis test: map";
    private const string ControlName = "RWMLWC stasis test: control";
    private static readonly HashSet<string> reported = new HashSet<string>();

    public static void Tick()
    {
        QuestManager qm = Find.QuestManager;
        if (!qm.QuestsListForReading.Any(q => q.name == MapName))
        {
            Map? frozen = Find.Maps.FirstOrDefault(m => PerMapSim.IsFrozen(m));
            if (frozen == null)
            {
                return;
            }
            Make(MapName, frozen);
            Make(ControlName, null);
            TestAutomation.Note($"quest test: created on frozen map {frozen.uniqueID} at tick {Find.TickManager.TicksGame}, delay {DelayTicks}");
            return;
        }
        foreach (Quest q in qm.QuestsListForReading.Where(q => q.name == MapName || q.name == ControlName))
        {
            QuestPart_Delay? delay = q.PartsListForReading.OfType<QuestPart_Delay>().FirstOrDefault();
            if (delay != null && delay.State == QuestPartState.Disabled && reported.Add(q.name))
            {
                string frozen = string.Join(",", Find.Maps.Where(m => PerMapSim.IsFrozen(m)).Select(m => m.uniqueID));
                TestAutomation.Note($"quest test: '{q.name}' delay fired at tick {Find.TickManager.TicksGame}, frozen maps [{frozen}]");
            }
        }
    }

    private static void Make(string name, Map? map)
    {
        Quest quest = Quest.MakeRaw();
        quest.name = name;
        quest.hidden = true;
        // Без root игра выбрасывает задание при загрузке (QuestManager.ExposeData), а общий снимок при возвращении
        // игрока — загрузка; не принятому заданию не приходит сигнал запуска (части слушают только Ongoing).
        quest.root = DefDatabase<QuestScriptDef>.AllDefsListForReading.OrderBy(d => d.defName, System.StringComparer.Ordinal).First();
        quest.SetInitiallyAccepted();
        quest.AddPart(new QuestPart_Delay { delayTicks = DelayTicks, inSignalEnable = quest.InitiateSignal });
        if (map != null)
        {
            quest.AddPart(new QuestPart_Filter_AnyColonistAlive { mapParent = map.Parent });
        }
        Find.QuestManager.Add(quest);
        quest.Initiate();
    }
}
