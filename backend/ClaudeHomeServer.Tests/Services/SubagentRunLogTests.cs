using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Хранилище паспортов прогонов сабагентов: диагностика (что отдаёт GET /api/subagents/runs)
// плюс одноразовая отметка «сабагент чата оборвался» — по ней исполнитель задач понимает,
// что итог хода принимать нельзя.
public class SubagentRunLogTests
{
    private static SubagentRunPassport Passport(string agentId, bool truncated = false,
        string? sessionId = "sess-1", string agentType = "mark", int toolUses = 5,
        int duration = 600, long context = 130_000) =>
        new(AgentId: agentId, AgentType: agentType, Description: "Волна 1", SessionId: sessionId,
            ToolUseId: "toolu_1", StartedAt: DateTime.UtcNow.AddSeconds(-duration),
            LastActivityAt: DateTime.UtcNow, DurationSeconds: duration, ToolUses: toolUses,
            AssistantMessages: 10, Prompts: 1, ContextTokens: context, OutputTokens: 3000,
            LastStopReason: truncated ? "tool_use" : "end_turn", Truncated: truncated,
            LastTool: "Bash", Model: "claude-opus-5", TranscriptBytes: 1024, NudgeAttempts: 0,
            Partial: false, FinishedBy: "bg_done", RecordedAt: DateTime.UtcNow);

    [Fact]
    public void Record_ПовторныйПаспортТогоЖеАгента_ОбновляетЗапись()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true));
        log.Record(Passport("a1", truncated: false, toolUses: 9));

        var recent = log.Recent();
        recent.Should().HaveCount(1);
        recent[0].ToolUses.Should().Be(9);
        recent[0].Truncated.Should().BeFalse();
    }

    [Fact]
    public void NoteNudge_СчётчикДобиванийПереживаетОбновлениеПаспорта()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true));
        log.NoteNudge("a1");
        // Добитый агент дописывает тот же транскрипт и завершается снова — счётчик не теряем
        log.Record(Passport("a1", truncated: false));

        log.Recent()[0].NudgeAttempts.Should().Be(1);
    }

    [Fact]
    public void TakeTruncated_ОтметкаОдноразовая()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true));

        log.TakeTruncated("sess-1").Should().NotBeNull();
        log.TakeTruncated("sess-1").Should().BeNull();
    }

    [Fact]
    public void TakeTruncated_ШтатныйОтчётСнимаетОтметку()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true));
        log.Record(Passport("a2", truncated: false));

        log.TakeTruncated("sess-1").Should().BeNull();
    }

    [Fact]
    public void TakeTruncated_ОтметкаНеУтекаетВЧужойЧат()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true, sessionId: "sess-1"));

        log.TakeTruncated("sess-2").Should().BeNull();
        log.TakeTruncated("sess-1").Should().NotBeNull();
    }

    [Fact]
    public void Recent_СвежиеПервыми_ИЛимит()
    {
        var log = new SubagentRunLog();
        for (var i = 0; i < 5; i++) log.Record(Passport("a" + i));

        var recent = log.Recent(2);
        recent.Should().HaveCount(2);
        recent[0].AgentId.Should().Be("a4");
        recent[1].AgentId.Should().Be("a3");
    }

    [Fact]
    public void Stats_ДоляОбрывовПоТипамАгентов()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true, agentType: "mark"));
        log.Record(Passport("a2", truncated: true, agentType: "mark"));
        log.Record(Passport("a3", truncated: false, agentType: "designer"));

        var stats = log.Stats();
        stats[0].AgentType.Should().Be("mark");
        stats[0].Runs.Should().Be(2);
        stats[0].Truncated.Should().Be(2);
        stats.Single(s => s.AgentType == "designer").Truncated.Should().Be(0);
    }

    [Fact]
    public void Record_БезСессии_ТолькоВДиагностику()
    {
        var log = new SubagentRunLog();
        log.Record(Passport("a1", truncated: true, sessionId: null));

        log.Recent().Should().HaveCount(1);
        log.TakeTruncated("sess-1").Should().BeNull();
    }

    [Fact]
    public void Эскалация_ПослеПотолкаДобиванийОбрывПерестаётБытьВосстановимым()
    {
        // Две попытки — потолок (SessionManager.MaxSubagentNudges): дальше зовём человека
        TaskExecutionService.SubagentStateFor(0).Should().Be(ExecutorStopClassifier.SubagentTurnState.None);
        TaskExecutionService.SubagentStateFor(1).Should().Be(ExecutorStopClassifier.SubagentTurnState.Truncated);
        TaskExecutionService.SubagentStateFor(SessionManager.MaxSubagentNudges)
            .Should().Be(ExecutorStopClassifier.SubagentTurnState.Truncated);
        TaskExecutionService.SubagentStateFor(SessionManager.MaxSubagentNudges + 1)
            .Should().Be(ExecutorStopClassifier.SubagentTurnState.Stuck);
    }
}
