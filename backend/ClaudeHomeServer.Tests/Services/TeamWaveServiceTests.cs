using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Раздача под-задач и волны режима «Командная реализация» (Э3): по подтверждённому плану
// бэкенд создаёт задачи с правильной атрибуцией (исполнитель, чат-штаб как источник,
// координатор как постановщик) и пакетно стартует волну. Волну считает бэкенд: следующая
// ждёт, пока закроются задачи предыдущей.
// TaskExecutionService в тестах не передаём — запуск claude.exe здесь не гоняется, проверяем
// раздачу: карточки задач, состояние режима и счётчики бюджета.
public class TeamWaveServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly TeamPlanningService _teamPlanning;
    private readonly TeamWaveService _sut;
    private string _plannerAnswer = "{}";
    private const string UserId = "user-1";
    private const string Username = "tester";
    // Снимок уведомлений (NotificationService шлёт их тем же хабом в группу user_*) —
    // под локом: бродкасты приходят и из фоновых задач
    private readonly List<ClaudeHomeServer.Protocol.NotificationMessage> _notifications = [];
    private readonly object _notificationsLock = new();
    // Снимок всех broadcast-ов хаба с ГРУППОЙ рассылки: пульс волны обязан идти строго
    // в группу сессии-штаба (не user_/project_-wide) — группа замыкается прокси
    private readonly List<(string Group, ClaudeHomeServer.Protocol.ServerMessage Message)> _hubSends = [];
    private readonly object _hubSendsLock = new();

    public TeamWaveServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "team_wave_tests_" + Guid.NewGuid().ToString("N"));
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

        var hub = new Mock<IHubContext<SessionHub>>();
        var clients = new Mock<IHubClients>();
        // Прокси фабрикуется ПОД ГРУППУ (замыкает её): один общий прокси не различал,
        // в какую группу ушёл broadcast, а пульс волны адресуется строго в сессию-штаб
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns((string group) =>
        {
            var proxy = new Mock<IClientProxy>();
            proxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object[], CancellationToken>((_, args, _) =>
                {
                    if (args.Length > 0 && args[0] is ClaudeHomeServer.Protocol.NotificationMessage n)
                        lock (_notificationsLock) _notifications.Add(n);
                    if (args.Length > 0 && args[0] is ClaudeHomeServer.Protocol.ServerMessage m)
                        lock (_hubSendsLock) _hubSends.Add((group, m));
                })
                .Returns(Task.CompletedTask);
            return proxy.Object;
        });
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _teamPlanning = new TeamPlanningService(_personas, new StubPlanner(() => _plannerAnswer));
        _sessions = CreateSessionManager(config, userStore, appSettings, hub);
        // Реальный NotificationService с дисковым стором (паттерн TaskExecutionServiceDelegationReportTests):
        // напоминания о карточках проверяем по broadcast-снимку выше
        var notif = new NotificationService(
            new NotificationStore(config, NullLogger<NotificationStore>.Instance),
            hub.Object,
            new PushService(config, new PushSubscriptionStore(config),
                new JwtService(config, userStore, NullLogger<JwtService>.Instance),
                NullLogger<PushService>.Instance),
            _personas, _projects, NullLogger<NotificationService>.Instance);
        _sut = new TeamWaveService(_sessions, _tasks, _projects, hub.Object,
            NullLogger<TeamWaveService>.Instance, _personas, notif: notif);
    }

    public void Dispose()
    {
        _sessions.KillAllProcesses();
        // Устойчиво к гонке с фоновой записью истории чата (см. TestFs)
        Helpers.TestFs.DeleteDirectoryResilient(_dir);
        GC.SuppressFinalize(this);
    }

    private SessionManager CreateSessionManager(IConfiguration config, UserStore userStore,
        AppSettingsService appSettings, Mock<IHubContext<SessionHub>> hub)
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders, subPool);
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
        var personaMemory = new PersonaMemoryService(knowledge, _personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var history = new ChatHistoryService(config);
        return new SessionManager(_projects, hub.Object, history, config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, _personas, personaMemory,
            bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, teamPlanning: _teamPlanning);
    }

    // Чат-штаб с включённым режимом и командой из двух персон. resumeSessionId — задать
    // ClaudeSessionId (ключ дисковой истории), нужен тестам гонки снимков плана «после
    // рестарта сервера» (m1/m2).
    private async Task<(Session Session, Persona Backend, Persona Frontend)> MakeStabAsync(
        string name, string? resumeSessionId = null)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var project = _projects.Create(name, dir, UserId, Username);

        Persona Mk(string personaName, string role) => _personas.Create(UserId, personaName, role,
            null, null, null, null, PersonaScope.Project, project.Id, null, null, memoryEnabled: false);

        var coordinator = Mk("Алекс", "Тимлид");
        var backend = Mk("Денис", "Backend-разработчик");
        var frontend = Mk("Кира", "Frontend-разработчик");

        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto,
            resumeSessionId: resumeSessionId, personaId: coordinator.Id);
        await _sessions.SetTeamImplementAsync(session.Id, enabled: true, coordinatorPersonaId: coordinator.Id,
            userId: UserId);
        // Этот файл тестирует раздачу волн, а не интервью: дефолтная стадия свежего режима —
        // Interview (волна 3, спека Э8) — сюда не годится, StartWaveAsync держал бы волну (M2).
        // Тесты, которым нужна другая стадия, переставляют её сами уже после этого вызова.
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Planning; return true; });
        return (_sessions.GetById(session.Id)!, backend, frontend);
    }

    // Доступ к приватному SessionEntry реестра _sessions в SessionManager (white-box):
    // нужен, чтобы обнулить Accumulator и получить чат «после рестарта сервера» — только в
    // этом состоянии GetTeamPlanAsync на каждое чтение десериализует НОВЫЙ объект плана
    // с диска (у живого чата план — один и тот же объект в аккумуляторе).
    private object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var entries = (System.Collections.IDictionary)field.GetValue(_sessions)!;
        return entries[sessionId]!;
    }

    private static void ClearAccumulator(object entry) =>
        entry.GetType().GetField("Accumulator")!.SetValue(entry, null);

    // M7: ходы в тестах завершаются прямым HandleTeamTurnEndAsync, минуя запуск — флаг
    // «вводная от человека» проставляем явно, как это сделал бы запуск по сообщению человека.
    private static void SetTeamTurnFromHuman(object entry, bool value) =>
        entry.GetType().GetField("TeamTurnFromHuman")!.SetValue(entry, value);

    // Живой прогон CLI у чата (белый ящик, приём StubAdapter из SessionManagerTests): в entry
    // реестра ставим адаптер с HasLiveTurn=true — реальный claude.exe в юнит-тестах не
    // поднимается, а пульс обязан считать идущий ход активностью волны.
    private void MarkLiveTurn(string sessionId)
    {
        var entry = GetEntry(sessionId);
        var adapter = new Mock<ClaudeHomeServer.Services.Llm.ILlmSessionAdapter>();
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
    }

    // Штаб «после рестарта сервера» с УТВЕРЖДЁННЫМ планом на две под-задачи ОДНОЙ волны:
    // карточка плана лежит в истории на диске, аккумулятора у чата нет (оживает лениво,
    // с первым ходом). Волна ещё НЕ роздана — это делает сам тест, чтобы воспроизвести гонку
    // независимых чтений плана (m1/m2, второй проход Глеба).
    private async Task<(Session Session, TeamImplementPlan Plan)> MakeRestartedRunningStabAsync(string name)
    {
        var (session, backend, frontend) = await MakeStabAsync(name, resumeSessionId: "csid-" + name);
        _plannerAnswer = $$"""
            {"summary":"Экспорт задач в CSV","subtasks":[
              {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
               "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
               "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
              {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
               "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
               "files":["frontend/src/components/Toolbar.tsx"],"wave":1,"doneCriteria":"файл скачивается"}]}
            """;
        var (plan, reason) = await _sessions.CreateTeamPlanAsync(session.Id, "Экспорт задач в CSV", UserId);
        reason.Should().BeNull();

        ClearAccumulator(GetEntry(session.Id));
        return (_sessions.GetById(session.Id)!, plan!);
    }

    // План на две волны: серверная часть первой волной, фронтовая — второй
    private static TeamImplementPlan MakePlan(Persona backend, Persona frontend) => new()
    {
        Request = "Экспорт задач в CSV",
        Summary = "Экспорт задач в CSV",
        Approved = true,
        Subtasks =
        [
            new TeamImplementSubtask
            {
                Title = "Эндпоинт экспорта",
                Goal = "GET /api/tasks/export отдаёт CSV",
                ExecutorPersonaId = backend.Id,
                ExecutorRationale = "Серверная часть — его зона",
                Files = ["backend/ClaudeHomeServer/Controllers/TasksController.cs"],
                Wave = 1,
                DoneCriteria = "dotnet build зелёный, эндпоинт отдаёт CSV",
            },
            new TeamImplementSubtask
            {
                Title = "Кнопка «Экспорт»",
                Goal = "Кнопка в тулбаре задач",
                ExecutorPersonaId = frontend.Id,
                ExecutorRationale = "UI — её зона",
                Files = ["frontend/src/components/Toolbar.tsx"],
                Wave = 2,
                DoneCriteria = "файл скачивается",
            },
        ],
    };

    [Fact]
    public async Task StartWave_СоздаётЗадачиПервойВолныСАтрибуциейИСтартуетИх()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-run");
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        // Волна 1 — только серверная под-задача: фронтовая ждёт своей волны
        created.Should().HaveCount(1);
        var task = created[0];
        task.Title.Should().Be("Эндпоинт экспорта");
        task.PersonaId.Should().Be(backend.Id, "исполнитель — персона из плана");
        task.Assignee.Should().Be(TaskItemAssignee.Claude, "персона-исполнитель подразумевает Claude");
        task.SourceSessionId.Should().Be(session.Id, "чат-штаб — источник: из него вычисляется родитель чата исполнения");
        task.CreatedByPersonaId.Should().Be(session.TeamImplement!.CoordinatorPersonaId, "постановщик — координатор");
        task.ProjectId.Should().Be(session.ProjectId);
        // Описание задачи несёт всё, что нужно исполнителю
        task.Description.Should().Contain("GET /api/tasks/export");
        task.Description.Should().Contain("TasksController.cs");
        task.Description.Should().Contain("dotnet build зелёный");
        // Связь под-задачи с задачей трекера
        plan.Subtasks[0].TaskId.Should().Be(task.Id);
        plan.Subtasks[1].TaskId.Should().BeNull("вторая волна ещё не роздана");

        // Состояние режима: номер волны, плановое число волн и счётчики бюджета от бэкенда
        var ti = _sessions.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Wave);
        ti.WaveNumber.Should().Be(1);
        ti.PlannedWaves.Should().Be(2, "бейдж покажет честное «волна 1 из 2»");
        ti.Budget.TasksUsed.Should().Be(1);
        ti.Budget.RunsUsed.Should().Be(1);
        ti.Budget.WavesUsed.Should().Be(1);
    }

    // B3 приёмки: исполнитель не стартовал (модель недоступна, лимит провайдера, задача
    // удалена) — раньше это уходило только в лог, и человек видел лишь исчезнувшую задачу.
    [Fact]
    public async Task ЗапускИсполнителяУпал_ПубликуетКарточкуВЛентуШтаба()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-launch-fail");
        var plan = MakePlan(backend, frontend);
        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var task = created[0];

        await _sut.RaiseLaunchFailedAsync(_sessions.GetById(session.Id)!, task,
            new InvalidOperationException("Задача удалена"));

        var ti = _sessions.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "карточка эскалации ставит практику на ожидание решения — молчаливых пауз не бывает");
    }

    [Fact]
    public async Task StartWave_ВтораяВолнаЖдётЗакрытияПервой()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-wait");
        var plan = MakePlan(backend, frontend);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        // Задача первой волны ещё в работе — вторую не раздаём
        var blocked = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        blocked.Should().BeEmpty("волна 2 зависит от невыполненной волны 1");
        plan.Subtasks[1].TaskId.Should().BeNull();

        // Первая закрыта — вторая уходит исполнителю
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        var second = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        second.Should().HaveCount(1);
        second[0].PersonaId.Should().Be(frontend.Id);
        plan.Subtasks[1].TaskId.Should().Be(second[0].Id);
        var ti = _sessions.GetById(session.Id)!.TeamImplement!;
        ti.WaveNumber.Should().Be(2);
        ti.Budget.TasksUsed.Should().Be(2);
        ti.Budget.WavesUsed.Should().Be(2);
    }

    [Fact]
    public async Task StartWave_ПланРоздан_ПовторныйВызовНичегоНеСоздаёт()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-done");
        var plan = MakePlan(backend, frontend);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        var second = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(second[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var extra = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        extra.Should().BeEmpty("все под-задачи плана уже розданы");
        _tasks.GetByProject(session.ProjectId!).Should().HaveCount(2, "дублей задач нет");
    }

    [Fact]
    public async Task StartWave_БезРежима_НичегоНеДелает()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-nomode");
        await _sessions.SetTeamImplementAsync(session.Id, enabled: false, userId: UserId);
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(_sessions.GetById(session.Id)!, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty();
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty();
    }

    [Fact]
    public async Task StartWave_ШтабВWorktree_ОписаниеНазываетРабочееДерево()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-worktree");
        session.WorktreePath = Path.Combine(_dir, "wt", "feature-x");
        session.WorktreeBranch = "feature/x";
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created[0].Description.Should().Contain(session.WorktreePath,
            "исполнитель обязан знать, что работа идёт в worktree, а не в основном репо");
        created[0].Description.Should().Contain("feature/x");
    }

    [Fact]
    public async Task StartWave_ШтабВWorktree_ПодЗадачаНесётДеревоПолем()
    {
        // Источник правды — поле задачи: по нему чат-исполнитель стартует прямо в дереве
        // (TaskExecutionService → SessionManager.AttachWorktreeAsync), а не «догадывается»
        // из текста описания
        var (session, backend, frontend) = await MakeStabAsync("wave-worktree-field");
        session.WorktreePath = Path.Combine(_dir, "wt", "feature-y");
        session.WorktreeBranch = "feature/y";
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created[0].WorktreePath.Should().Be(session.WorktreePath);
        created[0].WorktreeBranch.Should().Be("feature/y");
    }

    [Fact]
    public async Task StartWave_ШтабБезWorktree_ПодЗадачаБезДерева()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-no-worktree");
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created[0].WorktreePath.Should().BeNull("исполнитель стартует в корне проекта");
    }

    // --- Выбор волны (чистая логика зависимостей) ---

    [Fact]
    public void SelectWave_ПервойИдётМинимальнаяВолна()
    {
        var plan = MakePlan(MakeStubPersona("b"), MakeStubPersona("f"));

        var (wave, subtasks) = TeamWaveService.SelectWave(plan, _ => false);

        wave.Should().Be(1);
        subtasks.Should().ContainSingle().Which.Wave.Should().Be(1);
    }

    [Fact]
    public void SelectWave_НезакрытаяПредыдущаяВолнаБлокируетСледующую()
    {
        var plan = MakePlan(MakeStubPersona("b"), MakeStubPersona("f"));
        plan.Subtasks[0].TaskId = "task-1";

        var (wave, subtasks) = TeamWaveService.SelectWave(plan, _ => false);

        wave.Should().Be(2);
        subtasks.Should().BeEmpty("задача волны 1 не в Done");
    }

    [Fact]
    public void SelectWave_ЗакрытаяПредыдущаяОткрываетСледующую()
    {
        var plan = MakePlan(MakeStubPersona("b"), MakeStubPersona("f"));
        plan.Subtasks[0].TaskId = "task-1";

        var (wave, subtasks) = TeamWaveService.SelectWave(plan, id => id == "task-1");

        wave.Should().Be(2);
        subtasks.Should().ContainSingle().Which.Wave.Should().Be(2);
    }

    [Fact]
    public void SubtaskDescription_БезWorktree_НеУпоминаетРабочееДерево()
    {
        var plan = MakePlan(MakeStubPersona("b"), MakeStubPersona("f"));

        var text = TeamImplementPrompts.SubtaskDescription(plan, plan.Subtasks[0], null, null);

        text.Should().NotContain("worktree");
        text.Should().Contain("## Что сделать");
        text.Should().Contain("## Файлы во владении");
        text.Should().Contain("## Критерий готовности");
        text.Should().Contain("волне 1 из 2");
    }

    // --- Правило «любая работа — через задачу» ---

    [Fact]
    public void CoordinatorTurn_НесётПравилоЗадачиИСтадиюВолны()
    {
        var team = new SessionTeamImplement
        {
            Stage = TeamImplementStage.Wave,
            WaveNumber = 1,
            PlannedWaves = 2,
        };

        var text = TeamImplementPrompts.CoordinatorTurn(team);

        text.Should().Contain("без задачи");
        text.Should().Contain("волна 1 из 2");
        text.Should().Contain("Сам файлы не правишь", "правило включено по умолчанию");
    }

    [Fact]
    public void CoordinatorTurn_ПравилоСнято_ПроОтключённыеИнструментыМолчит()
    {
        var team = new SessionTeamImplement { CoordinatorNoCode = false };

        TeamImplementPrompts.CoordinatorTurn(team).Should().NotContain("Сам файлы не правишь");
    }

    [Fact]
    public void CoordinatorDisallowed_ИменаИзвестныCli()
    {
        // Неизвестное имя в deny-правиле роняет запуск claude с кодом 1 — сверяемся
        // с рабочим списком профиля «Только чтение»
        TeamImplementPrompts.CoordinatorDisallowed.Should()
            .BeSubsetOf(PersonaAccessPolicy.ReadOnlyDisallowed);
    }

    private Persona MakeStubPersona(string name) => _personas.Create(UserId, name, null, null, null, null,
        null, PersonaScope.Global, null, null, null, memoryEnabled: false);

    // --- Э4: автономный цикл волн, бюджет, перевыдача, зависание ---

    // Штаб с ОПУБЛИКОВАННОЙ карточкой плана: автономный цикл ходит именно по ней
    // (team.PlanCardId → карточка в ленте), а не по объекту плана из теста.
    private async Task<(Session Session, TeamImplementPlan Plan)> MakeRunningStabAsync(string name, bool autoWaves = true)
    {
        var (session, backend, frontend) = await MakeStabAsync(name);
        await _sessions.SetTeamImplementAutoAsync(session.Id, autoWaves, UserId);
        _plannerAnswer = $$"""
            {"summary":"Экспорт задач в CSV","subtasks":[
              {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
               "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
               "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
              {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
               "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
               "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
            """;
        var (plan, reason) = await _sessions.CreateTeamPlanAsync(session.Id, "Экспорт задач в CSV", UserId);
        reason.Should().BeNull();

        // Держим штаб «занятым»: закрытие волны шлёт координатору сводку (summaryTurn), а у
        // свободного чата это запускает РЕАЛЬНЫЙ ход claude (TestLauncherFactory → LocalProcessRunner).
        // На CI (ubuntu без claude) такой ход падает мгновенно и асинхронно через
        // HandleTeamTurnEndAsync переводит только что выставленную стадию Checking в AwaitingDecision —
        // проверка входа в Checking гоняется с этим падением. При статусе Working сводка уходит
        // в очередь (EnqueuePending), процесс не стартует, стадия детерминирована. Ход штаба тесты
        // класса и так симулируют вручную (HandleTeamTurnEndAsync), на реальный запуск не полагаясь.
        var running = _sessions.GetById(session.Id)!;
        running.Status = SessionStatus.Working;
        return (running, plan!);
    }

    private SessionTeamImplement Team(string sessionId) => _sessions.GetById(sessionId)!.TeamImplement!;

    [Fact]
    public async Task StartWave_ИсчерпанныйБюджет_ВолнаНеСтартуетИПрактикаЖдётЧеловека()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-budget");
        var plan = MakePlan(backend, frontend);
        // Потолок задач выбран предыдущими волнами итерации
        var team = session.TeamImplement!;
        team.Budget.TasksUsed = team.Budget.MaxTasks;

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty("бюджет итерации исчерпан — цикл останавливается");
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty("ни одной задачи сверх бюджета");
        plan.Subtasks[0].TaskId.Should().BeNull();
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "человек получает карточку с расходом, а не молчаливую остановку");
    }

    [Fact]
    public async Task StartWave_ИсчерпанныеВолны_ОстанавливаетДажеПриСвободныхЗадачах()
    {
        // MaxWaves — отдельное измерение бюджета от MaxTasks: волна не должна стартовать,
        // если исчерпан именно потолок числа волн, а не задач/запусков
        var (session, backend, frontend) = await MakeStabAsync("wave-maxwaves");
        var plan = MakePlan(backend, frontend);
        var team = session.TeamImplement!;
        team.Budget.WavesUsed = team.Budget.MaxWaves;

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty("потолок числа волн исчерпан");
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty();
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    [Fact]
    public async Task StartWave_ПослеОстановкиЧеловеком_НовыеВолныНеСтартуют()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-stopped");
        var plan = MakePlan(backend, frontend);
        await _sessions.StopTeamImplementAsync(session.Id, UserId);

        var created = await _sut.StartWaveAsync(_sessions.GetById(session.Id)!, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty();
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty();
    }

    [Fact]
    public async Task ЗакрытиеВолны_Авто_СледующаяВолнаСтартуетСама()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-auto");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        first.Should().ContainSingle();

        // Исполнитель закрыл задачу — единственный путь в Done поднимает событие
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        var wave2 = _tasks.GetByProject(session.ProjectId!)
            .Where(t => t.Labels.Contains("волна 2")).ToList();
        wave2.Should().ContainSingle("при авто-волнах следующая уходит без согласования");
        var team = Team(session.Id);
        team.WaveNumber.Should().Be(2);
        team.ClosedWave.Should().Be(1);
        team.Stage.Should().Be(TeamImplementStage.Wave, "карточки между волнами не появляются");
        team.Budget.WavesUsed.Should().Be(2);
    }

    [Fact]
    public async Task ЗакрытиеВолны_АвтоСнято_СледующаяЖдётРешенияЧеловека()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-manual", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("без авто волна ждёт кнопки");
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    [Fact]
    public async Task ЗакрытиеВолны_ПовторныйКолбэк_ВторойРазНеЗакрывает()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-twice");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        _tasks.GetByProject(session.ProjectId!).Should().HaveCount(2, "дублей задач второй волны нет");
        Team(session.Id).Budget.WavesUsed.Should().Be(2);
    }

    [Fact]
    public async Task ЗакрытиеПоследнейВолны_ПереводитВПроверку()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-last");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        var second = _tasks.GetByProject(session.ProjectId!).First(t => t.Labels.Contains("волна 2"));
        _tasks.Update(second.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(second.Id)!);

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Checking,
            "волны кончились — координатор проверяет результат и подводит итог");
    }

    // --- Minor (волна 3): кнопки skip/drop/editRest карточки эскалации двигают бэкенд ---

    [Fact]
    public async Task RespondEscalation_Skip_ЗакрываетПодЗадачуИЗапускаетСледующуюВолну()
    {
        // Раньше кнопка ничего не делала: под-задача оставалась незакрытой, и волна не могла
        // закрыться до ручного tasks_complete.
        var (session, plan) = await MakeRunningStabAsync("wave-skip");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var task = first[0];
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.TaskFailed,
            Title = "Задача провалилась дважды",
            TaskId = task.Id,
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.TaskFailed),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "skip", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetById(task.Id)!.Status.Should().Be(TaskItemStatus.Done,
            "пропущенная под-задача не должна вечно висеть открытой — иначе волна не закроется");
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(task.Id)!);
        var wave2 = _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2")).ToList();
        wave2.Should().ContainSingle("волна 1 закрыта пропуском — авто-волны раздают следующую");
    }

    [Fact]
    public async Task RespondEscalation_Drop_ЗакрываетПодЗадачуСПояснением()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-drop", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var task = first[0];
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял",
            TaskId = task.Id,
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "drop", userId: UserId);

        ok.Should().BeTrue();
        var dropped = _tasks.GetById(task.Id)!;
        dropped.Status.Should().Be(TaskItemStatus.Done);
        dropped.ResultMarkdown.Should().Contain("Снято", "человек должен видеть, что задачу сняли, а не забыли");
    }

    [Fact]
    public async Task RespondEscalation_EditRest_ВозвращаетШтабВИнтервьюДляПерепланирования()
    {
        // Раньше editRest уводил стадию в Wave без восстановления сторожа (волна уже закрыта,
        // ClosedWave == WaveNumber — ветка обновления отсечек не срабатывала).
        var (session, plan) = await MakeRunningStabAsync("wave-editrest", autoWaves: false);
        _sessions.WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Wave;
            t.WaveNumber = 1;
            t.ClosedWave = 1; // волна 1 уже закрыта — ровно ситуация карточки WaveGate
            return true;
        });
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.WaveGate,
            Title = "Волна 1 закрыта. Запустить волну 2?",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.WaveGate),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "editRest", userId: UserId);

        ok.Should().BeTrue();
        var team = Team(session.Id);
        team.Stage.Should().Be(TeamImplementStage.Interview,
            "«Изменить остаток плана» — перепланирование, а не продолжение закрытой волны");
        team.WaveStartedAt.Should().BeNull("сторож волны не должен тикать в интервью");
        team.Replanning.Should().BeTrue("план уже был опубликован — следующий будет новой версией");
    }

    // --- Мёртвая зона конвейера (прод 2026-08-17): решение по карточке после закрытой волны ---

    // Хвост прод-инцидента: карточка PlanDeviation висела, когда последняя задача волны
    // закрылась — авто-раздача следующей была подавлена («практика ждёт человека»), а
    // «Разрешить» в белом списке actionId отсутствовал. До фикса конвейер стоял часами,
    // пока человек не жал «Остановить → Продолжить».
    [Fact]
    public async Task RespondEscalation_AllowПослеЗакрытойВолны_РаздаётСледующуюВолну()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-allow");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        // Координатор сообщил о расхождении с планом — практика ждёт решения человека
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);

        // Волна закрылась, пока карточка ждала ответа: ClosedWave == WaveNumber, отсечка пуста
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var dead = Team(session.Id);
        dead.ClosedWave.Should().Be(dead.WaveNumber, "воспроизвели мёртвую зону инцидента");
        dead.WaveStartedAt.Should().BeNull();

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("«Разрешить» после закрытой волны обязан раздать следующую — иначе конвейер мёртв");
        var team = Team(session.Id);
        team.Stage.Should().Be(TeamImplementStage.Wave);
        team.WaveNumber.Should().Be(2);
        team.WaveStartedAt.Should().NotBeNull("сторож зависших волн снова тикает");
    }

    // Раздача по решению человека теперь идёт по состоянию ИЛИ по кнопке из белого списка:
    // для runNext оба условия истинны одновременно — сработать должно ровно один раз.
    [Fact]
    public async Task RespondEscalation_RunNextПослеЗакрытойВолны_РаздаётВолнуРовноОдинРаз()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-runnext", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var gate = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveGate);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, gate.Id, "runNext", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("двойного вызова раздачи быть не должно");
        Team(session.Id).Budget.WavesUsed.Should().Be(2, "волна 2 посчитана один раз");
    }

    // Действия, завершающие/останавливающие работу, конвейер не двигают — даже из состояния
    // «закрытая волна + нерозданные под-задачи» (стадия у них не Wave).
    [Fact]
    public async Task RespondEscalation_FinishПослеЗакрытойВолны_СледующуюВолнуНеРаздаёт()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-finish", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var gate = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveGate);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, gate.Id, "finish", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("«Завершить итерацию» новую работу не разворачивает");
        Team(session.Id).Stage.Should().Be(TeamImplementStage.Checking);
    }

    [Fact]
    public async Task RespondEscalation_Stop_НаМёртвойЗонеСостояний_ВолнуНеРаздаёт()
    {
        // «Остановить» информационной карточки добавленной волны: даже если бэкенд уже стоит
        // в форме мёртвой зоны (Wave + закрытая волна + нерозданные под-задачи), остановка
        // не должна разворачивать новую работу
        var (session, plan) = await MakeRunningStabAsync("wave-stop-dead", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Wave; return true; });
        var info = new TeamEscalation
        {
            Kind = TeamEscalationKind.WaveAdded,
            Title = "Новая вводная в работе",
            Wave = 2,
            Actions = TeamEscalationActions.For(TeamEscalationKind.WaveAdded),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, info);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, info.Id, "stop", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("«Остановить» новую работу не разворачивает");
        var team = Team(session.Id);
        team.Stopped.Should().BeTrue();
        team.Stage.Should().Be(TeamImplementStage.Wave, "стадию «Остановить» не двигает — работу не возобновляет");
    }

    // Приёмка мёртвой зоны: авто-волны СНЯТЫ, а карточка, погашенная человеком, — не гейт
    // волны (PlanDeviation, «Разрешить»). Договор режима при снятых авто-волнах: следующая
    // волна идёт только по явной кнопке человека, иначе он получает гейт-карточку.
    // D1 (ревью 2026-08-17): докрут по состоянию различает повод вызова — «Разрешить» это
    // не «Запустить», волну он не раздаёт, а поднимает гейт той же сборкой, что закрытие волны.
    [Fact]
    public async Task RespondEscalation_AllowПриСнятыхАвтоволнах_НеРаздаётВолнуМимоГейта()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-allow-manual", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        // Волна закрылась, пока карточка ждала ответа: гейт-карточку закрытие не подняло
        // (практика ждёт человека) — форма мёртвой зоны при снятых авто-волнах
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("авто-волны сняты — «Разрешить» по чужой карточке не заменяет кнопку запуска");
        var gate = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveGate);
        gate.Title.Should().Be("Волна 1 закрыта. Запустить волну 2?",
            "вместо раздачи человек получает гейт-карточку");
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "гейт вернул практику в ожидание явного решения человека");
    }

    // Парный путь того же договора (D1): ответ обычным сообщением (ResumeTeamFromDecision
    // OnUserInput) — тоже докрут по состоянию, а не кнопка запуска: при снятых авто-волнах
    // гейт-карточка вместо молчаливой раздачи следующей волны.
    [Fact]
    public async Task ОтветТекстом_ПриСнятыхАвтоволнах_НеРаздаётВолнуМимоГейта()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-answer-manual", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        await _sessions.SendMessageAsync(session.Id, "делайте по варианту 2", []);

        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("текстовый ответ — не кнопка «Запустить»: авто-волны сняты");
        (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Should().ContainSingle(c => c.Kind == TeamEscalationKind.WaveGate,
                "человек получает гейт, а не молчаливую раздачу");
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    // Major (ревью 2026-08-17), третья дверь в мёртвую зону: clarify посреди волны → интервью;
    // волна закрылась (waitsHuman ставит ClosedWave и ничего не двигает); координатор ответил
    // маркером <team:talk/> — интервью закрыто, и раздачу следующей обязан позвать тот же
    // предикат, что у решения по карточке, иначе конвейер стоит до сторожа.
    [Fact]
    public async Task МаркерРазговора_ПослеЗакрытияВолныВИнтервью_РаздаётСледующуюВолну()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-talk-deadzone");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        // Координатор объявил тупик посреди волны — практика в интервью, волны на паузе
        await _sessions.EnterInterviewAsync(session.Id, "тест: тупик в волне", withTurn: false);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var mid = Team(session.Id);
        mid.ClosedWave.Should().Be(mid.WaveNumber, "волна закрыта в интервью — воспроизвели дверь");
        mid.Stage.Should().Be(TeamImplementStage.Interview, "закрытие конвейер не двигает");

        await _sessions.HandleTeamTurnEndAsync(session.Id,
            "Вопросов нет, продолжаем.\n<team:talk/>", failed: false);

        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("выход из интервью обязан раздать следующую волну — иначе мёртвая зона");
        var team = Team(session.Id);
        team.Stage.Should().Be(TeamImplementStage.Wave);
        team.WaveNumber.Should().Be(2);
        team.WaveStartedAt.Should().NotBeNull("сторож зависших волн снова тикает");
    }

    // Тот же выход из интервью при снятых авто-волнах (Major + D1): не раздача, а гейт —
    // повод StateCatchUp, как у двух других точек докрута по состоянию.
    [Fact]
    public async Task МаркерРазговора_ПослеЗакрытияВолны_ПриСнятыхАвтоволнах_ПоднимаетГейт()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-talk-gate", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        await _sessions.EnterInterviewAsync(session.Id, "тест: тупик в волне", withTurn: false);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        await _sessions.HandleTeamTurnEndAsync(session.Id,
            "Вопросов нет, продолжаем.\n<team:talk/>", failed: false);

        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("авто-волны сняты — выход из интервью не заменяет кнопку запуска");
        (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Should().ContainSingle(c => c.Kind == TeamEscalationKind.WaveGate);
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    // Действия, уводящие практику из волны, конвейер не двигают и из формы мёртвой зоны:
    // «Завершить с замечаниями» — в проверку, «Изменить остаток» — в интервью,
    // «Повторить планирование» — в планирование. Новая работа ни в одном случае не уходит.
    [Theory]
    [InlineData("finishWithIssues", TeamImplementStage.Checking)]
    [InlineData("editRest", TeamImplementStage.Interview)]
    [InlineData("retryPlan", TeamImplementStage.Planning)]
    public async Task RespondEscalation_ДействияВнеВолны_СледующуюВолнуНеРаздают(
        string actionId, TeamImplementStage expected)
    {
        var (session, plan) = await MakeRunningStabAsync("wave-noflow-" + actionId, autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var gate = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveGate);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, gate.Id, actionId, userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty($"«{actionId}» новую работу не разворачивает");
        Team(session.Id).Stage.Should().Be(expected);
    }

    // Повторное решение по той же карточке (двойной клик, ретрай запроса) не должно раздать
    // волну второй раз — иначе на одной под-задаче выросли бы задачи-дубли.
    [Fact]
    public async Task RespondEscalation_ПовторноеРешениеПоТойЖеКарточке_ВолнуНеРаздаётДважды()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-allow-twice");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        var firstAnswer = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: UserId);
        var secondAnswer = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: UserId);

        firstAnswer.Should().BeTrue();
        secondAnswer.Should().BeFalse("карточка уже погашена — второе решение по ней не проходит");
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("повторное решение не должно раздать ту же волну второй раз");
        Team(session.Id).Budget.WavesUsed.Should().Be(2, "волна 2 посчитана один раз");
    }

    // Две параллельные раздачи по решению человека (клик по карточке и текстовый ответ
    // одновременно) идут через один и тот же per-session семафор волны — дублей быть не должно.
    [Fact]
    public async Task РешениеИТекстОдновременно_ВолнаРаздаётсяОдинРаз()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-decision-race");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.GetById(session.Id)!.Status = SessionStatus.Working;

        await Task.WhenAll(
            _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: UserId),
            _sessions.SendMessageAsync(session.Id, "разрешаю, продолжай", []));

        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("две параллельные раздачи не должны создать задачи-дубли");
        Team(session.Id).Budget.WavesUsed.Should().Be(2);
    }

    // P23 в форме мёртвой зоны: плановые волны кончились (ClosedWave >= PlannedWaves), а в
    // плане ещё висят нерозданные под-задачи. Раздавать нечего — практика уходит в Idle,
    // а не встаёт в вечную «волну N из N».
    [Fact]
    public async Task RespondEscalation_ВсеПлановыеВолныЗакрыты_ВIdleБезРаздачи()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-allow-terminal");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        // Итерация признана завершённой на первой волне
        _sessions.WithTeamState(session.Id, t => { t.PlannedWaves = 1; return true; });

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("плановые волны закрыты — новую работу решение не разворачивает");
        Team(session.Id).Stage.Should().Be(TeamImplementStage.Idle,
            "итерация завершена: Idle, а не вечная «волна N из N»");
    }

    // Сторож мёртвой зоны не должен путать «конвейер встал» со стадиями, где работы и не
    // должно быть: интервью, планирование, проверка, ожидание новой вводной.
    [Theory]
    [InlineData(TeamImplementStage.Interview)]
    [InlineData(TeamImplementStage.Planning)]
    [InlineData(TeamImplementStage.Checking)]
    [InlineData(TeamImplementStage.Idle)]
    public async Task СторожВолн_МёртваяЗонаВнеСтадииВолны_Молчит(TeamImplementStage stage)
    {
        var (session, plan) = await MakeRunningStabAsync("wave-deadzone-stage-" + stage, autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.WithTeamState(session.Id, t => { t.Stage = stage; return true; });
        _sessions.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddHours(-1);

        await _sut.CheckStalledWavesAsync();

        (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Where(c => c.Kind == TeamEscalationKind.WaveStalled).Should().BeEmpty(
                $"в стадии «{stage}» работы и не должно быть — тревожить человека не за что");
    }

    // Практика остановлена человеком: стоящий конвейер здесь — его собственное решение,
    // карточка о «мёртвой зоне» была бы ложной тревогой.
    [Fact]
    public async Task СторожВолн_МёртваяЗонаПриОстановкеЧеловеком_Молчит()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-deadzone-stopped", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Wave;
            t.Stopped = true;
            return true;
        });
        _sessions.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddHours(-1);

        await _sut.CheckStalledWavesAsync();

        (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Where(c => c.Kind == TeamEscalationKind.WaveStalled).Should().BeEmpty(
                "остановлено человеком — стоящий конвейер тут ожидаем");
    }

    // --- M2: волна не идёт поверх стадий, которые ждут человека ---

    [Theory]
    [InlineData(TeamImplementStage.Interview)]
    [InlineData(TeamImplementStage.AwaitingDecision)]
    public async Task StartWave_СтадияЖдётЧеловека_ВолнаНеСтартуетИСтадиюНеЗатирает(TeamImplementStage stage)
    {
        // Окно clarify→vN+1: координатор объявил тупик (версия плана ещё та же, гард версий
        // пропускает), доехавшие задачи запускали следующую волну — и стадия затиралась в Wave
        var (session, backend, frontend) = await MakeStabAsync("wave-stage-" + stage);
        var plan = MakePlan(backend, frontend);
        _sessions.WithTeamState(session.Id, t => { t.Stage = stage; return true; });

        var created = await _sut.StartWaveAsync(_sessions.GetById(session.Id)!, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty("человек сейчас отвечает — конвейер ждёт его");
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty();
        var team = Team(session.Id);
        team.Stage.Should().Be(stage, "стадия не подменена волной");
        team.WaveNumber.Should().Be(0);
        team.Budget.TasksUsed.Should().Be(0, "резерв бюджета не прошёл");
    }

    [Fact]
    public async Task ЗакрытиеВолны_ПрактикаВИнтервью_СледующаяНеСтартует()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-close-interview");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        // Координатор объявил тупик — волны на паузе, идёт интервью
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Interview; return true; });

        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().BeEmpty("волна 2 не стартует поверх интервью");
        var team = Team(session.Id);
        team.ClosedWave.Should().Be(1, "работа доделана — волна закрыта честно");
        team.Stage.Should().Be(TeamImplementStage.Interview, "интервью не сбито");
    }

    [Fact]
    public async Task ЗакрытиеПоследнейВолны_ПрактикаЖдётРешения_ВПроверкуНеУводит()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-close-awaiting");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var second = _tasks.GetByProject(session.ProjectId!).First(t => t.Labels.Contains("волна 2"));
        // По второй волне пришёл блокер — практика ждёт решения человека
        _sessions.WithTeamState(session.Id,
            t => { t.Stage = TeamImplementStage.AwaitingDecision; return true; });

        _tasks.Update(second.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(second.Id)!);

        var team = Team(session.Id);
        team.ClosedWave.Should().Be(2);
        team.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "«проверка» не подменяет стадию, в которой человек как раз отвечает");
    }

    [Fact]
    public async Task ПровалЗадачи_ПерваяНеудача_Перевыдача()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-retry");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var failed = _tasks.MarkClaudeResult(first[0].Id, "error")!;

        await _sut.OnTaskFailedAsync(failed);

        var team = Team(session.Id);
        team.Budget.RetriesUsed.Should().Be(1, "перевыдача считается отдельным потолком");
        team.Stage.Should().Be(TeamImplementStage.Wave, "человека не зовём — справляемся сами");
        _tasks.GetById(failed.Id)!.Description.Should().Contain("Повторная попытка",
            "исполнитель должен знать причину прошлого провала");
    }

    [Fact]
    public async Task ПровалЗадачи_ВтораяНеудача_Эскалация()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-retry2");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var failed = _tasks.MarkClaudeResult(first[0].Id, "error")!;
        await _sut.OnTaskFailedAsync(failed);

        await _sut.OnTaskFailedAsync(_tasks.GetById(failed.Id)!);

        var team = Team(session.Id);
        team.Budget.RetriesUsed.Should().Be(1, "второй перевыдачи не даём");
        team.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "провал дважды — человек решает, кому отдать");
    }

    [Fact]
    public async Task ПровалЗадачи_ПотолокПеревыдачВыбран_СразуЭскалация()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-retry-cap");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        Team(session.Id).Budget.RetriesUsed = Team(session.Id).Budget.MaxRetries;
        var failed = _tasks.MarkClaudeResult(first[0].Id, "error")!;

        await _sut.OnTaskFailedAsync(failed);

        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        _tasks.GetById(failed.Id)!.Description.Should().NotContain("Повторная попытка");
    }

    [Fact]
    public async Task СторожВолн_ВолнаМолчитДольшеТаймаута_ПоднимаетКарточку()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-stalled");
        await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        // Волна не подаёт признаков жизни четыре часа — при дефолтном таймауте это зависание
        var stalledTeam = Team(session.Id);
        stalledTeam.WaveStartedAt = DateTime.UtcNow.AddHours(-4);
        stalledTeam.WaveActivityAt = DateTime.UtcNow.AddHours(-4);

        await _sut.CheckStalledWavesAsync();

        var team = Team(session.Id);
        team.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        team.WaveStartedAt.Should().BeNull("повторно ту же волну сторож не эскалирует");
    }

    [Fact]
    public async Task СторожВолн_ВолнаВПределахТаймаута_Молчит()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-fresh");
        await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        await _sut.CheckStalledWavesAsync();

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Wave);
    }

    [Fact]
    public async Task СторожВолн_ДолгаяНоЖиваяВолна_НеЭскалируется()
    {
        // Таймаут считается от последней активности: волна из долгих задач идёт часами,
        // но пока в ней что-то закрывается — она живая, а не зависшая
        var (session, plan) = await MakeRunningStabAsync("wave-long-alive");
        await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var team = Team(session.Id);
        team.WaveStartedAt = DateTime.UtcNow.AddHours(-6);
        team.WaveActivityAt = DateTime.UtcNow.AddMinutes(-5);

        await _sut.CheckStalledWavesAsync();

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Wave,
            "ложная тревога на длинной работе — худший вид карточки: человек перестаёт им верить");
    }

    // --- Мёртвая зона конвейера: страховка сторожа (прод 2026-08-17) ---

    [Fact]
    public async Task СторожВолн_МёртваяЗона_ЗакрытаяВолнаБезРаздачи_ПоднимаетКарточкуРовноОдинРаз()
    {
        // Волна закрыта, следующая не роздана (WaveStartedAt пуст) — прежний сторож такое
        // состояние не видел вовсе: бейдж «волна N из M», работы нет, тишина. Карточка
        // WaveStalled уводит практику в «ждёт решения», поэтому второй тик не дублирует её.
        var (session, plan) = await MakeRunningStabAsync("wave-deadzone", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        // Решение человека вернуло практику в Wave, но раздача не случилась — мёртвая зона
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Wave; return true; });
        _sessions.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddHours(-1);

        await _sut.CheckStalledWavesAsync();
        await _sut.CheckStalledWavesAsync();

        var open = await _sessions.GetOpenTeamEscalationsAsync(session.Id);
        open.Where(c => c.Kind == TeamEscalationKind.WaveStalled).Should().ContainSingle(
            "карточка о стоящем конвейере нужна ровно одна");
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "карточка перевела практику в ожидание человека — повторных эскалаций нет");
    }

    [Fact]
    public async Task СторожВолн_МёртваяЗонаВПределахТаймаута_Молчит()
    {
        // Отсчёт мёртвой зоны — от UpdatedAt чата: сразу после решения человека паниковать рано
        var (session, plan) = await MakeRunningStabAsync("wave-deadzone-fresh", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Wave; return true; });
        _sessions.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddMinutes(-1);

        await _sut.CheckStalledWavesAsync();

        var open = await _sessions.GetOpenTeamEscalationsAsync(session.Id);
        open.Where(c => c.Kind == TeamEscalationKind.WaveStalled).Should().BeEmpty(
            "порог мёртвой зоны ещё не вышел");
    }

    // D2 (ревью 2026-08-17): у карточки мёртвой зоны свой набор кнопок — «Снять» убрана.
    // TaskId у карточки нет, ветка drop в SessionManager пуста, а блок раздачи по состоянию
    // под ней запускал бы следующую волну: подпись кнопки врала о последствии. «Перезапустить»
    // запускает её честной подписью (докрут по состоянию, повод StateCatchUp).
    [Fact]
    public async Task КарточкаМёртвойЗоны_БезКнопкиСнять_ПерезапускРаздаётВолну()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-deadzone-buttons");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        // Мёртвая зона при включённых авто: волна закрылась, пока висела карточка (waitsHuman
        // не двигает конвейер), и раздачу после ответа никто не позвал — форма инцидента 17.08
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Wave; return true; });
        _sessions.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddHours(-1);

        await _sut.CheckStalledWavesAsync();

        var card = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveStalled);
        card.Actions.Select(a => a.Id).Should().NotContain("drop", "снимать с карточки мёртвой зоны нечего");
        card.Actions.Select(a => a.Id).Should().Equal("restart", "finish");

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, card.Id, "restart", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("«Перезапустить» запускает следующую волну — уже честной подписью");
    }

    // --- Пульс волны (КР-наблюдаемость, этап 1) ---

    private List<(string Group, ClaudeHomeServer.Protocol.ServerMessage Message)> HubSends()
    {
        lock (_hubSendsLock) return [.. _hubSends];
    }

    // Возвращаем штабу «свободный» статус: MakeRunningStabAsync держит его Working, а
    // Working без живого прогона для пульса — мёртвый штаб (dead), что мешает проверкам
    // тихих границ liveness.
    private void FreeStab(string sessionId) =>
        _sessions.GetById(sessionId)!.Status = SessionStatus.Active;

    // Активность волны = max по UpdatedAt задач волны, дочерних чатов-исполнителей и
    // старта волны: закрытие/перевыдача двигают UpdatedAt задачи, ход исполнителя —
    // UpdatedAt его чата. Молчаливая по задачам волна жива, пока работает исполнитель.
    [Fact]
    public async Task Пульс_АктивностьСчитаетсяПоВсемТочкамВолны()
    {
        var (session, plan) = await MakeRunningStabAsync("pulse-activity");
        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var task = _tasks.GetById(created[0].Id)!;
        // Вся волна молчит 45 минут — за пределами обоих порогов
        var aged = DateTime.UtcNow.AddMinutes(-45);
        _sessions.WithTeamState(session.Id, t => { t.WaveStartedAt = aged; t.WaveActivityAt = aged; return true; });
        task.UpdatedAt = aged;
        FreeStab(session.Id);

        _sut.BuildWaveSnapshot(_sessions.GetById(session.Id)!)!.Liveness
            .Should().Be(WaveLiveness.Stalled);

        // Дочерний чат исполнителя обновился только что — волна снова жива
        await _sessions.CreateAsync(session.ProjectId!, ClaudeMode.Auto,
            taskId: task.Id, taskExecution: true);
        var snap = _sut.BuildWaveSnapshot(_sessions.GetById(session.Id)!)!;
        snap.Liveness.Should().Be(WaveLiveness.Alive,
            "ход исполнителя двигает UpdatedAt его чата — это тоже активность волны");
        snap.LastActivityAt.Should().BeOnOrAfter(DateTime.UtcNow.AddSeconds(-5));
        snap.QuietSeconds.Should().BeLessThan(60);
        snap.TasksTotal.Should().Be(1);
        snap.TasksActive.Should().Be(1, "задача волны ещё не в Done");
    }

    [Theory]
    [InlineData(-5, "alive")]
    [InlineData(-20, "quiet")]
    [InlineData(-45, "stalled")]
    public async Task Пульс_ГраницыТишины(int ageMinutes, string expectedToken)
    {
        // Дефолтные пороги: quiet > 15 мин, stalled > 30 мин (тишина ≤ порога — прежняя
        // ступень). Сравниваем по wire-токену: он же уходит фронту.
        var (session, plan) = await MakeRunningStabAsync("pulse-boundary-" + -ageMinutes);
        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var aged = DateTime.UtcNow.AddMinutes(ageMinutes);
        _sessions.WithTeamState(session.Id, t => { t.WaveStartedAt = aged; t.WaveActivityAt = aged; return true; });
        _tasks.GetById(created[0].Id)!.UpdatedAt = aged;
        FreeStab(session.Id);

        var snap = _sut.BuildWaveSnapshot(_sessions.GetById(session.Id)!)!;

        TeamWaveService.LivenessToken(snap.Liveness).Should().Be(expectedToken);
        snap.QuietSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Пульс_МёртвыйШтаб_WorkingБезЖивогоПрогона()
    {
        // Чат заявляет работу (Working), а прогона CLI нет — ход убит сбоем и не разобран.
        // Это обвал (dead), а не тишина: приоритет выше quiet/stalled.
        var (session, plan) = await MakeRunningStabAsync("pulse-dead");
        await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var info = _sessions.GetById(session.Id)!;
        info.Status.Should().Be(SessionStatus.Working, "предпосылка: штаб числится занятым");

        var snap = _sut.BuildWaveSnapshot(info)!;
        snap.Liveness.Should().Be(WaveLiveness.Dead,
            "Working без живого прогона — мёртвый штаб, даже при свежей активности");
        snap.TasksActive.Should().Be(1);

        // Свободный штаб между ходами — норма волны, не dead
        FreeStab(session.Id);
        _sut.BuildWaveSnapshot(_sessions.GetById(session.Id)!)!.Liveness
            .Should().Be(WaveLiveness.Alive);
    }

    // Сценарий A (major, ревью этапа 1): последняя задача волны, исполнитель честно
    // собирает проект 45 минут. Все UpdatedAt-якоря статичны (двигаются только на границах
    // ходов), но прогон CLI ребёнка жив — это активность «сейчас», а не «зависло».
    // Родительство чата-исполнителя закрепляем через SetParent, а не вычисляемым по задаче
    // путём: Session.TaskSourceSessionResolver статический, и параллельный тестовый класс
    // (WebApplicationFactory со своей TaskManager) переприсваивает его себе — вычисляемый
    // ParentSessionId флакует в null. Для пульса оба пути — один и тот же ребёнок
    // (ParentSessionId), авто-резолв отдельно покрыт тестом активности выше.
    [Fact]
    public async Task Пульс_ЖивойХодИсполнителяСпасаетОтЛожногоЗависания()
    {
        var (session, plan) = await MakeRunningStabAsync("pulse-live-executor");
        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var task = _tasks.GetById(created[0].Id)!;
        // Волна молчит 45 минут — за пределами обоих порогов
        var aged = DateTime.UtcNow.AddMinutes(-45);
        _sessions.WithTeamState(session.Id, t => { t.WaveStartedAt = aged; t.WaveActivityAt = aged; return true; });
        task.UpdatedAt = aged;
        FreeStab(session.Id);
        // Чат-исполнитель создан давно и молчит (UpdatedAt — с начала хода), но ход идёт
        var child = await _sessions.CreateAsync(session.ProjectId!, ClaudeMode.Auto,
            taskId: task.Id, taskExecution: true);
        _sessions.SetParent(child.Id, session.Id, UserId);
        _sessions.GetById(child.Id)!.UpdatedAt = aged;
        MarkLiveTurn(child.Id);

        var snap = _sut.BuildWaveSnapshot(_sessions.GetById(session.Id)!)!;

        snap.Liveness.Should().Be(WaveLiveness.Alive,
            "живой прогон исполнителя — активность волны, даже когда все якоря UpdatedAt статичны");
        snap.QuietSeconds.Should().BeLessThan(60);
    }

    // Сценарий B (major, ревью этапа 1): стадия Checking после закрытия волны
    // (WaveStartedAt обнулён), ход финальной проверки идёт 45 минут. Прогон штаба жив —
    // не dead (прогон есть) и не stalled (проверка — тоже работа команды). Заодно фикс:
    // пульс в Checking штатен, стадия уходит в снапшот как есть.
    [Fact]
    public async Task Пульс_ЖивойХодФинальнойПроверкиСпасаетОтЛожногоЗависания()
    {
        var (session, plan) = await MakeRunningStabAsync("pulse-live-checking");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var second = _tasks.GetByProject(session.ProjectId!).First(t => t.Labels.Contains("волна 2"));
        _tasks.Update(second.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(second.Id)!);
        Team(session.Id).Stage.Should().Be(TeamImplementStage.Checking,
            "предпосылка: обе волны закрыты, координатор проверяет результат");

        // Всё молчит 45 минут, детей нет — единственная активность: идущий ход проверки
        var aged = DateTime.UtcNow.AddMinutes(-45);
        foreach (var t in _tasks.GetByProject(session.ProjectId!)) t.UpdatedAt = aged;
        _sessions.WithTeamState(session.Id, t => { t.WaveActivityAt = aged; return true; });
        var info = _sessions.GetById(session.Id)!;
        info.UpdatedAt = aged;
        info.Status = SessionStatus.Working; // ход финальной проверки идёт
        MarkLiveTurn(session.Id);

        var snap = _sut.BuildWaveSnapshot(info)!;

        snap.Stage.Should().Be(TeamImplementStage.Checking, "пульс в стадии проверки штатен");
        snap.Liveness.Should().Be(WaveLiveness.Alive,
            "живой ход проверки — активность: ни dead (прогон есть), ни stalled (проверка идёт)");
        snap.QuietSeconds.Should().BeLessThan(60);
    }

    // Инвариант эфемерности: пульс не пишется в history.json, не двигает MessageCount
    // и Session.UpdatedAt (по нему сортировка чатов и непрочитанность).
    [Fact]
    public async Task Пульс_НеПишетИсториюИНеДвигаетЧат()
    {
        var (session, plan) = await MakeRunningStabAsync("pulse-ephemeral");
        await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        FreeStab(session.Id);
        var before = _sessions.GetById(session.Id)!;
        var updatedAtBefore = before.UpdatedAt;
        var countBefore = before.MessageCount;
        var historyBefore = await _sessions.GetHistoryAsync(session.Id);

        await _sut.SendWavePulsesAsync();

        var after = _sessions.GetById(session.Id)!;
        after.UpdatedAt.Should().Be(updatedAtBefore,
            "по UpdatedAt идут сортировка и непрочитанность — пульс их не двигает");
        after.MessageCount.Should().Be(countBefore, "пульс — не сообщение чата");
        (await _sessions.GetHistoryAsync(session.Id)).Should().HaveCount(historyBefore.Count,
            "пульс эфемерный: в history.json ничего не добавляется");
    }

    // Пульс адресуется строго в session-группу своего штаба: не owner-wide (user_*),
    // не project_*, и штаб без работающей волны пульса не получает вовсе.
    [Fact]
    public async Task Пульс_УходитТолькоВГруппуСвоегоШтаба()
    {
        var (sessionA, planA) = await MakeRunningStabAsync("pulse-hub-a");
        await _sut.StartWaveAsync(sessionA, planA, TeamWaveTrigger.UserCommand);
        var (sessionB, planB) = await MakeRunningStabAsync("pulse-hub-b");
        await _sut.StartWaveAsync(sessionB, planB, TeamWaveTrigger.UserCommand);
        // Штаб на стадии планирования: волны нет — пульса нет
        var (sessionC, _, _) = await MakeStabAsync("pulse-hub-c");
        FreeStab(sessionA.Id);
        FreeStab(sessionB.Id);

        await _sut.SendWavePulsesAsync();

        var pulses = HubSends()
            .Where(s => s.Message is ClaudeHomeServer.Protocol.TeamWavePulseMessage)
            .ToList();
        pulses.Should().HaveCount(2, "по одному пульсу на штаб с идущей волной");
        pulses.Should().OnlyContain(p => p.Group == sessionA.Id || p.Group == sessionB.Id,
            "пульс — в группу сессии-штаба, не в user_/project_-группы");
        pulses.Select(p => p.Message.SessionId).Should()
            .BeEquivalentTo([sessionA.Id, sessionB.Id]);
        var a = (ClaudeHomeServer.Protocol.TeamWavePulseMessage)pulses
            .Single(p => p.Message.SessionId == sessionA.Id).Message;
        a.Stage.Should().Be("wave");
        a.WaveNumber.Should().Be(1);
        a.PlannedWaves.Should().Be(2);
        a.TasksTotal.Should().Be(1);
        a.Liveness.Should().Be("alive");
    }

    // --- Приёмка, круг 2 (2026-08-17): обратная сторона гейта авто-волн ---

    // D1, обратная сторона: «Добавить бюджет» — явная кнопка человека, и при СНЯТЫХ
    // авто-волнах она обязана раздать волну, а не поднять ещё один гейт поверх решения.
    [Fact]
    public async Task RespondEscalation_AddBudgetПриСнятыхАвтоволнах_РаздаётВолнуБезГейта()
    {
        var (session, plan) = await MakeRunningStabAsync("qa-addbudget-manual", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var budgetCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = "Бюджет итерации исчерпан",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, budgetCard);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, budgetCard.Id, "addBudget",
            userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("«Добавить бюджет» — кнопка человека: гейт авто-волн ей не нужен");
    }

    // D1, обратная сторона: «Продолжить» после «Остановить» — тоже явная кнопка.
    [Fact]
    public async Task RespondEscalation_ResumeПриСнятыхАвтоволнах_РаздаётВолнуБезГейта()
    {
        var (session, plan) = await MakeRunningStabAsync("qa-resume-manual", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        await _sessions.StopTeamImplementAsync(session.Id, UserId);
        var stopCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.Stopped,
            Title = "Практика остановлена",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Stopped),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, stopCard);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, stopCard.Id, "resume", userId: UserId);

        ok.Should().BeTrue();
        Team(session.Id).Stopped.Should().BeFalse();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("«Продолжить» — кнопка человека: работа возобновляется сразу");
    }

    // D3 (приёмка круга 2, починен кругом 3): карточка таймаута волны публикуется с TaskId,
    // когда молчит ровно ОДНА под-задача — «Снять» закрывает её и волной, как заявлено.
    // При нескольких молчащих кнопки «Снять» нет вовсе (WithoutDrop): одна кнопка не может
    // выбрать, какую из них снять.
    [Fact]
    public async Task КарточкаЗависшейВолны_КнопкаСнять_ЗакрываетПодЗадачу()
    {
        var (session, plan) = await MakeRunningStabAsync("qa-stalled-drop");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _sessions.WithTeamState(session.Id, t =>
        {
            t.WaveStartedAt = DateTime.UtcNow.AddHours(-5);
            t.WaveActivityAt = DateTime.UtcNow.AddHours(-5);
            return true;
        });

        await _sut.CheckStalledWavesAsync();

        var card = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveStalled);
        card.TaskId.Should().NotBeNull("иначе «Снять» нечего снимать — ветка drop не сработает");
        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, card.Id, "drop", userId: UserId);
        ok.Should().BeTrue();
        _tasks.GetById(first[0].Id)!.Status.Should().Be(TaskItemStatus.Done,
            "«Снять» обязана закрыть под-задачу зависшей волны");
    }

    // D2 + круг 3 (приёмка круга 2): «Перезапустить» стал явной командой человека — при
    // снятых авто-волнах клик по карточке мёртвой зоны раздаёт волну СРАЗУ, а не поднимает
    // гейт для второго клика. Гейт нужен докруту по состоянию, а не кнопке с подписью
    // «Перезапустить»: подпись снова обещает ровно то, что делает.
    [Fact]
    public async Task КарточкаМёртвойЗоны_ПерезапускПриСнятыхАвтоволнах_РаздаётВолнуСПервогоКлика()
    {
        var (session, plan) = await MakeRunningStabAsync("qa-deadzone-manual", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Wave; return true; });
        _sessions.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        await _sut.CheckStalledWavesAsync();
        var card = (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Single(c => c.Kind == TeamEscalationKind.WaveStalled);

        var ok = await _sessions.RespondTeamEscalationAsync(session.Id, card.Id, "restart", userId: UserId);

        ok.Should().BeTrue();
        _tasks.GetByProject(session.ProjectId!).Where(t => t.Labels.Contains("волна 2"))
            .Should().ContainSingle("«Перезапустить» — явная кнопка человека: раздача с первого клика");
        var team = Team(session.Id);
        team.Stage.Should().Be(TeamImplementStage.Wave);
        team.WaveStartedAt.Should().NotBeNull("сторож зависших волн снова тикает");
    }

    // D4 (приёмка круга 2, починен кругом 3): человек пишет в чат, пока висит гейт-карточка.
    // Каждое сообщение снимает стадию «ждёт решения» → докрут по состоянию → при снятых
    // авто-волнах поднимался ЕЩЁ ОДИН такой же гейт (с уведомлением, push и своим счётчиком
    // напоминаний), а прежняя оставалась открытой. Договор: на одну закрытую волну человеку
    // показывают ровно один открытый гейт, сколько бы сообщений он ни написал.
    [Fact]
    public async Task ГейтВолны_ОтветТекстом_НеПлодитДублейКарточек()
    {
        var (session, plan) = await MakeRunningStabAsync("qa-gate-text", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        await _sessions.SendMessageAsync(session.Id, "да, поехали", []);
        await _sessions.SendMessageAsync(session.Id, "ну что там", []);

        (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Where(c => c.Kind == TeamEscalationKind.WaveGate)
            .Should().ContainSingle("на одну закрытую волну человеку показывают один гейт, "
                + "а не по карточке на каждое его сообщение");
    }

    // Состояние режима переживает рестарт: то, что чинит фикс (стадия, ClosedWave, отсечки,
    // AutoWaves), лежит в data/sessions.json и читается обратно тем же сериализатором.
    [Fact]
    public async Task СостояниеРежима_ПослеГейта_ПереживаетРестартЧерезSessionsJson()
    {
        var (session, plan) = await MakeRunningStabAsync("qa-persist", autoWaves: false);
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);
        var live = Team(session.Id);

        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var onDisk = System.Text.Json.JsonSerializer.Deserialize<List<Session>>(
            await File.ReadAllTextAsync(Path.Combine(_dir, "sessions.json")), opts)!
            .Single(s => s.Id == session.Id).TeamImplement!;

        onDisk.Stage.Should().Be(live.Stage);
        onDisk.WaveNumber.Should().Be(live.WaveNumber);
        onDisk.ClosedWave.Should().Be(live.ClosedWave);
        onDisk.AutoWaves.Should().BeFalse("снятые авто-волны обязаны пережить рестарт — иначе гейт исчезнет");
        onDisk.WaveStartedAt.Should().Be(live.WaveStartedAt);
        onDisk.PlanCardId.Should().Be(live.PlanCardId);
    }

    // Старая запись sessions.json (до ветки) читается без потерь: полей режима в ней меньше,
    // недостающие берут дефолты, чат не теряется.
    [Fact]
    public void СтараяЗаписьSessionsJson_БезНовыхПолейРежима_ЧитаетсяСДефолтами()
    {
        // Форма файла — как его пишет SessionManager.SaveSessions (PascalCase + enum строкой)
        const string legacy = """
            [{"Id":"s-old","ProjectId":"p-1","ClaudeSessionId":"c-1","Status":"Finished",
              "TeamImplement":{"Enabled":true,"Stage":"Wave","WaveNumber":2}}]
            """;
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        var loaded = System.Text.Json.JsonSerializer.Deserialize<List<Session>>(legacy, opts)!.Single();

        loaded.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
        loaded.TeamImplement.WaveNumber.Should().Be(2);
        loaded.TeamImplement.ClosedWave.Should().Be(0);
        loaded.TeamImplement.WaveStartedAt.Should().BeNull();
        loaded.TeamImplement.AutoWaves.Should().BeTrue("дефолт режима — авто-волны включены");
    }

    // --- Бюджет: волна помещается целиком или не стартует ---

    // План из двух под-задач ОДНОЙ волны: обе уходят исполнителям разом
    private static TeamImplementPlan MakeParallelPlan(Persona backend, Persona frontend)
    {
        var plan = MakePlan(backend, frontend);
        plan.Subtasks[1].Wave = 1;
        return plan;
    }

    [Fact]
    public async Task StartWave_ОстаткаБюджетаНеХватаетНаВсюВолну_НеРаздаётНичего()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-budget-fit");
        var plan = MakeParallelPlan(backend, frontend);
        // В волне две задачи, а до потолка осталась одна
        var team = session.TeamImplement!;
        team.Budget.TasksUsed = team.Budget.MaxTasks - 1;

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty("волна либо помещается в остаток целиком, либо не стартует");
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty(
            "половина волны без исполнителей хуже честной остановки с карточкой");
        var after = Team(session.Id);
        after.Budget.TasksUsed.Should().Be(after.Budget.MaxTasks - 1, "отказ ничего не расходует");
        after.Budget.WavesUsed.Should().Be(0);
        after.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "человек получает карточку исчерпания, а не тишину");
        plan.Subtasks.Should().OnlyContain(s => s.TaskId == null);
    }

    [Fact]
    public async Task StartWave_ОстаткаХватаетРовноНаВолну_РаздаётЕёЦеликом()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-budget-exact");
        var plan = MakeParallelPlan(backend, frontend);
        var team = session.TeamImplement!;
        team.Budget.TasksUsed = team.Budget.MaxTasks - 2;

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().HaveCount(2);
        var after = Team(session.Id);
        after.Budget.TasksUsed.Should().Be(after.Budget.MaxTasks, "потолок выбран ровно, без перерасхода");
        after.Stage.Should().Be(TeamImplementStage.Wave);
    }

    [Fact]
    public async Task StartWave_ПараллельныеРаздачиОднойВолны_НеПлодятДублейИСчитаютЧестно()
    {
        // Раздачу зовут три независимые точки (подтверждение плана, авто-волна, кнопка
        // карточки) — они могут прийти одновременно, а int++ не атомарен
        var (session, plan) = await MakeRunningStabAsync("wave-race");

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand))));

        _tasks.GetByProject(session.ProjectId!).Should().ContainSingle(
            "волна раздаётся ровно один раз, сколько бы вызовов ни пришло разом");
        var team = Team(session.Id);
        team.Budget.TasksUsed.Should().Be(1);
        team.Budget.RunsUsed.Should().Be(1);
        team.Budget.WavesUsed.Should().Be(1, "потерянных инкрементов быть не должно");
    }

    // m1 (второй проход Глеба, e7aee793): у чата БЕЗ аккумулятора GetTeamPlanAsync каждое
    // чтение десериализует НОВЫЙ объект плана. Сценарий Глеба — две раздающие карточки
    // («Добавить бюджет» + «Продолжить») читают план ДО того, как записался снимок соседа:
    // семафор StartWaveAsync серилизует РАБОТУ, но не спасает от устаревшего АРГУМЕНТА —
    // без перечитывания под локом волна раздалась бы дважды.
    [Fact]
    public async Task StartWave_ДваНезависимыхСнимкаПланаПослеРестарта_НеПлодятДублейЗадач()
    {
        var (session, plan) = await MakeRestartedRunningStabAsync("wave-restart-race");

        // Независимые чтения — так же, как ResolveTeamEscalationAsync читает план заново
        // перед КАЖДЫМ вызовом TeamWaveStarter на очередной клик по карточке
        var snapshot1 = (await _sessions.GetTeamPlanAsync(session.Id, plan.Id))!;
        var snapshot2 = (await _sessions.GetTeamPlanAsync(session.Id, plan.Id))!;
        snapshot1.Should().NotBeSameAs(snapshot2,
            "без аккумулятора план десериализуется заново на каждое чтение — предпосылка гонки");

        await Task.WhenAll(
            _sut.StartWaveAsync(session, snapshot1, TeamWaveTrigger.UserCommand),
            _sut.StartWaveAsync(session, snapshot2, TeamWaveTrigger.UserCommand));

        _tasks.GetByProject(session.ProjectId!).Should().HaveCount(2,
            "волна из двух под-задач раздаётся один раз, а не дважды с двух устаревших снимков плана");
        var team = Team(session.Id);
        team.Budget.TasksUsed.Should().Be(2, "потерянных/задвоенных инкрементов не должно быть и здесь");
        team.Budget.RunsUsed.Should().Be(2);
        team.Budget.WavesUsed.Should().Be(1, "это одна и та же волна, а не две");
    }

    // m2 (второй проход Глеба): тот же корень, что m1, только для перевыдачи — два провала
    // РАЗНЫХ под-задач одной волны, каждый со своим устаревшим снимком плана, писали план
    // ЦЕЛИКОМ на диск: последняя запись затирала Attempts++ соседа, и вместо одной
    // перевыдачи каждой выходило бы (не всегда, но регулярно) две.
    [Fact]
    public async Task ПровалЗадачи_ПараллельныеПровалыРазныхПодзадачПослеРестарта_НеТеряютAttempts()
    {
        var (session, plan) = await MakeRestartedRunningStabAsync("wave-retry-race-restart");
        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        created.Should().HaveCount(2, "предпосылка: обе под-задачи волны 1 розданы разом");
        var failedA = _tasks.MarkClaudeResult(created[0].Id, "error")!;
        var failedB = _tasks.MarkClaudeResult(created[1].Id, "error")!;

        // Каждый OnTaskFailedAsync сам перечитывает план (ResolveContextAsync → GetTeamPlanAsync) —
        // без аккумулятора это новый объект на каждый вызов, как и у m1
        await Task.WhenAll(
            _sut.OnTaskFailedAsync(failedA),
            _sut.OnTaskFailedAsync(failedB));

        var team = Team(session.Id);
        team.Budget.RetriesUsed.Should().Be(2, "у каждой из двух под-задач — ровно одна перевыдача");
        var reloaded = (await _sessions.GetTeamPlanAsync(session.Id, plan.Id))!;
        reloaded.Subtasks.Should().OnlyContain(s => s.Attempts == 2,
            "провал соседней под-задачи не должен затирать Attempts++ на диске (m2)");
    }

    [Fact]
    public async Task ПровалЗадачи_ПараллельныеПровалы_ДаютРовноОднуПеревыдачу()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-retry-race");
        var first = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);
        var failed = _tasks.MarkClaudeResult(first[0].Id, "error")!;

        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => _sut.OnTaskFailedAsync(_tasks.GetById(failed.Id)!))));

        var team = Team(session.Id);
        team.Budget.RetriesUsed.Should().Be(1, "перевыдача одна, даже если провал обработан несколько раз");
        plan.Subtasks[0].Attempts.Should().Be(2);
    }

    [Fact]
    public void CoordinatorTurn_НесётБюджетИПротоколЭскалации()
    {
        var team = new SessionTeamImplement
        {
            Stage = TeamImplementStage.Wave,
            Budget = new TeamImplementBudget { TasksUsed = 3, WavesUsed = 1, RunsUsed = 3 },
        };

        var text = TeamImplementPrompts.CoordinatorTurn(team);

        text.Should().Contain("задачи 3/12");
        text.Should().Contain("<escalate:deviation>");
        text.Should().Contain("<escalate:check>");
        // Продуктовая развилка ушла из маркеров в ASK (единый канал вопросов с интервью):
        // в живом ходу координатор спрашивает инструментом, а не публикует карточку с полем
        text.Should().NotContain("<escalate:decision>");
        text.Should().Contain("AskUserQuestion");
    }

    [Fact]
    public void CoordinatorTurn_ВОжиданииВводной_НесётПравилаКлассификации()
    {
        var team = new SessionTeamImplement { Stage = TeamImplementStage.Idle };

        var text = TeamImplementPrompts.CoordinatorTurn(team);

        text.Should().Contain("ждём следующую вводную");
        text.Should().Contain("МЕНЯТЬ ФАЙЛЫ", "границу «работа/разговор» проводит координатор");
        text.Should().Contain("<team:work>");
        text.Should().Contain("уточняющий вопрос", "спорное трактуем как разговор");
    }

    [Fact]
    public void CoordinatorTurn_ВоВремяВолны_КлассификациюНеТребует()
    {
        var team = new SessionTeamImplement { Stage = TeamImplementStage.Wave, WaveNumber = 1 };

        TeamImplementPrompts.CoordinatorTurn(team).Should().NotContain("<team:work>",
            "вторая волна поверх идущей не разворачивается");
    }

    // --- Э8: волна идёт только по подтверждённой последней версии плана ---

    [Fact]
    public async Task StartWave_ПоУстаревшейВерсииПлана_НеСтартует()
    {
        // После уточнений опубликован план v2 — доигрывать v1 нельзя, иначе авто-волна
        // обошла бы обязательное подтверждение новой версии
        var (session, backend, frontend) = await MakeStabAsync("wave-stale");
        var plan = MakePlan(backend, frontend);
        plan.Version = 1;
        _sessions.WithTeamState(session.Id, t =>
        {
            t.PlanVersion = 2;
            t.ApprovedPlanVersion = 1;
            return true;
        });

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty("актуальна версия 2 — старый план не доигрывается");
        Team(session.Id).Budget.TasksUsed.Should().Be(0, "бюджет на устаревший план не тратится");
    }

    [Fact]
    public async Task StartWave_ПоНеподтверждённойВерсииПлана_НеСтартует()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-unapproved");
        var plan = MakePlan(backend, frontend);
        plan.Version = 2;
        _sessions.WithTeamState(session.Id, t =>
        {
            t.PlanVersion = 2;
            t.ApprovedPlanVersion = 1;
            return true;
        });

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().BeEmpty("новая версия плана ждёт кнопки «Запустить» даже при авто-волнах");
    }

    [Fact]
    public async Task StartWave_СостояниеБезВерсий_РаботаетПоСтарому()
    {
        // Обратная совместимость: у чатов, начатых до Э8, версий в состоянии нет (нули) —
        // гард выключен, иначе идущая практика встала бы прямо на апгрейде сервера
        var (session, backend, frontend) = await MakeStabAsync("wave-legacy");
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(session, plan, TeamWaveTrigger.UserCommand);

        created.Should().HaveCount(1);
    }

    [Fact]
    public async Task CheckStalledWaves_ВСтадииИнтервью_НеЭскалирует()
    {
        // Ожидание ответа человека — не зависание: в интервью таймаут волны не тикает
        var (session, backend, frontend) = await MakeStabAsync("wave-interview-timeout");
        await _sut.StartWaveAsync(session, MakePlan(backend, frontend), TeamWaveTrigger.UserCommand);
        _sessions.WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Interview;
            t.WaveStartedAt = DateTime.UtcNow.AddDays(-1);
            t.WaveActivityAt = DateTime.UtcNow.AddDays(-1);
            return true;
        });

        await _sut.CheckStalledWavesAsync();

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Interview, "карточки зависания в интервью нет");
    }

    [Theory]
    // Уведомление и push идут от лица персоны штаба; без персоны — обезличенный фолбэк.
    // Заголовок разведён по виду карточки: в списке уведомлений видно только его, и по
    // нему должно быть понятно, горит работа или просто ждёт решения/ответов.
    // Гейт волны и информационные карточки — прежний текст «нужно ваше решение».
    [InlineData("Алекс", TeamEscalationKind.WaveGate, "Алекс: нужно ваше решение")]
    [InlineData(null, TeamEscalationKind.WaveGate, "Команда ждёт вашего решения")]
    [InlineData("Алекс", TeamEscalationKind.WaveAdded, "Алекс: нужно ваше решение")]
    // Вопросы интервью и тупик с уточнениями — ждём ответов, а не решения
    [InlineData("Алекс", TeamEscalationKind.NeedsClarification, "Алекс ждёт ответов по задаче")]
    [InlineData(null, TeamEscalationKind.NeedsClarification, "Команда ждёт ответов по задаче")]
    // Не-информационные карточки, кроме гейта и вопросов: практика реально остановлена
    [InlineData("Алекс", TeamEscalationKind.Blocker, "Алекс: практика остановлена")]
    [InlineData("Алекс", TeamEscalationKind.TaskFailed, "Алекс: практика остановлена")]
    [InlineData("Алекс", TeamEscalationKind.PlanDeviation, "Алекс: практика остановлена")]
    [InlineData("Алекс", TeamEscalationKind.CheckFailed, "Алекс: практика остановлена")]
    [InlineData("Алекс", TeamEscalationKind.ProductDecision, "Алекс: практика остановлена")]
    [InlineData("Алекс", TeamEscalationKind.WaveStalled, "Алекс: практика остановлена")]
    [InlineData("Алекс", TeamEscalationKind.Stopped, "Алекс: практика остановлена")]
    [InlineData(null, TeamEscalationKind.BudgetExhausted, "Практика остановлена")]
    public void WaitingTitle_ПерсонифицированныйЗаголовок(string? persona, TeamEscalationKind kind, string expected) =>
        TeamImplementPrompts.WaitingTitle(persona, kind).Should().Be(expected);

    // --- Повторные напоминания о висящей карточке остановки (прод 15→16.08) ---

    private List<ClaudeHomeServer.Protocol.NotificationMessage> Notifications()
    {
        lock (_notificationsLock) return [.. _notifications];
    }

    // Опубликовать «состаренную» карточку остановки: порог первого напоминания считается
    // от CreatedAt, поэтому для проверки порога карточка создаётся сразу постаревшей
    // (init-свойство задаётся в инициализаторе).
    private async Task<TeamEscalation> PublishAgedCardAsync(string sessionId, TeamEscalationKind kind,
        TimeSpan age, string title)
    {
        var card = new TeamEscalation
        {
            Kind = kind,
            Title = title,
            Details = "Тестовая карточка",
            CreatedAt = DateTime.UtcNow - age,
            Actions = TeamEscalationActions.For(kind),
        };
        await _sessions.PublishTeamEscalationAsync(sessionId, card);
        return card;
    }

    [Fact]
    public async Task Напоминание_КарточкаБезОтветаДольшеЧаса_ПовторяетУведомление()
    {
        var (session, _, _) = await MakeStabAsync("remind-first");
        var card = await PublishAgedCardAsync(session.Id, TeamEscalationKind.BudgetExhausted,
            TimeSpan.FromHours(2), "Бюджет итерации израсходован");

        await _sut.CheckAwaitingEscalationsAsync();

        Notifications().Should().ContainSingle("первый оклик человек пропустил — практика не молчит");
        var n = Notifications()[0];
        n.Title.Should().Be("Алекс: практика всё ещё ждёт", "напоминание — от лица координатора штаба");
        n.Body.Should().Be("Бюджет итерации израсходован · без ответа 2 ч",
            "заголовок карточки и целые часы без ответа");
        n.SessionId.Should().Be(session.Id);
        (await _sessions.GetOpenTeamEscalationsAsync(session.Id)).Single(c => c.Id == card.Id)
            .RemindersSent.Should().Be(1, "счётчик напоминаний живёт на карточке");
    }

    [Fact]
    public async Task Напоминание_ДоИстеченияЧаса_Молчит()
    {
        var (session, _, _) = await MakeStabAsync("remind-fresh");
        await PublishAgedCardAsync(session.Id, TeamEscalationKind.BudgetExhausted,
            TimeSpan.FromMinutes(30), "Бюджет итерации израсходован");

        await _sut.CheckAwaitingEscalationsAsync();

        Notifications().Should().BeEmpty("порог первого напоминания — час после публикации");
    }

    [Fact]
    public async Task Напоминание_ВтороеЧерезЧетыреЧасаПервого_ТретьегоНет()
    {
        var (session, _, _) = await MakeStabAsync("remind-second");
        var card = await PublishAgedCardAsync(session.Id, TeamEscalationKind.BudgetExhausted,
            TimeSpan.FromHours(2), "Бюджет итерации израсходован");

        await _sut.CheckAwaitingEscalationsAsync();
        Notifications().Should().ContainSingle();

        // Состариваем первое напоминание на четыре часа: карточка — живой объект аккумулятора
        var open = (await _sessions.GetOpenTeamEscalationsAsync(session.Id)).Single(c => c.Id == card.Id);
        open.RemindersSent.Should().Be(1);
        open.LastReminderAt = DateTime.UtcNow.AddHours(-4);

        await _sut.CheckAwaitingEscalationsAsync();
        Notifications().Should().HaveCount(2, "второе напоминание — через 4 часа после первого");

        // Лимит исчерпан: даже сутки молчания третьего оклика не дадут
        open.LastReminderAt = DateTime.UtcNow.AddHours(-25);
        await _sut.CheckAwaitingEscalationsAsync();
        Notifications().Should().HaveCount(2, "максимум два напоминания — навязчивость хуже пропущенного сигнала");
        (await _sessions.GetOpenTeamEscalationsAsync(session.Id)).Single(c => c.Id == card.Id)
            .RemindersSent.Should().Be(2);
    }

    [Fact]
    public async Task Напоминание_ЧеловекВЧате_Молчит()
    {
        var (session, _, _) = await MakeStabAsync("remind-viewer");
        await PublishAgedCardAsync(session.Id, TeamEscalationKind.BudgetExhausted,
            TimeSpan.FromHours(2), "Бюджет итерации израсходован");
        _sessions.AddViewer(session.Id, "conn-1");

        await _sut.CheckAwaitingEscalationsAsync();

        Notifications().Should().BeEmpty("человек и так видит карточку в открытой ленте");
    }

    [Fact]
    public async Task Напоминание_ИнформационнаяКарточка_НеНапоминает()
    {
        var (session, _, _) = await MakeStabAsync("remind-info");
        // Добавочная волна — не остановка: работа идёт, карточка клика не ждёт
        await PublishAgedCardAsync(session.Id, TeamEscalationKind.WaveAdded,
            TimeSpan.FromHours(2), "Новая вводная в работе");

        await _sut.CheckAwaitingEscalationsAsync();

        Notifications().Should().BeEmpty("информационные карточки напоминаний не порождают");
    }

    [Fact]
    public async Task НапоминаниеПослеОтветаНаКарточку_Прекращается()
    {
        var (session, _, _) = await MakeStabAsync("remind-answered");
        var card = await PublishAgedCardAsync(session.Id, TeamEscalationKind.BudgetExhausted,
            TimeSpan.FromHours(2), "Бюджет итерации израсходован");
        // Занимаем штаб: ответ по карточке уходит координатору ходом (очередь, не запуск CLI)
        _sessions.GetById(session.Id)!.Status = SessionStatus.Working;
        (await _sessions.RespondTeamEscalationAsync(session.Id, card.Id, "addBudget", userId: UserId))
            .Should().BeTrue();

        await _sut.CheckAwaitingEscalationsAsync();

        Notifications().Should().BeEmpty("карточка закрыта решением человека — напоминать не о чем");
    }

    // Счётчик напоминаний живёт на карточке в истории и переживает рестарт: после
    // перезапуска сервера (аккумулятор чата не оживлён, карточка читается с диска) оклик
    // не начинается заново — иначе человек получил бы второе «первое» напоминание.
    [Fact]
    public async Task Напоминание_ПослеРестартаСервера_СчётчикНеНачинаетсяЗаново()
    {
        var (session, _, _) = await MakeStabAsync("remind-restart", resumeSessionId: "csid-remind-restart");
        await PublishAgedCardAsync(session.Id, TeamEscalationKind.BudgetExhausted,
            TimeSpan.FromHours(2), "Бюджет итерации израсходован");

        await _sut.CheckAwaitingEscalationsAsync();
        Notifications().Should().ContainSingle("первое напоминание отправлено до рестарта");

        ClearAccumulator(GetEntry(session.Id));
        await _sut.CheckAwaitingEscalationsAsync();

        Notifications().Should().ContainSingle("после рестарта счётчик прочитан с диска, а не обнулён");
    }

    // --- Э5: непрерывный контур — новая вводная разворачивает волну с нуля ---

    [Fact]
    public async Task НоваяВводная_ПослеЗавершённойИтерации_РазворачиваетВолнуИБюджетСНуля()
    {
        var (session, backend, frontend) = await MakeStabAsync("continuous");
        _plannerAnswer = $$"""
            {"summary":"Экспорт задач в CSV","subtasks":[
              {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
               "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
               "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
              {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
               "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
               "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
            """;
        var (plan, _) = await _sessions.CreateTeamPlanAsync(session.Id, "Экспорт задач в CSV", UserId);
        await _sessions.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run, userId: UserId);

        // Штаб «занят»: сводка закрытой волны уходит в очередь, а не запускает реальный ход
        // claude, который на CI без claude падает и роняет Checking → AwaitingDecision (см. подробно
        // в MakeRunningStabAsync). Ход штаба тест ниже симулирует вручную (HandleTeamTurnEndAsync).
        _sessions.GetById(session.Id)!.Status = SessionStatus.Working;

        // Итерация отработала: обе волны закрыты исполнителями, координатор подвёл итог
        foreach (var wave in (int[])[1, 2])
        {
            var task = _tasks.GetByProject(session.ProjectId!).Single(t => t.Labels.Contains($"волна {wave}"));
            _tasks.Update(task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
            await _sut.OnTeamTaskDoneAsync(_tasks.GetById(task.Id)!);
        }
        Team(session.Id).Stage.Should().Be(TeamImplementStage.Checking);
        await _sessions.HandleTeamTurnEndAsync(session.Id, "Итерация завершена: 2 задачи", failed: false);
        Team(session.Id).Stage.Should().Be(TeamImplementStage.Idle, "режим не выключился вместе с планом");
        var spent = Team(session.Id).Budget;
        spent.TasksUsed.Should().Be(2, "предпосылка: бюджет прошлой итерации израсходован");

        // Человек пишет следующую вводную — режим включать заново не нужно
        _sessions.GetById(session.Id)!.Status = SessionStatus.Working;
        await _sessions.SendMessageAsync(session.Id, "теперь добавь выгрузку в XLSX", []);
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        _plannerAnswer = $$"""
            {"summary":"Выгрузка в XLSX","subtasks":[
              {"title":"XLSX-выгрузка","goal":"Формат xlsx в экспорте",
               "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
               "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
            """;
        await _sessions.HandleTeamTurnEndAsync(session.Id,
            "Беру в работу.\n<team:work>добавить выгрузку в XLSX</team>", failed: false);

        // Волна развернулась сама: задача создана, счёт волн и бюджет — с нуля
        var added = _tasks.GetByProject(session.ProjectId!).Single(t => t.Title == "XLSX-выгрузка");
        added.PersonaId.Should().Be(backend.Id);
        added.SourceSessionId.Should().Be(session.Id);
        var team = Team(session.Id);
        team.Stage.Should().Be(TeamImplementStage.Wave);
        team.WaveNumber.Should().Be(1);
        team.PlannedWaves.Should().Be(1);
        team.Budget.TasksUsed.Should().Be(1, "бюджет обнулён вводной человека");
        team.Budget.WavesUsed.Should().Be(1);

        // Волна новой итерации закрывается штатно: старый ClosedWave её не блокирует
        _tasks.Update(added.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(added.Id)!);
        Team(session.Id).Stage.Should().Be(TeamImplementStage.Checking);
    }

    // --- КР-наблюдаемость, этап 3: перезапуск задачи/волны человеком ---

    // Штаб с опубликованным планом из ДВУХ под-задач одной волны (обе розданы) и заглушенной
    // тишиной: всё молчит дольше StalledMinutes (45 мин при пороге 30), штаб свободен —
    // форма зависшей волны, ради которой в поповере есть кнопка перезапуска.
    private async Task<(Session Session, TaskItem A, TaskItem B)> MakeStalledWaveAsync(string name)
    {
        var (session, backend, frontend) = await MakeStabAsync(name);
        await _sessions.SetTeamImplementAutoAsync(session.Id, true, UserId);
        _plannerAnswer = $$"""
            {"summary":"Экспорт","subtasks":[
              {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
               "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
               "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
              {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
               "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
               "files":["frontend/src/components/Toolbar.tsx"],"wave":1,"doneCriteria":"файл скачивается"}]}
            """;
        var (plan, reason) = await _sessions.CreateTeamPlanAsync(session.Id, "Экспорт", UserId);
        reason.Should().BeNull();
        var created = await _sut.StartWaveAsync(_sessions.GetById(session.Id)!, plan!,
            TeamWaveTrigger.UserCommand);
        created.Should().HaveCount(2, "обе под-задачи одной волны розданы разом");

        var aged = DateTime.UtcNow.AddMinutes(-45);
        _sessions.WithTeamState(session.Id, t => { t.WaveStartedAt = aged; t.WaveActivityAt = aged; return true; });
        foreach (var t in created) _tasks.GetById(t.Id)!.UpdatedAt = aged;
        FreeStab(session.Id);
        return (session, created[0], created[1]);
    }

    // Чат-исполнитель для задачи волны: linked-сессия с отметкой запуска. liveTurn=true —
    // ещё и живой прогон (приём MarkLiveTurn): HasLiveTurnProcess — тот же предикат, которым
    // пульс волны считает идущий ход активностью. busyStatus — статус Starting/Working/
    // Waiting (занятое исполнение, как его видит перевыдача).
    private async Task<Session> AttachExecutorAsync(TaskItem task, Session stab,
        bool liveTurn = false, bool busyStatus = false)
    {
        var child = await _sessions.CreateAsync(stab.ProjectId!, ClaudeMode.Auto,
            taskId: task.Id, taskExecution: true);
        _tasks.MarkClaudeStarted(task.Id, child.Id, DateTime.UtcNow);
        if (liveTurn) MarkLiveTurn(child.Id);
        if (busyStatus) _sessions.GetById(child.Id)!.Status = SessionStatus.Working;
        return _sessions.GetById(child.Id)!;
    }

    [Fact]
    public async Task ПерезапускЗадачи_Проваленная_ПеревыдачаТемЖеПутём()
    {
        var (session, a, _) = await MakeStalledWaveAsync("restart-task-failed");
        var failed = _tasks.MarkClaudeResult(a.Id, "error")!;

        var result = await _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id);

        result.Outcome.Should().Be("reissued");
        var team = Team(session.Id);
        team.Budget.RetriesUsed.Should().Be(1, "кнопка человека считает тот же потолок перевыдач");
        team.Budget.RunsUsed.Should().Be(3, "старт волны (2) плюс перевыдача (1)");
        _tasks.GetById(a.Id)!.Description.Should().Contain("Повторная попытка",
            "исполнителю уходит та же дописка причины, что при провале хода");
        failed.ClaudeResult.Should().Be("error");
    }

    [Fact]
    public async Task ПерезапускЗадачи_ЖивойИсполнительСНедавнейАктивностью_ОтказСОбъяснением()
    {
        var (session, a, _) = await MakeStalledWaveAsync("restart-task-live");
        await AttachExecutorAsync(a, session, liveTurn: true);
        // Прогон жив, задача обновилась только что — это работа, а не зависание
        _tasks.GetById(a.Id)!.UpdatedAt = DateTime.UtcNow;

        var act = () => _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*мин назад была активность*");
        Team(session.Id).Budget.RetriesUsed.Should().Be(0, "отказ ничего не расходует");
    }

    [Fact]
    public async Task ПерезапускЗадачи_ЖивойНоЗависшийПрогон_ТишинаДольшеПорога_Перевыдаёт()
    {
        var (session, a, _) = await MakeStalledWaveAsync("restart-task-hung");
        // Прогон формально жив, но вся активность задачи — 45 минут назад (гейт пускает)
        var child = await AttachExecutorAsync(a, session, liveTurn: true);
        _tasks.GetById(a.Id)!.UpdatedAt = DateTime.UtcNow.AddMinutes(-45);
        child.UpdatedAt = DateTime.UtcNow.AddMinutes(-45);

        var result = await _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id);

        result.Outcome.Should().Be("reissued");
        result.Message.Should().Contain("попытка 2", "повторная попытка той же под-задачи");
        _tasks.GetById(a.Id)!.Description.Should().Contain("не подавало признаков жизни",
            "зависшему исполнителю объясняем причину перезапуска, а не провала");
    }

    [Fact]
    public async Task ПерезапускЗадачи_Завершённая_Отказ()
    {
        var (session, a, _) = await MakeStalledWaveAsync("restart-task-done");
        _tasks.Update(a.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var act = () => _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*уже завершена*");
    }

    [Fact]
    public async Task ПерезапускЗадачи_ПопыткиИсчерпаны_КарточкаВЛентуеСообщениеЧеловеку()
    {
        var (session, a, _) = await MakeStalledWaveAsync("restart-task-cap");
        var failed = _tasks.MarkClaudeResult(a.Id, "error")!;
        await _sut.OnTaskFailedAsync(failed); // первая перевыдача — попытка 2, дальше потолок

        var result = await _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id);

        result.Outcome.Should().Be("escalated", "вторая перевыдача той же под-задачи не даётся");
        result.Message.Should().Contain("перевыдача не помогла");
        (await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Should().ContainSingle(c => c.Kind == TeamEscalationKind.TaskFailed);
    }

    [Fact]
    public async Task ПерезапускЗадачи_ПовторныйКликПокаИдёт_Отказ()
    {
        var (session, a, _) = await MakeStalledWaveAsync("restart-task-twice");
        _tasks.MarkClaudeResult(a.Id, "error");
        var key = "task:" + a.Id;
        _sut.TestHoldRestart = new TaskCompletionSource();
        try
        {
            var first = Task.Run(() => _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id));
            // Детерминированно ждём, что первый вызов захватил гейт (не «маленький sleep»)
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!_sut.RestartInFlight(key) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            _sut.RestartInFlight(key).Should().BeTrue("первый вызов держит гейт перезапуска");

            Func<Task> secondCall = () => _sut.RestartWaveTaskAsync(_sessions.GetById(session.Id)!, a.Id);
            (await secondCall.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*уже перезапускается*");

            _sut.TestHoldRestart.TrySetResult();
            (await first).Outcome.Should().Be("reissued");
        }
        finally
        {
            _sut.TestHoldRestart.TrySetResult();
            _sut.TestHoldRestart = null;
        }
    }

    [Fact]
    public async Task ПерезапускВолны_ЖиваяВолна_ТребуетПодтверждения()
    {
        var (session, _, _) = await MakeStalledWaveAsync("restart-wave-confirm");
        // Оживим волну: задача только что обновилась, штаб работает — liveness alive
        foreach (var t in _tasks.GetByProject(session.ProjectId!))
            _tasks.GetById(t.Id)!.UpdatedAt = DateTime.UtcNow;

        var result = await _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: false);

        result.RequiresConfirm.Should().BeTrue("перезапуск живой волны — только с подтверждения");
        result.Message.Should().Contain("выглядит живой");
        Team(session.Id).Budget.RetriesUsed.Should().Be(0, "предупреждение ничего не расходует");
    }

    [Fact]
    public async Task ПерезапускВолны_ЖивыеИсполнения_ПредупреждаетСписком()
    {
        var (session, a, b) = await MakeStalledWaveAsync("restart-wave-live-tasks");
        await AttachExecutorAsync(a, session, busyStatus: true);

        var result = await _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: false);

        result.RequiresConfirm.Should().BeTrue("раздавать поверх живого исполнения молча нельзя");
        result.LiveTasks.Should().ContainSingle().Which.Should().Be(a.Title,
            "человек видит, ЧТО именно живо");
        _tasks.GetById(b.Id)!.Description.Should().NotContain("Повторная попытка",
            "без подтверждения ничего не перевыдаётся");
    }

    [Fact]
    public async Task ПерезапускВолны_Подтверждён_ОстанавливаетЗанятоеИПеревыдаётНесделанное()
    {
        var (session, a, b) = await MakeStalledWaveAsync("restart-wave-go");
        // Занятое исполнение без живого прогона (зависший статус): Interrupt реанимирует
        // его в Active — мок-адаптер настоящего прогона в юнит-тестах не изображает
        var child = await AttachExecutorAsync(a, session, busyStatus: true);

        var result = await _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: true);

        result.RequiresConfirm.Should().BeFalse();
        result.Reissued.Should().Be(2, "обе незакрытые под-задачи перевыданы");
        _sessions.GetById(child.Id)!.Status.Should().NotBe(SessionStatus.Working,
            "занятое исполнение остановлено — иначе два агента правили бы одни файлы");
        Team(session.Id).Budget.RetriesUsed.Should().Be(2);
    }

    [Fact]
    public async Task ПерезапускВолны_ЗависшаяБезЖивых_ИдётБезПодтверждения()
    {
        var (session, a, b) = await MakeStalledWaveAsync("restart-wave-stalled");

        var result = await _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: false);

        result.RequiresConfirm.Should().BeFalse("тишина дольше порога — подтверждение не нужно");
        result.Reissued.Should().Be(2, "обе незакрытые под-задачи перевыдаются");
        _tasks.GetById(a.Id)!.Description.Should().Contain("Повторная попытка");
        _tasks.GetById(b.Id)!.Description.Should().Contain("Повторная попытка");
    }

    [Fact]
    public async Task ПерезапускВолны_ЗакрытыеНеТрогаем()
    {
        var (session, a, b) = await MakeStalledWaveAsync("restart-wave-done");
        _tasks.Update(a.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var result = await _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: true);

        result.Reissued.Should().Be(1, "перевыдаётся только незакрытое");
        var done = _tasks.GetById(a.Id)!;
        done.Status.Should().Be(TaskItemStatus.Done);
        done.Description.Should().NotContain("Повторная попытка",
            "принятая работа неприкосновенна — ни при каких условиях");
        _tasks.GetById(b.Id)!.Description.Should().Contain("Повторная попытка");
    }

    [Fact]
    public async Task ПерезапускВолны_ВсёЗакрыто_ПерезапускатьНечего()
    {
        var (session, a, b) = await MakeStalledWaveAsync("restart-wave-empty");
        _tasks.Update(a.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        _tasks.Update(b.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var result = await _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: true);

        result.RequiresConfirm.Should().BeFalse();
        result.Reissued.Should().Be(0);
        result.Message.Should().Contain("Незакрытых задач в волне нет");
    }

    [Fact]
    public async Task ПерезапускВолны_ПовторныйВызовПокаИдёт_Отказ()
    {
        var (session, _, _) = await MakeStalledWaveAsync("restart-wave-twice");
        var key = "wave:" + session.Id;
        _sut.TestHoldRestart = new TaskCompletionSource();
        try
        {
            var first = Task.Run(() => _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: false));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!_sut.RestartInFlight(key) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            _sut.RestartInFlight(key).Should().BeTrue();

            var act = () => _sut.RestartWaveAsync(_sessions.GetById(session.Id)!, confirm: true);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*уже перезапускается*");

            _sut.TestHoldRestart.TrySetResult();
            (await first).Reissued.Should().Be(2);
        }
        finally
        {
            _sut.TestHoldRestart.TrySetResult();
            _sut.TestHoldRestart = null;
        }
    }

    // Планировщик-заглушка: отдаёт заранее заданный JSON-план вместо вызова модели
    private sealed class StubPlanner(Func<string> answer) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
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

        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey,
            string prompt, string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
