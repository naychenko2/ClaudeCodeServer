using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2c-5: боковые пути записи заметок. После S2c-7 редактор общего проекта сможет писать
/// содержимое заметок (WriteContent), но через эти пути запись задач и чужих источников
/// открыта быть не должна:
/// (а) мост «чекбокс → задача» (promote/toggle/set-due) — owner-Write, проверка явная в мосту;
/// (б) уборка истёкших не берёт общие проекты не-владельца;
/// (в) Move между источниками — решение владельца (перенос внутри источника — дверь
///     содержимого, с S2c-7 на WriteContent, см. «Редактор_ПереносВнутриПроекта_Допустим»);
/// (г) каскад ссылок (удаление/переименование) пишет только в источниках с WriteContent у вызывающего;
/// (д) события чекбокса проектной заметки уходят в группу проекта (дефект 540f8158);
/// конкурентность: Delete (примитив уборки) и Update одной заметки — под одним замком _writeGates (S2c-2).
///
/// Край HTTP (маппинг 403) покрыт SharedProjectNotesAccessTests; здесь — граница на уровне сервиса.
/// Полный хост в этом тестовом проекте не поднимается по его построению (см. комментарий
/// SharedProjectNotesAccessTests), TestWebApplicationFactory здесь не нужен.
/// </summary>
public class NotesSideWritePathsTests : IDisposable
{
    private const string TodoNote = "Дела проекта";
    private const string TargetNote = "Цель";
    private const string ExpiredNote = "Истекшая";
    private const string LinkNote = "Ссылка";
    private const string PersonalNote = "Моё";

    private readonly string _dir;
    private readonly string _p1Root;
    private readonly string _p2Root;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _p1;
    private readonly string _p2;

    public NotesSideWritePathsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_side_paths_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = Config();

        // При пустом users.json UserStore создаёт дефолтного admin — он и есть владелец проектов
        _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s2c5-editor", "pwd", "user").Id;
        _readerId = _users.Add("s2c5-reader", "pwd", "user").Id;
        // Флаг «Общие проекты» включён у всех: в частности он нужен владельцу, чтобы видеть
        // чужой проект P2 участником (тесты каскада), а не только участникам
        foreach (var id in new[] { _ownerId, _editorId, _readerId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));

        _p1Root = Path.Combine(_dir, "p1");
        _p2Root = Path.Combine(_dir, "p2");
        Directory.CreateDirectory(Path.Combine(_p1Root, "notes"));
        Directory.CreateDirectory(Path.Combine(_p2Root, "notes"));

        File.WriteAllText(Path.Combine(_p1Root, "notes", TodoNote + ".md"), $"# {TodoNote}\n\n- [ ] Проектная задача\n");
        File.WriteAllText(Path.Combine(_p1Root, "notes", TargetNote + ".md"), $"# {TargetNote}\n\nТекст цели.\n");
        // Большая исходная содержимая: окно между проверкой файла и записью в Update
        // должна ловить параллельный Delete без замка (тест конкурентности)
        File.WriteAllText(Path.Combine(_p1Root, "notes", ExpiredNote + ".md"),
            "---\nexpires: 2020-01-01T00:00:00Z\n---\n# " + ExpiredNote + "\n\n" + new string('а', 2_000_000) + "\n");
        File.WriteAllText(Path.Combine(_p2Root, "notes", LinkNote + ".md"), $"# {LinkNote}\n\nСм. [[{TargetNote}]].\n");

        _p1 = _projects.Create("Проект 1", _p1Root, _ownerId, "owner").Id;
        // P2 — чужой проект (владелец — читатель): вызывающий может быть в нём только
        // участником (reader/editor), иначе tests (в)/(г) проверяли бы владение, а не участность
        _p2 = _projects.Create("Проект 2", _p2Root, _readerId, "reader").Id;

