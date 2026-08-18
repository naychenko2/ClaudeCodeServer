using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Автодобивание оборванного сабагента: политика отправки, потолок в две попытки и разведение
// двух реакций — «продолжить» (обрыв на середине) против «зовите человека» (терминальный отказ).
public class SubagentNudgeTests : IDisposable
{
    private readonly string _dir;

    public SubagentNudgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "subagent_nudge_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ResultMessage Result(string subtype = "success", string? status = null) =>
        new(subtype, DurationMs: 10, NumTurns: 1, Usage: null, TotalCostUsd: null, ApiErrorStatus: status);

    // ─── Политика отправки ───────────────────────────────────────────────────

    [Fact]
    public void ShouldNudge_ОбычныйЧатПослеОбрыва_Добиваем()
    {
        SessionManager.ShouldNudgeSubagent(nudgesSent: 0, workLoopActive: false, teamActive: false,
            hasPending: false, loopTurnInFlight: false).Should().BeTrue();
    }

    [Fact]
    public void ShouldNudge_ПотолокДвеПопытки()
    {
        SessionManager.ShouldNudgeSubagent(SessionManager.MaxSubagentNudges - 1, false, false, false, false)
            .Should().BeTrue();
        SessionManager.ShouldNudgeSubagent(SessionManager.MaxSubagentNudges, false, false, false, false)
            .Should().BeFalse();
    }

    [Theory]
    // У цикла «до готово», штаба и непустой очереди свой протокол продолжения — уступаем им,
    // иначе второй systemDirective ушёл бы в тот же процесс вторым ходом
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void ShouldNudge_ЧужойПротоколПродолжения_Уступаем(
        bool workLoop, bool team, bool pending, bool loopTurnInFlight)
    {
        SessionManager.ShouldNudgeSubagent(0, workLoop, team, pending, loopTurnInFlight)
            .Should().BeFalse();
    }

    // ─── Привязка счётчика к агенту ──────────────────────────────────────────

    [Fact]
    public void СериюДобиванийЗакрываетТолькоТотЖеАгент()
    {
        // Серия ещё не начата — любой штатный отчёт годится
        SessionManager.ResetsNudgeSeries(null, "agent-B").Should().BeTrue();
        // Добивали A — только его отчёт значит «добили»
        SessionManager.ResetsNudgeSeries("agent-A", "agent-A").Should().BeTrue();
        SessionManager.ResetsNudgeSeries("agent-A", "agent-B").Should().BeFalse();
        // Оборвался другой агент — у него своя серия попыток
        SessionManager.StartsNudgeSeries("agent-A", "agent-B").Should().BeTrue();
        SessionManager.StartsNudgeSeries("agent-A", "agent-A").Should().BeFalse();
    }

    // Дефект был не в политике, а в том, чем её кормили: счётчик и отметка общие на сессию,
    // а агентов в ходе двое. Оборвался A, штатно доложился B — счётчик обнулялся, отметка от A
    // жила, и добивание уходило с attempt=1 бесконечно (чат крутил системные директивы сам).
    [Fact]
    public void ДваАгентаВХоде_ОтчётЧужого_ПотолокВсёРавноДостигается()
    {
        string? nudgeAgent = null;
        var nudges = 0;
        var sent = 0;

        // Четыре хода подряд: A обрывается, B в том же ходе докладывается штатно (его паспорт
        // приходит в сток последним), по result уходит добивание A
        for (var turn = 0; turn < 4; turn++)
        {
            // Сток паспортов: штатный отчёт B
            if (SessionManager.ResetsNudgeSeries(nudgeAgent, "agent-B"))
            {
                nudges = 0;
                nudgeAgent = null;
            }
            // Конец хода: оборванный A
            if (SessionManager.StartsNudgeSeries(nudgeAgent, "agent-A")) nudges = 0;
            if (!SessionManager.ShouldNudgeSubagent(nudges, false, false, false, false)) continue;
            nudgeAgent = "agent-A";
            nudges++;
            sent++;
        }

        sent.Should().Be(SessionManager.MaxSubagentNudges);
    }

    // ─── Категории остановки исполнителя ─────────────────────────────────────

