using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Гейт подсистемы Plugins (ADR-014, ADR-031): при `Subsystems:plugins:Enabled=false`
// `PluginsSubsystem.Register` не вызывается, и `PluginStore`/`IPluginDirSource` в
// контейнер не попадают. Правило пилота — «обязательный параметр конструктора от
// отключаемой подсистемы — дефект». В шаге 1.2 потребителя `IPluginDirSource` вне
// вертикали ещё нет (ClaudeSession/Llm подключит его опционально в своём шаге),
// поэтому инвариант «нет обязательного параметра от Plugins» держится по построению:
// здесь проверяется сама структурная часть гейта — регистрация/отсутствие по флагу.
//
// НЕ через TestWebApplicationFactory: как в `NotesSubsystemGateTests`, флаг читается в
// `AddSubsystems` до `ConfigureWebHost`, поэтому точечный `ServiceCollection` + реальный
// `AddSubsystems` — самый короткий честный путь (детерминирован, не зависит от
// параллелизма).
public class PluginsSubsystemGateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plugins_RegisteredOnlyWhenSubsystemEnabled(bool enabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:plugins:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSubsystems(config, new PluginsSubsystem());

        // Оба сервиса регистрируются в PluginsSubsystem.Register: гейт структурный —
        // Register не вызван, и ни одного дескриптора от вертикали в коллекции нет.
        services.Any(d => d.ServiceType == typeof(PluginStore)).Should().Be(enabled,
            $"PluginStore регистрируется в PluginsSubsystem.Register (Enabled={enabled})");
        services.Any(d => d.ServiceType == typeof(IPluginDirSource)).Should().Be(enabled,
            $"IPluginDirSource регистрируется в PluginsSubsystem.Register (Enabled={enabled})");

        if (!enabled)
        {
            // Ключевой инвариант: при выключенной подсистеме перечисление шва не только
            // пусто, но и НЕ БРОСАЕТ — сироты-форвардера на PluginStore в коллекции не
            // остаётся.
            using var sp = services.BuildServiceProvider();
            var act = () => sp.GetServices<IPluginDirSource>().ToList();
            act.Should().NotThrow();
            act().Should().BeEmpty();
        }
    }
}
