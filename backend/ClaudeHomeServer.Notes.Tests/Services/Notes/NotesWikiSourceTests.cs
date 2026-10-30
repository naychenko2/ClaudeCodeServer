using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Tests.Services;

// Источник wiki в графе NotesService (S2e): появляется только по признаку заведённости
// (pages/_schema.md), читается как любой другой источник, а пишет в него один Хранитель —
// отказ с кодом wiki_readonly на обоих серверных путях: REST (409) и MCP (тот же код).
//
// Сами пути записи (EnsureNotWiki по каноническому пути) охраняет NotesWikiGateTests; здесь —
// появление источника, контракт отказа на вызывающих сторонах и BuildFresh (ADR-035, реш. 1, 2, 4).
public class NotesWikiSourceTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;     // dataDir
    private readonly string _vault;   // {dir}/notes/{User} — личный vault
    private readonly string _pages;   // {dir}/wiki/{User}/pages — корень источника wiki
    // Гейт фич-флагов (Core-шов): источник wiki отдаётся только при включённом llm-wiki
    // (хвост ревью S2e). Тесты этого файла — про заведённую вику с включённым флагом;
    // выключенный флаг проверяется отдельно, в разделе 6.
    private readonly WikiFlagGate _flags = new();
    private readonly NotesService _sut;
    private readonly NotesKnowledgeService _kb;
    private readonly NotesAiService _ai;
    private readonly NoteTaskSyncService _noteTasks;
    private readonly RecordingLog _noteTasksLog = new();

    public NotesWikiSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_wiki_source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _vault = Path.Combine(_dir, "notes", User);
        Directory.CreateDirectory(_vault);
        _pages = WikiPaths.Pages(_dir, User);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        _sut = new NotesService(projects, config, NullLogger<NotesService>.Instance, flags: _flags);
        _kb = new NotesKnowledgeService(new NoopKnowledgeIndex(), _sut, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        _ai = new NotesAiService(_sut, config, new NoopCheapRunner());
        _noteTasks = new NoteTaskSyncService(_sut, new NoopNoteTaskBridge(), projects, _kb,
            new RecordingNotesNotifier(), _noteTasksLog);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Источник появляется только по признаку заведённости ─────────────

    [Fact]
    public void ЗаведённаяВики_ИсточникВГрафе_СтраницаРезолвитсяИзЛичнойЗаметки()
    {
        InitWiki();
        WriteWikiPage("Личное", "Страница вики", "# Страница вики\n\nФакт, который пишет Хранитель.");
        WriteFile(Path.Combine(_vault, "Моя заметка.md"), "# Моя заметка\n\nСм. [[Страница вики]].");

        var all = _sut.GetSummaries(User, null, null);
        var page = all.SingleOrDefault(n => n.Title == "Страница вики");
        page.Should().NotBeNull("заведённая вики — отдельный источник графа (ADR-035, решение 1)");
        page!.Source.Should().Be("wiki");

        // Служебные файлы раздела (_schema/_index/_log/_health) — не заметки (тот же смысл,
        // что templates/): их в списке нет
        all.Select(n => n.Title).Should().NotContain("_schema");

        // Резолв [[…]] из личной заметки — обычный, тем же ByName: отдельного резолвера вики нет
        var mine = all.Single(n => n.Title == "Моя заметка");
        _sut.GetDetail(User, mine.Id)!.Links
            .Should().Contain(l => l.Resolved && l.TargetId == page.Id,
                "ссылка заметки человека на страницу вики обязана разрешиться");
        var resolved = _sut.ResolveByName(User, "Страница вики", null);
        resolved.HasValue.Should().BeTrue();
        resolved!.Value.Note.Source.Should().Be("wiki");

        // «Куда создать» read-only источник не предлагает: создание там всё равно отказывает
        _sut.GetSources(User).Select(s => s.Key).Should().NotContain("wiki");
    }

    [Fact]
    public void БезSchema_ИсточникаWiki_Нет_ИСтраницаНеРезолвится()
    {
        // pages/ и страницы есть, признака заведённости нет: источника нет ни в графе,
        // ни в резолве (гейт по признаку на диске, а не по фич-флагу — fail-closed)
        Directory.CreateDirectory(_pages);
        WriteWikiPage("Личное", "Страница вики", "# Страница вики");
        WriteFile(Path.Combine(_vault, "Моя заметка.md"), "# Моя заметка\n\nСм. [[Страница вики]].");

        _sut.GetSummaries(User, null, null).Select(n => n.Title).Should().NotContain("Страница вики");
        _sut.ResolveByName(User, "Страница вики", null).HasValue.Should().BeFalse();
        _sut.GetSources(User).Select(s => s.Key).Should().NotContain("wiki");
    }

    // ─── 2. BuildFresh: построение мимо кэша (ADR-035, решение 4, шаг 1) ───

    [Fact]
    public void BuildFresh_ВидитФайлМладшеTTL_КэшЕгоНеВидит()
    {
        InitWiki();
        // Модель уже построена первым чтением и держится в кэше (TTL 2 с)
        _sut.GetSummaries(User, null, null).Select(n => n.Title).Should().NotContain("Свежая страница");

        WriteWikiPage("Общее", "Свежая страница", "# Свежая страница"); // записана меньше 2 с назад

        // Читательский путь — из кэша: страницы нет
        _sut.GetSummaries(User, null, null).Select(n => n.Title).Should().NotContain("Свежая страница");
        // Путь Хранителя — мимо кэша: то, что реально на диске
        _sut.BuildFresh(User).Notes.Select(n => n.Title).Should().Contain("Свежая страница");
    }

    // ─── 3. REST: 409 с кодом wiki_readonly (ревью S1a, п. 1) ──────────────

    [Fact]
    public async Task Контроллер_СозданиеПравкаПереносУдалениеВики_409СКодом()
    {
        InitWiki();
        var pageFile = WriteWikiPage("Личное", "Страница вики", "# Страница вики\n\nисходное тело");
        var wikiId = IdOf("Страница вики");
        var mineId = _sut.Create(User, new CreateNoteRequest("Заметка человека", "тело", "personal")).Id;

        var ctl = Controller();

        AssertWiki409((await ctl.Create(new CreateNoteRequest("Новая страница", "тело", "wiki"))).Result,
            "создание в источник wiki");
        AssertWiki409((await ctl.Update(wikiId, new UpdateNoteRequest(Content: "новое тело"))).Result,
            "правка страницы вики");
        AssertWiki409((await ctl.Move(wikiId, new MoveNoteRequest("Подпапка"))).Result,
            "перенос внутри вики");
        AssertWiki409((await ctl.Move(mineId, new MoveNoteRequest(null, "wiki"))).Result,
            "перенос заметки в вику");
        AssertWiki409(await ctl.Delete(wikiId), "удаление страницы вики");

        // Ни одна попытка не тронула файл и не оставила половины записи
        File.ReadAllText(pageFile).Should().Be("# Страница вики\n\nисходное тело");

        // Отказ не всеобщий: обычная личная заметка тем же действием проходит
        (await ctl.Create(new CreateNoteRequest("Обычная заметка", "тело", "personal"))).Result
            .Should().BeAssignableTo<OkObjectResult>();
    }

    // ─── 4. MCP: тот же код отказа, не общая «Ошибка» транспорта (ревью S1a, п. 2) ──

    [Fact]
    public async Task Тулсет_ТеЖеОперацииОтказываютСКодомWikiReadonly()
    {
        InitWiki();
        WriteWikiPage("Личное", "Страница вики", "# Страница вики\n\nисходное тело");
        var wikiId = IdOf("Страница вики");
        var (toolset, ctx) = McpEnv();

        AssertWikiDeny(await toolset.CallAsync("notes_create",
            Args(("title", "Новая страница"), ("content", "тело"), ("source", "wiki")), ctx, default), "notes_create");
        AssertWikiDeny(await toolset.CallAsync("notes_update",
            Args(("id", wikiId), ("content", "новое тело")), ctx, default), "notes_update");
        AssertWikiDeny(await toolset.CallAsync("notes_move",
            Args(("id", wikiId), ("folder", "Подпапка")), ctx, default), "notes_move");
        AssertWikiDeny(await toolset.CallAsync("notes_delete", Args(("id", wikiId)), ctx, default), "notes_delete");

        // Отказ не всеобщий: обычная личная заметка тем же инструментом создаётся
        (await toolset.CallAsync("notes_create",
            Args(("title", "Обычная заметка"), ("content", "тело")), ctx, default)).IsError.Should().BeFalse();
    }

    // ─── 5. Побочный писатель: страница вики с чекбоксом не меняется (реш. 2) ──

    [Fact]
    public async Task ОбратнаяЗадачаГалочки_НеМеняетСтраницуВики()
    {
        // Обе заметки — до первого чтения: модель кэшируется на владельца, и файл,
        // записанный после построения, следующим IdOf уже не виден (тот же смысл, что у
        // BuildFresh выше). Строка чекбокса — 0-based, индекс 2.
        InitWiki();
        var wikiFile = WriteWikiPage("Личное", "План вики", "# План вики\n\n- [ ] Пункт вики\n");
        var normal = WriteFile(Path.Combine(_vault, "План.md"), "# План\n\n- [ ] Пункт\n");
        var before = Sha256(wikiFile);
        var wikiId = IdOf("План вики");
        var normalId = IdOf("План");

        await _noteTasks.SyncTaskToNoteAsync(User, TaskOf(wikiId, line: 2, done: true));

        Sha256(wikiFile).Should().Be(before,
            "галочку в страницу вики ставит только Хранитель (ADR-035, решение 2)");

        // Одного файла мало: без пропуска вики запись упёрлась бы в EnsureNotWiki, а общий
        // catch в NoteTaskSyncService оставил бы файл нетронутым — различие только в
        // предупреждении «Не удалось синхронизировать галочку…». Доказательство пропуска —
        // сама отсутствовавшая попытка: ни одного предупреждения в журнале (ревью S2e, п. 1).
        _noteTasksLog.Warnings.Should().BeEmpty(
            "пропуск обязан исключить саму попытку записи в вики: без неё Update бросает "
            + "wiki_readonly, и catch пишет о несинхронизированной галочке — файл при этом тот же");

        // Тот же вызов на обычной заметке файл меняет: иначе тест проходил бы «на ничего»
        await _noteTasks.SyncTaskToNoteAsync(User, TaskOf(normalId, line: 2, done: true));
        File.ReadAllText(normal).Should().Contain("- [x] Пункт");
    }

    // ─── 6. Флаг llm-wiki: источник отдаётся только при включённом (хвост ревью S2e) ──

    [Fact]
    public void ФлагВыключен_ИсточникаWiki_Нет_ЗаписьВВикиВсёРавноОтказана()
    {
        // «Флаг прячет UI и инструменты»: до включения llm-wiki источник wiki не входит
        // в граф — ни в списке, ни в резолве. Гейт записи от флага НЕ зависит: EnsureNotWiki
        // бьёт по каноническому пути, и заведённая вика закрыта чужим писателям всегда.
        InitWiki();
        WriteWikiPage("Личное", "Страница вики", "# Страница вики");
        _flags.On = false;

        _sut.GetSummaries(User, null, null).Select(n => n.Title).Should().NotContain("Страница вики",
            "при выключенном флаге источник wiki не отдаётся ни человеку в списке, ни агенту");
        _sut.ResolveByName(User, "Страница вики", null).HasValue.Should().BeFalse();

        var ex = Assert.Throws<WikiReadOnlyException>(() =>
            _sut.Create(User, new CreateNoteRequest("Новая страница", "тело", "wiki")));
        ex.Message.Should().Be(WikiReadOnlyException.ErrorText,
            "отказ записи не зависит от флага: он держится признаком заведённости и путём");

        // Включённый флаг — источник есть (тот же диск, тот же сервис, другой флаг).
        // Модель кэшируется на 2 с: без явной инвалидации последний вызов читал бы её
        // с выключенным флагом и прошёл бы «на ничего».
        _flags.On = true;
        _sut.Invalidate(User);
        _sut.GetSummaries(User, null, null).Select(n => n.Title).Should().Contain("Страница вики");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    private void InitWiki()
    {
        Directory.CreateDirectory(_pages);
        File.WriteAllText(Path.Combine(_pages, WikiPaths.SchemaFileName),
            "# Схема раздела\n", new UTF8Encoding(false));
    }

    private string WriteWikiPage(string section, string title, string content)
    {
        var dir = Path.Combine(_pages, section);
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, title + ".md");
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    private static string WriteFile(string full, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    private string IdOf(string title) =>
        _sut.GetSummaries(User, null, null).Single(n => n.Title == title).Id;

    private static TaskItem TaskOf(string noteId, int line, bool done) => new()
    {
        Id = Guid.NewGuid().ToString(),
        OwnerId = User,
        Title = "Задача из чекбокса",
        Status = done ? TaskItemStatus.Done : TaskItemStatus.Todo,
        SourceNoteId = noteId,
        SourceNoteLine = line,
    };

    // Контроллер напрямую: тот же UserId по claim "sub", что в бою (JwtRegisteredClaimNames.Sub
    // == "sub"); хост не поднимается — тестируется контракт действия, не транспорт.
    private NotesController Controller()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", User)], "test"));
        return new NotesController(_sut, _kb, _ai, _noteTasks, new RecordingNotesNotifier())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static void AssertWiki409(IActionResult? result, string what)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{what} обязан отдать 409 с телом").Which;
        obj.StatusCode.Should().Be(409);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement;
        body.GetProperty("code").GetString().Should().Be(WikiReadOnlyException.ErrorCode);
        body.GetProperty("message").GetString().Should().Be(WikiReadOnlyException.ErrorText);
    }

    private (NotesToolset Toolset, McpToolCallContext Ctx) McpEnv()
    {
        var session = new Session { Id = "s-wiki-source-test", OwnerId = User }; // без проекта: источник по умолчанию — личный
        return (
            new NotesToolset(new AllowAllPersonas(), new AllowAllBindings(), new OwnSession(session),
                new TestSessionBroadcaster(), _sut, _kb, _ai, _noteTasks),
            new McpToolCallContext(User, session.Id, session.Id));
    }

    private static void AssertWikiDeny(McpToolCallResult res, string tool)
    {
        res.IsError.Should().BeTrue($"{tool} обязан отказать");
        // Не общий текст транспорта («Ошибка: …») и не 500: отказ с тем же кодом, что у REST
        res.Text.Should().StartWith(WikiReadOnlyException.ErrorCode + ":")
            .And.Contain(WikiReadOnlyException.ErrorText, tool);
    }

    private static JsonObject Args(params (string Key, string Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v;
        return o;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ─── Фейки швов ────────────────────────────────────────────────────────

    // Recording-логгер NoteTaskSyncService: тест проверяет отсутствие предупреждения —
    // оно появляется только если запись в вики реально пробовалась и упала в общий catch
    // (ревью S2e, п. 1). Warn+ — чтобы информационные строки (промоут) не шумели.
    private sealed class RecordingLog : ILogger<NoteTaskSyncService>
    {
        public List<string> Warnings { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    private sealed class RecordingNotesNotifier : INotesHubNotifier
    {
        public Task BroadcastNotesChangedAsync(string userId, string action, string noteId) => Task.CompletedTask;
        public Task BroadcastTaskChangedAsync(string userId, string action, string taskId) => Task.CompletedTask;
    }

    // Гейт llm-wiki с управляемым состоянием: семантика — ровно SourcesFor-проверка
    // (один ключ, один владелец). Прочие флаги — всегда false, как у FeatureFlagService
    // без override.
    private sealed class WikiFlagGate : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool On { get; set; } = true;
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoopKnowledgeIndex : IKnowledgeIndex
    {
        // Dify не настроен — как в бою: все методы бросают, потребители гейтятся Available
        public bool IsConfigured => false;
        public Task<string> CreateDatasetAsync(string name, string permission = "only_me",
            string? description = null, string? indexingTechnique = null) => No<string>();
        public Task DeleteDocumentAsync(string datasetId, string documentId) => No();
        public Task DeleteDatasetAsync(string datasetId) => No();
        public Task RenameDatasetAsync(string datasetId, string newName) => No();
        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName,
            string content, List<string>? tags = null,
            IReadOnlyDictionary<string, string>? metadata = null) => No<DifyDocumentInfo>();
        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query,
            int topK = 8, IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and") =>
            No<IReadOnlyList<DifyRetrieveChunk>>();
        private static Exception Error() => new InvalidOperationException("Dify не настроен");
        private static Task No() => Task.FromException(Error());
        private static Task<T> No<T>() => Task.FromException<T>(Error());
    }

    private sealed class NoopCheapRunner : ICheapTextRunner
    {
        // ИИ-действия в этих тестах не зовутся; если что-то позвал — пусть будет видно
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "none";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<string>(actionKey);
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Throw<string?>(actionKey);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Throw<OneShotResult>(actionKey);
        private static Task<T> Throw<T>(string actionKey) =>
            Task.FromException<T>(new InvalidOperationException(
                $"ИИ-действие {actionKey} не должно вызываться в этом тесте"));
    }

    private sealed class NoopNoteTaskBridge : INoteTaskBridge
    {
        public IReadOnlyList<NoteTaskRef> GetBySourceNote(string ownerId, string noteId) => [];
        public NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req) =>
            throw new InvalidOperationException("создание задачи в этом тесте не ожидается");
        public NoteTaskRef? Update(string ownerId, string taskId, NoteTaskUpdateRequest req) => null;
        public NoteTaskRef? SpawnNextOccurrence(string ownerId, string completedTaskId) => null;
    }

    // Тулсет на каждый вызов резолвит сессию-вызывателя, персону и её привязки: здесь
    // интересуются не они, а отказ источника — поэтому все три шва «разрешающие».
    private sealed class OwnSession(Session session) : IMcpSessionAccessor
    {
        public Session? GetOwned(string sessionId, string ownerId) =>
            sessionId == session.Id && ownerId == session.OwnerId ? session : null;
    }

    private sealed class AllowAllPersonas : IPersonaResolver
    {
        public Persona? Get(string id, string userId) => null; // чат без персоны: аннотационный модуль не нужен
    }

    private sealed class AllowAllBindings : IMcpPersonaBindings
    {
        public bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key) => true;
        public bool SectionEnabled(string? ownerId, Persona? persona, string key) => true;
    }
}
