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
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты формирования блока auto-recall для системного промпта (чистая логика).
public class NotesRecallTests
{
    private static NoteSemanticHit Hit(string title, double score) =>
        new(Id: "id-" + title, Title: title, Source: "personal", SourceLabel: "Личный",
            Score: score, Snippet: "фрагмент " + title);

    [Fact]
    public void BuildRecallBlock_ФильтруетПоПорогуИTopK()
    {
        // Dify отдаёт хиты уже отсортированными по score — блок берёт top-K в этом порядке
        var hits = new[]
        {
            Hit("A", 0.9), Hit("D", 0.8), Hit("B", 0.5), Hit("C", 0.2),
        };
        var block = NotesKnowledgeService.BuildRecallBlock(hits, minScore: 0.4, topK: 2);

        block.Should().NotBeNull();
        block!.Should().Contain("[[A]]").And.Contain("[[D]]"); // топ-2 из прошедших порог
        block.Should().NotContain("[[B]]");                    // за пределами topK
        block.Should().NotContain("[[C]]");                    // ниже порога
        block.Should().Contain("notes_read");
    }

    [Fact]
    public void BuildRecallBlock_ВсеНижеПорога_Null()
    {
        var hits = new[] { Hit("A", 0.1), Hit("B", 0.2) };
        NotesKnowledgeService.BuildRecallBlock(hits, minScore: 0.35, topK: 4).Should().BeNull();
    }

    [Fact]
    public void BuildRecallBlock_ПустойСписок_Null()
    {
        NotesKnowledgeService.BuildRecallBlock([], minScore: 0.35, topK: 4).Should().BeNull();
    }

    // S2a-4 (разрез И.3, решение 29.10): в узкой рамке общего проекта личный vault в подборку
    // не идёт — гейт реального контрибьютора IsEnabled при Narrow выключен, при Wide включён
    // (шина зовёт BuildAsync только включённых — TurnEventBus.ApplyAsync).
    [Fact]
    public void IsEnabled_УзкаяРамкаВыключен_ШирокаяВключён()
    {
        var config = new ConfigurationBuilder().Build();
        // Гейт читает только поля контекста хода и не трогает сервисы (их поведение —
        // тесты выше и SearchAsync-тесты вертикали).
        var notes = new NotesKnowledgeService(null!, null!, null!, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var contributor = new NotesRecallContributor(notes, config,
            NullLogger<NotesRecallContributor>.Instance);

        var wide = new ClaudeHomeServer.Services.Turn.PromptSessionContext(
            new Session(), "owner-1", null, null, HasNotesMcp: true);
        var narrow = new ClaudeHomeServer.Services.Turn.PromptSessionContext(
            new Session(), "owner-1", null, null, HasNotesMcp: true,
            Frame: ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow);

        contributor.IsEnabled(wide).Should().BeTrue("в личном чате личный vault включён, как раньше");
        contributor.IsEnabled(narrow).Should().BeFalse(
            "в узкой рамке общего проекта личный vault в подборку хода не попадает");
    }
}

// S6g-1, golden секций хода (ADR-035, решение 8): контрибьютор «recall-notes» остаётся
// ОДНОЙ секцией — страницы вики приходят в неё из датасета :wiki после пост-фильтра,
// отдельной секции для вики нет; «_index» в ход не подмешивается (проекции не индексируются);
// если в выдаче есть страница и заметка, которую она цитирует (цепочка: сноска страницы →
// карточка → её ref «note:{id}»), — остаётся страница.
public class NotesRecallTurnSectionTests : IDisposable
{
    private const string User = "u1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FakeProjects _projects = new();
    private readonly NoSpheres _spheres = new();
    private readonly RecordingIndex _index = new();
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;

