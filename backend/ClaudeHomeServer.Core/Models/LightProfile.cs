namespace ClaudeHomeServer.Models;

// Профиль «облегчённого контекста» (краткая карта вместо полного CLAUDE.md, урезанные
// встроенные инструменты, урезанный набор MCP-серверов и их инструментов, хвостовой recall).
// Единый носитель для двух источников: собственные поля провайдера (BareMode и соседи в
// LlmProviderConfig — local-qwen) и общая секция конфига LlmProviders:LightProfile — для
// моделей без собственного профиля (Opus/Sonnet у персоны с LightContext). Резолвится
// ТОЛЬКО через LlmProviderRegistry.LightProfileFor: все потребители (ClaudeSession и фильтр
// McpToolWhitelist) обязаны идти этой дорогой, иначе состав MCP у них разъедется
// («No such tool available»).
public class LightProfile
{
    // Откуда профиль: ключ провайдера или "light" для общей секции (диагностика в логах)
    public string Source { get; set; } = "light";

    // Автозагрузка CLAUDE.md проекта отключена, подаётся краткая карта (см. SystemPromptFile)
    public bool BareMode { get; set; } = true;

    // Гасить MCP-серверы, которых нет в KeepMcpServers
    public bool TrimMcpServers { get; set; } = true;

    public string[] KeepMcpServers { get; set; } = [];

    // Белый список инструментов внутри оставленного сервера (семантика — LlmProviderConfig.KeepMcpTools)
    public Dictionary<string, string[]> KeepMcpTools { get; set; } = [];

    public string? SystemPromptFile { get; set; }

    public string[] BareTools { get; set; } = [];

    // Нестабильные секции промпта — хвостом хода (см. LlmProviderConfig.RecallInTurnText)
    public bool RecallInTurnText { get; set; }

    // Подставлять ли серверную карту (SystemPromptFile — карта НАШЕГО репозитория), когда у
    // проекта нет своего docs/CLAUDE-local.md. У провайдерного профиля true (прежнее
    // поведение local-qwen), у общей секции false: персоне в чужом проекте карта чужого
    // репозитория — неверный контекст, лучше без карты.
    public bool ServerMapFallback { get; set; }

    public static LightProfile FromProvider(LlmProviderConfig p) => new()
    {
        Source = p.Key,
        BareMode = p.BareMode,
        TrimMcpServers = p.TrimMcpServers,
        KeepMcpServers = p.KeepMcpServers,
        KeepMcpTools = p.KeepMcpTools,
        SystemPromptFile = p.SystemPromptFile,
        BareTools = p.BareTools,
        RecallInTurnText = p.RecallInTurnText,
        ServerMapFallback = true,
    };
}
