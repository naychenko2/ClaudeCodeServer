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

    // --- Регресс причины 1: KillAllProcesses + ErrorMessage → Working сохраняется ---

    [Fact]
    public async Task ШтатнаяОстановка_ErrorMessage_СтатусWorking_СохраняетсяИКандидатВозвращается()
    {
        var session = MakeWorkingSession(claudeSessionId: "csid-" + Guid.NewGuid().ToString("N"));
        WriteSessions([session]);
        var sut = CreateSessionManager();

        // LoadSessions уже отобрал сессию в кандидаты (исходный Working) — снимок не пуст.
        sut.TakeRestartResumeCandidates().Should().Contain(session.Id,
            "исходный Working — ход оборван, кандидат на auto-resume");

        // Штатная остановка: KillAllProcesses поднимает признак остановки первой строкой.
        sut.KillAllProcesses();

        // Умирающий процесс присылает ErrorMessage; обработчик зовёт ApplyStatusAsync,
        // который при поднятом признаке ранним выходом не перетирает статус. LoadSessions
        // уже перевёл Working → Orphaned в памяти; без фикса ErrorMessage сделал бы Error.
        var acc = new TurnAccumulator(new List<StoredMessage>());
        await sut.OnMessageAsync(session.Id, acc, new ErrorMessage("процесс завершился"));

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            "признак остановки: ErrorMessage не перетирает статус в Error (в памяти остался Orphaned из LoadSessions)");

        // И на диске: sessions.json всё ещё Working (ApplyStatusAsync не вызвал SaveSessions).
        var raw = File.ReadAllText(_sessionsJsonPath);
        raw.Should().Contain("\"Status\":1",
            "Working (int 1) сохранён в sessions.json — не перетёрт в Error (int 5)");

        // Новый экземпляр поверх того же файла: LoadSessions снова видит Working → кандидат.
        var sut2 = CreateSessionManager();
        sut2.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            "LoadSessions переводит Working → Orphaned (как и любой живой статус при рестарте)");
        sut2.TakeRestartResumeCandidates().Should().Contain(session.Id,
            "sessions.json сохранил Working — новый старт снова отбирает сессию в кандидаты");
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
    // LoadSessions переводит Working → Orphaned, а для Orphaned exited и так ничего не меняет
    // (ветка `_ => null`), поэтому сначала возвращаем в памяти честный Working живого хода.
    // Мутация: убрать гашение error/exited по _stopping в OnMessageAsync → Active → тест красный.
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
        File.ReadAllText(_sessionsJsonPath).Should().Contain("\"Status\":1",
            "на диске остался Working — sessions.json не перезаписан");
        CreateSessionManager().TakeRestartResumeCandidates().Should().Contain(session.Id,
            "после рестарта сессия снова в кандидатах на auto-resume");
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
}
