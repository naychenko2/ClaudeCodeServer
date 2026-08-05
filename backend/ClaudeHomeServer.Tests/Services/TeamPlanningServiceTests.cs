using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Планирование режима «Командная реализация» (Э2): подбор координатора и планировщика,
// состав кандидатов, карточки в промпт и разбор структурного плана.
// Главное требование этапа: бэкендовая под-задача уходит бэкендеру, фронтовая — фронтендеру,
// и обоснование выбора видно в данных карточки.
public class TeamPlanningServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PersonaManager _personas;
    private readonly TeamPlanningService _sut;
    private const string UserId = "user-1";
    private const string ProjectId = "project-1";

    public TeamPlanningServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "teamplan_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _personas = new PersonaManager(config);
        _sut = new TeamPlanningService(_personas);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Persona MakePersona(string name, string? role, PersonaSpecialty specialty = PersonaSpecialty.None,
        PersonaScope scope = PersonaScope.Project, string? description = null,
        List<PersonaBinding>? bindings = null)
    {
        var p = _personas.Create(UserId, name, role, description, systemPrompt: null, model: null,
            effort: null, scope, ProjectId, color: null, greeting: null, memoryEnabled: false,
            specialty: specialty);
        if (bindings is not null) p.Bindings = bindings;
        return p;
    }

    private static Session MakeSession(string? personaId, string? projectId = ProjectId,
        List<string>? executors = null, string? plannerId = null) => new()
        {
            ProjectId = projectId,
            OwnerId = UserId,
            PersonaId = personaId,
            TeamImplement = new SessionTeamImplement
            {
                CoordinatorPersonaId = personaId,
                PlannerPersonaId = plannerId,
                ExecutorPersonaIds = executors ?? [],
            },
        };

    // --- Координатор ---

    [Fact]
    public void ResolveCoordinator_СобеседникЧата()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var session = MakeSession(coord.Id);

        _sut.ResolveCoordinator(session, UserId)!.Id.Should().Be(coord.Id);
    }

    [Fact]
    public void ResolveCoordinator_ЧатБезПерсоны_Null()
    {
        // Режим требует выбрать координатора — фронт показывает пикер при включении
        _sut.ResolveCoordinator(MakeSession(personaId: null), UserId).Should().BeNull();
    }

    [Fact]
    public void ResolveCoordinator_ЧужойВладелец_Null()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        _sut.ResolveCoordinator(MakeSession(coord.Id), "another-user").Should().BeNull();
    }

    // --- Планировщик ---

    [Fact]
    public void ResolvePlanner_ПоСпециальностиPlanner()
    {
        var coord = MakePersona("Алекс", "Тимлид", PersonaSpecialty.Coordinator);
        var planner = MakePersona("Софья", "Продакт", PersonaSpecialty.Planner);
        var dev = MakePersona("Денис", "Бэкенд", PersonaSpecialty.Executor);
        var session = MakeSession(coord.Id);

        _sut.ResolvePlanner(session, UserId, [planner, dev])!.Id
            .Should().Be(planner.Id, "Planner — первый приоритет");
    }

    [Fact]
    public void ResolvePlanner_БезPlanner_БерётCoordinator()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var mid = MakePersona("Мария", "Менеджер", PersonaSpecialty.Coordinator);
        var analyst = MakePersona("Софья", "Аналитик", PersonaSpecialty.Analyst);
        var session = MakeSession(coord.Id);

        _sut.ResolvePlanner(session, UserId, [analyst, mid])!.Id
            .Should().Be(mid.Id, "Coordinator приоритетнее Analyst");
    }

    [Fact]
    public void ResolvePlanner_ТолькоAnalyst_БерётЕго()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var analyst = MakePersona("Софья", "Аналитик", PersonaSpecialty.Analyst);
        var dev = MakePersona("Денис", "Бэкенд", PersonaSpecialty.Executor);

        _sut.ResolvePlanner(MakeSession(coord.Id), UserId, [dev, analyst])!.Id.Should().Be(analyst.Id);
    }

    [Fact]
    public void ResolvePlanner_ЯвныйСильнееСпециальности()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var planner = MakePersona("Софья", "Продакт", PersonaSpecialty.Planner);
        var dev = MakePersona("Денис", "Бэкенд", PersonaSpecialty.Executor);
        var session = MakeSession(coord.Id, plannerId: dev.Id);

        _sut.ResolvePlanner(session, UserId, [planner, dev])!.Id
            .Should().Be(dev.Id, "явно указанный планировщик сильнее подбора");
    }

    [Fact]
    public void ResolvePlanner_НикогоПодходящего_ОткатНаКоординатора()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var dev = MakePersona("Денис", "Бэкенд", PersonaSpecialty.Executor);

        _sut.ResolvePlanner(MakeSession(coord.Id), UserId, [dev])!.Id.Should().Be(coord.Id);
    }

    // --- Состав кандидатов ---

    [Fact]
    public void ResolveCandidates_БезЯвногоВыбора_ВсяКомандаПроектаБезКоординатора()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var dev = MakePersona("Денис", "Бэкенд");
        var front = MakePersona("Кира", "Фронтенд");

        var ids = _sut.ResolveCandidates(MakeSession(coord.Id), UserId).Select(p => p.Id).ToList();

        ids.Should().BeEquivalentTo([dev.Id, front.Id], "координатор не исполнитель сам себе");
    }

    [Fact]
    public void ResolveCandidates_ЯвныйВыбор_СужаетКруг()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var dev = MakePersona("Денис", "Бэкенд");
        MakePersona("Кира", "Фронтенд");
        var session = MakeSession(coord.Id, executors: [dev.Id]);

        _sut.ResolveCandidates(session, UserId).Select(p => p.Id).Should().Equal(dev.Id);
    }

    [Fact]
    public void ResolveCandidates_ГлобальныйЧатБезСостава_Пусто()
    {
        var coord = MakePersona("Алекс", "Тимлид", scope: PersonaScope.Global);
        MakePersona("Денис", "Бэкенд");
        var session = MakeSession(coord.Id, projectId: null);

        _sut.ResolveCandidates(session, UserId).Should().BeEmpty(
            "вне проекта команды нет — состав нужно выбрать явно");
    }

    // --- Карточки кандидатов ---

    [Fact]
    public void BuildCard_НесётПрофильИПривязкиКПапкам()
    {
        var dev = MakePersona("Денис", "Backend-разработчик", PersonaSpecialty.Executor,
            description: "C#/ASP.NET Core, Services/Llm, MCP-серверы",
            bindings:
            [
                new PersonaBinding
                {
                    Type = PersonaBindingType.ProjectPath, Target = ProjectId, Path = "backend",
                    Condition = "Любые задачи по серверной части на C#",
                },
                // Рубильник инструмента к выбору исполнителя отношения не имеет
                new PersonaBinding { Type = PersonaBindingType.Tool, Target = "tasks" },
                // Выключенная привязка в карточку не попадает
                new PersonaBinding
                {
                    Type = PersonaBindingType.ProjectPath, Target = ProjectId, Path = "docs",
                    Mode = PersonaBindingMode.Off,
                },
            ]);

        var card = TeamPlanningService.BuildCard(dev);

        card.PersonaId.Should().Be(dev.Id);
        card.Name.Should().Be("Денис");
        card.Role.Should().Be("Backend-разработчик");
        card.Specialty.Should().Be("Исполнитель (универсальный)");
        card.Description.Should().Contain("ASP.NET");
        card.Bindings.Should().ContainSingle()
            .Which.Should().Be("папка backend — Любые задачи по серверной части на C#");
    }

    [Fact]
    public void BuildCard_БезСпециальности_Null()
    {
        TeamPlanningService.BuildCard(MakePersona("Денис", "Бэкенд")).Specialty.Should().BeNull();
    }

    // --- Промпт планировщика ---

    [Fact]
    public void BuildPlannerPrompt_НесётКандидатовИТребованиеОбоснования()
    {
        var dev = TeamPlanningService.BuildCard(MakePersona("Денис", "Backend-разработчик",
            PersonaSpecialty.Executor, description: "C#/ASP.NET Core"));
        var front = TeamPlanningService.BuildCard(MakePersona("Кира", "Frontend-разработчик"));

        var prompt = TeamPlanningService.BuildPlannerPrompt("Добавить экспорт в CSV",
            [dev, front], "ClaudeCodeServer");

        prompt.Should().Contain("Добавить экспорт в CSV");
        prompt.Should().Contain("ClaudeCodeServer");
        // Кандидаты уходят с id — планировщик обязан вернуть именно их
        prompt.Should().Contain($"personaId={dev.PersonaId}").And.Contain($"personaId={front.PersonaId}");
        prompt.Should().Contain("Backend-разработчик").And.Contain("Frontend-разработчик");
        prompt.Should().Contain("специальность: Исполнитель");
        // Главное требование Э2
        prompt.Should().Contain("executorRationale");
        prompt.Should().Contain("бэкенд — бэкендеру");
    }

    // Дыра покрытия (волна 3): «previous»-путь перепланирования (Э8) — промпт обязан нести
    // предыдущую версию плана и просить блок «changes», иначе «Что изменилось» в карточке
    // человеку взять неоткуда.
    [Fact]
    public void BuildPlannerPrompt_ПерепланированиеСПредыдущимПланом_НесётЕгоИТребуетChanges()
    {
        var dev = TeamPlanningService.BuildCard(MakePersona("Денис", "Backend-разработчик"));
        var previous = new TeamImplementPlan
        {
            Version = 1,
            Summary = "Экспорт задач в CSV",
            Subtasks =
            [
                new TeamImplementSubtask { Title = "Эндпоинт экспорта", Wave = 1, TaskId = "task-1" },
                new TeamImplementSubtask { Title = "Кнопка «Экспорт»", Wave = 2 },
            ],
        };

        var prompt = TeamPlanningService.BuildPlannerPrompt("Добавить экспорт в CSV, но в XLSX",
            [dev], "ClaudeCodeServer", previous);

        prompt.Should().Contain("ПРЕДЫДУЩИЙ ПЛАН (версия 1)");
        prompt.Should().Contain("Эндпоинт экспорта (волна 1, уже роздана исполнителю)",
            "розданная под-задача — сигнал планировщику не плодить её заново");
        prompt.Should().Contain("Кнопка «Экспорт» (волна 2)");
        prompt.Should().Contain("changes", "блок «что изменилось» обязателен только при перепланировании");
        prompt.Should().Contain("\"changes\":");
    }

    [Fact]
    public void BuildPlannerPrompt_БезПредыдущегоПлана_НеТребуетChanges()
    {
        var dev = TeamPlanningService.BuildCard(MakePersona("Денис", "Backend-разработчик"));

        var prompt = TeamPlanningService.BuildPlannerPrompt("Добавить экспорт в CSV",
            [dev], "ClaudeCodeServer");

        prompt.Should().NotContain("ПРЕДЫДУЩИЙ ПЛАН");
        prompt.Should().NotContain("\"changes\":");
    }

    // Прод 2026-08-02 (находка Веры): планировщик выдал под-задачу с буквальным
    // «<файл-1 в корне проекта>» вместо значения, которое человек указал в вводной —
    // промпт обязан явно запрещать плейсхолдеры и требовать перенос конкретики или допущение.
    [Fact]
    public void BuildPlannerPrompt_ЗапрещаетПлейсхолдерыИТребуетКонкретику()
    {
        var dev = TeamPlanningService.BuildCard(MakePersona("Денис", "Backend-разработчик"));

        var prompt = TeamPlanningService.BuildPlannerPrompt("Добавить экспорт в CSV",
            [dev], "ClaudeCodeServer");

        prompt.Should().Contain("плейсхолдер");
        prompt.Should().Contain("<файл-1 в корне проекта>", "конкретный пример утечки из прода — по нему проверяется, что запрет адресный");
        prompt.Should().Contain("assumptions", "неизвестное значение — в допущения, а не в заглушку");
    }

    // Прод 2026-08-03 (находка Веры): просили hello5.txt/«QA smoke test file 5», получили
    // smoke5.txt/«smoke run 5 ok» — это НЕ плейсхолдер (запрет 6a его уже покрывает), а прямая
    // подмена названного человеком значения своим. Правило обязано отдельно запрещать и это.
    [Fact]
    public void BuildPlannerPrompt_ЗапрещаетПодменуНазванныхЗначенийСвоими()
    {
        var dev = TeamPlanningService.BuildCard(MakePersona("Денис", "Backend-разработчик"));

        var prompt = TeamPlanningService.BuildPlannerPrompt("Создай hello5.txt", [dev], "ClaudeCodeServer");

        prompt.Should().Contain("дословно");
        prompt.Should().Contain("hello5.txt", "конкретный пример утечки из прода — по нему проверяется, что запрет адресный");
    }

    // --- Разбор плана ---

    [Fact]
    public void ParsePlan_РаздаётРаботуПоПрофилю_ОбоснованиеВДанных()
    {
        var dev = MakePersona("Денис", "Backend-разработчик", PersonaSpecialty.Executor);
        var front = MakePersona("Кира", "Frontend-разработчик");
        var raw = $$"""
            Вот план:
            {"summary":"Экспорт задач в CSV",
             "subtasks":[
               {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
                "executorPersonaId":"{{dev.Id}}","executorRationale":"Бэкенд-часть — его зона",
                "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"200 и CSV"},
               {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
                "executorPersonaId":"{{front.Id}}","executorRationale":"UI — фронтенд",
                "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
            """;

        var plan = TeamPlanningService.ParsePlan(raw, "Экспорт в CSV", [dev, front])!;

        plan.Summary.Should().Be("Экспорт задач в CSV");
        plan.Subtasks.Should().HaveCount(2);
        // Бэкендовая часть — бэкендеру, фронтовая — фронтендеру
        var backend = plan.Subtasks[0];
        backend.ExecutorPersonaId.Should().Be(dev.Id);
        backend.ExecutorRationale.Should().Be("Бэкенд-часть — его зона");
        backend.Files.Should().Equal("backend/Controllers/TasksController.cs");
        backend.Wave.Should().Be(1);
        backend.DoneCriteria.Should().Be("200 и CSV");
        plan.Subtasks[1].ExecutorPersonaId.Should().Be(front.Id);
        plan.Subtasks[1].Wave.Should().Be(2);
        // Сводные поля карточки
        plan.WaveCount.Should().Be(2);
        plan.ExecutorCount.Should().Be(2);
        plan.Request.Should().Be("Экспорт в CSV");
    }

    [Fact]
    public void ParsePlan_ИсполнительПоHandle_ТожеПринимается()
    {
        var dev = MakePersona("Денис", "Бэкенд");
        var raw = $$"""
            {"summary":"s","subtasks":[{"title":"t","executorPersonaId":"@{{dev.Handle}}",
             "executorRationale":"его зона","wave":1}]}
            """;

        TeamPlanningService.ParsePlan(raw, "req", [dev])!.Subtasks[0].ExecutorPersonaId
            .Should().Be(dev.Id, "модель иногда отдаёт handle вместо id");
    }

    [Fact]
    public void ParsePlan_ЧужойИсполнитель_ЗаменяетсяСПометкой()
    {
        var dev = MakePersona("Денис", "Бэкенд");
        var raw = """
            {"summary":"s","subtasks":[{"title":"t","executorPersonaId":"чужой-id",
             "executorRationale":"","wave":1}]}
            """;

        var subtask = TeamPlanningService.ParsePlan(raw, "req", [dev])!.Subtasks[0];

        subtask.ExecutorPersonaId.Should().Be(dev.Id, "исполнитель только из состава кандидатов");
        subtask.ExecutorRationale.Should().Contain("проверьте",
            "человек должен увидеть, что выбор не обоснован, и поправить до запуска");
    }

    [Fact]
    public void ParsePlan_БезОбоснования_СтавитПометку()
    {
        var dev = MakePersona("Денис", "Бэкенд");
        var raw = $$"""
            {"summary":"s","subtasks":[{"title":"t","executorPersonaId":"{{dev.Id}}","wave":1}]}
            """;

        TeamPlanningService.ParsePlan(raw, "req", [dev])!.Subtasks[0].ExecutorRationale
            .Should().Contain("не обосновал");
    }

    [Fact]
    public void ParsePlan_НекорректнаяВолна_НормализуетсяВЕдиницу()
    {
        var dev = MakePersona("Денис", "Бэкенд");
        var raw = $$"""
            {"summary":"s","subtasks":[
              {"title":"a","executorPersonaId":"{{dev.Id}}","executorRationale":"r","wave":0},
              {"title":"b","executorPersonaId":"{{dev.Id}}","executorRationale":"r","wave":"2"}]}
            """;

        var plan = TeamPlanningService.ParsePlan(raw, "req", [dev])!;

        plan.Subtasks[0].Wave.Should().Be(1);
        plan.Subtasks[1].Wave.Should().Be(2, "номер волны строкой тоже принимается");
    }

    [Fact]
    public void ParsePlan_БольшеПотолка_Обрезается()
    {
        var dev = MakePersona("Денис", "Бэкенд");
        var items = string.Join(",", Enumerable.Range(1, TeamPlanningService.MaxSubtasks + 5)
            .Select(i => $$"""{"title":"t{{i}}","executorPersonaId":"{{dev.Id}}","executorRationale":"r","wave":1}"""));

        TeamPlanningService.ParsePlan($$"""{"summary":"s","subtasks":[{{items}}]}""", "req", [dev])!
            .Subtasks.Should().HaveCount(TeamPlanningService.MaxSubtasks);
    }

    [Theory]
    [InlineData("не json вовсе")]
    [InlineData("""{"summary":"s"}""")]                       // нет subtasks
    [InlineData("""{"summary":"s","subtasks":[]}""")]         // пустой план
    [InlineData("""{"summary":"s","subtasks":[{"goal":"g"}]}""")] // под-задача без названия
    public void ParsePlan_НевалидныйОтвет_Null(string raw)
    {
        var dev = MakePersona("Денис", "Бэкенд");
        TeamPlanningService.ParsePlan(raw, "req", [dev]).Should().BeNull();
    }

    // --- Реальный состав команды проекта ---

    // Фактическая команда ClaudeCodeServer (роли, специальности и привязки — как в data/personas.json):
    // на ней проверяется, что промпт несёт признаки для выбора, а планировщик берётся по специальности.
    private (Persona Coordinator, List<Persona> Team) MakeRealTeam()
    {
        Persona Mk(string name, string role, PersonaSpecialty spec, string? desc, string? path,
            string? condition) => MakePersona(name, role, spec, description: desc,
            bindings: path is null ? null :
            [
                new PersonaBinding
                {
                    Type = PersonaBindingType.ProjectPath, Target = ProjectId, Path = path,
                    Condition = condition ?? "",
                },
            ]);

        var alex = Mk("Александр", "Тимлид / Архитектор", PersonaSpecialty.Consultant,
            "Тимлид и архитектор: кросс-слойный дизайн, ADR, инварианты", "docs",
            "Обсуждение документации, ADR и принятых решений");
        var team = new List<Persona>
        {
            Mk("Денис", "Backend-разработчик (.NET)", PersonaSpecialty.Executor,
                "Backend: C#/ASP.NET Core, Services/Llm, MCP-серверы, SignalR", "backend",
                "Любые задачи по серверной части на C#/.NET"),
            Mk("Кира", "Frontend-разработчик", PersonaSpecialty.Executor,
                "Frontend: React 18 + TypeScript, дизайн-система проекта", "frontend/src",
                "Любые задачи по интерфейсу и клиентскому коду"),
            Mk("Майя", "UI/UX-дизайнер", PersonaSpecialty.Designer,
                "Макеты в docs/mockups, дизайн-система design.ts/theme.css", null, null),
            Mk("Софья", "Продакт-аналитик", PersonaSpecialty.Analyst,
                "Польза фич, UX-формулировки, фич-флаги", "docs", "Продуктовая документация"),
            Mk("Глеб", "Код-ревьюер", PersonaSpecialty.Reviewer,
                "Находки по severity, безопасность и изоляция данных", null, null),
            Mk("Вера", "QA-тестер", PersonaSpecialty.Tester,
                "Ручная проверка через Playwright, регрессии", "frontend", "Проверка интерфейса"),
        };
        return (alex, team);
    }

    [Fact]
    public void РеальнаяКоманда_ПланировщикПоСпециальности_Аналитик()
    {
        var (coordinator, team) = MakeRealTeam();

        // В команде нет Planner, есть Consultant-координатор и Analyst — берётся Софья
        _sut.ResolvePlanner(MakeSession(coordinator.Id), UserId, team)!
            .Name.Should().Be("Софья", "Analyst — третий приоритет, Planner и Coordinator в команде нет");
    }

    [Fact]
    public void РеальнаяКоманда_ПромптНесётПризнакиДляВыбораИсполнителя()
    {
        var (_, team) = MakeRealTeam();
        var cards = team.Select(TeamPlanningService.BuildCard).ToList();

        var prompt = TeamPlanningService.BuildPlannerPrompt(
            "Добавить экспорт задач в CSV: эндпоинт, кнопка в тулбаре и тесты",
            cards, "ClaudeCodeServer");

        // По этим строкам планировщик и различает, кому что отдать
        prompt.Should().Contain("Backend-разработчик (.NET)")
            .And.Contain("папка backend — Любые задачи по серверной части на C#/.NET");
        prompt.Should().Contain("Frontend-разработчик")
            .And.Contain("папка frontend/src — Любые задачи по интерфейсу и клиентскому коду");
        prompt.Should().Contain("UI/UX-дизайнер").And.Contain("специальность: Дизайнер");
        prompt.Should().Contain("QA-тестер").And.Contain("специальность: Тестировщик");
        prompt.Should().Contain("Код-ревьюер").And.Contain("специальность: Ревьюер");
        // Каждый кандидат уходит с id — только из них планировщик выбирает исполнителя
        foreach (var c in cards) prompt.Should().Contain($"personaId={c.PersonaId}");
    }

    [Fact]
    public async Task РеальнаяКоманда_ПланРаздаётРаботуПоПрофилю()
    {
        var (coordinator, team) = MakeRealTeam();
        var denis = team.Single(p => p.Name == "Денис");
        var kira = team.Single(p => p.Name == "Кира");
        var vera = team.Single(p => p.Name == "Вера");
        // Ответ планировщика на составе реальной команды: бэкенд Денису, фронт Кире, тесты Вере
        var cheap = new FakeCheapRunner($$"""
            {"summary":"Экспорт задач в CSV","subtasks":[
              {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export отдаёт CSV",
               "executorPersonaId":"{{denis.Id}}","executorRationale":"Серверная часть — его папка backend",
               "files":["backend/ClaudeHomeServer/Controllers/TasksController.cs"],"wave":1,
               "doneCriteria":"dotnet build зелёный, эндпоинт отдаёт CSV"},
              {"title":"Кнопка «Экспорт» в тулбаре","goal":"Кнопка вызывает эндпоинт",
               "executorPersonaId":"{{kira.Id}}","executorRationale":"UI — её папка frontend/src",
               "files":["frontend/src/components/TasksToolbar.tsx"],"wave":2,
               "doneCriteria":"npm run build зелёный, файл скачивается"},
              {"title":"Прогон сценария экспорта","goal":"Проверить сценарий в живом приложении",
               "executorPersonaId":"{{vera.Id}}","executorRationale":"Проверка сценариев — её специальность",
               "files":[],"wave":3,"doneCriteria":"сценарий пройден, баги описаны"}]}
            """);
        var sut = new TeamPlanningService(_personas, cheap);
        var session = MakeSession(coordinator.Id, executors: team.Select(p => p.Id).ToList());

        var (plan, timedOut) = await sut.CreatePlanAsync(session, UserId,
            "Добавить экспорт задач в CSV", "ClaudeCodeServer");

        timedOut.Should().BeFalse();
        plan.Should().NotBeNull();
        plan!.Subtasks.Should().HaveCount(3);
        // Главный критерий Э2: работа ушла по профилю персон
        plan.Subtasks[0].ExecutorPersonaId.Should().Be(denis.Id, "бэкендовая часть — бэкендеру");
        plan.Subtasks[1].ExecutorPersonaId.Should().Be(kira.Id, "фронтовая — фронтендеру");
        plan.Subtasks[2].ExecutorPersonaId.Should().Be(vera.Id, "проверка — тестировщику");
        // И обоснование каждого выбора видно в данных карточки
        plan.Subtasks.Should().OnlyContain(s => s.ExecutorRationale.Length > 0);
        plan.Subtasks[0].ExecutorRationale.Should().Contain("backend");
        plan.WaveCount.Should().Be(3);
        plan.ExecutorCount.Should().Be(3);
    }

    [Fact]
    public async Task CreatePlanAsync_БезКандидатов_Null()
    {
        var coord = MakePersona("Алекс", "Тимлид", scope: PersonaScope.Global);
        var session = MakeSession(coord.Id, projectId: null);

        var (plan, _) = await _sut.CreatePlanAsync(session, UserId, "сделай что-нибудь");
        plan.Should().BeNull();
    }

    [Fact]
    public async Task CreatePlanAsync_ЗовётПланировщикаИПроставляетЕго()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        var planner = MakePersona("Софья", "Продакт", PersonaSpecialty.Planner);
        var dev = MakePersona("Денис", "Backend-разработчик");
        var front = MakePersona("Кира", "Frontend-разработчик");
        var cheap = new FakeCheapRunner($$"""
            {"summary":"Экспорт","subtasks":[
              {"title":"API","executorPersonaId":"{{dev.Id}}","executorRationale":"бэкенд его","wave":1},
              {"title":"Кнопка","executorPersonaId":"{{front.Id}}","executorRationale":"UI её","wave":2}]}
            """);
        var sut = new TeamPlanningService(_personas, cheap);

        var (plan, _) = await sut.CreatePlanAsync(MakeSession(coord.Id), UserId, "Экспорт в CSV", "CCS");

        plan.Should().NotBeNull();
        plan!.PlannerPersonaId.Should().Be(planner.Id, "план построил подобранный планировщик");
        plan.Subtasks.Select(s => s.ExecutorPersonaId).Should().Equal(dev.Id, front.Id);
        // В промпт ушли карточки кандидатов и место каталога — то самое
        cheap.LastActionKey.Should().Be(LocalActionCatalog.TeamImplementPlan);
        cheap.LastPrompt.Should().Contain($"personaId={dev.Id}").And.Contain($"personaId={front.Id}");
        cheap.LastPrompt.Should().Contain("Экспорт в CSV");
    }

    [Fact]
    public async Task CreatePlanAsync_ПланировщикУпал_Null()
    {
        var coord = MakePersona("Алекс", "Тимлид");
        MakePersona("Денис", "Бэкенд");
        var sut = new TeamPlanningService(_personas, new FakeCheapRunner(throwOnRun: true));

        var (plan, timedOut) = await sut.CreatePlanAsync(MakeSession(coord.Id), UserId, "задача");
        plan.Should().BeNull("падение планировщика не роняет ход — человек получает причину отказа");
        timedOut.Should().BeFalse("обычный сбой — не таймаут, текст отказа другой");
    }

    [Fact]
    public async Task CreatePlanAsync_Таймаут_NullСПризнакомТаймаута()
    {
        // Обрыв по таймауту отличается от прочих отказов: причина не в постановке человека,
        // и SessionManager показывает другой текст карточки (PlannerTimeoutReason).
        var coord = MakePersona("Алекс", "Тимлид");
        MakePersona("Денис", "Бэкенд");
        var sut = new TeamPlanningService(_personas, new FakeCheapRunner(throwTimeout: true));

        var (plan, timedOut) = await sut.CreatePlanAsync(MakeSession(coord.Id), UserId, "задача");

        plan.Should().BeNull();
        timedOut.Should().BeTrue();
    }

    // Подставной раннер: запоминает вызов и отдаёт заготовленный ответ.
    // throwOnRun — обычный сбой, throwTimeout — обрыв по таймауту (LlmTimeoutException).
    private sealed class FakeCheapRunner(string answer = "", bool throwOnRun = false,
        bool throwTimeout = false) : ICheapTextRunner
    {
        public string? LastActionKey { get; private set; }
        public string LastPrompt { get; private set; } = "";

        public bool UsesLocal(string actionKey) => false;

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            LastActionKey = actionKey;
            LastPrompt = prompt;
            if (throwTimeout) throw new LlmTimeoutException();
            return throwOnRun
                ? throw new InvalidOperationException("модель недоступна")
                : Task.FromResult(answer);
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
