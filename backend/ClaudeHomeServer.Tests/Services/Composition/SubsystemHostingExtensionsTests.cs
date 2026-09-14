using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Services.Composition;

// Сторож семантики `AddGatedHostedService`/`AddGatedHostedFrom`/`ResolveDataDir` и
// пост-билд фазы `UseSubsystems`. Семантика hosted-гейта — точная копия хелпера
// `AddHosted` из Program.cs (до того, как его перенесли в Composition): в Testing
// без явного `Testing:EnableHostedServices=true` hosted НЕ регистрируется, иначе — да.
// Эти тесты — негативная проверка №1 и №2 из задачи «Контракт подсистем v1.1».
public class SubsystemHostingExtensionsTests
{
    private static IConfiguration BuildConfig(params (string Key, string Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => (string?)p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // Семанический носитель: один тип — один hosted-сервис, без зависимостей,
    // чтобы тесты проверяли именно регистрацию, а не резолв.
    private sealed class NoopHosted : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopHostedOther : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // Негативная проверка №1 (точная копия семантики Program.cs).
    [Fact]
    public void AddGatedHostedService_РегистрируетHosted_ВнеTesting()
    {
        var services = new ServiceCollection();
        // ASPNETCORE_ENVIRONMENT не задан => не Testing => гейт открыт.
        var config = BuildConfig();

        services.AddGatedHostedService<NoopHosted>(config);

        services.Should().Contain(s =>
            s.ServiceType == typeof(IHostedService)
            && s.ImplementationType == typeof(NoopHosted));
    }

    [Fact]
    public void AddGatedHostedService_НеРегистрируетHosted_ВTestingБезКлюча()
    {
        var services = new ServiceCollection();
        // В Testing без явного Testing:EnableHostedServices=true hosted НЕ регистрируется —
        // точная копия семантики хелпера AddHosted из Program.cs:121-130. Поведение,
        // может, неудобное, но менять его в этой задаче нельзя: «копировать 1-в-1».
        var config = BuildConfig(("ASPNETCORE_ENVIRONMENT", "Testing"));

        services.AddGatedHostedService<NoopHosted>(config);

        services.Should().NotContain(s => s.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddGatedHostedService_НеРегистрируетHosted_ВTestingСКлючомFalse()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(
            ("ASPNETCORE_ENVIRONMENT", "Testing"),
            ("Testing:EnableHostedServices", "false"));

        services.AddGatedHostedService<NoopHosted>(config);

        services.Should().NotContain(s => s.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddGatedHostedService_РегистрируетHosted_ВTestingСКлючомTrue()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(
            ("ASPNETCORE_ENVIRONMENT", "Testing"),
            ("Testing:EnableHostedServices", "true"));

        services.AddGatedHostedService<NoopHosted>(config);

        services.Should().Contain(s =>
            s.ServiceType == typeof(IHostedService)
            && s.ImplementationType == typeof(NoopHosted));
    }

    [Fact]
    public void AddGatedHostedFrom_РегистрируетHostedЧерезФабрику()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();
        var factoryCalls = 0;

        services.AddGatedHostedFrom<NoopHosted>(config, _ =>
        {
            factoryCalls++;
            return new NoopHosted();
        });

        services.Should().Contain(s =>
            s.ServiceType == typeof(IHostedService) && s.ImplementationFactory != null);

        // Фабрика НЕ зовётся на регистрации — hosted-инстанс создаётся при старте.
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public void AddGatedHostedFrom_НеРегистрируетHosted_ВTesting()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(("ASPNETCORE_ENVIRONMENT", "Testing"));

        services.AddGatedHostedFrom<NoopHosted>(config, _ => new NoopHosted());

        services.Should().NotContain(s => s.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddGatedHostedService_ВозвращаетТуЖеКоллекцию()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        // Контракт совместимости с Microsoft.Extensions.DependencyInjection: возврат
        // той же коллекции разрешает композицию `services.AddGatedHostedService<...>(...).Add...`.
        var returned = services.AddGatedHostedService<NoopHosted>(config);

        Assert.Same(services, returned);
    }

    [Fact]
    public void ResolveDataDir_КореньОтDataPath()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "wt-resolve-data-dir-test", "projects.json");
        var config = BuildConfig(("DataPath", dataFile));

        var dir = SubsystemHostingExtensions.ResolveDataDir(config);

        // DirectoryName(FullPath(DataPath)) — каталог, в котором лежит projects.json.
        Path.GetFullPath(dir).Should().Be(Path.GetFullPath(Path.GetDirectoryName(dataFile)!));
    }

    [Fact]
    public void ResolveDataDir_ПриклеиваетSubfolders()
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "wt-resolve-data-dir-test", "projects.json");
        var config = BuildConfig(("DataPath", dataFile));

        var dir = SubsystemHostingExtensions.ResolveDataDir(config, "code-graphs", "abc");

        var expected = Path.Combine(
            Path.GetDirectoryName(dataFile)!, "code-graphs", "abc");
        Path.GetFullPath(dir).Should().Be(Path.GetFullPath(expected));
    }

    [Fact]
    public void ResolveDataDir_БезDataPathИспользуетДефолт()
    {
        // DataPath не задан => дефолт AppContext.BaseDirectory/data/projects.json,
        // корень — его директория (т.е. AppContext.BaseDirectory/data).
        var config = BuildConfig();

        var dir = SubsystemHostingExtensions.ResolveDataDir(config);

        var expectedDefault = Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        Path.GetFullPath(dir).Should().Be(
            Path.GetFullPath(Path.GetDirectoryName(expectedDefault)!));
    }

