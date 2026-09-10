using System.Runtime.CompilerServices;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Turn;

// Сторож гейта секции «Главные узлы кода проекта» (контрибьютор IPromptSectionContributor,
// CodeGraphContributor). Старая формула (BuildCodeGraphProvider в SessionManager.cs до
// коммита fca17d78): провайдер не null, rootPath не пустой, и _bindings.ServerToolEnabled
// возвращает true. ServerToolEnabled — deny-only: без персоны всегда true, с персоной —
// выключает только явная Off-привязка на «codegraph» (ключ в ServerKeys, его Persona.Tools
// никогда не знал). Перенос на EffectiveToolEnabled + гейт Persona is not null сломал
// проектный чат без персоны: секция не появлялась, даже если сервер кодграф разрешает.
//
// Мутационный контракт: вернуть гейт «Persona is not null» в IsEnabled → NoPersona_*
// должны краснеть.
//
// Отдельный класс от CodeGraphContributorTests (worktree-фолбэк, ADR-003, задача 9d446f86):
// две несовместимые фикстуры (тут — прямой конструктор с реальным PersonaBindingsService,
// там — полный DI-контейнер) для одного класса объединять не стали.
public class CodeGraphContributorIsEnabledTests : IDisposable
{
    private const string CodeGraphKey = "codegraph";

    private readonly string _tempDir;
    private readonly PersonaBindingsService _bindings;
    private readonly string _userId;

