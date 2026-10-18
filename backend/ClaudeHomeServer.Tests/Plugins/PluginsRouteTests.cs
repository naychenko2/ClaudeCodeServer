using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Plugins;

// M1: {id} в маршрутах работает с publisher/slug. URL несёт один сегмент publisher~slug
// ('~' вместо '/'), экшен декодирует. Тест — через TestServer с реальным HTTP:
//   200 для local-test/pkg, 404 для несуществующего.
public class PluginsRouteTests : IAsyncLifetime
{
    private const string Owner = "owner-a";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PluginStore _store = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_route_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "true",
            })
            .Build();

        _store = new PluginStore(config);
        var secrets = new PluginSecretsStore(config);
        var validator = new PluginPackageValidator();
        var installer = new PluginInstaller(_store, validator);
        var uninstall = new PluginUninstall(_store, secrets, new StubCascade());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // IConfiguration контроллера — наш (DataPath + гейт); дефолт гейта true.
        builder.Services.AddSingleton<IConfiguration>(config);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(secrets);
        builder.Services.AddSingleton(installer);
        builder.Services.AddSingleton(uninstall);
        builder.Services.AddSingleton<IFeatureFlagGate>(new StubFlagGate());
        // Контроллер — в отдельной сборке: добавляем её ApplicationPart явно (детерминированно).
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
            parts.ApplicationParts.Add(new AssemblyPart(typeof(PluginsController).Assembly)));

        _app = builder.Build();
        // User (sub) ставим до UseAuthorization: [Authorize] проверяет HttpContext.User.
        _app.Use((ctx, next) =>
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", Owner)], "test"));
            return next();
        });
        _app.UseRouting();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Установить пакет local-test/pkg (через installer, как в реальном install).
    private void InstallPackage()
    {
        var root = _store.DraftDir(Owner, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# A\n");
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "local-test/pkg", name = "pkg", version = "1.0.0",
                contributes = new { skills = new[] { "skills/a" } },
                permissions = new { requested = new[] { "notes:read" } },
            }, new JsonSerializerOptions { WriteIndented = true }));
        new PluginInstaller(_store, new PluginPackageValidator()).Install(Owner, "draft:pkg")
            .Status.Should().Be(InstallStatus.Ok);
    }

    [Fact]
    public async Task Route_СуществующийПакет_Отдаёт200()
    {
        InstallPackage();

        // id = local-test/pkg → в URL: local-test~pkg (один сегмент).
        var resp = await _client.GetAsync("/api/plugins/local-test~pkg/permissions");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Route_НесуществующийПакет_Отдаёт404()
    {
        InstallPackage();

        var resp = await _client.GetAsync("/api/plugins/ghost~missing/permissions");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    // IFeatureFlagGate: флаг plugins-mvp вкл (тест не про гейты, а про маршрутизацию).
    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) =>
            key != FeatureFlagKeys.PluginsMvp || true;
    }

    // IPluginInstallCascade: no-op.
    private sealed class StubCascade : IPluginInstallCascade
    {
        public void OnUninstalled(string ownerId, string pluginId) { }
    }
}
