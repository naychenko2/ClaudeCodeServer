using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

public interface ILlmSessionAdapterFactory
{
    // Выбирает адаптер по LlmProviderResolver.Resolve(session.Model)
    ILlmSessionAdapter Create(Session session, LlmSessionContext context);

    LlmCapabilities GetCapabilities(LlmProvider provider);

    // DeepSeek доступен только при заданном API-ключе
    bool IsProviderAvailable(LlmProvider provider);
}

// Фабрика адаптеров: Claude-специфичные зависимости (MCP-конфиг, скиллы, база знаний,
// disallowed tools) живут здесь, а не в SessionManager — он про жизненный цикл, не про провайдера.
public sealed class LlmSessionAdapterFactory : ILlmSessionAdapterFactory
{
    private readonly string? _mcpConfigPath;
    private readonly string[] _disallowedTools;
    private readonly SkillsService _skills;
    private readonly WorkspaceKnowledgeStore _workspaceStore;

    public LlmSessionAdapterFactory(IConfiguration config, SkillsService skills,
        WorkspaceKnowledgeStore workspaceStore)
    {
        _mcpConfigPath = config["McpConfigPath"];
        _disallowedTools = config.GetSection("Claude:DisallowedTools").Get<string[]>() ?? [];
        _skills = skills;
        _workspaceStore = workspaceStore;
    }

    public ILlmSessionAdapter Create(Session session, LlmSessionContext context) =>
        LlmProviderResolver.Resolve(session.Model) switch
        {
            LlmProvider.DeepSeek => throw new NotSupportedException("Адаптер DeepSeek ещё не подключён"),
            _ => new Claude.ClaudeSession(session, context, _mcpConfigPath, _skills, _workspaceStore, _disallowedTools),
        };

    public LlmCapabilities GetCapabilities(LlmProvider provider) => LlmCapabilitiesCatalog.For(provider);

    public bool IsProviderAvailable(LlmProvider provider) => provider == LlmProvider.Claude;
}
