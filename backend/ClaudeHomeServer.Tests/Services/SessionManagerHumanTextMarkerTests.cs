using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Шаг 1 сжатия, доработка 2: маркер реплики человека (ccs:human-text) ставится BuildCliTurnText
// ТОЛЬКО на прозу человека — не на слэш-команды/скиллы (разворот скилла важнее) и не на
// не-человеческие ходы (auto, systemDirective, реплики персон). Интеграция через реальный
// SendDirectAsync → BuildCliTurnText: подставной адаптер ловит текст, который реально уехал бы
// в CLI. Мутация «маркер всегда» (безусловный WithHumanTextMarker) краснит все False-ветки.
// Лента (аккумулятор) и history.json маркера не содержат: туда уходит сырой text, а маркер —
// только в текст для CLI (minor-1 Глеба).
public class SessionManagerHumanTextMarkerTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly SessionManager _sut;
    private readonly TestSessionBroadcaster _broadcaster;

    public SessionManagerHumanTextMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_marker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Session:AutoSaveSeconds"] = "0",
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
                ["Delivery:AwaitProcessExitSeconds"] = "0"
            })
            .Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);
        _broadcaster = new TestSessionBroadcaster();

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var activity = new SubscriptionActivityTracker();
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var actionOverrides = new ClaudeHomeServer.Services.Llm.LocalActionOverridesStore(config);
        var assignments = new ClaudeHomeServer.Services.Llm.ModelAssignmentResolver(appSettings, actionOverrides,
            new ClaudeHomeServer.Services.Llm.UserModelTierResolver(userStore, appSettings));
        var teamPlanning = new TeamPlanningService(personas);

        _sut = new SessionManager(_projectManager, _historyService, config, adapters, falCost, usage, appSettings,
            userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            git: new ClaudeHomeServer.Services.Git.GitService(TestLauncherFactory.Instance),
            assignments: assignments, teamPlanning: teamPlanning, activity: activity,
            subagentRuns: new ClaudeHomeServer.Services.Llm.Claude.SubagentRunLog(),
            broadcaster: _broadcaster);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // --- Вспомогательные ---

    private object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (System.Collections.IDictionary)field.GetValue(_sut)!;
        return sessions[sessionId]!;
    }

    // Свободный чат (Active) с подставным адаптером: ход идёт прямо в процесс (SendDirectAsync →
    // BuildCliTurnText), адаптер ловит текст, который реально уехал бы в CLI. Возвращает сессию
    // и Func, отдающий последний уехавший в адаптер текст.
    private async Task<(Session Session, Func<string?> SentText)> MkMarkerChatAsync(string suffix)
    {
        var dir = Path.Combine(_tempDir, "proj_" + suffix);
        Directory.CreateDirectory(dir);
        var project = _projectManager.Create("M-" + suffix, dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Status = SessionStatus.Active;
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель

        var entry = GetEntry(session.Id);
        var adapter = new Mock<ILlmSessionAdapter>();
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        adapter.SetupGet(a => a.Info).Returns(info);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        string? sent = null;
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Callback<string, IReadOnlyList<string>?, int, bool, string?>((t, _, _, _, _) => sent = t)
            .Returns(Task.CompletedTask);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        entry.GetType().GetField("RunId")!.SetValue(entry, 1L);

        return (session, () => sent);
    }

    // --- Тесты: маркер только на прозу человека ---

    // Находка Глеба: /skill-name arg (реплика человека) → в адаптер уходит БЕЗ маркера, текст
    // начинается с '/skill-name' — именно его TryExpandSkill разворачивает. Мутация «маркер
    // всегда» добавила бы маркер первой строкой → тест краснеет.
    [Fact]
    public async Task СлэшКоманда_БезМаркера_ТекстНачинаетсяСКоманды()
    {
        var (session, sentText) = await MkMarkerChatAsync("slash");
        await _sut.SendMessageAsync(session.Id, "/review src/Foo.cs", []);

        var sent = sentText()!;
        sent.Should().NotContain("ccs:human-text", "маркер не должен ломать разворот скилла");
        sent.Should().StartWith("/review", "ведущий '/' сохранён — TryExpandSkill его увидит");
    }

    // Ведущие пробелы перед '/' — та же команда: TrimStart() снимает их, маркера нет.
    [Fact]
    public async Task ВедущиеПробелыПередСлэш_БезМаркера()
    {
        var (session, sentText) = await MkMarkerChatAsync("spaces");
        await _sut.SendMessageAsync(session.Id, "   /review src/Foo.cs", []);

        sentText()!.Should().NotContain("ccs:human-text");
    }

    // auto (автоматика) → без маркера.
    [Fact]
    public async Task Auto_БезМаркера()
    {
        var (session, sentText) = await MkMarkerChatAsync("auto");
        await _sut.SendMessageAsync(session.Id, "сделай задачу", [], auto: true);

        sentText()!.Should().NotContain("ccs:human-text");
    }

    // systemDirective (цикл «до готово», доклады) → без маркера: «human-text» не накрывает директивы.
    [Fact]
    public async Task SystemDirective_БезМаркера()
    {
        var (session, sentText) = await MkMarkerChatAsync("directive");
        await _sut.SendMessageAsync(session.Id, "сделай задачу", [], systemDirective: true);

        sentText()!.Should().NotContain("ccs:human-text");
    }

    // Реплика персоны (групповой чат) → без маркера.
    [Fact]
    public async Task SenderPersonaId_БезМаркера()
    {
        var (session, sentText) = await MkMarkerChatAsync("persona");
        await _sut.SendMessageAsync(session.Id, "сделай задачу", [], senderPersonaId: "p-1");

        sentText()!.Should().NotContain("ccs:human-text");
    }

    // Обычная реплика человека → маркер стоит, N и sha только по сырому тексту (контракт шага 1).
    [Fact]
    public async Task ПрозаЧеловека_МаркерПоСыромуТексту()
    {
        const string raw = "сделай задачу 🚀";
        var (session, sentText) = await MkMarkerChatAsync("prose");
        await _sut.SendMessageAsync(session.Id, raw, []);

        var sent = sentText()!;
        sent.Should().StartWith("<!-- ccs:human-text ", "маркер стоит первой строкой");
        // N = кодовые точки сырой реплики (🚀 = 1 точка), не всего текста с маркером
        var expectedChars = raw.EnumerateRunes().Count();
        sent.Should().Contain($"chars={expectedChars}");
        sent.Should().EndWith(raw, "сырая реплика идёт сразу после маркера");
    }

    // Лента (аккумулятор) и history.json маркера не содержат: туда уходит сырой text, а маркер —
    // только в текст для CLI (minor-1 Глеба: сейчас такого теста не было).
    [Fact]
    public async Task ЛентаИИстория_БезМаркера()
    {
        const string raw = "сделай задачу";
        var (session, _) = await MkMarkerChatAsync("history");
        session.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        var entry = GetEntry(session.Id);
        var acc = (TurnAccumulator)entry.GetType().GetField("Accumulator")!.GetValue(entry)!;

        await _sut.SendMessageAsync(session.Id, raw, []);

        // Лента: аккумулятор хранит сырую реплику человека, без маркера
        var userMsgs = acc.GetAll().OfType<StoredUserMessage>().ToList();
        userMsgs.Should().Contain(m => m.Text == raw, "лента хранит сырую реплику");
        userMsgs.Should().NotContain(m => m.Text.Contains("ccs:human-text"), "маркер в ленту не уходит");

        // history.json: в живом ходе пишется fire-and-forget на событиях хода, но mock-адаптер
        // сообщений не шлёт — запись не сработает сама. Триггерим её явно (SaveSnapshotAsync) и
        // проверяем файл ЖЁСТКО: он обязан появиться (путь — от SaveKey, а не от ClaudeSessionId)
        // и маркера в нём нет. minor a Глеба: раньше тест молчал (guard `if (File.Exists)`),
        // если файл не появился.
        await acc.SaveSnapshotAsync(_historyService);
        var historyFile = Path.Combine(_tempDir, "sessions", acc.SaveKey!, "history.json");
        File.Exists(historyFile).Should().BeTrue("history.json не записан после SaveSnapshotAsync");
        File.ReadAllText(historyFile).Should().NotContain("ccs:human-text", "маркер в history.json не уходит");
    }
}
