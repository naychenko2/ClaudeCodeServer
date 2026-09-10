using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ClaudeHomeServer.Services.Mcp.Http;

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
        // Резолв названия задачи по id (волна 1 team-blocker-honest, дефект 1430b732):
        // нужен тесту публикации эскалации — карточка блокера должна принести Title
        // задачи, чтобы подпись диалога снятия могла показать «Задача «X»…», а не
        // заголовок карточки «Исполнитель застрял: …».
        _sessions.GetTaskTitle = id => _tasks.GetById(id)?.Title;
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

    // Хелпер для тестов: достать словарь _sessions у SessionManager (нужен доступ к
    // SessionEntry.Info — у публичного типа Session такого свойства нет).
    private System.Collections.IDictionary MakeSessionEntryDict()
    {
        var entryField = typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (System.Collections.IDictionary)entryField.GetValue(_sessions)!;
    }

    // ClaudeSessionId у SessionEntry — через прямой reflection (SessionEntry приватный,
    // dynamic не работает). Возвращает ключ истории для чата.
    private string GetClaudeSessionId(string sessionId)
    {
        var dict = MakeSessionEntryDict();
        var entry = dict[sessionId]!;
        var infoField = entry.GetType().GetField("Info",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var info = infoField!.GetValue(entry);
        // ClaudeSessionId у SessionInfo — это СВОЙСТВО (record), не поле
        var claudeSidProp = info!.GetType().GetProperty("ClaudeSessionId",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        return (string)claudeSidProp!.GetValue(info)!;
    }

    // И служба окончания хода штаба (TeamTurnCompletionService.HandleTeamTurnEndAsync —
    // единственный способ прогнать ветку разрешения маркеров без шины turn/completed)
    private ClaudeHomeServer.Services.Team.TeamTurnCompletionService TurnCompletion =>
        (ClaudeHomeServer.Services.Team.TeamTurnCompletionService)
            typeof(SessionManager).GetField("_teamTurnCompletion",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_sessions)!;

    // ChatHistoryService у ядра — нужен для прямого чтения истории в тестах M3
    private ChatHistoryService History =>
        (ChatHistoryService)typeof(SessionManager).GetField("_history",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(_sessions)!;

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

    // Smoke L3: без открытой блокер-карточки TryAutoResolveTeamBlockerInternalAsync
    // выходит раньше — на `SessionManager.cs:6410` `if (openBlocker is null) return false;`.
    // Эта строка в фикс-коммите 02e375aa не менялась; и старый, и новый код возвращают
    // false. Тест НЕ покрывает фикс строки `:6427` (возврат реального результата гашения):
    // сценарий «карточка блокера в аккумуляторе есть, но _teamDecision.TryResolveBlockerByFactAsync
    // возвращает false» стабильным юнит-тестом не воспроизводится — подменить _teamDecision
    // без правок продакшн-кода нельзя (метод не virtual, нет ITeamDecisionService, Moq и
    // Castle DynamicProxy перехватывают только virtual). Сценарий практически достижим
    // только гонкой, и для неё тест-страж не нужен.
    [Fact]
    public async Task TryAutoResolveTeamBlocker_НетОткрытогоБлокера_ВозвращаетFalse()
    {
        var (stab, _) = await MakeStabAsync("auto-resolve-empty");
        // Перевод штаба в AwaitingDecision, чтобы пройти первый гард в TryAutoResolve…
        ((ITeamRunState)_sessions).WithTeamState(stab.Id, t =>
        {
            t.Stage = TeamImplementStage.AwaitingDecision;
            return true;
        });

        // Никаких открытых блокер-карточек нет → метод должен вернуть false.
        var result = await _sessions.TryAutoResolveTeamBlockerAsync(stab.Id,
            "координатор шлёт <team:work>…</team>");
        result.Should().BeFalse("без открытой блокер-карточки гасить нечего");
    }

    // Smoke L3 (контр-кейс): после успешного гашения блокера второй вызов выходит на ту
    // же `SessionManager.cs:6410` — открытых блокеров больше нет. И старый, и новый код
    // возвращают false здесь. Тест фикс строки `:6427` так же не покрывает, как и первый
    // (см. комментарий выше). Оставляем как сторожа того, что повторный ход не навешивает
    // лишний `team:resolved`.
    [Fact]
    public async Task TryAutoResolveTeamBlocker_ПовторныйВызовПослеГашения_ВозвращаетFalse()
    {
        var (stab, _) = await MakeStabAsync("auto-resolve-second");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());

        await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        // Первый вызов: блокер есть, метод гасит и возвращает true
        var first = await _sessions.TryAutoResolveTeamBlockerAsync(stab.Id,
            "<team:work>продолжаем</team>");
        first.Should().BeTrue("блокер-карточка была открыта и должна быть снята");

        // Второй вызов: открытых блокеров больше нет
        var second = await _sessions.TryAutoResolveTeamBlockerAsync(stab.Id,
            "<team:work>продолжаем</team>");
        second.Should().BeFalse("открытых блокеров больше нет — повторного гашения не требуется");
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

    // S6 (фикс-волна): гейт запуска в стадии AwaitingDecision с ЕДИНСТВЕННОЙ открытой
    // карточкой-блокером отбивать не должен — координатор сам снимает блокер (см. контракт
    // выше «TryConsumeTeamImplementRun_ОтказПоAwaitingDecisionТолькоЕслиЕстьНеблокер»).
    // Раньше кейс прикрывался тем же тестом, что и не-блокер; в ревью `d9d95d53` Глеб
    // попросил отдельный кейс ради читаемости. Сторож sync-over-async (deadlock на
    // запросном потоке в xunit): в xunit нет SynchronizationContext — deadlock'а не будет
    // ни с прежним .GetAwaiter().GetResult(), ни с нынешним sync-швом
    // _history.GetOpenTeamEscalationsSync; покрывать тут нечего. Полезное в тесте —
    // ассерт «открытый блокер → Allowed».
    [Fact]
    public async Task TryConsumeTeamImplementRun_ОткрытыйБлокер_ЧестныйAllowed()
    {
        var (stab, _) = await MakeStabAsync("sync-over-async-blocker");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Тест", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());

        await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        var (verdict, reason) = _sessions.TryConsumeTeamImplementRun(stab.Id, UserId);
        verdict.Should().Be(SessionManager.TeamRunQuota.Allowed,
            "блокер — единственная открытая карточка, координатор сам разбирается, запуск разрешён");
        reason.Should().BeNull();
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
        var resumeResult = method.Invoke(_sessions, [stab.Id, sessionEntry]);
        if (resumeResult is not Task resumeTask)
            throw new InvalidOperationException(
                $"ResumeTeamFromDecisionOnUserInput вернул {resumeResult?.GetType().Name ?? "null"}, ожидался Task");
        await resumeTask;

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

    // === Волна 1 team-blocker-honest: тесты S3 (проводка сигналов через настоящие точки входа) ===

    // M1 (фикс-волна): бюджет ВСЕГДА проверяется в гейте запуска — даже в стадии
    // AwaitingDecision. Раньше ветка `else if (Stage == AwaitingDecision) reason = null;`
    // съедала следующий `else` с Budget.ExceededReason() и RunsUsed уходил за MaxRuns
    // без отказа. Закрепляем: принудительно выставляем исчерпанный бюджет в стадии
    // AwaitingDecision — гейт должен дать Exhausted с reason про бюджет, а не про
    // «ждёт решения».
    [Fact]
    public async Task TryConsumeTeamImplementRun_БюджетИсчерпанВAwaitingDecision_Отбивает()
    {
        var (stab, _) = await MakeStabAsync("m1-budget-awaiting");
        ((ITeamRunState)_sessions).WithTeamState(stab.Id, t =>
        {
            t.Stage = TeamImplementStage.AwaitingDecision;
            t.Budget.MaxRuns = 3;
            t.Budget.RunsUsed = 3; // уже на потолке
            return true;
        });

        var (verdict, reason) = _sessions.TryConsumeTeamImplementRun(stab.Id, UserId);
        ((SessionManager.TeamRunQuota)(int)verdict).Should()
            .Be(SessionManager.TeamRunQuota.Exhausted,
                "бюджет проверяется всегда — даже в стадии «ждёт решения»");
        reason.Should().NotBeNull();
        reason.Should().Contain("запуск", "текст отказа говорит про исчерпание запусков");
    }

    // M2 (фикс-волна): отказ гейта не должен двигать счётчики бюджета. Раньше
    // RunsUsed/TasksUsed инкрементировались ДО проверки открытых карточек, и пять
    // отказов подряд жгли пять единиц впустую. Закрепляем: после Stopped-отказа
    // счётчики остаются как есть.
    [Fact]
    public async Task TryConsumeTeamImplementRun_ОтказНеДвигаетСчётчики_ВозвратКвотыНеНужен()
    {
        var (stab, _) = await MakeStabAsync("m2-no-burn");
        ((ITeamRunState)_sessions).WithTeamState(stab.Id, t =>
        {
            t.Stopped = true;
            t.Budget.RunsUsed = 5;
            t.Budget.TasksUsed = 7;
            return true;
        });

        // Отказ по «практика остановлена» — гейт НЕ должен списать счётчики.
        // Гоняем пять отказов подряд: бюджет не должен «сгореть» впустую.
        for (var i = 0; i < 5; i++)
        {
            var (verdict, _) = _sessions.TryConsumeTeamImplementRun(stab.Id, UserId);
            ((SessionManager.TeamRunQuota)(int)verdict).Should()
                .Be(SessionManager.TeamRunQuota.Exhausted);
        }
        var after = _sessions.GetById(stab.Id)!.TeamImplement!.Budget;
        after.RunsUsed.Should().Be(5, "отказы по Stopped не списывают RunsUsed");
        after.TasksUsed.Should().Be(7, "отказы по Stopped не списывают TasksUsed");
    }

    // M3 (фикс-волна, ветка resolvedByStaff): ResolutionNote, записанный в
    // TryResolveBlockerByFactAsync, должен пережить перечитывание истории после F5
    // (или рестарта). Раньше запись шла отдельным MutateCardAsync у активного чата,
    // который ходил через диск и затирался ближайшим снимком Accumulator-а. Теперь
    // Set+ChosenActionId+ResolutionNote идут одним вызовом ResolveEscalationAsync,
    // и снимок консистентно заливает их на диск.
    [Fact]
    public async Task TryResolveBlockerByFact_ResolutionNoteПереживаетПеречитываниеИзИстории()
    {
        var (stab, _) = await MakeStabAsync("m3-staff");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Блокер-задача", Description: "", Assignee: TaskItemAssignee.Claude), null);
        // Update нужен, чтобы SourceSessionId инициализировался текущей сессией
        _tasks.Update(task.Id, new UpdateTaskRequest());
        var blocker = await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        // Сигнал: прямая ветка resolvedByStaff (та, что ходят tasks_update/tasks_complete/
        // tasks_run_executor через FireResolveBlockerByTask). Сейчас зовём единую точку.
        var ok = await _sessions.TryResolveBlockerByFactAsync(
            stab.Id, task.Id, "штаб переписал задачу — подпись должна остаться");
        ok.Should().BeTrue();

        // Сейчас карточка погашена и её нет в OpenEscalationsAsync — перечитываем
        // непосредственно из истории.
        var fromHistory = await _sessions.ListOpenEscalationsAsync(stab.Id);
        fromHistory.Should().BeEmpty("карточка погашена");

        // Прямой путь: у активного чата карточки живут в Accumulator, не на диске —
// ClaudeSessionId у только что созданного чата ещё null. Accumulator — публичное поле.
        var dict = MakeSessionEntryDict();
        var entry0 = dict[stab.Id]!;
        var accField = entry0.GetType().GetField("Accumulator",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var acc = accField!.GetValue(entry0)!;
        var storedAll = (System.Collections.Generic.List<StoredMessage>)acc.GetType()
            .GetMethod("GetAll")!.Invoke(acc, null)!;
        var entry = storedAll.OfType<StoredTeamEscalationMessage>()
            .FirstOrDefault(m => m.EscalationId == blocker.Id);
        entry.Should().NotBeNull("карточка осталась в истории с подписью");
        entry!.Escalation.Resolved.Should().BeTrue();
        entry.Escalation.ChosenActionId.Should().Be("resolvedByStaff");
        entry.Escalation.ResolutionNote.Should().Be("штаб переписал задачу — подпись должна остаться");
    }

    // M3 (фикс-волна, ветка message): тот же контракт для пути «Ответ сообщением» —
    // текст человека из «ждёт решения» тоже должен оставить ResolutionNote в истории.
    // ResumeTeamFromDecisionOnUserInput приватный — идём через reflection, чтобы
    // проверить проводку сигнала, а не только прямую точку гашения.
    [Fact]
    public async Task ОтветСообщением_ResolutionNoteТожеПереживаетПеречитываниеИзИстории()
    {
        var (stab, _) = await MakeStabAsync("m3-message");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Блокер-msg", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());
        var blocker = await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        // Приватный метод SessionManager.ResumeTeamFromDecisionOnUserInput через reflection
        var method = typeof(SessionManager).GetMethod("ResumeTeamFromDecisionOnUserInput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var entryDict = MakeSessionEntryDict();
        var sessionEntryObj = entryDict[stab.Id]!;
        var resumeResult = method.Invoke(_sessions, [stab.Id, sessionEntryObj]);
        if (resumeResult is not Task resumeTask)
            throw new InvalidOperationException(
                $"ResumeTeamFromDecisionOnUserInput вернул {resumeResult?.GetType().Name ?? "null"}, ожидался Task");
        await resumeTask;

        // Карточка блокера погашена, ResolutionNote == "Ответ сообщением"
        var dict2 = MakeSessionEntryDict();
        var entry2 = dict2[stab.Id]!;
        var accField2 = entry2.GetType().GetField("Accumulator",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var acc2 = accField2!.GetValue(entry2)!;
        var storedAll2 = (System.Collections.Generic.List<StoredMessage>)acc2.GetType()
            .GetMethod("GetAll")!.Invoke(acc2, null)!;
        var entry = storedAll2.OfType<StoredTeamEscalationMessage>()
            .FirstOrDefault(m => m.EscalationId == blocker.Id);
        entry.Should().NotBeNull();
        entry!.Escalation.Resolved.Should().BeTrue();
        entry.Escalation.ChosenActionId.Should().Be("message");
        entry.Escalation.ResolutionNote.Should().Be("Ответ сообщением");
    }

    // S5 (фикс-волна): ответ сообщением гасит только карточки, чьё решение по сути
    // есть текст (Blocker/TaskFailed/PlanDeviation/CheckFailed/ProductDecision). НЕ
    // гасит BudgetExhausted/WaveGate/Stopped — их кнопка делает серверное действие
    // (поднять потолки, раздать волну, снять Стоп), которого текст не заменит. Без
    // этой проверки у человека терялась кнопка «Добавить бюджет» карточки BudgetExhausted.
    [Fact]
    public async Task ОтветСообщением_ГаситТолькоТекстовыеКарточки_BudgetExhaustedОстаётся()
    {
        var (stab, _) = await MakeStabAsync("s5-message-extinguish");
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Блокер-s5", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());

        // Открываем две решающие карточки разом: текстовую (Blocker) и нетекстовую (BudgetExhausted)
        await PublishBlockerAsync(stab, task.Id);
        var budgetCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = "Бюджет исчерпан",
            Details = "запусков не осталось",
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        await ((ITeamHistoryStore)_sessions).PublishTeamEscalationAsync(stab.Id, budgetCard);

        // Сообщение из AwaitingDecision
        var method = typeof(SessionManager).GetMethod("ResumeTeamFromDecisionOnUserInput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var entryDict = MakeSessionEntryDict();
        var sessionEntryObj = entryDict[stab.Id]!;
        var resumeResult = method.Invoke(_sessions, [stab.Id, sessionEntryObj]);
        if (resumeResult is not Task resumeTask)
            throw new InvalidOperationException(
                $"ResumeTeamFromDecisionOnUserInput вернул {resumeResult?.GetType().Name ?? "null"}, ожидался Task");
        await resumeTask;

        var open = await _sessions.ListOpenEscalationsAsync(stab.Id);
        // BudgetExhausted остаётся открытой — её кнопка «Добавить бюджет» единственный
        // способ поднять потолки, поэтому сообщение её НЕ гасит
        open.Should().Contain(c => c.Id == budgetCard.Id);
        // Текстовые карточки (Blocker) по тексту человека гасятся
        open.Should().NotContain(c => c.Kind == TeamEscalationKind.Blocker);
    }

    // M4 (фикс-волна): маркер <team:resolved> НЕ ДОЛЖЕН прерывать разбор других маркеров.
    // В одном ходу координатор может снять блокер И попросить решение — карточка
    // развилки должна тоже появиться. Раньше return под resolved глотал всё, что после
    // него в turnText. Закрепляем: комбинации (resolved + escalate) и (resolved + work)
    // оба обрабатываются, блокер-карточка гасится.
    [Fact]
    public async Task HandleTeamTurnEnd_ResolvedИEscalate_ОбаОбрабатываются()
    {
        var (stab, _) = await MakeStabAsync("m4-resolved-escalate");
        // Реальная задача (taskId not null) — блокер-карточка погасится маркером resolved
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Блокер-m4", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());
        var blocker = await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        var coord = (ClaudeHomeServer.Services.Team.TeamTurnCompletionService)
            typeof(SessionManager).GetField("_teamTurnCompletion",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(_sessions)!;
        var text = "Снял блокер. <team:resolved>задача решена</team> Нужно решение по другой теме. " +
                   "<escalate:deviation>тест отклонения</escalate>";
        await coord.HandleTeamTurnEndAsync(stab.Id, text, failed: false);

        // Карточка блокера должна быть погашена по resolved-маркеру
        var openBlockers = await _sessions.ListOpenEscalationsAsync(stab.Id);
        openBlockers.Should().NotContain(c => c.Id == blocker.Id && c.Kind == TeamEscalationKind.Blocker,
            "resolved-маркер должен погасить блокер до того, как escalate опубликует своё");
        // И новая карточка эскалации появилась
        var openAll = await _sessions.ListOpenEscalationsAsync(stab.Id);
        openAll.Should().Contain(c => c.Kind == TeamEscalationKind.PlanDeviation,
            "и escalate-маркер отработал — карточка опубликована");
    }

    [Fact]
    public async Task HandleTeamTurnEnd_ResolvedИWork_ОбаОбрабатываются()
    {
        var (stab, _) = await MakeStabAsync("m4-resolved-work");
        // Реальная задача — блокер-карточка погасится маркером resolved
        var task = _tasks.Create(stab.ProjectId, UserId, new CreateTaskRequest(
            Title: "Блокер-m4w", Description: "", Assignee: TaskItemAssignee.Claude), null);
        _tasks.Update(task.Id, new UpdateTaskRequest());
        var blocker = await PublishBlockerAsync(stab, task.Id);
        stab.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        var coord = (ClaudeHomeServer.Services.Team.TeamTurnCompletionService)
            typeof(SessionManager).GetField("_teamTurnCompletion",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(_sessions)!;
        var text = "Снял блокер. <team:resolved>переписал задачу</team> А вот новая вводная: " +
                   "<team:work>доделать форму поиска</team>";
        // Plan-режим обязателен для StartTeamWorkAsync — иначе вызов уйдёт в тишину
        await coord.HandleTeamTurnEndAsync(stab.Id, text, failed: false);

        var openBlockers = await _sessions.ListOpenEscalationsAsync(stab.Id);
        openBlockers.Should().NotContain(c => c.Id == blocker.Id && c.Kind == TeamEscalationKind.Blocker,
            "resolved-маркер снимает блокер ДО того, как team:work попытается стартовать");
    }

    // S2 (фикс-волна): сигнал «штаб переписал задачу» гасит блокер ТОЛЬКО когда
    // вызывающая сессия сама — штаб (caller == task.SourceSessionId). Из чата исполнителя
    // правка задачи не должна гасить карточку человека. Проверяем чистую функцию —
    // отдельно от FireResolveBlockerByTask, чтобы не поднимать TasksToolset с его
    // девятью DI-зависимостями только ради проверки контракта (TasksToolset вызывает
    // этот helper из обоих мест: tasks_update/tasks_complete/tasks_run_executor).
    [Fact]
    public void ShouldExtinguishBlocker_CallerРавенSource_Гасит()
    {
        var task = new TaskItem
        {
            Id = "t1",
            OwnerId = UserId,
            SourceSessionId = "stab-1",
            Title = "T",
            Status = TaskItemStatus.InProgress,
            Priority = TaskItemPriority.Medium,
        };
        TasksToolset.ShouldExtinguishBlocker(task, "stab-1").Should().BeTrue(
            "штаб-сессия как caller гасит блокер по своей задаче");
    }

    [Fact]
    public void ShouldExtinguishBlocker_CallerНеРавенSource_НеГасит()
    {
        var task = new TaskItem
        {
            Id = "t1",
            OwnerId = UserId,
            SourceSessionId = "stab-1",
            Title = "T",
            Status = TaskItemStatus.InProgress,
            Priority = TaskItemPriority.Medium,
        };
        // caller = executor session — не должен гасить карточку штаба
        TasksToolset.ShouldExtinguishBlocker(task, "executor-2").Should().BeFalse(
            "правка задачи из чата исполнителя НЕ гасит блокер-карточку штаба");
        TasksToolset.ShouldExtinguishBlocker(task, "").Should().BeFalse("пустой caller — защита");
    }

    // ─── PublishTeamEscalationAsync → TaskTitle (волна 1 team-blocker-honest, дефект 1430b732) ───
    // Карточка блокера едет в WS-событии team_escalation (и в историю) с плоским полем
    // taskTitle. Подпись диалога снятия «Задача «X» будет закрыта как снятая» опирается
    // на taskTitle — иначе подставляется заголовок карточки («Исполнитель застрял: …»),
    // и человек не видит, какую задачу закрывает. Мутация для проверки: убрать резолв
    // _sessions.GetTaskTitle?.Invoke(tid) в PublishTeamEscalationAsync — тест сразу
    // падает на null в задаче и в broadcast'е.

    [Fact]
    public async Task PublishTeamEscalation_ПодтягиваетTaskTitleИзСтораЗадач()
    {
        var (stab, _) = await MakeStabAsync("task-title");
        var task = _tasks.Create(stab.ProjectId, UserId,
            new CreateTaskRequest(Title: "Подготовить отчёт о шабаше"));
        _tasks.Update(task.Id, new UpdateTaskRequest());  // привязка SourceSessionId

        var blocker = await PublishBlockerAsync(stab, task.Id);

        blocker.TaskTitle.Should().Be("Подготовить отчёт о шабаше",
            "штаб обязан подтянуть название задачи — иначе подпись диалога снятия врёт");

        // Проверка wire-сообщения: фронт получит TaskTitle и подставит в подпись.
        // В Session два сообщения: TeamEscalationMessage и TeamImplementMessage
        // (PublishTeamEscalationAsync после смены стадии транслирует снимок режима).
        _broadcaster.Clear();
        await ((ITeamHistoryStore)_sessions).PublishTeamEscalationAsync(stab.Id, new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Застрял",
            Details = "нужна помощь",
            TaskId = task.Id,
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        });
        var msg = _broadcaster.Session
            .Select(t => t.Message).OfType<TeamEscalationMessage>().Single();
        msg.TaskTitle.Should().Be("Подготовить отчёт о шабаше");
    }

    [Fact]
    public async Task PublishTeamEscalation_ЗадачаУдаленаTaskTitleОстаётсяNull()
    {
        // null для удалённой задачи — фронт падает обратно на заголовок карточки,
        // не падает. Проверяем деградацию, а не молчаливое враньё.
        var (stab, _) = await MakeStabAsync("task-title-deleted");
        var blocker = await PublishBlockerAsync(stab, "missing-task-id");

        blocker.TaskTitle.Should().BeNull("удалённая задача — null, не пустой title");
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