using System.Net;
using ClaudeHomeServer.Services.Architecture;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.DynamicModules;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Проводка шва вклада статики (риск 5 плана viaduct-embed-plan): ветку /modules/viaduct
// Main ставит генерическим циклом по IStaticBranchContributor. Если вклад потеряется при
// переезде регистрации (подсистема → динамический модуль через ModuleLoader), сборка это
// не заметит, а редактор молча отдаст SPA-фолбэк. Сторож: вклад резолвится ровно один,
// и на хосте с MapFallback «не установлен» даёт 404 viaduct_not_installed, а не фолбэк.
public sealed class ArchitectureStaticBranchWiringTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ccs-arch-wiring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    // DataPath во временной папке: сборки Viaduct там нет — модуль «не установлен»
    private Dictionary<string, string?> BaseConfig() => new()
    {
        ["DataPath"] = Path.Combine(_dataDir, "projects.json"),
    };

    [Fact]
    public async Task Register_подсистемы__вклад_статики_ставит_ветку_до_фолбэка()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
        var services = new ServiceCollection();

        new ArchitectureSubsystem().Register(services, config);

        await AssertBranchWired(services, config);
    }

    // После 10.2 подсистему регистрирует ModuleLoader из dll modules/architecture
    // (копия CopyArchitectureModuleForTests) — вклад обязан пережить этот путь
    [Fact]
    public async Task Загрузка_через_ModuleLoader__вклад_статики_ставит_ветку_до_фолбэка()
    {
        var values = BaseConfig();
        values["DynamicModules:0:Key"] = "architecture";
        values["DynamicModules:0:Title"] = "Архитектура";
        values["DynamicModules:0:Version"] = "1.0.0";
        values["DynamicModules:0:Enabled"] = "true";
        values["DynamicModules:0:Backend:AssemblyPath"] = "modules/architecture/ClaudeHomeServer.Architecture.dll";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();

        var loaded = new ModuleLoader(new ModuleRegistry(config), config, NullLogger<ModuleLoader>.Instance)
            .LoadAll(services);

        loaded.Should().ContainSingle("dll модуля лежит в modules/architecture и обязана загрузиться")
            .Which.GetName().Name.Should().Be("ClaudeHomeServer.Architecture");
        await AssertBranchWired(services, config);
    }

    private static async Task AssertBranchWired(IServiceCollection registrations, IConfiguration config)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddConfiguration(config);
        foreach (var d in registrations) builder.Services.Add(d);
        await using var app = builder.Build();

        // Тот же генерический цикл, что в Program.cs, на хосте с SPA-фолбэком
        var contributors = app.Services.GetServices<IStaticBranchContributor>().ToList();
        contributors.Should().ContainSingle("вклад статики Viaduct — ровно один");
        foreach (var contributor in contributors) contributor.Configure(app);
        app.MapFallback(ctx => ctx.Response.WriteAsync("ccs-spa-fallback"));
        await app.StartAsync();

        var resp = await app.GetTestClient().GetAsync("/modules/viaduct/");
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain(ViaductStaticHosting.NotInstalledCode);
        body.Should().NotContain("ccs-spa-fallback");
    }
}
