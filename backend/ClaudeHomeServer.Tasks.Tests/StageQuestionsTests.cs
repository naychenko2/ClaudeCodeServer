using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B04: вопросы человеку на экране этапа — открытый, отвеченный, их отсутствие, ответ через сервис.
public class StageQuestionsTests : IDisposable
{
    private const string Owner = "owner", Stranger = "stranger";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stageq_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly StageScreenService _sut;

    public StageQuestionsTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        _tasks = new TaskManager(config, journal: journal);
        _sut = new StageScreenService(_tasks, journal: journal);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private TaskItem Step()
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        return _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B04"));
    }

    [Fact]
    public void Нет_вопросов_пустой_массив()
    {
        var step = Step();
        var screen = _sut.Build(step.Id, Owner).Value!;
        screen.Questions.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Открытый_вопрос_текст_из_причины_ожидания()
    {
        var step = Step();
        _tasks.SetAwaitingHuman(step.Id, "Какой порог взять?");

        var q = _sut.Build(step.Id, Owner).Value!.Questions.Should().ContainSingle().Subject;
        q.Status.Should().Be("open");
        q.Text.Should().Be("Какой порог взять?");
        q.Answer.Should().BeNull();
        q.WaitedMinutes.Should().BeGreaterThanOrEqualTo(0);
        q.AskedBy.Name.Should().Be("шаг");
    }

    [Fact]
    public void Ответ_переводит_вопрос_в_отвеченный_и_снимает_ожидание()
    {
        var step = Step();
        _tasks.SetAwaitingHuman(step.Id, "Какой порог взять?");
        var openId = _sut.Build(step.Id, Owner).Value!.Questions[0].Id;

        var result = _sut.AnswerQuestion(step.Id, openId, "  Берите 30  ", Owner);

        result.Outcome.Should().Be(StageOutcome.Ok);
        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().BeNull();
        var q = _sut.Build(step.Id, Owner).Value!.Questions.Should().ContainSingle().Subject;
        q.Status.Should().Be("answered");
        q.Id.Should().Be(openId);
        q.Text.Should().Be("Какой порог взять?");
        q.Answer!.Text.Should().Be("Берите 30");
        q.Answer.By.Should().Be(Owner);
    }

    [Fact]
    public void Открытые_идут_раньше_истории_история_новые_сверху()
    {
        var step = Step();
        _tasks.SetAwaitingHuman(step.Id, "первый");
        _sut.AnswerQuestion(step.Id, _sut.Build(step.Id, Owner).Value!.Questions[0].Id, "а1", Owner);
        Thread.Sleep(5);
        _tasks.SetAwaitingHuman(step.Id, "второй");
        _sut.AnswerQuestion(step.Id, _sut.Build(step.Id, Owner).Value!.Questions[0].Id, "а2", Owner);
        Thread.Sleep(5);
        _tasks.SetAwaitingHuman(step.Id, "третий");

        var qs = _sut.Build(step.Id, Owner).Value!.Questions;
        qs.Select(q => q.Text).Should().Equal("третий", "второй", "первый");
        qs.Select(q => q.Status).Should().Equal("open", "answered", "answered");
    }

    [Fact]
    public void Ответ_отказывает_на_пустой_текст_чужой_вопрос_и_постороннего()
    {
        var step = Step();
        _tasks.SetAwaitingHuman(step.Id, "вопрос");
        var id = _sut.Build(step.Id, Owner).Value!.Questions[0].Id;

        _sut.AnswerQuestion(step.Id, id, "   ", Owner).Outcome.Should().Be(StageOutcome.BadRequest);
        _sut.AnswerQuestion(step.Id, "нет-такого:1", "ответ", Owner).Outcome.Should().Be(StageOutcome.NotFound);
        _sut.AnswerQuestion(step.Id, id, "ответ", Stranger).Outcome.Should().Be(StageOutcome.NotFound);
        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().NotBeNull("отказы ожидание не снимают");

        _sut.AnswerQuestion(step.Id, id, "ответ", Owner).Outcome.Should().Be(StageOutcome.Ok);
        _sut.AnswerQuestion(step.Id, id, "ещё раз", Owner).Outcome.Should().Be(StageOutcome.NotFound);
    }
}
