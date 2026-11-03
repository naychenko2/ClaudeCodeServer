using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
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
/// S3-3c2: человеческие перенос заметки (Move, в т.ч. между источниками), перенос и удаление
/// папки (MoveFolder, DeleteFolder) — тот же журнал. Move: старый и новый путь, каждый со root
/// СВОЕГО проекта; папки — путём КАЖДОГО своего файла (путь папки в журнал не идёт, тот же
/// порядок S3-3b в FilesController); список файлов папки снимается до операции. Отказанный
/// перенос (403/404) — ни одного пути; агентские вызовы — молчат.
///
/// S3-3c1 доработка 1 (вердикт ea994f7c): пять человеческих ручек аннотаций — annotate
/// (комментарий-заметка + маркер ^id в файле документа), reply, repin, status, link-mention —
/// тот же журнал. Документ журналится только когда ^id был реально записан (новый маркер);
/// порядок Record следует порядку записей файла. Агентские вызовы тех же Core
/// (NotesToolset: Annotate/Reply/SetAnnotationStatus) — молчат; личный vault — молчит;
/// no-op правки срока (снятие несуществующего) — молчит.
///
/// S3-3c3 (хвосты дельта-ревью 5deed348, п.11): чекбокс задачи и срок в заметке — два
/// человеческих пути NoteTaskSyncService (toggle/set-due из NotesController, byHuman: true):
/// состоявшаяся правка — ровно один Record той же заметки; фоновая синхронизация и обратный
/// путь «задача → заметка» (SyncTaskToNoteAsync) — молчат; no-op (галочка уже в нужном
/// состоянии, тот же срок) — файла нет, Record нет (тот же узор, что у no-op срока); отказ
/// 403/404 — молчит; гонка «тоггл ‖ правка» той же заметки — автор последнего Record =
/// автор итогового файла (двух тогглов одной проектной заметки от разных людей в продукте
/// быть не может: мост чекбокса закрыт owner-Write для не-владельца).
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
    // S3-3c3: чанка с чекбоксом в общем проекте — путь toggle/set-due (NoteTaskSyncService)
    private const string TodoNote = "Дела проекта";
    private const string TodoBody = "# Дела проекта\n\n- [ ] Задача из чанки\n";

    private readonly string _dir;
    private readonly string _projectRoot;
    private readonly string _projectRoot2;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _editorAId;
    private readonly string _editorBId;
    private readonly string _readerId;
    private readonly string _projectId;
    private readonly string _projectId2;

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
        File.WriteAllText(Path.Combine(_projectRoot, "notes", TodoNote + ".md"), TodoBody);
        _projectId = _projects.Create("Проект S3-3c1", _projectRoot, _ownerId, "owner").Id;

        // Второй общий проект того же владельца — для переноса между источниками (S3-3c2)
        _projectRoot2 = Path.Combine(_dir, "project2");
        Directory.CreateDirectory(Path.Combine(_projectRoot2, "notes"));
        _projectId2 = _projects.Create("Проект S3-3c2", _projectRoot2, _ownerId, "owner").Id;
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

    // S3-3c3: NoteTaskSyncService поверх того же NotesService с фейк-журналом: проверяется
    // путь чекбокса/срока до Record; мост и нотификатор — заглушки (синхронизацию задач и
    // события здесь не измеряем), индексация — NoDify (не настроена, QueueSync ничего не пишет).
    private NoteTaskSyncService Sync(NotesService notes) =>
        new(notes, new CapturingBridge(), _projects,
            new NotesKnowledgeService(new NoDify(), notes, _users, _config, NullLogger<NotesKnowledgeService>.Instance),
            new NoopNotifier(), NullLogger<NoteTaskSyncService>.Instance);

    private void AddMember(string userId, ProjectRole role)
    {
        var members = _projects.GetById(_projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember { UserId = userId, Role = role, AddedBy = _ownerId });
        _projects.SetMembers(_projectId, members);
    }

    private string ProjectNoteId(NotesService sut) => ProjectNoteId(sut, ProjectNote);

    private string ProjectNoteId(NotesService sut, string title) =>
        sut.GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == title).Id;

    private string ProjectFile(string rel) => Path.Combine(_projectRoot, "notes", rel);

    // Комментарий к проектной заметке-документу — та же форма, что в
    // SharedProjectNotesWriteContentTests (офсеты-хинт, истина — сверка Text)
    private AnnotateRequest ProjectAnnotate(string comment) =>
        new(new AnnotateDocRef(_projectId, "notes/" + ProjectNote + ".md"),
            new AnnotateSelection(0, 0, "Текст проектной заметки"), Comment: comment);

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

    // --- S3-3c2: перенос заметки (c1): старый и новый путь, каждый со root своего проекта ---

    [Fact]
    public void ПереносЗаметкиВнутриПроекта_ДваRecord_СтарыйИНовыйПуть()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Заметка создана агентом (без byHuman) — в журнале пусто
        var created = sut.Create(_editorId, new CreateNoteRequest("Переносимая", "тело", _projectId, Folder: "Исходник"));
        journal.Calls.Should().BeEmpty();

        var moved = sut.Move(_editorId, created.Id, "Цель", byHuman: true);
        moved.Should().NotBeNull();

        // Оба пути переезда: старого файла больше нет — журнал обязан видеть и его удаление,
        // и новый путь
        journal.Calls.Should().HaveCount(2);
        journal.Calls[0].Rel.Should().Be("notes/Исходник/Переносимая.md");
        journal.Calls[1].Rel.Should().Be("notes/Цель/Переносимая.md");
        journal.Calls.Should().AllSatisfy(c =>
        {
            c.ProjectId.Should().Be(_projectId);
            c.Root.Should().Be(_projectRoot);
            c.UserId.Should().Be(_editorId);
        });
    }

    [Fact]
    public void ПереносМеждуДвумяОбщимиПроектами_ПоОдномуВКаждый_СоСвоимRoot()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Перенос между источниками — решение владельца (S2c-5): оба проекта его
        var created = sut.Create(_ownerId, new CreateNoteRequest("Межпроектная", "тело", _projectId));
        var moved = sut.Move(_ownerId, created.Id, null, _projectId2, byHuman: true);
        moved.Should().NotBeNull();

        // По одной записи в каждый проект, каждая со root СВОЕГО проекта
        journal.Calls.Should().HaveCount(2);
        journal.Calls[0].Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Межпроектная.md", _ownerId));
        journal.Calls[1].Should().Be(new FakeJournal.Call(_projectId2, _projectRoot2, "notes/Межпроектная.md", _ownerId));
    }

    [Fact]
    public void ПереносПроектаВЛичныйVault_ОдинRecord_СтарыйПуть()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);

        var created = sut.Create(_ownerId, new CreateNoteRequest("ВЛичный", "тело", _projectId));
        var moved = sut.Move(_ownerId, created.Id, null, "personal", byHuman: true);
        moved.Should().NotBeNull();

        // Личная сторона — вне журнала: только старый путь проекта
        journal.Calls.Should().ContainSingle()
            .Which.Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/ВЛичный.md", _ownerId));
    }

    [Fact]
    public void ПереносИзЛичногоВПроект_ОдинRecord_НовыйПуть()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);

        var created = sut.Create(_ownerId, new CreateNoteRequest("ИзЛичной", "тело"));
        var moved = sut.Move(_ownerId, created.Id, null, _projectId, byHuman: true);
        moved.Should().NotBeNull();

        // Личная сторона — вне журнала: только новый путь проекта
        journal.Calls.Should().ContainSingle()
            .Which.Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/ИзЛичной.md", _ownerId));
    }

    // --- S3-3c2: папки (c1): путь каждого файла, путь папки — нет ---

    [Fact]
    public void ПереносПапкиСДвумяФайлами_ЧетыреRecord_БезПутиПапки()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);
        for (var i = 0; i < 2; i++)
            sut.Create(_editorId, new CreateNoteRequest($"Файл {i}", $"# Тело {i}\n", _projectId, Folder: "Папка"));

        var map = sut.MoveFolder(_editorId, _projectId, "Папка", "Папка2", byHuman: true);
        map.Should().HaveCount(2);

        // Старый и новый путь каждого файла; самого пути папки в журнале нет
        journal.Calls.Should().HaveCount(4);
        journal.Calls.Select(c => c.Rel).Should().BeEquivalentTo(
            "notes/Папка/Файл 0.md", "notes/Папка2/Файл 0.md",
            "notes/Папка/Файл 1.md", "notes/Папка2/Файл 1.md");
        journal.Calls.Should().NotContain(c => c.Rel == "notes/Папка" || c.Rel == "notes/Папка2");
        journal.Calls.Should().AllSatisfy(c =>
        {
            c.ProjectId.Should().Be(_projectId);
            c.Root.Should().Be(_projectRoot);
            c.UserId.Should().Be(_editorId);
        });
    }

    [Fact]
    public void СносПапкиСДвумяФайлами_ДваRecord_БезПутиПапки()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);
        for (var i = 0; i < 2; i++)
            sut.Create(_editorId, new CreateNoteRequest($"Снос {i}", $"# Тело {i}\n", _projectId, Folder: "Снос"));

        sut.DeleteFolder(_editorId, _projectId, "Снос", byHuman: true).Should().Be(2);

        // Каждый файл — своим путём (снят до удаления); самого пути папки в журнале нет
        journal.Calls.Should().HaveCount(2);
        journal.Calls.Select(c => c.Rel).Should().BeEquivalentTo("notes/Снос/Снос 0.md", "notes/Снос/Снос 1.md");
        journal.Calls.Should().NotContain(c => c.Rel == "notes/Снос");
    }

    // --- S3-3c2: агентские переносы без byHuman и отказы: журнал молчит (c2) ---

    [Fact]
    public void ПереносыБезByHuman_НиОднойЗаписиВЖурнал()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        var created = sut.Create(_editorId, new CreateNoteRequest("Агентский перенос", "тело", _projectId, Folder: "А"));
        // Так зовёт NotesToolset (NotesToolset.cs:288) — без byHuman
        sut.Move(_editorId, created.Id, "B").Should().NotBeNull();
        sut.MoveFolder(_editorId, _projectId, "B", "C").Should().HaveCount(1);
        sut.DeleteFolder(_editorId, _projectId, "C").Should().Be(1);

        journal.Calls.Should().BeEmpty("журнал — только правки человека (byHuman); агентские переносы молчат");
    }

    [Fact]
    public void Отказ403_ИОтказ404_Переносы_НиОднойЗаписиВЖурнал()
    {
        AddMember(_editorId, ProjectRole.Editor);
        AddMember(_readerId, ProjectRole.Reader);
        var journal = new FakeJournal();
        var sut = Service(journal);
        var id = ProjectNoteId(sut);

        // 403: читатель без WriteContent — ResolveRoot бросает до любой работы с файлами
        Action act = () => sut.Move(_readerId, id, "Куда-то", byHuman: true);
        act.Should().Throw<UnauthorizedAccessException>();
        act = () => sut.MoveFolder(_readerId, _projectId, "Заметки нет", "Куда", byHuman: true);
        act.Should().Throw<UnauthorizedAccessException>();
        act = () => sut.DeleteFolder(_readerId, _projectId, "Заметки нет", byHuman: true);
        act.Should().Throw<UnauthorizedAccessException>();

        // 404: переносить нечего — Move даёт null, папочные операции — KeyNotFoundException
        var gone = sut.Create(_ownerId, new CreateNoteRequest("Призрачная", "тело", _projectId));
        sut.Delete(_ownerId, gone.Id).Should().BeTrue();
        sut.Move(_editorId, gone.Id, "Куда-то", byHuman: true).Should().BeNull();
        act = () => sut.MoveFolder(_editorId, _projectId, "Нет такой папки", "Куда", byHuman: true);
        act.Should().Throw<KeyNotFoundException>();
        act = () => sut.DeleteFolder(_editorId, _projectId, "Нет такой папки", byHuman: true);
        act.Should().Throw<KeyNotFoundException>();

        journal.Calls.Should().BeEmpty("отказанный перенос не состоялся — ни одного пути в журнале");
    }

    // --- S3-3c2: конкурентность (c3) ---

    /// <summary>
    /// Два MoveFolder одной папки параллельно в разные цели. Замки F-6a (ключ источника + id
    /// заметок папки) сериализуют пару: ровно один успех, проигравший под тем же замком видит
    /// уже перенесённую папку (KeyNotFoundException) и не пишет ни одного нового пути. Порядок
    /// журнала относительно диска проверяется удержанием: первый Record победителя держится
    /// внутри его замка, и пока он удержан, перенос уже должен быть совершён на диске (старого
    /// места нет, папка в одной из целей). Мутация «Record новых путей до фактического
    /// переноса»: запись приходит, когда папка ещё на старом месте, — проверка ниже краснеет.
    /// </summary>
    [Fact]
    public async Task Гонка_ДваMoveFolderОднойПапки_УспехРовноОдин_ЖурналТолькоПослеФактическогоПереноса()
    {
        AddMember(_editorAId, ProjectRole.Editor);
        AddMember(_editorBId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);
        for (var i = 0; i < 2; i++)
            sut.Create(_ownerId, new CreateNoteRequest($"Гонка {i}", $"# Тело {i}\n", _projectId, Folder: "Гонка"));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ma = Task.Run(async () =>
        {
            await start.Task;
            try { return (Moved: (IReadOnlyList<MovedNoteId>?)sut.MoveFolder(_editorAId, _projectId, "Гонка", "ЦельА", byHuman: true), Fail: (Exception?)null); }
            catch (Exception ex) { return (null, ex); }
        });
        var mb = Task.Run(async () =>
        {
            await start.Task;
            try { return (Moved: (IReadOnlyList<MovedNoteId>?)sut.MoveFolder(_editorBId, _projectId, "Гонка", "ЦельБ", byHuman: true), Fail: (Exception?)null); }
            catch (Exception ex) { return (null, ex); }
        });
        start.SetResult();

        // Первый Record — внутри замка победителя. Пока он удержан, перенос уже обязан быть
        // совершён на диске: старого места нет, папка в одной из целей.
        var firstUserId = await journal.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var movedBeforeRecord = !Directory.Exists(ProjectFile("Гонка")) &&
            (Directory.Exists(ProjectFile("ЦельА")) || Directory.Exists(ProjectFile("ЦельБ")));
        journal.ReleaseFirst();

        var ra = await ma.WaitAsync(TimeSpan.FromSeconds(30));
        var rb = await mb.WaitAsync(TimeSpan.FromSeconds(30));

        movedBeforeRecord.Should().BeTrue(
            "запись новых путей обязана идти после фактического переноса: на момент первого Record папка уже в новом месте");

        // Ровно один успех: проигравший под тем же замком видит уже перенесённую папку
        var won = ra.Moved is not null ? ra : rb;
        var lost = ra.Moved is not null ? rb : ra;
        won.Moved.Should().HaveCount(2, "победитель перенёс папку с обеими заметками");
        lost.Fail.Should().BeOfType<KeyNotFoundException>("проигравший под тем же замком видит уже перенесённую папку");
        var loserTarget = ra.Moved is not null ? "ЦельБ" : "ЦельА";

        // Новых путей проигравшего в журнале 0: все записи — победителя, только перенесённые файлы
        journal.Calls.Should().HaveCount(4);
        journal.Calls.Should().AllSatisfy(c => c.UserId.Should().Be(firstUserId));
        journal.Calls.Should().NotContain(c => c.Rel.StartsWith("notes/" + loserTarget + "/"));
        journal.Calls.Should().NotContain(c => c.Rel == "notes/Гонка" || c.Rel == "notes/ЦельА" || c.Rel == "notes/ЦельБ");
    }

    // --- S3-3c1 доработка 1 (вердикт ea994f7c): пять человеческих ручек аннотаций ---

    [Fact]
    public void КомментарийКДокументу_ДваRecord_ДокументИКомментарий_АгентИЛичныйVaultМолчат()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        var comment = sut.Annotate(_editorId, ProjectAnnotate("Комментарий человека"), byHuman: true);
        comment.Should().NotBeNull();

        // Ровно две записи, в порядке записей файла: сначала документ (в него записан маркер
        // ^id), потом комментарий-заметка в «Комментарии»
        journal.Calls.Should().HaveCount(2);
        journal.Calls[0].Rel.Should().Be("notes/Заметка проекта.md");
        journal.Calls[1].Rel.Should().Be("notes/Комментарии/Комментарий человека.md");
        journal.Calls.Should().AllSatisfy(c =>
        {
            c.ProjectId.Should().Be(_projectId);
            c.Root.Should().Be(_projectRoot);
            c.UserId.Should().Be(_editorId);
        });

        // Агентский annotate того же документа (так зовёт NotesToolset.cs:216): комментарий
        // создан, ^id уже существует — журнал молчит
        var agentComment = sut.Annotate(_editorId, ProjectAnnotate("Комментарий агента"));
        agentComment.Should().NotBeNull();
        journal.Calls.Should().HaveCount(2);

        // Личный vault: документ и комментарий в нём — ни одной новой записи
        var personalDoc = sut.Create(_ownerId, new CreateNoteRequest("Личный документ", "Текст личного документа.\n"), byHuman: true);
        personalDoc.Should().NotBeNull();
        var personalComment = sut.Annotate(_ownerId, new AnnotateRequest(
            new AnnotateDocRef("personal", "Личный документ.md"),
            new AnnotateSelection(0, 0, "Текст личного документа"), Comment: "Комментарий личный"), byHuman: true);
        personalComment.Should().NotBeNull();
        journal.Calls.Should().HaveCount(2, "личный vault — вне журнала, даже когда обе стороны его");
    }

    [Fact]
    public void ОтветВТреде_ОдинRecord_ПутьЗаметкиОтвета_АгентМолчит()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Корневой комментарий создан агентом — журнал молчит
        var root = sut.Annotate(_editorId, ProjectAnnotate("Корневой комментарий"));
        root.Should().NotBeNull();
        journal.Calls.Should().BeEmpty();

        var reply = sut.Reply(_editorId, root.Id, new ReplyRequest("Ответ человека"), byHuman: true);
        reply.Should().NotBeNull();

        // Ответ — заметка в «Комментарии» проекта: ровно одна запись её пути
        journal.Calls.Should().ContainSingle().Which.Should()
            .Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Комментарии/Ответ человека.md", _editorId));

        // Агентский ответ (NotesToolset.cs:241) — молчит
        sut.Reply(_editorId, root.Id, new ReplyRequest("Ответ агента")).Should().NotBeNull();
        journal.Calls.Should().HaveCount(1);
    }

    [Fact]
    public void ПерепривязкаКомментария_ДваRecord_ДокументИКомментарий_АгентМолчит()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Комментарий создан агентом (^id в текстовом блоке) — журнал молчит
        var comment = sut.Annotate(_editorId, ProjectAnnotate("Комментарий для перепривязки"));
        comment.Should().NotBeNull();
        journal.Calls.Should().BeEmpty();

        // Перепривязка человеком к блоку заголовка: в документ пишется НОВЫЙ ^id —
        // оба пути, в порядке записей файла: сначала документ, потом комментарий
        var repinned = sut.RepinAnnotation(_editorId, comment.Id,
            new AnnotateSelection(0, 0, "Заметка проекта"), byHuman: true);
        repinned.Should().NotBeNull();

        journal.Calls.Should().HaveCount(2);
        journal.Calls[0].Rel.Should().Be("notes/Заметка проекта.md");
        journal.Calls[1].Rel.Should().Be("notes/Комментарии/Комментарий для перепривязки.md");
        journal.Calls.Should().AllSatisfy(c =>
        {
            c.ProjectId.Should().Be(_projectId);
            c.Root.Should().Be(_projectRoot);
            c.UserId.Should().Be(_editorId);
        });

        // Агентская перепривязка к старому месту (^id уже есть, файла документа нет) — молчит
        sut.RepinAnnotation(_editorId, comment.Id, new AnnotateSelection(0, 0, "Текст проектной заметки"))
            .Should().NotBeNull();
        journal.Calls.Should().HaveCount(2);
    }

    [Fact]
    public void СтатусКомментария_ОдинRecord_ПутьКомментария_АгентМолчит()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        var comment = sut.Annotate(_editorId, ProjectAnnotate("Комментарий для статуса"));
        comment.Should().NotBeNull();
        journal.Calls.Should().BeEmpty();

        var resolved = sut.SetAnnotationStatus(_editorId, comment.Id, "resolved", byHuman: true);
        resolved.Should().NotBeNull();

        // Frontmatter статуса — правка файла комментария: ровно одна запись его пути
        journal.Calls.Should().ContainSingle().Which.Should()
            .Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Комментарии/Комментарий для статуса.md", _editorId));

        // Агентская смена статуса (NotesToolset.cs:273) — молчит
        sut.SetAnnotationStatus(_editorId, comment.Id, "open").Should().NotBeNull();
        journal.Calls.Should().HaveCount(1);
    }

    [Fact]
    public void СвязатьУпоминание_ОдинRecord_ПутьЗаметки_АгентМолчит()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);

        // Заметка с упоминанием, созданная агентом в проекте — журнал молчит
        var note = sut.Create(_editorId, new CreateNoteRequest("Заметка с упоминанием", "Текст про Важная Тема внутри.\n", _projectId));
        note.Should().NotBeNull();
        journal.Calls.Should().BeEmpty();

        var linked = sut.LinkMention(_editorId, note.Id, "Важная Тема", byHuman: true);
        linked.Should().NotBeNull();
        linked!.Content.Should().Contain("[[Важная Тема]]");

        // Ровно одна запись: путь заметки, содержимое которой переписано
        journal.Calls.Should().ContainSingle().Which.Should()
            .Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Заметка с упоминанием.md", _editorId));

        // Агентское «связать» другой проектной заметки: файл переписан, журнал молчит
        var other = sut.Create(_editorId, new CreateNoteRequest("Другая заметка", "И про Важная Тема.\n", _projectId));
        other.Should().NotBeNull();
        sut.LinkMention(_editorId, other.Id, "Важная Тема").Should().NotBeNull();
        journal.Calls.Should().HaveCount(1);
    }

    // --- S3-3c1 доработка 1 (minor): no-op срока не пишет лишнего Record ---

    [Fact]
    public void СнятиеОтсутствующегоСрока_НиОдногоRecord_УстановкаСрока_Один()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);
        var created = sut.Create(_editorId, new CreateNoteRequest("Заметка без срока", "тело", _projectId));
        created.Should().NotBeNull();
        journal.Calls.Should().BeEmpty();

        // Снятие срока у заметки без срока: контент ровно тот же — файла не было,
        // Record тоже нет
        var kept = sut.Update(_editorId, created.Id, new UpdateNoteRequest(ExpiresAfterMinutes: null), byHuman: true);
        kept.Should().NotBeNull();
        journal.Calls.Should().BeEmpty("no-op срока не должен плодить запись нетронутого файла");

        // Настоящий срок: содержимое изменено — ровно одна запись
        var dated = sut.Update(_editorId, created.Id, new UpdateNoteRequest(ExpiresAfterMinutes: 10), byHuman: true);
        dated.Should().NotBeNull();
        journal.Calls.Should().ContainSingle().Which.Rel.Should().Be("notes/Заметка без срока.md");
    }

    // --- S3-3c3: чекбокс задачи и срок в заметке (путь NoteTaskSyncService) ---

    // Вызывающий — владелец: мост чекбокса закрыт owner-Write для не-владельца
    // (EnsureSourceWrite, NotesService.cs:1693; граница — в NotesSideWritePathsTests)
    [Fact]
    public async Task ТогглИСрокЧекбоксаПроектнойЗаметки_РовноОдинRecord_КаждаяПравка()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);
        var sync = Sync(sut);
        var id = ProjectNoteId(sut, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;

        await sync.ToggleAsync(_ownerId, id, line, done: true, byHuman: true);
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().Contain("- [x]");

        // Ровно одна запись: корень — RootPath проекта, путь — заметки, автор — вызывающий
        journal.Calls.Should().ContainSingle()
            .Which.Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Дела проекта.md", _ownerId));

        // Срок на той же строке — ровно один новый Record того же пути
        await sync.SetDueAsync(_ownerId, id, line, "2030-01-01", byHuman: true);
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().Contain("📅 2030-01-01");
        journal.Calls.Should().HaveCount(2);
        journal.Calls[^1].Should().Be(new FakeJournal.Call(_projectId, _projectRoot, "notes/Дела проекта.md", _ownerId));
    }

    [Fact]
    public async Task ТоглБезByHumanИОбратныйПутьЗадачи_НиОднойЗаписиВЖурнал()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);
        var sync = Sync(sut);
        var id = ProjectNoteId(sut, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;

        // Фоновый/агентский вызов (без byHuman — так зовут не-человеческие пути) — файл
        // изменён, журнал молчит
        await sync.ToggleAsync(_ownerId, id, line, done: true);
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().Contain("- [x]");
        await sync.SetDueAsync(_ownerId, id, line, "2030-01-01");
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().Contain("📅 2030-01-01");
        journal.Calls.Should().BeEmpty();

        // Обратная синхронизация «задача → заметка» (SyncTaskToNoteAsync, по инициативе
        // системы) — правка по чужой инициативе: снятая галочка, журнала нет
        await sync.SyncTaskToNoteAsync(_ownerId, new TaskItem
        {
            ProjectId = _projectId,
            OwnerId = _ownerId,
            Title = "Задача из чанки",
            Status = TaskItemStatus.Todo,
            SourceNoteId = id,
            SourceNoteLine = line,
        });
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().Contain("- [ ]");
        journal.Calls.Should().BeEmpty("обратная запись — не правка человека");
    }

    [Fact]
    public async Task ТоглИСрокЛичнойЧанки_НиОднойЗаписиВЖурнал()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);
        var sync = Sync(sut);
        var personal = sut.Create(_ownerId, new CreateNoteRequest("Личная чанка", "# Личная чанка\n\n- [ ] Личная задача\n"), byHuman: true);
        personal.Should().NotBeNull();
        var line = sync.ListForNote(_ownerId, personal.Id).Single().Line;

        await sync.ToggleAsync(_ownerId, personal.Id, line, done: true, byHuman: true);
        await sync.SetDueAsync(_ownerId, personal.Id, line, "2030-01-01", byHuman: true);

        journal.Calls.Should().BeEmpty("личный vault — вне журнала правок общего проекта");
    }

    [Fact]
    public async Task ТоглУжеВНужномСостоянииИТотЖеСрок_НиОдногоRecord_АРеальнаяПравкаОдин()
    {
        var journal = new FakeJournal();
        var sut = Service(journal);
        var sync = Sync(sut);
        var id = ProjectNoteId(sut, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;

        // Галочка уже снята, просят снять — no-op: файла нет, Record нет (тот же узор, что у
        // снятия несуществующего срока в «СнятиеОтсутствующегоСрока»)
        await sync.ToggleAsync(_ownerId, id, line, done: false, byHuman: true);
        journal.Calls.Should().BeEmpty("no-op чекбокса не должен плодить запись нетронутого файла");

        // Настоящая правка — ровно один Record
        await sync.ToggleAsync(_ownerId, id, line, done: true, byHuman: true);
        journal.Calls.Should().ContainSingle().Which.Rel.Should().Be("notes/Дела проекта.md");

        // Тот же срок повторно — no-op: нового Record нет
        await sync.SetDueAsync(_ownerId, id, line, "2030-01-01", byHuman: true);
        journal.Calls.Should().HaveCount(2);
        await sync.SetDueAsync(_ownerId, id, line, "2030-01-01", byHuman: true);
        journal.Calls.Should().HaveCount(2, "no-op срока не должен плодить запись нетронутого файла");

        // Снятие существующего срока — реальная правка: ровно один новый Record
        await sync.SetDueAsync(_ownerId, id, line, null, byHuman: true);
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().NotContain("📅");
        journal.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Отказ403_ИОтказ404_ТоглИСрок_НиОднойЗаписиВЖурнал()
    {
        AddMember(_editorId, ProjectRole.Editor);
        AddMember(_readerId, ProjectRole.Reader);
        var journal = new FakeJournal();
        var sut = Service(journal);
        var sync = Sync(sut);
        var id = ProjectNoteId(sut, TodoNote);
        var line = sync.ListForNote(_editorId, id).Single().Line;

        // 403: мост чекбокса закрыт owner-Write для не-владельца — EnsureSourceWrite бросает
        // до любой работы с файлом
        Func<Task> act = () => sync.ToggleAsync(_readerId, id, line, done: true, byHuman: true);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        act = () => sync.SetDueAsync(_readerId, id, line, "2030-01-01", byHuman: true);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // 404: заметки нет — KeyNotFoundException до любой правки
        var gone = sut.Create(_ownerId, new CreateNoteRequest("Призрачная чанка", "# Призрачная чанка\n\n- [ ] Задача\n", _projectId));
        sut.Delete(_ownerId, gone.Id).Should().BeTrue();
        act = () => sync.ToggleAsync(_editorId, gone.Id, 0, done: true, byHuman: true);
        await act.Should().ThrowAsync<KeyNotFoundException>();

        // 404-подобный: строка не чекбокс — InvalidOperationException (владелец: правка
        // дошла бы до разбора строки), файл не тронут
        act = () => sync.SetDueAsync(_ownerId, id, 0, "2030-01-01", byHuman: true);
        await act.Should().ThrowAsync<InvalidOperationException>();

        journal.Calls.Should().BeEmpty("отказанный вызов не состоялся — ни одного пути в журнале");
        File.ReadAllText(ProjectFile(TodoNote + ".md")).Should().Be(TodoBody);
    }

    /// <summary>
    /// S3-3c3 (c3): тоггл ‖ правка той же заметки — ровно та пара, что названа в «Двух
    /// вызовы» задачи. Двух тогглов одной проектной заметки от разных людей в продукте быть
    /// не может: мост чекбокса закрыт owner-Write для не-владельца (EnsureSourceWrite,
    /// NotesService.cs:1693; граница держится NotesSideWritePathsTests «Редактор_ТогглИСрок…
    /// Отказ»), поэтому A — владелец (тоггл), B — редактор (обычная правка через WriteContent).
    /// Оба — человеческие правки одного id; порядок решает замок id в Update. Победитель
    /// пишет файл и Record ВНУТРИ замка (фейк-журнал держит первый Record до ReleaseFirst
    /// или 2 с — пока держат, проигравший не может записать файл).
    /// Мутация «Record после выхода из WithWriteGates»: проигравший пишет файл и журнал, пока
    /// первый удержан вне замка, — его Record доходит раньше; автором последней записи
    /// оказывается тот, чьё содержимое стёрто (обе проверки ниже краснеют; на копии).
    /// </summary>
    [Fact]
    public async Task Гонка_ТоглИПравкаОднойЗаметки_АвторПоследнегоRecordРавенАвторуИтоговогоФайла()
    {
        AddMember(_editorAId, ProjectRole.Editor);
        var journal = new FakeJournal();
        var sut = Service(journal);
        var sync = Sync(sut);
        var id = ProjectNoteId(sut, TodoNote);
        var line = sync.ListForNote(_ownerId, id).Single().Line;
        var contentB = "# Дела проекта\n\n- [ ] Задача из чанки (правка редактора)\n";

        var a = Task.Run(async () => (NoteDetail?)await sync.ToggleAsync(_ownerId, id, line, done: true, byHuman: true));
        var b = Task.Run(() => sut.Update(_editorAId, id, new UpdateNoteRequest(Content: contentB), byHuman: true));

        var firstUserId = await journal.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var otherTask = firstUserId == _ownerId ? b : a;
        var otherId = firstUserId == _ownerId ? _editorAId : _ownerId;
        var otherRanWhileHeld = await Task.WhenAny(otherTask, Task.Delay(1000)) == otherTask;

        journal.ReleaseFirst();
        await a;
        await b;

        otherRanWhileHeld.Should().BeFalse(
            "проигравший не мог записать файл и журнал, пока первый Record удержан внутри замка id");

        // Ровно два Record: обе правки состоялись
        journal.Calls.Should().HaveCount(2);

        // Автор последнего Record = автор итогового содержимого: последний был владелец —
        // файл отмечен его тогглом; последний был редактор — в файле его правка
        var last = journal.Calls[^1];
        last.UserId.Should().Be(otherId);
        var text = File.ReadAllText(ProjectFile(TodoNote + ".md"));
        if (last.UserId == _ownerId) text.Should().Contain("- [x]");
        else text.Should().Contain("правка редактора");
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

    // Мост задач — заглушка: «задач из заметки» здесь нет (GetBySourceNote пуст),
    // синхронизация задачи в этих тестах не проверяется
    private sealed class CapturingBridge : INoteTaskBridge
    {
        public IReadOnlyList<NoteTaskRef> GetBySourceNote(string ownerId, string noteId) => [];
        public NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req) =>
            new("task-1", req.Title, req.SourceNoteLine, req.DueDate, req.Status, req.Recurrence,
                NoteTaskKind.Task, projectId);
        public NoteTaskRef? Update(string ownerId, string taskId, NoteTaskUpdateRequest req) => null;
        public NoteTaskRef? SpawnNextOccurrence(string ownerId, string completedTaskId) => null;
    }

    private sealed class NoopNotifier : INotesHubNotifier
    {
        public Task BroadcastNotesChangedAsync(string userId, string action, string noteId, string? projectId = null) => Task.CompletedTask;
        public Task BroadcastTaskChangedAsync(string userId, string action, string taskId, string? projectId = null) => Task.CompletedTask;
    }

    // Индексация не настроена: QueueSync у неконфигурированного IKnowledgeIndex ничего не пишет
    private sealed class NoDify : IKnowledgeIndex
    {
        public bool IsConfigured => false;

        public Task<string> CreateDatasetAsync(string name, string permission = "only_me", string? description = null,
            string? indexingTechnique = null) => throw new NotSupportedException();

        public Task DeleteDocumentAsync(string datasetId, string documentId) => throw new NotSupportedException();

        public Task DeleteDatasetAsync(string datasetId) => throw new NotSupportedException();

        public Task RenameDatasetAsync(string datasetId, string newName) => throw new NotSupportedException();

        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName, string content,
            List<string>? tags = null) => throw new NotSupportedException();

        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query, int topK = 8,
            IReadOnlyList<KnowledgeMetadataFilter>? filters = null) => throw new NotSupportedException();
    }
}
