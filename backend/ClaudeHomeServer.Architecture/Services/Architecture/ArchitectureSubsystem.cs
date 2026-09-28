using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Architecture;

// Подсистема «Архитектура» (встраивание Viaduct, docs/research/viaduct-embed-plan.md).
// Здесь генератор стартовой модели из кода, хранилище модели (версия → 409) и
// MCP-тулсет arch_* для персон поверх того же хранилища.
// Контроллер и раздача собранного Viaduct тоже здесь. Вертикаль не знает про
// CodeGraph: снимок графа приходит через Core-шов IArchitectureCodeSource (форвардер
// в Main), контроллер перекладывает его в нейтральные записи (ArchitectureInputs.cs).
public sealed class ArchitectureSubsystem : IAppSubsystem
{
    // Константой — чтобы SessionManager, решающий объявление MCP-сервера ходу, спрашивал
    // SubsystemGate тем же ключом, а не строкой-однофамильцем. Источник строки один —
    // имя MCP-сервера в Core (им же SessionManager проверяет ActiveKeys)
    public const string SubsystemKey = McpEndpoints.ArchitectureName;

    public string Key => SubsystemKey;

    public string Title => "Архитектура";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ArchitectureModelGenerator>();
        services.AddSingleton<ArchitectureModelStore>();
        // MCP-over-HTTP-тулсет (ADR-012): выключенная подсистема — тулсета нет в реестре,
        // а SessionManager не объявляет сервер ходу по тому же SubsystemGate
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset, ArchitectureToolset>();
        // Ветка /modules/viaduct: Main ставит её циклом по вкладам на своём месте конвейера
        services.AddSingleton<IStaticBranchContributor, ViaductStaticBranch>();
    }
}
