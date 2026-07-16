using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Синк «файл проекта ↔ документ Dify»: идемпотентная индексация, дифф по хешам
// (правка/удаление), миграция при переносе, bootstrap карты для старых датасетов.
// Dify мокается фейковым HttpMessageHandler, записывающим вызовы.
public class ProjectKnowledgeSyncServiceTests : IDisposable
{
    // Фейковый Dify: create dataset → ds-1, create_by_text → doc-N, GET documents →
    // настраиваемый список, DELETE → 204. Все вызовы записываются.
    private sealed class FakeDifyHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Calls = new();
        public string DocumentsJson = "[]";       // содержимое data для GET documents
        public string DatasetsJson = "[]";        // содержимое data для GET datasets
        private int _docSeq;

        public List<(string Method, string Path, string Body)> CallsOf(string method, string pathPart) =>
            Calls.Where(c => c.Method == method && c.Path.Contains(pathPart)).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, path, body));

            if (request.Method == HttpMethod.Post && path == "/v1/datasets")
                return Json("{\"id\":\"ds-1\"}");
            if (request.Method == HttpMethod.Get && path == "/v1/datasets")
                return Json($"{{\"data\":{DatasetsJson},\"has_more\":false,\"total\":0}}");
            if (request.Method == HttpMethod.Post && path.Contains("/document/create_by_text"))
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString();
                return Json($"{{\"document\":{{\"id\":\"doc-{++_docSeq}\",\"name\":{JsonSerializer.Serialize(name)},\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/documents"))
                return Json($"{{\"data\":{DocumentsJson},\"has_more\":false,\"total\":0}}");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private readonly string _tempDir;
    private readonly string _projectDir;
    private readonly FakeDifyHandler _dify = new();
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly KnowledgeService _knowledge;
    private readonly FileService _files = new();
    private readonly ProjectKnowledgeSyncService _sut;
    private readonly Project _project;
    private readonly string _ownerId;
    private readonly IConfiguration _config;
    private readonly UserStore _users;

    public ProjectKnowledgeSyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pksync_tests_" + Guid.NewGuid().ToString("N"));
        _projectDir = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(_projectDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        _users = new UserStore(_config, NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        var appSettings = new AppSettingsService(_config);
        _projects = new ProjectManager(_config, _users, appSettings);
        _personas = new PersonaManager(_config);
        _wkStore = new WorkspaceKnowledgeStore(_config);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));
        _knowledge = new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "key" }),
            _wkStore);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _sut = new ProjectKnowledgeSyncService(_knowledge, _wkStore, _projects, _files, hub.Object,
            NullLogger<ProjectKnowledgeSyncService>.Instance);

        _project = _projects.Create("proj", _projectDir, _ownerId, "tester");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    private void WriteProjectFile(string rel, string content)
    {
        var full = Path.Combine(_projectDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private WorkspaceKnowledge Wk() => _wkStore.GetByPath(_projectDir)!;

    [Fact]
    public async Task IndexPathAsync_Индексирует_И_Отслеживает()
    {
        WriteProjectFile("docs/a.md", "привет");

        var (datasetId, doc) = await _sut.IndexPathAsync(_project, "tester", "docs/a.md");

        datasetId.Should().Be("ds-1");
        doc.Name.Should().Be("docs/a.md");
        Wk().Docs.Should().ContainKey("docs/a.md");
        Wk().Docs!["docs/a.md"].DocId.Should().Be(doc.Id);
        Wk().Docs!["docs/a.md"].Hash.Should().NotBeEmpty();
    }

    [Fact]
    public async Task IndexPathAsync_Повторно_Обновляет_Без_Дублей()
    {
        WriteProjectFile("a.md", "версия 1");
        var (_, first) = await _sut.IndexPathAsync(_project, "tester", "a.md");

        WriteProjectFile("a.md", "версия 2");
        var (_, second) = await _sut.IndexPathAsync(_project, "tester", "a.md");

        second.Id.Should().NotBe(first.Id);
        Wk().Docs.Should().HaveCount(1);
        Wk().Docs!["a.md"].DocId.Should().Be(second.Id);
        // Старый документ удалён — дубль не плодится
        _dify.CallsOf("DELETE", $"/documents/{first.Id}").Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncAsync_Изменение_Файла_Переиндексирует()
    {
        WriteProjectFile("a.md", "старое");
        var (_, first) = await _sut.IndexPathAsync(_project, "tester", "a.md");
        var oldHash = Wk().Docs!["a.md"].Hash;

        WriteProjectFile("a.md", "новое содержимое");
        var changed = await _sut.SyncAsync(_projectDir);

        changed.Should().Be(1);
        Wk().Docs!["a.md"].Hash.Should().NotBe(oldHash);
        Wk().Docs!["a.md"].DocId.Should().NotBe(first.Id);
        _dify.CallsOf("DELETE", $"/documents/{first.Id}").Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncAsync_Без_Изменений_Ничего_Не_Делает()
    {
        WriteProjectFile("a.md", "текст");
        await _sut.IndexPathAsync(_project, "tester", "a.md");
        var callsBefore = _dify.Calls.Count;

        var changed = await _sut.SyncAsync(_projectDir);

        changed.Should().Be(0);
        _dify.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task SyncAsync_Удаление_Файла_Удаляет_Документ()
    {
        WriteProjectFile("a.md", "текст");
        var (_, doc) = await _sut.IndexPathAsync(_project, "tester", "a.md");

        File.Delete(Path.Combine(_projectDir, "a.md"));
        var changed = await _sut.SyncAsync(_projectDir);

        changed.Should().Be(1);
        Wk().Docs.Should().BeEmpty();
        _dify.CallsOf("DELETE", $"/documents/{doc.Id}").Should().HaveCount(1);
    }

    [Fact]
    public async Task Rename_Через_FileService_Мигрирует_Документ()
    {
        WriteProjectFile("docs/a.md", "текст");
        var (_, oldDoc) = await _sut.IndexPathAsync(_project, "tester", "docs/a.md");

        // files.Rename поднимает OnMutated → HandleRename мигрирует ключ с Hash=""
        _files.Rename(_projectDir, "docs/a.md", "docs/b.md");
        var changed = await _sut.SyncAsync(_projectDir);

        changed.Should().Be(1);
        Wk().Docs.Should().ContainKey("docs/b.md").And.NotContainKey("docs/a.md");
        Wk().Docs!["docs/b.md"].DocId.Should().NotBe(oldDoc.Id);
        // Документ пересоздан под новым именем, старый удалён
        _dify.CallsOf("DELETE", $"/documents/{oldDoc.Id}").Should().HaveCount(1);
        _dify.CallsOf("POST", "/document/create_by_text").Last().Body.Should().Contain("docs/b.md");
    }

    [Fact]
    public async Task Rename_Папки_Мигрирует_Всё_Поддерево()
    {
        WriteProjectFile("docs/a.md", "а");
        WriteProjectFile("docs/inner/b.md", "б");
        await _sut.IndexPathAsync(_project, "tester", "docs/a.md");
        await _sut.IndexPathAsync(_project, "tester", "docs/inner/b.md");

        _files.Rename(_projectDir, "docs", "wiki");
        await _sut.SyncAsync(_projectDir);

        Wk().Docs!.Keys.Should().BeEquivalentTo(new[] { "wiki/a.md", "wiki/inner/b.md" });
    }

    [Fact]
    public async Task SyncAsync_Детектит_Перенос_По_Хешу_Среди_Хинтов()
    {
        WriteProjectFile("a.md", "уникальное содержимое");
        var (_, oldDoc) = await _sut.IndexPathAsync(_project, "tester", "a.md");

        // Перенос мимо файлового API (как это делает агент)
        File.Move(Path.Combine(_projectDir, "a.md"), Path.Combine(_projectDir, "moved.md"));
        var changed = await _sut.SyncAsync(_projectDir, ["moved.md"]);

        changed.Should().Be(1);
        Wk().Docs.Should().ContainKey("moved.md").And.NotContainKey("a.md");
        _dify.CallsOf("DELETE", $"/documents/{oldDoc.Id}").Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncAsync_Бутстрапит_Карту_И_Схлопывает_Дубли()
    {
        // Датасет создан до фичи: Docs=null, в Dify три документа (один — дубль имени,
        // один — от давно удалённого файла)
        WriteProjectFile("docs/a.md", "живой файл");
        var wk = _wkStore.GetOrCreate(_projectDir);
        wk.DifyDatasetId = "ds-1";
        _wkStore.Save(wk);
        _dify.DocumentsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "d1", name = "docs/a.md", indexing_status = "completed" },
            new { id = "d2", name = "docs/a.md", indexing_status = "completed" },
            new { id = "d3", name = "gone.md", indexing_status = "completed" },
        });

        await _sut.SyncAsync(_projectDir);

        Wk().Docs!.Keys.Should().BeEquivalentTo(new[] { "docs/a.md" });
        // Дубль удалён, документ пропавшего файла удалён, живой пересоздан (хеш был сброшен)
        _dify.CallsOf("DELETE", "/documents/d2").Should().HaveCount(1);
        _dify.CallsOf("DELETE", "/documents/d3").Should().HaveCount(1);
        _dify.CallsOf("POST", "/document/create_by_text").Should().HaveCount(1);
    }

    [Fact]
    public async Task ForgetDocument_Снимает_С_Отслеживания()
    {
        WriteProjectFile("a.md", "текст");
        var (_, doc) = await _sut.IndexPathAsync(_project, "tester", "a.md");

        _sut.ForgetDocument(_projectDir, doc.Id);
        var changed = await _sut.SyncAsync(_projectDir);

        Wk().Docs.Should().BeEmpty();
        changed.Should().Be(0);   // живой файл не переиндексируется после ручного удаления документа
    }

    [Fact]
    public async Task UserKnowledgeCascade_Чистит_Датасеты_По_Префиксу_И_Персоны()
    {
        var persona = _personas.Create(_ownerId, "Ада", "Аналитик", null, null,
            null, null, PersonaScope.Global, null, null, null, memoryEnabled: true);
        var personaMemory = new PersonaMemoryService(_knowledge, _personas, _users, _config,
            NullLogger<PersonaMemoryService>.Instance);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(_knowledge, notesSvc, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var teamMemory = new TeamMemoryService(_config);
        var cascade = new UserKnowledgeCascade(_knowledge, _wkStore, _projects, _personas,
            personaMemory, teamMemory, notesKb, NullLogger<UserKnowledgeCascade>.Instance);

        // Запись знаний проекта владельца — каскад должен её снять
        var wk = _wkStore.GetOrCreate(_projectDir);
        wk.DifyDatasetId = "ds-1";
        _wkStore.Save(wk);

        _dify.DatasetsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "n1", name = "tester:notes" },
            new { id = "k1", name = "tester:kb:Доки" },
            new { id = "o1", name = "other:kb:Чужая" },
            new { id = "p1", name = "Публичная" },
        });

        await cascade.CleanupAsync(_ownerId, "tester");

        _personas.GetByOwner(_ownerId).Should().BeEmpty();
        _dify.CallsOf("DELETE", "/datasets/n1").Should().HaveCount(1);
        _dify.CallsOf("DELETE", "/datasets/k1").Should().HaveCount(1);
        _dify.CallsOf("DELETE", "/datasets/o1").Should().BeEmpty();
        _dify.CallsOf("DELETE", "/datasets/p1").Should().BeEmpty();
        // Запись знаний проекта владельца снята
        _wkStore.GetByPath(_projectDir).Should().BeNull();
    }
}

