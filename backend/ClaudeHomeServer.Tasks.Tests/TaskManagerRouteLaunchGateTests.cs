using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B3: гейты запуска по маршруту фичи в TaskManager.FeatureRouteLaunchRefusal — «маршрут не
// подтверждён» и «гейт слияния» (разрез 2 §1.8, Г3). Гейт читает эпик и задачи фичи В МОМЕНТ
// вызова, а не из снимка; отказ — строка, а не исключение.
public class TaskManagerRouteLaunchGateTests : IDisposable
{
    private const string Owner = "u";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tmrg-{Guid.NewGuid():N}");
    private readonly IConfiguration _config;
    private readonly TaskManager _tasks;

    public TaskManagerRouteLaunchGateTests()
    {
        Directory.CreateDirectory(_dir);
        _config = Config("projects");
        _tasks = new TaskManager(_config, workflows: new WorkflowTemplateStore(Path.Combine(_dir, "workflow-templates.json")));
    }

    // Отдельный tasks.json в той же папке: второй TaskManager не воюет с основным за общий файл.
    private IConfiguration Config(string name) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, $"{name}.json") })
        .Build();

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    // plan → (step-back | step-front) → final (узел слияния: два предшественника) → merge (линейный)
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

    private TaskItem Epic(bool confirmed = true)
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        if (confirmed) _tasks.ConfirmWorkflow(epic.Id, Route());
        return epic;
    }

    // Легаси-запись: эпик, рождённый до маркера режима маршрута (M1) — RouteRequired = null,
    // как в tasks.json у фич, созданных до этого шага. Снимаем маркер напрямую: другой точки,
    // где он читается, нет (через PUT/MCP маркер не принимается).
    private static TaskItem AsLegacy(TaskItem epic)
    {
        epic.RouteRequired = null;
        return epic;
    }

    // Задача этапа, созданная штатно (получает слепок из подтверждённого маршрута)
    private TaskItem Stage(TaskItem epic, string stageId, string role, string? lane = null) =>
        _tasks.Create(null, Owner, new CreateTaskRequest(stageId, FeatureId: epic.Id, Role: role, StepRef: stageId,
            StageId: stageId, Lane: lane));

    private void Done(TaskItem t) => _tasks.Update(t.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

    // Узел слияния final: берём слепок настоящей задачи, чтобы After пришёл из маршрута
    private TaskItem MergeNode(TaskItem epic, string role = TaskRoles.Final) => new()
    {
        Id = Guid.NewGuid().ToString(), Title = "узел", OwnerId = Owner, FeatureId = epic.Id, Role = role,
        Stage = new StageSnapshot { TemplateId = "tpl", Role = role, StageId = "final", After = ["step-back", "step-front"] },
    };

    // --- Гейт подтверждения -------------------------------------------------

    [Fact]
    public void Подтверждение_МаршрутПредложенНоНеПодтверждён_ОтказДляЗадачиФичи()
    {
        var epic = Epic(confirmed: false);
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = "tpl" });
        var step = new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step };

        _tasks.FeatureRouteLaunchRefusal(step).Should().Be("маршрут фичи не подтверждён — подтвердите его на странице фичи");
    }

    [Fact]
    public void Подтверждение_Consult_Проходит()
    {
        var epic = Epic(confirmed: false);
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = "tpl" });
        var consult = new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Consult };

        _tasks.FeatureRouteLaunchRefusal(consult).Should().BeNull();
    }

    [Fact]
    public void Подтверждение_ЛегасиЭпикБезПредложенияИМаршрута_Проходит()
    {
        var epic = AsLegacy(Epic(confirmed: false));
        var step = new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step };

        _tasks.FeatureRouteLaunchRefusal(step).Should().BeNull();
    }

    [Fact]
    public void Подтверждение_ЗадачаБезФичи_ГейтНеЗадевает()
    {
        _tasks.FeatureRouteLaunchRefusal(new TaskItem { OwnerId = Owner, Role = TaskRoles.Step }).Should().BeNull();
        _tasks.FeatureRouteLaunchRefusal(new TaskItem { OwnerId = Owner, Title = "обычная" }).Should().BeNull();
    }

    [Fact]
    public void Подтверждение_ПослеПодтвержденияМаршрута_ЗапускРазрешаетсяБезПереоткрытияЗадачи()
    {
        // Устаревший снимок: задачу «прочитали» до подтверждения, подтверждение случилось
        // до гейта — гейт видит эпик живьём и не отказывает ложно
        var epic = Epic(confirmed: false);
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = "tpl" });
        var stale = new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step };
        _tasks.FeatureRouteLaunchRefusal(stale).Should().NotBeNull();

        _tasks.ConfirmWorkflow(epic.Id, Route());

        _tasks.FeatureRouteLaunchRefusal(stale).Should().BeNull();
    }

    // --- Маркер режима маршрута (M1) ----------------------------------------
    // Инвариант: ни один эпик, созданный или переведённый в feature при подключённом сторе с
    // непустым каталогом, не запускает шаги без подтверждённого маршрута.

    [Fact]
    public void Маркер_ЭпикПриНепустомКаталоге_БезПредложенияИМаршрута_СтоитНаГейте()
    {
        // Каталог непустой всегда: стор дополняет файл пресетами кода (EnsureBuiltIns).
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        epic.RouteRequired.Should().BeTrue("стор подключён и каталог не пуст — фича в режиме маршрута");

        var step = new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step };
        _tasks.FeatureRouteLaunchRefusal(step).Should()
            .Be("маршрут фичи не подтверждён — подтвердите его на странице фичи");
    }

    [Fact]
    public void Маркер_ПереводЗадачиВFeature_СтавитМаркерИГейтСтоит()
    {
        var task = _tasks.Create(null, Owner, new CreateTaskRequest("была задачей"));
        task.RouteRequired.Should().BeNull("не фича — маркера нет");

        _tasks.Update(task.Id, new UpdateTaskRequest(Role: TaskRoles.Feature));

        _tasks.GetById(task.Id)!.RouteRequired.Should().BeTrue("переход в feature — то же правило, что у Create");
        _tasks.FeatureRouteLaunchRefusal(new TaskItem { OwnerId = Owner, FeatureId = task.Id, Role = TaskRoles.Step })
            .Should().NotBeNull();
    }

    [Fact]
    public void Маркер_БезСтораШаблонов_МаркераНетИГейтНеСтоит()
    {
        using var tasks = new TaskManager(Config("no-store"));
        var epic = tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));

        epic.RouteRequired.Should().NotBeTrue("стора нет — подтвердить маршрут нечем, фичу не запираем");
        tasks.FeatureRouteLaunchRefusal(new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step })
            .Should().BeNull();
    }

    [Fact]
    public void Маркер_ПустойКаталогШаблонов_МаркераНетИГейтНеСтоит()
    {
        // Пустого каталога в работе не бывает (пресеты кода добавляются всегда) — состояние
        // берём из шва стора (EmptyCatalogForTests), а не правкой его приватного поля.
        var store = WorkflowTemplateStore.EmptyCatalogForTests(Path.Combine(_dir, "empty-templates.json"));
        using var tasks = new TaskManager(Config("empty-catalog"), workflows: store);

        var epic = tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));

        epic.RouteRequired.Should().NotBeTrue("каталог пуст — подтвердить нечем, фичу не запираем");
        tasks.FeatureRouteLaunchRefusal(new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step })
            .Should().BeNull();
    }

    [Fact]
    public void Маркер_UpdateУжеФичиБезСменыРоли_МаркерНеТрогает()
    {
        // Маркер ставится только при ПЕРЕХОДЕ роли в feature (TaskManager.cs:1463). Легаси-фичу
        // (маркер снят) любой Update, который роль не меняет, на гейт вернуть не вправе —
        // в том числе Update, явно приславший ту же роль (roleBefore == feature).
        var epic = AsLegacy(Epic(confirmed: false));

        _tasks.Update(epic.Id, new UpdateTaskRequest(Title: "переименовали", Role: TaskRoles.Feature));

        _tasks.GetById(epic.Id)!.RouteRequired.Should().BeNull("роль не менялась — маркера не было и не стало");
        _tasks.FeatureRouteLaunchRefusal(new TaskItem { OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Step })
            .Should().BeNull("легаси-фича остаётся вне режима маршрута");
    }

    // --- Гейт слияния -------------------------------------------------------

    [Fact]
    public void Слияние_ОдинПредшественникНеDone_Отказ()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        Stage(epic, "step-front", TaskRoles.Step, "front"); // todo

        _tasks.FeatureRouteLaunchRefusal(MergeNode(epic)).Should().Contain("«step-front»").And.Contain("не завершена");
    }

    [Fact]
    public void Слияние_ПредшественникБезЗадач_ОтказСТекстомНеНачата()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));

        _tasks.FeatureRouteLaunchRefusal(MergeNode(epic)).Should().Be(
            "ветка «step-front» не начата; если она не нужна — обновите маршрут фичи");
    }

    [Fact]
    public void Слияние_ВсеПредшественникиDone_Проходит()
    {
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        Done(Stage(epic, "step-front", TaskRoles.Step, "front"));

        _tasks.FeatureRouteLaunchRefusal(MergeNode(epic)).Should().BeNull();
    }

    [Fact]
    public void Слияние_ЛинейноеРебро_НеГейтится()
    {
        var epic = Epic();
        var merge = new TaskItem
        {
            OwnerId = Owner, FeatureId = epic.Id, Role = TaskRoles.Merge,
            Stage = new StageSnapshot { Role = TaskRoles.Merge, StageId = "merge", After = ["final"] },
        };

        // предшественник final даже не начат — линейные рёбра гейтом слияния не закрываются
        _tasks.FeatureRouteLaunchRefusal(merge).Should().BeNull();
    }

    [Theory]
    [InlineData(TaskRoles.Review)]
    [InlineData(TaskRoles.Rework)]
    [InlineData(TaskRoles.Consult)]
    public void Слияние_ИсключённыеРоли_Проходят(string role)
    {
        var epic = Epic();
        _tasks.FeatureRouteLaunchRefusal(MergeNode(epic, role)).Should().BeNull();
    }

    [Fact]
    public void Слияние_Дефект_Проходит()
    {
        var epic = Epic();
        var defect = MergeNode(epic);
        defect.Kind = TaskKind.Defect;

        _tasks.FeatureRouteLaunchRefusal(defect).Should().BeNull();
    }

    [Fact]
    public async Task Слияние_ПредшественникЗакрываетсяПараллельно_НетЛожногоПропуска()
    {
        // Гейт читает состояния в момент вызова: пока предшественник не Done — отказ, ложного
        // пропуска нет; закрылся — тот же вызов проходит. Между проверками снимка нет.
        var epic = Epic();
        Done(Stage(epic, "step-back", TaskRoles.Step, "back"));
        var front = Stage(epic, "step-front", TaskRoles.Step, "front");
        var node = MergeNode(epic);

        var refusals = new List<string?>();
        using var started = new ManualResetEventSlim();
        var closer = Task.Run(() => { started.Wait(); Done(front); });
        for (var i = 0; i < 200; i++)
        {
            if (i == 50) started.Set();
            refusals.Add(_tasks.FeatureRouteLaunchRefusal(node));
        }
        await closer;

        // Пропуск (null) допустим только после закрытия; отказ после закрытия невозможен
        var firstPass = refusals.FindIndex(r => r is null);
        if (firstPass >= 0)
            refusals.Skip(firstPass).Should().OnlyContain(r => r == null, "закрытый предшественник не «открывается» обратно");
        _tasks.FeatureRouteLaunchRefusal(node).Should().BeNull();
    }
}