    [Fact]
    public void Classify_ОбрывСабагента_ВосстановимаяКатегория()
    {
        var reason = ExecutorStopClassifier.Classify(Result(), null,
            ExecutorStopClassifier.SubagentTurnState.Truncated);

        reason.Should().Be(ExecutorStopClassifier.SubagentTruncatedReason);
        ExecutorStopClassifier.IsTerminal(reason).Should().BeFalse();
    }

    [Fact]
    public void Classify_ДобиванияИсчерпаны_ТерминальнаяКатегория()
    {
        var reason = ExecutorStopClassifier.Classify(Result(), null,
            ExecutorStopClassifier.SubagentTurnState.Stuck);

        reason.Should().Be(ExecutorStopClassifier.SubagentStuckReason);
        ExecutorStopClassifier.IsTerminal(reason).Should().BeTrue();
    }

    [Fact]
    public void Classify_ОтказАвторизацииСильнееОбрыва()
    {
        // Не авторизовавшись, добивать нечего — реакция «зовите человека»
        ExecutorStopClassifier.Classify(Result(status: "401"), null,
                ExecutorStopClassifier.SubagentTurnState.Truncated)
            .Should().Be(ExecutorStopClassifier.AuthFailedReason);
    }

    [Fact]
    public void IsTerminal_НеизвестнаяПричина_СчитаетсяТерминальной()
    {
        ExecutorStopClassifier.IsTerminal("что-то новое").Should().BeTrue();
    }

    [Fact]
    public void IsTerminal_ПричиныНет_ЭтоОбычныйХод()
    {
        // null = остановки не было: задача без пометки не должна читаться как «работа встала»
        ExecutorStopClassifier.IsTerminal(null).Should().BeFalse();
        ExecutorStopClassifier.IsTerminal(
            ExecutorStopClassifier.Classify(Result(), null)).Should().BeFalse();
    }

    // ─── Пометка на задаче ───────────────────────────────────────────────────

    [Fact]
    public void MarkExecutorStopped_ВосстановимаяПричина_ПометкуНеСтавит()
    {
        var tasks = new TaskManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build());
        var task = tasks.Create("proj-1", "user-1", new CreateTaskRequest(Title: "Задача"));

        var after = tasks.MarkExecutorStopped(task.Id, DateTime.UtcNow,
            ExecutorStopClassifier.SubagentTruncatedReason);

        // «Работа встала, зовите человека» — не про обрыв на середине: его добивают продолжением
        after!.ExecutorStoppedAt.Should().BeNull();
        after.ExecutorStopReason.Should().BeNull();

        var stopped = tasks.MarkExecutorStopped(task.Id, DateTime.UtcNow,
            ExecutorStopClassifier.SubagentStuckReason);
        stopped!.ExecutorStoppedAt.Should().NotBeNull();
        stopped.ExecutorStopReason.Should().Be(ExecutorStopClassifier.SubagentStuckReason);
    }

    [Fact]
    public void ExecutorStopText_УТерминальногоОбрываСвояФормулировка()
    {
        TaskExecutionService.ExecutorStopText(ExecutorStopClassifier.SubagentStuckReason)
            .Should().Contain("сабагент");
    }

    // ─── Текст добивания ─────────────────────────────────────────────────────

    [Fact]
    public void ResumeTruncated_НесётНомерПопыткиАгентаИМестоОбрыва()
    {
        var run = new SubagentRunPassport("a1", "mark", "Волна 1: агент деплоя", "sess-1", "toolu_1",
            DateTime.UtcNow.AddMinutes(-17), DateTime.UtcNow, 1020, 51, 130, 1, 133_000, 4000,
            "tool_use", true, "Bash", "claude-opus-5", 1024, 0, false, "bg_done", DateTime.UtcNow);

        var text = SubagentPrompts.ResumeTruncated(run, attempt: 2, max: SessionManager.MaxSubagentNudges);

        text.Should().Contain("2/2");
        text.Should().Contain("Волна 1: агент деплоя");
        text.Should().Contain("Bash");
        // Обрывок не должен уехать в итог как готовый результат
        text.Should().Contain("обрывок");
    }
}
