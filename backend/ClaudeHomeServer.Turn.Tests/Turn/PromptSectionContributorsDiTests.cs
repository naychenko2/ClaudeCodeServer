using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Turn;

// Сторож регистрации IPromptSectionContributor (этап 2 плана «Шина событий хода»,
// ADR-013). До этих тестов забытый вызов AddPromptSectionContributors() в Program.cs
// проходил незаметно: golden-тесты клепают свою шину вручную и не ловят DI-граф. Здесь:
// 1) быстрый тест регистрации в DI (ServiceDescriptor'ы — 7 конкретных классов);
// 2) функциональный — реальный резолв + проверка Key уникальны и Order по возрастанию;
// 3) тест шины — RegisterAll + ApplyAsync на двух мок-контрибьюторах, секции в правильном порядке;
// 4) негативный — без контрибьюторов в шине ApplyAsync даёт пустые Sections.
//
// Тест (4) — доказательство, что тест (1) реально сторожит: если кто-то уберёт вызов
// AddPromptSectionContributors() из Program.cs, контейнер отдаст 0 дескрипторов, и шина
// в проде будет пустой, как и тест (4).
public class PromptSectionContributorsDiTests
{
    // Тест 1: быстрый сторож DI — после вызова AddPromptSectionContributors() в контейнере
    // ровно 7 «чистых» регистраций IPromptSectionContributor (контрибьюторы, оставшиеся
    // в Turn после инверсии). CodeGraphContributor и NotesRecallContributor уехали в свои
    // вертикали — их регистрируют CodeGraphSubsystem/NotesSubsystem, а не Turn.
    // НЕ требует зависимостей контрибьюторов — резолва нет, только ServiceDescriptor'ы.
    [Fact]
    public void AddPromptSectionContributors_RegistersSevenCoreContributors()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPromptSectionContributors();

        // AddPromptSectionContributor<T> регистрирует пару: конкретный тип (с ImplementationType)
        // И IPromptSectionContributor через factory (ImplementationType=null, задан Factory).
        // Для конкретного типа ImplementationType=typeof(T>, проверяем через него — иначе
        // сообщение «забыли добавить контрибьютора» не различить от «добавили, но не тот тип».
        var concreteDescriptors = services
            .Where(d => typeof(IPromptSectionContributor).IsAssignableFrom(d.ServiceType)
                        && d.ServiceType != typeof(IPromptSectionContributor)
                        && d.ImplementationType is not null)
            .ToList();
        concreteDescriptors.Should().HaveCount(7,
            "AddPromptSectionContributors должен зарегистрировать ровно 7 чистых контрибьюторов Turn");

        // Канонический список «чистых» контрибьюторов Turn. Новый контрибьютор = новая
        // строка в AddPromptSectionContributors() И здесь, иначе сторож не отличит «забыли
        // добавить» от «добавили лишнего». Чужие (CodeGraph/Notes) сюда НЕ входят — их
        // сторожит тест 2 (полный набор) и тесты их подсистем.
        var implTypes = concreteDescriptors.Select(d => d.ImplementationType!).ToHashSet();
        implTypes.Should().BeEquivalentTo(new[]
        {
            typeof(DossierTrailerContributor),
            typeof(SphereCharterContributor),
            typeof(PersonaRecallContributor),
            typeof(PromptSectionsContributor),
            typeof(PersonaBindingsContributor),
            typeof(PersonaLayerContributor),
            typeof(TurnContextContributor),
        });

        // Каждый контрибьютор едет и в общий набор IPromptSectionContributor (через factory).
        var interfaceDescriptors = services
            .Where(d => d.ServiceType == typeof(IPromptSectionContributor))
            .ToList();
        interfaceDescriptors.Should().HaveCount(7,
            "набор IPromptSectionContributor должен состоять из 7 элементов (по одному на чистого контрибьютора)");

