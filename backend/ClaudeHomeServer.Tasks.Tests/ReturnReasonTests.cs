using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

// Структурированная причина возврата в вердикте: закрытый список, только при returned, other — с Notes.
public class ReturnReasonTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _sut;

    public ReturnReasonTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "return_reason_tests_" + Guid.NewGuid().ToString("N"));
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

    private TaskItem NewTask() => _sut.Create(null, "u", new CreateTaskRequest("ревью"));

    private static TaskVerification V(string? verdict, string? reason, string? notes = "замечания") =>
        new() { Verdict = verdict, ReturnReason = reason, Notes = notes, VerifiedAt = DateTime.UtcNow };

    [Theory]
    [InlineData("size")]
    [InlineData("side-effect")]
    [InlineData("contract")]
    [InlineData("robustness")]
    [InlineData("quality")]
    [InlineData("other")]
    public void Returned_ПричинаИзСписка_СохраняетсяИЧитаетсяОбратно(string reason)
    {
        var task = NewTask();

        // вердикт хранится у закрытой задачи (переоткрытие его сбрасывает)
        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Verification: V(TaskVerdicts.Returned, reason)));

        var stored = _sut.GetById(task.Id)!.Verification!;
        stored.Verdict.Should().Be(TaskVerdicts.Returned);
        stored.ReturnReason.Should().Be(reason);
    }

    [Fact]
    public void Returned_ПричинаВнеСписка_Отказ()
    {
        var task = NewTask();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Verification: V(TaskVerdicts.Returned, "придумал")));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("придумал");
        _sut.GetById(task.Id)!.Verification.Should().BeNull("при отказе состояние не меняется");
    }

    [Theory]
    [InlineData(TaskVerdicts.Accepted)]
    [InlineData(TaskVerdicts.AcceptedWithRemarks)]
    [InlineData(TaskVerdicts.Withdrawn)]
    [InlineData(null)]
    public void НеReturned_ПричинуЗадатьНельзя(string? verdict)
    {
        var task = NewTask();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Verification: V(verdict, "size")));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("returned");
    }

    [Fact]
    public void Returned_БезПричины_Принимается_СтарыеДанные()
    {
        var task = NewTask();

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Verification: V(TaskVerdicts.Returned, null)));

        _sut.GetById(task.Id)!.Verification!.ReturnReason.Should().BeNull();
    }

    [Fact]
    public void Returned_ПричинаСПробелами_СохраняетсяБезПробелов()
    {
        var task = NewTask();

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Verification: V(TaskVerdicts.Returned, " size ")));

        _sut.GetById(task.Id)!.Verification!.ReturnReason.Should().Be("size",
            "санирование в TaskManager: REST и MCP получают один исход");
    }

    [Theory]
    [InlineData(TaskVerdicts.Returned)]
    [InlineData(TaskVerdicts.Accepted)]
    public void ПричинаИзОдногоПробела_ВСторНеПопадает_ХранитсяNull(string verdict)
    {
        var task = NewTask();

        _sut.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done, Verification: V(verdict, "   ")));

        _sut.GetById(task.Id)!.Verification!.ReturnReason.Should().BeNull();
    }

    [Fact]
    public void Создание_ПричинаСПробелами_СохраняетсяБезПробелов()
    {
        // Вердикт хранится только у закрытой задачи (Create обнуляет его у незакрытой)
        var task = _sut.Create(null, "u", new CreateTaskRequest("t",
            Status: TaskItemStatus.Done,
            Verification: V(TaskVerdicts.Returned, " quality ")));

        task.Verification!.ReturnReason.Should().Be("quality");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Other_БезПояснения_Отказ(string? notes)
    {
        var task = NewTask();

        var act = () => _sut.Update(task.Id, new UpdateTaskRequest(Verification: V(TaskVerdicts.Returned, "other", notes)));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("notes");
    }

    [Fact]
    public void Создание_СВердиктомAcceptedИПричиной_Отказ()
    {
        var act = () => _sut.Create(null, "u", new CreateTaskRequest("t",
            Verification: V(TaskVerdicts.Accepted, "quality")));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void СтарыйJson_БезReturnReason_ЧитаетсяПустойПричиной()
    {
        var v = System.Text.Json.JsonSerializer.Deserialize<TaskVerification>(
            """{"verifiedAt":"2026-10-01T00:00:00Z","verdict":"returned","notes":"старое"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        v.ReturnReason.Should().BeNull();
    }
}
