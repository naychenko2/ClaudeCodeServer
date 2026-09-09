using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Turn;

// Сторож worktree-фолбэка slice графа кода (ADR-003). Дефект, живущий с коммита fca17d78
// (перевод CodeGraph-секции на контрибьютор IPromptSectionContributor): PromptSessionContext
// не нёс корень проекта, поэтому CodeGraphContributor звал GetSliceAsync(RootPath) без
// второго аргумента и терял slice главной ветки у чата с отдельным worktree.
//
// Тесты:
//  1) Worktree-чат, граф ветки не построен → секция содержит slice главной ветки с пометкой;
//  2) Worktree-чат, граф ветки построен → секция от своей ветки без пометки «ГЛАВНОЙ ветки»;
//  3) Не-worktree чат (MainRootPath == RootPath) → fallback сводится к no-op, без пометки;
//  4) Worktree-чат, графа нет ни у одной ветки → секция code-graph пустая.
//
// Мутационная проверка: если убрать sessionContext.MainRootPath из вызова GetSliceAsync
// в CodeGraphContributor.BuildAsync, тест 1 краснеет (нет ни «Demo.Hub», ни пометки
// «ГЛАВНОЙ ветки»), а тесты 2–4 не задевает.
//
// Гейт IsEnabled (провайдер/rootPath/ServerToolEnabled, включая сценарий без персоны —
// дефект fd2c29ff) покрыт отдельным классом CodeGraphContributorIsEnabledTests: разные
// фикстуры (DI-контейнер здесь vs прямой конструктор там) для одного класса несовместимы.
public class CodeGraphContributorTests
{
    // Корневой каталог теста с двумя путями: главная ветка + worktree-ветка.
    // Граф (HubGraph) кладётся только в тот путь, который передали как non-null.
    private static (CodeGraphContributor Contributor, string MainPath, string WorktreePath)
        BuildSut(CodeGraph? graphInMain = null, CodeGraph? graphInWorktree = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cgcontrib_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var main = Path.Combine(dir, "main");
        var worktree = Path.Combine(dir, "wt");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(worktree);

        var dataDir = Path.Combine(dir, "data");
        Directory.CreateDirectory(dataDir);
        var persistence = new GraphPersistence(dataDir, NullLogger<GraphPersistence>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, null!, persistence,
            new ConfigurationBuilder().Build());
        var provider = new CodeGraphPromptProvider(graphs, NullLogger<CodeGraphPromptProvider>.Instance);

        // Без исходников нечего индексировать; IsStale при пустом .cs всё равно считает,
        // а сам HubGraph god-узлов даёт (Demo.Hub → 12 листьев). Контракт GetSliceAsync —
        // возвращать slice, когда snapshot есть и есть god-узлы.
        File.WriteAllText(Path.Combine(main, "Hub.cs"), "namespace Demo { public class Hub {} }");
        if (graphInWorktree is not null)
            File.WriteAllText(Path.Combine(worktree, "Hub.cs"), "namespace Demo { public class Hub {} }");

        if (graphInMain is not null)
            persistence.SaveAsync(main, graphInMain, CancellationToken.None).GetAwaiter().GetResult();
        if (graphInWorktree is not null)
            persistence.SaveAsync(worktree, graphInWorktree, CancellationToken.None).GetAwaiter().GetResult();

        using var provider_sp = BuildServiceProvider(provider);
        var contributor = provider_sp.GetRequiredService<CodeGraphContributor>();
        return (contributor, main, worktree);
    }

    // Минимальный DI-граф, в котором живёт настоящий PersonaBindingsService —
    // только так контрибьютор собирается через тот же путь, что в проде. Сам контрибьютор
    // резолвится напрямую из контейнера; прочие контрибьюторы не нужны — мы проверяем только
    // его поведение.
    private static ServiceProvider BuildServiceProvider(CodeGraphPromptProvider codeGraphProvider)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(Path.GetTempPath(), "data", "projects.json"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<FakeHostEnvironment>();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        services.AddSingleton(userStore);
        var appSettings = new AppSettingsService(config);
        services.AddSingleton(appSettings);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        services.AddSingleton(projectManager);
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
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        services.AddSingleton(personaMemory);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesKb,
            knowledge, new SkillsService(), userStore, config,
            NullLogger<PersonaBindingsService>.Instance, notes: notesSvc);
        services.AddSingleton(bindings);
        var promptBuilder = new PersonaPromptBuilder(new LlmProviderRegistry(config));
        services.AddSingleton(promptBuilder);
        services.AddSingleton<SpecialtySettingsStore>();
        services.AddSingleton<DossierStore>();
        services.AddSingleton<DossierRecallService>();

        services.AddSingleton(codeGraphProvider);
        services.AddSingleton<ILogger<CodeGraphContributor>>(NullLogger<CodeGraphContributor>.Instance);

        // Контрибьютор собираем здесь же — конструктор один, и он резолвит провайдер,
        // биндинги и логгер. AddPromptSectionContributors() не нужен: в тесте работаем
        // только с этим одним контрибьютором.
        services.AddSingleton<CodeGraphContributor>();

