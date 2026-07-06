using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm.DeepSeek;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

public interface ILlmSessionAdapterFactory
{
    // Выбирает адаптер по LlmProviderResolver.Resolve(session.Model)
    ILlmSessionAdapter Create(Session session, LlmSessionContext context);

    LlmCapabilities GetCapabilities(LlmProvider provider);

    // DeepSeek доступен только при заданном API-ключе
    bool IsProviderAvailable(LlmProvider provider);
}

// Фабрика адаптеров: провайдеро-специфичные зависимости (MCP-конфиг и скиллы Claude,
// HTTP-клиент и options DeepSeek) живут здесь, а не в SessionManager —
// он про жизненный цикл, не про провайдера.
public sealed class LlmSessionAdapterFactory : ILlmSessionAdapterFactory
{
    private readonly string? _mcpConfigPath;
    private readonly string[] _disallowedTools;
    private readonly SkillsService _skills;
    private readonly WorkspaceKnowledgeStore _workspaceStore;
    private readonly DeepSeekClient _deepSeekClient;
    private readonly IOptions<DeepSeekOptions> _deepSeekOptions;
    private readonly FileService _files;
    // Папка историй сессий (та же, что у ChatHistoryService) — для deepseek-messages.json
    private readonly string _sessionsBasePath;

    private readonly IHttpClientFactory _httpFactory;

    public LlmSessionAdapterFactory(IConfiguration config, SkillsService skills,
        WorkspaceKnowledgeStore workspaceStore, DeepSeekClient deepSeekClient,
        IOptions<DeepSeekOptions> deepSeekOptions, FileService files, IHttpClientFactory httpFactory)
    {
        _mcpConfigPath = config["McpConfigPath"];
        _disallowedTools = config.GetSection("Claude:DisallowedTools").Get<string[]>() ?? [];
        _skills = skills;
        _workspaceStore = workspaceStore;
        _deepSeekClient = deepSeekClient;
        _deepSeekOptions = deepSeekOptions;
        _files = files;
        _httpFactory = httpFactory;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsBasePath = Path.Combine(dataDir, "sessions");
    }

    public ILlmSessionAdapter Create(Session session, LlmSessionContext context) =>
        LlmProviderResolver.Resolve(session.Model) switch
        {
            LlmProvider.DeepSeek => CreateDeepSeek(session, context),
            _ => new Claude.ClaudeSession(session, context, _mcpConfigPath, _skills, _workspaceStore, _disallowedTools),
        };

    private DeepSeekSession CreateDeepSeek(Session session, LlmSessionContext context)
    {
        if (!_deepSeekOptions.Value.Enabled)
            throw new InvalidOperationException(
                "DeepSeek не настроен: задай DeepSeek:ApiKey в appsettings.Local.json");
        return new DeepSeekSession(session, context, _deepSeekClient, _deepSeekOptions, _files,
            _sessionsBasePath, _skills, _mcpConfigPath, _workspaceStore, _httpFactory);
    }

    public LlmCapabilities GetCapabilities(LlmProvider provider) => LlmCapabilitiesCatalog.For(provider);

    public bool IsProviderAvailable(LlmProvider provider) =>
        provider == LlmProvider.Claude || _deepSeekOptions.Value.Enabled;
}
