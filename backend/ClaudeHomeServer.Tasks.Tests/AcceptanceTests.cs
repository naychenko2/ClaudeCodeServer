using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

// Критерии приёмки и свидетельства: единственный гейт — TaskManager (AcceptanceRules).
public class AcceptanceTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _sut;

    public AcceptanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "acceptance_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sut = new TaskManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build());
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TaskItem TaskWithThreeCriteria() => _sut.Create(null, "u", new CreateTaskRequest("t",
        Acceptance: [new("", "кнопка ≥ 40 по обеим сторонам"), new("", "ui_probe без находок"), new("", "тест красный на мутации")]));

    private static AcceptanceEvidence Ev(string id) => new(id, "прогон", "ок");

    [Fact]
    public void Create_ПустымId_ПрисваиваетСтабильныеId()
    {
        var task = TaskWithThreeCriteria();
        task.Acceptance.Select(c => c.Id).Should().Equal("c1", "c2", "c3");
    }

    [Fact]
    public void Закрытие_БезСвидетельств_ОтказСПеречнемВсехТрёх()
    {
        var task = TaskWithThreeCriteria();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain("c1").And.Contain("c2").And.Contain("c3");
        _sut.GetById(task.Id)!.Status.Should().NotBe(TaskItemStatus.Done, "при отказе состояние не меняется");
    }

    [Fact]
    public void Закрытие_ДвумяИзТрёх_ОтказПеречисляетТолькоНезакрытый()
    {
        var task = TaskWithThreeCriteria();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done, Evidence: [Ev("c1"), Ev("c3")]));

        var msg = act.Should().Throw<InvalidOperationException>().Which.Message;
        msg.Should().Contain("c2").And.NotContain("c1:").And.NotContain("c3:");
        // Свидетельства из отказавшего вызова не сохраняются
        _sut.GetById(task.Id)!.Evidence.Should().BeEmpty();
    }

    [Fact]
    public void Закрытие_ВсемиТремя_Проходит()
    {
        var task = TaskWithThreeCriteria();

        var done = _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done, Evidence: [Ev("c1"), Ev("c2"), Ev("c3")]))!;

        done.Status.Should().Be(TaskItemStatus.Done);
        done.Evidence.Should().HaveCount(3);
    }

    [Fact]
    public void Свидетельства_НакапливаютсяМеждуВызовами()
    {
        var task = TaskWithThreeCriteria();
        _sut.Update(task.Id, new UpdateTaskRequest(Evidence: [Ev("c1")]));
        _sut.Update(task.Id, new UpdateTaskRequest(Evidence: [Ev("c2"), new("c1", "повтор", "новое")]));

        var cur = _sut.GetById(task.Id)!;
        cur.Evidence.Should().HaveCount(2);
        cur.Evidence.Single(e => e.CriterionId == "c1").How.Should().Be("повтор");
    }

    [Fact]
    public void Свидетельство_ПустоеИлиНаНеизвестныйКритерий_Отказ()
    {
        var task = TaskWithThreeCriteria();

        var empty = () => _sut.Update(task.Id, new UpdateTaskRequest(Evidence: [new("c1", "  ", "ок")]));
        var unknown = () => _sut.Update(task.Id, new UpdateTaskRequest(Evidence: [Ev("zzz")]));

        empty.Should().Throw<InvalidOperationException>();
        unknown.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("zzz");
    }

    [Fact]
    public void ЗадачаБезКритериев_ЗакрываетсяКакРаньше()
    {
        var task = _sut.Create(null, "u", new CreateTaskRequest("обычная"));

        var done = _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done))!;

        done.Status.Should().Be(TaskItemStatus.Done);
        done.Acceptance.Should().BeEmpty();
    }

    [Fact]
    public void Создание_СразуЗакрытойСКритериями_Отказ()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("t",
            Status: TaskItemStatus.Done, Acceptance: [new("", "критерий")]));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Исполнитель_НеМожетУбратьИлиИзменитьКритерий()
    {
        var task = TaskWithThreeCriteria();

        var remove = () => _sut.Update(task.Id, new UpdateTaskRequest(Acceptance: []),
            isAgentCall: true, callerIsExecutor: true);
        var edit = () => _sut.Update(task.Id, new UpdateTaskRequest(
            Acceptance: [new("c1", "поправил формулировку")]), isAgentCall: true, callerIsExecutor: true);

        remove.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("acceptance");
        edit.Should().Throw<InvalidOperationException>();
        _sut.GetById(task.Id)!.Acceptance.Should().HaveCount(3);
    }

    [Fact]
    public void Исполнитель_МожетДобавлятьСвидетельстваИЗакрыватьПоГейту()
    {
        var task = TaskWithThreeCriteria();

        var done = _sut.Update(task.Id, new UpdateTaskRequest(
            Status: TaskItemStatus.Done, Evidence: [Ev("c1"), Ev("c2"), Ev("c3")]),
            isAgentCall: true, callerIsExecutor: true)!;

        done.Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public void ПостановщикИЧеловек_МогутМенятьКритерии()
    {
        var task = TaskWithThreeCriteria();

        // Постановщик: агентский вызов, но не исполнитель этой задачи
        _sut.Update(task.Id, new UpdateTaskRequest(Acceptance: [new("c1", "оставили один")]),
            isAgentCall: true, callerIsExecutor: false);
        // Человек: REST без агентских признаков
        var human = _sut.Update(task.Id, new UpdateTaskRequest(Acceptance: []))!;

        human.Acceptance.Should().BeEmpty();
    }

    [Fact]
    public void УбранныйКритерий_СвидетельствоОтбрасывается()
    {
        var task = TaskWithThreeCriteria();
        _sut.Update(task.Id, new UpdateTaskRequest(Evidence: [Ev("c1"), Ev("c2")]));

        var after = _sut.Update(task.Id, new UpdateTaskRequest(Acceptance: [new("c2", "ui_probe без находок")]))!;

        after.Evidence.Select(e => e.CriterionId).Should().Equal("c2");
    }

    [Fact]
    public void СтарыйTasksJson_БезПолей_ЧитаетсяПустымиСписками()
    {
        var item = System.Text.Json.JsonSerializer.Deserialize<TaskItem>(
            """{"id":"old-1","title":"старая"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        item.Acceptance.Should().BeEmpty();
        item.Evidence.Should().BeEmpty();
    }

    [Fact]
    public void ТекстКритерияПустой_ОтказНаСоздании()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("t", Acceptance: [new("", "  ")]));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ЯвныйNullВJson_ЧитаетсяПустымиСпискамиБезПадения()
    {
        var item = System.Text.Json.JsonSerializer.Deserialize<TaskItem>(
            """{"id":"old-2","title":"после ручной правки","acceptance":null,"evidence":null}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        item.Acceptance.Should().NotBeNull().And.BeEmpty();
        item.Evidence.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Исполнитель_ТеЖеКритерииВДругомПорядке_Проходит()
    {
        var task = TaskWithThreeCriteria();
        var reordered = task.Acceptance.AsEnumerable().Reverse().ToList();

        var updated = _sut.Update(task.Id, new UpdateTaskRequest(Acceptance: reordered),
            isAgentCall: true, callerIsExecutor: true)!;

        updated.Acceptance.Should().HaveCount(3);
    }

    [Fact]
    public void SameCriteria_РазныйТекстИлиСоставПриТомЖеЧисле_НеРавны()
    {
        AcceptanceCriterion[] a = [new("c1", "x"), new("c2", "y")];

        AcceptanceRules.SameCriteria(a, [new("c2", "y"), new("c1", "x")]).Should().BeTrue();
        AcceptanceRules.SameCriteria(a, [new("c1", "x"), new("c2", "иначе")]).Should().BeFalse();
        AcceptanceRules.SameCriteria(a, [new("c1", "x"), new("c3", "y")]).Should().BeFalse();
        AcceptanceRules.SameCriteria(a, [new("c1", "x")]).Should().BeFalse();
    }
}