    // Защита от тихого отказа: после регистрации одного hosted другой hosted того же
    // гейта не должен «затереть» первый. Если бы хелпер был кривой (например, всегда
    // возвращал новую коллекцию), тест бы это поймал на композиции.
    [Fact]
    public void AddGatedHostedService_НесколькоВызововРегистрируютНесколькоHosted()
    {
        var services = new ServiceCollection();
        var config = BuildConfig();

        services.AddGatedHostedService<NoopHosted>(config);
        services.AddGatedHostedService<NoopHostedOther>(config);

        services.Where(s => s.ServiceType == typeof(IHostedService)).Should().HaveCount(2);
    }

    // Негативная проверка №2: подсистема с IAppPhaseSubsystem получает вызов
    // ConfigureApp, подсистема без него — не падает.
    private sealed class StubSubsystem : IAppSubsystem
    {
        public string Key => "stub";
        public string Title => "Stub";
        public int RegisterCalls { get; private set; }
        public void Register(IServiceCollection services, IConfiguration config) => RegisterCalls++;
    }

    private sealed class StubAppPhaseSubsystem : IAppPhaseSubsystem
    {
        public string Key => "stub-phase";
        public string Title => "Stub Phase";
        public int RegisterCalls { get; private set; }
        public int ConfigureAppCalls { get; private set; }
        public WebApplication? LastApp { get; private set; }

        public void Register(IServiceCollection services, IConfiguration config) => RegisterCalls++;

        public void ConfigureApp(WebApplication app)
        {
            ConfigureAppCalls++;
            LastApp = app;
        }
    }

    [Fact]
    public void UseSubsystems_ЗовётConfigureApp_УIAppPhaseSubsystem()
    {
        var phase = new StubAppPhaseSubsystem();
        var plain = new StubSubsystem();
        var builder = WebApplication.CreateBuilder(args: []);
        builder.Services.AddLogging();
        builder.Services.AddSubsystems(builder.Configuration, plain, phase);
        using var app = builder.Build();

        app.UseSubsystems();

        phase.ConfigureAppCalls.Should().Be(1,
            "UseSubsystems обязан звать ConfigureApp у IAppPhaseSubsystem ровно один раз");
        phase.LastApp.Should().BeSameAs(app,
            "фаза получает тот же WebApplication, который передали в UseSubsystems");
        plain.RegisterCalls.Should().Be(1,
            "Register отрабатывает всегда — он не часть фазы");
    }

    [Fact]
    public void UseSubsystems_НеПадаетНаПодсистемеБезIAppPhase()
    {
        var plain = new StubSubsystem();
        var builder = WebApplication.CreateBuilder(args: []);
        builder.Services.AddLogging();
        builder.Services.AddSubsystems(builder.Configuration, plain);
        using var app = builder.Build();

        var act = () => app.UseSubsystems();
        act.Should().NotThrow("подсистема без IAppPhaseSubsystem — обычный случай, не ошибка");
        plain.RegisterCalls.Should().Be(1);
    }

    [Fact]
    public void UseSubsystems_НеПадаетБезПодсистем()
    {
        var builder = WebApplication.CreateBuilder(args: []);
        builder.Services.AddLogging();
        using var app = builder.Build();

        var act = () => app.UseSubsystems();
        act.Should().NotThrow("пустой контейнер подсистем — штатное состояние продукта без подсистем");
    }

    [Fact]
    public void UseSubsystems_ЗовётConfigureAppВПорядкеРегистрации()
    {
        var callOrder = new List<string>();
        var builder = WebApplication.CreateBuilder(args: []);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAppSubsystem>(new OrderProbe("first", callOrder));
        builder.Services.AddSingleton<IAppSubsystem>(new OrderProbe("second", callOrder));
        builder.Services.AddSingleton<IAppSubsystem>(new OrderProbe("third", callOrder));
        using var app = builder.Build();

        app.UseSubsystems();

        callOrder.Should().Equal("first", "second", "third");
    }

    private sealed class OrderProbe : IAppPhaseSubsystem
    {
        private readonly string _label;
        private readonly List<string> _log;

        public OrderProbe(string label, List<string> log)
        {
            _label = label;
            _log = log;
        }

        public string Key => $"probe-{_label}";
        public string Title => $"Probe {_label}";
        public void Register(IServiceCollection services, IConfiguration config) { }
        public void ConfigureApp(WebApplication app) => _log.Add(_label);
    }

    [Fact]
    public void AddSubsystems_РегистрируетИнстансКакIAppSubsystem()
    {
        // Инвариант: после AddSubsystems подсистема доступна через `IEnumerable<IAppSubsystem>`,
        // иначе `UseSubsystems` после Build не сможет её найти.
        var services = new ServiceCollection();
        services.AddLogging();
        var config = BuildConfig();
        var a = new StubSubsystem();
        var b = new StubAppPhaseSubsystem();

        services.AddSubsystems(config, a, b);

        using var sp = services.BuildServiceProvider();
        var list = sp.GetServices<IAppSubsystem>().ToList();

        list.Should().HaveCount(2);
        list.Select(s => s.Key).Should().BeEquivalentTo(new[] { "stub", "stub-phase" });
    }
}
