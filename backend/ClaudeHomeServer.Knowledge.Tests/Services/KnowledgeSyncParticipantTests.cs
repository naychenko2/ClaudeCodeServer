using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
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
    // Фейковый Dify: create dataset → ds-N, create_by_text → doc-N (или ForceDocId),
    // DELETE → 204 и документ уходит из ErrorDocs; листинг с ?status=error отдаёт ErrorDocs
    private sealed class FakeDifyHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path)> Calls = new();
        // datasetId → id документов в статусе error
        public readonly Dictionary<string, List<string>> ErrorDocs = new();
        // Отдавать на create один и тот же id — так на проде два ключа получили общий DocId
        public string? ForceDocId;
        private int _docSeq;
        private int _dsSeq;

        public List<(string Method, string Path)> CallsOf(string method, string pathPart) =>
            Calls.Where(c => c.Method == method && c.Path.Contains(pathPart)).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, path));

            if (request.Method == HttpMethod.Post && path == "/v1/datasets")
                return Json($"{{\"id\":\"ds-{++_dsSeq}\"}}");
            if (request.Method == HttpMethod.Post && path.Contains("/document/create_by_text"))
            {
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString();
                var id = ForceDocId ?? $"doc-{++_docSeq}";
                return Json($"{{\"document\":{{\"id\":\"{id}\",\"name\":{JsonSerializer.Serialize(name)},\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/documents"))
            {
                // /v1/datasets/{id}/documents
                var datasetId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
                var errors = request.RequestUri!.Query.Contains("status=error")
                    ? ErrorDocs.GetValueOrDefault(datasetId, [])
                    : [];
                var data = string.Join(',', errors.Select(d =>
                    JsonSerializer.Serialize(new { id = d, name = d, indexing_status = "error", error = "Max retries exceeded" })));
                return Json($"{{\"data\":[{data}],\"has_more\":false,\"total\":{errors.Count}}}");
            }
            if (request.Method == HttpMethod.Delete)
            {
                foreach (var list in ErrorDocs.Values) list.Remove(path.Split('/')[^1]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
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
        var svc = new PersonaMemoryService(_knowledge, personas, personas,
            new PersonaDirectoryAdapter(personas), new NoopPersonaEvents(),
            new EmptyDifyMetrics(), _users, _config,
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
        var svc = new TeamMemoryService(_config, null, _knowledge, null, _users);
        var entry = svc.Add(_ownerId, "proj-1", "общая договорённость");
        await svc.SyncAsync(_ownerId, "proj-1");   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.Label.Should().Be($"team:{_ownerId}:proj-1");
        target.OwnerUserIds.Should().BeEquivalentTo([_ownerId]);

        await AssertContractAsync(target, "doc-1", entry.Id, () => svc.SyncAsync(_ownerId, "proj-1"));
    }

    [Fact]
    public async Task SphereMemoryService_Соблюдает_Контракт_УчастникаИУдаляетДатасет()
    {
        var svc = new SphereMemoryService(_config, null, _knowledge, null, _users);
        var entry = svc.Add(_ownerId, "sph-1", "общая договорённость сферы");
        await svc.SyncAsync(_ownerId, "sph-1");   // ds-1, doc-1

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        target.Label.Should().Be($"sphere:{_ownerId}:sph-1");
        target.OwnerUserIds.Should().BeEquivalentTo([_ownerId]);
        await AssertContractAsync(target, "doc-1", entry.Id, () => svc.SyncAsync(_ownerId, "sph-1"));

        // Удаление сферы уносит Dify-датасет вместе с записями
        (await svc.DeleteAllForSphereAsync(_ownerId, "sph-1")).Should().Be(1);
        _dify.CallsOf("DELETE", "/datasets/ds-1").Should().NotBeEmpty();
        ((IKnowledgeSyncParticipant)svc).ListTargets().Should().BeEmpty();
    }

    [Fact]
    public async Task DossierStore_Соблюдает_Контракт_Участника()
    {
        var svc = new DossierStore(_config, null, _knowledge, null, _users);
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
        var files = new ProjectFileGateway(new FileService());
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var svc = new ProjectKnowledgeSyncService(_knowledge, _wkStore, projects, files,
            new RecordingHubNotifier(), new NullDifyMetrics(),
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

    private ChangeDossier NewDossier(string sha) => new()
    {
        OwnerId = _ownerId,
        ProjectId = "proj-1",
        CommitSha = sha,
        CommitSubject = "fix: тест",
        Why = "проверка " + sha,
    };

    // Регрессия 28.10.2026: на проде у двух паспортов досье ClaudeCodeServer оказался общий
    // DocId — ToDictionary в ResolveAsync падал, и цель не лечилась ни на одном тике.
    [Fact]
    public async Task DossierStore_Общий_DocId_У_Двух_Записей_Не_Роняет_Resolve()
    {
        var svc = new DossierStore(_config, null, _knowledge, null, _users);
        var first = svc.Add(NewDossier("aaa1111"));
        var second = svc.Add(NewDossier("bbb2222"));
        _dify.ForceDocId = "doc-shared";
        await svc.SyncAsync(_ownerId, "proj-1");
        _dify.ForceDocId = null;

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        var resolved = await target.ResolveAsync(["doc-shared"]);
        resolved.Should().BeEquivalentTo(new[] { ("doc-shared", first.Id), ("doc-shared", second.Id) });

        // Обе записи пересоздаются своими документами
        await target.InvalidateAsync([first.Id, second.Id]);
        (await svc.SyncAsync(_ownerId, "proj-1")).Should().Be(2);
        (await target.ResolveAsync(["doc-shared"])).Should().BeEmpty();
    }

    [Fact]
    public async Task TeamMemoryService_Общий_DocId_У_Двух_Записей_Не_Роняет_Resolve()
    {
        var svc = new TeamMemoryService(_config, null, _knowledge, null, _users);
        var first = svc.Add(_ownerId, "proj-1", "договорённость один");
        var second = svc.Add(_ownerId, "proj-1", "совсем другой факт два");
        _dify.ForceDocId = "doc-shared";
        await svc.SyncAsync(_ownerId, "proj-1");

        var target = ((IKnowledgeSyncParticipant)svc).ListTargets().Should().ContainSingle().Subject;
        (await target.ResolveAsync(["doc-shared"])).Select(r => r.EntryKey)
            .Should().BeEquivalentTo([first.Id, second.Id]);
    }

    // Участник, чьи записи не лечатся никогда (как оставшиеся 84 записи памяти sergey):
    // стоит в списке ПЕРЕД командной памятью и досье и каждым своим обходом выбирает бюджет
    private sealed class HogParticipant : IKnowledgeSyncParticipant
    {
        public IReadOnlyList<KnowledgeSyncTarget> ListTargets() =>
        [
            new KnowledgeSyncTarget("ds-hog", ["owner"], "persona:hog",
                docIds => Task.FromResult<IReadOnlyList<(string, string)>>(
                    docIds.Select(d => (d, "entry-" + d)).ToList()),
                _ => Task.CompletedTask, () => { }),
        ];
        public Task DeleteAllAsync(string userId) => Task.CompletedTask;
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Сквозная регрессия инцидента 28.10.2026: error-документы командной памяти и досье
    // пересоздаются реконсайлером, даже когда весь бюджет тика выбрала память персон,
    // идущая в списке участников первой
    [Fact]
    public async Task Реконсайлер_Пересоздаёт_Досье_И_Командную_Память_За_Прожорливой_Персоной()
    {
        var team = new TeamMemoryService(_config, null, _knowledge, null, _users);
        team.Add(_ownerId, "proj-1", "общая договорённость");
        await team.SyncAsync(_ownerId, "proj-1");                    // ds-1, doc-1
        var dossiers = new DossierStore(_config, null, _knowledge, null, _users);
        dossiers.Add(NewDossier("abc1234"));
        await dossiers.SyncAsync(_ownerId, "proj-1");                // ds-2, doc-2

        // Документы упали на эмбеддингах; у «персоны» вечные ошибки на весь бюджет
        _dify.ErrorDocs["ds-1"] = ["doc-1"];
        _dify.ErrorDocs["ds-2"] = ["doc-2"];
        _dify.ErrorDocs["ds-hog"] = ["h-1", "h-2"];

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dify:Reconcile:Mode"] = "heal",
                ["Dify:Reconcile:MaxPerCycle"] = "2",
            })
            .Build();
        var time = new FakeTime(DateTimeOffset.Parse("2026-10-28T01:23:00Z"));
        var sut = new KnowledgeIndexReconciler(_knowledge,
            new IKnowledgeSyncParticipant[] { new HogParticipant(), team, dossiers }, config,
            NullLogger<KnowledgeIndexReconciler>.Instance, time);

        await sut.TickAsync();                                       // бюджет ушёл «персоне»
        time.Now += TimeSpan.FromMinutes(5);
        await sut.TickAsync();                                       // очередь команды и досье

        // Штатный синк (KickSync — дебаунс, в тесте зовём напрямую) удаляет error-документ
        // и пересоздаёт запись новым документом
        var createsBefore = _dify.CallsOf("POST", "/document/create_by_text").Count;
        (await team.SyncAsync(_ownerId, "proj-1")).Should().Be(1);
        (await dossiers.SyncAsync(_ownerId, "proj-1")).Should().Be(1);
        _dify.CallsOf("DELETE", "/documents/doc-1").Should().ContainSingle();
        _dify.CallsOf("DELETE", "/documents/doc-2").Should().ContainSingle();
        _dify.CallsOf("POST", "/document/create_by_text").Should().HaveCount(createsBefore + 2);

        // Следующий обход видит, что ошибки ушли: обе записи — recovered. Через 16 минут
        // бюджет снова у «персоны» (её срок раньше) — команда и досье ждут тик, а не
        // сдвигаются на период
        time.Now += TimeSpan.FromMinutes(16);
        await sut.TickAsync();
        time.Now += TimeSpan.FromMinutes(5);
        await sut.TickAsync();
        sut.RecoveredTotal.Should().Be(2);
        sut.LastCounts.Should().Contain(new ReconcileTargetStatus($"team:{_ownerId}:proj-1", 0, 0));
        sut.LastCounts.Should().Contain(new ReconcileTargetStatus($"dossiers:{_ownerId}:proj-1", 0, 0));
    }
}
