using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Автор задачи, созданной через мост заметок (S2b-1, доработка 2): признак CreatedByHuman
// едет из Core-записи NoteTaskCreateRequest через TaskBridge в CreateTaskRequest. Забытая
// передача в мосте (или отсутствие признака) оставляет человеческий путь без автора.
public class TaskBridgeAuthorTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly TaskBridge _sut;

    public TaskBridgeAuthorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_bridge_author_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new TaskBridge(_tasks);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static NoteTaskCreateRequest Req(bool human) => new(
        Title: "Чекбокс", DueDate: null, Status: NoteTaskStatus.Todo,
        SourceNoteId: "note-1", SourceNoteLine: 3, Recurrence: null, CreatedByHuman: human);

    [Fact]
    public void Create_ПризнакЧеловека_АвторомСтановитсяВладелец()
    {
        var created = _sut.Create(null, "user-1", Req(human: true));

        _tasks.GetById(created.Id)!.CreatedByUserId.Should().Be("user-1");
    }

    [Fact]
    public void Create_БезПризнака_АвтораНет()
    {
        var created = _sut.Create(null, "user-1", Req(human: false));

        _tasks.GetById(created.Id)!.CreatedByUserId.Should().BeNull();
    }

    // Fail-closed: запись, собранная без указания признака, автора не получает.
    [Fact]
    public void Create_ПризнакПоУмолчанию_АвтораНет()
    {
        var req = new NoteTaskCreateRequest("Чекбокс", null, NoteTaskStatus.Todo, "note-1", 4, null);

        var created = _sut.Create(null, "user-1", req);

        _tasks.GetById(created.Id)!.CreatedByUserId.Should().BeNull();
    }
}
