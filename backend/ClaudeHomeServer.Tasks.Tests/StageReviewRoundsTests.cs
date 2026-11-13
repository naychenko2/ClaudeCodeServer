using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B05: раунды ревью этапа структурно — блокеры file:line, замечания, ожидание следующего раунда, легаси.
public class StageReviewRoundsTests : IDisposable
{
    private const string Owner = "owner";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stagerr_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly StageScreenService _sut;

    public StageReviewRoundsTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new StageScreenService(_tasks);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private TaskItem Step(bool legacy)
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B05"));
        // маршрут ставим после создания задач: пустой граф снял бы роли step/review
        if (!legacy) _tasks.GetById(epic.Id)!.Workflow = new FeatureWorkflow { TemplateId = "t", ConfirmedVersion = 1 };
        return step;
    }

    private void Review(TaskItem step, string verdict, string? notes, string? reason = null)
    {
        var wf = _tasks.GetById(step.FeatureId!)!.Workflow;
        _tasks.GetById(step.FeatureId!)!.Workflow = null;
        var r = _tasks.Create(null, Owner, new CreateTaskRequest("ревью", FeatureId: step.FeatureId, Role: TaskRoles.Review, ReviewOf: [step.Id]));
        _tasks.GetById(step.FeatureId!)!.Workflow = wf;
        r.Verification = new TaskVerification { Verdict = verdict, ReturnReason = reason, Notes = notes, VerifiedAt = DateTime.UtcNow };
    }

    [Fact]
    public void Возврат_с_двумя_блокерами_и_ожидание_раунда_2()
    {
        var step = Step(legacy: false);
        Review(step, TaskVerdicts.Returned,
            "- блокер: TaskExecutionService.cs:412 — порог берётся из конфига\n- [blocker] Services/Guard.cs:7 нет сторожа\n- замечание: переименовать поле",
            reason: "contract");

        var rr = _sut.Build(step.Id, Owner).Value!.Review.ReviewRounds;

        rr.Structured.Should().BeTrue();
        rr.Hint.Should().BeNull();
        rr.Rounds.Should().HaveCount(2);
        var round = rr.Rounds[0];
        round.Round.Should().Be(1);
        round.Verdict.Should().Be("returned");
        round.ReturnReason.Should().Be("contract");
        round.Pending.Should().BeFalse();
        var next = rr.Rounds[1];
        next.Round.Should().Be(2);
        next.Pending.Should().BeTrue();
        next.Verdict.Should().BeNull();
        next.Reviewer.Should().BeNull();
        round.Blockers.Should().HaveCount(2);
        round.Blockers[0].Should().Be(new StageReviewBlocker("блокер", "TaskExecutionService.cs", 412, "порог берётся из конфига"));
        round.Blockers[1].Should().Be(new StageReviewBlocker("blocker", "Services/Guard.cs", 7, "нет сторожа"));
        round.Remarks.Should().Equal("переименовать поле");
    }

    [Fact]
    public void Принято_без_замечаний_раунд_не_ждёт_следующего()
    {
        var step = Step(legacy: false);
        Review(step, TaskVerdicts.Accepted, null);

        var rr = _sut.Build(step.Id, Owner).Value!.Review.ReviewRounds;

        var round = rr.Rounds.Should().ContainSingle().Subject;
        round.Round.Should().Be(1);
        round.Verdict.Should().Be("accepted");
        round.Blockers.Should().BeEmpty();
        round.Remarks.Should().BeEmpty();
        round.Pending.Should().BeFalse();
    }

    [Fact]
    public void Нет_разметки_notes_одним_замечанием_без_структуры()
    {
        var step = Step(legacy: false);
        Review(step, TaskVerdicts.AcceptedWithRemarks, "просто текст без тегов");

        var rr = _sut.Build(step.Id, Owner).Value!.Review.ReviewRounds;

        rr.Structured.Should().BeFalse();
        var round = rr.Rounds.Single();
        round.Structured.Should().BeFalse();
        round.Blockers.Should().BeEmpty();
        round.Remarks.Should().Equal("просто текст без тегов");
    }

    [Fact]
    public void Легаси_эпик_без_разбора_с_подсказкой()
    {
        var step = Step(legacy: true);
        Review(step, TaskVerdicts.Returned, "блокер: A.cs:1 — что-то");

        var rr = _sut.Build(step.Id, Owner).Value!.Review.ReviewRounds;

        rr.Structured.Should().BeFalse();
        rr.Hint.Should().Be("legacy");
        var round = rr.Rounds[0];
        round.Blockers.Should().BeEmpty();
        round.Remarks.Should().Equal("блокер: A.cs:1 — что-то");
        _sut.Build(step.Id, Owner).Value!.Review.Reviews.Should().ContainSingle();
    }

    [Fact]
    public void Номера_раундов_растут_по_порядку_создания()
    {
        var step = Step(legacy: false);
        Review(step, TaskVerdicts.Returned, "блокер: A.cs:1 — первый");
        Review(step, TaskVerdicts.Returned, "блокер: B.cs:2 — второй");
        Review(step, TaskVerdicts.Accepted, null);

        var rounds = _sut.Build(step.Id, Owner).Value!.Review.ReviewRounds.Rounds;

        rounds.Select(r => r.Round).Should().Equal(1, 2, 3);
        rounds.Select(r => r.Verdict).Should().Equal("returned", "returned", "accepted");
        rounds[0].Blockers[0].File.Should().Be("A.cs");
        rounds[1].Blockers[0].File.Should().Be("B.cs");
        rounds.Any(r => r.Pending).Should().BeFalse();
    }

    [Fact]
    public void Без_ревью_раундов_нет()
    {
        var step = Step(legacy: false);
        var rr = _sut.Build(step.Id, Owner).Value!.Review.ReviewRounds;
        rr.Rounds.Should().BeEmpty();
        rr.Structured.Should().BeFalse();
    }
}
