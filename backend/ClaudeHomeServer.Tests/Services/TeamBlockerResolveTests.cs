using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Волна 1 задачи team-blocker-honest: карточка блокера гаснет по факту снятия штабом.
// Проверяет шесть сигналов единой точки TryResolveBlockerByFactAsync:
// 1) tasks_update задачи-блокера,
// 2) tasks_complete той же задачи,
// 3) tasks_run_executor после успешного ExecuteAsync,
// 4) SubtaskDropHandler (drop по кнопке «Снять»),
// 5) chats_send в дочерний чат задачи,
// 6) маркер <team:resolved>суть</team> в ходе координатора.
//
// Плюс три инварианта: «две открытые карточки — стадия держится до снятия второй»,
// «блокер в последней волне при волнах 4/4 будит координатора» (квота TryConsumeTeamWakeup),
// «ответ сообщением гасит карточку» (ResumeTeamFromDecisionOnUserInput).
//
// Парсер маркера разрезает маркер в код-блоке как цитату (команда Киры, волна 1 —
// содержимое НЕ идёт в reason), и принимает его вне код-блока как активный вызов.
[Collection(TestCollections.SessionStaticResolvers)]
public class TeamBlockerResolveTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly TestSessionBroadcaster _broadcaster = new();

    private const string UserId = "user-1";
    private const string Username = "tester";

    public TeamBlockerResolveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "team_blocker_resolve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, userStore, appSettings);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);
        _sessions = CreateSessionManager(config, userStore, appSettings);
    }

    public void Dispose()
    {
        _sessions.KillAllProcesses();
        Helpers.TestFs.DeleteDirectoryResilient(_dir);
        GC.SuppressFinalize(this);
    }

    private SessionManager CreateSessionManager(IConfiguration config, UserStore userStore,
        AppSettingsService appSettings)
    {
        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new SkillsService(),
            new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(userStore);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(),
            userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var history = new ChatHistoryService(config);
        var planning = new TeamPlanningService(_personas, new StubPlanner(() => "{}"));
        return new SessionManager(_projects, history, config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, flags, _personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, teamPlanning: planning,
            broadcaster: _broadcaster);
    }

    // Штаб с включённым режимом в Planning — для тестов триггера блокера достаточно одного
    // чата-штаба, без плана и волны
    private async Task<(Session Session, Persona Backend)> MakeStabAsync(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var project = _projects.Create(name, dir, UserId, Username);

        var coordinator = _personas.Create(UserId, "Алекс", "Тимлид",
            null, null, null, null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var backend = _personas.Create(UserId, "Денис", "Backend-разработчик",
            null, null, null, null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);

        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto,
            personaId: coordinator.Id);
        await _sessions.SetTeamImplementAsync(session.Id, enabled: true,
            coordinatorPersonaId: coordinator.Id, userId: UserId);
        return (_sessions.GetById(session.Id)!, backend);
    }

    // Прямой путь публикации карточки — через каст ITeamHistoryStore, как в TeamWaveServiceTests.
    // Без EscalationRaiser: тест не зависит от нотификаций, проверяем только гашение.
    private async Task<TeamEscalation> PublishBlockerAsync(Session stabSession, string taskId, int wave = 1)
    {
        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял",
            Details = "нужна помощь",
            TaskId = taskId,
            Wave = wave,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        var stab = _sessions.GetById(stabSession.Id)!;
        await ((ITeamHistoryStore)_sessions).PublishTeamEscalationAsync(stab.Id, card);
        return card;
    }

    [Fact]
    public async Task TryResolveBlockerByFact_ГаситКарточкуБлокера()
    {
        var (stab, _) = await MakeStabAsync("resolve-basic");
        // Создаём задачу в штабе
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест", Description: "", Assignee: TaskItemAssignee.Claude), null);
        // Имитация источника из штаба — без этого погашение не найдёт чат-штаб
        _tasks.Update(task.Id, new UpdateTaskRequest());

        // Публикуем открытую блокер-карточку
        await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        // Сигнал: имитация того, что штаб переписал задачу. Напрямую зовём единую точку
        var ok = await _sessions.TryResolveBlockerByFactAsync(stab.Id, task.Id, "штаб переписал задачу");
        ok.Should().BeTrue("открытая блокер-карточка должна быть погашена");

        var open = await _sessions.ListOpenEscalationsAsync(stab.Id);
        open.Should().BeEmpty("единственная открытая карточка — блокер — должна быть снята");
        stab.TeamImplement!.Stage.Should().NotBe(TeamImplementStage.AwaitingDecision);
    }

    [Fact]
    public async Task TryResolveBlockerByFact_ДвеОткрытыеКарточки_СтадияДержитсяДоСнятияВторой()
    {
        var (stab, _) = await MakeStabAsync("resolve-multi");
        var task1 = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест1", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task1.Id, new UpdateTaskRequest());
        var task2 = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест2", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task2.Id, new UpdateTaskRequest());

        // Две открытые блокер-карточки на РАЗНЫЕ задачи: стадия держится в AwaitingDecision,
        // пока обе решающие открыты
        await PublishBlockerAsync(stab, task1.Id);
        await PublishBlockerAsync(stab, task2.Id);

        // Первое гашение — есть ещё одна открытая блокер-карточка, стадия держится
        await _sessions.TryResolveBlockerByFactAsync(stab.Id, task1.Id, "первое");
        var openAfterFirst = await _sessions.ListOpenEscalationsAsync(stab.Id);
        openAfterFirst.Should().HaveCount(1, "вторая карточка блокера по task2 ещё открыта");
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        // Второе гашение — открытых решающих карточек больше нет, стадия возвращается
        await _sessions.TryResolveBlockerByFactAsync(stab.Id, task2.Id, "второе");
        var openAfterSecond = await _sessions.ListOpenEscalationsAsync(stab.Id);
        openAfterSecond.Should().BeEmpty();
        stab.TeamImplement!.Stage.Should().NotBe(TeamImplementStage.AwaitingDecision);
    }

    [Fact]
    public async Task TryConsumeTeamWakeup_БлокерВПоследнейВолне4из4_БудитКоординатора()
    {
        // Квота пробуждений не должна отбивать блокер из-за исчерпания волн. Проверяем
        // отдельный счётчик WakeupsUsed — он у нас отдельный от лимита волн
        var (stab, _) = await MakeStabAsync("wakeup-last-wave");
        ((ITeamRunState)_sessions).WithTeamState(stab.Id, t =>
        {
            t.WaveNumber = 4;
            t.ClosedWave = 4;
            t.PlannedWaves = 4;
            t.Budget.WavesUsed = t.Budget.MaxWaves; // лимит волн выбран
            return true;
        });

        // Без срабатывания квоты пробуждений
        var result = _sessions.TryConsumeTeamWakeup(stab.Id);
        result.Allowed.Should().BeTrue("блокер должен доходить до координатора и при исчерпанных волнах");
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task TryConsumeTeamWakeup_ОтбиваетПоWakeupsMaxed_ТекстПроПробуждения()
    {
        var (stab, _) = await MakeStabAsync("wakeup-stop");
        ((ITeamRunState)_sessions).WithTeamState(stab.Id, t =>
        {
            t.Budget.WakeupsUsed = t.Budget.MaxWakeups; // лимит пробуждений выбран
            return true;
        });

        var stopped = _sessions.TryConsumeTeamWakeup(stab.Id);
        stopped.Allowed.Should().BeFalse("пробуждения выбраны — отказ");
        stopped.Reason.Should().NotBeNull();

        // Проверка: текст причины — про пробуждения, а не про общий бюджет
        stopped.Reason.Should().Contain("срочных вызовов");
    }

    [Fact]
    public async Task TryConsumeTeamImplementRun_ОтказПоAwaitingDecisionТолькоЕслиЕстьНеблокер()
    {
        var (stab, _) = await MakeStabAsync("run-awaiting");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());

        // Только блокер — координатор сам снимает, гейт не должен отбивать
        await PublishBlockerAsync(stab, task.Id);

        var (verdict, reason) = _sessions.TryConsumeTeamImplementRun(stab.Id, UserId);
        // Cast через (int): два enum'а с одинаковыми значениями, см. комментарий ядра
        ((SessionManager.TeamRunQuota)(int)verdict).Should().Be(SessionManager.TeamRunQuota.Allowed,
            "в стадии AwaitingDecision с одним блокером координатор сам разбирается — запуск разрешён");
        reason.Should().BeNull();
    }

    [Fact]
    public async Task TryConsumeTeamImplementRun_ОтказПоНеблокеру_ТекстЧестный()
    {
        var (stab, _) = await MakeStabAsync("run-awaiting-nonblocker");
        // Не-блокер карточка — Stopped: кнопка «Остановить»
        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.Stopped,
            Title = "Практика остановлена",
            Details = "",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Stopped),
        };
        await ((ITeamHistoryStore)_sessions).PublishTeamEscalationAsync(stab.Id, card);

        var (verdict, reason) = _sessions.TryConsumeTeamImplementRun(stab.Id, UserId);
        ((SessionManager.TeamRunQuota)(int)verdict).Should().Be(SessionManager.TeamRunQuota.Exhausted,
            "есть открытая не-блокер карточка — отказ");
        reason.Should().NotBeNull();
    }

    [Fact]
    public async Task ОтветСообщением_ГаситОткрытуюКарточкуБлокера()
    {
        var (stab, _) = await MakeStabAsync("message-resolves");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());

        await PublishBlockerAsync(stab, task.Id);

        // Ответ человека сообщением (ResumeTeamFromDecisionOnUserInput) — приватный
        // метод ядра, проверяем через рефлексию — единственный путь, раз он private
        var method = typeof(SessionManager).GetMethod("ResumeTeamFromDecisionOnUserInput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var entryField = typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var entryDict = (System.Collections.IDictionary)entryField.GetValue(_sessions)!;
        var sessionEntry = entryDict[stab.Id]!;
        await (Task)method.Invoke(_sessions, new object[] { stab.Id, sessionEntry })!;

        var open = await _sessions.ListOpenEscalationsAsync(stab.Id);
        open.Should().BeEmpty("ответ сообщением гасит открытую карточку блокера");
        stab.TeamImplement!.Stage.Should().NotBe(TeamImplementStage.AwaitingDecision);
    }

    // Парсер маркера: вне код-блока маркер активен, внутри — нет (это цитата)
    [Fact]
    public void ParseResolvedMarker_ВнеКодБлока_ВозвращаетСуть()
    {
        var text = "Координатор снял блокер. <team:resolved>задача переписана</team>";
        var note = TeamProtocolMarkers.ParseResolvedMarker(text);
        note.Should().Be("задача переписана");
    }

    [Fact]
    public void ParseResolvedMarker_ВКодБлоке_ЦитатаНеСрабатывает()
    {
        var text = "Для справки: `<team:resolved>пример</team>` — синтаксис маркера.";
        var note = TeamProtocolMarkers.ParseResolvedMarker(text);
        note.Should().BeNull("маркер внутри кода — это цитата протокола, а не активный вызов");
    }

    [Fact]
    public void ParseResolvedMarker_ЗакрытиеПоИмениТерпится()
    {
        // XML-привычка модели — закрывает тег по имени вместо </team:resolved>
        var text = "Готово. <team:resolved>блокер снят</team:resolved>";
        var note = TeamProtocolMarkers.ParseResolvedMarker(text);
        note.Should().Be("блокер снят");
    }

    [Fact]
    public void StripTeamProtocolMarkers_ВырезаетResolvedКакИПрочие()
    {
        var text = "Текст. <team:resolved>суть</team> Хвост.";
        var stripped = TeamProtocolMarkers.StripTeamProtocolMarkers(text);
        stripped.Should().NotContain("team:resolved");
        stripped.Should().NotContain("суть");
        stripped.Should().Contain("Текст.");
        stripped.Should().Contain("Хвост.");
    }

    // Заглушка планировщика: тесты не вызывают планирование, но SessionManager требует
    // ссылку на ICheapTextRunner в конструкторе
    private sealed class StubPlanner(Func<string> answer) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(answer());

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer());

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}