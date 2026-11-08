using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// F-3 (разрез 4bc3d04f §3): AI-ручки заметок (toc/translate) проверяют право WriteContent
/// ДО вызова модели. Читатель общего проекта получает отказ и счётчик вызовов LLM = 0;
/// редактор, владелец и личный vault — как раньше. Тест бесхостовый: настоящий NotesService +
/// ProjectAccessService (образец SharedProjectNotesWriteContentTests), модель подменена счётным
/// ICheapTextRunner (образец NotesAiConsumerTests). Отказ проверяется на уровне сервиса
/// (UnauthorizedAccessException) — в 403 его ведёт существующий catch контроллера
/// (NotesController.cs:300,315), он не изменён.
/// </summary>
public class NotesAiWriteBeforeModelTests : IDisposable
{
    private const string ProjectNote = "Заметка проекта для AI";
    private const string NoteBody = "# Заметка проекта для AI\n\n## Первый раздел\n\nТекст первого раздела.\n\n## Второй раздел\n\nТекст второго раздела.\n";

    private readonly string _dir;
    private readonly string _projectRoot;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _projectId;
    // Одна точка доступа на сервис и на ручку: тот же экземпляр, то же решение о праве
    private readonly ProjectAccessService _access;

    public NotesAiWriteBeforeModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_ai_f3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = Config();

        // При пустом users.json UserStore создаёт дефолтного admin — он и есть владелец проекта
        _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("f3-editor", "pwd", "user").Id;
        _readerId = _users.Add("f3-reader", "pwd", "user").Id;
        foreach (var id in new[] { _ownerId, _editorId, _readerId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));

