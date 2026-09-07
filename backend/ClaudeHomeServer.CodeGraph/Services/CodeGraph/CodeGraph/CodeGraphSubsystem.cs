using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.CodeGraph;

// Подсистема «Граф кода»: DI-регистрации + пост-билд регистрация языковых
// провайдеров (первое применение IAppPhaseSubsystem).
//
// MCP-тулсет CodeGraphToolset НЕ здесь — он часть спины Services/Mcp/Http
// и регистрируется в Program.cs вместе с остальными тулсетами ADR-012.
public sealed class CodeGraphSubsystem : IAppPhaseSubsystem
{
    public string Key => "code-graph";

    public string Title => "Граф кода";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // CodeGraph: граф зависимостей кода (узлы — типы, рёбра — Calls/Implements/References)
        // GraphPersistence требует dataDir из IConfiguration — ленивый factory, чтобы test-in-memory
        // (DataPath из TestWebApplicationFactory) тоже применялся, как у ProjectManager.
        services.AddSingleton(sp => new GraphPersistence(
            Path.GetDirectoryName(Path.GetFullPath(
                sp.GetRequiredService<IConfiguration>()["DataPath"]
                    ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!,
            sp.GetRequiredService<ILogger<GraphPersistence>>()));
        services.AddSingleton<CodeGraphService>();
        // Per-ход slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A)
        services.AddSingleton<CodeGraphPromptProvider>();
        // Тонкие запросы к графу (find/neighbors/hubs) — за ними MCP-сервер codegraph
        services.AddSingleton<CodeGraphQueryService>();
    }

    public void ConfigureApp(WebApplication app)
    {
        // Регистрация языковых провайдеров CodeGraph (C# для .cs; TS/React для .ts/.tsx)
        // Блок переезжал из-под `if (!inspectionMode)` в Program.cs — гейт обязан сохраниться,
        // иначе инспекционная копия начнёт регистрировать провайдеры (см. комментарий у блока в Program.cs).
        if (app.Configuration.GetValue<bool>("InspectionMode")) return;
        try
        {
            var codeGraphService = app.Services.GetRequiredService<CodeGraphService>();
            var csProvider = new CSharpGraphProvider(
                app.Services.GetRequiredService<ILogger<CSharpGraphProvider>>());
            codeGraphService.RegisterProvider(".cs", csProvider);
            Console.WriteLine("[CodeGraph] зарегистрирован провайдер для .cs");
            // TS-провайдер гоняет Node-экстрактор frontend/scripts/codegraph-extractor.mjs;
            // без Node/скрипта тихо отдаёт пустой граф (см. TypeScriptGraphProvider).
            var tsProvider = new TypeScriptGraphProvider(
                app.Services.GetRequiredService<ILogger<TypeScriptGraphProvider>>(),
                app.Configuration);
            codeGraphService.RegisterProvider(".ts", tsProvider);
            codeGraphService.RegisterProvider(".tsx", tsProvider);
            Console.WriteLine("[CodeGraph] зарегистрирован провайдер для .ts/.tsx");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeGraph] не удалось зарегистрировать провайдер: {ex.Message}");
        }
    }
}