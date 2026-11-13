using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// M3: авто-подтверждение очевидного маршрута. Инвариант: авто-путь никогда не перезаписывает
// маршрут, подтверждённый человеком (проверка «маршрута нет» — под _workflowLock в ConfirmWorkflow).
public class WorkflowAutoConfirmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wauto-{Guid.NewGuid():N}");
    private readonly WorkflowTemplateStore _store;
    private readonly TaskManager _tasks;
    private const string Owner = "u";

    public WorkflowAutoConfirmTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new WorkflowTemplateStore(Path.Combine(_dir, "workflow-templates.json"));
        _tasks = new TaskManager(Config(), workflows: _store);
    }

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "tasks.json"),
            ["Tasks:SaveDebounceMs"] = "0",
        })
        .Build();

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private sealed class StubRunner : ICheapTextRunner
    {
        public int Calls;
        public string Text = "";
        public Exception? Failure;
        public bool GateMode;
        public readonly List<TaskCompletionSource<string>> Gates = [];
        public string LastPrompt = "";
        public string? LastActionKey;
        public object? LastJsonFormat;

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "local=stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastPrompt = prompt;
            LastJsonFormat = jsonFormat;
            if (Failure is not null) throw Failure;
            if (!GateMode) return Task.FromResult(Text);
            var gate = new TaskCompletionSource<string>();
            Gates.Add(gate);
            return gate.Task;
        }

        public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => await RunAsync(actionKey, prompt, null, ownerId, jsonFormat, ct);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(new OneShotResult(RunAsync(actionKey, prompt, fallbackModel, ownerId, jsonFormat, ct).Result, null, 10));
    }

    private WorkflowRecommender MakeService(StubRunner runner) =>
        new WorkflowRecommender(_tasks, _store, Config(), runner);

    private WorkflowTemplate Template() => _store.Save(new WorkflowTemplate
    {
        Name = "тест",
        Description = "правка с ревью",
        Stages =
        [
            new() { Role = TaskRoles.Plan },
            new() { Role = TaskRoles.Step },
            new() { Role = TaskRoles.Review },
            new() { Role = TaskRoles.Qa, Presence = WorkflowStagePresence.Optional },
            new() { Role = TaskRoles.Final },
        ],
    }, WorkflowTemplateScope.Global);

    private TaskItem Epic() =>
        _tasks.Create(null, Owner, new CreateTaskRequest("фича", Description: "малая правка", Role: TaskRoles.Feature));

    private static WorkflowProposal Obvious(string templateId) => new()
    {
        TemplateId = templateId, Scope = "global", NeedsHuman = false, DoubtReason = null,
    };

    [Fact]
    public void Очевидное_ПишетМаршрутСПометкойВыбраноМоделью()
    {
        var tpl = Template();
        var epic = Epic();

        var result = MakeService(new StubRunner()).TryAutoConfirm(epic.Id, Obvious(tpl.Id));

        result.Should().NotBeNull();
        var wf = _tasks.GetById(epic.Id)!.Workflow;
        wf.Should().NotBeNull();
        wf!.AutoConfirmed.Should().BeTrue();
        wf.ConfirmedByUser.Should().BeFalse();
        wf.TemplateId.Should().Be(tpl.Id);
        wf.ConfirmedVersion.Should().Be(tpl.Version);
        wf.Stages.Should().NotBeEmpty();
    }

    // Гейт запуска: фича в режиме маршрута (RouteRequired=true) до авто-подтверждения закрыта, после — открыта
    [Fact]
    public void РежимМаршрута_АвтоПодтверждениеОткрываетГейтЗапуска()
    {
        var tpl = Template();
        var epic = Epic();
        epic.RouteRequired.Should().BeTrue();
        var step = new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step };
        _tasks.FeatureRouteLaunchRefusal(step).Should().NotBeNull();

        MakeService(new StubRunner()).TryAutoConfirm(epic.Id, Obvious(tpl.Id)).Should().NotBeNull();

        var wf = _tasks.GetById(epic.Id)!.Workflow!;
        wf.AutoConfirmed.Should().BeTrue();
        wf.ConfirmedByUser.Should().BeFalse();
        _tasks.FeatureRouteLaunchRefusal(step).Should().BeNull();
    }

    [Fact]
    public void НуженЧеловек_ИлиПричина_ИлиСнятыЭтапы_МаршрутаНет()
    {
        var tpl = Template();
        var service = MakeService(new StubRunner());
        var variants = new[]
        {
            new WorkflowProposal { TemplateId = tpl.Id, NeedsHuman = true },
            new WorkflowProposal { TemplateId = tpl.Id, NeedsHuman = false, DoubtReason = WorkflowDoubtReasons.Ambiguous },
            new WorkflowProposal { TemplateId = tpl.Id, NeedsHuman = false, Skipped = [TaskRoles.Qa] },
        };
        foreach (var p in variants)
        {
            var epic = Epic();
            _tasks.SetWorkflowProposal(epic.Id, p);
            service.TryAutoConfirm(epic.Id, p).Should().BeNull();
            _tasks.GetById(epic.Id)!.WorkflowProposal.Should().NotBeNull();
            _tasks.GetById(epic.Id)!.Workflow.Should().BeNull();
        }
    }

    // Человек раньше авто-пути: его маршрут не меняется
    [Fact]
    public void ЧеловекРаньше_МаршрутЧеловекаНеТронут()
    {
        var tpl = Template();
        var epic = Epic();
        var human = new FeatureWorkflow
        {
            TemplateId = tpl.Id, Scope = "global", Stages = WorkflowGraph.Resolve(tpl, []),
            ConfirmedVersion = tpl.Version, ConfirmedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConfirmedByUser = true,
        };
        _tasks.ConfirmWorkflow(epic.Id, human);

        var result = MakeService(new StubRunner()).TryAutoConfirm(epic.Id, Obvious(tpl.Id));

        result.Should().BeNull();
        var wf = _tasks.GetById(epic.Id)!.Workflow!;
        wf.ConfirmedByUser.Should().BeTrue();
        wf.AutoConfirmed.Should().BeFalse();
        wf.ConfirmedAt.Should().Be(human.ConfirmedAt);
    }

    // Два одновременных авто-подтверждения: пишет ровно один, второй получает null
    [Fact]
    public void ДваПотока_МаршрутЗаписанОдинРаз()
    {
        var tpl = Template();
        var epic = Epic();
        var service = MakeService(new StubRunner());
        using var barrier = new Barrier(2);
        var results = new TaskItem?[2];
        var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
        {
            barrier.SignalAndWait();
            results[i] = service.TryAutoConfirm(epic.Id, Obvious(tpl.Id));
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        results.Count(r => r is not null).Should().Be(1);
        _tasks.GetById(epic.Id)!.Workflow!.AutoConfirmed.Should().BeTrue();
    }

    // Устаревший ответ: шаблон удалён, пока модель думала — маршрута нет
    [Fact]
    public void ШаблонУдалён_МаршрутаНет()
    {
        var tpl = Template();
        var epic = Epic();
        var proposal = Obvious(tpl.Id);
        _store.Delete(tpl.Id, WorkflowTemplateScope.Global);

        _tasks.SetWorkflowProposal(epic.Id, proposal);
        MakeService(new StubRunner()).TryAutoConfirm(epic.Id, proposal).Should().BeNull();
        _tasks.GetById(epic.Id)!.Workflow.Should().BeNull();
        _tasks.GetById(epic.Id)!.WorkflowProposal.Should().NotBeNull();
    }

    // Весь фоновый путь: RecommendAsync + TryAutoConfirm по очевидному ответу модели
    [Fact]
    public async Task СквознойПуть_ОчевидныйОтвет_МаршрутПодтверждён()
    {
        var tpl = Template();
        var epic = Epic();
        var runner = new StubRunner
        {
            Text = "{\"templateId\":\"" + tpl.Id + "\",\"skipped\":[],\"rationale\":\"x\",\"needsHuman\":false}",
        };
        var service = MakeService(runner);

        var proposal = await service.RecommendAsync(epic.Id, Owner, CancellationToken.None);
        proposal.Should().NotBeNull();
        service.TryAutoConfirm(epic.Id, proposal!).Should().NotBeNull();

        var current = _tasks.GetById(epic.Id)!;
        current.Workflow!.AutoConfirmed.Should().BeTrue();
        // предложение пишет RecommendAsync, TryAutoConfirm его не трогает: оно остаётся рядом с маршрутом
        current.WorkflowProposal.Should().NotBeNull();
    }

    private sealed class OwnerCapture : ISessionBroadcaster
    {
        public readonly List<TaskItem> Sent = [];
        public readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message)
        {
            if (message is TaskChangedMessage { Task: { } t }) lock (Sent) Sent.Add(t);
            First.TrySetResult();
            return Task.CompletedTask;
        }
    }

    // Порядок фонового пути: авто-подтверждение — до рассылки, рассылка одна и несёт уже подтверждённый маршрут
    [Fact]
    public async Task ФоновыйПуть_АвтоПодтверждениеДоРассылки_РассылкаОдна()
    {
        var tpl = Template();
        var epic = Epic();
        var runner = new StubRunner
        {
            Text = "{\"templateId\":\"" + tpl.Id + "\",\"skipped\":[],\"rationale\":\"x\",\"needsHuman\":false}",
        };
        var board = new OwnerCapture();

        EpicWorkflowRecommend.Start(MakeService(runner), _tasks, board, null, null, null, epic);
        await board.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100); // хвост: вторая рассылка, если бы она была, успела бы дойти

        lock (board.Sent)
        {
            board.Sent.Should().ContainSingle();
            board.Sent[0].Workflow.Should().NotBeNull();
            board.Sent[0].Workflow!.AutoConfirmed.Should().BeTrue();
        }
    }
}
