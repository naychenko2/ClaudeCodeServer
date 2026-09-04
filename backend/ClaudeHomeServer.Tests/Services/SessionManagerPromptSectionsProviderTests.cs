using System.Reflection;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// SessionManager.BuildPromptSectionsProvider (план Ђ—екции промптовї этап 3): граничные
// контракты Ч персона без специальности (none), групповой чат, гейт по флагу
// specialty-prompt-sections на каждый ход (переключение действует сразу, без пересборки).
// —борка сво€ (а не общий SessionManagerTests): нужен SpecialtySettingsStore, которого
// у общего _sut той сборки нет.
public class SessionManagerPromptSectionsProviderTests : IDisposable
{
    private readonly string _tempDir;

    public SessionManagerPromptSectionsProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smgr_prompt_sections_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (SessionManager Sut, PersonaManager Personas, UserStore Users, string OwnerId) BuildSut()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new SkillsService(),
            new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        var launchers = new LauncherFactory(userStore, sandbox);
        var specialtySettings = ClaudeHomeServer.Tests.Helpers.TestSpecialtyStore.Create(config);

        var sut = new SessionManager(projectManager, hub.Object, historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            launchers, sandbox, specialtySettings: specialtySettings);

        // UserStore при пустом хранилище создаЄт дефолтного admin Ч используем его id владельцем
        var ownerId = userStore.GetFirst()!.Id;
        return (sut, personas, userStore, ownerId);
    }

    private static Func<string?, Task<string?>>? Invoke(
        SessionManager sut, string? ownerId, Session session, Persona? persona)
    {
        var method = typeof(SessionManager).GetMethod("BuildPromptSectionsProvider",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Func<string?, Task<string?>>?)method.Invoke(sut, [ownerId, session, persona]);
    }

    [Fact]
    public void —пециальностьNone_ѕровайдерNull()
    {
        var (sut, personas, _, ownerId) = BuildSut();
        var persona = personas.Create(ownerId, "јн€", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: PersonaSpecialty.None);

        var provider = Invoke(sut, ownerId, new Session(), persona);

        provider.Should().BeNull("у персоны нет специальности Ч секций не бывает по контракту плана");
    }

    [Fact]
    public void √рупповой„ат_ѕровайдерNull()
    {
        var (sut, personas, _, ownerId) = BuildSut();
        var persona = personas.Create(ownerId, "Ѕор€", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: PersonaSpecialty.Executor);
        var session = new Session { Participants = [persona.Id, "друга€-персона"] };

        var provider = Invoke(sut, ownerId, session, persona);

        provider.Should().BeNull("групповые чаты без секций специальности Ч контракт плана");
    }

    [Fact]
    public void ѕерсонаNull_ѕровайдерNull()
    {
        var (sut, _, _, ownerId) = BuildSut();

        var provider = Invoke(sut, ownerId, new Session(), persona: null);

        provider.Should().BeNull("сесси€ без персоны (онбординг, чат-мастер) Ч секций нет");
    }

    [Fact]
    public async Task ‘лаг¬ыключен_“екстNull()
    {
        var (sut, personas, _, ownerId) = BuildSut();
        var persona = personas.Create(ownerId, "¬ера", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: PersonaSpecialty.Executor);

        var provider = Invoke(sut, ownerId, new Session(), persona);
        provider.Should().NotBeNull("провайдер собираетс€ Ч гейт по флагу внутри, на каждый ход");

        var text = await provider!(null);
        text.Should().BeNull("флаг specialty-prompt-sections выключен по умолчанию Ч как до фичи");
    }

    [Fact]
    public async Task ‘лаг¬ключЄн_“екст—екцийѕо–оли()
    {
        var (sut, personas, users, ownerId) = BuildSut();
        users.SetFeatureFlag(ownerId, FeatureFlagKeys.SpecialtyPromptSections, true).Should().BeTrue();
        var persona = personas.Create(ownerId, "√риша", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: PersonaSpecialty.Executor);

        var provider = Invoke(sut, ownerId, new Session(), persona);
        var text = await provider!(null);

        text.Should().NotBeNull();
        text.Should().Contain("dossier_lookup", "у исполнител€ секци€ Ђистори€ї включена по умолчанию");
    }

    [Fact]
    public async Task ‘лагѕереключаетс€Ќа аждый’од_Ѕезѕересборкијдаптера()
    {
        //  онтракт: гейт по флагу Ч ¬Ќ”“–» провайдера (как у dossier), не на его построении Ч
        // переключение флага должно действовать сразу, без пересоздани€ адаптера/сессии
        var (sut, personas, users, ownerId) = BuildSut();
        var persona = personas.Create(ownerId, "ƒаша", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: PersonaSpecialty.Executor);
        var provider = Invoke(sut, ownerId, new Session(), persona)!;

        (await provider(null)).Should().BeNull("флаг выключен на момент первого вызова");

        users.SetFeatureFlag(ownerId, FeatureFlagKeys.SpecialtyPromptSections, true);
        (await provider(null)).Should().NotBeNull("тот же провайдер Ч переключение флага подхватилось без пересборки");
    }
}
