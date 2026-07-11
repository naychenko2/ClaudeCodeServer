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

// Пайплайн совещания персон (P7) с фейковым one-shot раннером: порядок фаз,
// число вызовов (2N+1), деградация при ошибках, отмена, параллелизм ≤3.
public class PersonaMeetingServiceTests : IDisposable
{
    // Маркеры фаз в промптах (см. PersonaMeetingService)
    private const string M1 = "НЕЗАВИСИМУЮ";
    private const string M2 = "Перекрёстная атака";
    private const string M3 = "Дистиллируй итог";

    // Фейковый one-shot: потокобезопасный журнал промптов + программируемое поведение
    private sealed class FakeRunner : IOneShotRunner
    {
        public readonly ConcurrentQueue<string> Prompts = new();
        public Func<string, CancellationToken, Task<string>> Behavior =
            (_, _) => Task.FromResult("ответ");
        public int MaxConcurrency;
        private int _current;

        public string? NormalizeModel(string? model) => model;

        public async Task<string> RunAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Prompts.Enqueue(prompt);
            var now = Interlocked.Increment(ref _current);
            try
            {
                InterlockedMax(ref MaxConcurrency, now);
                return await Behavior(prompt, ct);
            }
            finally { Interlocked.Decrement(ref _current); }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int snapshot;
            while (value > (snapshot = Volatile.Read(ref target)))
                if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot) return;
        }
    }

    private readonly string _tempDir;
    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly FakeRunner _runner = new();
    private readonly PersonaMeetingService _sut;

    public PersonaMeetingServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "meet_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Persona:AskTimeoutMs"] = "5000",
                ["Persona:MeetingTimeoutMs"] = "30000",
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
        _sessions = new SessionManager(_projects, hub.Object, history, config, adapters, falCost,
            usage, appSettings, _users, jwt, server.Object, llmProviders, notesKb, flags,
            _personas, personaMemory, promptBuilder, NullLogger<SessionManager>.Instance);

        var ask = new PersonaAskService(personaMemory, promptBuilder, _runner, config,
            NullLogger<PersonaAskService>.Instance);
        _sut = new PersonaMeetingService(_sessions, _personas, ask, config,
            NullLogger<PersonaMeetingService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Пользователь + проект + N персон с именами Alfa, Beta, Gamma, Delta + сессия проекта
    private async Task<(User User, Session Session, List<Persona> Personas)> MkFixtureAsync(int count, string suffix)
    {
        var user = _users.Add("meet-user-" + suffix, "pw-123456", "user");
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + suffix)).FullName;
        var project = _projects.Create("MEET-" + suffix, dir, user.Id, user.Username);
        string[] names = ["Alfa", "Beta", "Gamma", "Delta"];
        var personas = names.Take(count)
            .Select(n => _personas.Create(user.Id, n, "Эксперт", null, null,
                model: null, effort: null, PersonaScope.Project, project.Id,
                color: null, greeting: null, memoryEnabled: false))
            .ToList();
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        return (user, session, personas);
    }

    private static bool ForPersona(string prompt, string name) => prompt.Contains($"по имени {name}");

    private async Task<string> StartAndWaitAsync(string ownerId, string sessionId,
        List<Persona> personas, string question = "Какой стек выбрать?")
    {
        var id = _sut.Start(ownerId, sessionId, question, personas.Select(p => p.Id).ToList());
        await _sut.WhenDoneAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(20));
        return id;
    }

    [Fact]
    public async Task Полный_пайплайн_порядок_фаз_и_2N_плюс_1_вызовов()
    {
        var (user, session, personas) = await MkFixtureAsync(3, "full");

        await StartAndWaitAsync(user.Id, session.Id, personas);

        var prompts = _runner.Prompts.ToList();
        prompts.Should().HaveCount(7, "N=3: 3 позиции + 3 критики + 1 синтез");
        prompts.Take(3).Should().OnlyContain(p => p.Contains(M1), "первой идёт фаза независимых позиций");
        prompts.Skip(3).Take(3).Should().OnlyContain(p => p.Contains(M2), "второй — перекрёстная критика");
        prompts[6].Should().Contain(M3, "последним — синтез");
        // Синтез — от ведущей (первая в списке)
        ForPersona(prompts[6], "Alfa").Should().BeTrue();

        // Все три фазы легли в историю чата
        var history = await _sessions.GetHistoryAsync(session.Id);
        var cards = history.OfType<StoredMeetingPhaseMessage>().ToList();
        cards.Select(c => c.Phase).Should().Equal(
            PersonaMeetingService.PhaseIndependent,
            PersonaMeetingService.PhaseAttack,
            PersonaMeetingService.PhaseSynthesis);
        cards[0].Entries.Should().HaveCount(3);
        cards[2].Entries.Should().ContainSingle(e => e.PersonaId == personas[0].Id && !e.IsError);
    }

    [Fact]
    public async Task Упавшая_персона_фазы_1_даёт_failed_entry_но_совещание_завершается()
    {
        var (user, session, personas) = await MkFixtureAsync(3, "fail1");
        _runner.Behavior = (prompt, _) =>
            prompt.Contains(M1) && ForPersona(prompt, "Beta")
                ? throw new InvalidOperationException("Claude не ответил за отведённое время")
                : Task.FromResult("ответ");

        await StartAndWaitAsync(user.Id, session.Id, personas);

        // 3 позиции (одна упала) + 2 критики (без выбывшей) + 1 синтез
        _runner.Prompts.Should().HaveCount(6);
        var history = await _sessions.GetHistoryAsync(session.Id);
        var cards = history.OfType<StoredMeetingPhaseMessage>().ToList();
        cards.Should().HaveCount(3, "совещание дошло до синтеза");
        var independent = cards.Single(c => c.Phase == PersonaMeetingService.PhaseIndependent);
        independent.Entries.Should().ContainSingle(e => e.IsError && e.PersonaId == personas[1].Id);
        // Критика — только между живыми, выбывшая не участвует
        var attack = cards.Single(c => c.Phase == PersonaMeetingService.PhaseAttack);
        attack.Entries.Select(e => e.PersonaId).Should().BeEquivalentTo([personas[0].Id, personas[2].Id]);
    }

    [Fact]
    public async Task Меньше_двух_живых_после_фазы_1_совещание_прерывается()
    {
        var (user, session, personas) = await MkFixtureAsync(2, "dead");
        _runner.Behavior = (_, _) => throw new InvalidOperationException("all down");

        await StartAndWaitAsync(user.Id, session.Id, personas);

        _runner.Prompts.Should().HaveCount(2, "только фаза 1 — дальше некому обсуждать");
        var history = await _sessions.GetHistoryAsync(session.Id);
        var cards = history.OfType<StoredMeetingPhaseMessage>().ToList();
        cards.Should().ContainSingle(c => c.Phase == PersonaMeetingService.PhaseIndependent);
        cards.Should().NotContain(c => c.Phase == PersonaMeetingService.PhaseAttack);
        cards.Should().NotContain(c => c.Phase == PersonaMeetingService.PhaseSynthesis);
    }

    [Fact]
    public async Task Cancel_посреди_фазы_2_синтеза_не_будет()
    {
        var (user, session, personas) = await MkFixtureAsync(2, "cancel");
        var phase2Started = new TaskCompletionSource();
        _runner.Behavior = async (prompt, ct) =>
        {
            if (prompt.Contains(M2))
            {
                phase2Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);   // висим до отмены
            }
            return "ответ";
        };

        _sut.Start(user.Id, session.Id, "вопрос?", personas.Select(p => p.Id).ToList());
        await phase2Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _sut.Cancel(session.Id).Should().BeTrue();
        await _sut.WhenDoneAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(10));

        _runner.Prompts.Should().NotContain(p => p.Contains(M3), "после отмены синтез не запускается");
        var history = await _sessions.GetHistoryAsync(session.Id);
        history.OfType<StoredMeetingPhaseMessage>()
            .Should().NotContain(c => c.Phase == PersonaMeetingService.PhaseSynthesis);
    }

    [Fact]
    public async Task Повторный_Start_в_том_же_чате_бросает()
    {
        var (user, session, personas) = await MkFixtureAsync(2, "dup");
        var gate = new TaskCompletionSource();
        _runner.Behavior = async (_, ct) => { await gate.Task.WaitAsync(ct); return "ответ"; };

        _sut.Start(user.Id, session.Id, "вопрос?", personas.Select(p => p.Id).ToList());
        var act = () => _sut.Start(user.Id, session.Id, "ещё раз", personas.Select(p => p.Id).ToList());

        act.Should().Throw<InvalidOperationException>();
        gate.SetResult();
        await _sut.WhenDoneAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Параллелизм_фазы_не_превышает_трёх()
    {
        var (user, session, personas) = await MkFixtureAsync(4, "par");
        _runner.Behavior = async (_, ct) => { await Task.Delay(120, ct); return "ответ"; };

        await StartAndWaitAsync(user.Id, session.Id, personas);

        _runner.Prompts.Should().HaveCount(9, "N=4: 4 + 4 + 1");
        _runner.MaxConcurrency.Should().BeLessThanOrEqualTo(3);
    }
}
