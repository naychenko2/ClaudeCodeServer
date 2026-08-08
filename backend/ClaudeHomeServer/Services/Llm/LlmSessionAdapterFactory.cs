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
    // Токен HTTP MCP-сервера glif (инжектится там же, рядом с fal-ai)
    private readonly string? _glifMcpToken;
    private readonly string[] _disallowedTools;
    private readonly SkillsService _skills;
    private readonly WorkspaceKnowledgeStore _workspaceStore;
    private readonly LlmProviderRegistry _providers;
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    private readonly FileWatcherOptions _fileWatcherOptions;
    private readonly TimeSpan? _bgLingerTimeout;
    // Резолвер назначений: сессия без своей модели идёт на модель своего места (слоты
    // тиров), поэтому и провайдер резолвится по эффективной модели, а не по пустой Model
    private readonly ModelAssignmentResolver? _assignments;
    // Атрибуция file_changed чату-источнику — singleton на процесс, общий для всех сессий;
    // null — в тестах фабрики, собранных без него (фильтрация тогда просто выключена)
    private readonly FileChangeAttributor? _fileChangeAttributor;
    // Стор настроек фолбэк-оркестрации (потолок подмен per-owner → global → дефолт).
    // null (тесты без DI) — адаптер идёт по дефолту FallbackSettingsStore.DefaultMaxSubstitutions.
    private readonly FallbackSettingsStore? _fallbackSettings;
    // Кулдаун недоступности провайдера (волна 2): провайдер, вернувший Unreachable/ProviderError,
    // помечается недоступным на TTL — фолбэк пропускает его шаги цепочки. null (тесты) — выключен.
    private readonly ProviderHealthRegistry? _health;
    // Логгер фолбэк-оркестрации: без него подмены нечем отлаживать (что
    // классифицировали, куда переключились, почему кандидат отвергнут). null в тестах
    // без DI — адаптер пишет в Console.Error, чтобы не терять диагностику совсем.
    private readonly ILogger? _log;

    public LlmSessionAdapterFactory(IConfiguration config, SkillsService skills,
        WorkspaceKnowledgeStore workspaceStore, LlmProviderRegistry providers,
        ClaudeSubscriptionPool subscriptionPool, ModelAssignmentResolver? assignments = null,
        FileChangeAttributor? fileChangeAttributor = null,
        FallbackSettingsStore? fallbackSettings = null,
        ProviderHealthRegistry? health = null,
        ILogger<LlmSessionAdapterFactory>? log = null)
    {
        _assignments = assignments;
        _fileChangeAttributor = fileChangeAttributor;
        _fallbackSettings = fallbackSettings;
        _health = health;
        _log = log;
        _mcpConfigPath = config["McpConfigPath"];
        _falMcpApiKey = config["Fal:McpApiKey"];
        _glifMcpToken = config["Glif:McpToken"];
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
        // Провайдер по явному полю Provider (приоритет), затем по Model — по ЭФФЕКТИВНОЙ:
        // пустая session.Model означает «по назначению места», и если назначение указывает
        // на стороннего провайдера, проверку настроенности надо проходить по нему.
        // Ключ места — как в ClaudeSession.UsageKey (исполнитель задач → персона → чат).
        LlmProviderConfig? provider = null;
        if (!string.IsNullOrEmpty(session.Provider) && session.Provider != "claude")
            provider = _providers.GetByKey(session.Provider);
        var usageKey = session.TaskExecution || session.TaskId is not null ? LocalActionCatalog.TasksExecutor
            : !string.IsNullOrWhiteSpace(session.PersonaId) ? LocalActionCatalog.ChatPersona
            : LocalActionCatalog.ChatNew;
        provider ??= _providers.ResolveByModel(
            _assignments?.Resolve(usageKey, session.Model, session.OwnerId) ?? session.Model);

        if (provider is { Enabled: false })
            throw new InvalidOperationException(
                $"Провайдер «{provider.DisplayName}» не настроен: задай LlmProviders:{provider.Key}:ApiKey в appsettings.Local.json");

        // Фолбэк при рантайм-ошибках доставки (ADR «Порядок резолва модели…»): адаптер
        // оборачивается оркестратором, который видит все события хода через перехват
        // OnMessage и перезапускает ход на другой паре «модель × подписка» (уровень 1 —
        // ротация подписок пула, уровень 2 — цепочка сторонних провайдеров, потолок 5)
        FallbackLlmSessionAdapter? fallback = null;
        var innerContext = context with
        {
            OnMessage = msg => fallback is not null ? fallback.HandleMessageAsync(msg) : context.OnMessage(msg),
        };
        var claudeSession = new Claude.ClaudeSession(session, innerContext, _mcpConfigPath, _skills,
            _workspaceStore, _disallowedTools, _providers, _subscriptionPool, _fileWatcherOptions,
            _bgLingerTimeout, _falMcpApiKey, _glifMcpToken, _assignments, _fileChangeAttributor);
        fallback = new FallbackLlmSessionAdapter(claudeSession,
            () => claudeSession.EffectiveTurnModel,
            context.OnMessage, _subscriptionPool, _providers, context.RootPath,
            context.Launcher, context.CliConfigRoot, _fallbackSettings,
            () => claudeSession.EffectiveTurnChain, _health, _log);
        return fallback;
    }
}
