using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S3-3c1: человеческие Create/Update/Delete заметки ОБЩЕГО проекта пишут в журнал правок
/// (IHumanEditJournal, S3-3) по образцу S3-3b (FilesController.RecordHumanEdit). Агентские
/// вызовы (без byHuman — так зовёт NotesToolset) и личный vault — молчат; отказ 403/409
/// до записи — молчит. Ключ журнала: root = RootPath проекта (не его notes/), rel =
/// «notes/&lt;файл&gt;.md», автор = id вызывающего.
///
/// Фейк-журнал записывает полученные вызовы как есть: правила отбора (общий проект,
/// git-репозиторий, автор-участник) — ответственность самого HumanEditCommitter, здесь не
/// проверяются. Стенд — как в SharedProjectNotesWriteContentTests: границы на уровне сервиса,
/// полный хост в этом тестовом проекте не поднимается по его построению.
/// </summary>
public class NotesHumanEditJournalTests : IDisposable
{
    private const string ProjectNote = "Заметка проекта";
    private const string NoteBody = "# Заметка проекта\n\nТекст проектной заметки.\n";

    private readonly string _dir;
    private readonly string _projectRoot;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _editorAId;
    private readonly string _editorBId;
    private readonly string _readerId;
    private readonly string _projectId;

    public NotesHumanEditJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s33c1_notes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = Config();

        // При пустом users.json UserStore создаёт дефолтного admin — он и есть владелец проекта
        _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("s33c1-editor", "pwd", "user").Id;
        _editorAId = _users.Add("s33c1-editor-a", "pwd", "user").Id;
        _editorBId = _users.Add("s33c1-editor-b", "pwd", "user").Id;
        _readerId = _users.Add("s33c1-reader", "pwd", "user").Id;
        foreach (var id in new[] { _ownerId, _editorId, _editorAId, _editorBId, _readerId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));

