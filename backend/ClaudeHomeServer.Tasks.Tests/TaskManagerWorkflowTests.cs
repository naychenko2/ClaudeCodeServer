using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B2: маршрут эпика, слепок этапа и TaskManager.Create (разрез workflow §1, §3, §4; И3/И4/И7).
public class TaskManagerWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tmw-{Guid.NewGuid():N}");
    private readonly WorkflowTemplateStore _store;
    private readonly IConfiguration _config;
    private readonly TaskManager _tasks;
    private const string Owner = "u";

    public TaskManagerWorkflowTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new WorkflowTemplateStore(Path.Combine(_dir, "workflow-templates.json"));
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(_config, workflows: _store);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private static WorkflowTemplate Template(string prompt = "p1") => new()
    {
        Name = "тест",
        Stages =
        [
            new() { Role = TaskRoles.Plan },
            new() { Role = TaskRoles.Step, Prompt = prompt, StallMinutes = 1, PersonaId = "persona-step", DisallowedTools = ["Bash"] },
            new() { Role = TaskRoles.Review, Specialty = PersonaSpecialty.Reviewer },
            new() { Role = TaskRoles.Qa, Presence = WorkflowStagePresence.Optional },
            new() { Role = TaskRoles.Final },
            new() { Role = TaskRoles.Merge },
        ],
    };

    // Линейный маршрут по ролям: id этапа = роль, After = предыдущий (разрез 2 §0.3).
    private static List<RouteStage> Linear(IEnumerable<string> roles)
    {
        var list = roles.ToList();
        return list.Select((r, i) => new RouteStage { Id = r, Role = r, After = i == 0 ? [] : [list[i - 1]] }).ToList();
    }

    private TaskItem Epic(WorkflowTemplate? saved = null, params string[] skipped)
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        if (saved is null) return epic;
        var route = saved.Stages.Select(s => s.Role).Where(r => !skipped.Contains(r)).ToList();
        _tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow
        {
            TemplateId = saved.Id, Scope = "global", Stages = Linear(route), Skipped = skipped.ToList(),
            ConfirmedVersion = saved.Version, ConfirmedAt = DateTime.UtcNow,
        });
        return epic;
    }

    private TaskItem Step(TaskItem epic, string? role = TaskRoles.Step, string? personaId = null, string? title = "шаг") =>
        _tasks.Create(null, Owner, new CreateTaskRequest(title!, FeatureId: epic.Id, Role: role, PersonaId: personaId, StepRef: "B1"));

    private TaskManager TasksWithPersonas(params Persona[] personas) =>
        new(_config, workflows: _store, personas: new FakePersonaLookup(personas));

    private sealed class FakePersonaLookup(params Persona[] items) : IPersonaLookup
    {
        public Persona? GetByIdInternal(string id) => items.FirstOrDefault(p => p.Id == id);
        public IReadOnlyCollection<Persona> GetAllInternal() => items;
    }

    // Эпик с подтверждённым маршрутом «все роли шаблона» в заданном менеджере.
    private TaskItem EpicIn(TaskManager tasks, WorkflowTemplate tpl, string? projectId = null)
    {
        var epic = tasks.Create(projectId, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow
        {
            TemplateId = tpl.Id, Scope = "global", Stages = Linear(tpl.Stages.Select(s => s.Role)),
            Skipped = [], ConfirmedVersion = tpl.Version, ConfirmedAt = DateTime.UtcNow,
        });
        return epic;
    }

    [Fact]
    public void Create_ЭпикБезWorkflow_StageNull_Легаси()
    {
        var epic = Epic();
        Step(epic).Stage.Should().BeNull();
    }

    [Fact]
    public void Create_СлепокИзТекущейВерсии_КлампСтоллаИПерсонаИзЭтапа()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var epic = Epic(tpl);

        var step = Step(epic);

        step.Stage.Should().NotBeNull();
        step.Stage!.TemplateId.Should().Be(tpl.Id);
        step.Stage.TemplateVersion.Should().Be(1);
        step.Stage.Role.Should().Be(TaskRoles.Step);
        step.Stage.Prompt.Should().Be("p1");
        step.Stage.StallMinutes.Should().Be(5, "NormalizeStall поднимает 1 до пола 5");
        step.Stage.DisallowedTools.Should().Equal("Bash");
        step.PersonaId.Should().Be("persona-step", "personaId предзаполнен из этапа");
        step.Assignee.Should().Be(TaskItemAssignee.Claude);
    }

    [Fact]
    public void Create_ПереданныйPersonaId_НеПерезаписываетсяЭтапом()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global));
        Step(epic, personaId: "other").PersonaId.Should().Be("other");
    }

    // Персона этапа приходит из шаблона мимо входных врат дверей (TaskPersonaGate): при
    // подстановке проверяем существование и владельца — недоступного исполнителя не ставим.
    [Fact]
    public void Create_ПерсонаЭтапа_НеНайдена_Отказ()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var tasks = TasksWithPersonas();
        var epic = EpicIn(tasks, tpl);

        var act = () => tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id,
            Role: TaskRoles.Step, StepRef: "B1"));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("не найдена или недоступна");
    }

    [Fact]
    public void Create_ПерсонаЭтапа_ЧужогоВладельца_Отказ()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var tasks = TasksWithPersonas(new Persona { Id = "persona-step", OwnerId = "чужой", Name = "Ч" });
        var epic = EpicIn(tasks, tpl);

        var act = () => tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id,
            Role: TaskRoles.Step, StepRef: "B1"));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("не найдена или недоступна");
    }

    [Fact]
    public void Create_ПерсонаЭтапа_ПроектнаяЧужогоПроекта_Отказ()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var tasks = TasksWithPersonas(new Persona
        {
            Id = "persona-step", OwnerId = Owner, Name = "П", Scope = PersonaScope.Project, ProjectId = "p2",
        });
        var epic = EpicIn(tasks, tpl, "p1");

        var act = () => tasks.Create("p1", Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id,
            Role: TaskRoles.Step, StepRef: "B1"));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("только задачи своего проекта");
    }

    [Fact]
    public void Create_ПерсонаЭтапа_СвоегоВладельца_Подставляется()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var tasks = TasksWithPersonas(new Persona { Id = "persona-step", OwnerId = Owner, Name = "С" });
        var epic = EpicIn(tasks, tpl);

        var step = tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id,
            Role: TaskRoles.Step, StepRef: "B1"));

        step.PersonaId.Should().Be("persona-step");
        step.Assignee.Should().Be(TaskItemAssignee.Claude);
    }

    [Fact]
    public void Create_SpecialtyЭтапа_НеЗаполняетPersonaId_ПодсказкаВСлепке()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global));
        var review = Step(epic, TaskRoles.Review);
        review.PersonaId.Should().BeNull();
        review.Stage!.Specialty.Should().Be(PersonaSpecialty.Reviewer);
    }

    [Fact]
    public void Create_РольСнятаМаршрутом_Отказ_СостояниеНеМеняется()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global), TaskRoles.Qa);
        var before = _tasks.GetByOwner(Owner).Count;

        var act = () => Step(epic, TaskRoles.Qa);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("этап пропущен маршрутом");
        _tasks.GetByOwner(Owner).Count.Should().Be(before);
    }

    [Fact]
    public void Create_Rework_БерётЭтапStep()
    {
        var epic = Epic(_store.Save(Template("pr"), WorkflowTemplateScope.Global));
        var review = Step(epic, TaskRoles.Review);

        var rework = _tasks.Create(null, Owner, new CreateTaskRequest("доработка", FeatureId: epic.Id,
            Role: TaskRoles.Rework, StepRef: "B1", ReviewOf: [review.Id]));

        rework.Stage!.Role.Should().Be(TaskRoles.Step);
        rework.Stage.Prompt.Should().Be("pr");
    }

    [Fact]
    public void Create_ЗадачаЭпикаБезЯвнойРоли_НеПолучаетСлепка()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global), TaskRoles.Qa);

        var defect = _tasks.Create(null, Owner, new CreateTaskRequest("баг", FeatureId: epic.Id,
            Kind: TaskKind.Defect, Repro: new DefectRepro { Steps = "шаги" }));

        defect.Role.Should().Be(TaskRoles.Qa, "роль дефекту ставит сервер");
        defect.Stage.Should().BeNull("автороль маршрутом не режется, дефект не теряем");
    }

    [Fact]
    public void Create_ШаблонУдалёнПослеПодтверждения_СлепокБезПолейЭтапа_НеПадает()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var epic = Epic(tpl);
        _store.Delete(tpl.Id, WorkflowTemplateScope.Global);

        var step = Step(epic);

        step.Stage.Should().NotBeNull();
        step.Stage!.TemplateId.Should().Be(tpl.Id);
        step.Stage.TemplateVersion.Should().Be(1, "версия — из подтверждения");
        step.Stage.Role.Should().Be(TaskRoles.Step);
        step.Stage.Prompt.Should().BeNull();
        step.Stage.StallMinutes.Should().BeNull();
        step.PersonaId.Should().BeNull();
    }

    [Fact]
    public void Create_ПравкаШаблона_ДаётСлепокСледующейЗадачи_СтарыйНеТронут()
    {
        var tpl = _store.Save(Template("p1"), WorkflowTemplateScope.Global);
        var epic = Epic(tpl);
        var first = Step(epic);

        var next = Template("p2"); next.Id = tpl.Id;
        _store.Save(next, WorkflowTemplateScope.Global);
        var second = Step(epic);

        first.Stage!.Prompt.Should().Be("p1");
        first.Stage.TemplateVersion.Should().Be(1);
        second.Stage!.Prompt.Should().Be("p2");
        second.Stage.TemplateVersion.Should().Be(2);
    }

    [Fact]
    public void PlanSteps_КиндQaУСнятойРоли_Отказ_НаCreateИНаUpdate()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global), TaskRoles.Qa);
        List<PlannedStep> Steps(string kind) =>
            [new() { StepRef = "B1", Title = "шаг" }, new() { StepRef = "QA1", Title = "проверка", Kind = kind }];

        var create = () => _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id,
            Role: TaskRoles.Plan, PlanSteps: Steps(PlannedStep.KindQa)));
        create.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("этап пропущен маршрутом");

        var ok = _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id,
            Role: TaskRoles.Plan, PlanSteps: Steps(PlannedStep.KindReview)));
        var update = () => _tasks.Update(ok.Id, new UpdateTaskRequest(PlanSteps: Steps(PlannedStep.KindQa)));
        update.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("этап пропущен маршрутом");
        _tasks.GetById(ok.Id)!.PlanSteps!.Select(s => s.Kind).Should().Contain(PlannedStep.KindReview);
    }

    [Fact]
    public void PlanSteps_ЭпикБезWorkflow_КиндQaПроходит()
    {
        var epic = Epic();
        var plan = _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new() { StepRef = "B1", Title = "шаг" }, new() { StepRef = "QA1", Title = "п", Kind = PlannedStep.KindQa }]));
        plan.PlanSteps.Should().HaveCount(2);
    }

    // Смена роли после создания проходит проверку маршрута, как и Create: роль этапа,
    // снятая маршрутом эпика, → отказ; Stage при этом не переписывается (И3/И4).
    [Fact]
    public void Update_СменаРолиНаСнятуюМаршрутом_Отказ()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global), TaskRoles.Qa);
        var task = _tasks.Create(null, Owner, new CreateTaskRequest("задача", FeatureId: epic.Id));

        var act = () => _tasks.Update(task.Id, new UpdateTaskRequest(Role: TaskRoles.Qa));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("этап пропущен маршрутом");
        var stored = _tasks.GetById(task.Id)!;
        stored.Role.Should().BeNull("отказ до мутации");
        stored.Stage.Should().BeNull();
    }

    // Правка задачи, у которой роль не менялась (дефект с авто-qa при снятом qa), не отказывает.
    [Fact]
    public void Update_ПравкаБезСменыРоли_ДефектСАвтоQa_Проходит()
    {
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global), TaskRoles.Qa);
        var defect = _tasks.Create(null, Owner, new CreateTaskRequest("баг", FeatureId: epic.Id,
            Kind: TaskKind.Defect, Repro: new DefectRepro { Steps = "шаги" }));

        _tasks.Update(defect.Id, new UpdateTaskRequest(Title: "новое имя"));

        var stored = _tasks.GetById(defect.Id)!;
        stored.Title.Should().Be("новое имя");
        stored.Role.Should().Be(TaskRoles.Qa);
        stored.Stage.Should().BeNull();
    }

    // Легаси-эпик без Workflow: маршрута нет — смена роли проходит, Stage остаётся null.
    [Fact]
    public void Update_СменаРоли_ЛегасиЭпикБезWorkflow_Проходит()
    {
        var epic = Epic();
        var task = _tasks.Create(null, Owner, new CreateTaskRequest("задача", FeatureId: epic.Id));

        _tasks.Update(task.Id, new UpdateTaskRequest(Role: TaskRoles.Qa));

        var stored = _tasks.GetById(task.Id)!;
        stored.Role.Should().Be(TaskRoles.Qa);
        stored.Stage.Should().BeNull();
    }

    // И3/И4: Stage пишет только Create — ни один путь обновления его не меняет.
    [Fact]
    public void Stage_НеизменнаНаПутяхОбновления()
    {
        var tpl = _store.Save(Template("p1"), WorkflowTemplateScope.Global);
        var epic = Epic(tpl);
        var step = Step(epic);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(step.Stage);

        var next = Template("p2"); next.Id = tpl.Id;
        _store.Save(next, WorkflowTemplateScope.Global);

        _tasks.Update(step.Id, new UpdateTaskRequest(Title: "новое имя", Description: "описание", Priority: TaskItemPriority.High));
        _tasks.Update(step.Id, new UpdateTaskRequest(PersonaId: "другая"));
        _tasks.Update(step.Id, new UpdateTaskRequest(ColumnId: "col-review"), effectiveColumn: null);
        _tasks.Update(step.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        // Смена роли в пределах маршрута (step → review) проходит, но слепок прежнего этапа не переписывает.
        _tasks.Update(step.Id, new UpdateTaskRequest(Role: TaskRoles.Review));
        _tasks.GetById(step.Id)!.Role.Should().Be(TaskRoles.Review);

        System.Text.Json.JsonSerializer.Serialize(_tasks.GetById(step.Id)!.Stage).Should().Be(snapshot);
    }

    // И7: Workflow/Proposal пишут только ConfirmWorkflow/SetWorkflowProposal.
    [Fact]
    public void Workflow_НеизменнаНаПутяхОбновленияЭпика()
    {
        var tpl = _store.Save(Template(), WorkflowTemplateScope.Global);
        var epic = Epic(tpl, TaskRoles.Qa);
        _tasks.SetWorkflowProposal(epic.Id, new WorkflowProposal { TemplateId = tpl.Id, Skipped = [TaskRoles.Qa], ProposedAt = DateTime.UtcNow });
        var wf = System.Text.Json.JsonSerializer.Serialize(_tasks.GetById(epic.Id)!.Workflow);
        var pr = System.Text.Json.JsonSerializer.Serialize(_tasks.GetById(epic.Id)!.WorkflowProposal);

        _tasks.Update(epic.Id, new UpdateTaskRequest(Title: "переименовал", Description: "д"));
        _tasks.Update(epic.Id, new UpdateTaskRequest(ColumnId: "col-x"), effectiveColumn: null);
        _tasks.Update(epic.Id, new UpdateTaskRequest(Priority: TaskItemPriority.Urgent));

        System.Text.Json.JsonSerializer.Serialize(_tasks.GetById(epic.Id)!.Workflow).Should().Be(wf);
        System.Text.Json.JsonSerializer.Serialize(_tasks.GetById(epic.Id)!.WorkflowProposal).Should().Be(pr);
    }

    // Двери PUT (UpdateTaskRequest — его принимают REST и MCP tasks_update/tasks_complete) и Create
    // не содержат полей маршрута вовсе: принять их нечем.
    [Theory]
    [InlineData(typeof(UpdateTaskRequest))]
    [InlineData(typeof(CreateTaskRequest))]
    public void ЗапросыЗаписи_НеНесутПолейМаршрута(Type dto)
    {
        var names = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        names.Should().NotContain(["Stage", "Workflow", "WorkflowProposal"]);
    }

    [Fact]
    public void SetWorkflowProposal_ИConfirmWorkflow_НеЭпик_Null()
    {
        var epic = Epic();
        var step = Step(epic);
        _tasks.SetWorkflowProposal(step.Id, new WorkflowProposal()).Should().BeNull();
        _tasks.ConfirmWorkflow(step.Id, new FeatureWorkflow()).Should().BeNull();
        _tasks.ConfirmWorkflow("нет-такого", new FeatureWorkflow()).Should().BeNull();
    }

    [Fact]
    public async Task Create_ПараллельноССохранениемШаблона_СлепокОднойВерсии()
    {
        var tpl = _store.Save(Template("p1"), WorkflowTemplateScope.Global);
        var epic = Epic(tpl);

        var saver = Task.Run(() =>
        {
            for (var v = 2; v <= 60; v++)
            {
                var t = Template($"p{v}"); t.Id = tpl.Id;
                t.Stages[1].StallMinutes = 5 + v;
                _store.Save(t, WorkflowTemplateScope.Global);
            }
        });
        var creators = Enumerable.Range(0, 60).Select(_ => Task.Run(() => Step(epic))).ToArray();
        await Task.WhenAll(creators.Append(saver));

        foreach (var s in creators.Select(c => c.Result.Stage!))
        {
            s.Prompt.Should().Be($"p{s.TemplateVersion}", "слепок целиком одной версии: поле не из соседней");
            if (s.TemplateVersion > 1) s.StallMinutes.Should().Be(Math.Min(240, 5 + s.TemplateVersion));
        }
    }

    [Fact]
    public async Task ConfirmWorkflow_ПараллельноСCreate_ШагВидитМаршрутЦеликом()
    {
        var a = Template(); a.Name = "A";
        var tplA = _store.Save(a, WorkflowTemplateScope.Global);
        var b = Template(); b.Name = "B"; b.Stages[3].Presence = WorkflowStagePresence.Off;
        var tplB = _store.Save(b, WorkflowTemplateScope.Global);
        var epic = Epic(tplA);

        var confirm = Task.Run(() => _tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow
        {
            TemplateId = tplB.Id, Scope = "global",
            Stages = Linear([TaskRoles.Step, TaskRoles.Review, TaskRoles.Final, TaskRoles.Merge]), Skipped = [TaskRoles.Qa],
            ConfirmedVersion = tplB.Version, ConfirmedAt = DateTime.UtcNow,
        }));
        var creates = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            try { return Step(epic, TaskRoles.Qa); } catch (InvalidOperationException) { return null; }
        })).ToArray();
        await Task.WhenAll(creates.Append(confirm));

        foreach (var qa in creates.Select(c => c.Result).Where(t => t is not null))
        {
            qa!.Stage!.TemplateId.Should().Be(tplA.Id, "qa создан только по старому маршруту, целиком");
            qa.Stage.StageId.Should().Be(TaskRoles.Qa);
            qa.Stage.After.Should().Equal([TaskRoles.Review], "After — из того же маршрута A, что и StageId (линейный: предыдущий этап)");
        }
        _tasks.GetById(epic.Id)!.Workflow!.TemplateId.Should().Be(tplB.Id);
    }

    // ─── G2: выбор этапа по графу (WorkflowGraph.StageFor) ───────────────────────────

    // cut → design → {plan, qa-plan}; plan → {step-back, step-front} → review → qa ← qa-plan; qa → final → merge.
    private static WorkflowTemplate ParallelCycle() => new()
    {
        Name = "parallel-cycle",
        Stages =
        [
            new() { Role = TaskRoles.Cut },
            new() { Role = TaskRoles.Design },
            new() { Role = TaskRoles.Plan, After = ["design"], Prompt = "план" },
            new() { Role = TaskRoles.Qa, Id = "qa-plan", After = ["design"], Prompt = "тест-план" },
            new() { Role = TaskRoles.Step, Id = "step-back", After = ["plan"], Lane = "back", Prompt = "бэк" },
            new() { Role = TaskRoles.Step, Id = "step-front", After = ["plan"], Lane = "front", Prompt = "фронт" },
            new() { Role = TaskRoles.Review, After = ["step-back", "step-front"] },
            new() { Role = TaskRoles.Qa, After = ["review", "qa-plan"], Prompt = "qa-итог" },
            new() { Role = TaskRoles.Final },
            new() { Role = TaskRoles.Merge },
        ],
    };

    private TaskItem ParallelEpic(TaskManager? tasks = null)
    {
        tasks ??= _tasks;
        var tpl = _store.Save(ParallelCycle(), WorkflowTemplateScope.Global);
        var epic = tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow
        {
            TemplateId = tpl.Id, Scope = "global", Stages = WorkflowGraph.Resolve(tpl, []),
            ConfirmedVersion = tpl.Version, ConfirmedAt = DateTime.UtcNow,
        });
        return epic;
    }

    private TaskItem Create(TaskItem epic, string role, string? lane = null, string? stageId = null) =>
        _tasks.Create(null, Owner, new CreateTaskRequest("задача", FeatureId: epic.Id, Role: role, Lane: lane, StageId: stageId, StepRef: "B1"));

    [Theory]
    [InlineData("back", "step-back", "бэк")]
    [InlineData("backend", "step-back", "бэк")]
    [InlineData("front", "step-front", "фронт")]
    public void Create_ПараллельныйМаршрут_StepВыбираетЭтапПоЛинии(string lane, string stageId, string prompt)
    {
        var step = Create(ParallelEpic(), TaskRoles.Step, lane);

        step.Stage!.StageId.Should().Be(stageId);
        step.Stage.Role.Should().Be(TaskRoles.Step);
        step.Stage.Lane.Should().Be(stageId == "step-back" ? "back" : "front", "Lane — из этапа маршрута");
        step.Stage.After.Should().Equal(["plan"], "After — предшественники из маршрута эпика");
        step.Stage.Prompt.Should().Be(prompt, "поля этапа берутся из шаблона по id этапа, а не по роли");
    }

    [Fact]
    public void Create_ПараллельныйМаршрут_ДваQa_БезStageId_Отказ_СоStageId_Этап()
    {
        var epic = ParallelEpic();

        var act = () => Create(epic, TaskRoles.Qa);
        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("несколько этапов роли «qa»").And.Contain("укажите stageId (qa-plan, qa)");

        var plan = Create(epic, TaskRoles.Qa, stageId: "qa-plan");
        plan.Stage!.StageId.Should().Be("qa-plan");
        plan.Stage.After.Should().Equal(["design"]);
        plan.Stage.Prompt.Should().Be("тест-план");

        var final = Create(epic, TaskRoles.Qa, stageId: "qa");
        final.Stage!.After.Should().Equal(["review", "qa-plan"]);
        final.Stage.Prompt.Should().Be("qa-итог");
    }

    [Fact]
    public void Create_StageIdЧужойРоли_ИлиНеизвестный_Отказ()
    {
        var epic = ParallelEpic();

        var wrongRole = () => Create(epic, TaskRoles.Review, stageId: "qa-plan");
        wrongRole.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("имеет роль «qa»");
        var unknown = () => Create(epic, TaskRoles.Step, stageId: "step-nope");
        unknown.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("не найден");
        _tasks.GetAll().Should().NotContain(t => t.FeatureId == epic.Id, "отказ до вставки в стор");
    }

    [Fact]
    public void Create_ШагЛинииБезЭтапа_Отказ_СоСпискомId()
    {
        var act = () => Create(ParallelEpic(), TaskRoles.Step, "other");

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("укажите stageId (step-back, step-front)");
    }

    [Fact]
    public void Create_Rework_БерётЭтапStep_ПоЛинии()
    {
        var epic = ParallelEpic();
        var review = Create(epic, TaskRoles.Review);
        var rework = _tasks.Create(null, Owner, new CreateTaskRequest("доработка", FeatureId: epic.Id,
            Role: TaskRoles.Rework, Lane: "front", StepRef: "B1", ReviewOf: [review.Id]));

        rework.Stage!.StageId.Should().Be("step-front");
    }

    [Fact]
    public void Create_Merge_ОдиночнаяРоль_ЭтапОднозначен()
    {
        var epic = ParallelEpic();
        _tasks.Create(null, Owner, new CreateTaskRequest("финал", FeatureId: epic.Id, Role: TaskRoles.Final,
            Status: TaskItemStatus.Done, Verification: new TaskVerification { Verdict = "accepted" }));

        Create(epic, TaskRoles.Merge).Stage!.StageId.Should().Be("merge");
    }

    // Шим Route отдаёт id этапов; три места TaskManager сравнивали его с ролью (R-G1): на параллельном
    // маршруте смена роли на qa/step давала ложный отказ.
    [Theory]
    [InlineData(TaskRoles.Qa)]
    [InlineData(TaskRoles.Step)]
    [InlineData(TaskRoles.Review)]
    public void Update_СменаРолиНаРольСНесколькимиЭтапами_ПараллельныйМаршрут_НеОтказывает(string role)
    {
        var epic = ParallelEpic();
        var task = _tasks.Create(null, Owner, new CreateTaskRequest("задача", FeatureId: epic.Id));

        _tasks.Update(task.Id, new UpdateTaskRequest(Role: role));

        var stored = _tasks.GetById(task.Id)!;
        stored.Role.Should().Be(role);
        stored.Stage.Should().BeNull("слепок в Update не пишется (И3/И4)");
    }

    [Fact]
    public void Update_СменаРолиНаСнятуюМаршрутомQa_ПараллельныйМаршрут_Отказ()
    {
        var tpl = _store.Save(ParallelCycle(), WorkflowTemplateScope.Global);
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        var route = WorkflowGraph.Resolve(tpl, []).Where(s => s.Role != TaskRoles.Qa).ToList();
        _tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow { TemplateId = tpl.Id, Stages = route, ConfirmedVersion = tpl.Version });
        var task = _tasks.Create(null, Owner, new CreateTaskRequest("задача", FeatureId: epic.Id));

        var act = () => _tasks.Update(task.Id, new UpdateTaskRequest(Role: TaskRoles.Qa));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("этап пропущен маршрутом");
    }

    [Fact]
    public void PlanSteps_ПараллельныйМаршрут_QaИReviewВПлане_НеОтказывают()
    {
        var epic = ParallelEpic();

        var plan = _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps:
            [
                new PlannedStep { StepRef = "B1", Title = "бэк", Lane = "back" },
                new PlannedStep { StepRef = "R-B1", Title = "ревью", Kind = PlannedStep.KindReview },
                new PlannedStep { StepRef = "Q-B1", Title = "qa", Kind = PlannedStep.KindQa },
            ]));

        plan.PlanSteps.Should().HaveCount(3);
    }

    [Fact]
    public void PlanSteps_ЛинияШагаНеСовпадаетСЛиниямиМаршрута_Отказ()
    {
        var epic = ParallelEpic();

        var act = () => _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "прочее", Lane = "other" }]));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("линия «other»");
    }

    [Fact]
    public void PlanSteps_ЛинияШагаСОднимЭтапомБезЛинии_НеПроверяется()
    {
        // Линейный маршрут: этап step без линии — правило линии не действует.
        var epic = Epic(_store.Save(Template(), WorkflowTemplateScope.Global));

        var plan = _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "любая", Lane = "other" }]));

        plan.PlanSteps.Should().HaveCount(1);
    }

    [Fact]
    public void Update_PlanSteps_ЛинияНеСовпадает_Отказ()
    {
        var epic = ParallelEpic();
        var plan = _tasks.Create(null, Owner, new CreateTaskRequest("план", FeatureId: epic.Id, Role: TaskRoles.Plan,
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "бэк", Lane = "back" }]));

        var act = () => _tasks.Update(plan.Id, new UpdateTaskRequest(
            PlanSteps: [new PlannedStep { StepRef = "B1", Title = "прочее", Lane = "other" }]));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("линия «other»");
    }

    // Параллельное создание двух шагов разных линий: у каждого свой этап, без перекрёстного слепка.
    [Fact]
    public async Task Create_ДваШагаРазныхЛинийПараллельно_КаждыйСвойStageId()
    {
        var epic = ParallelEpic();

        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            Create(epic, TaskRoles.Step, i % 2 == 0 ? "back" : "front"))).ToArray();
        var done = await Task.WhenAll(tasks);

        for (var i = 0; i < done.Length; i++)
            done[i].Stage!.StageId.Should().Be(i % 2 == 0 ? "step-back" : "step-front");
    }

    // Ошибка StageFor — штатный отказ (InvalidOperationException → 400), а не иной тип исключения.
    [Fact]
    public void Create_НеоднозначныйЭтап_ИсключениеТипаОтказа()
    {
        var act = () => Create(ParallelEpic(), TaskRoles.Qa);

        act.Should().Throw<InvalidOperationException>();
    }
}
