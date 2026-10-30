using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// S6g-1: сужение recall и семантического поиска модели набором видимых разделов
// (ADR-035, решение 8; ADR-036, решения 2 и 6). Два класса проверок:
//   c1 — запрос к :wiki идёт с фильтром «wiki_section ∈ набор» и top-k ×3, но гарантия —
//       пост-фильтр кода: хит чужого раздела, вернувшийся из подменённого Dify вопреки
//       фильтру, отбрасывается;
//   c2 — в проектном ходе не возвращаются личная заметка и заметка соседнего проекта
//       (решение Андрея 2026-10-21: меняет поведение у всех владельцев с первого выката).
// Dify здесь — подменённый: он намеренно игнорирует фильтр запроса — ровно то состояние,
// ради которого и написан пост-фильтр.
public class WikiRecallFilterTests : IDisposable
{
    private const string User = "u1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FakeProjects _projects = new();
    // Сфер нет вовсе: набор проектного хода fail-closed — «projects/p1 + common»
    // (ADR-036, решение 2). Личный раздел и соседний проект в него не входят.
    private readonly NoSpheres _spheres = new();
    private readonly RecordingIndex _index = new();
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;

    public WikiRecallFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_recall_filter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Порог recall опущен: тестовые чанки подменённого Dify имеют score ниже боевого.
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Notes:AutoRecallMinScore"] = "0.1",
            })
            .Build();

        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        AddProject("p1");
        AddProject("q"); // тот же владелец, вне сферы p1 (сфер нет) — соседний проект

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance,
            flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(_index, _notes, users, _config,
            NullLogger<NotesKnowledgeService>.Instance, _spheres, _projects);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void AddProject(string id)
    {
        var root = Path.Combine(_dir, "projects", id);
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        _projects.Items[id] = new Project { Id = id, OwnerId = User, Name = "Проект " + id, RootPath = root };
    }

    private void InitWiki() =>
        new WikiKeeper(_notes, _config).EnsureInitializedAsync(User).GetAwaiter().GetResult();

    private void WriteWiki(string relUnderPages, string content)
    {
        // EnsureInitialized создаёт только _schema.md: папку раздела делает сам пишущий.
        var full = Path.Combine(WikiPaths.Pages(_dir, User), relUnderPages);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // docId, который подменённый Dify выдал при индексации документа с таким заголовком
    private string DocIdOf(string title) =>
        _index.Indexed.First(i => i.Title == title).DocId;

    private string DatasetIdOf(string suffix) =>
        _index.Datasets.First(d => d.Name.EndsWith(suffix, StringComparison.Ordinal)).Id;

    private async Task<IReadOnlyList<NoteSemanticHit>> ScopedSearch(string projectId) =>
        await _kb.SearchAsync(User, "сборка на linux", 8, new WikiVisibility.CallContext(projectId, null));

    // Путь хода целиком: настоящий контрибьютор сессии с ProjectId.
    private async Task<PromptSectionContribution?> RecallOf(string? projectId)
    {
        var session = new Session { Id = "s-turn", OwnerId = User, ProjectId = projectId, Name = "Чат" };
        var ctx = new PromptSessionContext(session, User, null, _dir, HasNotesMcp: true);
        return await new NotesRecallContributor(_kb, _config, NullLogger<NotesRecallContributor>.Instance)
            .BuildAsync(ctx, "сборка на linux");
    }

    // ─── c1: хит чужого раздела вопреки фильтру Dify отбросен пост-фильтром ───

    [Fact]
    public async Task ХитЧужогоРаздела_ВопрекиФильтруDify_ОтброшенПостФильтром()
    {
        InitWiki();
        WriteWiki("Проекты/p1/Знание.md", Page("Знание"));
        WriteWiki("Личное/Чужое.md", Page("Чужое"));
        (await _kb.SyncAllAsync(User)).Should().Be(2);

        var wikiDs = DatasetIdOf(":wiki");
        // Подменённый Dify нарушает свой фильтр: хит личного раздела приехал в проектный ход.
        _index.Response[wikiDs] = [
            ChunkWithDoc("Чужое", 0.9),
            ChunkWithDoc("Знание", 0.8),
        ];

        var hits = await ScopedSearch("p1");

        // Мутация «убрать пост-фильтр в SearchAsync» краснит этот assert: без него сюда
        // приходит «Чужое».
        hits.Select(h => h.Title).Should().Equal("Знание");

        // Запрос к :wiki действительно шёл с фильтром набора и запасом ×3 (ADR-036, реш. 6):
        // это оптимизация шума, а не гарантия — проверяем и сам запрос.
        var wikiReq = _index.Requests.Last(r => r.DatasetId == wikiDs);
        wikiReq.TopK.Should().Be(24, "запрос к :wiki берёт top-k с запасом ×3");
        wikiReq.Logic.Should().Be("or", "набор разделов — OR по ключам");
        wikiReq.Filters!.Select(f => (f.Name, f.Op, f.Value)).Should()
            .Equal(("wiki_section", "is", "projects/p1"), ("wiki_section", "is", "common"));
    }

    // ─── c2: проектный ход не возвращает личную заметку и заметку соседнего проекта ───

    [Fact]
    public async Task RecallПроектногоХода_НеВозвращаетЛичнуюЗаметкуИЗаметкуСоседнегоПроекта()
    {
        // p1 и q — проекты одного владельца, сфер нет (fail-closed: виден только projects/p1).
        // Все три заметки — одной темы, чтобы подменённый Dify вернул их все.
        WriteFile(Path.Combine(_dir, "notes", User, "Личная.md"), "# Личная\n\nСборка на linux лично.");
        WriteFile(Path.Combine(_dir, "projects", "q", "notes", "Соседняя.md"), "# Соседняя\n\nСборка на linux в проекте q.");
        WriteFile(Path.Combine(_dir, "projects", "p1", "notes", "Своя.md"), "# Своя\n\nСборка на linux в проекте p1.");
        (await _kb.SyncAllAsync(User)).Should().Be(3);

        var notesDs = DatasetIdOf(":notes");
        _index.Response[notesDs] = [
            ChunkWithDoc("Соседняя", 0.9),
            ChunkWithDoc("Личная", 0.85),
            ChunkWithDoc("Своя", 0.8),
        ];

        var hits = await ScopedSearch("p1");

        // 0 хитов с домом personal и projects/q — те самые «0 хитов» критерия c2.
        hits.Should().NotContain(h => h.Source == "personal");
        hits.Should().NotContain(h => h.Source == "q");
        hits.Select(h => h.Title).Should().Equal("Своя");

        // Тот же исход — на пути хода (контрибьютор): в секции только своя заметка.
        var contribution = await RecallOf("p1");
        contribution.Should().NotBeNull();
        contribution!.Sections.Should().HaveCount(1);
        contribution.Sections[0].Text.Should().Contain("Своя")
            .And.NotContain("Личная").And.NotContain("Соседняя");
    }

    // Явный поиск человека (REST, без контекста) — не сужается: человек видит всё (ADR-036, реш. 2).
    [Fact]
    public async Task ПутьБезКонтекста_ВозвращаетВсеЗаметкиКакПрежде()
    {
        WriteFile(Path.Combine(_dir, "notes", User, "Личная.md"), "# Личная\n\nСборка на linux лично.");
        WriteFile(Path.Combine(_dir, "projects", "q", "notes", "Соседняя.md"), "# Соседняя\n\nСборка на linux в проекте q.");
        (await _kb.SyncAllAsync(User)).Should().Be(2);

        var notesDs = DatasetIdOf(":notes");
        _index.Response[notesDs] = [ChunkWithDoc("Соседняя", 0.9), ChunkWithDoc("Личная", 0.85)];

        var hits = await _kb.SearchAsync(User, "сборка на linux", 8);
        hits.Select(h => h.Title).Should().Equal("Соседняя", "Личная");
    }

    // ─── обвязка Dify и горизонталей ───

    private DifyRetrieveChunk ChunkWithDoc(string title, double score) =>
        new("фрагмент " + title, score, DocIdOf(title), title);

    private static string Page(string title) =>
        "---\ntype: topic\nsummary: тест\nstatus: active\n---\n\n# " + title +
        "\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n";

    private sealed class NoSpheres : ISphereDirectory
    {
        public bool Enabled(string ownerId) => false;
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class FakeProjects : IProjectManager
    {
        public readonly Dictionary<string, Project> Items = new();

        public Project? GetById(string id) => Items.GetValueOrDefault(id);
        public IReadOnlyCollection<Project> GetByOwner(string userId) =>
            Items.Values.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => Items.Values.ToList();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }

    private sealed class WikiFlagOn : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    // Подменённый Dify: помнит созданные датасеты, проиндексированные документы и запросы
    // retrieve; отвечает тем, что положено в Response, — фильтр запроса игнорирует намеренно
    // (гарантию держит пост-фильтр кода, не Dify).
    private sealed class RecordingIndex : IKnowledgeIndex
    {
        public bool IsConfigured => true;
        public readonly List<(string Name, string Id)> Datasets = new();
        public readonly List<(string Title, string DocId)> Indexed = new();
        public readonly List<(string DatasetId, int TopK, IReadOnlyList<KnowledgeMetadataFilter>? Filters, string Logic)> Requests = new();
        public readonly Dictionary<string, List<DifyRetrieveChunk>> Response = new();

        public Task<string> CreateDatasetAsync(string name, string permission = "only_me",
            string? description = null, string? indexingTechnique = null)
        {
            var id = "ds-" + name;
            Datasets.Add((name, id));
            return Task.FromResult(id);
        }

        public Task DeleteDocumentAsync(string datasetId, string documentId) => Task.CompletedTask;
        public Task DeleteDatasetAsync(string datasetId) => Task.CompletedTask;
        public Task RenameDatasetAsync(string datasetId, string newName) => Task.CompletedTask;

        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName,
            string content, List<string>? tags = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var docId = "doc-" + (Indexed.Count + 1);
            Indexed.Add((fileName, docId));
            return Task.FromResult(new DifyDocumentInfo(docId, fileName, "completed"));
        }

        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query,
            int topK = 8, IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and")
        {
            Requests.Add((datasetId, topK, filters, logic));
            IReadOnlyList<DifyRetrieveChunk> r =
                Response.TryGetValue(datasetId, out var prepared) ? prepared : new List<DifyRetrieveChunk>();
            return Task.FromResult(r);
        }
    }
}
