using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Провижн авто-ассистента (DefaultAssistantProvisioner, фича default-personas-onboarding).
// Проверяет контракт из плана §2: идемпотентность, идемпотентность под гонкой, профиль
// Coordinator/Full/personas-manage, поведение при выключенном флаге и обрыве между
// созданием и досевом привязок.
public class DefaultAssistantProvisionerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _bindings;
    private readonly FeatureFlagService _flags;
    private readonly Mock<IHubContext<SessionHub>> _hub;
    private readonly List<ServerMessage> _broadcasts;
    private readonly DefaultAssistantProvisioner _sut;
    private readonly string _userId;

    public DefaultAssistantProvisionerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_provisioner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userId = _users.GetFirst()!.Id; // дефолтный admin пустого стора

        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, _users, appSettings);
        _personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var mcp = new ClaudeHomeServer.Services.Mcp.McpRegistry(config,
            new ClaudeHomeServer.Services.Mcp.McpSecretStore(config));
        _bindings = new PersonaBindingsService(_personas, projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _users, config,
            NullLogger<PersonaBindingsService>.Instance, mcp);
        _flags = new FeatureFlagService(_users);

        // По умолчанию флаг включён (большинство кейсов) — отдельный тест его выключает.
        _users.SetFeatureFlag(_userId, FeatureFlagKeys.DefaultPersonasOnboarding, true);

        // Перехват broadcast-сообщений в группу (паттерн GlifCostPipelineTests).
        _broadcasts = [];
        _hub = new Mock<IHubContext<SessionHub>>();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(c => c.SendCoreAsync("message", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                if (args.Length > 0 && args[0] is ServerMessage m) _broadcasts.Add(m);
            })
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        _hub.Setup(h => h.Clients).Returns(clients.Object);

        _sut = new DefaultAssistantProvisioner(_users, _personas, _bindings, _flags,
            _hub.Object, NullLogger<DefaultAssistantProvisioner>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task EnsureAsync_СоздаётЗаготовку_ОдинРаз()
    {
        var persona = await _sut.EnsureAsync(_userId);

        persona.Should().NotBeNull();
        persona!.Name.Should().Be("Ассистент");
        persona.Role.Should().Be("Личный помощник");
        persona.Specialty.Should().Be(PersonaSpecialty.Coordinator);
        persona.Access.Should().Be(PersonaAccess.Full);
        _personas.GetByOwner(_userId).Should().HaveCount(1, "заготовка создаётся ровно одна");

        // Broadcast строго по факту создания: created (стор персон) + default (перечитывание /me)
        _broadcasts.OfType<PersonasChangedMessage>()
            .Select(m => m.Action)
            .Should().Contain(new[] { "created", "default" });
    }

    [Fact]
    public async Task EnsureAsync_Повтор_ВозвращаетТуЖеПерсону_БезДубля()
    {
        var first = await _sut.EnsureAsync(_userId);
        var second = await _sut.EnsureAsync(_userId);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.Id.Should().Be(first!.Id, "повторный вызов не плодит вторую персону");
        _personas.GetByOwner(_userId).Should().HaveCount(1);

        // При возврате существующей персоны broadcast не шлётся — событие только по факту создания.
        _broadcasts.OfType<PersonasChangedMessage>()
            .Count(m => m.Action == "created")
            .Should().Be(1);
    }

    [Fact]
    public async Task EnsureAsync_ПятьПараллельных_СоздаётОднуПерсону()
    {
        var results = await Task.WhenAll(Enumerable.Repeat(0, 5)
            .Select(_ => _sut.EnsureAsync(_userId)));

        results.Should().NotContainNulls();
        results.Select(p => p!.Id).Distinct().Should().ContainSingle("конкурентные вызовы дают одну персону");
        _personas.GetByOwner(_userId).Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureAsync_ОбрывМеждуСозданиемИДосевом_НеПлодитДубль()
    {
        // Имитация обрыва: Create отработал, Default+Assistant проставлены, а досев привязок
        // не успел (упал). Перечитываем состояние и убеждаемся, что повторный Ensure возвращает
        // ту же персону, а не создаёт вторую — DefaultPersonaId уже резолвится в живую.
        var stub = _personas.Create(_userId, "Ассистент", "Личный помощник", null, null,
            null, null, PersonaScope.Global, null, "orange", null, true,
            access: PersonaAccess.Full, specialty: PersonaSpecialty.Coordinator);
        _users.SetDefaultPersona(_userId, stub.Id);
        _users.SetAssistantPersona(_userId, stub.Id);
        // SeedDefaultPersonaProfile НЕ вызывали — профиль недосеян (без привязок)

        var returned = await _sut.EnsureAsync(_userId);

        returned.Should().NotBeNull();
        returned!.Id.Should().Be(stub.Id, "обрыв не должен плодить дубль — возвращается существующая");
        _personas.GetByOwner(_userId).Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureAsync_ФлагВыключен_ВозвращаетNull()
    {
        _users.SetFeatureFlag(_userId, FeatureFlagKeys.DefaultPersonasOnboarding, false);

        var persona = await _sut.EnsureAsync(_userId);

        persona.Should().BeNull("при выключенном флаге провижн невозможен");
        _personas.GetByOwner(_userId).Should().BeEmpty();
        _broadcasts.Should().BeEmpty("без создания нет и broadcast");
    }

    [Fact]
    public async Task EnsureAsync_Профиль_СодержитCoordinatorFullИПривязкуManage()
    {
        var persona = await _sut.EnsureAsync(_userId);

        persona.Should().NotBeNull();
        persona!.Specialty.Should().Be(PersonaSpecialty.Coordinator);
        persona.Access.Should().Be(PersonaAccess.Full);
        persona.Bindings.Should().NotBeNull();
        persona.Bindings!.Should().Contain(b =>
            b.Type == PersonaBindingType.Tool && b.Target == "personas-manage",
            "координатору положены привязки управления персонами");
    }

    [Fact]
    public async Task EnsureAsync_ПослеСоздания_AssistantPersonaIdРавенDefaultPersonaId()
    {
        var persona = await _sut.EnsureAsync(_userId);

        var me = _users.GetById(_userId);
        me.Should().NotBeNull();
        me!.DefaultPersonaId.Should().Be(persona!.Id);
        me.AssistantPersonaId.Should().Be(persona.Id,
            "заготовка фиксирована как дефолт — метка горит, пока оба поля совпадают");
    }
}