    public NotesRecallTurnSectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_recall_section_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Notes:AutoRecallMinScore"] = "0.1",
            })
            .Build();

        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var root = Path.Combine(_dir, "projects", "p1");
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        _projects.Items["p1"] = new Project { Id = "p1", OwnerId = User, Name = "Проект p1", RootPath = root };

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(_index, _notes, users, _config,
            NullLogger<NotesKnowledgeService>.Instance, _spheres, _projects);
        new WikiKeeper(_notes, _config).EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task СекцияОдна_СтраницаОстаётсяВместоЦитируемойЗаметки_ИндексНеПодмешивается()
    {
        // id заметки — детерминированный (base64url(sourceKey|relPath), NotesService.EncodeId):
        // карточку можно написать до первого чтения графа — первый GetSummaries случится
        // внутри SyncAllAsync, уже по всем файлам (кэш модели держал бы старый снимок).
        var noteId = Convert.ToBase64String(Encoding.UTF8.GetBytes("p1|Заметка-источник.md"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // 1. Заметка проекта p1 — источник карточки.
        var notePath = Path.Combine(_dir, "projects", "p1", "notes", "Заметка-источник.md");
        File.WriteAllText(notePath, "# Заметка-источник\n\nТочная цитата из источника для проверки.");

        // 2. Карточка источника: ref «note:{id}» — та же форма, что пишет WikiSourceCapture.
        WriteWiki("Проекты/p1/Источники/Карточка.md",
            "---\ntype: source\nsummary: карточка\norigin: note\nref: note:" + noteId +
            "\nhash: h1\ncaptured: 2026-10-30\nstatus: active\n---\n\n# Карточка\n\n> Точная цитата из источника для проверки.\n");

        // 3. Страница вики p1: утверждение со сноской на карточку — «цитирует» заметку через неё.
        WriteWiki("Проекты/p1/Знание.md",
            "---\ntype: topic\nsummary: тест\nstatus: active\n---\n\n# Знание\n\n" +
            "## Сводка\n\n- Сборка — на Linux.[^s1]\n\n## Хронология\n\n## Противоречия\n\n## История\n\n" +
            "[^s1]: [[Карточка]] — «Точная цитата из источника для проверки.»\n");

        // 4. Проекция _index раздела — обязана НЕ попасть ни в индекс, ни в ход.
        WriteWiki("Проекты/p1/_index.md", "# Указатель раздела\n\n- Знание — _index строка.\n");

        // :notes — заметка; :wiki — карточка и страница; _index — ни там ни другого
        // (предикат проекции общий для Scan и SyncAll).
        (await _kb.SyncAllAsync(User)).Should().Be(3);
        _index.Indexed.Should().NotContain(i => i.Title.StartsWith("_index"));

        // Подменённый Dify: в выдаче только страница и цитируемая ею заметка
        // (карточка в выдаче не обязана: цепочка читается с диска).
        var notesDs = DatasetIdOf(":notes");
        var wikiDs = DatasetIdOf(":wiki");
        _index.Response[notesDs] = [ChunkOf("Заметка-источник", 0.85)];
        _index.Response[wikiDs] = [ChunkOf("Знание", 0.9)];

        var session = new Session { Id = "s-turn", OwnerId = User, ProjectId = "p1", Name = "Чат" };
        var ctx = new PromptSessionContext(session, User, null, _dir, HasNotesMcp: true);
        var contribution = await new NotesRecallContributor(_kb, _config, NullLogger<NotesRecallContributor>.Instance)
            .BuildAsync(ctx, "сборка на linux");

        contribution.Should().NotBeNull();
        // Секция recall-notes ровно одна: отдельной секции recall для вики нет (ADR-035, реш. 8).
        contribution!.Sections.Should().HaveCount(1);
        contribution.Sections[0].Key.Should().Be("recall-notes");

        var text = contribution.Sections[0].Text;
        text.Should().Contain("[[Знание]]", "страница вики приходит в ту же единственную секцию");
        // Мутация «убрать исключение цитируемых заметок в NotesRecallContributor» краснит
        // этот assert: без него сюда возвращается и заметка, которую страница цитирует.
        text.Should().NotContain("Заметка-источник",
            "при странице и цитируемой ею заметке остаётся страница");
        text.Should().NotContain("_index", "проекции в ход не подмешиваются");
        contribution.ManifestItems!.Should().NotContain(i => i.Ref == noteId);
    }

    private void WriteWiki(string relUnderPages, string content)
    {
        // EnsureInitialized создаёт только _schema.md: папку раздела делает сам пишущий.
        var full = Path.Combine(WikiPaths.Pages(_dir, User), relUnderPages);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string DocIdOf(string title) => _index.Indexed.First(i => i.Title == title).DocId;

    private string DatasetIdOf(string suffix) =>
        _index.Datasets.First(d => d.Name.EndsWith(suffix, StringComparison.Ordinal)).Id;

    private DifyRetrieveChunk ChunkOf(string title, double score) =>
        new("фрагмент " + title, score, DocIdOf(title), title);

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

    // Подменённый Dify — тот же, что в WikiRecallFilterTests: помнит датасеты и документы;
    // отвечает подготовленным, фильтр запроса игнорирует намеренно.
    private sealed class RecordingIndex : IKnowledgeIndex
    {
        public bool IsConfigured => true;
        public readonly List<(string Name, string Id)> Datasets = new();
        public readonly List<(string Title, string DocId)> Indexed = new();
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
            IReadOnlyList<DifyRetrieveChunk> r =
                Response.TryGetValue(datasetId, out var prepared) ? prepared : new List<DifyRetrieveChunk>();
            return Task.FromResult(r);
        }
    }
}
