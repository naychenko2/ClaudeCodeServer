using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// R2 (ADR-039, §7 «Учитываемые пути»): учёт чтений страниц вики через MCP-инструменты
/// (NotesToolset → WikiReadsStore, keeper/reads.json). Проверяется ровно то, что говорит
/// инвариант «каждое чтение страницы вики агентом учитывается», и ровно границы учёта:
/// - notes_read видимой страницы → Read=1; скрытой — отказ и записи нет (учёт после
///   проверки видимости, не до неё);
/// - два параллельных notes_read одной страницы → Read=2 (замок стора, R1);
/// - wiki_query → Query=1 по каждой странице, попавшей в ответ; _index — проекция
///   раздела, в учёт не идёт;
/// - notes_resolve: видимая страница → Read; скрытая и обычная заметка — без учёта;
/// - notes_thread: корень-страница → Read; корень-скрытая и корень-обычная заметка — без учёта;
/// - ключ в reads.json — PageKey (путь под pages/), взятый ДО RedactWikiDetail: он меняет
///   Path на путь показа с живым именем раздела («Проекты/Проект p1/…»). Страница проекного
///   раздела — единственная фикстура, где эти два пути различаются (ревью 7d358c09):
///   в «Общем»/«Личном» они совпадают, и мутация «учёт после RedactWikiDetail» на них не ловится;
/// - сбой учёта (нечитаемый стор → RecordMany=false) не меняет ответ инструмента.
///
/// Видимость и состав tools/list здесь не проверяются — это NotesToolsetWikiTests и
/// McpToolsetStabilityTests. Сам стор (формат, замок, отказ) — WikiReadsStoreTests.
///
/// Тест без хоста: TestWebApplicationFactory не поднимается, меток Host/Slow нет.
/// </summary>
public class NotesToolsetWikiReadsTests : IDisposable
{
    private const string User = "u1";
    private const string Own = "projects/p1";
    private const string Question = "перенести сборку на linux";

    private const string VisibleKey = "Общее/Прочитанная.md";
    private const string HiddenKey = "Личное/Скрытая.md";
    // PageKey страницы проекного раздела: единственный раздел, где путь показа
    // («Проекты/Проект p1/…», живое имя проекта из AddProject) отличается от ключа —
    // на этой грани и проверяется «ключ ДО RedactWikiDetail».
    private const string ProjectPageKey = "Проекты/p1/Проектная.md";
    private const string ProjectDisplayPath = "Проекты/Проект p1/Проектная.md";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FakeProjects _projects = new();
    private readonly SpheresDir _spheres = new();
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;
    private readonly NotesAiService _ai;
    private readonly NoteTaskSyncService _noteTasks;
    private readonly WikiKeeper _keeper;
    private readonly FakeSessions _sessions = new();

    public NotesToolsetWikiReadsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_toolset_wiki_reads_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();

        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        AddProject("p1");

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(new NoopKnowledgeIndex(), _notes, users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        _ai = new NotesAiService(_notes, _config, new NoopCheapRunner());
        _noteTasks = new NoteTaskSyncService(_notes, new NoopNoteTaskBridge(), _projects, _kb,
            new NotesNoopHubNotifier(), NullLogger<NoteTaskSyncService>.Instance);

        _keeper = new WikiKeeper(_notes, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private WikiReadsStore.Snapshot Reads() => WikiReadsStore.Read(Root);

    private void AddProject(string id)
    {
        var root = Path.Combine(_dir, "projects", id);
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        _projects.Items[id] = new Project { Id = id, OwnerId = User, Name = "Проект " + id, RootPath = root };
    }

    // ── обвязка вызова: та же, что у NotesToolsetWikiTests. Сервисы заявки (ingest/capture/
    // chatIngest) не подставляются: ни один из проверяемых здесь четырёх инструментов их не зовёт. ──

    private (NotesToolset Toolset, McpToolCallContext Ctx) McpEnv(string? projectId, string sessionId = "s-call")
    {
        var session = new Session { Id = sessionId, OwnerId = User, ProjectId = projectId, Name = "Чат вызова" };
        _sessions.All.Add(session);
        return (
            new NotesToolset(new AllowAllPersonas(), new AllowAllBindings(), new OwnSession(session),
                new NotesNoopHubNotifier(), new NotesWideTurnFrame(), new NotesNoActorDenials(),
                _notes, _kb, _ai, _noteTasks, _config,
                sessionsDir: _sessions, spheres: _spheres, projects: _projects, flags: new WikiFlagOn()),
            new McpToolCallContext(User, sessionId, sessionId));
    }

    private static McpToolCallResult Call(NotesToolset toolset, McpToolCallContext ctx, string tool, JsonObject args) =>
        toolset.CallAsync(tool, args, ctx, CancellationToken.None).GetAwaiter().GetResult();

    private static JsonObject Args(params (string Key, string Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in pairs) obj[k] = v;
        return obj;
    }

    /// <summary>Отказ проверяется дословно (Be, а не Contain): неразличимость отказа с
    /// несуществующим — часть правила (NotesToolsetWikiTests, хвост R-WIKI-PRIV).</summary>
    private static void AssertDenied(McpToolCallResult res, string because)
    {
        res.IsError.Should().BeTrue($"отказ обязан быть отказом: {res.Text}");
        res.Text.Should().Be(because, $"причина отказа: {res.Text}");
    }

    // ── вики-фикстура: видимая (common), скрытая в проектном ходу (personal) и проектного
    // раздела (projects/p1) страницы ──

    private static string TopicPage(string title, string summary) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}

        ## Сводка
        {{summary}}

        ## Хронология

        ## Противоречия

        ## История
        """;

    private void Write(string sectionKey, string pageKey, string content)
    {
        var r = _keeper.WritePageAsync(User, sectionKey, pageKey, content).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private void Seed()
    {
        Write(WikiSections.Common, VisibleKey, TopicPage("Прочитанная", "Видимая страница для учёта."));
        Write(WikiSections.Personal, HiddenKey, TopicPage("Скрытая", "Скрытая в проектном ходу страница."));
        Write(Own, ProjectPageKey, TopicPage("Проектная", "Проектная страница для учёта ключа."));
    }

    /// <summary>Id страницы — детерминированный (base64url(sourceKey|relPath), как NotesService.EncodeId).</summary>
    private static string PageId(string relUnderPages) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("wiki|" + relUnderPages))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ── notes_read: видимая страница учитывается, скрытая — нет (учёт после видимости) ──

    [Fact]
    public void Рид_ВидимаяСтраницаУчитываетсяRead()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", PageId(VisibleKey))));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Видимая страница для учёта");

        var snap = Reads();
        snap.Valid.Should().BeTrue("учёт валидного стора записан");
        snap.Pages.Keys.Should().BeEquivalentTo(new[] { VisibleKey });
        snap.Pages[VisibleKey].Read.Should().Be(1);
        // Вид не другой группы: через MCP страница не «recall» и не «human».
        snap.Pages[VisibleKey].Recall.Should().Be(0);
        snap.Pages[VisibleKey].Query.Should().Be(0);
        snap.Pages[VisibleKey].Human.Should().Be(0);
    }

    [Fact]
    public void Рид_СтраницаПроектногоРазделаУчитываетсяПоPageKey()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", PageId(ProjectPageKey))));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Проектная страница для учёта ключа");
        // Путь показа отличается от PageKey живым именем проекта: только на этой грани
        // «учёт после RedactWikiDetail» отличается от «до» (в «Общем» пути совпадают).
        res.Text.Should().Contain(ProjectDisplayPath, "ответ несёт путь показа (RedactWikiDetail)");

        var snap = Reads();
        snap.Valid.Should().BeTrue();
        // Мутация (ревью 7d358c09) «учёт после RedactWikiDetail»: этот тест красный —
        // ключом стал бы путь показа «Проекты/Проект p1/…».
        snap.Pages.Keys.Should().BeEquivalentTo(new[] { ProjectPageKey },
            "ключ — detail.Path ДО RedactWikiDetail (NotesToolset.cs:191), не путь показа");
        snap.Pages[ProjectPageKey].Read.Should().Be(1);
    }

    [Fact]
    public void Рид_СкрытаяСтраницаНеУчитывается()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");
        var id = PageId(HiddenKey);

        var res = Call(toolset, ctx, "notes_read", Args(("id", id)));

        AssertDenied(res, $"Заметка {id} не найдена.");
        // Мутация (критерий c3) «учёт до проверки видимости»: этот тест красный —
        // отказанная страница попала бы в стор.
        Reads().Pages.Should().BeEmpty("отказ «не найдена» — не чтение, в учёт не идёт");
    }

    [Fact]
    public async Task Рид_ДваПараллельныхВызоваОднойСтраницыОбаУчитаны()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");
        var id = PageId(VisibleKey);

        var first = toolset.CallAsync("notes_read", Args(("id", id)), ctx, CancellationToken.None);
        var second = toolset.CallAsync("notes_read", Args(("id", id)), ctx, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        foreach (var res in results) res.IsError.Should().BeFalse(res.Text);
        // Read-modify-write под семафором файла (R1): второй вызов не затёрл первый.
        Reads().Pages[VisibleKey].Read.Should().Be(2);
    }

    // ── wiki_query: Query по каждой попавшей в ответ странице; _index — не страница ──

    [Fact]
    public void ВикиКвери_КаждаяПопавшаяСтраницаQueryIndexНеУчитывается()
    {
        // Две подходящие страницы в видимых из чата p1 разделах, третья — в скрытом личном
        // (тот же текст: различие должно быть о наборе, а не о слове).
        Write(Own, "Проекты/p1/Сущность-сборки.md", TopicPage("Сущность-сборки", "Сборку решено перенести на linux."));
        Write(WikiSections.Common, "Общее/Общий-факт.md", TopicPage("Общий-факт", "Общее: сборку перенести на linux."));
        Write(WikiSections.Personal, "Личное/Личный-факт.md", TopicPage("Личный-факт", "Личное: сборка на linux."));
        // Каталоги разделов — чтобы «_index не считается» проверялся против существующих файлов.
        foreach (var key in new[] { Own, WikiSections.Common })
            WikiProjections.RegenerateSection(Root, key);

        var (toolset, ctx) = McpEnv("p1");
        var res = Call(toolset, ctx, "wiki_query", Args(("question", Question)));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Сущность-сборки").And.Contain("Общий-факт");

        var snap = Reads();
        snap.Valid.Should().BeTrue();
        snap.Pages.Keys.Should().BeEquivalentTo(new[] { "Общее/Общий-факт.md", "Проекты/p1/Сущность-сборки.md" },
            "учёт ровно по страницам, попавшим в ответ: скрытый личный раздел и _index — нет");
        snap.Pages["Общее/Общий-факт.md"].Query.Should().Be(1);
        snap.Pages["Проекты/p1/Сущность-сборки.md"].Query.Should().Be(1);
        snap.Pages.Keys.Should().NotContain(k => k.Contains("_index", StringComparison.Ordinal));
    }

    // ── notes_resolve: видимая страница — Read; скрытая и обычная заметка — без учёта ──

    [Fact]
    public void Резолв_ВидимаяСтраницаУчитываетсяRead()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_resolve", Args(("name", "Прочитанная")));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Видимая страница для учёта");
        var snap = Reads();
        snap.Valid.Should().BeTrue();
        snap.Pages.Keys.Should().BeEquivalentTo(new[] { VisibleKey },
            "резолв по имени видит и скрытые страницы (пул глобален), но в учёт идёт только видимая");
        snap.Pages[VisibleKey].Read.Should().Be(1);
    }

    [Fact]
    public void Резолв_СтраницаПроектногоРазделаУчитываетсяПоPageKey()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_resolve", Args(("name", "Проектная")));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain(ProjectDisplayPath, "ответ несёт путь показа (RedactWikiDetail)");

        var snap = Reads();
        snap.Valid.Should().BeTrue();
        // Мутация «учёт после RedactWikiDetail» (NotesToolset.cs:491): ключ — r.Note.Path ДО него.
        snap.Pages.Keys.Should().BeEquivalentTo(new[] { ProjectPageKey },
            "ключ — r.Note.Path ДО RedactWikiDetail, не путь показа");
        snap.Pages[ProjectPageKey].Read.Should().Be(1);
    }

    [Fact]
    public void Резолв_СкрытаяСтраницаНеУчитывается()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_resolve", Args(("name", "Скрытая")));

        AssertDenied(res, "Заметка «Скрытая» не найдена.");
        // Мутация (критерий c3) «учёт до visible.Contains»: этот тест красный.
        Reads().Pages.Should().BeEmpty("отказанная страница — не чтение");
    }

    [Fact]
    public void Резолв_ОбычнаяЗаметкаНеУчитывается()
    {
        Seed();
        var note = _notes.Create(User, new CreateNoteRequest(
            Title: "Обычная-заметка", Content: "Содержимое обычной заметки.", Source: "p1"));
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_resolve", Args(("name", "Обычная-заметка")));

        res.IsError.Should().BeFalse(res.Text);
        Reads().Pages.Should().BeEmpty("обычная заметка — не страница вики");
    }

    // ── notes_thread: корень-страница — Read; корень-скрытая и корень-обычная заметка — без учёта ──

    [Fact]
    public void Тред_КореньСтраницаУчитываетсяRead()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_thread", Args(("id", PageId(VisibleKey))));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Видимая страница для учёта");
        Reads().Pages.Keys.Should().BeEquivalentTo(new[] { VisibleKey });
        Reads().Pages[VisibleKey].Read.Should().Be(1);
    }

    [Fact]
    public void Тред_КореньСтраницыПроектногоРазделаУчитываетсяПоPageKey()
    {
        Seed();
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_thread", Args(("id", PageId(ProjectPageKey))));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain(ProjectDisplayPath, "ответ несёт путь показа (RedactWikiDetail)");

        var snap = Reads();
        snap.Valid.Should().BeTrue();
        // Мутация «учёт после RedactWikiDetail» (NotesToolset.cs:391): ключ — rootDetail.Path ДО него.
        snap.Pages.Keys.Should().BeEquivalentTo(new[] { ProjectPageKey },
            "ключ — rootDetail.Path ДО RedactWikiDetail, не путь показа");
        snap.Pages[ProjectPageKey].Read.Should().Be(1);
    }

    [Fact]
    public void Тред_КореньСкрытаяСтраницаИКореньОбычнаяЗаметкаНеУчитываются()
    {
        Seed();
        var note = _notes.Create(User, new CreateNoteRequest(
            Title: "Обычный-корень", Content: "Содержимое обычной заметки.", Source: "p1"));
        var (toolset, ctx) = McpEnv("p1");
        var hiddenId = PageId(HiddenKey);

        var hidden = Call(toolset, ctx, "notes_thread", Args(("id", hiddenId)));
        AssertDenied(hidden, $"Комментарий {hiddenId} не найден.");

        var own = Call(toolset, ctx, "notes_thread", Args(("id", note.Id)));
        own.IsError.Should().BeFalse(own.Text);

        Reads().Pages.Should().BeEmpty("корень-скрытая (отказ) и корень-обычная заметка — не чтение страницы");
    }

    // ── сбой учёта не меняет ответ инструмента ──

    [Fact]
    public void СбойУчётаНеМеняетОтветИнструмента()
    {
        Seed();
        // Нечитаемый стор: RecordMany отказывает (fail-closed R1) — ответ notes_read тот же.
        File.WriteAllText(SafePath.Join(Root, "keeper/reads.json"), "{ не json");
        var (toolset, ctx) = McpEnv("p1");

        var res = Call(toolset, ctx, "notes_read", Args(("id", PageId(VisibleKey))));

        res.IsError.Should().BeFalse(res.Text);
        res.Text.Should().Contain("Видимая страница для учёта");
        Reads().Valid.Should().BeFalse("стор остался нечитаемым: учёт не записан, и это не исключение наружу");
    }

    // ── обвязка швов (по образцу NotesToolsetWikiTests) ──

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class SpheresDir : ISphereDirectory
    {
        public readonly Dictionary<string, List<string>> Spheres = new();

        public bool Enabled(string ownerId) => true;
        public string? SphereOf(string ownerId, string projectId) =>
            Spheres.Keys.FirstOrDefault(s => Spheres[s].Contains(projectId));
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) =>
            Spheres.TryGetValue(sphereId, out var list) ? list.ToList() : [];
        public string? SphereName(string ownerId, string sphereId) => Spheres.ContainsKey(sphereId) ? "Сфера" : null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class FakeProjects : IProjectManager
    {
        public readonly Dictionary<string, Project> Items = new();

        public Project? GetById(string id) => Items.GetValueOrDefault(id);
        public IReadOnlyCollection<Project> GetByOwner(string userId) => Items.Values.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => Items.Values.ToList();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }

    private sealed class FakeSessions : ISessionDirectory
    {
        public readonly List<Session> All = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(new List<StoredMessage>());
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    private sealed class OwnSession(Session session) : IMcpSessionAccessor
    {
        public Session? GetOwned(string sessionId, string ownerId) =>
            sessionId == session.Id && ownerId == session.OwnerId ? session : null;
    }

    private sealed class AllowAllPersonas : IPersonaResolver
    {
        public Persona? Get(string id, string userId) => null;
    }

    private sealed class AllowAllBindings : IMcpPersonaBindings
    {
        public bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key) => true;
        public bool SectionEnabled(string? ownerId, Persona? persona, string key) => true;
    }

    // Инертные швы соседних сервисов: нужны только чтобы тулсет был собран (все четыре
    // co-регистрируются одним рубильником вертикали). В этих тестах они не зовутся.
    private sealed class NoopKnowledgeIndex : IKnowledgeIndex
    {
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
        private static Task No() => Task.FromException(new InvalidOperationException("Dify не настроен"));
        private static Task<T> No<T>() => Task.FromException<T>(new InvalidOperationException("Dify не настроен"));
    }

    private sealed class NoopNoteTaskBridge : INoteTaskBridge
    {
        public IReadOnlyList<NoteTaskRef> GetBySourceNote(string ownerId, string noteId) => [];
        public NoteTaskRef Create(string? projectId, string ownerId, NoteTaskCreateRequest req) =>
            throw new InvalidOperationException("задача из заметки в этом тесте не ожидается");
        public NoteTaskRef? Update(string ownerId, string taskId, NoteTaskUpdateRequest req) => null;
        public NoteTaskRef? SpawnNextOccurrence(string ownerId, string completedTaskId) => null;
    }

    private sealed class NoopCheapRunner : ICheapTextRunner
    {
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
            Task.FromException<T>(new InvalidOperationException($"ИИ-действие {actionKey} здесь не ожидается"));
    }
}
