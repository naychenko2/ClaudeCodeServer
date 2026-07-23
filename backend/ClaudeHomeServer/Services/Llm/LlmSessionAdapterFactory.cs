using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

public interface ILlmSessionAdapterFactory
{
    // Единственный рантайм — claude CLI; сторонний провайдер (по LlmProviderRegistry
    // из session.Model) подключается env-оверрайдами процесса
    ILlmSessionAdapter Create(Session session, LlmSessionContext context);
}

// Фабрика адаптеров: провайдеро-специфичные зависимости (MCP-конфиг, скиллы,
// реестр провайдеров) живут здесь, а не в SessionManager —
// он про жизненный цикл, не про провайдера.
public sealed class LlmSessionAdapterFactory : ILlmSessionAdapterFactory
{
    private readonly string? _mcpConfigPath;
    // Ключ HTTP MCP-сервера fal-ai (инжектится в конфиг хода в BuildTurnMcpConfig)
    private readonly string? _falMcpApiKey;
    private readonly string[] _disallowedTools;
    private readonly SkillsService _skills;
    private readonly WorkspaceKnowledgeStore _workspaceStore;
    private readonly LlmProviderRegistry _providers;
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    private readonly FileWatcherOptions _fileWatcherOptions;
    private readonly TimeSpan? _bgLingerTimeout;

    public LlmSessionAdapterFactory(IConfiguration config, SkillsService skills,
        WorkspaceKnowledgeStore workspaceStore, LlmProviderRegistry providers,
        ClaudeSubscriptionPool subscriptionPool)
    {
        _mcpConfigPath = config["McpConfigPath"];
        _falMcpApiKey = config["Fal:McpApiKey"];
        _disallowedTools = config.GetSection("Claude:DisallowedTools").Get<string[]>() ?? [];
        // Шумоподавление ватчера изменений файлов (секция FileWatcher) — пустые списки
        // в конфиге дают дефолты, отдельные ключи переопределяют только себя
        var fw = config.GetSection("FileWatcher");
        var d = FileWatcherOptions.Default;
        _fileWatcherOptions = new FileWatcherOptions(
            IgnoreDirs: fw.GetSection("IgnoreDirs").Get<string[]>() is { Length: > 0 } dirs ? dirs : d.IgnoreDirs,
            IgnoreFilePatterns: fw.GetSection("IgnoreFilePatterns").Get<string[]>() is { Length: > 0 } pats ? pats : d.IgnoreFilePatterns,
            RespectGitignore: fw.GetValue("RespectGitignore", d.RespectGitignore));
        // Потолок доживания процесса с фоновыми агентами после конца хода (минуты) —
        // прокидываем в каждый адаптер, а не мутируем глобальный static
        if (int.TryParse(config["Claude:BgLingerMinutes"], out var lingerMin) && lingerMin > 0)
            _bgLingerTimeout = TimeSpan.FromMinutes(lingerMin);
        _skills = skills;
        _workspaceStore = workspaceStore;
        _providers = providers;
        _subscriptionPool = subscriptionPool;
    }

    public ILlmSessionAdapter Create(Session session, LlmSessionContext context)
    {
        // Провайдер по явному полю Provider (приоритет), затем по Model
        LlmProviderConfig? provider = null;
        if (!string.IsNullOrEmpty(session.Provider) && session.Provider != "claude")
            provider = _providers.GetByKey(session.Provider);
        provider ??= _providers.ResolveByModel(session.Model);

        if (provider is { Enabled: false })
            throw new InvalidOperationException(
                $"Провайдер «{provider.DisplayName}» не настроен: задай LlmProviders:{provider.Key}:ApiKey в appsettings.Local.json");
        return new Claude.ClaudeSession(session, context, _mcpConfigPath, _skills,
            _workspaceStore, _disallowedTools, _providers, _subscriptionPool, _fileWatcherOptions,
            _bgLingerTimeout, _falMcpApiKey);
    }
}
