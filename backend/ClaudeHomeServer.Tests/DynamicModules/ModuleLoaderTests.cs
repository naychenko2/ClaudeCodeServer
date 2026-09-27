using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.DynamicModules;

namespace ClaudeHomeServer.Tests.DynamicModules;

// Юнит-проверка механики сценария Б (без поднятия веб-хоста, детерминированно):
// 1) ModuleRegistry читает массив ModuleDescriptor из секции "DynamicModules" конфига;
// 2) ModuleLoader грузит сборку по Backend.AssemblyPath, находит IAppSubsystem и зовёт Register;
// 3) Register реально отрабатывает — подсистема появляется в DI по Key="__stub".
// Путь dll — относительный modules/__stub/StubModule.dll, его Target CopyStubModuleForTests
// кладёт рядом с test-host (AppContext.BaseDirectory), как и Loader в проде.
public class ModuleLoaderTests
{
    private static IConfiguration StubConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DynamicModules:0:Key"] = "__stub",
            ["DynamicModules:0:Title"] = "Test Stub Module",
            ["DynamicModules:0:Version"] = "0.0.1",
            ["DynamicModules:0:Enabled"] = "true",
            // относительный путь — от базового каталога процесса (AppContext.BaseDirectory);
            // загрузчик берёт именно Backend.AssemblyPath (M2: единый манифест)
            ["DynamicModules:0:Backend:AssemblyPath"] = "modules/__stub/StubModule.dll",
        }).Build();

    [Fact]
    public void Registry_ЧитаетМанифестИзСекцииDynamicModules()
    {
        var registry = new ModuleRegistry(StubConfig());

        registry.All.Should().HaveCount(1);
        registry.All[0].Key.Should().Be("__stub");
        registry.All[0].Enabled.Should().BeTrue();
        // M2: бинд единого манифеста — Backend.AssemblyPath, а не плоский AssemblyPath;
        // Frontend необязателен (в заглушке его нет → null).
        registry.All[0].Backend.Should().NotBeNull("Backend обязателен для загрузчика");
        registry.All[0].Backend!.AssemblyPath.Should().Be("modules/__stub/StubModule.dll");
        registry.All[0].Frontend.Should().BeNull("Frontend необязателен");
    }

    [Fact]
    public void LoadAll_ГрузитСборку_ВызываетRegister_ИПодсистемаРезолвится()
    {
        var registry = new ModuleRegistry(StubConfig());
        var loader = new ModuleLoader(registry, StubConfig(), NullLogger<ModuleLoader>.Instance);
        var services = new ServiceCollection();

        var loaded = loader.LoadAll(services);

        // Сборка загружена и это именно StubModule с контроллером.
        loaded.Should().HaveCount(1);
        loaded[0].GetName().Name.Should().Be("StubModule");
        loaded[0].GetTypes().Any(t => t.Name == "StubController").Should().BeTrue(
            "в загруженной сборке есть контроллер заглушки (его подключит AddApplicationPart)");

        // Register вызван: подсистема зарегистрирована под IAppSubsystem и резолвится.
        using var provider = services.BuildServiceProvider();
        var subsystems = provider.GetServices<IAppSubsystem>().ToList();
        subsystems.Should().Contain(s => s.Key == "__stub",
            "StubSubsystem.Register должен зарегистрировать себя под IAppSubsystem");
    }

    [Fact]
    public void DisabledМодуль_НеЗагружается()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DynamicModules:0:Key"] = "__stub",
            ["DynamicModules:0:Backend:AssemblyPath"] = "modules/__stub/StubModule.dll",
            ["DynamicModules:0:Enabled"] = "false",
        }).Build();

        var registry = new ModuleRegistry(config);
        var loader = new ModuleLoader(registry, config, NullLogger<ModuleLoader>.Instance);

        var loaded = loader.LoadAll(new ServiceCollection());
        loaded.Should().BeEmpty("Enabled=false — загрузчик модуль пропускает");
    }

    // Гейт отключаемости Subsystems:{Key}:Enabled=false (ADR-014): запись модуля Enabled=true,
    // но Register не вызывается, сборка наружу не отдаётся (Program.cs не подключит её
    // ApplicationPart), а в стор подсистем модуль уходит неактивным.
    [Fact]
    public void ГейтПодсистемыВыключен_RegisterНеЗовётся_СторПишетDisabled()
    {
        var config = new ConfigurationBuilder()
            .AddConfiguration(StubConfig())
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Subsystems:__stub:Enabled"] = "false" })
            .Build();
        var states = new SubsystemStateStore();
        var loader = new ModuleLoader(new ModuleRegistry(config), config, NullLogger<ModuleLoader>.Instance, states);
        var services = new ServiceCollection();

        loader.LoadAll(services).Should().BeEmpty("выключенный гейтом модуль наружу не отдаётся");
        services.Should().NotContain(d => d.ServiceType == typeof(IAppSubsystem), "Register не вызван");
        var snapshot = states.Snapshot(config).Single(s => s.Key == "__stub");
        snapshot.Active.Should().BeFalse();
        snapshot.RestartRequired.Should().BeFalse();
    }

    // Раздача MF-remote /{key}-remote (цикл в Program.cs) идёт по ServedRemotes: только
    // загруженные модули. Интеграционно на хосте не проверить честно — в Testing-хосте нет
    // wwwroot рядом с test-host (весь блок раздачи фронта там не ставится), а подложить его
    // в общий bin значило бы включить SPA-фолбэк всем параллельным хостам прогона.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServedRemotes_ТолькоЗагруженныеМодулиСFrontend(bool gateEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddConfiguration(StubConfig())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DynamicModules:0:Frontend:RemoteUrl"] = "/__stub-remote/remoteEntry.js",
                ["DynamicModules:0:Frontend:ExposedModule"] = "./subsystem",
                ["Subsystems:__stub:Enabled"] = gateEnabled ? "true" : "false",
            })
            .Build();
        var registry = new ModuleRegistry(config);
        var states = new SubsystemStateStore();
        var loaded = new ModuleLoader(registry, config, NullLogger<ModuleLoader>.Instance, states)
            .LoadAll(new ServiceCollection());
        // Как Program.cs: загруженную сборку записываем в стор активной
        foreach (var assembly in loaded)
        {
            var implType = assembly.GetTypes().First(t => !t.IsAbstract && typeof(IAppSubsystem).IsAssignableFrom(t));
            states.RecordActive((IAppSubsystem)Activator.CreateInstance(implType)!);
        }

        registry.All.Single().Frontend.Should().NotBeNull("запись с Frontend и Enabled=true в обоих случаях");
        var served = registry.ServedRemotes(states).Select(m => m.Key);
        if (gateEnabled)
            served.Should().Equal("__stub");
        else
            served.Should().BeEmpty("гейт выключен — модуль не загружен, remote не раздаётся");
    }
}
