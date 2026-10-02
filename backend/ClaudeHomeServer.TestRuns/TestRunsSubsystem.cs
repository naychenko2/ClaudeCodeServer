using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.TestRuns;

// Подсистема прогона тестов (инструмент run_tests, docs/research/test-progress-2026-10.md):
// движок запуска `dotnet test` через среду проекта с общим потолком тяжёлых сборок. Тулсет
// MCP-сервера `tests` живёт в Main и зависит от движка опционально: выключенная подсистема
// не регистрирует TestRunService, и инструмент честно отказывает текстом.
public sealed class TestRunsSubsystem : IAppSubsystem
{
    public const string SubsystemKey = "test-runs";

    public string Key => SubsystemKey;

    public string Title => "Прогон тестов";

    public string Description =>
        "Инструмент агента run_tests: dotnet test в среде проекта с очередью сборок и остановкой по «Стоп».";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TestRunsOptions.From(config));
        services.AddSingleton<TestRunService>();
        // Подсказка «тесты — через run_tests»: выключенная подсистема не объявляет и секцию
        services.AddPromptSectionContributor<TestRunsHintContributor>();
    }
}
