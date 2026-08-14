using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Knowledge;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Контракт участника реконсайлера (шаг 2): у каждого из пяти владельцев синка
// ResolveAsync сопоставляет DocId с ключом записи и НЕ мутирует стор, InvalidateAsync
// сбрасывает хеш так, что штатный дифф-синк удаляет старый документ и пересоздаёт
// новый, неизвестный DocId (сирота) даёт пустой список.
public class KnowledgeSyncParticipantTests : IDisposable
{
    // Фейковый Dify: create dataset → ds-1, create_by_text → doc-N, DELETE → 204
    private sealed class FakeDifyHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path)> Calls = new();
        private int _docSeq;

        public List<(string Method, string Path)> CallsOf(string method, string pathPart) =>
            Calls.Where(c => c.Method == method && c.Path.Contains(pathPart)).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, path));

            if (request.Method == HttpMethod.Post && path == "/v1/datasets")
                return Json("{\"id\":\"ds-1\"}");
            if (request.Method == HttpMethod.Post && path.Contains("/document/create_by_text"))
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString();
                return Json($"{{\"document\":{{\"id\":\"doc-{++_docSeq}\",\"name\":{JsonSerializer.Serialize(name)},\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/documents"))
                return Json("{\"data\":[],\"has_more\":false,\"total\":0}");
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
    private readonly FakeDifyHandler _dify = new();
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly string _ownerId;
    private readonly KnowledgeService _knowledge;
    private readonly WorkspaceKnowledgeStore _wkStore;

    public KnowledgeSyncParticipantTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ksp_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _users = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));
        _wkStore = new WorkspaceKnowledgeStore(_config);
        _knowledge = new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "key" }),
            _wkStore);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    // Общая проверка контракта: resolve по живому DocId → ключ; чтение не мутирует стор
    // (повторный синк — ноль изменений); неизвестный DocId → пусто; инвалидация ключа →
    // синк удаляет старый документ и создаёт новый.
    private async Task AssertContractAsync(
        KnowledgeSyncTarget target, string docId, string expectedKey, Func<Task<int>> runSync)
    {
        // Resolve: живой DocId → ключ записи
        var resolved = await target.ResolveAsync([docId]);
        resolved.Should().ContainSingle().Which.Should().Be((docId, expectedKey));

        // Сирота: неизвестный DocId не сопоставляется
        (await target.ResolveAsync(["doc-nope"])).Should().BeEmpty();

        // Resolve — чистое чтение: штатному синку нечего делать
        (await runSync()).Should().Be(0);

        // Invalidate → хеш сброшен → синк пересоздаёт: DELETE старого + create нового
        var deletesBefore = _dify.CallsOf("DELETE", $"/documents/{docId}").Count;
        var createsBefore = _dify.CallsOf("POST", "/document/create_by_text").Count;
        await target.InvalidateAsync([expectedKey]);
        (await runSync()).Should().Be(1);
        _dify.CallsOf("DELETE", $"/documents/{docId}").Should().HaveCount(deletesBefore + 1);
        _dify.CallsOf("POST", "/document/create_by_text").Should().HaveCount(createsBefore + 1);
    }

    [Fact]
    public async Task PersonaMemoryService_Соблюдает_Контракт_Участника()
    {
        var personas = new PersonaManager(_config);
        var svc = new PersonaMemoryService(_knowledge, personas, _users, _config,
            NullLogger<PersonaMemoryService>.Instance);
        var persona = personas.Create(_ownerId, "Ада", "Аналитик", null, null,
            null, null, PersonaScope.Global, null, null, null, memoryEnabled: true);
        var entry = svc.Remember(_ownerId, persona.Id, PersonaMemoryType.Semantic,
            "факт номер один", null, null)!;
        await svc.SyncAsync(_ownerId, persona.Id);   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.DatasetId.Should().Be("ds-1");
        target.Label.Should().Be($"persona:{persona.Id}");
        target.OwnerUserIds.Should().BeEquivalentTo([_ownerId]);

        await AssertContractAsync(target, "doc-1", entry.Id, () => svc.SyncAsync(_ownerId, persona.Id));
    }

    [Fact]
    public async Task TeamMemoryService_Соблюдает_Контракт_Участника()
    {
        var svc = new TeamMemoryService(_config, null, _knowledge, _users);
        var entry = svc.Add(_ownerId, "proj-1", "общая договорённость");
        await svc.SyncAsync(_ownerId, "proj-1");   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.Label.Should().Be($"team:{_ownerId}:proj-1");
        target.OwnerUserIds.Should().BeEquivalentTo([_ownerId]);

        await AssertContractAsync(target, "doc-1", entry.Id, () => svc.SyncAsync(_ownerId, "proj-1"));
    }

    [Fact]
    public async Task DossierStore_Соблюдает_Контракт_Участника()
    {
        var svc = new DossierStore(_config, null, _knowledge, _users);
        var dossier = new ChangeDossier
        {
            OwnerId = _ownerId,
            ProjectId = "proj-1",
            CommitSha = "abc1234",
            CommitSubject = "fix: тест",
            Why = "проверка контракта",
        };
        svc.Add(dossier);
        await svc.SyncAsync(_ownerId, "proj-1");   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.Label.Should().Be($"dossiers:{_ownerId}:proj-1");

        await AssertContractAsync(target, "doc-1", dossier.Id, () => svc.SyncAsync(_ownerId, "proj-1"));
    }

    [Fact]
    public async Task NotesKnowledgeService_Соблюдает_Контракт_Участника()
    {
        var appSettings = new AppSettingsService(_config);
        var projects = new ProjectManager(_config, _users, appSettings);
        var notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance);
        var svc = new NotesKnowledgeService(_knowledge, notes, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var note = notes.Create(_ownerId, new CreateNoteRequest("Заметка", "содержимое заметки"));
        await svc.SyncAllAsync(_ownerId);   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.Label.Should().Be($"notes:{_ownerId}");

        await AssertContractAsync(target, "doc-1", note.Id, () => svc.SyncAllAsync(_ownerId));
    }

    [Fact]
    public async Task ProjectKnowledgeSyncService_Соблюдает_Контракт_Участника()
    {
        var appSettings = new AppSettingsService(_config);
        var projects = new ProjectManager(_config, _users, appSettings);
        var files = new FileService();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var svc = new ProjectKnowledgeSyncService(_knowledge, _wkStore, projects, files, hub.Object,
            NullLogger<ProjectKnowledgeSyncService>.Instance);

        var projectDir = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "a.md"), "текст файла");
        var project = projects.Create("proj", projectDir, _ownerId, "tester");
        await svc.IndexPathAsync(project, "tester", "a.md");   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.DatasetId.Should().Be("ds-1");
        target.Label.Should().Be($"project:{WorkspaceKnowledgeStore.NormalizePath(projectDir)}");
        target.OwnerUserIds.Should().BeEquivalentTo([_ownerId]);

        await AssertContractAsync(target, "doc-1", "a.md", () => svc.SyncAsync(projectDir));
    }
}
