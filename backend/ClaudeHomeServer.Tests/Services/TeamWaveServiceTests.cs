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
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _teamPlanning = new TeamPlanningService(_personas, new StubPlanner(() => _plannerAnswer));
        _sessions = CreateSessionManager(config, userStore, appSettings, hub);
        _sut = new TeamWaveService(_sessions, _tasks, _projects, hub.Object,
            NullLogger<TeamWaveService>.Instance);
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
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
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

        var created = await _sut.StartWaveAsync(session, plan);

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

    [Fact]
    public async Task StartWave_ВтораяВолнаЖдётЗакрытияПервой()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-wait");
        var plan = MakePlan(backend, frontend);
        var first = await _sut.StartWaveAsync(session, plan);

        // Задача первой волны ещё в работе — вторую не раздаём
        var blocked = await _sut.StartWaveAsync(session, plan);
        blocked.Should().BeEmpty("волна 2 зависит от невыполненной волны 1");
        plan.Subtasks[1].TaskId.Should().BeNull();

        // Первая закрыта — вторая уходит исполнителю
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        var second = await _sut.StartWaveAsync(session, plan);

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
        var first = await _sut.StartWaveAsync(session, plan);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        var second = await _sut.StartWaveAsync(session, plan);
        _tasks.Update(second[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));

        var extra = await _sut.StartWaveAsync(session, plan);

        extra.Should().BeEmpty("все под-задачи плана уже розданы");
        _tasks.GetByProject(session.ProjectId!).Should().HaveCount(2, "дублей задач нет");
    }

    [Fact]
    public async Task StartWave_БезРежима_НичегоНеДелает()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-nomode");
        await _sessions.SetTeamImplementAsync(session.Id, enabled: false, userId: UserId);
        var plan = MakePlan(backend, frontend);

        var created = await _sut.StartWaveAsync(_sessions.GetById(session.Id)!, plan);

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

        var created = await _sut.StartWaveAsync(session, plan);

        created[0].Description.Should().Contain(session.WorktreePath,
            "исполнитель обязан знать, что работа идёт в worktree, а не в основном репо");
        created[0].Description.Should().Contain("feature/x");
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
        return (_sessions.GetById(session.Id)!, plan!);
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

        var created = await _sut.StartWaveAsync(session, plan);

        created.Should().BeEmpty("бюджет итерации исчерпан — цикл останавливается");
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty("ни одной задачи сверх бюджета");
        plan.Subtasks[0].TaskId.Should().BeNull();
        Team(session.Id).Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "человек получает карточку с расходом, а не молчаливую остановку");
    }

    [Fact]
    public async Task StartWave_ПослеОстановкиЧеловеком_НовыеВолныНеСтартуют()
    {
        var (session, backend, frontend) = await MakeStabAsync("wave-stopped");
        var plan = MakePlan(backend, frontend);
        await _sessions.StopTeamImplementAsync(session.Id, UserId);

        var created = await _sut.StartWaveAsync(_sessions.GetById(session.Id)!, plan);

        created.Should().BeEmpty();
        _tasks.GetByProject(session.ProjectId!).Should().BeEmpty();
    }

    [Fact]
    public async Task ЗакрытиеВолны_Авто_СледующаяВолнаСтартуетСама()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-auto");
        var first = await _sut.StartWaveAsync(session, plan);
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
        var first = await _sut.StartWaveAsync(session, plan);

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
        var first = await _sut.StartWaveAsync(session, plan);
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
        var first = await _sut.StartWaveAsync(session, plan);
        _tasks.Update(first[0].Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(first[0].Id)!);

        var second = _tasks.GetByProject(session.ProjectId!).First(t => t.Labels.Contains("волна 2"));
        _tasks.Update(second.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        await _sut.OnTeamTaskDoneAsync(_tasks.GetById(second.Id)!);

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Checking,
            "волны кончились — координатор проверяет результат и подводит итог");
    }

    [Fact]
    public async Task ПровалЗадачи_ПерваяНеудача_Перевыдача()
    {
        var (session, plan) = await MakeRunningStabAsync("wave-retry");
        var first = await _sut.StartWaveAsync(session, plan);
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
        var first = await _sut.StartWaveAsync(session, plan);
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
        var first = await _sut.StartWaveAsync(session, plan);
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
        await _sut.StartWaveAsync(session, plan);
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
        await _sut.StartWaveAsync(session, plan);

        await _sut.CheckStalledWavesAsync();

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Wave);
    }

    [Fact]
    public async Task СторожВолн_ДолгаяНоЖиваяВолна_НеЭскалируется()
    {
        // Таймаут считается от последней активности: волна из долгих задач идёт часами,
        // но пока в ней что-то закрывается — она живая, а не зависшая
        var (session, plan) = await MakeRunningStabAsync("wave-long-alive");
        await _sut.StartWaveAsync(session, plan);
        var team = Team(session.Id);
        team.WaveStartedAt = DateTime.UtcNow.AddHours(-6);
        team.WaveActivityAt = DateTime.UtcNow.AddMinutes(-5);

        await _sut.CheckStalledWavesAsync();

        Team(session.Id).Stage.Should().Be(TeamImplementStage.Wave,
            "ложная тревога на длинной работе — худший вид карточки: человек перестаёт им верить");
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

        var created = await _sut.StartWaveAsync(session, plan);

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

        var created = await _sut.StartWaveAsync(session, plan);

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
            .Select(_ => Task.Run(() => _sut.StartWaveAsync(session, plan))));

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
            _sut.StartWaveAsync(session, snapshot1),
            _sut.StartWaveAsync(session, snapshot2));

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
        var created = await _sut.StartWaveAsync(session, plan);
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
        var first = await _sut.StartWaveAsync(session, plan);
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
        text.Should().Contain("<escalate:decision>");
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

    // Планировщик-заглушка: отдаёт заранее заданный JSON-план вместо вызова модели
    private sealed class StubPlanner(Func<string> answer) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;

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
