using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Единая точка правды на право чата получать десктопную грань —
/// <see cref="SessionManager.DesktopTurnEligible"/> (ADR-008: «Грань не доставляется в ходы
/// исполнения задач, отложенные и регулярные чаты, групповые чаты»). Раньше рядом с живым
/// инлайн-правилом в BuildDesktopContext существовал мёртвый дубль DesktopTurnEligibility
/// с ДРУГИМ предикатом участников (Count &gt; 1 вместо Count &gt; 0) — этот набор фиксирует
/// выбранную форму, чтобы следующая правка не могла тихо разъехаться с ADR.
/// </summary>
public class DesktopTurnEligibleTests
{
    private static Session Chat(bool desktop = true, bool taskExecution = false,
        string? taskId = null, string? automationRuleId = null, List<string>? participants = null) => new()
    {
        DesktopChat = desktop,
        TaskExecution = taskExecution,
        TaskId = taskId,
        AutomationRuleId = automationRuleId,
        Participants = participants,
    };

    [Fact]
    public void РучнойДесктопныйЧат_Право_Есть()
    {
        SessionManager.DesktopTurnEligible(Chat()).Should().BeTrue();
    }

    [Fact]
    public void НеДесктопныйЧат_Права_Нет()
    {
        SessionManager.DesktopTurnEligible(Chat(desktop: false)).Should().BeFalse();
    }

    [Fact]
    public void ЧатИсполнительЗадачи_Права_Нет()
    {
        // Чат, созданный TaskExecutionService: Origin выводится из TaskId
        SessionManager.DesktopTurnEligible(Chat(taskExecution: true)).Should().BeFalse();
        SessionManager.DesktopTurnEligible(Chat(taskId: "task-1")).Should().BeFalse();
    }

    [Fact]
    public void ЧатПравилаПроактивности_Права_Нет()
    {
        // Отложенные и регулярные срабатывания: человека у машины в этот момент нет
        SessionManager.DesktopTurnEligible(Chat(automationRuleId: "rule-1")).Should().BeFalse();
    }

    [Fact]
    public void ГрупповойЧат_Права_Нет()
    {
        // ValidateParticipants держит состав 2–8 персон: Participants не пуст только у групповых
        SessionManager.DesktopTurnEligible(Chat(participants: ["p1", "p2"])).Should().BeFalse();
    }

    [Fact]
    public void ОдиночныйСоставУчастников_Права_Нет()
    {
        // Контроль расхождения двух трактовок «группового»: раньше мёртвый дубль пропускал
        // одиночный состав (Count > 1), живое правило режет (Count > 0). Выбрана строгая
        // форма: Participants — признак группового чата в любой форме, чат с одной персоной
        // хранит её в PersonaId. Если кто-то смягчит предикат обратно — падает этот тест.
        SessionManager.DesktopTurnEligible(Chat(participants: ["p1"])).Should().BeFalse();
    }
}
