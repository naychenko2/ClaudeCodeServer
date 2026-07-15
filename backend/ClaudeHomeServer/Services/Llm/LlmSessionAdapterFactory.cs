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
    private readonly string[] _disallowedTools;
    private readonly SkillsService _skills;
    private readonly WorkspaceKnowledgeStore _workspaceStore;
    private readonly LlmProviderRegistry _providers;
    private readonly ClaudeSubscriptionPool _subscriptionPool;

    public LlmSessionAdapterFactory(IConfiguration config, SkillsService skills,
        WorkspaceKnowledgeStore workspaceStore, LlmProviderRegistry providers,
        ClaudeSubscriptionPool subscriptionPool)
    {
        _mcpConfigPath = config["McpConfigPath"];
        _disallowedTools = config.GetSection("Claude:DisallowedTools").Get<string[]>() ?? [];
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
            _workspaceStore, _disallowedTools, _providers, _subscriptionPool);
    }
}
