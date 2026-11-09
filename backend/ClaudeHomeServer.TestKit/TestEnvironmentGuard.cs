using System.Runtime.CompilerServices;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Переводит весь тестовый процесс в среду "Testing".
///
/// Зачем: Program.cs по этой среде НЕ подключает appsettings.Local.json разработчика —
/// иначе боевые токены ClaudeSubscriptions, Dify:ApiKey и ключи LlmProviders протекают
/// в тестовые хосты, а SubscriptionUsageWarmupService (WarmupOnStartup, дефолт true)
/// запускает настоящие claude.exe «ping»-ходы с боевым OAuth-токеном (~50 обращений
/// к Anthropic на каждый dotnet test, жгут квоту подписок).
///
/// Почему переменная окружения, а не builder.UseEnvironment("Testing") в фабрике:
/// WebApplicationFactory применяет ConfigureWebHost ОТЛОЖЕННО — Program.cs к этому
/// моменту уже исполнился и прочитал builder.Environment (известная грабля minimal
/// hosting, dotnet/aspnetcore#37680). Переменная читается WebApplication.CreateBuilder
/// при создании билдера — раньше любой строчки Program.cs. Фабрика дополнительно зовёт
/// UseEnvironment("Testing"), чтобы IHostEnvironment внутри приложения совпадал.
///
/// Почему ModuleInitializer: срабатывает при загрузке сборки — раньше любого теста
/// и любого тестового хоста (тот же паттерн, что TestTelemetryGuard).
/// </summary>
internal static class TestEnvironmentGuard
{
    [ModuleInitializer]
    internal static void SetTestingEnvironment()
    {
        // DOTNET_ENVIRONMENT сильнее ASPNETCORE_ENVIRONMENT у generic host — ставим обе,
        // чтобы среда была "Testing" независимо от того, какая из них задана на машине
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
    }
}