        _projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "notes"));
        File.WriteAllText(Path.Combine(_projectRoot, "notes", ProjectNote + ".md"), NoteBody);
        _projectId = _projects.Create("Проект S3-3c1", _projectRoot, _ownerId, "owner").Id;
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

    // Новый экземпляр сервиса на каждый тест (та же причина, что в стенде), с фейк-журналом
    // вместо реального HumanEditCommitter: здесь проверяется сам вызов Record, а не git-коммит.
    private NotesService Service(FakeJournal journal) =>
        new(_projects, Config(), NullLogger<NotesService>.Instance, null,
            new ProjectAccessService(_projects, new NoSessions(), new SharedProjectsGate(_config, new FeatureFlagService(_users))),
            journal);

    private void AddMember(string userId, ProjectRole role)
    {
        var members = _projects.GetById(_projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember { UserId = userId, Role = role, AddedBy = _ownerId });
        _projects.SetMembers(_projectId, members);
    }

    private string ProjectNoteId(NotesService sut) =>
        sut.GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == ProjectNote).Id;

    private string ProjectFile(string rel) => Path.Combine(_projectRoot, "notes", rel);

    // --- Полный цикл человека: каждая состоявшаяся правка — ровно один Record (c1) ---

    [Fact]
    public void ПолныйЦиклЧеловека_КаждаяПравка_РовноОдинRecord()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        var created = sut.Create(_editorId, new CreateNoteRequest("Заметка журнала", "тело", _projectId), byHuman: true);

        // Ровно одна запись: корень — RootPath проекта (не его notes/), путь — от корня,
        // автор — id вызывающего
        journal.Calls.Should().ContainSingle()
            .Which.Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Заметка журнала.md", _editorId));

        // Правка содержимого — ровно один новый Record того же пути
        var updated = sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: "Правка"), byHuman: true);
        updated.Should().NotBeNull();
        journal.Calls.Should().HaveCount(2);
        journal.Calls[^1].Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Заметка журнала.md", _editorId));

        // Удаление — ровно один новый Record: путь удалён, это тоже правка
        sut.Delete(_editorId, created.Id, byHuman: true).Should().BeTrue();
        journal.Calls.Should().HaveCount(3);
        journal.Calls[^1].Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Заметка журнала.md", _editorId));
    }

    // --- Переименование по заголовку: старый и новый путь (c1) ---

    [Fact]
    public void СменаЗаголовка_ДваRecord_СтарыйИНовыйПуть()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Заметка создана агентом (без byHuman) — в журнале пусто; переименование человеком даёт ровно 2
        var created = sut.Create(_editorId, new CreateNoteRequest("Старая", "тело", _projectId));
        journal.Calls.Should().BeEmpty();

        var renamed = sut.Update(_editorId, created.Id, new UpdateNoteRequest(Title: "Новая"), byHuman: true);
        renamed.Should().NotBeNull();

        // Старого файла больше нет — журнал обязан видеть и его удаление, и новый путь
        journal.Calls.Should().HaveCount(2);
        journal.Calls[0].Rel.Should().Be("notes/Старая.md");
        journal.Calls[1].Rel.Should().Be("notes/Новая.md");
        journal.Calls.Should().AllSatisfy(c =>
        {
            c.ProjectId.Should().Be(_projectId);
            c.Root.Should().Be(_projectRoot);
            c.UserId.Should().Be(_editorId);
        });
    }

    // --- Личный vault: ни одной записи (c1) ---

    [Fact]
    public void ЛичныйVault_НиОднойЗаписиВЖурнал()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);

        var created = sut.Create(_ownerId, new CreateNoteRequest("Личная", "тело"), byHuman: true);
        sut.Update(_ownerId, created.Id, new UpdateNoteRequest(Content: "правка"), byHuman: true).Should().NotBeNull();
        sut.Delete(_ownerId, created.Id, byHuman: true).Should().BeTrue();

        journal.Calls.Should().BeEmpty("личный vault — вне журнала правок общего проекта");
    }

    // --- Отказ 403 и 409: правки не было — журнал молчит (c1) ---

    [Fact]
    public void Отказ403_ИОтказ409_НиОднойЗаписиВЖурнал()
    {
        AddMember(_editorId, ProjectRole.Editor);
        AddMember(_readerId, ProjectRole.Reader);
        var journal = new FakeJournal();
        var sut = Service(journal);
        var id = ProjectNoteId(sut);

        // 403: читатель без WriteContent — ResolveRoot бросает до любой записи файла
        Action act = () => sut.Create(_readerId, new CreateNoteRequest("Чужая", "тело", _projectId), byHuman: true);
        act.Should().Throw<UnauthorizedAccessException>();
        act = () => sut.Update(_readerId, id, new UpdateNoteRequest(Content: "правка читателя"), byHuman: true);
        act.Should().Throw<UnauthorizedAccessException>();
        act = () => sut.Delete(_readerId, id, byHuman: true);
        act.Should().Throw<UnauthorizedAccessException>();

        // 409: If-Match против фактического хеша — NoteConflictException ДО записи, файл не тронут
        act = () => sut.Update(_editorId, id, new UpdateNoteRequest(Content: "правка"), "не-тот-хеш", byHuman: true);
        act.Should().Throw<NoteConflictException>();

        journal.Calls.Should().BeEmpty("отказанная правка не состоялась — журнал её видеть не должен");
        File.ReadAllText(ProjectFile(ProjectNote + ".md")).Should().Contain("Текст проектной заметки.");
    }

    // --- Агентский вызов без byHuman: ни одной записи (c2) ---

    [Fact]
    public void АгентскийВызовБезByHuman_НиОднойЗаписиВЖурнал()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Так зовёт NotesToolset (NotesToolset.cs:155/:173/:194) — без byHuman
        var created = sut.Create(_editorId, new CreateNoteRequest("Агентская", "тело", _projectId));
        sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: "правка")).Should().NotBeNull();
        sut.Delete(_editorId, created.Id).Should().BeTrue();

        journal.Calls.Should().BeEmpty("журнал — только правки человека (byHuman); агентские вызовы молчат");
    }

    // --- Конкурентность (c3): порядок журнала обязан совпадать с порядком записей файла ---

    /// <summary>
    /// Два Update одной заметки параллельно от A и B. Record делается ВНУТРИ замка id (в
    /// UpdateCore): первый, дошедший до журнала, удерживается фейк-журналом внутри замка, и
    /// проигравший не может записать файл раньше — порядок вызовов Record совпадает с
    /// порядком записей файла. Мутация «Record после выхода из WithWriteGates»: проигравший
    /// записывает файл и журнал, пока первый удержан, — его Record доходит раньше, и автором
    /// последней правки журнала оказывается проигравший (проверка ниже краснеет).
    /// </summary>
    [Fact]
    public async Task Гонка_ДвеПравкиОднойЗаметки_АвторПоследнегоRecordРавенАвторуИтоговогоФайла()
    {
        AddMember(_editorAId, ProjectRole.Editor);
        AddMember(_editorBId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);
        var created = sut.Create(_editorAId, new CreateNoteRequest("Заметка гонки", "тело", _projectId));
        var full = ProjectFile("Заметка гонки.md");
        var contentA = "# Заметка гонки\n\nПРАВКА_A\n";
        var contentB = "# Заметка гонки\n\nПРАВКА_B\n";

        var a = Task.Run(() => sut.Update(_editorAId, created.Id, new UpdateNoteRequest(Content: contentA), null, true));
        var b = Task.Run(() => sut.Update(_editorBId, created.Id, new UpdateNoteRequest(Content: contentB), null, true));

        // Кто первым дошёл до журнала — удерживаем его Record внутри замка (2 с). Пока он
        // удержан, второй НЕ может записать файл: дошёл до журнала раньше, чем его отпустили,
        // — порядок журнала разошёлся с порядком файлов (та и есть мутация).
        var firstUserId = await journal.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var otherTask = firstUserId == _editorAId ? b : a;
        var otherId = firstUserId == _editorAId ? _editorBId : _editorAId;
        var otherRanWhileHeld = await Task.WhenAny(otherTask, Task.Delay(1000)) == otherTask;

        journal.ReleaseFirst();
        await a;
        await b;

        otherRanWhileHeld.Should().BeFalse(
            "проигравший не мог записать файл и журнал, пока первый Record удержан внутри замка id");

        // Автор последнего Record = автор итогового содержимого файла
        journal.Calls.Should().HaveCount(2);
        var last = journal.Calls[^1];
        last.UserId.Should().Be(otherId);
        File.ReadAllText(full).Should().Contain(last.UserId == _editorAId ? "ПРАВКА_A" : "ПРАВКА_B");
    }

    /// <summary>
    /// Фейк-журнал: пишет полученные вызовы как есть. Первый Record держит вызывающую сторону
    /// внутри её замка до ReleaseFirst (или 2 с) — это наблюдатель порядка, не часть продукта.
    /// Правила отбора (общий проект, репозиторий, автор-участник) — у реального
    /// HumanEditCommitter; здесь они не повторяются.
    /// </summary>
    private sealed class FakeJournal : IHumanEditJournal
    {
        public sealed record Call(string ProjectId, string Root, string Rel, string UserId);

        public List<Call> Calls { get; } = [];
        public TaskCompletionSource<string> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first;

        public void Record(string projectId, string root, string relPath, string userId)
        {
            if (!_first)
            {
                _first = true;
                FirstStarted.TrySetResult(userId);
                // Держим первого внутри его замка: второй обязан ждать за тем же замком id
                _releaseFirst.Task.Wait(TimeSpan.FromSeconds(2));
            }
            lock (Calls) Calls.Add(new Call(projectId, root, relPath, userId));
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public HumanEditEntry? PendingAuthor(string root, string relPath) => null;
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
