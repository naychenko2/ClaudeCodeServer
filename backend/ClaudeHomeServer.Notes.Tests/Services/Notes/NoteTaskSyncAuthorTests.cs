using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Автор задачи, промоутнутой из чекбокса заметки (S2b-1, доработка 2): NoteTaskSyncService
/// обязан донести явный признак пути вызова до моста. Человеческий путь (createdByHuman: true)
/// ставит CreatedByHuman, агентный и серверный (false) — нет. Мост подменён заглушкой, которая
/// запоминает запрос; сквозная проверка «мост → CreatedByUserId» — в TaskBridgeAuthorTests.
/// </summary>
public class NoteTaskSyncAuthorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _userId;
    private readonly NotesService _notes;
    private readonly CapturingBridge _bridge = new();
    private readonly NoteTaskSyncService _sut;

    public NoteTaskSyncAuthorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "note_task_author_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userId = users.GetFirst()!.Id;
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        _notes = new NotesService(projects, config, NullLogger<NotesService>.Instance, null,
            new ProjectAccessService(projects, new NoSessions(),
                new SharedProjectsGate(config, new FeatureFlagService(users))));
        _sut = new NoteTaskSyncService(_notes, _bridge, projects, null!, new NoopNotifier(),
            NullLogger<NoteTaskSyncService>.Instance);

        var personal = Path.Combine(_dir, "notes", _userId);
        Directory.CreateDirectory(personal);
        File.WriteAllText(Path.Combine(personal, "Дела.md"), "# Дела\n\n- [ ] Полить цветы\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (string NoteId, int Line) Checkbox()
    {
        var id = _notes.GetSummaries(_userId, "personal", null).Single(n => n.Title == "Дела").Id;
        return (id, _sut.ListForNote(_userId, id).Single().Line);
    }

    [Fact]
    public async Task Promote_ЧеловеческийПуть_ПередаётПризнакВМост()
    {
        var (id, line) = Checkbox();

        await _sut.PromoteAsync(_userId, id, line, createdByHuman: true);

        _bridge.Created.Should().ContainSingle().Which.CreatedByHuman.Should().BeTrue();
    }

    [Fact]
    public async Task Promote_АгентныйИСерверныйПуть_ПризнакаНет()
    {
        var (id, line) = Checkbox();

        await _sut.PromoteAsync(_userId, id, line, createdByHuman: false);

        _bridge.Created.Should().ContainSingle().Which.CreatedByHuman.Should().BeFalse();
    }

    private sealed class CapturingBridge : INoteTaskBridge
    {
        public List<NoteTaskCreateRequest> Created { get; } = [];

        public IReadOnlyList<NoteTaskRef> GetBySourceNote(string ownerId, string noteId) => [];

        public NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req)
        {
            Created.Add(req);
            return new NoteTaskRef("task-1", req.Title, req.SourceNoteLine, req.DueDate, req.Status, req.Recurrence,
                NoteTaskKind.Task, projectId);
        }

        public NoteTaskRef? Update(string ownerId, string taskId, NoteTaskUpdateRequest req) => null;

        public NoteTaskRef? SpawnNextOccurrence(string ownerId, string completedTaskId) => null;
    }

    private sealed class NoopNotifier : INotesHubNotifier
    {
        public Task BroadcastNotesChangedAsync(string userId, string action, string noteId) => Task.CompletedTask;
        public Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null) => Task.CompletedTask;
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }
}
