using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты корректности маппинга статусов сессий при рестарте сервера (LoadSessions).
/// </summary>
public class SessionStatusTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectsJsonPath;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public SessionStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sess_status_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _projectsJsonPath = Path.Combine(_tempDir, "projects.json");
        _sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _projectsJsonPath,
            })
            .Build();

        var userStore = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        _projectManager = new ProjectManager(_config, userStore, appSettings);
        _historyService = new ChatHistoryService(_config);
    }

    public void Dispose()
    {
        Helpers.TestFs.DeleteDirectoryResilient(_tempDir);
    }

    private SessionManager CreateSessionManager()
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var userStore = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projectManager, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projectManager, _historyService, _config, adapters, falCost, usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, _broadcaster);
    }

    private void WriteSessions(IEnumerable<Session> sessions)
    {
        var json = JsonSerializer.Serialize(sessions.ToList());
        File.WriteAllText(_sessionsJsonPath, json);
    }

    private Session MakeSession(SessionStatus status, string projectId = "proj-1") =>
        new Session
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // --- LoadSessions: маппинг статусов ---

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Starting)]
    [InlineData(SessionStatus.Waiting)]
    public void LoadSessions_ActiveProcessStatuses_BecomeOrphaned(SessionStatus status)
    {
        var session = MakeSession(status);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        var loaded = sut.GetById(session.Id);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(SessionStatus.Orphaned,
            because: $"статус {status} означает, что процесс работал — после рестарта он прерван");
    }

    [Fact]
    public void LoadSessions_ActiveStatus_BecomesFinished()
    {
        var session = MakeSession(SessionStatus.Active);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        var loaded = sut.GetById(session.Id);
        loaded!.Status.Should().Be(SessionStatus.Finished,
            because: "Active означает сессия была открыта, но Клод не работал — корректно завершена");
    }

    [Fact]
    public void LoadSessions_FinishedStatus_StaysFinished()
    {
        var session = MakeSession(SessionStatus.Finished);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Finished);
    }

    [Fact]
    public void LoadSessions_ErrorStatus_StaysError()
    {
        var session = MakeSession(SessionStatus.Error);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }

    [Fact]
    public void LoadSessions_OrphanedStatus_StaysOrphaned()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            because: "уже помеченная прерванной сессия не меняет статус при следующем рестарте");
    }

    // --- Все статусы сразу ---

    [Fact]
    public void LoadSessions_MixedStatuses_MappedCorrectly()
    {
        var working = MakeSession(SessionStatus.Working);
        var starting = MakeSession(SessionStatus.Starting);
        var waiting = MakeSession(SessionStatus.Waiting);
        var active = MakeSession(SessionStatus.Active);
        var finished = MakeSession(SessionStatus.Finished);
        var error = MakeSession(SessionStatus.Error);
        var orphaned = MakeSession(SessionStatus.Orphaned);

        WriteSessions([working, starting, waiting, active, finished, error, orphaned]);

        var sut = CreateSessionManager();

        sut.GetById(working.Id)!.Status.Should().Be(SessionStatus.Orphaned);
        sut.GetById(starting.Id)!.Status.Should().Be(SessionStatus.Orphaned);
        sut.GetById(waiting.Id)!.Status.Should().Be(SessionStatus.Orphaned);
        sut.GetById(active.Id)!.Status.Should().Be(SessionStatus.Finished);
        sut.GetById(finished.Id)!.Status.Should().Be(SessionStatus.Finished);
        sut.GetById(error.Id)!.Status.Should().Be(SessionStatus.Error);
        sut.GetById(orphaned.Id)!.Status.Should().Be(SessionStatus.Orphaned);
    }

    // --- Целостность enum (сериализация через System.Text.Json) ---

    [Fact]
    public void SessionStatus_EnumValues_StableAfterSerializationRoundtrip()
    {
        // Проверяем, что добавление Orphaned в конец не сдвинуло числовые значения
        // существующих статусов. Если сдвинулось — прочитанные из файла данные
        // будут неверно трактоваться.
        ((int)SessionStatus.Starting).Should().Be(0);
        ((int)SessionStatus.Working).Should().Be(1);
        ((int)SessionStatus.Active).Should().Be(2);
        ((int)SessionStatus.Waiting).Should().Be(3);
        ((int)SessionStatus.Finished).Should().Be(4);
        ((int)SessionStatus.Error).Should().Be(5);
        ((int)SessionStatus.Orphaned).Should().Be(6);
    }

    [Fact]
    public void SessionStatus_SerializedAsInteger_DeserializesCorrectly()
    {
        // Симулируем формат, в котором sessions.json сохраняет статусы
        // System.Text.Json сериализует enum как int по умолчанию
        var session = MakeSession(SessionStatus.Finished);

        var json = JsonSerializer.Serialize(new List<Session> { session });

        // Значение 4 (Finished) должно читаться как Finished, а не Orphaned
        json.Should().Contain("\"Status\":4",
            because: "Finished имеет целочисленное значение 4");

        var restored = JsonSerializer.Deserialize<List<Session>>(json)!;
        restored[0].Status.Should().Be(SessionStatus.Finished);
    }

    [Fact]
    public void SessionStatus_Orphaned_SerializesAsInt6()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        var json = JsonSerializer.Serialize(new List<Session> { session });

        // Важно: Orphaned = 6, не 4 (Finished = 4 до добавления Orphaned в конец)
        json.Should().Contain("\"Status\":6",
            because: "Orphaned добавлен в конец enum и имеет значение 6");
    }

    // --- LoadSessions: нет файла ---

    [Fact]
    public void LoadSessions_NoFile_StartsEmpty()
    {
        // sessions.json не существует — менеджер стартует без сессий
        File.Exists(_sessionsJsonPath).Should().BeFalse();

        var sut = CreateSessionManager();

        sut.GetById("any-id").Should().BeNull();
    }

    // --- LoadSessions: повреждённый файл ---

    [Fact]
    public void LoadSessions_CorruptFile_StartsEmpty()
    {
        File.WriteAllText(_sessionsJsonPath, "not valid json {{{");

        var sut = CreateSessionManager();

        // Не должен бросать исключение, просто пустой список
        sut.GetById("any-id").Should().BeNull();
    }

    // --- Записи с полями удалённого десктопного агента (ADR-008) ---

    // Разовой миграции нет: старые записи sessions.json/projects.json с DesktopChat и
    // DesktopAgentEnabled обязаны читаться штатно (неизвестное поле System.Text.Json пропускает),
    // а бывший десктопный чат — становиться обычным чатом проекта с сохранённым транскриптом.
    // Упади загрузка — JsonFileStore унёс бы весь стор в .corrupt, и чаты с проектами пропали бы.

    [Fact]
    public void LoadSessions_ЗаписьСDesktopChat_ЧитаетсяОбычнымЧатом()
    {
        File.WriteAllText(_sessionsJsonPath, """
            [{ "Id": "chat-desktop", "ProjectId": "proj-1", "Status": 4,
               "CreatedAt": "2026-09-01T10:00:00Z", "UpdatedAt": "2026-09-01T10:00:00Z",
               "ClaudeSessionId": "csid-desktop", "DesktopChat": true }]
            """);

        var sut = CreateSessionManager();

        var loaded = sut.GetById("chat-desktop");
        loaded.Should().NotBeNull("запись с полем удалённого типа чата не должна теряться");
        loaded!.ClaudeSessionId.Should().Be("csid-desktop", "транскрипт бывшего десктопного чата сохраняется");
        sut.GetByProject("proj-1").Select(s => s.Id).Should().Contain("chat-desktop");
    }

    [Fact]
    public void LoadProjects_ЗаписьСDesktopAgentEnabled_ЧитаетсяШтатно()
    {
        File.WriteAllText(_projectsJsonPath, """
            [{ "Id": "proj-desktop", "Name": "Старый проект", "RootPath": "/tmp/old-desktop-project",
               "OwnerId": "owner-1", "CreatedAt": "2026-09-01T10:00:00Z", "UpdatedAt": "2026-09-01T10:00:00Z",
               "DesktopAgentEnabled": true }]
            """);

        var userStore = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, userStore, new AppSettingsService(_config));

        projects.GetById("proj-desktop").Should().NotBeNull("запись с полем удалённого тумблера не должна теряться")
            .And.Subject.As<Project>().Name.Should().Be("Старый проект");
    }

    // --- B1: кандидаты на auto-resume (LoadSessions + TakeRestartResumeCandidates) ---

    [Fact]
    public void TakeRestartResumeCandidates_Working_ReturnsThenClears()
    {
        var working = MakeSession(SessionStatus.Working);
        WriteSessions([working]);

        var sut = CreateSessionManager();

        var first = sut.TakeRestartResumeCandidates();
        first.Should().Contain(working.Id, "исходный Working — ход оборван, кандидат на auto-resume");

        var second = sut.TakeRestartResumeCandidates();
        second.Should().BeEmpty("второй вызов не должен вернуть кандидатов повторно");
    }

    [Fact]
    public void TakeRestartResumeCandidates_Waiting_NotCandidate()
    {
        var waiting = MakeSession(SessionStatus.Waiting);
        WriteSessions([waiting]);

        var sut = CreateSessionManager();

        sut.TakeRestartResumeCandidates().Should().BeEmpty(
            "Waiting — ход ещё не шёл, кандидатом не является");
    }

    [Fact]
    public void TakeRestartResumeCandidates_Starting_NotCandidate()
    {
        // Starting — CLI ещё не стартовал, ход не шёл: кандидатом не является.
        var starting = MakeSession(SessionStatus.Starting);
        WriteSessions([starting]);

        var sut = CreateSessionManager();

        sut.TakeRestartResumeCandidates().Should().BeEmpty(
            "Starting — ход ещё не шёл, кандидатом не является");
    }

    [Fact]
    public void TakeRestartResumeCandidates_OldOrphaned_NotCandidate()
    {
        // Старый Orphaned без снимка — «только последний рестарт»: кандидатом не является.
        var orphaned = MakeSession(SessionStatus.Orphaned);
        WriteSessions([orphaned]);

        var sut = CreateSessionManager();

        sut.TakeRestartResumeCandidates().Should().BeEmpty(
            "старый Orphaned — «только последний рестарт», кандидатом не является");
    }

    [Fact]
    public async Task OnResult_ResetsRestartResumeAttempts()
    {
        var working = MakeSession(SessionStatus.Working);
        working.RestartResumeAttempts = 5;
        WriteSessions([working]);

        var sut = CreateSessionManager();
        var loaded = sut.GetById(working.Id)!;

        var acc = new TurnAccumulator(new List<StoredMessage>());
        await sut.OnMessageAsync(working.Id, acc, new ResultMessage("success", 100, 1, null, null));

        loaded.RestartResumeAttempts.Should().Be(0, "result хода сбрасывает счётчик auto-resume");
    }

    // Продолженный ход дошёл до результата инструмента — счётчик auto-resume обнуляется
    // (следующий рестарт снова его продолжит); до tool_result (crash сразу после --resume)
    // счётчик не трогаем. Мутация «убрать сброс в ToolResultMessage» → RED.
    [Fact]
    public async Task OnToolResult_ResetsRestartResumeAttempts()
    {
        var working = MakeSession(SessionStatus.Working);
        working.RestartResumeAttempts = 1;
        WriteSessions([working]);

        var sut = CreateSessionManager();
        var loaded = sut.GetById(working.Id)!;

        var acc = new TurnAccumulator(new List<StoredMessage>());
        await sut.OnMessageAsync(working.Id, acc, new ToolUseMessage("tu-1", "Bash", new { }));
        loaded.RestartResumeAttempts.Should().Be(1, "вызов инструмента — ещё не прогресс");
        await sut.OnMessageAsync(working.Id, acc, new ToolResultMessage("tu-1", "ok", false));
        loaded.RestartResumeAttempts.Should().Be(0, "результат инструмента — живая работа, не crash-loop");
    }

    [Fact]
    public void ResetRestartResumeAttempts_DoesNotMoveUpdatedAt()
    {
        // Сброс счётчика — служебная операция: сама по себе UpdatedAt не двигает
        // (сдвинет отдельная смена статуса, а не сброс).
        var s = MakeSession(SessionStatus.Active);
        var before = s.UpdatedAt;
        s.RestartResumeAttempts = 5;
        s.RestartResumeAttempts = 0; // то, что делает обработчик result
        s.UpdatedAt.Should().Be(before, "сброс счётчика UpdatedAt не двигает");
    }

    // --- B: сброс счётчика новым ходом человека (инцидент 20.10, чат df485156) ---
    // Один раз продолженный и снова оборванный чат иначе навсегда терял бы авто-продолжение,
    // даже если следующий ход запустил человек. Ход человека (hub или из очереди) обнуляет
    // счётчик в момент, когда он РЕАЛЬНО стартует (SendDirectAsync, после гейтов отказа/парковки);
    // авто-ходы (ServerRestartResume/WorkLoop/Direct/подталкивания) счётчик не трогают —
    // защита от crash-loop цела, а человекское сообщение не повторяется автоматически.
    // Проект создаём реальный: SendDirectAsync после точки сброса доходит до EnsureProcess,
    // который без проекта бросает «Проект не найден» (сброс уже прошёл, но тест не должен
    // падать на побочном эффекте).

    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private Project MakeProject(string name) =>
        _projectManager.Create(name, Path.Combine(_tempDir, "proj_" + name),
            TestUserId, TestUsername, createDirectory: true);

    [Fact]
    public async Task ХодЧеловека_СбрасываетСчётчикAutoResume()
    {
        var project = MakeProject("human-reset");
        var s = MakeSession(SessionStatus.Active, projectId: project.Id);
        s.RestartResumeAttempts = 1;
        WriteSessions([s]);

        var sut = CreateSessionManager();

        // Сообщение человека из хаба: auto=false (дефолт), cause=Unknown (дефолт) →
        // effectiveCause=User. Чат свободен → ход стартует → счётчик обнуляется.
        await sut.SendMessageAsync(s.Id, "продолжи", []);

        sut.GetById(s.Id)!.RestartResumeAttempts.Should().Be(0,
            "ход по сообщению человека обнуляет счётчик auto-resume");
    }

    [Theory]
    [InlineData(SessionManager.DeliveryCause.ServerRestartResume)]
    [InlineData(SessionManager.DeliveryCause.WorkLoop)]
    public async Task АвтоХод_СчётчикНеТрогает(SessionManager.DeliveryCause cause)
    {
        var project = MakeProject("auto-" + cause);
        var s = MakeSession(SessionStatus.Active, projectId: project.Id);
        s.RestartResumeAttempts = 1;
        WriteSessions([s]);

        var sut = CreateSessionManager();

        // Авто-ход (не человек): effectiveCause=cause, не User/QueueUser → счётчик не сбрасывается.
        await sut.SendMessageAsync(s.Id, "авто", [], auto: true, cause: cause);

        sut.GetById(s.Id)!.RestartResumeAttempts.Should().Be(1,
            $"авто-ход {cause} счётчик auto-resume не трогает (защита от crash-loop)");
    }

    // --- B: ветка очереди (QueueUser) — самый частый человеческий путь, стоявший за инцидентом 20.10 ---
    // Чат занят → сообщение человека встаёт в очередь → разбор очереди (DeliverPendingAsync →
    // SendDirectAsync с cause=QueueUser) обнуляет счётчик в момент фактической доставки, а не
    // постановки в очередь. Сброс обязан дойти до диска: иначе после рестарта счётчик
    // «продолженного человеком» чата снова блокировал бы авто-продолжение.
    [Fact]
    public async Task Очередь_ДоставкаСообщенияЧеловека_СбрасываетСчётчикAutoResume()
    {
        var project = MakeProject("queue-reset");
        var s = MakeSession(SessionStatus.Working, projectId: project.Id);
        s.RestartResumeAttempts = 1;
        WriteSessions([s]);

        var sut = CreateSessionManager();
        // LoadSessions переводит Working → Orphaned (ADR-033): возвращаем Working,
        // чтобы чат был «занят» (ход в полёте) в момент сообщения человека.
        sut.GetById(s.Id)!.Status = SessionStatus.Working;

        // Чат занят (Working) → сообщение человека встаёт в очередь, а не в новый ход.
        var outcome = await sut.SendMessageAsync(s.Id, "из очереди", []);
        outcome.Should().Be(SessionManager.SendUserOutcome.Queued,
            "занятый чат: сообщение человека уходит в очередь");

        // Разбор очереди: DeliverPendingAsync → SendDirectAsync (fromQueue, cause=QueueUser).
        // Приватный метод вызываем рефлексией (прецедент — ChatContinueServiceTests: _sessions/Pending).
        var entry = GetEntry(sut, s.Id);
        var pending = (System.Collections.IList)entry.GetType().GetField("Pending")!.GetValue(entry)!;
        var head = (SessionManager.QueuedMessage)pending[0];
        var deliver = typeof(SessionManager).GetMethod("DeliverPendingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)deliver.Invoke(sut, [s.Id, entry, head])!;

        sut.GetById(s.Id)!.RestartResumeAttempts.Should().Be(0,
            "сообщение человека, доставленное из очереди, обнуляет счётчик auto-resume");
        File.ReadAllText(_sessionsJsonPath).Should().Contain("\"RestartResumeAttempts\":0",
            "сброс доехал до диска: счётчик не переживёт рестарт");
    }

    private static object GetEntry(SessionManager sut, string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)field.GetValue(sut)!)[sessionId]!;
    }
}