// Миграция записи WorkspaceKnowledge при смене RootPath проекта
public class WorkspaceKnowledgeStoreMoveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceKnowledgeStore _store;

    public WorkspaceKnowledgeStoreMoveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wkmove_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _store = new WorkspaceKnowledgeStore(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Move_Переносит_Запись_Под_Новый_Ключ()
    {
        var oldRoot = Path.Combine(_tempDir, "old");
        var newRoot = Path.Combine(_tempDir, "new");
        var wk = _store.GetOrCreate(oldRoot);
        wk.DifyDatasetId = "ds-1";
        wk.Docs = new Dictionary<string, WorkspaceDocRef> { ["a.md"] = new() { DocId = "d1", Hash = "h" } };
        _store.Save(wk);

        _store.Move(oldRoot, newRoot).Should().BeTrue();

        _store.GetByPath(oldRoot).Should().BeNull();
        var moved = _store.GetByPath(newRoot)!;
        moved.DifyDatasetId.Should().Be("ds-1");
        moved.Docs.Should().ContainKey("a.md");
        moved.RootPath.Should().Be(newRoot);
    }

    [Fact]
    public void Move_Не_Затирает_Существующий_Датасет_Под_Новым_Ключом()
    {
        var oldRoot = Path.Combine(_tempDir, "old");
        var newRoot = Path.Combine(_tempDir, "new");
        var wkOld = _store.GetOrCreate(oldRoot);
        wkOld.DifyDatasetId = "ds-old";
        _store.Save(wkOld);
        var wkNew = _store.GetOrCreate(newRoot);
        wkNew.DifyDatasetId = "ds-new";
        _store.Save(wkNew);

        _store.Move(oldRoot, newRoot).Should().BeFalse();

        _store.GetByPath(newRoot)!.DifyDatasetId.Should().Be("ds-new");
        _store.GetByPath(oldRoot)!.DifyDatasetId.Should().Be("ds-old");
    }
}
