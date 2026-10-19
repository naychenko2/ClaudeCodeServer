using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.TurnAutoResume;

/// <summary>
/// Регресс (ADR-033, причина 1): штатная остановка (KillAllProcesses) не должна перетирать
/// Working в Error/Active. Умирающий процесс присылает ErrorMessage/ExitedMessage, и
/// OnMessageAsync при поднятом признаке остановки не переводит по ним статус ни в памяти,
/// ни на диске — sessions.json сохранит Working, и LoadSessions отберёт сессию в
/// кандидаты на auto-resume. ResultMessage пишется как обычно (ход честно завершился).
/// Мутация: убрать гашение error/exited по _stopping в OnMessageAsync → тесты красные.
/// </summary>
public class GracefulShutdownStatusTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectsJsonPath;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public GracefulShutdownStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "graceful_shutdown_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _projectsJsonPath = Path.Combine(_tempDir, "projects.json");
        _sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _projectsJsonPath,
            })
            .Build();

        var userStore = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        _projectManager = new ProjectManager(_config, userStore, appSettings);
        _historyService = new ChatHistoryService(_config);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private SessionManager CreateSessionManager()
    {
        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var userStore = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
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
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projectManager, _historyService, _config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, _broadcaster);
    }

    private void WriteSessions(IEnumerable<Session> sessions)
    {
        var json = JsonSerializer.Serialize(sessions.ToList());
        File.WriteAllText(_sessionsJsonPath, json);
    }

    private Session MakeWorkingSession(string? claudeSessionId = null) =>
        new Session
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = "proj-1",
            ClaudeSessionId = claudeSessionId,
            Status = SessionStatus.Working,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // --- Регресс причины 1: KillAllProcesses + ErrorMessage не перетирает статус ---
    // Залипший Working (замечание Веры): LoadSessions переводит Working → Orphaned и
    // сохраняет на диск, поэтому после первого старта на диске уже Orphaned, а не Working.
    // Штатная остановка (ErrorMessage умирающего процесса) при поднятом признаке не
    // перетирает его в Error; второй старт видит Orphaned на диске и ход НЕ берёт в
    // кандидаты («только ходы последнего рестарта»). Мутация: убрать сохранение Orphaned
    // в LoadSessions → на диске Working → второй старт снова берёт кандидата → тест красный.
    [Fact]
    public async Task ШтатнаяОстановка_ErrorMessage_OrphanedСохраняется_ВторойСтартНеБерётКандидата()
    {
        var session = MakeWorkingSession(claudeSessionId: "csid-" + Guid.NewGuid().ToString("N"));
        WriteSessions([session]);
        var sut = CreateSessionManager();

        // LoadSessions отобрал сессию в кандидаты (исходный Working) и сохранил Orphaned на диск.
        sut.TakeRestartResumeCandidates().Should().Contain(session.Id,
            "исходный Working — ход оборван, кандидат на auto-resume");

        // Штатная остановка: KillAllProcesses поднимает признак остановки первой строкой.
        sut.KillAllProcesses();

        // Умирающий процесс присылает ErrorMessage; OnMessageAsync при поднятом признаке гасит
        // его (newStatus = null), не перетирая статус. LoadSessions уже перевёл Working → Orphaned
        // в памяти и на диске; без гашения ErrorMessage сделал бы Error.
        var acc = new TurnAccumulator(new List<StoredMessage>());
        await sut.OnMessageAsync(session.Id, acc, new ErrorMessage("процесс завершился"));

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            "признак остановки: ErrorMessage не перетирает статус в Error (в памяти Orphaned из LoadSessions)");

        // На диске Orphaned (строкой, как пишет SaveSessions): LoadSessions его сохранил,
        // ErrorMessage в окне остановки не перетёр.
        var raw = File.ReadAllText(_sessionsJsonPath);
        raw.Should().Contain("\"Status\":\"Orphaned\"",
            "Orphaned сохранён в sessions.json — не перетёрт в Error")
            .And.NotContain("\"Status\":\"Error\"");

        // Второй старт поверх того же файла: на диске Orphaned (не Working) → ход НЕ кандидат.
        var sut2 = CreateSessionManager();
        sut2.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            "LoadSessions видит Orphaned на диске (уже не живой статус)");
        sut2.TakeRestartResumeCandidates().Should().NotContain(session.Id,
            "на диске Orphaned, не Working — «только ходы последнего рестарта», давно оборванный ход не возобновляется");
    }

    // --- Контроль: БЕЗ признака остановки ErrorMessage всё же переводит в Error ---
    // Тест показывает, что именно гашение по _stopping (а не что-то другое) держит Working:
    // без поднятого признака статус станет Error.
    [Fact]
    public async Task БезОстановки_ErrorMessage_ПереводитВError()
    {
        var session = MakeWorkingSession(claudeSessionId: "csid-" + Guid.NewGuid().ToString("N"));
        WriteSessions([session]);
        var sut = CreateSessionManager();

        // KillAllProcesses НЕ вызывали: признак остановки не поднят.
        var acc = new TurnAccumulator(new List<StoredMessage>());
        await sut.OnMessageAsync(session.Id, acc, new ErrorMessage("процесс завершился"));

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error,
            "без признака остановки ErrorMessage штатно переводит Working → Error");
    }

    // --- ExitedMessage в окне остановки: Working в памяти не сбрасывается в Active ---
    // LoadSessions переводит Working → Orphaned (и сохраняет Orphaned на диск), а для
    // Orphaned exited и так ничего не меняет (ветка `_ => null`), поэтому сначала возвращаем
    // в памяти честный Working живого хода. Мутация: убрать гашение error/exited по _stopping
    // в OnMessageAsync → Active → тест красный.
    [Fact]
    public async Task ШтатнаяОстановка_ExitedMessage_СтатусWorking_СохраняетсяИКандидатВозвращается()
    {
        var session = MakeWorkingSession(claudeSessionId: "csid-" + Guid.NewGuid().ToString("N"));
        WriteSessions([session]);
        var sut = CreateSessionManager();
        sut.GetById(session.Id)!.Status = SessionStatus.Working;

        sut.KillAllProcesses();
        await sut.OnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()), new ExitedMessage());

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Working,
            "признак остановки: exited умирающего процесса не сбрасывает Working в Active");
        // На диске Orphaned (LoadSessions его сохранил); exited в окне остановки не перезаписал.
        File.ReadAllText(_sessionsJsonPath).Should().Contain("\"Status\":\"Orphaned\"",
            "на диске Orphaned из LoadSessions — exited в окне остановки его не перезаписал");
        CreateSessionManager().TakeRestartResumeCandidates().Should().NotContain(session.Id,
            "на диске Orphaned, не Working — «только ходы последнего рестарта»");
    }

    // --- ResultMessage в окне остановки: ход завершился честно, пишется Active ---
    // Без точечного выхода (гасим только обрывы) result тоже терялся бы, на диске оставался бы
    // Working, и уже завершённый ход после рестарта «продолжился» бы повторно.
    // Мутация: гасить по _stopping и ResultMessage → Working на диске → тест красный.
    [Fact]
    public async Task ШтатнаяОстановка_ResultMessage_ПишетсяActive_ВКандидатыНеИдёт()
    {
        var session = MakeWorkingSession(claudeSessionId: "csid-" + Guid.NewGuid().ToString("N"));
        WriteSessions([session]);
        var sut = CreateSessionManager();
        sut.GetById(session.Id)!.Status = SessionStatus.Working;

        sut.KillAllProcesses();
        await sut.OnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 1, 1, null, null));

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active, "result хода пишется и при остановке");
        var raw = File.ReadAllText(_sessionsJsonPath);
        // SaveSessions пишет статус строкой (в отличие от исходного файла теста с числом)
        raw.Should().Contain("\"Status\":\"Active\"", "на диске Active")
            .And.NotContain("\"Status\":1").And.NotContain("\"Status\":\"Working\"");
        CreateSessionManager().TakeRestartResumeCandidates().Should().NotContain(session.Id,
            "ход завершён — повторно продолжать его после рестарта нельзя");
    }

    // --- Залипший Working (замечание Веры): первый старт сохраняет Orphaned, второй ход не берёт ---
    // sessions.json с Working → первый SessionManager (LoadSessions переводит Working → Orphaned
    // и сохраняет на диск; флаг роли не играет, ход не отправляется) → второй SessionManager поверх
    // того же файла видит Orphaned (не живой статус) → TakeRestartResumeCandidates() пуст.
    // Мутация: убрать сохранение Orphaned в LoadSessions → на диске Working → второй старт берёт
    // кандидата → тест красный.
    [Fact]
    public void ЗалипшийWorking_ПервыйСтартСохраняетOrphaned_ВторойСтартНеБерётКандидата()
    {
        var session = MakeWorkingSession(claudeSessionId: "csid-" + Guid.NewGuid().ToString("N"));
        WriteSessions([session]);

        // Первый старт: LoadSessions отбирает кандидата (исходный Working) и сохраняет Orphaned на диск.
        var first = CreateSessionManager();
        first.TakeRestartResumeCandidates().Should().Contain(session.Id,
            "первый старт: исходный Working — ход оборван, кандидат");

        // Второй старт поверх того же файла: на диске Orphaned (не Working) → ход не кандидат.
        var second = CreateSessionManager();
        second.TakeRestartResumeCandidates().Should().BeEmpty(
            "на диске Orphaned, не Working — «только ходы последнего рестарта», давно оборванный ход не возобновляется");
        second.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            "LoadSessions видит Orphaned на диске (уже не живой статус)");
        File.ReadAllText(_sessionsJsonPath).Should().Contain("\"Status\":\"Orphaned\"",
            "на диске Orphaned — Working не залип");
    }
}
