using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    public ResumeAfterRestartAsyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "resume_restart_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _projectsJsonPath = Path.Combine(_tempDir, "projects.json");
        _sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Подкласс SessionManager: швы IsTailIntactForResume / SendRestartResumeTurnAsync
    // подменяются, чтобы тесты управляли целостностью хвоста и наблюдали отправки.
    private sealed class SpySessionManager : SessionManager
    {
        public bool TailIntact = true;
        // Инициализация = false: поле нигде не выставляется тестом (всегда record-режим),
        // но присваивание в объявлении гасит CS0649 «поле никогда не присваивается».
        public bool UseRealSend = false;
        public int SendCalls;
        // Журнал порядка шагов: фиксирует, была ли карточка server_restart уже в истории
        // на момент отправки resume-хода (контракт: Append карточки ДО OnUserMessage).
        public readonly List<string> OpLog = [];

        // Та же сигнатура, что у сжатого конструктора фасада (S3-8c): аргументы резолвятся
        // из контейнера помощника (CreateSpySessionManager), подмена — только виртуальных швов.
        public SpySessionManager(
            SessionStore store, SessionHistory historyRole, TurnCostLedger costs,
            PendingQueueService pending, CliRoute route, ChatWorktree worktree,
            ProviderMigration providerMigration, ChatNaming naming, ChatVisibility visibility,
            ChatSettings settings, McpEndpoint mcpEndpoint, BuiltinMcpContexts builtinContexts,
            PersonaLayerBuilder personaLayer, ExternalMcpContexts externalContexts,
            ModulesMcpContexts moduleContexts, TurnPassports turnPassports, TurnProcessHost processHost,
            TurnRecovery recovery, ChatCreation chatCreation, ChatDeletion chatDeletion,
            TurnCompletion completion, TurnIntake intake,
            ProjectManager projects, ChatHistoryService history, IConfiguration config,
            PersonaManager personas, ILogger<SessionManager> log,
            ISessionBroadcaster broadcaster, ILoggerFactory loggerFactory, IServiceProvider services,
            ClaudeHomeServer.Services.Turn.ITurnEventBus turnEvents, AppSettingsService appSettings,
            ClaudeHomeServer.Services.Execution.SandboxManager sandbox,
            // Необязательные — в хвосте, как у фасада: реестр вертикали llm может не быть.
            LlmProviderRegistry? llmProviders,
            ClaudeHomeServer.Services.Team.TeamPlanningService? teamPlanning, TaskManager? tasks,
            ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog? subagentRuns)
            : base(store, historyRole, costs, pending, route, worktree, providerMigration, naming,
                visibility, settings, mcpEndpoint, builtinContexts, personaLayer, externalContexts,
                moduleContexts, turnPassports, processHost, recovery, chatCreation, chatDeletion,
                completion, intake, projects, history, config, personas, log, broadcaster,
                loggerFactory, services, turnEvents, appSettings, sandbox, llmProviders, teamPlanning,
                tasks, subagentRuns)
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

    // Ядро собирается общим помощником S3-8a (тот же AddSessionCore, что в проде; конфиг и
    // пути — от него, DataPath = _tempDir/projects.json — те же файлы, что пишет WriteSessions).
    // Спай конструируется из тех же ролей контейнера: подменяются только виртуальные швы.
    private SpySessionManager CreateSpySessionManager()
    {
        var sp = SessionCoreTestHost.Build(_tempDir).Services;
        return new SpySessionManager(
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<SessionHistory>(),
            sp.GetRequiredService<TurnCostLedger>(),
            sp.GetRequiredService<PendingQueueService>(),
            sp.GetRequiredService<CliRoute>(),
            sp.GetRequiredService<ChatWorktree>(),
            sp.GetRequiredService<ProviderMigration>(),
            sp.GetRequiredService<ChatNaming>(),
            sp.GetRequiredService<ChatVisibility>(),
            sp.GetRequiredService<ChatSettings>(),
            sp.GetRequiredService<McpEndpoint>(),
            sp.GetRequiredService<BuiltinMcpContexts>(),
            sp.GetRequiredService<PersonaLayerBuilder>(),
            sp.GetRequiredService<ExternalMcpContexts>(),
            sp.GetRequiredService<ModulesMcpContexts>(),
            sp.GetRequiredService<TurnPassports>(),
            sp.GetRequiredService<TurnProcessHost>(),
            sp.GetRequiredService<TurnRecovery>(),
            sp.GetRequiredService<ChatCreation>(),
            sp.GetRequiredService<ChatDeletion>(),
            sp.GetRequiredService<TurnCompletion>(),
            sp.GetRequiredService<TurnIntake>(),
            sp.GetRequiredService<ProjectManager>(),
            sp.GetRequiredService<ChatHistoryService>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<PersonaManager>(),
            sp.GetRequiredService<ILogger<SessionManager>>(),
            sp.GetRequiredService<ISessionBroadcaster>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp,
            sp.GetRequiredService<ClaudeHomeServer.Services.Turn.ITurnEventBus>(),
            sp.GetRequiredService<AppSettingsService>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Execution.SandboxManager>(),
            // Реестр хост просит обязательно: он нужен ролям ядра (SessionCoreRegistration),
            // в тестовом контейнере вертикаль llm всегда включена.
            sp.GetRequiredService<LlmProviderRegistry>(),
            sp.GetService<ClaudeHomeServer.Services.Team.TeamPlanningService>(),
            sp.GetService<TaskManager>(),
            sp.GetService<ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog>());
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
