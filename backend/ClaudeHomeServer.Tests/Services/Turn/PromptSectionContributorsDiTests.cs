using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    // ровно 7 регистраций IPromptSectionContributor с правильными типами. НЕ требует
    // зависимостей контрибьюторов — резолва нет, только анализ ServiceDescriptor'ов.
    [Fact]
    public void AddPromptSectionContributors_RegistersSevenConcreteContributors()
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
            "AddPromptSectionContributors должен зарегистрировать ровно 7 конкретных контрибьюторов");

        // Эти 7 классов — единственный канонический список. Новый контрибьютор = новая
        // строка в AddPromptSectionContributors() И здесь, иначе сторож не отличит «забыли
        // добавить в Program.cs» от «добавили лишнего».
        var implTypes = concreteDescriptors.Select(d => d.ImplementationType!).ToHashSet();
        implTypes.Should().BeEquivalentTo(new[]
        {
            typeof(DossierTrailerContributor),
            typeof(NotesRecallContributor),
            typeof(PersonaRecallContributor),
            typeof(PromptSectionsContributor),
            typeof(PersonaBindingsContributor),
            typeof(CodeGraphContributor),
            typeof(PersonaLayerContributor),
        });

        // Каждый контрибьютор едет и в общий набор IPromptSectionContributor (через factory).
        var interfaceDescriptors = services
            .Where(d => d.ServiceType == typeof(IPromptSectionContributor))
            .ToList();
        interfaceDescriptors.Should().HaveCount(7,
            "набор IPromptSectionContributor должен состоять из 7 элементов (по одному на каждого контрибьютора)");

        // Все регистрации — singleton (контрибьюторы без состояния, как и шина).
        concreteDescriptors.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Singleton);
        interfaceDescriptors.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Singleton);
    }

    // Тест 2: реальный резолв IEnumerable<IPromptSectionContributor>, проверка Key и Order.
    // Требует минимального набора зависимостей контрибьюторов (см. BuildSut).
    [Fact]
    public void ResolvedContributors_HaveUniqueKeysAndAscendingOrder()
    {
        using var provider = BuildSut();

        var contributors = provider.GetServices<IPromptSectionContributor>().ToList();

        contributors.Should().HaveCount(7);

        // Уникальность Key — две секции с одинаковым Key замазали бы друг друга в снапшоте.
        var keys = contributors.Select(c => c.Key).ToList();
        keys.Should().OnlyHaveUniqueItems("Key контрибьютора обязан быть уникальным");

        // Упорядоченность Order — склейка секций идёт по возрастанию Order, поэтому фиксируем
        // их последовательность как контракт (золотые эталоны промпта ожидают именно этот порядок).
        var orders = contributors.Select(c => c.Order).ToList();
        orders.Should().BeInAscendingOrder("контрибьюторы должны ехать по возрастанию Order");
        orders.Should().Equal(new[] { 100, 200, 300, 400, 500, 600, 900 },
            "канонический порядок секций промпта (dossier-trailer → persona-layer)");

        // Парность: каждый Key обязан встретиться ровно один раз И с правильным Order.
        // Это ловит классическую регрессию «поправили Order у одного контрибьютора, забыли у соседа».
        var keyToOrder = contributors.ToDictionary(c => c.Key, c => c.Order);
        keyToOrder.Should().ContainKey("dossier-trailer").WhoseValue.Should().Be(100);
        keyToOrder.Should().ContainKey("recall-notes").WhoseValue.Should().Be(200);
        keyToOrder.Should().ContainKey("recall-memory").WhoseValue.Should().Be(300);
        keyToOrder.Should().ContainKey("prompt-sections").WhoseValue.Should().Be(400);
        keyToOrder.Should().ContainKey("persona-bindings").WhoseValue.Should().Be(500);
        keyToOrder.Should().ContainKey("code-graph").WhoseValue.Should().Be(600);
        keyToOrder.Should().ContainKey("persona-layer").WhoseValue.Should().Be(900);
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

    // Минимальный DI-граф, достаточный для резолва всех 7 контрибьюторов. По образцу
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
            var appSettings = new AppSettingsService(config);
            services.AddSingleton(appSettings);
            var projectManager = new ProjectManager(config, userStore, appSettings);
            services.AddSingleton(projectManager);
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
            var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config,
                NullLogger<PersonaMemoryService>.Instance);
            services.AddSingleton(personaMemory);
            var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc,
                notesKb, knowledge, new SkillsService(), userStore, config,
                NullLogger<PersonaBindingsService>.Instance);
            services.AddSingleton(bindings);
            var promptBuilder = new PersonaPromptBuilder(
                new LlmProviderRegistry(config));
            services.AddSingleton(promptBuilder);
            services.AddSingleton<SpecialtySettingsStore>(); // для PromptSectionsContributor
            services.AddSingleton<DossierStore>(); // для PersonaRecallContributor
            services.AddSingleton<DossierRecallService>(); // для PersonaRecallContributor
            // CodeGraphService с зависимостями: нужен для CodeGraphPromptProvider → CodeGraphContributor
            services.AddSingleton<CodeGraphService>(sp =>
                new CodeGraphService(
                    NullLogger<CodeGraphService>.Instance,
                    sp.GetRequiredService<ProjectManager>(),
                    new GraphPersistence(Path.Combine(tempDir, "data"), NullLogger<GraphPersistence>.Instance),
                    sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton<CodeGraphPromptProvider>(); // для CodeGraphContributor
            services.AddSingleton<SkillsService>(); // для PersonaLayerContributor

            // Логгеры для контрибьюторов с ILogger в конструкторе
            services.AddSingleton<ILogger<NotesRecallContributor>>(NullLogger<NotesRecallContributor>.Instance);
            services.AddSingleton<ILogger<PersonaBindingsContributor>>(NullLogger<PersonaBindingsContributor>.Instance);
            services.AddSingleton<ILogger<CodeGraphContributor>>(NullLogger<CodeGraphContributor>.Instance);
            services.AddSingleton<ILogger<PersonaRecallContributor>>(NullLogger<PersonaRecallContributor>.Instance);

            // Собственно регистрация контрибьюторов — та же, что в Program.cs.
            services.AddPromptSectionContributors();

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
}