        // Все регистрации — singleton (контрибьюторы без состояния, как и шина).
        concreteDescriptors.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Singleton);
        interfaceDescriptors.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Singleton);
    }

    // Тест 2: реальный резолв IEnumerable<IPromptSectionContributor>, проверка Key и Order.
    // Требует минимального набора зависимостей всех 8 контрибьюторов (см. BuildSut).
    //
    // После инверсии DI отдаёт контрибьюторов в порядке РЕГИСТРАЦИИ подсистем, а не по
    // Order. Канонический порядок задаёт САМ Order, а не порядок в IEnumerable — шина
    // TurnEventBus.ApplyAsync сортирует по Order. Поэтому здесь сортируем по Order и
    // фиксируем каноническую последовательность: это и есть сторож «порядок секций».
    [Fact]
    public void ResolvedContributors_HaveUniqueKeysAndCanonicalOrder()
    {
        using var provider = BuildSut();

        var contributors = provider.GetServices<IPromptSectionContributor>().ToList();

        contributors.Should().HaveCount(9,
            "полный набор = 7 чистых Turn + CodeGraph + Notes");

        // Уникальность Key — две секции с одинаковым Key замазали бы друг друга в снапшоте.
        var keys = contributors.Select(c => c.Key).ToList();
        keys.Should().OnlyHaveUniqueItems("Key контрибьютора обязан быть уникальным");

        // Order тоже обязан быть уникальным: при равных Order шина доупорядочивает по
        // порядку подписки (ThenBy(Seq)), а он зависит от порядка регистрации подсистем —
        // нестабильно между запусками. Канонический шаг 100 это исключает.
        var orders = contributors.Select(c => c.Order).ToList();
        orders.Should().OnlyHaveUniqueItems("Order контрибьютора обязан быть уникальным");

        // Сортируем по Order — ровно как шина (TurnEventBus.ApplyAsync: OrderBy(Order)).
        var ordered = contributors.OrderBy(c => c.Order).Select(c => c.Order).ToList();
        ordered.Should().Equal(new[] { 100, 150, 200, 300, 400, 500, 600, 690, 900 },
            "канонический порядок секций промпта (dossier-trailer → persona-layer)");

        // Парность: каждый Key обязан встретиться ровно один раз И с правильным Order.
        // Это ловит классическую регрессию «поправили Order у одного контрибьютора, забыли у соседа».
        var keyToOrder = contributors.ToDictionary(c => c.Key, c => c.Order);
        keyToOrder.Should().ContainKey("dossier-trailer").WhoseValue.Should().Be(100);
        keyToOrder.Should().ContainKey("sphere-charter").WhoseValue.Should().Be(150);
        keyToOrder.Should().ContainKey("recall-notes").WhoseValue.Should().Be(200);
        keyToOrder.Should().ContainKey("recall-memory").WhoseValue.Should().Be(300);
        keyToOrder.Should().ContainKey("prompt-sections").WhoseValue.Should().Be(400);
        keyToOrder.Should().ContainKey("persona-bindings").WhoseValue.Should().Be(500);
        keyToOrder.Should().ContainKey("code-graph").WhoseValue.Should().Be(600);
        keyToOrder.Should().ContainKey("turn-context").WhoseValue.Should().Be(690);
        keyToOrder.Should().ContainKey("persona-layer").WhoseValue.Should().Be(900);

        // Инверсия: чужие контрибьюторы живут по своим вертикалям. Проверяем, что их типы
        // приехали в полный набор ИЗ CodeGraph/Notes-сборок (а не из Turn).
        contributors.Should().Contain(c => c.GetType() == typeof(CodeGraphContributor));
        contributors.Should().Contain(c => c.GetType() == typeof(NotesRecallContributor));
    }

    // Тест 3: RegisterAll + ApplyAsync. Два мок-контрибьютора с разными Order — после
    // ApplyAsync секции обоих в Sections и идут в порядке возрастания Order.
    [Fact]
    public async Task RegisterAll_AddsContributorsSectionsInOrderOnPromptAssembling()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        var contributors = new IPromptSectionContributor[]
        {
            new OrderContributor("later", 300, "поздняя"),
            new OrderContributor("early", 100, "ранняя"),
            new OrderContributor("middle", 200, "средняя"),
        };

        PromptSectionContributorsRegistration.RegisterAll(bus, contributors);

        var session = new Session { OwnerId = "owner-1" };
        var ctx = new PromptSessionContext(session, "owner-1", Persona: null, RootPath: null,
            HasNotesMcp: false, HasMemoryMcp: false, HasWorkspaceMcp: false,
            WorkspaceSections: Array.Empty<string>());
        var turn = new TurnContext("session-1", "owner-1", 1, 0);
        var prompt = new PromptAssembling(turn, ctx);

        await bus.ApplyAsync(prompt);

        prompt.Sections.Should().HaveCount(3, "каждый контрибьютор добавил свою секцию");
        prompt.Sections.Select(s => s.Key).Should().Equal(new[] { "early", "middle", "later" },
            "секции склеиваются по возрастанию Order контрибьютора, а не по порядку регистрации");
        prompt.Sections.Select(s => s.Text).Should().Equal(new[] { "ранняя", "средняя", "поздняя" });
    }

    // TS-18 (параллельная сборка recall): гонка параллельной группы. Реестр получил
    // двух parallel-safe членов в обратном порядке (поздний Order первым), и «поздний»
    // завершается первым — порядок завершения задан гейтами TaskCompletionSource, без
    // Task.Delay. Секции в событии остаются в порядке Order, а не завершения и не
    // получения. Проверка «оба внутри BuildAsync» — сторож параллельного старта:
    // при последовательных фильтрах второй не начался бы, пока первый ждёт свой гейт.
    [Fact]
    public async Task ParallelGroup_CompletingInReverseOrder_SectionsStayInOrder()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        var later = new GateContributor("later", 300);
        var earlier = new GateContributor("earlier", 200);
        // Порядок получения намеренно обратный: он тоже не должен определять результат.
        PromptSectionContributorsRegistration.RegisterAll(bus,
            new IPromptSectionContributor[] { later, earlier });

        var prompt = new PromptAssembling(new TurnContext("session-1", "owner-1", 1, 0), ParallelCtx());
        var applying = bus.ApplyAsync(prompt);

        var bothEntered = Task.WhenAll(later.Entered, earlier.Entered);
        (await Task.WhenAny(bothEntered, Task.Delay(5000))).Should().Be(bothEntered,
            "оба члена группы должны быть внутри BuildAsync одновременно — иначе гонки нет");

        // Завершение — в порядке, обратном Order: «later» завершится строго раньше,
        // чем «earlier» будет отпущен.
        later.Gate.SetResult();
        await later.Done;
        earlier.Gate.SetResult();

        await applying;

        prompt.Sections.Select(s => s.Key).Should().Equal(new[] { "earlier", "later" },
            "секции склеиваются по Order, а не по порядку завершения или получения");
        prompt.ContributorTimingsMs.Should().ContainKeys("earlier", "later");
    }

    // TS-18: исключение одного члена группы не ломает ни второго, ни хода:
    // сбойный измеряется как державший сборку (как раньше), здоровый даёт свою секцию,
    // цепочка продолжается следующим фильтром.
    [Fact]
    public async Task ParallelGroup_FailingMemberDoesNotBreakTheOtherOrTheTurn()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        var bad = new GateContributor("bad", 200, throws: true);
        var good = new GateContributor("good", 300);
        var after = new OrderContributor("after", 400, "после группы"); // не parallel-safe, после группы
        PromptSectionContributorsRegistration.RegisterAll(bus,
            new IPromptSectionContributor[] { bad, good, after });

        var prompt = new PromptAssembling(new TurnContext("session-1", "owner-1", 1, 0), ParallelCtx());
        var applying = bus.ApplyAsync(prompt);

        var bothEntered = Task.WhenAll(bad.Entered, good.Entered);
        (await Task.WhenAny(bothEntered, Task.Delay(5000))).Should().Be(bothEntered,
            "оба члена группы стартуют одновременно");

        good.Gate.SetResult();
        bad.Gate.SetResult(); // внутри BuildAsync — намеренное исключение

        await applying; // ход не падает

        prompt.Sections.Select(s => s.Key).Should().Equal(new[] { "good", "after" },
            "исключение одного члена не уносит секцию другого и не обрывает цепочку");
        prompt.ContributorTimingsMs.Should().ContainKeys("bad", "good", "after");
    }

    // TS-18: чужой контрибьютор с Order строго между членами ParallelSafe-группы разрывает её:
    // иначе склеенная группа обогнала бы его в цепочке. Оба члена отпущены сразу (гейты открыты),
    // порядок секций доказывает разрыв: при склейке в группу было бы earlier, later, foreign.
    [Fact]
    public async Task ParallelGroup_ForeignOrderBetweenMembers_BreaksTheGroup()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        var earlier = new GateContributor("earlier", 200);
        var foreign = new OrderContributor("foreign", 250, "чужая");
        var later = new GateContributor("later", 300);
        earlier.Gate.SetResult();
        later.Gate.SetResult();
        PromptSectionContributorsRegistration.RegisterAll(bus,
            new IPromptSectionContributor[] { later, foreign, earlier });

        var prompt = new PromptAssembling(new TurnContext("session-1", "owner-1", 1, 0), ParallelCtx());
        await bus.ApplyAsync(prompt);

        prompt.Sections.Select(s => s.Key).Should().Equal(new[] { "earlier", "foreign", "later" },
            "чужая секция между членами не должна быть обогнана склеенной группой");
    }

    // TS-18: равный Order у двух ParallelSafe-членов группу не разрывает (чужого Order строго
    // между ними нет): оба внутри BuildAsync одновременно, секции — в порядке получения.
    [Fact]
    public async Task ParallelGroup_EqualOrder_StaysOneGroupInReceiptOrder()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        var a = new GateContributor("a", 200);
        var b = new GateContributor("b", 200);
        PromptSectionContributorsRegistration.RegisterAll(bus,
            new IPromptSectionContributor[] { a, b });

        var prompt = new PromptAssembling(new TurnContext("session-1", "owner-1", 1, 0), ParallelCtx());
        var applying = bus.ApplyAsync(prompt);

        var bothEntered = Task.WhenAll(a.Entered, b.Entered);
        (await Task.WhenAny(bothEntered, Task.Delay(5000))).Should().Be(bothEntered,
            "равный Order не разрывает группу: оба члена внутри BuildAsync одновременно");
        a.Gate.SetResult();
        b.Gate.SetResult();
        await applying;

        prompt.Sections.Select(s => s.Key).Should().Equal(new[] { "a", "b" },
            "при равном Order склейка стабильна — в порядке получения реестра");
    }

    // TS-18: parallel-safe ровно у пары recall (recall-notes + recall-memory).
    // Сторож от обратного: «отметить своего контрибьютора параллельным за скорость»,
    // не проверив отсутствия общего состояния, и от потери отметки.
    [Fact]
    public void ParallelSafe_IsClaimedExactlyByRecallPair()
    {
        using var provider = BuildSut();

        var parallelSafe = provider.GetServices<IPromptSectionContributor>()
            .Where(c => c.ParallelSafe).Select(c => c.Key).ToList();

        parallelSafe.Should().BeEquivalentTo(new[] { "recall-notes", "recall-memory" },
            "parallel-safe — только проверенная пара recall; остальные по дефолту последовательны");
    }

    // Замер для паспорта хода: обёртка реестра пишет длительность каждого СРАБОТАВШЕГО
    // контрибьютора; выключенный (IsEnabled=false) в замеры не попадает.
    [Fact]
    public async Task RegisterAll_RecordsDurationOnlyForEnabledContributors()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        PromptSectionContributorsRegistration.RegisterAll(bus, new IPromptSectionContributor[]
        {
            new TimedContributor("slow", 100, enabled: true, delayMs: 80),
            new TimedContributor("off", 200, enabled: false, delayMs: 80),
        });
        var session = new Session { OwnerId = "owner-1" };
        var ctx = new PromptSessionContext(session, "owner-1", Persona: null, RootPath: null,
            HasNotesMcp: false, HasMemoryMcp: false, HasWorkspaceMcp: false,
            WorkspaceSections: Array.Empty<string>());
        var prompt = new PromptAssembling(new TurnContext("session-1", "owner-1", 1, 0), ctx);

        await bus.ApplyAsync(prompt);

        prompt.ContributorTimingsMs.Should().ContainKey("slow");
        prompt.ContributorTimingsMs["slow"].Should().BeGreaterThanOrEqualTo(50);
        prompt.ContributorTimingsMs.Should().NotContainKey("off");
    }

    // Сквозной замер для slowSections: контрибьютор с задержкой 1 с попадает в выборку паспорта
    // со значением ≥ 1000, быстрый — нет.
    [Fact]
    public async Task SlowContributor_LandsInSlowSections_FastDoesNot()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        PromptSectionContributorsRegistration.RegisterAll(bus, new IPromptSectionContributor[]
        {
            new TimedContributor("slow", 100, enabled: true, delayMs: 1000),
            new TimedContributor("fast", 200, enabled: true, delayMs: 0),
        });
        var session = new Session { OwnerId = "owner-1" };
        var ctx = new PromptSessionContext(session, "owner-1", Persona: null, RootPath: null,
            HasNotesMcp: false, HasMemoryMcp: false, HasWorkspaceMcp: false,
            WorkspaceSections: Array.Empty<string>());
        var prompt = new PromptAssembling(new TurnContext("session-1", "owner-1", 1, 0), ctx);

        await bus.ApplyAsync(prompt);
        var slow = TurnStartTimings.SelectSlow(prompt.ContributorTimingsMs);

        slow.Should().NotBeNull();
        var map = slow!;
        map.Should().ContainKey("slow");
        map["slow"].Should().BeGreaterThanOrEqualTo(1000);
        map.Should().NotContainKey("fast");
    }

    // Тест 4 (НЕГАТИВНЫЙ): без регистрации контрибьюторов ApplyAsync даёт пустые Sections.
    // Доказательство, что тест 5 реально сторожит: если убрать вызов из Program.cs, в
    // проде шина будет пустой и секции промпта не появятся.
    [Fact]
    public async Task WithoutRegistration_ApplyAsyncProducesEmptySections()
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        // Намеренно НЕ вызываем RegisterAll.

        var session = new Session { OwnerId = "owner-1" };
        var ctx = new PromptSessionContext(session, "owner-1", Persona: null, RootPath: null,
            HasNotesMcp: true, HasMemoryMcp: true, HasWorkspaceMcp: true,
            WorkspaceSections: Array.Empty<string>());
        var turn = new TurnContext("session-1", "owner-1", 1, 0);
        var prompt = new PromptAssembling(turn, ctx);

        await bus.ApplyAsync(prompt);

        prompt.Sections.Should().BeEmpty(
            "без RegisterAll в шине нет подписчиков на prompt/assembling — секции не появятся, " +
            "и это и есть та «тихая деградация», которую сторожит тест 5");
        prompt.ManifestItems.Should().BeEmpty();
    }

    // Тест 5 (НЕГАТИВНАЯ СТОРОЖЕВАЯ): Program.cs ДОЛЖЕН содержать незакомментированный
    // вызов AddPromptSectionContributors(). Парсинг текстовый — без WebApplicationFactory
    // (тот поднимает SignalR-хаб и хост и для этой задачи дороже, чем пользы).
    //
    // Зачем: тесты 1–3 проверяют сам extension-метод и шину, но НЕ проверяют, что
    // метод вызван в Program.cs. Без этого теста можно спокойно закомментировать вызов,
    // тесты останутся зелёными, а в проде упадёт сборка промпта: тихая деградация,
    // против которой и писалась задача. Парсинг Program.cs — простой явный страж.
    [Fact]
    public void ProgramCs_CallsAddPromptSectionContributors_Uncommented()
    {
        // Ищем Program.cs от корня репо вверх (тесты запускаются из backend/).
        var programPath = LocateProgramCs();
        File.Exists(programPath).Should().BeTrue($"Program.cs должен существовать по пути {programPath}");

        var lines = File.ReadAllLines(programPath);

        // Строки с вызовом — есть ли такая вообще. Игнорируем чисто комментарийные строки:
        // // AddPromptSectionContributors() — закомментированный вызов; не считается.
        var callLines = lines
            .Select((text, idx) => (Text: text.TrimStart(), Number: idx + 1))
            .Where(l => l.Text.Contains("AddPromptSectionContributors(", StringComparison.Ordinal))
            .ToList();

        callLines.Should().NotBeEmpty(
            "Program.cs должен содержать вызов AddPromptSectionContributors() — " +
            "иначе в проде DI отдаст пустой набор IPromptSectionContributor, и секции промпта " +
            "(recall-notes, persona-layer и т.д.) молча пропадут");

        // Среди строк с упоминанием должна быть хотя бы одна НЕ закомментированная,
        // у которой после trim остался именно вызов (не текст в //-комментарии).
        var uncommentedCall = callLines.Any(l =>
            !l.Text.StartsWith("//") && l.Text.Contains("builder.Services.AddPromptSectionContributors"));
        uncommentedCall.Should().BeTrue(
            "вызов AddPromptSectionContributors() должен быть РАСКОММЕНТИРОВАН в Program.cs — " +
            "закомментированный вызов означает «DI пуст», что и есть та самая тихая деградация");
    }

    // Поднимаемся от bin/ теста до каталога, содержащего *.slnx (это backend/ClaudeHomeServer.slnx).
    // Program.cs лежит на одном уровне с .slnx.
    private static string LocateProgramCs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.EnumerateFiles(dir.FullName, "*.slnx").Any())
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "ClaudeHomeServer", "Program.cs");
    }

    // Минимальный DI-граф, достаточный для резолва всех 8 контрибьюторов. По образцу
    // SessionManagerPromptSectionsProviderTests.BuildSut — там собирается похожий набор.
    private static ServiceProvider BuildSut()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "promptsectest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(tempDir, "data", "projects.json"),
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddSingleton<FakeHostEnvironment>();

            // Базовые сервисы — реальные (нужны для конструкторов конкретных классов).
            var userStore = new UserStore(config,
                new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
            services.AddSingleton(userStore);
            // Шов IUserStore (Core) — UserStore реализует шов, регистрируем по тому же
            // образцу, что и в Program.cs.
            services.AddSingleton<IUserStore>(userStore);
            var appSettings = new AppSettingsService(config);
            services.AddSingleton(appSettings);
            var projectManager = new ProjectManager(config, userStore, appSettings);
            services.AddSingleton(projectManager);
            // Шов IProjectManager (Core) — на ту же реализацию, что и ProjectManager
            // (тот же приём, что в Program.cs: полный ProjectManager реализует шов).
            services.AddSingleton<IProjectManager>(projectManager);
            // Шов IProjectRootLookup зарегистрирован в Program.cs; в тесте регистрируем
            // вручную (тот же ProjectRootLookup, что и в Main).
            services.AddSingleton<IProjectRootLookup>(_ => new ProjectRootLookup(projectManager));
            var history = new ChatHistoryService(config);
            services.AddSingleton(history);
            var wkStore = new WorkspaceKnowledgeStore(config);
            services.AddSingleton(wkStore);
            var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
                Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
            services.AddSingleton(knowledge);
            var flags = new FeatureFlagService(userStore);
            services.AddSingleton(flags);
            var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
            services.AddSingleton(notesSvc);
            var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
                NullLogger<NotesKnowledgeService>.Instance);
            services.AddSingleton(notesKb);
            var personas = new PersonaManager(config);
            services.AddSingleton(personas);
            var personaMemory = new PersonaMemoryService(knowledge, personas, personas,
                new PersonaDirectoryAdapter(personas), new NoopPersonaEvents(),
                new EmptyDifyMetrics(), userStore, config,
                NullLogger<PersonaMemoryService>.Instance);
            services.AddSingleton(personaMemory);
            var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
                knowledge, new SkillsService(), userStore, config,
                NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
            services.AddSingleton(bindings);
            // Этап 5, шаг 6: CodeGraphContributor гейтит секцию через Core-шов
            // IPersonaServerToolGate, а не через конкретный PersonaBindingsService.
            // Форвардер — тот же, что в Program.cs.
            services.AddSingleton<IPersonaServerToolGate>(bindings);
            var promptBuilder = new PersonaPromptBuilder(
                new LlmProviderRegistry(config));
            services.AddSingleton(promptBuilder);
            services.AddSingleton<SpecialtySettingsStore>(); // для PromptSectionsContributor
            // Этап 5, шаг 4: контрибьютор идёт через Core-шов IPromptSectionProvider,
            // не через прямую SpecialtySettingsStore. В тесте подключаем адаптер по тому
            // же рецепту, что и в Main (`LlmSubsystem.Register`).
            services.AddSingleton<IPromptSectionProvider>(sp =>
                new SpecialtyPromptSectionProvider(sp.GetRequiredService<SpecialtySettingsStore>()));
            services.AddSingleton<DossierStore>(); // для PersonaRecallContributor
            services.AddSingleton<DossierRecallService>(); // для PersonaRecallContributor
            // Этап 5, узкие швы Turn: контрибьютор идёт в память персоны через Core-шов
            // IPersonaRecallSource, а не через PersonaMemoryService напрямую. Адаптер —
            // тот же, что регистрирует Program.cs.
            services.AddSingleton<IPersonaRecallSource, PersonaRecallSourceAdapter>();
            // CodeGraphService с зависимостями: нужен для CodeGraphPromptProvider → CodeGraphContributor
            services.AddSingleton<CodeGraphService>(sp =>
                new CodeGraphService(
                    NullLogger<CodeGraphService>.Instance,
                    sp.GetRequiredService<IProjectRootLookup>(),
                    new GraphPersistence(Path.Combine(tempDir, "data"), NullLogger<GraphPersistence>.Instance),
                    sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton<CodeGraphPromptProvider>(); // для CodeGraphContributor
            services.AddSingleton<SkillsService>(); // для адаптеров швов слоя персоны
            // Этап 5, узкие швы Turn: слой персоны идёт к умениям и к каталогу командных
            // механик через Core-швы, а не через SkillsService/TeamMechanicsPromptCatalog
            // напрямую. Адаптеры — те же, что в Program.cs.
            services.AddSingleton<IAgentPromptSource, AgentPromptSourceAdapter>();
            services.AddSingleton<ITeamMechanicsBlockSource, TeamMechanicsBlockAdapter>();
            // Этап 5 (Turn): ещё 4 узких шва, чтобы Turn зависел только от Core.
            // В тесте — те же форвардеры на корневые сервисы, что и в Program.cs.
            services.AddSingleton<IFeatureFlagGate, FeatureFlagGateAdapter>();
            services.AddSingleton<IPersonaResolver, PersonaResolverAdapter>();
            services.AddSingleton<IPersonaPromptAssembler, PersonaPromptAssemblerAdapter>();
            services.AddSingleton<IPersonaBindingsSource, PersonaBindingsSourceAdapter>();
            services.AddSingleton<IChatHistoryLoader, ChatHistoryLoaderAdapter>();
            // Хвост «Контекст хода»: реестр видов и стор контекста чата (в проде — AddChatContext)
            services.AddSingleton<ContextKindRegistry>(new ContextKindRegistry([]));
            services.AddSingleton<IChatContextStore>(new Mock<IChatContextStore>().Object);
            // Устав сферы: справочник сфер в проде даёт Program.cs; здесь — заглушка
            services.AddSingleton<ISphereDirectory>(new Mock<ISphereDirectory>().Object);

            // Реестр актора хода в проде — SessionManager, который сам получает всех контрибьюторов
            // в конструкторе. Имитируем этот цикл: контрибьютор с зависимостью от реестра в
            // конструкторе не резолвится (циклическая зависимость), хост не поднялся бы.
            // Фабрики для детектора циклов DI непрозрачны, поэтому реэнтрантный вход ловим сами —
            // иначе возврат зависимости дал бы бесконечную рекурсию, а не внятное падение.
            var inRegistry = false;
            services.AddSingleton<ClaudeHomeServer.Services.Access.ITurnActorRegistry>(sp =>
            {
                if (inRegistry)
                    throw new InvalidOperationException(
                        "Циклическая зависимость: реестр актора хода запрошен при построении контрибьюторов");
                inRegistry = true;
                try { _ = sp.GetServices<IPromptSectionContributor>().ToList(); }
                finally { inRegistry = false; }
                return new Mock<ClaudeHomeServer.Services.Access.ITurnActorRegistry>().Object;
            });

            // Логгеры для контрибьюторов с ILogger в конструкторе
            services.AddSingleton<ILogger<NotesRecallContributor>>(NullLogger<NotesRecallContributor>.Instance);
            services.AddSingleton<ILogger<PersonaBindingsContributor>>(NullLogger<PersonaBindingsContributor>.Instance);
            services.AddSingleton<ILogger<CodeGraphContributor>>(NullLogger<CodeGraphContributor>.Instance);
            services.AddSingleton<ILogger<PersonaRecallContributor>>(NullLogger<PersonaRecallContributor>.Instance);

            // Собственно регистрация контрибьюторов — та же, что в Program.cs: 6 чистых
            // из Turn + по одному из CodeGraph и Notes (их в проде регистрируют свои
            // *Subsystem.Register; здесь — тот же extension-паттерн, что в подсистемах).
            services.AddPromptSectionContributors();
            services.AddPromptSectionContributor<CodeGraphContributor>();
            services.AddPromptSectionContributor<NotesRecallContributor>();

            return services.BuildServiceProvider();
        }
        finally
        {
            // ServiceProvider IDisposable — очистка делается вызывающим через using.
            // Папку удалит IDisposable-обёртка ниже; эту логику оставляем на using.
            _ = tempDir;
        }
    }

    // Минимальный контрибьютор-стаб для теста шины: всегда IsEnabled=true, секция с заданным Key/Order/Text.
    private sealed class OrderContributor : IPromptSectionContributor
    {
        public OrderContributor(string key, int order, string text)
        {
            Key = key;
            Order = order;
            _text = text;
        }
        public string Key { get; }
        public string Title => Key;
        public int Order { get; }
        public string Group => "test";
        public bool IsEnabled(PromptSessionContext sessionContext) => true;
        public Task<PromptSectionContribution?> BuildAsync(
            PromptSessionContext sessionContext, string? turnText) =>
            Task.FromResult<PromptSectionContribution?>(
                new PromptSectionContribution([new PromptSection(Key, _text)]));
        private readonly string _text;
    }

    private sealed class TimedContributor(string key, int order, bool enabled, int delayMs) : IPromptSectionContributor
    {
        public string Key => key;
        public string Title => key;
        public int Order => order;
        public string Group => "test";
        public bool IsEnabled(PromptSessionContext sessionContext) => enabled;
        public async Task<PromptSectionContribution?> BuildAsync(
            PromptSessionContext sessionContext, string? turnText)
        {
            await Task.Delay(delayMs);
            return new PromptSectionContribution([new PromptSection(key, key)]);
        }
    }

    // TS-18: член параллельной группы с гейтами: Entered сигналит, что работа началась,
    // Gate отпускает завершение, Done — что BuildAsync уже вернулся. Тест держит
    // порядок завершения сам (TaskCompletionSource), без Task.Delay.
    // RunContinuationsAsynchronously — чтобы SetResult из теста не исполнял продолжение
    // на теме теста (иначе тест встал бы внутри своего же гейта).
    private sealed class GateContributor(string key, int order, bool throws = false) : IPromptSectionContributor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Key => key;
        public string Title => key;
        public int Order => order;
        public string Group => "test";
        public bool ParallelSafe => true;
        public bool IsEnabled(PromptSessionContext sessionContext) => true;
        public Task Entered => _entered.Task;
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Done => _done.Task;
        public async Task<PromptSectionContribution?> BuildAsync(
            PromptSessionContext sessionContext, string? turnText)
        {
            _entered.SetResult();
            await Gate.Task;
            if (throws) throw new InvalidOperationException($"{key}: намеренный сбой теста");
            _done.SetResult();
            return new PromptSectionContribution([new PromptSection(key, key)]);
        }
    }

    private static PromptSessionContext ParallelCtx()
    {
        var session = new Session { OwnerId = "owner-1" };
        return new PromptSessionContext(session, "owner-1", Persona: null, RootPath: null,
            HasNotesMcp: false, HasMemoryMcp: false, HasWorkspaceMcp: false,
            WorkspaceSections: Array.Empty<string>());
    }
}
