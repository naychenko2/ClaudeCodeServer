using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

// Гейт «предгейт перед закрытием шага» (NC-P1b): TaskManager.Update на переходе в Done
public class TaskManagerPregateTests : IDisposable
{
    private const string Wt = "/tmp/wt-pregate";
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";

    private sealed class FakeHead(string? sha) : IWorktreeHeadReader
    {
        public string? Sha { get; set; } = sha;
        public string? Head(string worktreePath) => Sha;
        public bool? IsClean(string worktreePath) => true;
        public IReadOnlyList<string>? ChangedFiles(string worktreePath) => null;
    }

    private readonly string _dir;
    private readonly FakeHead _head = new(Sha1);
    private TaskManager _sut;

    public TaskManagerPregateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pregate_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sut = Build(null);
    }

    private TaskManager Build(string? testRunsEnabled)
    {
        var settings = new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") };
        if (testRunsEnabled is not null) settings["Subsystems:test-runs:Enabled"] = testRunsEnabled;
        return new TaskManager(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), heads: _head);
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
    public void Шаг_БезРезультатаПредгейта_НеЗакрывается()
    {
        var task = Step();

        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Предгейт не пройден");
        _sut.GetById(task.Id)!.Status.Should().NotBe(TaskItemStatus.Done);
    }

    [Fact]
    public void Шаг_КрасныйПредгейт_ОтказНесётВыводГейта()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, false, "✗ build: error CS0246 в Foo.cs", "build");

        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);

        var msg = act.Should().Throw<InvalidOperationException>().Which.Message;
        msg.Should().Contain("красный").And.Contain("error CS0246 в Foo.cs");
        _sut.GetById(task.Id)!.Status.Should().NotBe(TaskItemStatus.Done);
    }

    [Fact]
    public void Шаг_ЗелёныйПоТекущемуSha_Закрывается()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, true, "", null);

        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void Шаг_NothingApplicableБезGreen_Закрывается()
    {
        var task = Step();
        var record = _sut.RecordPregate(task.Id, Sha1, false, "NotRun: изменения не касаются ни одной проверки", null, nothingApplicable: true)!;

        record.Green.Should().BeFalse();
        record.NothingApplicable.Should().BeTrue();
        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void Шаг_NothingApplicableПоСтаромуSha_ОтказУстарел()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, false, "NotRun", null, nothingApplicable: true);
        _head.Sha = Sha2;

        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("устарел");
    }

    [Fact]
    public void Шаг_ЗелёныйПоСтаромуSha_ОтказУстарел()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, true, "", null);
        _head.Sha = Sha2;

        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("устарел");
    }

    [Fact]
    public void Шаг_ShaНеПрочитан_ОтказFailClosed()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, true, "", null);
        _head.Sha = null;

        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("не удалось определить");
    }

    [Fact]
    public void Доработка_ТожеПодГейтом()
    {
        var task = Step(TaskRoles.Rework);

        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void СменаРолиВТомЖеЗапросе_ГейтНеОбходит()
    {
        var task = Step();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Role: TaskRoles.Cut));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Предгейт не пройден");
        _sut.GetById(task.Id)!.Status.Should().NotBe(TaskItemStatus.Done);
    }

    [Fact]
    public void СбросДереваВТомЖеЗапросе_ГейтНеОбходит()
    {
        var task = Step();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, WorktreePath: ""));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Предгейт не пройден");
    }

    [Fact]
    public void ПодменаДереваВТомЖеЗапросеЧтоЗакрытие_Отказ_ДажеПриТомЖеSha()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, true, "", null);

        var act = () => _sut.Update(task.Id,
            new UpdateTaskRequest(Status: TaskItemStatus.Done, WorktreePath: "/tmp/wt-other"), isAgentCall: true);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("дерево шага меняется");
        var after = _sut.GetById(task.Id)!;
        after.Status.Should().NotBe(TaskItemStatus.Done);
        after.WorktreePath.Should().Be(Wt);
    }

    [Fact]
    public void ТоЖеДеревоСКосойЧертой_НеПодмена()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, true, "", null);

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, WorktreePath: Wt + "/"), isAgentCall: true)!
            .Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void TreeSwapRefusal_ГраничныеСлучаи()
    {
        PregateRules.TreeSwapRefusal(Wt, "/tmp/other").Should().NotBeNull();
        PregateRules.TreeSwapRefusal(Wt, Wt).Should().BeNull();
        PregateRules.TreeSwapRefusal(Wt, null).Should().BeNull("дерево не меняют");
        PregateRules.TreeSwapRefusal(null, Wt).Should().BeNull("у задачи не было дерева: записи предгейта нет, закрыть нельзя и так");
    }

    [Fact]
    public void ГейтПоРолиДоИПосле()
    {
        PregateRules.GatedPath(TaskRoles.Step, TaskRoles.Cut, Wt, Wt).Should().Be(Wt, "роль была step");
        PregateRules.GatedPath(TaskRoles.Cut, TaskRoles.Rework, Wt, Wt).Should().Be(Wt, "роль стала rework");
        PregateRules.GatedPath(TaskRoles.Cut, TaskRoles.Review, Wt, Wt).Should().BeNull();
        PregateRules.GatedPath(TaskRoles.Step, TaskRoles.Step, null, null).Should().BeNull("дерева нет");
    }

    [Fact]
    public void СбросПредгейта_ЧеловекомНачинаетСерию_ИсполнителюОтказ()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, false, "x", "build");
        _sut.RecordPregate(task.Id, Sha1, false, "x", "build");
        PregateRules.Exhausted(_sut.GetById(task.Id)!.Pregate).Should().BeTrue();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(ResetPregate: true), isAgentCall: true, callerIsExecutor: true);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("постановщиком");
        _sut.GetById(task.Id)!.Pregate.Should().NotBeNull("отказ не трогает запись");

        _sut.Update(task.Id, new UpdateTaskRequest(ResetPregate: true), isAgentCall: true);
        var reset = _sut.GetById(task.Id)!;
        reset.Pregate.Should().BeNull();
        PregateRules.Exhausted(reset.Pregate).Should().BeFalse();
    }

    [Fact]
    public void СбросПредгейта_НеИсчерпаннойСерии_Отказ()
    {
        var task = Step();
        _sut.RecordPregate(task.Id, Sha1, false, "x", "build");

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(ResetPregate: true), isAgentCall: true);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("исчерпанной");
        _sut.GetById(task.Id)!.Pregate.Should().NotBeNull("отказ не трогает запись");
    }

    [Fact]
    public void Applies_СогласованоСGatedPath_НаНеизменныхРолиИДереве()
    {
        foreach (var role in new[] { TaskRoles.Step, TaskRoles.Rework, TaskRoles.Cut, null })
            foreach (var path in new[] { Wt, "", "  ", null })
                PregateRules.Applies(role, path).Should().Be(PregateRules.GatedPath(role, role, path, path) is not null,
                    $"роль {role}, дерево «{path}»");
    }

    [Fact]
    public void ШагБезWorktree_ЗакрываетсяБезГейта()
    {
        var task = Step(worktree: null);

        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void НеШагСWorktree_ЗакрываетсяБезГейта()
    {
        var task = Step(TaskRoles.Review);

        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void ОбычнаяЗадача_ЗакрываетсяБезГейта()
    {
        var task = _sut.Create("p", "u", new CreateTaskRequest("обычная", WorktreePath: Wt));

        _sut.Update(task.Id, Close())!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void ТестРанВыключен_ШагЗакрываетсяКакРаньше()
    {
        _sut.Dispose();
        _sut = Build("false");
        var task = Step();

        _sut.Update(task.Id, Close(), isAgentCall: true)!.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void ВторойКрасныйПодрядПоТойЖеПричине_ДокладПостановщику_ИотказПросит()
    {
        var task = Step();
        var escalations = new List<string>();
        _sut.PregateEscalated += (_, text) => escalations.Add(text);

        _sut.RecordPregate(task.Id, Sha1, false, "✗ build: warning CS8618", "build");
        escalations.Should().BeEmpty("первый красный — ещё не доклад");
        var second = _sut.RecordPregate(task.Id, Sha1, false, "✗ build: warning CS8618", "build");

        second!.RedStreak.Should().Be(2);
        escalations.Should().ContainSingle().Which.Should().Contain("Третьего круга нет").And.Contain("CS8618");
        var act = () => _sut.Update(task.Id, Close(), isAgentCall: true);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("chats_report_up");
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

    [Fact]
    public void ТретьегоКругаНет_СерияИсчерпана()
    {
        PregateRules.Exhausted(new PregateRecord(Sha1, false, "x", DateTime.UtcNow, 2, "build")).Should().BeTrue();
        PregateRules.Exhausted(new PregateRecord(Sha1, false, "x", DateTime.UtcNow, 1, "build")).Should().BeFalse();
        PregateRules.Exhausted(new PregateRecord(Sha1, true, "", DateTime.UtcNow)).Should().BeFalse();
    }
}
