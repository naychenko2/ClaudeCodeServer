using System.Collections.Concurrent;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Конвейер пантеона: порядок фаз, круги доработки по REJECT, двойной REJECT → error без
// исполнения, материализация ролей, отмена. Исполнение (execute) заменено фейком-seam'ом
// ExecuteAsync — реальный ход claude в юнит-тесте не спавним.
public class PersonaPipelineServiceTests : IDisposable
{
    // Фейковый one-shot: журнал промптов + программируемое поведение (как в meeting-тестах)
    private sealed class FakeRunner : IOneShotRunner
    {
        public readonly ConcurrentQueue<string> Prompts = new();
        public Func<string, CancellationToken, Task<string>> Behavior = (_, _) => Task.FromResult("ответ");
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Prompts.Enqueue(prompt);
            return Behavior(prompt, ct);
        }
    }

    // Подмена execute-фазы: фиксируем вызов вместо реального хода
    private sealed class TestablePipeline(SessionManager s, PersonaManager p, PersonaAskService a,
        IConfiguration c, NullLogger<PersonaPipelineService> l) : PersonaPipelineService(s, p, a, c, l)
    {
        public volatile bool Executed;
        public string? ExecutorId;
        public string? ExecutedPlan;
        internal override Task ExecuteAsync(string sessionId, string ownerId, Persona executor,
            string task, string plan, bool isGroup)
        {
            Executed = true;
            ExecutorId = executor.Id;
            ExecutedPlan = plan;
            return Task.CompletedTask;
        }
    }

    private const string M_Analysis = "Проанализируй задачу перед планированием";
    private const string M_Plan = "полным";
    private const string M_Review = "Отревьюй план";

    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;
    private readonly FakeRunner _runner = new();
    private readonly TestablePipeline _sut;

    public PersonaPipelineServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pipe_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Persona:AskTimeoutMs"] = "5000",
                ["Persona:PipelineTimeoutMs"] = "30000",
            })
            .Build();

        _users = new UserStore(config, NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, _users, appSettings);
        var history = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new LlmProviderRegistry(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(_users);
        var notesSvc = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        _personas = new PersonaManager(config);
        var personaMemory = new PersonaMemoryService(knowledge, _personas, _users, config,
            NullLogger<PersonaMemoryService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), flags, _users, config, NullLogger<PersonaBindingsService>.Instance);
        _sessions = new SessionManager(_projects, hub.Object, history, config, adapters, falCost,
            usage, appSettings, _users, jwt, server.Object, llmProviders, notesKb, flags,
            _personas, personaMemory, bindings, promptBuilder, NullLogger<SessionManager>.Instance);

        var ask = new PersonaAskService(personaMemory, promptBuilder, _runner, config,
            NullLogger<PersonaAskService>.Instance);
        _sut = new TestablePipeline(_sessions, _personas, ask, config,
            NullLogger<PersonaPipelineService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<(User User, Session Session)> MkFixtureAsync(string suffix)
    {
        var user = _users.Add("pipe-user-" + suffix, "pw-123456", "user");
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + suffix)).FullName;
        var project = _projects.Create("PIPE-" + suffix, dir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        return (user, session);
    }

    private async Task<string> StartAndWaitAsync(string ownerId, string sessionId,
        string task = "Сделать экспорт отчётов", string? executor = "omo-hephaestus")
    {
        var id = _sut.Start(ownerId, sessionId, task, executor);
        await _sut.WhenDoneAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(20));
        return id;
    }

    private List<StoredPipelinePhaseMessage> PhaseCards(string sessionId) =>
        _sessions.GetHistoryAsync(sessionId).GetAwaiter().GetResult()
            .OfType<StoredPipelinePhaseMessage>().ToList();

    [Fact]
    public async Task HappyPath_ФазыВПорядке_ИсполнениеЗапущено()
    {
        var (user, session) = await MkFixtureAsync("happy");

        await StartAndWaitAsync(user.Id, session.Id);

        // Порядок промптов: анализ → план → ревью
        var prompts = _runner.Prompts.ToList();
        prompts.Should().HaveCount(3);
        prompts[0].Should().Contain(M_Analysis);
        prompts[1].Should().Contain(M_Plan);
        prompts[2].Should().Contain(M_Review);

        // Карточки фаз в истории: analysis, plan, review, execute
        PhaseCards(session.Id).Select(c => c.Phase).Should().Equal(
            PersonaPipelineService.PhaseAnalysis, PersonaPipelineService.PhasePlan,
            PersonaPipelineService.PhaseReview, PersonaPipelineService.PhaseExecute);

        // Исполнение состоялось, исполнитель — Гефест
        _sut.Executed.Should().BeTrue();
        var hephaestus = _personas.GetByTemplateKey(user.Id, "omo-hephaestus");
        _sut.ExecutorId.Should().Be(hephaestus!.Id);
    }

    [Fact]
    public async Task МатериализуетРолиПантеона()
    {
        var (user, session) = await MkFixtureAsync("mat");

        await StartAndWaitAsync(user.Id, session.Id, executor: "omo-sisyphus");

        foreach (var key in new[] { "omo-metis", "omo-prometheus", "omo-momus", "omo-sisyphus" })
            _personas.GetByTemplateKey(user.Id, key).Should().NotBeNull($"роль {key} материализована");
        _sut.ExecutorId.Should().Be(_personas.GetByTemplateKey(user.Id, "omo-sisyphus")!.Id);
    }

    [Fact]
    public async Task Reject_ПланДорабатывается_ЗатемOkay()
    {
        var (user, session) = await MkFixtureAsync("reject1");
        var reviewCalls = 0;
        _runner.Behavior = (prompt, _) =>
        {
            if (prompt.Contains(M_Review))
                return Task.FromResult(++reviewCalls == 1
                    ? "[REJECT] Задача 2 ссылается на несуществующий файл."
                    : "[OKAY] План исполним.");
            return Task.FromResult("текст");
        };

        await StartAndWaitAsync(user.Id, session.Id);

        // Два круга plan+review: analysis, plan, review, plan(2), review(2), execute
        var cards = PhaseCards(session.Id);
        cards.Select(c => c.Phase).Should().Equal(
            "analysis", "plan", "review", "plan", "review", "execute");
        cards[3].Round.Should().Be(2, "второй план — круг доработки");
        _sut.Executed.Should().BeTrue("после OKAY исполнение запускается");
    }

    [Fact]
    public async Task ДвойнойReject_Error_БезИсполнения()
    {
        var (user, session) = await MkFixtureAsync("reject2");
        _runner.Behavior = (prompt, _) => Task.FromResult(
            prompt.Contains(M_Review) ? "[REJECT] Блокер не устранён." : "текст");

        await StartAndWaitAsync(user.Id, session.Id);

        // 2 круга: analysis, plan, review, plan, review — и стоп (execute нет)
        PhaseCards(session.Id).Select(c => c.Phase).Should().Equal(
            "analysis", "plan", "review", "plan", "review");
        _sut.Executed.Should().BeFalse("план не прошёл ревью — исполнение не запускалось");
    }

    [Fact]
    public async Task АнализУпалДоКонца_БезИсполнения()
    {
        var (user, session) = await MkFixtureAsync("failanalysis");
        _runner.Behavior = (prompt, _) => prompt.Contains(M_Analysis)
            ? throw new InvalidOperationException("модель молчит")
            : Task.FromResult("текст");

        await StartAndWaitAsync(user.Id, session.Id);

        _sut.Executed.Should().BeFalse();
        // Осталась только карточка упавшего анализа
        PhaseCards(session.Id).Select(c => c.Phase).Should().Equal("analysis");
    }

    [Fact]
    public async Task НеверныйИсполнитель_Исключение()
    {
        var (user, session) = await MkFixtureAsync("badexec");
        var act = () => _sut.Start(user.Id, session.Id, "задача", "omo-oracle");
        act.Should().Throw<InvalidOperationException>().WithMessage("*omo-sisyphus*");
    }

    [Fact]
    public async Task ПовторныйЗапуск_ВТомЖеЧате_Исключение()
    {
        var (user, session) = await MkFixtureAsync("dup");
        // Долгий раннер, чтобы первый конвейер не успел завершиться
        var gate = new TaskCompletionSource();
        _runner.Behavior = async (_, _) => { await gate.Task; return "текст"; };

        _sut.Start(user.Id, session.Id, "задача", "omo-hephaestus");
        var act = () => _sut.Start(user.Id, session.Id, "другая", "omo-hephaestus");
        act.Should().Throw<InvalidOperationException>().WithMessage("*уже идёт*");

        gate.SetResult();
        await _sut.WhenDoneAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(20));
    }
}
