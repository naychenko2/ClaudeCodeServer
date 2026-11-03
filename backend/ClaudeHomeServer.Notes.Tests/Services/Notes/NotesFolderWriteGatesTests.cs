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
///
/// Порядок захвата фиксирован (WithWriteGates по возрастанию ключей) — взаимной блокировки нет;
/// пустой список заметок папки — обычный случай: замок источника один, чужие заметки не тронуты.
///
/// Тесты бесхостовые: NotesService строится напрямую с точкой доступа (тот же приём, что у
/// SharedProjectNotesWriteContentTests). Гонки — две параллельные операции с одним барьером
/// старта (TaskCompletionSource), ни Task.Delay, ни Thread.Sleep; окно гонки расширяется
/// детерминированно (большое тело правки, скан холодной модели, mkdir глубокой цели),
/// повторы убирают случайность.
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
            var moved = await mv;
            var updated = await up;

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
            var removed = await dl;
            var updated = await up;

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
    /// id-замок в Move — тот же ключ, что у Update/Delete (S2c-11). Окно правки длинное: после
    /// проверки файла UpdateCore строит модель вызывающего (свой предыдущий Update её сбросил —
    /// холодная; проект на 500 заметок — скан milliseconds), и только потом читает и пишет файл.
    /// Окно переноса (mkdir 10-уровневой цели ≈ 0,1-0,3 мс) целиком лежит внутри скана модели
    /// (5-25 мс, запас 10-50×): без id-замка перенос успевает переместить файл, а правка после
    /// него читает старый путь — FileNotFoundException вместо честного null; либо, читай она
    /// раньше, запись подняла бы старый путь из-под переноса — заметка в двух местах. С id-замком
    /// операции сериализованы: ровно один исход — файл в единственном месте (новом), с прежним
    /// или новым содержимым. Гонка «перенос ↔ удаление» той же заметки — в
    /// SharedProjectNotesWriteContentTests (её исход не изменился).
    /// </summary>
    [Fact]
    public async Task Гонка_ПереносЗаметкиПротивПравкиТойЖеЗаметки_ИсходОдин()
    {
        AddMember(_editorId, ProjectRole.Editor);
        var sut = Service();
        // Фон для скана модели: 500 заметок — холодная модель редактора внутри UpdateCore
        // стоит milliseconds; это и есть окно, внутри которого обязан уместиться перенос.
        for (var n = 0; n < 500; n++)
            sut.Create(_ownerId, new CreateNoteRequest($"Фон{n}", "# Фон\n", _projectId, Folder: "Фон"));

        const int attempts = 3;
        for (var i = 0; i < attempts; i++)
        {
            var title = $"Живая{i}";
            var newFolder = string.Join('/', Enumerable.Range(1, 10).Select(j => $"н{i}-{j}"));
            var created = sut.Create(_ownerId, new CreateNoteRequest(title, "# Тело до правки\n", _projectId, Folder: "Живые"));
            var oldFull = ProjectFile($"Живые/{title}.md");
            var newFull = ProjectFile($"{newFolder}/{title}.md");

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mv = Task.Run(async () => { await start.Task; return sut.Move(_editorId, created.Id, newFolder); });
            var up = Task.Run(async () => { await start.Task; return sut.Update(_editorId, created.Id, new UpdateNoteRequest(Content: "# Новая правка\n")); });
            start.SetResult();
            var moved = await mv;
            var updated = await up;

            // Полуреального состояния нет ни при каком исходе гонки: старый путь пуст,
            // заметка ровно в одном месте — новом.
            File.Exists(oldFull).Should().BeFalse($"«{title}»: старый путь после гонки пуст (правка не подняла его из-под переноса)");
            File.Exists(newFull).Should().BeTrue($"«{title}»: заметка целиком в новом месте");
            if (updated is not null)
                File.ReadAllText(newFull).Should().Contain("Новая правка", $"«{title}»: выигравшая правка переехала вместе с заметкой");
        }
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
