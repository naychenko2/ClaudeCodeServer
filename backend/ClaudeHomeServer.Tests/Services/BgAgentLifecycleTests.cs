using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Жизненный цикл фоновых агентов: признак завершения bg_agent_done, поздние tool_result
// после конца хода, персист workflow_progress и маркеры обрыва при загрузке истории
public class BgAgentLifecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChatHistoryService _histSvc;

    public BgAgentLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bg_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _histSvc = new ChatHistoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- Маркер цикла «до готово» ---

    [Fact]
    public void ContainsPromiseMarker_НаходитМаркерВОбычномТексте()
    {
        SessionManager.ContainsPromiseMarker("Всё сделано.\n<promise>ГОТОВО</promise>", "ГОТОВО")
            .Should().BeTrue();
    }

    [Fact]
    public void ContainsPromiseMarker_ИгнорируетЦитатуВИнлайн_Коде()
    {
        SessionManager.ContainsPromiseMarker(
            "Когда закончу — выведу `<promise>ГОТОВО</promise>`, а пока продолжаю.", "ГОТОВО")
            .Should().BeFalse();
    }

    [Fact]
    public void ContainsPromiseMarker_ИгнорируетМаркерВКодБлоке()
    {
        SessionManager.ContainsPromiseMarker(
            "Пример протокола:\n```\n<promise>ГОТОВО</promise>\n```\nЕщё работаю.", "ГОТОВО")
            .Should().BeFalse();
    }

    [Fact]
    public void ContainsPromiseMarker_ЧувствителенКРегистру()
    {
        SessionManager.ContainsPromiseMarker("<promise>готово</promise>", "ГОТОВО")
            .Should().BeFalse();
    }

    [Fact]
    public void ContainsPromiseMarker_НезакрытыйКодБлок_НеПрячетМаркерПослеЗакрытогo()
    {
        // Маркер вне код-блоков находится, даже если раньше был закрытый блок
        SessionManager.ContainsPromiseMarker(
            "```\nкод\n```\nИтог: <promise>ГОТОВО</promise>", "ГОТОВО")
            .Should().BeTrue();
    }

    // --- Завершение фоновой задачи через опрос TaskOutput (модели вроде Kimi) ---

    [Fact]
    public void ParseTaskOutputCompletion_Completed_ВозвращаетAgentIdБезAborted()
    {
        var content = "<retrieval_status>success</retrieval_status>\n\n<task_id>abc3224d352c8d131</task_id>\n\n"
            + "<task_type>local_agent</task_type>\n\n<status>completed</status>\n\n<output>готовый ответ</output>";

        var r = ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.ParseTaskOutputCompletion(content);

        r.Should().NotBeNull();
        r!.Value.AgentId.Should().Be("abc3224d352c8d131");
        r.Value.Aborted.Should().BeFalse();
    }

    [Fact]
    public void ParseTaskOutputCompletion_Failed_ПомечаетAborted()
    {
        var r = ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.ParseTaskOutputCompletion(
            "<task_id>a824ed2f614ce2e90</task_id>\n<status>failed</status>");

        r.Should().NotBeNull();
        r!.Value.Aborted.Should().BeTrue();
    }

    [Fact]
    public void ParseTaskOutputCompletion_Running_ВозвращаетNull()
    {
        // Агент ещё работает (block:false) — не сигнал завершения
        ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.ParseTaskOutputCompletion(
            "<task_id>a1d9a08b4c5de1fa5</task_id>\n<status>running</status>")
            .Should().BeNull();
    }

    [Fact]
    public void ParseTaskOutputCompletion_ОбычныйToolResult_ВозвращаетNull()
    {
        // Квитанция запуска и произвольный вывод не должны триггерить завершение
        ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.ParseTaskOutputCompletion(
            "Async agent launched successfully… agentId: abc123").Should().BeNull();
        ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.ParseTaskOutputCompletion(
            "обычный текст без тегов").Should().BeNull();
    }

    // --- TurnAccumulator: поздние события доживающих агентов ---

    [Fact]
    public async Task OnToolResult_ПослеКонцаХода_ДописываетРезультатВИсторию()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("tool1", "Bash", new { });
        await acc.OnResultAsync("success", 10, 1, null, null, null, null, _histSvc); // ход закрыт, _pendingTools очищен

        acc.OnToolResult("tool1", "поздний результат", isError: false);

        var stored = acc.GetAll().OfType<StoredToolUseMessage>().Single(t => t.Id == "tool1");
        stored.Result.Should().Be("поздний результат");
    }

    [Fact]
    public void OnBgAgentsDone_ПомечаетКарточкуТекущегоХода()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("task1", "Task", new { });
        acc.OnToolResult("task1", "Async agent launched successfully", isError: false);

        acc.OnBgAgentsDone(["task1"]);

        acc.GetAll().OfType<StoredToolUseMessage>().Single().BgDone.Should().BeTrue();
    }

    [Fact]
    public async Task OnBgAgentsDone_ПомечаетКарточкуПрошлогоХода()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("task1", "Task", new { });
        await acc.OnResultAsync("success", 10, 1, null, null, null, null, _histSvc);

        acc.OnBgAgentsDone(["task1"]);

        acc.GetAll().OfType<StoredToolUseMessage>().Single().BgDone.Should().BeTrue();
    }

    [Fact]
    public void OnWorkflowProgress_Upsert_НеПлодитЗаписи()
    {
        var acc = new TurnAccumulator([]);
        acc.OnWorkflowProgress("wf1", isDone: false, [new WorkflowAgentDto("a1", "промпт", null, null, null)]);
        acc.OnWorkflowProgress("wf1", isDone: true,
            [new WorkflowAgentDto("a1", "промпт", null, null, null, IsDone: true)]);

        var stored = acc.GetAll().OfType<StoredWorkflowProgressMessage>().ToList();
        stored.Should().HaveCount(1);
        stored[0].IsDone.Should().BeTrue();
        stored[0].Agents.Should().ContainSingle(a => a.IsDone);
    }

    // --- ChatHistoryService: загрузка после рестарта ---

    [Fact]
    public async Task LoadAsync_НезавершённыйWorkflowProgress_ПомечаетсяAborted()
    {
        var sid = Guid.NewGuid().ToString();
        await _histSvc.SaveAsync(sid, [
            new StoredWorkflowProgressMessage { ToolUseId = "wf1", IsDone = false },
        ]);

        var loaded = await _histSvc.LoadAsync(sid);

        var wp = loaded.OfType<StoredWorkflowProgressMessage>().Single();
        wp.IsDone.Should().BeTrue();
        wp.Aborted.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ФоноваяКвитанцияБезBgDone_Закрывается()
    {
        var sid = Guid.NewGuid().ToString();
        await _histSvc.SaveAsync(sid, [
            new StoredToolUseMessage { Id = "t1", Name = "Task", Result = "Async agent launched successfully… agentId: abc123" },
            new StoredToolUseMessage { Id = "t2", Name = "Bash", Result = "обычный вывод" },
        ]);

        var loaded = await _histSvc.LoadAsync(sid);

        loaded.OfType<StoredToolUseMessage>().Single(t => t.Id == "t1").BgDone.Should().BeTrue();
        loaded.OfType<StoredToolUseMessage>().Single(t => t.Id == "t2").BgDone.Should().BeNull();
    }

    [Fact]
    public async Task AppendTurnAborted_ОборванныйХод_ДобавляетМаркер()
    {
        var sid = Guid.NewGuid().ToString();
        await _histSvc.SaveAsync(sid, [
            new StoredUserMessage("сделай что-нибудь"),
            new StoredTextMessage("начинаю…"),
        ]);

        await _histSvc.AppendTurnAbortedAsync(sid);

        var loaded = await _histSvc.LoadAsync(sid);
        loaded[^1].Should().BeOfType<StoredErrorMessage>();
    }

    [Fact]
    public async Task AppendTurnAborted_ЗавершённыйХод_НеТрогаетИсторию()
    {
        var sid = Guid.NewGuid().ToString();
        await _histSvc.SaveAsync(sid, [
            new StoredUserMessage("вопрос"),
            new StoredTextMessage("ответ"),
            new StoredResultMessage("success", 10, 1),
        ]);

        await _histSvc.AppendTurnAbortedAsync(sid);

        var loaded = await _histSvc.LoadAsync(sid);
        loaded.OfType<StoredErrorMessage>().Should().BeEmpty();
    }
}
