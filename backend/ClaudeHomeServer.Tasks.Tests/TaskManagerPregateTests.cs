using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

// Предгейт шага (NC-P1b): запись результата, серия красных и эскалация; закрытие шага он не блокирует
public class TaskManagerPregateTests : IDisposable
{
    private const string Wt = "/tmp/wt-pregate";
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";

    private readonly string _dir;
    private TaskManager _sut;

    public TaskManagerPregateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pregate_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sut = Build();
    }

    private TaskManager Build()
    {
        var settings = new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") };
        return new TaskManager(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TaskItem Step(string role = TaskRoles.Step, string? worktree = Wt)
    {
        var epic = _sut.Create("p", "u", new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var review = _sut.Create("p", "u", new CreateTaskRequest("ревью", FeatureId: epic.Id, Role: TaskRoles.Review));
        return _sut.Create("p", "u", new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: role,
            StepRef: "B1", WorktreePath: worktree, ReviewOf: role == TaskRoles.Rework ? [review.Id] : null));
    }

    private static UpdateTaskRequest Close() => new(Status: TaskItemStatus.Done);

    [Fact]
    public void Шаг_БезРезультатаПредгейта_Закрывается()
    {
        var task = Step();

        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void Шаг_КрасныйПредгейт_Закрывается_ЗаписьСохраняется()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, false, "✗ build: error CS0246 в Foo.cs", "build");

        var closed = _sut.Update(task.Id, Close(), isAgentCall: true)!;

        closed.Status.Should().Be(TaskItemStatus.Done, "предгейт не блокирует закрытие");
        closed.Pregate.Should().NotBeNull("запись результата видна ревьюеру");
        closed.Pregate!.Summary.Should().Contain("error CS0246 в Foo.cs");
    }

    [Fact]
    public void Доработка_ЗакрываетсяБезПредгейта()
    {
        var task = Step(TaskRoles.Rework);

        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void Applies_РольИДерево()
    {
        PregateRules.Applies(TaskRoles.Step, Wt).Should().BeTrue();
        PregateRules.Applies(TaskRoles.Rework, Wt).Should().BeTrue();
        PregateRules.Applies(TaskRoles.Cut, Wt).Should().BeFalse("не шаг и не доработка");
        PregateRules.Applies(null, Wt).Should().BeFalse();
        PregateRules.Applies(TaskRoles.Step, "").Should().BeFalse("дерева нет");
        PregateRules.Applies(TaskRoles.Step, "  ").Should().BeFalse();
        PregateRules.Applies(TaskRoles.Step, null).Should().BeFalse();
    }

    [Fact]
    public void ВторойКрасныйПодрядПоТойЖеПричине_ДокладПостановщику()
    {
        var task = Step();
        var escalations = new List<string>();
        _sut.PregateEscalated += (_, text) => escalations.Add(text);

        _sut.RecordPregate(task.Id, Sha1, false, "✗ build: warning CS8618", "build");
        escalations.Should().BeEmpty("первый красный — ещё не доклад");
        var second = _sut.RecordPregate(task.Id, Sha1, false, "✗ build: warning CS8618", "build");

        second!.RedStreak.Should().Be(2);
        escalations.Should().ContainSingle().Which.Should().Contain("решение постановщика").And.Contain("CS8618");
    }

    [Fact]
    public void КрасныйПоДругойПричине_СерияНачинаетсяЗаново_БезДоклада()
    {
        var task = Step();
        var escalations = new List<string>();
        _sut.PregateEscalated += (_, text) => escalations.Add(text);

        _sut.RecordPregate(task.Id, Sha1, false, "x", "build");
        var second = _sut.RecordPregate(task.Id, Sha1, false, "y", "tsc");

        second!.RedStreak.Should().Be(1);
        escalations.Should().BeEmpty();
    }

    [Fact]
    public void ЗелёныйМеждуКрасными_СбрасываетСерию()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, false, "x", "build");
        _sut.RecordPregate(task.Id, Sha1, true, "", null);

        _sut.RecordPregate(task.Id, Sha2, false, "x", "build")!.RedStreak.Should().Be(1);
    }

}