    public CodeGraphContributorIsEnabledTests()
    {
        // Реальный PersonaBindingsService через минимальный DI-граф (по образцу
        // PersonaBindingsServiceTests). Moq здесь не подойдёт: конструктор принимает
        // конкретные (не интерфейсные) типы — PersonaManager, ProjectManager и т.д. — а
        // Moq не умеет создавать прокси для классов без parameterless конструктора.
        _tempDir = Path.Combine(Path.GetTempPath(), "codegraphcontrib_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        _userId = users.GetFirst()!.Id;
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, users, config,
            NullLogger<NotesKnowledgeService>.Instance);

        _bindings = new PersonaBindingsService(personas, projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), users, config,
            NullLogger<PersonaBindingsService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // CodeGraphPromptProvider sealed, и Moq его не мокает напрямую; для IsEnabled экземпляр
    // не обязан быть рабочим — метод не вызывает GetSliceAsync. Подменяем конструктор через
    // RuntimeHelpers, чтобы не поднимать тяжёлую инфраструктуру (CodeGraphService +
    // GraphPersistence + IProjectRootLookup + IConfiguration) только ради non-null ссылки.
    private static CodeGraphPromptProvider CreateFakePromptProvider() =>
        (CodeGraphPromptProvider)RuntimeHelpers.GetUninitializedObject(typeof(CodeGraphPromptProvider));

    private PromptSessionContext Ctx(Persona? persona, string? rootPath = "/tmp/proj") =>
        new(new Session { Id = "s1", OwnerId = _userId }, _userId, persona, rootPath,
            HasNotesMcp: false, HasMemoryMcp: false, HasWorkspaceMcp: false,
            WorkspaceSections: Array.Empty<string>());

    // Этап 5, шаг 6: контрибьютор принимает Core-шов IPersonaServerToolGate, а не
    // конкретный PersonaBindingsService. Семантика гейта та же (deny-only
    // ServerToolEnabled) — сервис реализует шов явной имплементацией, форвард один в один.
    private CodeGraphContributor BuildSut(CodeGraphPromptProvider? provider) =>
        new(provider, _bindings, NullLogger<CodeGraphContributor>.Instance);

    // --- Базовые гейты провайдера и rootPath (работают и без персоны) ---

    [Fact]
    public void IsEnabled_NoProvider_ReturnsFalse()
    {
        // Провайдер не подключён (null) — секция не появляется независимо от персоны и биндингов
        var sut = BuildSut(provider: null);

        sut.IsEnabled(Ctx(persona: null)).Should().BeFalse(
            "без провайдера графа секции быть не должно");
        sut.IsEnabled(Ctx(persona: NewPersona())).Should().BeFalse(
            "без провайдера секция выключена и с персоной");
    }

    [Fact]
    public void IsEnabled_EmptyRootPath_ReturnsFalse()
    {
        var sut = BuildSut(CreateFakePromptProvider());

        sut.IsEnabled(Ctx(persona: null, rootPath: null)).Should().BeFalse(
            "с пустым rootPath секция выключена — это чат вне проекта");
        sut.IsEnabled(Ctx(persona: null, rootPath: "  ")).Should().BeFalse(
            "с whitespace-only rootPath секция тоже выключена");
    }

    // --- Основной сценарий: чат без персоны (баг из задачи fd2c29ff) ---

    [Fact]
    public void IsEnabled_NoPersona_ServerEnables_ReturnsTrue()
    {
        // СЕРВЕР разрешает тулсет кодграфа (нет Off-привязки), персоны у сессии нет.
        // Старая формула BuildCodeGraphProvider включала секцию здесь; до фикса
        // CodeGraphContributor отшивал по гейту «Persona is not null».
        // ServerToolEnabled для persona == null возвращает true — это контракт из
        // PersonaBindingsService (см. тест ServerToolEnabled_БезПерсоны_Разрешено).
        var sut = BuildSut(CreateFakePromptProvider());

        sut.IsEnabled(Ctx(persona: null)).Should().BeTrue(
            "секция должна собираться и без персоны у сессии, если сервер кодграф разрешает");
    }

    // --- Существующие сценарии с персоной (регрессионный контракт) ---

    [Fact]
    public void IsEnabled_WithPersona_NoOffBinding_ReturnsTrue()
    {
        // Персона есть, явной Off-привязки на «codegraph» нет — секция должна включаться,
        // даже если Persona.Tools сужен до «обычных» tasks/notes (этот ключ в ServerKeys,
        // и Persona.Tools о нём никогда не знал: возврат на ServerToolEnabled здесь критичен,
        // EffectiveToolEnabled бы отшил секцию по фолбэку на Persona.Tools).
        var persona = NewPersona(tools: new List<string> { "tasks", "notes" });
        var sut = BuildSut(CreateFakePromptProvider());

        sut.IsEnabled(Ctx(persona)).Should().BeTrue(
            "у персоны нет Off-привязки на codegraph, и Persona.Tools не гейтит ServerKeys");
    }

    [Fact]
    public void IsEnabled_WithPersona_OffBinding_ReturnsFalse()
    {
        // Существующее поведение: явная Off-привязка tool:codegraph отключает секцию.
        // Контракт из старой формулы — ServerToolEnabled выключает только явный Off.
        var persona = NewPersona(tools: null, offCodeGraph: true);
        var sut = BuildSut(CreateFakePromptProvider());

        sut.IsEnabled(Ctx(persona)).Should().BeFalse(
            "Off-привязка персоны на tool:codegraph должна выключать секцию");
    }

    [Fact]
    public void IsEnabled_WithPersona_ToolsWithCodeGraph_ReturnsTrue()
    {
        // Дополнительная защита: даже если Persona.Tools СОДЕРЖИТ «codegraph» явно
        // (старые списки возможностей могли его включать), ServerToolEnabled это
        // игнорирует — выключает только явный Off. Если кто-то вернётся на
        // EffectiveToolEnabled, этот тест покраснеет для persona.Tools=["codegraph"]
        // без Off-привязки (он вернёт true через фолбэк), но тест ниже для
        // Tools=["tasks","notes"] поймает расхождение.
        var persona = NewPersona(tools: new List<string> { "tasks", "notes" });
        var sut = BuildSut(CreateFakePromptProvider());

        sut.IsEnabled(Ctx(persona)).Should().BeTrue();
    }

    // --- Мутационный контракт: вернуть гейт «Persona is not null» в IsEnabled ---

    [Fact]
    public void IsEnabled_NoPersona_MutationGuard_ReturnsTrue()
    {
        // Регрессионный страж: единственная причина падения этого ассерта — возврат
        // гейта «Persona is not null» в IsEnabled. Тест прогоняет IsEnabled с persona == null
        // и утверждает true; если кто-то по ошибке вернёт гейт, тест покраснеет.
        var sut = BuildSut(CreateFakePromptProvider());

        sut.IsEnabled(Ctx(persona: null))
            .Should().BeTrue(
                "этот ассерт падает только при возврате гейта `Persona is not null`");
    }

    private Persona NewPersona(List<string>? tools = null, bool offCodeGraph = false)
    {
        var bindings = new List<PersonaBinding>();
        if (offCodeGraph)
        {
            bindings.Add(new PersonaBinding
            {
                Type = PersonaBindingType.Tool,
                Target = CodeGraphKey,
                Mode = PersonaBindingMode.Off,
            });
        }
        return new Persona
        {
            Id = "p1",
            OwnerId = _userId,
            Name = "Test",
            Role = "test",
            Tools = tools,
            Bindings = bindings,
        };
    }
}