        return services.BuildServiceProvider();
    }

    // Хаб на 12 листьев (degree = 12 ≥ порога god-узлов 10) — даёт slice.
    private static CodeGraph HubGraph()
    {
        var nodes = new Dictionary<string, CodeGraphNode>
        {
            ["Demo.Hub"] = new()
            {
                Id = "Demo.Hub", Label = "Hub", FullyQualifiedName = "Demo.Hub",
                SourceFile = "Hub.cs", SourceLocation = "L1", Kind = NodeKind.Class,
            },
        };
        var edges = new List<CodeGraphEdge>();
        for (int i = 0; i < 12; i++)
        {
            var id = $"Demo.Leaf{i}";
            nodes[id] = new()
            {
                Id = id, Label = $"Leaf{i}", FullyQualifiedName = id,
                SourceFile = $"Leaf{i}.cs", SourceLocation = "L1", Kind = NodeKind.Class,
            };
            edges.Add(new() { Source = "Demo.Hub", Target = id, Relation = EdgeRelation.References,
                Confidence = EdgeConfidence.Extracted });
        }
        return new() { Nodes = nodes, Edges = edges };
    }

    // Сессия, у которой включён worktree (WorktreePath != null). Владелец и персона — реальные,
    // потому что IsEnabled контрибьютора гейтит по Persona != null + EffectiveToolEnabled.
    private static PromptSessionContext BuildContext(string rootPath, string mainRootPath) =>
        new(
            Session: new Session { OwnerId = "owner-1", WorktreePath = rootPath },
            OwnerId: "owner-1",
            Persona: new Persona { Id = "p1", OwnerId = "owner-1" },
            RootPath: rootPath,
            MainRootPath: mainRootPath,
            HasNotesMcp: true,
            HasMemoryMcp: true,
            HasWorkspaceMcp: true,
            WorkspaceSections: Array.Empty<string>());

    [Fact]
    public async Task BuildAsync_WorktreeЧатГрафВеткиНеПостроен_БерётSliceГлавнойВетки()
    {
        // Чат в worktree, граф worktree-ветки не построен, граф главной ветки проиндексирован.
        // Ожидаем: секция code-graph содержит slice главной ветки + пометку «ГЛАВНОЙ ветки».
        var (contrib, main, worktree) = BuildSut(graphInMain: HubGraph(), graphInWorktree: null);
        var ctx = BuildContext(worktree, main);

        var contribution = await contrib.BuildAsync(ctx, "разберись со структурой проекта");

        contribution.Should().NotBeNull();
        var codeGraph = contribution!.Sections.Single(s => s.Key == "code-graph");
        codeGraph.Text.Should().NotBeNullOrEmpty("worktree-чат без своего графа должен получить slice");
        codeGraph.Text.Should().Contain("Demo.Hub",
            "slice взят из графа ГЛАВНОЙ ветки — хаб Demo.Hub должен попасть");
        codeGraph.Text.Should().Contain("ГЛАВНОЙ ветки",
            "агент должен знать, что срез не от его дерева");
        // Параллельная статичная секция едет всегда, когда провайдер активен
        contribution.Sections.Should().Contain(s => s.Key == "code-navigation");
    }

    [Fact]
    public async Task BuildAsync_WorktreeЧатГрафВеткиПостроен_БерётСвойГрафБезПометки()
    {
        // Чат в worktree, граф ветки построен: fallback не применяется, метки «ГЛАВНОЙ ветки» нет.
        var (contrib, main, worktree) = BuildSut(graphInMain: HubGraph(), graphInWorktree: HubGraph());
        var ctx = BuildContext(worktree, main);

        var contribution = await contrib.BuildAsync(ctx, null);

        var codeGraph = contribution!.Sections.Single(s => s.Key == "code-graph");
        codeGraph.Text.Should().Contain("Demo.Hub");
        codeGraph.Text.Should().NotContain("ГЛАВНОЙ ветки",
            "свой граф есть — slice от ГЛАВНОЙ ветки не подмешивается");
    }

    [Fact]
    public async Task BuildAsync_НеWorktreeЧат_MainRootPathСовпадаетСRootPath_FallbackНеВиден()
    {
        // Обычный чат (без worktree): MainRootPath == RootPath; GetSliceAsync в провайдере
        // сводит fallback к no-op (нормализованные пути совпадают). Slice берётся по своему
        // графу без пометки, как и должно быть для не-worktree чата.
        var (contrib, main, _) = BuildSut(graphInMain: HubGraph(), graphInWorktree: null);
        var ctx = BuildContext(main, main);

        var contribution = await contrib.BuildAsync(ctx, null);

        var codeGraph = contribution!.Sections.Single(s => s.Key == "code-graph");
        codeGraph.Text.Should().Contain("Demo.Hub");
        codeGraph.Text.Should().NotContain("ГЛАВНОЙ ветки",
            "MainRootPath == RootPath → провайдер не подмешивает чужой slice");
    }

    [Fact]
    public async Task BuildAsync_WorktreeЧатГрафовНетНигде_СекцияПустая()
    {
        // Worktree-чат, но графа нет ни в ветке, ни в главном дереве: fallback не спас —
        // вернётся null, и секция code-graph будет пустой строкой.
        var (contrib, main, worktree) = BuildSut(graphInMain: null, graphInWorktree: null);
        var ctx = BuildContext(worktree, main);

        var contribution = await contrib.BuildAsync(ctx, null);

        var codeGraph = contribution!.Sections.Single(s => s.Key == "code-graph");
        codeGraph.Text.Should().BeEmpty("графов нет ни у одной ветки — fallback не спас");
    }
}
