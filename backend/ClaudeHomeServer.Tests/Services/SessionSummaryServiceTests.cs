using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тесты чистой логики «Итога сессии»: сборка транскрипта из StoredMessage и заголовок.
// Полный пайплайн (one-shot claude) требует claude.exe и здесь не гоняется.
public class SessionSummaryServiceTests
{
    // ─── BuildTranscript ─────────────────────────────────────────────────────

    [Fact]
    public void BuildTranscript_РепликиИИнструменты_ВПравильномФормате()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("Сделай фичу"),
            new StoredToolUseMessage { Name = "Read" },
            new StoredFileChangedMessage("src/a.ts", 10, 2),
            new StoredTextMessage("Готово, фича сделана"),
            new StoredThinkingMessage("внутренние размышления"),
            new StoredResultMessage("success", 100, 1),
        };

        var t = SessionSummaryService.BuildTranscript(messages, 10_000);

        t.Should().Contain("Пользователь:").And.Contain("Сделай фичу");
        t.Should().Contain("AI:").And.Contain("Готово, фича сделана");
        t.Should().Contain("[инструмент Read]");
        t.Should().Contain("[изменён файл src/a.ts +10/-2]");
        // thinking и метаданные result в транскрипт не попадают
        t.Should().NotContain("размышления").And.NotContain("success");
    }

    [Fact]
    public void BuildTranscript_ПустаяЛента_ПустаяСтрока()
    {
        SessionSummaryService.BuildTranscript([], 10_000).Should().BeEmpty();
    }

    [Fact]
    public void BuildTranscript_ПереполнениеБюджета_ГоловаПлюсХвост()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("НАЧАЛО " + new string('а', 5000)),
            new StoredTextMessage(new string('б', 5000) + " КОНЕЦ"),
        };

        var t = SessionSummaryService.BuildTranscript(messages, 1000);

        t.Length.Should().BeLessThan(1100); // бюджет + маркер сокращения
        t.Should().StartWith("Пользователь:").And.Contain("НАЧАЛО");
        t.Should().Contain("[…транскрипт сокращён…]");
        t.Should().EndWith("КОНЕЦ");
    }

    [Fact]
    public void BuildTranscript_ПустыеРеплики_Пропускаются()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("   "),
            new StoredTextMessage(""),
        };
        SessionSummaryService.BuildTranscript(messages, 1000).Should().BeEmpty();
    }

    // ─── BuildTitle ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildTitle_СИменемСессии()
    {
        var s = new Session { Name = "Рефакторинг заметок" };
        SessionSummaryService.BuildTitle(s).Should()
            .StartWith("Итог: Рефакторинг заметок · ");
    }

    [Fact]
    public void BuildTitle_БезИмени_Чат()
    {
        SessionSummaryService.BuildTitle(new Session()).Should().StartWith("Итог: чат · ");
    }

    [Fact]
    public void BuildTitle_ДлинноеИмя_Обрезается()
    {
        var s = new Session { Name = new string('х', 100) };
        var title = SessionSummaryService.BuildTitle(s);
        title.Should().Contain("…");
        title.Length.Should().BeLessThan(90);
    }

    // ─── SummarizeAsync: проверка гейта подсистемы Notes ────────────────────

    // Подсистема Notes отключена (notes = null). Проверка в SummarizeAsync должна
    // стоять ДО платного cheap.RunAsync: иначе пользователь платит за LLM-конспект,
    // который некуда сохранить, и тот гарантированно выбрасывается (эталон —
    // DailyBriefingService.BuildAndWriteAsync).
    [Fact]
    public async Task Summarize_NotesDisabled_LlmНеВызывается_503Возвращается()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "summary_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = BuildPipeline(tempDir);
            var runner = new CountingRunner("сводка");
            var sut = new SessionSummaryService(
                ctx.Sessions, ctx.Projects, runner,
                ctx.Broadcaster, ctx.Notif, ctx.Config,
                NullLogger<SessionSummaryService>.Instance,
                notes: null, kb: null); // ← подсистема Notes выключена
            var chat = NewProjectChatWithHistory(ctx);

            var act = () => sut.SummarizeAsync(ctx.OwnerId, chat.Id, CancellationToken.None);

            // SummaryUnavailableException → контроллер мапит в 503 + reason="notes_disabled"
            // (см. SessionSummaryController) — тот же контракт, что у «Утреннего брифа».
            // Тип важен: SummaryGenerationException ушёл бы в 502 «повтори», а повторять
            // при выключенной подсистеме бессмысленно.
            await act.Should().ThrowExactlyAsync<SummaryUnavailableException>()
                .WithMessage("*«Заметки» отключена*");
            runner.Calls.Should().Be(0,
                "при выключенной Notes платный LLM-вызов не должен случаться — " +
                "конспект некуда сохранить, и это известно ещё до первого токена");
            // inFlight снимается в finally, иначе следующий клик получит 409 навсегда
            var second = () => sut.SummarizeAsync(ctx.OwnerId, chat.Id, CancellationToken.None);
            // второй вызов должен дойти до той же проверки (а не 409 inFlight),
            // поэтому кидаем и его — runner.Calls всё ещё 0
            await second.Should().ThrowExactlyAsync<SummaryUnavailableException>();
            runner.Calls.Should().Be(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ─── Сборка графа для теста SummarizeAsync ──────────────────────────────

    private sealed record Pipeline(
        SessionManager Sessions, ProjectManager Projects,
        string OwnerId, IConfiguration Config, TestSessionBroadcaster Broadcaster,
        NotificationService Notif);

    private static Pipeline BuildPipeline(string tempDir)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tempDir, "projects.json"),
            // Автосейв выключен (как в SessionManagerTests): фон не вмешивается между
            // правкой и ассертами
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(tempDir, "claude-profile"),
        }).Build();

        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(users);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projects, wkStore,
            knowledge, new SkillsService(), users, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        var sessions = new SessionManager(projects, history, config, adapters, falCost,
            usage, appSettings, users, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster: broadcaster);
        var notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var push = new PushService(config,
            new PushSubscriptionStore(config), jwt, NullLogger<PushService>.Instance);
        var notif = new NotificationService(notifStore, broadcaster, push, personas, projects,
            NullLogger<NotificationService>.Instance);

        var owner = users.GetFirst()!;
        return new Pipeline(sessions, projects, owner.Id, config, broadcaster, notif);
    }

    private static Session NewProjectChatWithHistory(Pipeline ctx)
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            (string)ctx.Config["DefaultProjectsPath"]!,
            "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = ctx.Projects.Create("Проект", dir, ctx.OwnerId, "user");
        // История обязана лежать на диске ДО создания чата: StartNewSessionAsync читает её
        // один раз в Accumulator и с диска больше не перечитывается
        var csid = "summary_hist_" + Guid.NewGuid().ToString("N");
        var history = new ChatHistoryService(ctx.Config);
        history.SaveAsync(csid,
        [
            new StoredUserMessage("Сделай X"),
            new StoredTextMessage("Готово, X сделано"),
        ]).GetAwaiter().GetResult();
        return ctx.Sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid)
            .GetAwaiter().GetResult();
    }

    // Стаб раннера: считает вызовы, помнит параметры последнего
    private sealed class CountingRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public string Answer = answer;
        public string? LastActionKey;
        public string? LastOwnerId;

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastOwnerId = ownerId;
            return Task.FromResult(Answer);
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(Answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
