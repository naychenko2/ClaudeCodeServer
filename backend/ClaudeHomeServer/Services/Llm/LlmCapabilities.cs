namespace ClaudeHomeServer.Services.Llm;

// Возможности LLM-провайдера. Provider — wire-токен для фронта ("claude" | "deepseek").
// Отдаётся в GET /api/models (блок providers) и в session_started.
public sealed record LlmCapabilities(
    string Provider,
    bool SupportsPlanMode,         // режим «План» + карточки plan_review
    bool SupportsCompact,          // /compact + compact_boundary
    bool SupportsMcp,              // MCP-серверы (tasks/dify)
    bool SupportsEffort,           // уровень reasoning effort
    bool SupportsPermissionModes,  // весь набор ClaudeMode; false → только базовые режимы
    bool SupportsImages,           // image-блоки во вложениях
    bool SupportsAgents);          // инжекция промпта .claude/agents/<name>.md

public static class LlmCapabilitiesCatalog
{
    public static readonly LlmCapabilities Claude = new(
        Provider: "claude",
        SupportsPlanMode: true,
        SupportsCompact: true,
        SupportsMcp: true,
        SupportsEffort: true,
        SupportsPermissionModes: true,
        SupportsImages: true,
        SupportsAgents: true);

    public static readonly LlmCapabilities DeepSeek = new(
        Provider: "deepseek",
        SupportsPlanMode: true,        // эмуляция: exit_plan_mode + только read-инструменты
        SupportsCompact: true,         // суммаризация истории отдельным запросом
        SupportsMcp: true,             // stdio-клиент: tasks-server, Dify и серверы из .mcp.json
        SupportsEffort: true,          // маппинг на thinking.reasoning_effort (high/max)
        SupportsPermissionModes: true, // полный набор режимов (Execute-инструменты всегда спрашивают)
        SupportsImages: false,         // ограничение API — изображений нет
        SupportsAgents: true);         // промпт .claude/agents/<name>.md в системный контекст

    public static LlmCapabilities For(LlmProvider provider) =>
        provider == LlmProvider.DeepSeek ? DeepSeek : Claude;
}
