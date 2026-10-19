using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.TurnAutoResume;

/// <summary>
/// B3: публичный <see cref="SessionManager.ResumeAfterRestartAsync"/> — возобновление хода,
/// оборванного рестартом сервера. Пять регресс-тестов:
///   1. карточка server_restart лежит в истории ДО user_message;
///   2. битый хвост — отказ без отправки (счётчик не инкрементнут);
///   3. result хода сбрасывает RestartResumeAttempts;
///   4. UpdatedAt не двигается;
///   5. порядок шагов: Append карточки ДО OnUserMessage (карточка уже в истории на момент отправки).
/// </summary>
public class ResumeAfterRestartAsyncTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectsJsonPath;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public ResumeAfterRestartAsyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "resume_restart_tests_" + Guid.NewGuid().ToString("N"));
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

    // Подкласс SessionManager: швы IsTailIntactForResume / SendRestartResumeTurnAsync
    // подменяются, чтобы тесты управляли целостностью хвоста и наблюдали отправки.
    private sealed class SpySessionManager : SessionManager
    {
        public bool TailIntact = true;
        public bool UseRealSend;
        public int SendCalls;
        // Журнал порядка шагов: фиксирует, была ли карточка server_restart уже в истории
        // на момент отправки resume-хода (контракт: Append карточки ДО OnUserMessage).
        public readonly List<string> OpLog = [];

        public SpySessionManager(
            ProjectManager projects, ChatHistoryService history, IConfiguration config,
            ILlmSessionAdapterFactory adapters, FalCostService falCost, UsageService usage,
            AppSettingsService appSettings, UserStore users, JwtService jwt,
            Microsoft.AspNetCore.Hosting.Server.IServer server, LlmProviderRegistry llmProviders,
            FeatureFlagService flags, PersonaManager personas, PersonaBindingsService bindings,
            ClaudeSubscriptionPool subscriptionPool, ILogger<SessionManager> log,
            ClaudeHomeServer.Services.Execution.ILauncherFactory launchers,
            ClaudeHomeServer.Services.Execution.SandboxManager sandbox,
            ClaudeHomeServer.Services.Composition.ISessionBroadcaster broadcaster)
            : base(projects, history, config, adapters, falCost, usage, appSettings, users, jwt, server,
                llmProviders, flags, personas, bindings, subscriptionPool, log, launchers, sandbox, broadcaster)
        {
        }

        protected override bool IsTailIntactForResume(string sessionId) => TailIntact;

        protected override Task<SendUserOutcome> SendRestartResumeTurnAsync(string sessionId, string text)
        {
            SendCalls++;
            if (UseRealSend)
                return base.SendRestartResumeTurnAsync(sessionId, text);
            var acc = SessionManagerTestAccess.AccumulatorOf(this, sessionId);
            // Контракт порядка: карточка пишется в историю ДО отправки resume-хода.
            // На момент отправки она уже должна быть в аккумуляторе (Append раньше OnUserMessage).
            var cardAlready = acc.GetAll()
                .Any(m => m is StoredWorkLoopStoppedMessage w && w.Reason == "server_restart");
            OpLog.Add(cardAlready ? "card_before_send" : "send_before_card");
            // Record-режим: кладём user_message в аккумулятор — тот же побочный эффект,
            // что у реального хода (OnUserMessage в SendDirectAsync), без запуска процесса.
            acc.OnUserMessage(text, []);
            return Task.FromResult(SendUserOutcome.Started);
        }
    }

    private SpySessionManager CreateSpySessionManager()
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
        return new SpySessionManager(_projectManager, _historyService, _config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, _broadcaster);
    }

    private void WriteSessions(IEnumerable<Session> sessions)
    {
        var json = JsonSerializer.Serialize(sessions.ToList());
        File.WriteAllText(_sessionsJsonPath, json);
    }

    private Session MakeSession(SessionStatus status, string projectId = "proj-1",
        string? claudeSessionId = null) =>
        new Session
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ClaudeSessionId = claudeSessionId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // --- 1. Карточка server_restart в истории ДО user_message ---

    [Fact]
    public async Task ResumeAfterRestart_КарточкаЛегитВИсториюДосылки()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();

        var result = await sut.ResumeAfterRestartAsync(session.Id);

        result.Sent.Should().BeTrue("кандидат Orphaned с целым хвостом — возобновлён");
        var history = (await sut.GetHistoryAsync(session.Id)).ToList();
        var cardIdx = history.FindIndex(m => m is StoredWorkLoopStoppedMessage w && w.Reason == "server_restart");
        var userIdx = history.FindIndex(m => m is StoredUserMessage);
        cardIdx.Should().BeGreaterThanOrEqualTo(0, "карточка server_restart в истории");
        userIdx.Should().BeGreaterThanOrEqualTo(0, "resume-сообщение в истории");
        cardIdx.Should().BeLessThan(userIdx, "карточка ДО user_message");
    }

    // --- 2. Битый хвост — отказ без отправки, счётчик не инкрементнут ---

    [Fact]
    public async Task ResumeAfterRestart_БитыйХвост_ОтказБезОтправки()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();
        sut.TailIntact = false;

        var result = await sut.ResumeAfterRestartAsync(session.Id);

        result.Sent.Should().BeFalse("битый хвост — отказ");
        sut.SendCalls.Should().Be(0, "отправка не вызвана");
        sut.GetById(session.Id)!.RestartResumeAttempts.Should().Be(0, "счётчик не инкрементнут");
        var history = await sut.GetHistoryAsync(session.Id);
        history.OfType<StoredWorkLoopStoppedMessage>().Should().BeEmpty("карточка не добавлена");
    }

    // --- 3. Result хода сбрасывает RestartResumeAttempts ---

    [Fact]
    public async Task ResumeAfterRestart_ResultХодаСбрасываетСчётчик()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();

        var result = await sut.ResumeAfterRestartAsync(session.Id);
        result.Sent.Should().BeTrue();
        sut.GetById(session.Id)!.RestartResumeAttempts.Should().Be(1, "метод инкрементнул счётчик");

        // Симулируем result хода: обработчик сбрасывает счётчик (B2, SessionManager:9762).
        var acc = SessionManagerTestAccess.AccumulatorOf(sut, session.Id);
        await sut.OnMessageAsync(session.Id, acc, new ResultMessage("success", 100, 1, null, null));

        sut.GetById(session.Id)!.RestartResumeAttempts.Should().Be(0, "result хода сбрасывает счётчик");
    }

    // --- 4. UpdatedAt не двигается ---

    [Fact]
    public async Task ResumeAfterRestart_UpdatedAtНеДвигается()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();
        var before = sut.GetById(session.Id)!.UpdatedAt;

        await sut.ResumeAfterRestartAsync(session.Id);

        sut.GetById(session.Id)!.UpdatedAt.Should().Be(before,
            "служебные шаги (карточка + счётчик) UpdatedAt не двигают");
    }

    // --- 5. Контракт порядка: Append карточки ДО OnUserMessage ---
    // Тест 1 проверяет финальный порядок в истории, но карточка лежит в _history, а
    // user_message — в _currentTurn, и GetAll() всегда кладёт _history перед _currentTurn:
    // финальная лента не меняет порядок даже при перестановке шагов в реализации. Этот
    // тест ловит именно перестановку: на момент отправки resume-хода карточка уже в истории.

    [Fact]
    public async Task ResumeAfterRestart_КарточкаДоUserMessage_ПорядокШагов()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();

        var result = await sut.ResumeAfterRestartAsync(session.Id);

        result.Sent.Should().BeTrue("кандидат Orphaned с целым хвостом — возобновлён");
        sut.OpLog.Should().Equal(["card_before_send"],
            "карточка server_restart пишется в историю ДО отправки resume-хода " +
            "(Append раньше OnUserMessage; перестановка шагов даёт send_before_card)");
    }

    // --- 6. Карточка work_loop_stopped на ДИСКЕ (history.json) ---
    // Тест 1 проверяет in-memory (GetHistoryAsync читает аккумулятор). V1 нашёл, что
    // карточка теряется именно на диске: user-директива в истории, а карточки нет.
    // Сценарий 1: чат уже имел ClaudeSessionId (реальный ход шёл), после рестарта
    // история пишется под ClaudeSessionId, не под Id. Мутация: убрать гидратацию
    // (EnsureAccumulatorAsync) или AddWorkLoopStoppedNoticeAsync → карточки на диске нет.
    [Fact]
    public async Task ResumeAfterRestart_КарточкаНаДиске()
    {
        var csid = "csid-" + Guid.NewGuid().ToString("N");
        var session = MakeSession(SessionStatus.Orphaned, claudeSessionId: csid);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();

        var result = await sut.ResumeAfterRestartAsync(session.Id);
        result.Sent.Should().BeTrue("кандидат Orphaned — возобновлён");

        // history.json: data/sessions/{ClaudeSessionId}/history.json (ключ = HistoryKeyOf = ClaudeSessionId)
        var historyPath = Path.Combine(_tempDir, "sessions", csid, "history.json");
        File.Exists(historyPath).Should().BeTrue("history.json под ClaudeSessionId создан после resume");
        var raw = File.ReadAllText(historyPath);
        raw.Should().Contain("\"work_loop_stopped\"", "карточка work_loop_stopped на диске");
        raw.Should().Contain("\"server_restart\"", "reason=server_restart на диске");
    }

    // --- 7. RestartResumeAttempts == 1 на ДИСКЕ (sessions.json) ---
    // Тест 3 проверяет in-memory (GetById). V1 нашёл, что счётчик остаётся 0 на диске.
    // Мутация: убрать += 1 или SaveSessions() → счётчик 0 на диске → тест краснеет.
    [Fact]
    public async Task ResumeAfterRestart_СчётчикНаДиске()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);
        var sut = CreateSpySessionManager();

        var result = await sut.ResumeAfterRestartAsync(session.Id);
        result.Sent.Should().BeTrue("кандидат Orphaned — возобновлён");

        // sessions.json: SaveSessions сериализует с _jsonOpts (без PropertyNamingPolicy → PascalCase)
        var raw = File.ReadAllText(_sessionsJsonPath);
        raw.Should().Contain("\"RestartResumeAttempts\":1",
            "RestartResumeAttempts=1 записан в sessions.json");
    }
}
