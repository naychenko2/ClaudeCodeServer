using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Plugins;

// Выключатель наблюдателя черновиков (QA-фикс U): Plugins:DraftWatcher:Enabled (дефолт true).
// false — наблюдатель не регистрируется как hosted-сервис вовсе (нет inotify-экземпляра,
// тестовые хосты не искажают процессные замеры FileWatcherSubscriptionTests); true / нет ключа —
// регистрируется (боевое поведение не меняется).
//
// Проверка — по сервисному дескриптору (ServiceType=IHostedService,
// ImplementationType=PluginDraftWatcher), без резолва зависимостей (PluginStore,
// ISessionBroadcaster) и без старта хоста: AddHostedService<T> кладёт ровно такой дескриптор.
public class PluginDraftWatcherRegistrationTests
{
    // Есть ли в коллекции дескриптор hosted-сервиса PluginDraftWatcher.
    private static bool IsWatcherRegistered(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(PluginDraftWatcher));

    // Контейнер как в PluginsSubsystemDisabledTests: реальный AddSubsystems (подсистема включена)
    // + IConfiguration/IFeatureFlagGate (спина). enabled: null — ключ в конфиге отсутствует.
    private static ServiceCollection BuildServices(bool? enabled)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Subsystems:plugins:Enabled"] = "true",
        };
        if (enabled is not null)
            dict["Plugins:DraftWatcher:Enabled"] = enabled.Value ? "true" : "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IFeatureFlagGate>(new StubFlagGate());
        services.AddSubsystems(config, new PluginsSubsystem());
        return services;
    }

    [Fact]
    public void EnabledTrue_НаблюдательРегистрируется()
    {
        IsWatcherRegistered(BuildServices(enabled: true)).Should().BeTrue(
            "Plugins:DraftWatcher:Enabled=true — hosted-сервис на месте");
    }

    [Fact]
    public void EnabledFalse_НаблюдательНеРегистрируется()
    {
        IsWatcherRegistered(BuildServices(enabled: false)).Should().BeFalse(
            "Plugins:DraftWatcher:Enabled=false — hosted-сервиса нет, inotify не создаётся");
    }

    // Мутация: поменять дефолт PluginDraftWatcherOptions.Enabled на false → этот тест красный
    // (без ключа наблюдатель перестаёт регистрироваться, хотя боевое поведение — true).
    [Fact]
    public void ДефолтБезКлюча_НаблюдательРегистрируется()
    {
        IsWatcherRegistered(BuildServices(enabled: null)).Should().BeTrue(
            "нет ключа в конфиге — дефолт true, боевое поведение не меняется");
    }

    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => true;
    }
}
