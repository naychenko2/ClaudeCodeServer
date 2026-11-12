using System.Reflection;
using ClaudeHomeServer.Models;
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
    private readonly TaskManager _tasks;
    private const string Owner = "u";

    public TaskManagerWorkflowTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new WorkflowTemplateStore(Path.Combine(_dir, "workflow-templates.json"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config, workflows: _store);
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

    private TaskItem Epic(WorkflowTemplate? saved = null, params string[] skipped)
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        if (saved is null) return epic;
        var route = saved.Stages.Select(s => s.Role).Where(r => !skipped.Contains(r)).ToList();
        _tasks.ConfirmWorkflow(epic.Id, new FeatureWorkflow
        {
            TemplateId = saved.Id, Scope = "global", Route = route, Skipped = skipped.ToList(),
            ConfirmedVersion = saved.Version, ConfirmedAt = DateTime.UtcNow,
        });
        return epic;
    }

    private TaskItem Step(TaskItem epic, string? role = TaskRoles.Step, string? personaId = null, string? title = "шаг") =>
        _tasks.Create(null, Owner, new CreateTaskRequest(title!, FeatureId: epic.Id, Role: role, PersonaId: personaId, StepRef: "B1"));

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
            Route = [TaskRoles.Step, TaskRoles.Review, TaskRoles.Final, TaskRoles.Merge], Skipped = [TaskRoles.Qa],
            ConfirmedVersion = tplB.Version, ConfirmedAt = DateTime.UtcNow,
        }));
        var creates = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            try { return Step(epic, TaskRoles.Qa); } catch (InvalidOperationException) { return null; }
        })).ToArray();
        await Task.WhenAll(creates.Append(confirm));

        foreach (var qa in creates.Select(c => c.Result).Where(t => t is not null))
            qa!.Stage!.TemplateId.Should().Be(tplA.Id, "qa создан только по старому маршруту, целиком");
        _tasks.GetById(epic.Id)!.Workflow!.TemplateId.Should().Be(tplB.Id);
    }
}
