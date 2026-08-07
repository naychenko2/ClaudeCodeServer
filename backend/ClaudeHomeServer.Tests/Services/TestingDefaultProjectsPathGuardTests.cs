using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Финальная проверка: тестовый хост (WebApplicationFactory БЕЗ override DefaultProjectsPath)
// стартует с appsettings.Testing.json — пустой путь должен дойти до AppSettingsService.
// UserHomeResolver тогда вернёт null для любого пользователя, а не уведёт его в прод.
public class TestingDefaultProjectsPathGuardTests
{
    private sealed class NoOverrideFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }
    }

    [Fact]
    public void ТестовыйХост_БезOverride_DefaultProjectsPath_Пустой()
    {
        using var factory = new NoOverrideFactory();
        using var scope = factory.Services.CreateScope();
        var appSettings = scope.ServiceProvider.GetRequiredService<AppSettingsService>();

        var settings = appSettings.Get();

        settings.DefaultProjectsPath.Should().BeNullOrEmpty(
            "тестовый хост без override TestWebApplicationFactory должен получить пустой "
            + "DefaultProjectsPath из appsettings.Testing.json — иначе проект уйдёт в /projects "
            + "(контейнерный путь прода, маппится на C:\\ClaudeHome)");
    }
}
