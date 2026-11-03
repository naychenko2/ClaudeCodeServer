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
/// S3-3c2: человеческие перенос заметки (Move, в т.ч. между источниками), перенос и удаление
/// папки (MoveFolder, DeleteFolder) — тот же журнал. Move: старый и новый путь, каждый со root
/// СВОЕГО проекта; папки — путём КАЖДОГО своего файла (путь папки в журнал не идёт, тот же
/// порядок S3-3b в FilesController); список файлов папки снимается до операции. Отказанный
/// перенос (403/404) — ни одного пути; агентские вызовы — молчат.
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