        var personal = Path.Combine(_dir, "notes", _editorId);
        Directory.CreateDirectory(personal);
        File.WriteAllText(Path.Combine(personal, PersonalNote + ".md"), $"# {PersonalNote}\n\n- [ ] Личная задача\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();

    // Новый экземпляр сервиса на каждый тест: кэш модели в NotesService живёт 2 с и на
    // старом экземпляре вернул бы модель, построенную до изменения состава участников.
    private NotesService Service() =>
        new(_projects, Config(), NullLogger<NotesService>.Instance, null,
            new ProjectAccessService(_projects, new NoSessions(), new SharedProjectsGate(_config, new FeatureFlagService(_users))));

    private NoteTaskSyncService Sync(NotesService notes, CapturingBridge bridge, CapturingNotifier notifier) =>
        new(notes, bridge, _projects,
            new NotesKnowledgeService(new NoDify(), notes, _users, _config, NullLogger<NotesKnowledgeService>.Instance),
            notifier, NullLogger<NoteTaskSyncService>.Instance);

    private static void AddMember(ProjectManager projects, string projectId, string userId, ProjectRole role)
    {
        var members = projects.GetById(projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember { UserId = userId, Role = role, AddedBy = projects.GetById(projectId)!.OwnerId! });
        projects.SetMembers(projectId, members);
    }

    private static string NoteId(NotesService sut, string userId, string sourceKey, string title) =>
        sut.GetSummaries(userId, sourceKey, null).Single(n => n.Title == title).Id;

    // --- (а) мост «чекбокс → задача»: редактор общего проекта отказён, задача не создана ---

    [Fact]
    public async Task Редактор_ПромутЧекбоксПроектнойЗаметки_ОтказИЗадачиНет()
    {
        AddMember(_projects, _p1, _editorId, ProjectRole.Editor);
        var sut = Service();
        var bridge = new CapturingBridge();
        var sync = Sync(sut, bridge, new CapturingNotifier());
        var id = NoteId(sut, _ownerId, _p1, TodoNote);
        var line = sync.ListForNote(_editorId, id).Single().Line;

        Func<Task> act = () => sync.PromoteAsync(_editorId, id, line, createdByHuman: true);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // Ровно этот тест краснеет без явной проверки в PromoteAsync: без неё чекбокс
        // заметки редактора создаёт задачу (личную, мимо воли владельца)
        bridge.Created.Should().BeEmpty();
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("set-due")]
    public async Task Редактор_ТогглИСрокПроектнойЗаметки_ОтказФайлНеизменён(string op)
    {
        AddMember(_projects, _p1, _editorId, ProjectRole.Editor);
        var sut = Service();
        var sync = Sync(sut, new CapturingBridge(), new CapturingNotifier());
        var id = NoteId(sut, _ownerId, _p1, TodoNote);
        var line = sync.ListForNote(_editorId, id).Single().Line;

        Func<Task> act = op == "toggle"
            ? () => sync.ToggleAsync(_editorId, id, line, done: true)
            : () => sync.SetDueAsync(_editorId, id, line, "2030-01-01");
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // Defense для S2c-7: сегодня закрыто замком Write в ProjectAccessService, после
        // перевода notes.Update на WriteContent закрыто этой же явной проверкой
        File.ReadAllText(Path.Combine(_p1Root, "notes", TodoNote + ".md")).Should().Contain("- [ ] Проектная задача");
    }

    [Fact]
    public async Task Владелец_ПромутЧекбоксПроектнойЗаметки_ЗадачаСоздана()
    {
        var sut = Service();
        var bridge = new CapturingBridge();
        var sync = Sync(sut, bridge, new CapturingNotifier());
        var id = NoteId(sut, _ownerId, _p1, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;

        var created = await sync.PromoteAsync(_ownerId, id, line, createdByHuman: true);

        // Регрессия: owner-проверка не закрывает путь владельцу
        bridge.Created.Should().ContainSingle();
        created.ProjectId.Should().Be(_p1);
    }

    [Fact]
    public async Task Редактор_ПромутЧекбоксСвоейЛичнойЗаметки_ЗадачаСоздана()
    {
        AddMember(_projects, _p1, _editorId, ProjectRole.Editor);
        var sut = Service();
        var bridge = new CapturingBridge();
        var sync = Sync(sut, bridge, new CapturingNotifier());
        var id = NoteId(sut, _editorId, "personal", PersonalNote);
        var line = sync.ListForNote(_editorId, id).Single().Line;

        await sync.PromoteAsync(_editorId, id, line, createdByHuman: true);

        // Личный vault — своё: путь открыт и после owner-проверки (она про чужие проекты)
        bridge.Created.Should().ContainSingle();
    }

    // --- (б) уборка истёкших: общие проекты не-владельца в неё не попадают ---

    [Fact]
    public void УборкаИстёкших_РедакторНеТронетПроект_ВладелецУберёт()
    {
        AddMember(_projects, _p1, _editorId, ProjectRole.Editor);
        var sut = Service();
        var now = DateTime.UtcNow;

        // Мутация (б): снять фильтр SourcesFor(Write) в GetExpiredNotes → этот тест красный:
        // редактор видит WriteContent-источник и получил бы чужую заметку на удаление
        sut.GetExpiredNotes(_editorId, now).Should().NotContain(n => n.Title == ExpiredNote);
        sut.GetExpiredNotes(_ownerId, now).Should().Contain(n => n.Title == ExpiredNote);
    }

    // --- (в) перенос между источниками: только владелец; внутри источника — отдельная дверь ---

    [Fact]
    public void ВладелецПроекта_ПеренесётЗаметкуВЧужойПроект_ОтказФайлНаМесте()
    {
        // Владелец P1 — читатель в P2: основная дверь P1 пройдена, цель — отказана
        AddMember(_projects, _p2, _ownerId, ProjectRole.Reader);
        var sut = Service();
        var id = NoteId(sut, _ownerId, _p1, TargetNote);

        Action act = () => sut.Move(_ownerId, id, null, _p2);
        act.Should().Throw<UnauthorizedAccessException>();

        File.Exists(Path.Combine(_p1Root, "notes", TargetNote + ".md")).Should().BeTrue();
        File.Exists(Path.Combine(_p2Root, "notes", TargetNote + ".md")).Should().BeFalse();
    }

    [Fact]
    public void Редактор_ПеренесётСвоюЛичнуюЗаметкуВПроект_ОтказФайлВЛичномVault()
    {
        AddMember(_projects, _p1, _editorId, ProjectRole.Editor);
        var sut = Service();
        var id = NoteId(sut, _editorId, "personal", PersonalNote);

        Action act = () => sut.Move(_editorId, id, null, _p1);
        act.Should().Throw<UnauthorizedAccessException>();

        File.Exists(Path.Combine(_dir, "notes", _editorId, PersonalNote + ".md")).Should().BeTrue();
        File.Exists(Path.Combine(_p1Root, "notes", PersonalNote + ".md")).Should().BeFalse();
    }

    [Fact]
    public void Владелец_ПереносВнутриСвоегоПроекта_Допустим()
    {
        var sut = Service();
        var id = NoteId(sut, _ownerId, _p1, TargetNote);

        var moved = sut.Move(_ownerId, id, "Архив");

        moved.Should().NotBeNull();
        File.Exists(Path.Combine(_p1Root, "notes", "Архив", TargetNote + ".md")).Should().BeTrue();
    }

    [Fact]
    public void Редактор_ПереносВнутриПроекта_Допустим_S2c7()
    {
        AddMember(_projects, _p1, _editorId, ProjectRole.Editor);
        var sut = Service();
        var id = NoteId(sut, _ownerId, _p1, TargetNote);

        // S2c-7: перенос ВНУТРИ источника — обычная дверь содержимого заметки (WriteContent);
        // перенос между источниками остаётся на Write (тесты выше — «ОтказФайлНаМесте»)
        var moved = sut.Move(_editorId, id, "Архив");

        moved.Should().NotBeNull();
        File.Exists(Path.Combine(_p1Root, "notes", "Архив", TargetNote + ".md")).Should().BeTrue();
    }

    // --- (г) каскад ссылок: только источники с WriteContent у вызывающего ---

    [Fact]
    public void Переименование_КаскадНеПишетВЧужойПроектНаЧтение()
    {
        // Вызывающий — владелец P1 (где цель) и читатель в P2 (откуда ссылка на неё)
        AddMember(_projects, _p2, _ownerId, ProjectRole.Reader);
        var sut = Service();
        var id = NoteId(sut, _ownerId, _p1, TargetNote);

        sut.Update(_ownerId, id, new UpdateNoteRequest(Title: "Новая цель"));

        // Мутация (г): снять фильтр CanWriteContentIn в каскаде UpdateCore → красный:
        // авто-обновление ссылок записалось бы в проект, где вызывающий только читает
        File.ReadAllText(Path.Combine(_p2Root, "notes", LinkNote + ".md")).Should().Contain($"[[{TargetNote}]]");
    }

    [Fact]
    public void УдалениеСсылок_КаскадНеПишетВЧужойПроектНаЧтение()
    {
        AddMember(_projects, _p2, _ownerId, ProjectRole.Reader);
        var sut = Service();
        var id = NoteId(sut, _ownerId, _p1, TargetNote);

        sut.RemoveWikilinksTo(_ownerId, TargetNote, id);

        // Тот же фильтр в RemoveWikilinksTo
        File.ReadAllText(Path.Combine(_p2Root, "notes", LinkNote + ".md")).Should().Contain($"[[{TargetNote}]]");
    }

    [Fact]
    public void Переименование_КаскадПишетПроектГдеЕстьWriteContent()
    {
        // Граница положительная: вызывающий — редактор в P2, его правки там допустимы
        AddMember(_projects, _p2, _ownerId, ProjectRole.Editor);
        var sut = Service();
        var id = NoteId(sut, _ownerId, _p1, TargetNote);

        sut.Update(_ownerId, id, new UpdateNoteRequest(Title: "Новая цель"));

        File.ReadAllText(Path.Combine(_p2Root, "notes", LinkNote + ".md")).Should().Contain("[[Новая цель]]");
    }

    // --- (д) события чекбоксов заметки проекта — в группу проекта (дефект 540f8158) ---

    [Fact]
    public async Task ТогглЧекбоксаПроектнойЗаметки_СобытиеСПроектом()
    {
        var sut = Service();
        var notifier = new CapturingNotifier();
        var sync = Sync(sut, new CapturingBridge(), notifier);
        var id = NoteId(sut, _ownerId, _p1, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;

        await sync.ToggleAsync(_ownerId, id, line, done: true);

        // Мутация (д): убрать ProjectOf в ToggleAsync → красный (ProjectId null)
        notifier.Notes.Should().ContainSingle()
            .Which.ProjectId.Should().Be(_p1);
        File.ReadAllText(Path.Combine(_p1Root, "notes", TodoNote + ".md")).Should().Contain("- [x]");
    }

    [Fact]
    public async Task ТогглЧекбоксаЛичнойЗаметки_СобытиеБезПроекта()
    {
        var sut = Service();
        var notifier = new CapturingNotifier();
        var sync = Sync(sut, new CapturingBridge(), notifier);
        var id = NoteId(sut, _editorId, "personal", PersonalNote);
        var line = sync.ListForNote(_editorId, id).Single().Line;

        await sync.ToggleAsync(_editorId, id, line, done: true);

        // Личный vault — только владельцу, как раньше
        notifier.Notes.Should().ContainSingle().Which.ProjectId.Should().BeNull();
    }

    [Fact]
    public async Task ОбратнаяЗаписьЗадачи_СобытиеЗаметкиПроектаСПроектом()
    {
        var sut = Service();
        var notifier = new CapturingNotifier();
        var sync = Sync(sut, new CapturingBridge(), notifier);
        var id = NoteId(sut, _ownerId, _p1, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;

        await sync.SyncTaskToNoteAsync(_ownerId, new TaskItem
        {
            ProjectId = _p1,
            OwnerId = _ownerId,
            Title = "Проектная задача",
            Status = TaskItemStatus.Done,
            SourceNoteId = id,
            SourceNoteLine = line,
        });

        // Мутация (д): убрать ProjectOf в SyncTaskToNoteAsync → красный
        notifier.Notes.Should().ContainSingle().Which.ProjectId.Should().Be(_p1);
        File.ReadAllText(Path.Combine(_p1Root, "notes", TodoNote + ".md")).Should().Contain("- [x]");
    }

    // --- Конкурентность: Delete (примитив уборки) и Update одной заметки — под одним замком ---

    [Fact]
    public async Task УборкаИUpdateОднойЗаметки_НеВозрождаютУдалённое()
    {
        var sut = Service();
        // id истёкшей заметки — через GetExpiredNotes: из модели (GetSummaries) они исключены
        // намеренно (NotesService.cs:421), а убирает их именно уборка
        var id = sut.GetExpiredNotes(_ownerId, DateTime.UtcNow).Single(n => n.Title == ExpiredNote).Id;
        var full = Path.Combine(_p1Root, "notes", ExpiredNote + ".md");
        var newContent = "# " + ExpiredNote + "\n\n" + new string('б', 2_000_000) + "\n";

        // Старт обеих сторон по одному сигналу (без Task.Delay): Delete — то, что делает
        // фоновая уборка (NoteExpiryService), Update — обычная запись заметки.
        // Update регистрируем первым: TCS возобновляет продолжения в порядке регистрации,
        // и без замка Delete попадает в окно между проверкой файла и записью Update
        // (большое содержимое держит окно миллисекунды). С замком — Delete ждёт замок,
        // исход всегда согласованный.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upd = Task.Run(async () => { await start.Task; return sut.Update(_ownerId, id, new UpdateNoteRequest(Content: newContent)); });
        var del = Task.Run(async () => { await start.Task; return sut.Delete(_ownerId, id); });
        start.SetResult();
        var deleted = await del;
        await upd;

        // Согласованных исхода два: заметка удалена (или её правка состоялась ДО удаления),
        // или правка состоялась и заметка цела. Полуреального состояния нет:
        // Delete вернул true, а файл на диске — правка воскресила удалённое (без замка — да)
        if (deleted)
            File.Exists(full).Should().BeFalse("удалённую заметку правка не должна возрождать");
        if (File.Exists(full))
            File.ReadAllText(full).Should().Be(newContent);
    }

    // --- Заглушки ---

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

    private sealed class CapturingNotifier : INotesHubNotifier
    {
        public List<(string UserId, string Action, string NoteId, string? ProjectId)> Notes { get; } = [];

        public Task BroadcastNotesChangedAsync(string userId, string action, string noteId, string? projectId = null)
        {
            Notes.Add((userId, action, noteId, projectId));
            return Task.CompletedTask;
        }

        public Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null) =>
            Task.CompletedTask;
    }

    private sealed class NoDify : IKnowledgeIndex
    {
        public bool IsConfigured => false;

        public Task<string> CreateDatasetAsync(string name, string permission = "only_me", string? description = null,
            string? indexingTechnique = null) => throw new NotSupportedException();

        public Task DeleteDocumentAsync(string datasetId, string documentId) => throw new NotSupportedException();

        public Task DeleteDatasetAsync(string datasetId) => throw new NotSupportedException();

        public Task RenameDatasetAsync(string datasetId, string newName) => throw new NotSupportedException();

        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName, string content,
            List<string>? tags = null, IReadOnlyDictionary<string, string>? metadata = null) => throw new NotSupportedException();

        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query, int topK = 8,
            IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and") => throw new NotSupportedException();
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }
}
