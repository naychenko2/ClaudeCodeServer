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
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// R6 (ADR-039, реш. 7): путь recall учитывает чтения страниц вики, попавших в блок (вид Recall),
// и не пускает в блок спящие страницы (правило архива R5). Сбой архива или стора учёта блок не
// отменяет. Обвязка — как у NotesRecallTurnSectionTests: реальные NotesService/вики + подменённый Dify.
public class NotesRecallReadsTests : IDisposable
{
    private const string User = "u1";
    private const string KeySleeping = "Проекты/p1/Спящая.md";
    private const string KeyAlive = "Проекты/p1/Живая.md";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FakeProjects _projects = new();
    private readonly NoSpheres _spheres = new();
    private readonly RecordingIndex _index = new();
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;

    public NotesRecallReadsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_recall_reads_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = BaseCfg();

        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var root = Path.Combine(_dir, "projects", "p1");
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        _projects.Items["p1"] = new Project { Id = "p1", OwnerId = User, Name = "Проект p1", RootPath = root };
        // Гейт HasIndex контрибьютора требует проиндексированной обычной заметки (:notes-доки
        // реестра) — без неё путь вики в ход не доходит вовсе. Служебная заметка держит гейт
        // открытым; в выдачу она не попадает: для :notes-датасета ответ не подменён.
        File.WriteAllText(Path.Combine(root, "notes", "Служебная.md"),
            "# Служебная\n\nЗаметка для открытия индекса: гейт HasIndex считает :notes-доки.");

        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, flags: new WikiFlagOn());
        _kb = new NotesKnowledgeService(_index, _notes, users, _config,
            NullLogger<NotesKnowledgeService>.Instance, _spheres, _projects);
        new WikiKeeper(_notes, _config).EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Вики-хит в блоке → учтён ровно один раз и именно как Recall: слабый путь не двигает
    // сильное время страницы (LastStrongRead остаётся null).
    [Fact]
    public async Task ВикиХитВБлоке_УчтёнКакRecall()
    {
        WritePage("Проекты/p1/Живая.md");
        (await _kb.SyncAllAsync(User)).Should().Be(2); // служебная заметка (:notes) + страница (:wiki)
        _index.Response[DatasetIdOf(":wiki")] = [ChunkOf("Живая", 0.9)];

        var contribution = await New().BuildAsync(Ctx(), "живая страница");

        contribution.Should().NotBeNull();
        contribution!.Sections.Should().HaveCount(1);
        contribution.Sections[0].Text.Should().Contain("[[Живая]]");

        var snap = WikiReadsStore.Read(WikiPaths.Root(_dir, User));
        snap.Valid.Should().BeTrue();
        snap.Pages.Should().ContainKey(KeyAlive);
        snap.Pages[KeyAlive].Recall.Should().Be(1);
        snap.Pages[KeyAlive].LastRecall.Should().NotBeNull();
        snap.Pages[KeyAlive].LastStrongRead.Should().BeNull(
            "recall — слабый путь: он не двигает время сильных чтений");
    }

    // Хит, отрезанный topK, в блок не попал — записи о нём нет: учитывается чтение блока,
    // а не поиск.
    [Fact]
    public async Task ОтрезанныйTopK_НеУчтён()
    {
        WritePage("Проекты/p1/Первая.md");
        WritePage("Проекты/p1/Вторая.md");
        WritePage("Проекты/p1/Третья.md");
        await _kb.SyncAllAsync(User);
        _index.Response[DatasetIdOf(":wiki")] =
            [ChunkOf("Первая", 0.9), ChunkOf("Вторая", 0.8), ChunkOf("Третья", 0.7)];

        var contribution = await New(Cfg(("Notes:AutoRecallTopK", "2"))).BuildAsync(Ctx(), "страницы");

        contribution.Should().NotBeNull();
        var text = contribution!.Sections[0].Text;
        text.Should().Contain("[[Первая]]").And.Contain("[[Вторая]]");
        text.Should().NotContain("[[Третья]]");

        var snap = WikiReadsStore.Read(WikiPaths.Root(_dir, User));
        snap.Valid.Should().BeTrue();
        snap.Pages.Keys.Should().BeEquivalentTo(["Проекты/p1/Первая.md", "Проекты/p1/Вторая.md"],
            "учёт идёт по финальному блоку: отрезанный хит не прочтён");
    }

    // Спящая страница (R5: сигнала чтения нет дольше Window) выпадает из блока, освободившийся
    // слот занимает следующий хит; выпавший хит не учитывается.
    [Fact]
    public async Task СпящаяСтраницаВнеБлока_СлотЗанятСледующимХитом()
    {
        var sleeping = WritePage("Проекты/p1/Спящая.md");
        // Возраст страницы — по mtime (система «updated» в шапку не пишет, R5): 60 дней назад.
        File.SetLastWriteTimeUtc(sleeping, DateTimeOffset.UtcNow.AddDays(-60).UtcDateTime);
        WritePage("Проекты/p1/Живая.md");
        await _kb.SyncAllAsync(User);

        // Начало учёта 60 дней назад и то же время последнего чтения спящей: сигнал давно —
        // страница спит; живая моложе Window — не архивируется.
        var past = DateTimeOffset.UtcNow.AddDays(-60);
        WikiReadsStore.RecordMany(WikiPaths.Root(_dir, User), [(KeySleeping, WikiReadKind.Read)], past)
            .Should().BeTrue();

        _index.Response[DatasetIdOf(":wiki")] = [ChunkOf("Спящая", 0.9), ChunkOf("Живая", 0.8)];

        var contribution = await New(Cfg(("Notes:AutoRecallTopK", "1"))).BuildAsync(Ctx(), "сон и архив");

        contribution.Should().NotBeNull();
        var text = contribution!.Sections[0].Text;
        text.Should().NotContain("[[Спящая]]", "спящая страница в блок не попадает");
        text.Should().Contain("[[Живая]]", "освободившийся слот добирается следующим хитом");
        contribution.ManifestItems!.Should().NotContain(i => i.Title == "Спящая",
            "манифест — то, что попало в блок");

        var snap = WikiReadsStore.Read(WikiPaths.Root(_dir, User));
        snap.Pages[KeySleeping].Recall.Should().Be(0, "выпавший из блока хит не учитывается");
        snap.Pages[KeySleeping].LastRead.Should().Be(past, "учёт recall не отматывает и не двигает чужую запись");
        snap.Pages[KeyAlive].Recall.Should().Be(1);
    }

    // Поиск, пришедший после таймаута, не учитывается: return null — до учёта.
    [Fact]
    public async Task ТаймаутПоиска_УчётаНет()
    {
        WritePage("Проекты/p1/Живая.md");
        await _kb.SyncAllAsync(User);
        _index.Never = true; // поиск не отвечает вовсе

        var contribution = await New(Cfg(("Notes:AutoRecallTimeoutMs", "100"))).BuildAsync(Ctx(), "живая");

        contribution.Should().BeNull();
        File.Exists(Path.Combine(WikiPaths.Root(_dir, User), "keeper", "reads.json")).Should().BeFalse(
            "устаревший ответ не учитывается: return null идёт до учёта");
    }

    // Битый reads.json: судить не по чему — ничего не архивируется (fail-closed R1), блок тот же,
    // что без архива; запись в нечитаемый файл невозможна — файл остаётся как был.
    [Fact]
    public async Task БитыйСторУчёта_БлокКакБезАрхива()
    {
        var sleeping = WritePage("Проекты/p1/Спящая.md");
        File.SetLastWriteTimeUtc(sleeping, DateTimeOffset.UtcNow.AddDays(-60).UtcDateTime);
        await _kb.SyncAllAsync(User);

        var readsPath = Path.Combine(WikiPaths.Root(_dir, User), "keeper", "reads.json");
        Directory.CreateDirectory(Path.GetDirectoryName(readsPath)!);
        const string broken = "{ это не json";
        File.WriteAllText(readsPath, broken);

        _index.Response[DatasetIdOf(":wiki")] = [ChunkOf("Спящая", 0.9)];

        var contribution = await New(Cfg(("Notes:AutoRecallTopK", "1"))).BuildAsync(Ctx(), "сон");

        contribution.Should().NotBeNull(
            "сбой архива не отменяет блок: ход получает recall как без него");
        contribution!.Sections[0].Text.Should().Contain("[[Спящая]]",
            "на нечитаемом сторе ничего не архивируется — хит остаётся, как без архива");
        File.ReadAllText(readsPath).Should().Be(broken,
            "запись в невалидный файл отменена: чужие счётчики были бы стёрты");
    }

    // Два одновременных хода с одной страницей: семафор файла (R1) сериализует
    // read-modify-write — ни одна запись не теряется.
    [Fact]
    public async Task ДваОдновременныхВызова_ОбаУчтены()
    {
        WritePage("Проекты/p1/Живая.md");
        await _kb.SyncAllAsync(User);
        _index.Response[DatasetIdOf(":wiki")] = [ChunkOf("Живая", 0.9)];
        _index.DelayMs = 50; // оба поиска идут одновременно: запись идёт в гонке

        var contributor = New();
        var ctx = Ctx();
        var a = contributor.BuildAsync(ctx, "живая");
        var b = contributor.BuildAsync(ctx, "живая");
        var both = await Task.WhenAll(a, b);
        both.Should().NotContain(c => c == null);

        var snap = WikiReadsStore.Read(WikiPaths.Root(_dir, User));
        snap.Pages[KeyAlive].Recall.Should().Be(2,
            "read-modify-write под семафором файла: одновременные ходы не теряют записей друг друга");
    }

    // --- Обвязка ---

    private IConfiguration BaseCfg() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["Notes:AutoRecallMinScore"] = "0.1",
        })
        .Build();

    // Конфиг хода: база + переопределения (TopK/Timeout читаются на каждый ход).
    private IConfiguration Cfg(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["Notes:AutoRecallMinScore"] = "0.1",
        };
        foreach (var (k, v) in overrides) values[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private NotesRecallContributor New(IConfiguration? cfg = null) =>
        new(_kb, cfg ?? _config, NullLogger<NotesRecallContributor>.Instance);

    private PromptSessionContext Ctx()
    {
        var session = new Session { Id = "s-reads", OwnerId = User, ProjectId = "p1", Name = "Чат" };
        return new PromptSessionContext(session, User, null, _dir, HasNotesMcp: true);
    }

    // Типовая страница раздела p1; отдаёт полный путь — тесту нужно состарить файл по mtime.
    private string WritePage(string relUnderPages)
    {
        var title = Path.GetFileNameWithoutExtension(relUnderPages);
        var full = Path.Combine(WikiPaths.Pages(_dir, User), relUnderPages);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full,
            "---\ntype: topic\nsummary: тест\nstatus: active\n---\n\n# " + title + "\n\n" +
            "## Сводка\n\n- Строка страницы для теста учёта чтений.\n\n" +
            "## Хронология\n\n## Противоречия\n\n## История\n");
        return full;
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

    // Подменённый Dify — тот же, что в образце: помнит датасеты и документы, фильтр запроса
    // игнорирует намеренно. Never — ответ не приходит вовсе (тест таймаута); DelayMs —
    // одновременные вызовы идут в гонке (тест конкурентности).
    private sealed class RecordingIndex : IKnowledgeIndex
    {
        public bool IsConfigured => true;
        public readonly List<(string Name, string Id)> Datasets = new();
        public readonly List<(string Title, string DocId)> Indexed = new();
        public readonly Dictionary<string, List<DifyRetrieveChunk>> Response = new();
        public bool Never;
        public int DelayMs;

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

        public async Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query,
            int topK = 8, IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and")
        {
            if (Never) return await new TaskCompletionSource<IReadOnlyList<DifyRetrieveChunk>>().Task;
            if (DelayMs > 0) await Task.Delay(DelayMs);
            return Response.TryGetValue(datasetId, out var prepared)
                ? prepared
                : new List<DifyRetrieveChunk>();
        }
    }
}
