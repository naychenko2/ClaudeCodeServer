using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Утечка вызова инструмента в текст (локальная модель, vLLM qwen3_coder, upstream #56077):
//  • детектор — на реальных образцах из транскриптов 16–17.10 (четыре вида);
//  • классификатор — терминальность (leaked восстановим, stuck терминален);
//  • ветка ResultMessage — повтор → потолок → терминал; Tail ход не валит; чистый ход
//    сбрасывает серию.
public class ToolCallLeakTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly PersonaManager _personas;
    private readonly NotificationStore _notifStore;
    private readonly TaskExecutionService _sut;
    private readonly SessionManager _sessions;
    private readonly TestSessionBroadcaster _broadcaster;
    private readonly ProjectManager _projects;
    private readonly UserStore _userStore;

    public ToolCallLeakTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tool_call_leak_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
            })
            .Build();

        var userStore = _userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = _projects = new ProjectManager(config, userStore, appSettings);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);

        _broadcaster = new TestSessionBroadcaster();
        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(_notifStore, _broadcaster, push, _personas, projectManager,
            NullLogger<NotificationService>.Instance);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Moq.Mock<System.Net.Http.IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var server = new Moq.Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(userStore);
        var bindings = new PersonaBindingsService(_personas, projectManager, new WorkspaceKnowledgeStore(config),
            new KnowledgeService(new Moq.Mock<System.Net.Http.IHttpClientFactory>().Object,
                Microsoft.Extensions.Options.Options.Create(new DifyOptions()), new WorkspaceKnowledgeStore(config)),
            new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var historyService = new ChatHistoryService(config);
        _sessions = new SessionManager(projectManager, historyService, config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, flags, _personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster: _broadcaster);

        _sut = new TaskExecutionService(_tasks, _sessions, _personas, _broadcaster, push, notif,
            NullLogger<TaskExecutionService>.Instance, config);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_dir);

    private TaskItem CreateTrackedTask()
    {
        var task = _tasks.Create(null, "user-1", new CreateTaskRequest("Утечка вызова"));
        _tasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        return _tasks.GetById(task.Id)!;
    }

    // Реальная сессия в SessionManager (нужна, чтобы директива повтора ушла SendMessageAsync
    // без «Сессия не найдена») + задача, привязанная к её id.
    private async Task<(TaskItem Task, Session Session)> CreateExecutorAsync()
    {
        var user = _userStore.Add("leak-" + Guid.NewGuid().ToString("N")[..6], "password123", "user");
        var dir = Path.Combine(_dir, "proj_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var project = _projects.Create("P", dir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        var task = _tasks.Create(null, user.Id, new CreateTaskRequest("Утечка вызова"));
        _tasks.MarkClaudeStarted(task.Id, session.Id, DateTime.UtcNow);
        session.TaskId = task.Id;
        return (_tasks.GetById(task.Id)!, session);
    }

    private static ResultMessage Result(string subtype = "success", string? status = null) =>
        new(subtype, DurationMs: 10, NumTurns: 1, Usage: null, TotalCostUsd: null, ApiErrorStatus: status);

    // ─── Детектор: реальные образцы из транскриптов 16–17.10 ─────────────────────

    [Theory]
    [InlineData("еди <parameter name=\"file_path\">/home/an/Sources/.../MouseInputService.cs</parameter>")]
    [InlineData("стро </function> </invoke>")]
    [InlineData("</parameter> <parameter=offset> 27 </parameter> </function>")]
    [InlineData("commit. </function> </invoke></parameter></function></invoke>")]
    public void Detect_РеальныеОбразцы_Утечка(string text)
    {
        ToolCallLeakDetector.Detect(text).Should().NotBeNull("маркер XML-формата вызова обязан найтись");
    }

    [Fact]
    public void Detect_ЧистыйТекст_НеУтечка()
    {
        ToolCallLeakDetector.Detect("Проверил тесты, всё зелёное. Коммит готов.").Should().BeNull();
        ToolCallLeakDetector.Detect(null).Should().BeNull();
        ToolCallLeakDetector.Detect("").Should().BeNull();
    }

    // ─── Классификатор: терминальность ───────────────────────────────────────────

    [Fact]
    public void Classify_Утечка_ВосстановимаяНеТерминальна()
    {
        var reason = ExecutorStopClassifier.Classify(Result(), null,
            ExecutorStopClassifier.SubagentTurnState.None,
            ExecutorStopClassifier.ToolCallLeakTurnState.Leaked);
        reason.Should().Be(ExecutorStopClassifier.ToolCallLeakedReason);
        ExecutorStopClassifier.IsTerminal(reason).Should().BeFalse();
    }

    [Fact]
    public void Classify_УтечкаИсчерпана_Терминальна()
    {
        var reason = ExecutorStopClassifier.Classify(Result(), null,
            ExecutorStopClassifier.SubagentTurnState.None,
            ExecutorStopClassifier.ToolCallLeakTurnState.Stuck);
        reason.Should().Be(ExecutorStopClassifier.ToolCallLeakStuckReason);
        ExecutorStopClassifier.IsTerminal(reason).Should().BeTrue();
    }

    [Fact]
    public void Classify_АвторизацияСильнееУтечки()
    {
        ExecutorStopClassifier.Classify(Result(status: "401"), null,
                ExecutorStopClassifier.SubagentTurnState.None,
                ExecutorStopClassifier.ToolCallLeakTurnState.Stuck)
            .Should().Be(ExecutorStopClassifier.AuthFailedReason);
    }

    // ─── Ветка ResultMessage ─────────────────────────────────────────────────────

    // Первая WholeCall-утечка: восстановимая — итог не принимаем (ClaudeResult не встаёт),
    // ходу ушла директива повторить вызов.
    [Fact]
    public async Task Result_WholeCallПервая_ПовторБезИтога()
    {
        var (task, session) = await CreateExecutorAsync();

        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));

        var after = _tasks.GetById(task.Id)!;
        after.ClaudeResult.Should().BeNull("восстановимая утечка — итог хода не принимаем");
        after.ExecutorStopReason.Should().BeNull("человека ещё не зовём — повтор в пути");
    }

    // WholeCall-утечки подряд: пока повторы в пределах MaxSubagentNudges — восстановимо
    // (директива повторить, итог не принимаем); исчерпаны — работа встала: ClaudeResult=error
    // + терминальная пометка. Семантика потолка совпадает с обрывами сабагентов: 1,2 → повтор,
    // 3-я подряд → терминал.
    [Fact]
    public async Task Result_WholeCallПотолок_Терминал()
    {
        var (task, session) = await CreateExecutorAsync();

        // Ход 1: первая утечка — повтор (попытка 1), итог не принимаем
        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));
        // Ход 2: вторая утечка подряд — повтор (попытка 2), итог не принимаем
        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));
        // Ход 3: третья утечка подряд — потолок исчерпан, терминал
        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));

        var after = _tasks.GetById(task.Id)!;
        after.ClaudeResult.Should().Be("error", "потолок повторов исчерпан — ход провален");
        after.ExecutorStopReason.Should().Be(ExecutorStopClassifier.ToolCallLeakStuckReason);
        after.ExecutorStoppedAt.Should().NotBeNull();
    }

    // Tail-утечка ход не валит: ClaudeResult=success, пометки остановки нет.
    [Fact]
    public async Task Result_Tail_ХодНеВалит()
    {
        var (task, session) = await CreateExecutorAsync();

        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("tail", "Edit", "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));

        var after = _tasks.GetById(task.Id)!;
        after.ClaudeResult.Should().Be("success", "Tail-утечка — только счётчик, ход успешен");
        after.ExecutorStopReason.Should().BeNull();
    }

    // Чистый ход (без WholeCall-утечки) сбрасывает серию: доводим серию до границы потолка
    // (две утечки подряд), вставляем чистый ход, и следующая утечка — снова попытка 1 (повтор),
    // а не продолжение серии (иначе была бы уже третья подряд → терминал).
    [Fact]
    public async Task Result_ЧистыйХод_СбрасываетСерии()
    {
        var (task, session) = await CreateExecutorAsync();

        // Ход 1: утечка (попытка 1, повтор)
        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));
        // Ход 2: утечка (попытка 2, повтор) — серия на границе потолка
        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));
        // Ход 3: чистый (без утечки) — серия сбрасывается, ход успешен
        await _sut.OnSessionMessageAsync(session, Result("success"));
        // Ход 4: утечка снова — это попытка 1 (повтор), а не третья подряд (иначе терминал)
        await _sut.OnSessionMessageAsync(session, new ToolCallLeakMessage("whole_call", null, "…"));
        await _sut.OnSessionMessageAsync(session, Result("success"));

        var after = _tasks.GetById(task.Id)!;
        after.ExecutorStopReason.Should().BeNull("чистый ход сбросил серию — это новая попытка 1, не терминал");
        after.ClaudeResult.Should().Be("success", "чистый ход 3 успешен; ход 4 (повтор) итога не ставит");
    }
}
