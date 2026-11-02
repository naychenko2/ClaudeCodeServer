using System.Text;
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
// (ADR-035, решение 8; ADR-036, решения 2 и 6). Две вещи, которые держит этот файл:
//   — запрос к :wiki и :notes идёт с фильтром набора и top-k ×3, но гарантия —
//     пост-фильтр кода: хит чужого раздела, вернувшийся из подменённого Dify вопреки
//     фильтру, отбрасывается;
//   — в проектном ходе не возвращаются личная заметка и заметка соседнего проекта
//     (решение Андрея 2026-10-21: меняет поведение у всех владельцев с первого выката).
// Метки c1..c4 в заголовках разделов ниже — критерии карточки доработки R-S6g-1
// (её MINOR-пункты), не номера этих двух вещей (W3-7: прежняя шапка называла их теми
// же c1/c2 и смешивала два смысла).
// Dify здесь — подменённый: он намеренно игнорирует фильтр запроса — ровно то состояние,
// ради которого и написан пост-фильтр.
public class WikiRecallFilterTests : IDisposable
{
    private const string User = "u1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
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

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        AddProject("p1");
        AddProject("q"); // тот же владелец, вне сферы p1 (сфер нет) — соседний проект

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance,
            flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(_index, _notes, _users, _config,
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

    // ─── S6g-1: хит чужого раздела вопреки фильтру Dify отбросен пост-фильтром ───

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

    // ─── S6g-1: проектный ход не возвращает личную заметку и заметку соседнего проекта ───

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

    // ─── c1 (доработка R-S6g-1): поведение при включённых сферах, как в ADR-036 §2 ───

    // Решение Софьи по вилке вердикта R-S6g-1: ADR-036 §2 (принят Андреем 2026-10-27) даёт
    // проекту p1 сферы s1 набор projects/p1, projects/q (все q из сферы), spheres/s1, common —
    // соседи той же сферы ВИДНЫ. Здесь поведение фиксируется тестом как есть; правки recall
    // для соседей нет. Прежние тесты строили только NoSpheres — конфигурация «Q в сфере P»
    // не была покрыта ни одним тестом (MAJOR ревью).
    [Fact]
    public async Task СферныйХод_ВидитСоседейПоСфереИРазделСферы_ПроектыВнеСферыИЛичноеОтброшены()
    {
        var spheres = new SpheresOn(); // s1: p1 и q; проект r вне сфер
        var kb = new NotesKnowledgeService(_index, _notes, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance, spheres, _projects);
        AddProject("r");

        // Все заметки и страницы — одной темы, чтобы подменённый Dify вернул их в одной выдаче.
        WriteFile(Path.Combine(_dir, "notes", User, "Личная.md"), "# Личная\n\nСборка на linux лично.");
        WriteFile(Path.Combine(_dir, "projects", "r", "notes", "Чужая.md"), "# Чужая\n\nСборка на linux в проекте r.");
        WriteFile(Path.Combine(_dir, "projects", "q", "notes", "Соседняя.md"), "# Соседняя\n\nСборка на linux в проекте q.");
        WriteFile(Path.Combine(_dir, "projects", "p1", "notes", "Своя.md"), "# Своя\n\nСборка на linux в проекте p1.");
        InitWiki();
        WriteWiki("Сферы/s1/Сферное.md", Page("Сферное"));   // раздел сферы — в наборе
        WriteWiki("Проекты/p1/Знание.md", Page("Знание"));
        WriteWiki("Общее/Общее.md", Page("Общее"));          // common — в наборе

        (await kb.SyncAllAsync(User)).Should().Be(7); // 4 заметки + 3 страницы

        var notesDs = DatasetIdOf(":notes");
        var wikiDs = DatasetIdOf(":wiki");
        _index.Response[notesDs] = [
            ChunkWithDoc("Соседняя", 0.9), ChunkWithDoc("Своя", 0.85),
            ChunkWithDoc("Чужая", 0.7), ChunkWithDoc("Личная", 0.6),
        ];
        _index.Response[wikiDs] = [
            ChunkWithDoc("Сферное", 0.95), ChunkWithDoc("Знание", 0.8), ChunkWithDoc("Общее", 0.75),
        ];

        var hits = await kb.SearchAsync(User, "сборка на linux", 8,
            new WikiVisibility.CallContext("p1", null));

        // Выживают: своё, сосед по сфере, раздел сферы, common (по убыванию score).
        // Отброшены: проект r вне сферы и личное (личного в проектном ходе нет — правило таблицы).
        // Мутация «набор без проектов сферы» (убрать соседний цикл в WikiVisibility.SectionsFor)
        // краснит этот assert на «Соседняя»; мутация «набор включает R» (добавить все проекты
        // владельца) краснит его на «Чужая».
        hits.Select(h => h.Title).Should().Equal("Сферное", "Соседняя", "Своя", "Знание", "Общее");
        hits.Should().NotContain(h => h.Source == "r");
        hits.Should().NotContain(h => h.Source == "personal");
    }

    // ─── c2 (MINOR 1, R-S6g-1): хит с неопределяемым домом отбрасывается, а не становится common ───

    [Fact]
    public async Task ХитСНеопределяемымДомом_ОтброшенАНеПризнанCommon()
    {
        // Достижимый вариант null-дома в SectionKeyOf — невалидный id источника: заметка
        // проекта, чей id не является id раздела (WikiSections.IsValidId). Два других
        // сценария вердикта отсечены раньше и до пост-фильтра не доходят: :notes-хит без
        // summary не рождается вовсе (MapHits), wiki-страница с неразобранным путём не
        // индексируется (SyncAll) — они проверены здесь как поведение, но мутация
        // «?? common» до них не дотягивает и краснеет именно на этом, достижимом.
        AddProject("q 1"); // пробел в id — не id раздела

        WriteFile(Path.Combine(_dir, "projects", "p1", "notes", "Своя.md"), "# Своя\n\nСборка на linux в проекте p1.");
        WriteFile(Path.Combine(_dir, "projects", "q 1", "notes", "Чужая.md"), "# Чужая\n\nСборка на linux в проекте q 1.");
        WriteFile(Path.Combine(_dir, "projects", "p1", "notes", "Удалённая.md"), "# Удалённая\n\nСборка на linux, затем удалена.");
        InitWiki();
        WriteWiki("Служебное/Неразобранный.md", Page("Неразобранный")); // путь ни в один раздел
        WriteWiki("Проекты/p1/Знание.md", Page("Знание"));
        (await _kb.SyncAllAsync(User)).Should().Be(4); // Своя, Чужая, Удалённая, Знание; «Неразобранный» — нет
        _index.Indexed.Should().NotContain(i => i.Title == "Неразобранный");

        // Состояние живого индекса: заметку удалили с диска, документ в Dify ещё жив
        // (реконсик асинхронен) — старый docId остаётся в маппинге.
        File.Delete(Path.Combine(_dir, "projects", "p1", "notes", "Удалённая.md"));
        _notes.Invalidate(User);

        var notesDs = DatasetIdOf(":notes");
        var wikiDs = DatasetIdOf(":wiki");
        _index.Response[notesDs] = [
            ChunkWithDoc("Чужая", 0.9),        // дом не определяется → отброшен (не common)
            ChunkWithDoc("Удалённая", 0.85),   // summary нет → хит не рождается
            ChunkWithDoc("Своя", 0.8),
        ];
        _index.Response[wikiDs] = [ChunkWithDoc("Знание", 0.7)];

        var hits = await ScopedSearch("p1");

        // Мутация «h => SectionKeyOf(h, summaries) ?? WikiSections.Common» краснит этот
        // assert: «Чужая» стала бы «common», а common — в наборе проектного хода.
        hits.Select(h => h.Title).Should().Equal("Своя", "Знание");
    }

    // ─── c3 (MINOR 2, R-S6g-1): запрос к :notes идёт с запасом ×3, как :wiki ───

    [Fact]
    public async Task ЗапасTopKДляNotes_ТопKИзОтбрасываемыхДомовНеДаётПустогоВыдачи()
    {
        // Проект q: 10 заметок выше по выдаче, в p1 — одна ниже. Топ-8 без запаса состоял бы
        // целиком из отбрасываемых домов и пост-фильтр дал бы пустую выдачу, хотя релевантная
        // заметка P есть ниже (MINOR 2, R-S6g-1; усечение до topK — после пост-фильтра).
        for (var i = 1; i <= 10; i++)
            WriteFile(Path.Combine(_dir, "projects", "q", "notes", $"Чужая{i:00}.md"),
                $"# Чужая{i:00}\n\nСборка на linux в проекте q.");
        WriteFile(Path.Combine(_dir, "projects", "p1", "notes", "Своя.md"), "# Своя\n\nСборка на linux в проекте p1.");
        (await _kb.SyncAllAsync(User)).Should().Be(11);

        var notesDs = DatasetIdOf(":notes");
        _index.Response[notesDs] = Enumerable.Range(1, 10)
            .Select(i => ChunkWithDoc($"Чужая{i:00}", 0.9 - i * 0.01))
            .Append(ChunkWithDoc("Своя", 0.3))
            .ToList();

        var hits = await ScopedSearch("p1");

        // Без запаса (мутация «topK*3 → topK») запрос взял бы 8 чанков — все чужие, выдача
        // пустая, и этот assert краснеет.
        var notesReq = _index.Requests.Last(r => r.DatasetId == notesDs);
        notesReq.TopK.Should().Be(24, "запрос к :notes берёт top-k с тем же запасом ×3, что и :wiki");
        hits.Select(h => h.Title).Should().Equal("Своя");
    }

    // ─── c4 (MINOR 5, R-S6g-1): цитируемая карточка — того же раздела, что и страница ───

    [Fact]
    public void CitedNoteIds_БерётКарточкуТогоЖеРаздела_ЧтоСтраница()
    {
        // Две одноимённые карточки в разных разделах: «Карточка» p1 (ref — заметка p1) и
        // «Карточка» q (ref — заметка q). Карточка q новее: GetSummaries сортирует по
        // UpdatedAt desc, и прежний поиск по заголовку среди всех разделов взял бы её —
        // этот assert на прежнем коде краснеет (вернулся бы id заметки q).
        var p1NoteId = NoteIdOf("p1", "Заметка-p1.md");
        var qNoteId = NoteIdOf("q", "Заметка-q.md");

        WriteFile(Path.Combine(_dir, "projects", "p1", "notes", "Заметка-p1.md"), "# Заметка-p1\n\nЦитата из заметки p1.");
        WriteFile(Path.Combine(_dir, "projects", "q", "notes", "Заметка-q.md"), "# Заметка-q\n\nЦитата из заметки q.");

        InitWiki();
        WriteWiki("Проекты/p1/Источники/Карточка.md", Card(p1NoteId));
        WriteWiki("Проекты/q/Источники/Карточка.md", Card(qNoteId));
        var cardQ = Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", "q", "Источники", "Карточка.md");
        File.SetLastWriteTimeUtc(cardQ, DateTime.UtcNow.AddMinutes(1)); // q-карточка — первая в сортировке

        WriteWiki("Проекты/p1/Знание.md",
            "---\ntype: topic\nsummary: тест\nstatus: active\n---\n\n# Знание\n\n" +
            "## Сводка\n\n- Сборка — на Linux.[^s1]\n\n## Хронология\n\n## Противоречия\n\n## История\n\n" +
            "[^s1]: [[Карточка]] — «Цитата из заметки p1.»\n");

        var cited = _kb.CitedNoteIds(User, new NoteSemanticHit(
            WikiIdOf("Проекты/p1/Знание.md"), "Знание", "wiki", "Вики", 0.9, ""));

        cited.Should().Equal(p1NoteId);
    }

    // ─── обвязка Dify и горизонталей ───

    private DifyRetrieveChunk ChunkWithDoc(string title, double score) =>
        new("фрагмент " + title, score, DocIdOf(title), title);

    private static string Page(string title) =>
        "---\ntype: topic\nsummary: тест\nstatus: active\n---\n\n# " + title +
        "\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n";

    // Карточка источника с ref на заметку — та же форма, что пишет WikiSourceCapture.
    private static string Card(string refNoteId) =>
        "---\ntype: source\nsummary: карточка\norigin: note\nref: note:" + refNoteId +
        "\nhash: h1\ncaptured: 2026-10-30\nstatus: active\n---\n\n# Карточка\n\n> цитата из карточки.\n";

    // Id заметки/страницы детерминированный (base64url(sourceKey|relPath), NotesService.EncodeId):
    // его можно построить до первого чтения графа.
    private static string NoteIdOf(string sourceKey, string relPath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceKey + "|" + relPath))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string WikiIdOf(string relUnderPages) => NoteIdOf("wiki", relUnderPages);

    private sealed class NoSpheres : ISphereDirectory
    {
        public bool Enabled(string ownerId) => false;
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    // Сферы включены: сфера s1 с проектами p1 и q (тот же владелец); проект r вне сфер.
    // Форма справочника — как FakeDir в WikiVisibilityTests.
    private sealed class SpheresOn : ISphereDirectory
    {
        public readonly Dictionary<string, List<string>> Spheres = new() { ["s1"] = ["p1", "q"] };

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
            // Настоящий Dify не отдаёт больше чанков, чем запрошено: без этого усечения
            // запас ×3 в коде нельзя проверить мутацией (подменённый Dify вернул бы весь
            // список при любом top-k).
            return Task.FromResult<IReadOnlyList<DifyRetrieveChunk>>(r.Take(topK).ToList());
        }
    }
}
