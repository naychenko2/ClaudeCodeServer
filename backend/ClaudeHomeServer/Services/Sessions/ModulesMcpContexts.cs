using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Sessions;

// Контекст MCP-серверов внешних модулей (S3-4, docs/research/session-manager-split-2026-11.md).
// Тело перенесено из SessionManager дословно; фасад держит делегирующую обёртку прежней
// сигнатуры.
//
// Роль вынесена отдельно от ExternalMcpContexts: с модулями в ней было бы двенадцать
// зависимостей, а новая зависимость — сигнал резать класс, а не поднять лимит (S3-0 §2).
// Здесь их пять: endpoint, users, flags, modules, moduleTokens (последние два — GetService:
// без реестра модулей контекста нет, как и раньше).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и DI нужен
// публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4: на типы Services.Sessions
// ссылаются лишь SessionManager, сами Services.Sessions и тесты).
public sealed class ModulesMcpContexts
{
    private readonly McpEndpoint _endpoint;
    private readonly UserStore _users;
    private readonly FeatureFlagService _flags;
    private readonly Modules.ModuleRegistry? _modules;
    private readonly Modules.ModuleTokenService? _moduleTokens;

    public ModulesMcpContexts(McpEndpoint endpoint, UserStore users, FeatureFlagService flags,
        Modules.ModuleRegistry? modules, Modules.ModuleTokenService? moduleTokens)
    {
        _endpoint = endpoint;
        _users = users;
        _flags = flags;
        _modules = modules;
        _moduleTokens = moduleTokens;
    }

    // Контекст MCP-серверов внешних модулей (контракт §6, ТЗ R7): по каждому модулю
    // с mcp[] в манифесте и включённым у владельца флагом module-{id} (R8) — записи
    // серверов с адресом ЧЕРЕЗ gateway ядра и фабрикой свежего токена chan=mcp (TTL 60 мин;
    // ход длиннее часа получит 401 на инструментах модуля — по контракту это корректно).
    // args манифеста резолвятся от каталога модуля. null — модулей нет/все скрыты.
    internal ModulesMcpContext? BuildModulesContext(string? ownerId)
    {
        if (ownerId is null || _modules is null || _moduleTokens is null) return null;
        var user = _users.GetById(ownerId);
        if (user is null) return null;
        var apiBase = _endpoint.ResolveTasksApiUrl(ownerId);
        var servers = new List<ModuleMcpServer>();
        foreach (var module in _modules.All)
        {
            if (module.Manifest.Mcp is not { Count: > 0 } mcpList) continue;
            if (!_flags.IsEnabled(ownerId, module.FeatureFlagKey)) continue;
            var moduleRef = module;
            foreach (var mcp in mcpList)
            {
                var args = (mcp.Args ?? [])
                    .Select(a => Path.IsPathRooted(a) ? a : Path.GetFullPath(Path.Combine(module.ModuleDir, a)))
                    .ToList();
                servers.Add(new ModuleMcpServer(
                    mcp.Key, mcp.Command, args, module.Id,
                    $"{apiBase}{module.Manifest.Backend!.RoutePrefix}",
                    () => _moduleTokens.Issue(moduleRef, user.Id, user.DisplayName ?? user.Username, "mcp")));
            }
        }
        return servers.Count > 0 ? new ModulesMcpContext(servers) : null;
    }
}
