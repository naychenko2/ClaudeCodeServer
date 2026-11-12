using ClaudeHomeServer.Services.CodeGraph;

namespace ClaudeHomeServer.Services.Composition;

// Клей Architecture↔CodeGraph (Viaduct 10.1, S6-1): регистрация форвардера
// `IArchitectureCodeSource` → `ArchitectureCodeSource` (Main, тонкая обёртка над
// `CodeGraphService`) перенесена из точечного гейта в Program.cs.
//
// Гейт — тот же ключ, что проверялся раньше: `CodeGraphSubsystem.SubsystemKey`.
// Смысл сохранения гейта: у контроллера Architecture шов необязателен (нет → 503
// graph_unavailable), а форвардер без `CodeGraphService` не резолвится — при
// выключенном code-graph регистрация невозможна, а не просто нежелательна.
public static class ArchitectureCodeGraphGlue
{
    public static IServiceCollection AddArchitectureCodeSource(
        this IServiceCollection services, IConfiguration config)
    {
        if (SubsystemGate.IsEnabled(config, CodeGraphSubsystem.SubsystemKey))
            services.AddSingleton<IArchitectureCodeSource, ArchitectureCodeSource>();
        return services;
    }
}