        _projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "notes"));
        File.WriteAllText(Path.Combine(_projectRoot, "notes", ProjectNote + ".md"), NoteBody);
        _projectId = _projects.Create("Проект F-3", _projectRoot, _ownerId, "owner").Id;

        _access = new ProjectAccessService(_projects, new NoSessions(), new SharedProjectsGate(_config, new FeatureFlagService(_users)));
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

    // Новый экземпляр NotesService на каждый шаг: кэш модели живёт 2 с (та же причина,
    // что в SharedProjectNotesWriteContentTests)
    private NotesService Service() => new(_projects, Config(), NullLogger<NotesService>.Instance, null, _access);

    private NotesAiService Ai(CountingRunner runner) => new(Service(), Config(), runner, _access);

    private void AddMember(string userId, ProjectRole role)
    {
        var members = _projects.GetById(_projectId)!.Members?.ToList() ?? [];
        members.Add(new ProjectMember { UserId = userId, Role = role, AddedBy = _ownerId });
        _projects.SetMembers(_projectId, members);
    }

    private string ProjectNoteId() =>
        Service().GetSummaries(_ownerId, _projectId, null).Single(n => n.Title == ProjectNote).Id;

    private string ProjectFile() => Path.Combine(_projectRoot, "notes", ProjectNote + ".md");

    // Счётная подмена модели: фиксирует каждый вызов и отвечает заготовкой под формат ручки
    // (toc — markdown-список, translate — с маркером «LANG:»). Раннер, который не считает,
    // не доказывает «нуль вызовов».
    private sealed class CountingRunner : ICheapTextRunner
    {
        public int Calls;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Answer(actionKey));
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<string?>(Answer(actionKey));
        }

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new OneShotResult(Answer(actionKey), null, 0));
        }

        private static string Answer(string actionKey) => actionKey switch
        {
            LocalActionCatalog.NoteToc => "- Первый раздел\n- Второй раздел",
            LocalActionCatalog.NoteTranslate => "LANG: English\nПереведённый текст заметки",
            _ => "[]",
        };
    }

    // --- Читатель общего проекта: отказ ДО модели, ноль вызовов, файл проекта не тронут ---

    [Fact]
    public async Task Читатель_Toc_ОтказДоМодели_ВызововLLmНоль()
    {
        AddMember(_readerId, ProjectRole.Reader);
        var id = ProjectNoteId();
        var runner = new CountingRunner();
        var ai = Ai(runner);

        var act = () => ai.SuggestTocAsync(_readerId, id, CancellationToken.None);

        // Тот же отказ, что у ручки записи заметки: контроллер ведёт его к Forbid() (403),
        // как у Update (NotesController.cs:300)
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        runner.Calls.Should().Be(0, "отказанная ручка не должна звать модель");
        File.ReadAllText(ProjectFile()).Should().Be(NoteBody, "содержимое проектной заметки не тронуто");
    }

    [Fact]
    public async Task Читатель_Translate_ОтказДоМодели_ВызововLLmНоль()
    {
        AddMember(_readerId, ProjectRole.Reader);
        var id = ProjectNoteId();
        var runner = new CountingRunner();
        var ai = Ai(runner);

        var act = () => ai.TranslateAsync(_readerId, id, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        runner.Calls.Should().Be(0, "отказанная ручка не должна звать модель");
        File.ReadAllText(ProjectFile()).Should().Be(NoteBody, "содержимое проектной заметки не тронуто");
    }

    // --- Редактор и владелец: как раньше — запись идёт, модель званa ровно раз на ручку ---

    [Theory]
    [InlineData("editor")]
    [InlineData("owner")]
    public async Task РедакторИВладелец_TocTranslate_ПишутКакРаньше(string who)
    {
        if (who == "editor") AddMember(_editorId, ProjectRole.Editor);
        var userId = who == "editor" ? _editorId : _ownerId;

        var id = ProjectNoteId();
        var runner = new CountingRunner();
        var ai = Ai(runner);

        var toc = await ai.SuggestTocAsync(userId, id, CancellationToken.None);
        var translated = await ai.TranslateAsync(userId, id, CancellationToken.None);

        toc.Should().NotBeNull();
        translated.Should().NotBeNull();
        runner.Calls.Should().Be(2, "прошедшая ручка зовёт модель ровно раз");

        var text = File.ReadAllText(ProjectFile());
        text.Should().Contain("## Оглавление");
        text.Should().Contain("## Перевод (English)");

        // Владелец-арендатор видит то же содержимое (свежий экземпляр: кэш модели 2 с)
        Service().GetDetail(_ownerId, id)!.Content.Should().Contain("## Оглавление");
    }

    // --- Личный vault: проектной двери нет (то же право, что у дневниковой заметки) ---

    [Fact]
    public async Task ЛичныйVault_TocTranslate_ПишетКакРаньше()
    {
        // Читатель общего проекта в своём vault пишет всегда: Source == personal —
        // та же ветка права, что у ResolveRoot для личного источника (NotesService.cs:1517)
        var note = Service().Create(_readerId, new CreateNoteRequest("Личная заметка", "Текст личной заметки.\n", "personal"));

        var runner = new CountingRunner();
        var ai = Ai(runner);

        var toc = await ai.SuggestTocAsync(_readerId, note.Id, CancellationToken.None);
        var translated = await ai.TranslateAsync(_readerId, note.Id, CancellationToken.None);

        toc.Should().NotBeNull();
        translated.Should().NotBeNull();
        runner.Calls.Should().Be(2);

        var own = Path.Combine(_dir, "notes", _readerId, "Личная заметка.md");
        File.Exists(own).Should().BeTrue("личная заметка остаётся в vault вызывающего");
        File.ReadAllText(own).Should().Contain("## Оглавление").And.Contain("## Перевод (English)");
        File.ReadAllText(ProjectFile()).Should().Be(NoteBody, "проектный файл при этом не тронут");
    }

    // --- Форма композиции: точка приходит через DI, без неё — деградация без отказа ---
    // Параметр IProjectAccess в конструкторе NotesAiService опционален (тот же приём, что
    // «правило пилота» в NotesSubsystemGateTests: обязательный параметр ронял бы резолв в
    // неполной композиции). Здесь — фактическое поведение DI-активации: с зарегистрированной
    // точкой она впрыснута и работает (отказ с нулём вызовов), без неё — объект строится.

    [Fact]
    public async Task ДиСТочкойДоступа_РезолвитРучку_СРаботающейПроверкой()
    {
        var runner = new CountingRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IProjectAccess>(_access);
        services.AddSingleton(Service());
        services.AddSingleton<ICheapTextRunner>(runner);
        services.AddSingleton(_config);
        services.AddSingleton<NotesAiService>();
        using var sp = services.BuildServiceProvider();

        var ai = sp.GetRequiredService<NotesAiService>();
        AddMember(_readerId, ProjectRole.Reader);
        var id = ProjectNoteId();

        var act = () => ai.SuggestTocAsync(_readerId, id, CancellationToken.None);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        runner.Calls.Should().Be(0, "DI обязан вмешать точку доступа в опциональный параметр");
    }

    [Fact]
    public void ДиБезТочкиДоступа_РезолвуетсяБезОтказа()
    {
        // Неполная композиция (хост без Main-регистраций): активация не должна падать —
        // дефолт null, дверь остаётся за Update (смысл опционального параметра)
        var services = new ServiceCollection();
        services.AddSingleton(Service());
        services.AddSingleton<ICheapTextRunner>(new CountingRunner());
        services.AddSingleton(_config);
        services.AddSingleton<NotesAiService>();
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<NotesAiService>();
        act.Should().NotThrow();
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
