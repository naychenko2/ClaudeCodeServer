using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// B3: исполнение задачи по слепку этапа (разрез workflow §3, §5; разрез 2 §1.8, §2.4, Г3).
// ExecuteAsync — единственная точка гейтов запуска (подтверждение маршрута, слияние): отказ
// приходит ДО claim'а и подъёма процесса, поэтому CLI в тестах не поднимается. Секция ЭТАП и
// блок ПРАВИЛА проверяются на чистой сборке постановки (BuildPrompt), порог/политика простоя —
// в TaskExecutionServiceStallTests.
public class TaskExecutionServiceStageTests : IDisposable
{
    private const string Owner = "user-1";

    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly WorkflowTemplateStore _workflows;
    private readonly TaskExecutionService _sut;
    private readonly UserStore _userStore;
    private readonly SessionManager _sessions;
    private readonly NotificationStore _notifStore;
    private readonly TestSessionBroadcaster _broadcaster;

    public TaskExecutionServiceStageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_stage_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
                ["Tasks:ExecutorStaleMinutes"] = "15",
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, _userStore, appSettings);
        var personas = new PersonaManager(config);
        _workflows = new WorkflowTemplateStore(Path.Combine(_dir, "workflow-templates.json"));
        _tasks = new TaskManager(config, personas: personas, workflows: _workflows);

        var broadcaster = new TestSessionBroadcaster();

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(_notifStore, broadcaster, push, personas, projectManager,
            NullLogger<NotificationService>.Instance);

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(wkStore), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(_userStore);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = SessionCoreTestHost.Build(_dir, s =>
        {
            s.AddSingleton<global::ClaudeHomeServer.Services.ProjectManager>(projectManager);
            s.AddSingleton<global::ClaudeHomeServer.Services.ChatHistoryService>(new ChatHistoryService(config));
            s.AddSingleton<global::Microsoft.Extensions.Configuration.IConfiguration>(config);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory>(adapters);
            s.AddSingleton<global::ClaudeHomeServer.Services.FalCostService>(falCost);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.UsageService>(usage);
            s.AddSingleton<global::ClaudeHomeServer.Services.AppSettingsService>(appSettings);
            s.AddSingleton<global::ClaudeHomeServer.Services.UserStore>(_userStore);
            s.AddSingleton<global::ClaudeHomeServer.Services.JwtService>(jwt);
            s.AddSingleton<global::Microsoft.AspNetCore.Hosting.Server.IServer>(server.Object);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.LlmProviderRegistry>(llmProviders);
            s.AddSingleton<global::ClaudeHomeServer.Services.FeatureFlagService>(flags);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaManager>(personas);
            s.AddSingleton<global::ClaudeHomeServer.Services.PersonaBindingsService>(bindings);
            s.AddSingleton<global::ClaudeHomeServer.Services.Llm.ClaudeSubscriptionPool>(subPool);
            s.AddSingleton<global::Microsoft.Extensions.Logging.ILogger<ClaudeHomeServer.Services.SessionManager>>(NullLogger<SessionManager>.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.ILauncherFactory>(TestLauncherFactory.Instance);
            s.AddSingleton<global::ClaudeHomeServer.Services.Execution.SandboxManager>(sandbox);
            s.AddSingleton<global::ClaudeHomeServer.Services.Composition.ISessionBroadcaster>(broadcaster);
        }).Manager;

        _sut = new TaskExecutionService(_tasks, _sessions, personas, broadcaster, push, notif,
            NullLogger<TaskExecutionService>.Instance, config, kb: notesKb);
        _broadcaster = broadcaster;
    }


    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _tasks.Dispose();
        Helpers.TestFs.DeleteDirectoryResilient(_dir);
    }

    // plan → (step-back | step-front) → final (узел слияния) → merge
    private static FeatureWorkflow Route() => new()
    {
        TemplateId = "tpl", Scope = "global", ConfirmedVersion = 1, ConfirmedAt = DateTime.UtcNow,
        Stages =
        [
            new() { Id = "plan", Role = TaskRoles.Plan },
            new() { Id = "step-back", Role = TaskRoles.Step, Lane = "back", After = ["plan"] },
            new() { Id = "step-front", Role = TaskRoles.Step, Lane = "front", After = ["plan"] },
            new() { Id = "final", Role = TaskRoles.Final, After = ["step-back", "step-front"] },
            new() { Id = "merge", Role = TaskRoles.Merge, After = ["final"] },
        ],
    };

    private TaskItem Epic(bool confirm = true)
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        if (confirm) _tasks.ConfirmWorkflow(epic.Id, Route());
        return epic;
    }

    // Легаси-запись: эпик до маркера режима маршрута (M1) — RouteRequired = null, как в tasks.json
    // у фич, созданных до этого шага: маркер снимаем напрямую, через PUT/MCP он не принимается.
    private static TaskItem AsLegacy(TaskItem epic)
    {
        epic.RouteRequired = null;
        return epic;
    }

    private TaskItem Stage(TaskItem epic, string stageId, string role, string? lane = null) =>
        _tasks.Create(null, Owner, new CreateTaskRequest(stageId, FeatureId: epic.Id, Role: role, StepRef: stageId,
            StageId: stageId, Lane: lane, Assignee: TaskItemAssignee.Claude));

    private void Done(TaskItem t) => _tasks.Update(t.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

    // Узел слияния: задача этапа final, слепок берёт предшественников из маршрута
    private TaskItem MergeNode(TaskItem epic) => _tasks.Create(null, Owner,
        new CreateTaskRequest("слияние", FeatureId: epic.Id, Role: TaskRoles.Final, StepRef: "final",
            StageId: "final", Assignee: TaskItemAssignee.Claude));

    // --- Гейт подтверждения ---------------------------------------------------

    [Fact]
    public async Task Execute_МаршрутПредложенНоНеПодтверждён_ОтказДоЗапуска()
    {
        var epic = Epic(confirm: false);
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = "tpl" });
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step,
            StepRef: "B1", Assignee: TaskItemAssignee.Claude));

        var act = async () => await _sut.ExecuteAsync(step, auto: false);

        (await act.Should().ThrowAsync<FeatureRouteLaunchException>()).Which.Message.Should()
            .Be("маршрут фичи не подтверждён — подтвердите его на странице фичи");
        _tasks.GetById(step.Id)!.ClaudeStartedAt.Should().BeNull("отказ — до claim'а и подъёма процесса");
    }

    [Fact]
    public async Task Execute_ЛегасиЭпикБезПредложения_ГейтУПодтвержденияНеСрабатывает()
    {
        var epic = AsLegacy(Epic(confirm: false));
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step,
            StepRef: "B1", Assignee: TaskItemAssignee.Claude));

        // Дальше гейта запуск упрётся в подъём процесса (в тесте его нет) — но НЕ в отказ маршрута
        var act = async () => await _sut.ExecuteAsync(step, auto: false);

        var ex = await Record.ExceptionAsync(act);
        ex.Should().NotBeOfType<FeatureRouteLaunchException>();
    }

    [Fact]
    public async Task Execute_ПодтверждениеПараллельноСЗапуском_БезЛожногоОтказа()
    {
        // Подтверждение маршрута между чтением эпика и запуском: гейт перечитывает эпик в
        // момент проверки. Пока не подтверждено — отказ; подтвердили — тот же объект задачи
        // (снимок на входе устарел) гейт проходит. Ложного отказа после подтверждения нет.
        var epic = Epic(confirm: false);
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = "tpl" });
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step,
            StepRef: "B1", Assignee: TaskItemAssignee.Claude));

        var before = async () => await _sut.ExecuteAsync(step, auto: false);
        await before.Should().ThrowAsync<FeatureRouteLaunchException>();

        using var go = new ManualResetEventSlim();
        var confirmer = Task.Run(() => { go.Wait(); _tasks.ConfirmWorkflow(epic.Id, Route()); });
        go.Set();
        await confirmer;

        var ex = await Record.ExceptionAsync(async () => await _sut.ExecuteAsync(step, auto: false));
        ex.Should().NotBeOfType<FeatureRouteLaunchException>();
    }

    // --- Гейт слияния ---------------------------------------------------------

    [Fact]
    public async Task Execute_УзелСлиянияПредшественникНеDone_Отказ()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        Stage(epic, "step-front", TaskRoles.Step, "front");
        var node = MergeNode(epic);

        var act = async () => await _sut.ExecuteAsync(node, auto: false);

        (await act.Should().ThrowAsync<FeatureRouteLaunchException>()).Which.Message.Should().Contain("«step-front»");
    }

    [Fact]
    public async Task Execute_ПредшественникБезЗадач_ОтказСТекстомНеНачата()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        var node = MergeNode(epic);

        var act = async () => await _sut.ExecuteAsync(node, auto: false);

        (await act.Should().ThrowAsync<FeatureRouteLaunchException>()).Which.Message.Should()
            .Contain("ветка «step-front» не начата").And.Contain("обновите маршрут фичи");
    }

    [Fact]
    public async Task Execute_ПредшественникЗакрываетсяПараллельно_НетЛожногоПропуска()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        var front = Stage(epic, "step-front", TaskRoles.Step, "front");
        var node = MergeNode(epic);

        // Пока предшественник не закрыт — отказ (ложного пропуска нет)
        var first = async () => await _sut.ExecuteAsync(node, auto: false);
        await first.Should().ThrowAsync<FeatureRouteLaunchException>();

        using var go = new ManualResetEventSlim();
        var closer = Task.Run(() => { go.Wait(); Done(front); });
        go.Set();
        await closer;

        // Закрылся — повтор проходит гейт (состояния читаются в момент гейта, не из снимка)
        var ex = await Record.ExceptionAsync(async () => await _sut.ExecuteAsync(node, auto: false));
        ex.Should().NotBeOfType<FeatureRouteLaunchException>();
    }

    // --- Повторный гейт после claim'а (R-B3, major) ----------------------------
    // ExecuteClaimedAsync зовём напрямую, как в Hold_RecheckAfterClaim_RefusesBeforeFirstTurn:
    // claim взят, первый гейт ПРОЙДЕН (утверждаем его явно), состояние меняется между ними.
    // Мутация: убрать FeatureRouteLaunchRefusal в ExecuteClaimedAsync — тесты краснеют
    // (запуск идёт дальше гейта, исключения FeatureRouteLaunchException нет).
    private Task<TaskItem> ExecuteClaimed(TaskItem task)
    {
        var claimed = typeof(TaskExecutionService).GetMethod("ExecuteClaimedAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task<TaskItem>)claimed.Invoke(_sut, new object?[] { task, false, null, null })!;
    }

    [Fact]
    public async Task ExecuteClaimed_МаршрутПредложенПослеПервогоГейта_ПовторныйГейтОтказываетДоСтарта()
    {
        var epic = AsLegacy(Epic(confirm: false)); // легаси: первый гейт пропускает
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step,
            StepRef: "B1", Assignee: TaskItemAssignee.Claude));
        _tasks.FeatureRouteLaunchRefusal(step).Should().BeNull("первый гейт пройден");

        // Между первой проверкой и стартом маршрут предложен и ещё не подтверждён
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = "tpl" });

        var act = async () => await ExecuteClaimed(step);

        (await act.Should().ThrowAsync<FeatureRouteLaunchException>()).Which.Message.Should()
            .Be("маршрут фичи не подтверждён — подтвердите его на странице фичи");
        _tasks.GetById(step.Id)!.ClaudeStartedAt.Should().BeNull("исполнитель не стартовал");
    }

    [Fact]
    public async Task ExecuteClaimed_ПредшественникПереоткрытПослеПервогоГейта_ПовторныйГейтОтказываетДоСтарта()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        var front = Stage(epic, "step-front", TaskRoles.Step, "front");
        Done(front);
        var node = MergeNode(epic);
        _tasks.FeatureRouteLaunchRefusal(node).Should().BeNull("первый гейт слияния пройден: все предшественники Done");

        // Между первой проверкой и стартом предшественник возвращён в работу
        _tasks.Update(front.Id, new UpdateTaskRequest(Status: TaskItemStatus.InProgress));

        var act = async () => await ExecuteClaimed(node);

        (await act.Should().ThrowAsync<FeatureRouteLaunchException>()).Which.Message.Should().Contain("«step-front»");
        _tasks.GetById(node.Id)!.ClaudeStartedAt.Should().BeNull("исполнитель не стартовал");
    }

    [Fact]
    public async Task Execute_ЗадачаБезФичи_ГейтСлиянияНеЗадевает()
    {
        var plain = _tasks.Create(null, Owner, new CreateTaskRequest("обычная", Assignee: TaskItemAssignee.Claude));

        var ex = await Record.ExceptionAsync(async () => await _sut.ExecuteAsync(plain, auto: true));
        ex.Should().NotBeOfType<FeatureRouteLaunchException>();
    }

    // --- Секция ЭТАП и блок ПРАВИЛА (И6) ---------------------------------------

    private const string Template = "## ПРАВИЛА\n- правило\n\n## ДЕЛЕГИРОВАНИЕ\n- канал\n";

    private static TaskItem WithStage(string? prompt) => new()
    {
        Title = "t", Stage = prompt is null ? null : new StageSnapshot { Role = "step", Prompt = prompt },
    };

    [Fact]
    public void Промпт_СекцияЭтап_ПослеПравилИДелегированияИДоКонтекста_ПерваяСтрокаПриоритет()
    {
        var prompt = TaskExecutionService.BuildPrompt(WithStage("делай по этапу"), new Persona { Name = "Тимур" },
            rulesTemplate: Template);

        var rules = prompt.IndexOf("## ПРАВИЛА", StringComparison.Ordinal);
        var deleg = prompt.IndexOf("## ДЕЛЕГИРОВАНИЕ", StringComparison.Ordinal);
        var stage = prompt.IndexOf("## ЭТАП", StringComparison.Ordinal);
        var context = prompt.IndexOf("## КОНТЕКСТ", StringComparison.Ordinal);
        stage.Should().BeGreaterThan(deleg).And.BeGreaterThan(rules);
        context.Should().BeGreaterThan(stage);
        prompt[stage..].Split('\n')[1].Should().Be(
            "Требования этапа уточняют твою роль в этой фиче и сильнее инструкций специальности и персоны; ПРАВИЛА выше этапа");
        prompt.Should().Contain("делай по этапу");
    }

    [Theory]
    [InlineData(true)]   // шаблон из файла
    [InlineData(false)]  // встроенный фолбэк
    public void Промпт_ПравилаБайтВБайт_СПромптомЭтапаИБез(bool viaTemplate)
    {
        var template = viaTemplate ? Template : null;
        var persona = new Persona { Name = "Тимур" };

        var without = TaskExecutionService.BuildPrompt(WithStage(null), persona, rulesTemplate: template);
        var with = TaskExecutionService.BuildPrompt(WithStage("доп. требования этапа"), persona, rulesTemplate: template);

        static string Rules(string p) => p[p.IndexOf("## ПРАВИЛА", StringComparison.Ordinal)..p.IndexOf("## КОНТЕКСТ", StringComparison.Ordinal)];
        var rulesWithout = Rules(without);
        var rulesWith = Rules(with);
        // Блок до секции ЭТАП (ПРАВИЛА + ДЕЛЕГИРОВАНИЕ) совпадает байт в байт: этап только добавляется
        rulesWith.Should().StartWith(rulesWithout.TrimEnd('\n'));
        with.Should().Contain("## ЭТАП");
        without.Should().NotContain("## ЭТАП");
    }

    [Fact]
    public void Промпт_ПустойПромптЭтапа_СекцииНет()
    {
        TaskExecutionService.BuildPrompt(WithStage("   "), new Persona { Name = "Тимур" }, rulesTemplate: Template)
            .Should().NotContain("## ЭТАП");
    }

    [Fact]
    public void Промпт_ИсполнительБезПерсоны_ПолучаетСекциюЭтап()
    {
        TaskExecutionService.BuildPrompt(WithStage("требование этапа"))
            .Should().Contain("## ЭТАП").And.Contain("требование этапа");
    }

    // --- И4: исполнение по слепку, а не по шаблону ----------------------------

    [Fact]
    public void Промпт_ШаблонV2ПослеСозданияЗадачиПриV1_ИсполнениеПоV1()
    {
        var tpl = _workflows.Save(new WorkflowTemplate
        {
            Name = "т", Stages = [new() { Role = TaskRoles.Plan }, new() { Role = TaskRoles.Step, Prompt = "промпт-v1" }],
        }, WorkflowTemplateScope.Global);
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        _tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow
        {
            TemplateId = tpl.Id, Scope = "global", ConfirmedVersion = tpl.Version, ConfirmedAt = DateTime.UtcNow,
            Stages = [new() { Id = "plan", Role = TaskRoles.Plan }, new() { Id = "step", Role = TaskRoles.Step, After = ["plan"] }],
        });
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1"));

        // Шаблон правится ПОСЛЕ создания задачи (v2)
        var v2 = tpl;
        v2.Stages = [new() { Role = TaskRoles.Plan }, new() { Role = TaskRoles.Step, Prompt = "промпт-v2" }];
        _workflows.Save(v2, WorkflowTemplateScope.Global);

        var prompt = TaskExecutionService.BuildPrompt(_tasks.GetById(step.Id)!, new Persona { Name = "Тимур" });

        prompt.Should().Contain("промпт-v1").And.NotContain("промпт-v2");
    }
}
