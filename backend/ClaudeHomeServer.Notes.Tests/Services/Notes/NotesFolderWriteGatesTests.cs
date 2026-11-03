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
/// F-6a: папочные операции (MoveFolder, DeleteFolder) берут замки всех заметок папки —
/// правка/удаление заметки внутри держит замок на её id, а не на источник: без этих замков
/// гонка «папка ↔ заметка внутри» не сериализована (хвост 565100e3 п.6): правка пишет в файл
/// под переездом или удалением — полуреальное состояние («папка перенесена, заметка на старом
/// пути», «папка удалена, заметка воскресла»). С замками исход ровно один: или заметка целиком
/// в новом месте со своей правкой, или правка честным отказом (null → 404) после переезда/сноса.
/// Плюс гонка «перенос ↔ правка той же заметки» — чувствительна к снятию id-замка с Move
/// (единственный тест-мутация этого шага): окно правки с холодной моделью длиннее окна переноса.
/// И гонка «create ↔ create» одного пути: оба успешны, пути разные (суффикс), ни один файл
/// не затёрт — ожидание по решению постановщика (409 при коллизии имени не требуется).
///
/// Порядок захвата фиксирован (WithWriteGates по возрастанию ключей) — взаимной блокировки нет;
/// пустой список заметок папки — обычный случай: замок источника один, чужие заметки не тронуты.
///
/// Тесты бесхостовые: NotesService строится напрямую с точкой доступа (тот же приём, что у
/// SharedProjectNotesWriteContentTests). Гонки — две параллельные операции с одним барьером
/// старта (TaskCompletionSource); старт гонки — никогда через паузу (Task.Delay/Thread.Sleep),
/// пауза допускается только как таймаут наблюдения: взаимная блокировка или зависание даёт
/// красный тест (TaskCanceledException), а не вис.
/// Гонка «перенос ↔ правка» ставится детерминированно через шов TestProbeAfterFileExists:
/// правка стоит под своим id-замком до чтения файла, перенос поднимается ровно в это окно —
/// исход определяет порядок замка, а не тайминг (F-6a доработка 1).
/// </summary>
public class NotesFolderWriteGatesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _projectRoot;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;

    public NotesFolderWriteGatesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "f6a_folders_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = Config();

        // При пустом users.json UserStore создаёт дефолтного admin — он и есть владелец проекта
        _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("f6a-editor", "pwd", "user").Id;
        foreach (var id in new[] { _ownerId, _editorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));

        _projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "notes"));
        _projectId = _projects.Create("Проект F-6a", _projectRoot, _ownerId, "owner").Id;
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

    private NotesService Service() =>
        new(_projects, Config(), NullLogger<NotesService>.Instance, null,
            new ProjectAccessService(_projects, new NoSessions(), new SharedProjectsGate(_config, new FeatureFlagService(_users))));

    private void AddMember(string userId, ProjectRole role)
    {
        var members = _projects.GetById(_projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember { UserId = userId, Role = role, AddedBy = _ownerId });
        _projects.SetMembers(_projectId, members);
    }

    private string ProjectFile(string rel) => Path.Combine(_projectRoot, "notes", rel);

    // ─── Гонка «перенос папки ↔ правка заметки внутри» (хвост 565100e3 п.6) ───

    /// <summary>
    /// Без замков заметок папки (мутация — снять их в MoveFolder) критические секции
    /// переплетаются: Update проверил файл в «Старая», перенос переименовал папку, запись
    /// Update поднимает «Старая/…md» из небытия (DirectoryNotFoundException — правка падает)
    /// или оставляет его рядом с уже перенесённой копией — полуреальное состояние.
    /// С замками (id заметки + источник, одним WithWriteGates по возрастанию) — ровно один
    /// исход при любом порядке: правка до переноса (её содержимое переезжает в новое место,
    /// заметка в маппинге) или после (честный отказ null→404, файл целиком в новом месте).
    /// </summary>
    [Fact]
    public async Task Гонка_ПереносПапкиПротивПравкиЗаметкиВнутри_ИсходОдин()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();
        const int attempts = 5;

        for (var i = 0; i < attempts; i++)
        {
            var title = $"Папка-гонка {i}";
            var oldFolder = $"Старая{i}";   // имена уникальны по итерациям: перенос прошлой не мешает этой
            var newFolder = $"Новая{i}";
            var created = sut.Create(_ownerId,
                new CreateNoteRequest(title, "# Тело до правки\n", _projectId, Folder: oldFolder));
            var inOld = ProjectFile($"{oldFolder}/{title}.md");
            var inNew = ProjectFile($"{newFolder}/{title}.md");
            var newBody = "# " + title + "\n\n" + new string('б', 2_000_000) + "\n";

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mv = Task.Run(async () => { await start.Task; return sut.MoveFolder(_ownerId, _projectId, oldFolder, newFolder); });
            var up = Task.Run(async () => { await start.Task; return sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: newBody)); });
            start.SetResult();
            var moved = await mv.WaitAsync(TimeSpan.FromSeconds(30));
            var updated = await up.WaitAsync(TimeSpan.FromSeconds(30));

            // Маппинг переноса всегда настоящий: новый id читается и ведёт в новое место.
            var mapping = moved.Single(m => m.OldId == created.Id);
            var asOwner = Service().GetSummaries(_ownerId, _projectId, null)
                .Single(n => n.Title == title);
            asOwner.Id.Should().Be(mapping.NewId, $"«{title}»: id из маппинга — тот, что читается после цикла");
            asOwner.Path.Should().Be($"{newFolder}/{title}.md", $"«{title}»: заметка целиком в новом месте");

            if (updated is not null)
            {
                // Выиграла правка: она записана ДО переноса (под замком id) и переехала вместе
                // с папкой — её содержимое в новом месте, а не на старом пути
                File.Exists(inOld).Should().BeFalse($"«{title}»: старый путь после переноса пуст");
                File.ReadAllText(inNew).Should().Contain("ббб", $"«{title}»: выигравшая правка переехала, а не осталась на старом пути");
            }
            else
            {
                // Выиграл перенос: правка по уже перенесённому пути честным отказом (null → 404),
                // файл не тронут и целиком в новом месте
                File.Exists(inNew).Should().BeTrue($"«{title}»: перенос состоялся");
                File.ReadAllText(inNew).Should().Contain("Тело до правки", $"«{title}»: проигравшая правка не тронула файл");
            }
        }
    }

    // ─── Гонка «удаление папки ↔ правка заметки внутри» ───

    /// <summary>
    /// Тот же класс для DeleteFolder: без замка id заметки правка проверяет файл, снос удаляет
    /// папку, запись правки поднимает файл из-под сноса — «удалённая» заметка воскресает
    /// (DeleteFolder сказал «1 удалена», файл на месте). С замками: правка до сноса (её
    /// содержимое было в файле до удаления) или честный отказ после; файл после цикла не
    /// существует ни при каком переплетении.
    /// </summary>
    [Fact]
    public async Task Гонка_УдалениеПапкиПротивПравкиЗаметкиВнутри_ИсходОдин()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();
        const int attempts = 5;

        for (var i = 0; i < attempts; i++)
        {
            var title = $"Снос-гонка {i}";
            var created = sut.Create(_ownerId,
                new CreateNoteRequest(title, "# Тело до правки\n", _projectId, Folder: "Снос"));
            var full = ProjectFile($"Снос/{title}.md");
            var newBody = "# " + title + "\n\n" + new string('б', 2_000_000) + "\n";

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dl = Task.Run(async () => { await start.Task; return sut.DeleteFolder(_ownerId, _projectId, "Снос"); });
            var up = Task.Run(async () => { await start.Task; return sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: newBody)); });
            start.SetResult();
            var removed = await dl.WaitAsync(TimeSpan.FromSeconds(30));
            var updated = await up.WaitAsync(TimeSpan.FromSeconds(30));

            removed.Should().Be(1, $"«{title}»: папка с одной заметкой удалена");
            // Полуреального состояния нет: снос сказал «удалена» — правка не возрождает файл
            File.Exists(full).Should().BeFalse($"«{title}»: удалённая заметка не воскресает правкой");

            if (updated is not null)
            {
                // Выиграла правка: она была в файле до сноса (под замком id), затем всё удалено
                updated.Content.Should().Contain("ббб", $"«{title}»: выигравшая правка записана до сноса");
            }
        }
    }

    // ─── Пустая папка: список замков пуст, чужие заметки не тронуты, цикла нет ───

    /// <summary>
    /// Пустая (по заметкам) папка: non-md вложения в список замков не входят, пустой список —
    /// штатный случай (берётся только замок источника). Параллельные операции по заметке вне
    /// папки при этом завершаются: замок пустой папки чужих путей не держит, взаимной
    /// блокировки нет. Ожидание с явным таймаутом — если бы цикл возник, тест краснеет по
    /// TaskCanceledException, а не виснет.
    /// </summary>
    [Fact]
    public async Task ПустаяПапка_ЧужиеЗаметкиПодЗамокНеБерёт_ГонкаБезЦикла()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        // «Папка» — физическая, без заметок: только вложение
        Directory.CreateDirectory(Path.Combine(_projectRoot, "notes", "Пусто"));
        File.WriteAllText(ProjectFile("Пусто/вложение.png"), "png");
        var outside = sut.Create(_ownerId, new CreateNoteRequest("Чужая", "# Чужая\n", _projectId, Folder: "Другая"));

        // Гонка — операции по разным путям: перенос пустой папки и перенос чужой заметки.
        // Замок пустой папки — только источник; чужой путь он не держит, цикл невозможен.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mvf = Task.Run(async () => { await start.Task; return sut.MoveFolder(_ownerId, _projectId, "Пусто", "Пусто2"); });
        var mv = Task.Run(async () => { await start.Task; return sut.Move(_editorId, outside.Id, "Другая/Подпапка"); });
        start.SetResult();

        var movedEmpty = await mvf.WaitAsync(TimeSpan.FromSeconds(30));
        var movedNote = await mv.WaitAsync(TimeSpan.FromSeconds(30));

        movedEmpty.Should().BeEmpty("пустая папка переносится с пустым маппингом (пустой список замков — штатный случай)");
        movedNote.Should().NotBeNull("операция по заметке вне папки не ждёт пустой папки");
        File.Exists(ProjectFile("Другая/Подпапка/Чужая.md")).Should().BeTrue();
        Directory.Exists(ProjectFile("Пусто2")).Should().BeTrue();

        // Снос перенесённой пустой папки — вне гонки: заметок нет, счётчик нулевой
        sut.DeleteFolder(_ownerId, _projectId, "Пусто2").Should().Be(0);
    }

    // ─── Гонка «перенос заметки ↔ правка той же заметки» (id-замок Move) ───

    /// <summary>
    /// id-замок в Move — тот же ключ, что у Update/Delete (S2c-11). Гонка ставится через шов
    /// TestProbeAfterFileExists (NotesService.cs): правка сообщает «файл проверен» и стоит под
    /// своим id-замком до его чтения; тест поднимает перенос ровно в это окно и отпускает
    /// правку, когда перенос завершился (id-замка Move нет → перенос успевает, и правка падает
    /// FileNotFoundException на исчезнувшем пути — полуреальное состояние «файл перенесён под
    /// живой правкой») — или когда за 2 с перенос не завершился, то есть ждёт id-замок правки
    /// (с замком → исход ровно один: файл в единственном месте, новом, со своим содержимым).
    /// Порядок замка определяет исход, а не тайминг: мутация «снять id из ключей Move» краснит
    /// тест каждый прогон (F-6a доработка 1). Гонка «перенос ↔ удаление» той же заметки — в
    /// SharedProjectNotesWriteContentTests (её исход не изменился).
    /// </summary>
    [Fact]
    public async Task Гонка_ПереносЗаметкиПротивПравкиТойЖеЗаметки_ИсходОдин()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        const int attempts = 3;
        for (var i = 0; i < attempts; i++)
        {
            var title = $"Живая{i}";
            var newFolder = string.Join('/', Enumerable.Range(1, 10).Select(j => $"н{i}-{j}"));
            var created = sut.Create(_ownerId, new CreateNoteRequest(title, "# Тело до правки\n", _projectId, Folder: "Живые"));
            var oldFull = ProjectFile($"Живые/{title}.md");
            var newFull = ProjectFile($"{newFolder}/{title}.md");

            var atExists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.TestProbeAfterFileExists = () =>
            {
                atExists.SetResult();
                // правка стоит под своим id-замком до чтения файла; отпускает её тест —
                // по завершении переноса или когда ясно, что перенос ждёт этот замок
                try { release.Task.Wait(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
            };

            var up = Task.Run(() => sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: "# Новая правка\n")));
            await atExists.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var mv = Task.Run(() => sut.Move(_editorId, created.Id, newFolder));
            // перенос завершился → отпускаем правку: без id-замка она читает уже чужой путь
            // (полуреальное состояние), с id-замком её окно закрыло весь перенос;
            // за 2 с не завершился → он ждёт id-замок правки → отпускаем правку, перенос пойдёт после
            await Task.WhenAny(mv, Task.Delay(TimeSpan.FromSeconds(2)));
            release.SetResult();
            var updated = await up.WaitAsync(TimeSpan.FromSeconds(30));
            var moved = await mv.WaitAsync(TimeSpan.FromSeconds(30));

            // Полуреального состояния нет ни при каком исходе гонки: старый путь пуст,
            // заметка ровно в одном месте — новом.
            updated.Should().NotBeNull($"«{title}»: правка под своим id-замком завершилась до переноса");
            File.Exists(oldFull).Should().BeFalse($"«{title}»: старый путь после гонки пуст (правка не подняла его из-под переноса)");
            File.Exists(newFull).Should().BeTrue($"«{title}»: заметка целиком в новом месте");
            moved.Should().NotBeNull($"«{title}»: перенос после сериализованной правки состоялся");
            File.ReadAllText(newFull).Should().Contain("Новая правка", $"«{title}»: выигравшая правка переехала вместе с заметкой");
        }
    }

    // ─── Два переноса разных заметок с одним именем в одну папку (M2) ───

    /// <summary>
    /// Инвариант: два переноса в одну цель не затирают файл. Сериализацию пары держит замок
    /// источника: Move берёт его безусловно и, при смене источника, ключ целевого источника
    /// (NotesService.cs:997-998) — любой перенос в папку проекта держит её ключ, поэтому
    /// второй перенос видит цель занятой и получает «уже есть» (InvalidOperationException,
    /// NotesService.cs:1033-1034), а файл выигравшего цел. Мутация «снять целевой путь из
    /// ключей Move» (NotesService.cs:996) этот тест не краснит: при одном источнике пару
    /// сериализует ключ источника и без него — факт приложен в отчёте доработки.
    /// </summary>
    [Fact]
    public async Task ДваПереносаОдногоИмениВОднуПапку_ОдинУспешенВторойУжеЕсть_ФайлНеЗатёрт()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        var a = sut.Create(_ownerId, new CreateNoteRequest("Однако", "# Тело А\n", _projectId, Folder: "Исходники"));
        var b = sut.Create(_ownerId, new CreateNoteRequest("Однако", "# Тело Б\n", _projectId, Folder: "Другие"));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ma = Task.Run(async () => { await start.Task; return sut.Move(_editorId, a.Id, "Цель"); });
        var mb = Task.Run(async () => { await start.Task; return sut.Move(_editorId, b.Id, "Цель"); });
        start.SetResult();

        // Обе операции уже запущены параллельно; сбор результатов по очереди ничего не
        // сериализует — отказ одной из двух ловится отдельно, а не тонет в aggregate.
        NoteDetail? movedA = null, movedB = null;
        Exception? failA = null, failB = null;
        try { movedA = await ma.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception ex) { failA = ex; }
        try { movedB = await mb.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception ex) { failB = ex; }

        var outcomes = new[]
        {
            (Moved: movedA, Fail: failA, Old: "Исходники/Однако.md", Body: "Тело А"),
            (Moved: movedB, Fail: failB, Old: "Другие/Однако.md", Body: "Тело Б"),
        };
        var won = outcomes.Single(o => o.Moved is not null);
        var lost = outcomes.Single(o => o.Fail is not null);

        lost.Fail.Should().BeOfType<InvalidOperationException>("второй перенос в занятую цель — «уже есть», по текущей семантике Move");
        won.Moved!.Path.Should().Be("Цель/Однако.md", "выигравший перенос привёл заметку в цель");
        File.Exists(ProjectFile(won.Old)).Should().BeFalse("выигравший перенос оставил свой старый путь пустым");
        File.Exists(ProjectFile(lost.Old)).Should().BeTrue("проигравшая заметка осталась на своём пути, не перенесена");
        Directory.GetFiles(ProjectFile("Цель"), "*.md").Length.Should().Be(1, "в цели ровно один файл");
        File.ReadAllText(ProjectFile("Цель/Однако.md")).Should().Contain(won.Body, "файл цели — содержимое выигравшего переноса, не затёрт");
    }

    // ─── Гонка «create ↔ create» одного пути: оба успешны, пути разные, перезаписи нет ───

    /// <summary>
    /// Ожидание по решению постановщика (409 не требуется): CreateCore разрешает коллизию имени
    /// суффиксом (NotesService.cs:838-844) — принятое поведение. Два create под одним замком
    /// источника сериализованы; полуреального состояния нет: обе заметки настоящие, каждая со
    /// своим телом, пути разные, ни один файл не затёрт другим.
    /// </summary>
    [Fact]
    public async Task Гонка_СозданиеОднойПапкиИЗаголовка_ОбаУспешныБезПерезаписи()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c1 = Task.Run(async () => { await start.Task; return sut.Create(_editorId, new CreateNoteRequest("Двойная", "# Тело первое\n", _projectId, Folder: "Двойные")); });
        var c2 = Task.Run(async () => { await start.Task; return sut.Create(_editorId, new CreateNoteRequest("Двойная", "# Тело второе\n", _projectId, Folder: "Двойные")); });
        start.SetResult();
        var first = await c1;
        var second = await c2;

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Id.Should().NotBe(second.Id, "тот же заголовок не сливает два create в одну заметку");

        // Поиск по id, не по заголовку: без frontmatter заголовок — имя файла (NotesService.cs:243),
        // и суффикс «-2» виден в заголовке второй заметки. Ищем то, что вернули сами create.
        var both = Service().GetSummaries(_editorId, _projectId, null)
            .Where(n => n.Id == first.Id || n.Id == second.Id).ToList();
        both.Count.Should().Be(2, "оба create — настоящие заметки");
        both.Select(n => n.Path).Distinct().Count().Should().Be(2, "пути разные — коллизия решена суффиксом");

        var texts = both.Select(n => File.ReadAllText(ProjectFile(n.Path))).ToList();
        texts.Should().ContainSingle(t => t.Contains("Тело первое"), "первое тело цело");
        texts.Should().ContainSingle(t => t.Contains("Тело второе"), "второе тело цело — ни один файл не затёрт");
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
