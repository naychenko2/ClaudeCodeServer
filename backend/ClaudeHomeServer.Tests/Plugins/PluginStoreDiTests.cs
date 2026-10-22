using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Plugins;

// Итерация 2 (пункт 3): тест на НАСТОЯЩЕМ DI. Существующие тесты (PluginStoreChangedEventTests)
// передают ISessionBroadcaster явно в конструктор — они проверяют логику, но не то, что
// контейнер его инъектит. Здесь: сборка ServiceCollection → AddSingleton<PluginStore>()
// (как в PluginsSubsystem.Register) → резолв → вызов SaveInstalled → plugins_changed долетел.
// Если DI перестанет инъектировать (например, опциональный параметр станет null), тест краснеет.
public class PluginStoreDiTests : IDisposable
{
    private const string Owner = "owner-di";

    private readonly string _dir;
    private readonly RecordingBroadcaster _broadcaster;

    public PluginStoreDiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_pluginsdi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _broadcaster = new RecordingBroadcaster();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // Сборка контейнера: ISessionBroadcaster + PluginStore (как в PluginsSubsystem.Register).
    private ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionBroadcaster>(_broadcaster);
        services.AddSingleton<PluginStore>();
        services.AddSingleton<PluginSecretsStore>();
        services.AddSingleton<IServiceProvider>(sp => sp);
        // IConfiguration — PluginStore берёт DataPath отсюда
        services.AddSingleton(BuildConfig(_dir));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PluginStore_ResolvedFromDi_FiresPluginsChangedOnSaveInstalled()
    {
        var sp = BuildContainer();
        var store = sp.GetRequiredService<PluginStore>();

        // Запись установки → SaveInstalled → NotifyChanged → broadcaster.ToOwner
        var records = new List<PluginInstall>
        {
            new() { Id = "acme/test", Version = "1.0.0", Enabled = true },
        };
        store.SaveInstalled(Owner, records);

        // plugins_changed долетел владельцу
        _broadcaster.OwnerMessages.Should().ContainSingle()
            .Which.Should().BeOfType<PluginsChangedMessage>();
    }

    [Fact]
    public async Task PluginSecretsStore_ResolvedFromDi_FiresPluginsChangedOnSet()
    {
        var sp = BuildContainer();
        var secrets = sp.GetRequiredService<PluginSecretsStore>();

        secrets.Set(Owner, "acme/test", "api_key", "secret-value");

        _broadcaster.OwnerMessages.Should().ContainSingle()
            .Which.Should().BeOfType<PluginsChangedMessage>();
    }

    [Fact]
    public async Task PluginStore_WithoutBroadcaster_DoesNotThrow()
    {
        // Выключенная подсистема: ISessionBroadcaster не зарегистрирован → null.
        // Запись не падает, просто события нет.
        var services = new ServiceCollection();
        services.AddSingleton(BuildConfig(_dir));
        services.AddSingleton<PluginStore>();
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<PluginStore>();

        var records = new List<PluginInstall>
        {
            new() { Id = "acme/test", Version = "1.0.0", Enabled = true },
        };
        store.Invoking(s => s.SaveInstalled(Owner, records)).Should().NotThrow();
    }

    // Фейк: собирает ToOwner-сообщения.
    private sealed class RecordingBroadcaster : ISessionBroadcaster
    {
        public List<ServerMessage> OwnerMessages { get; } = [];
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) { OwnerMessages.Add(message); return Task.CompletedTask; }
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
