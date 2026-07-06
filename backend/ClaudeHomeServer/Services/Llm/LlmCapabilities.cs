namespace ClaudeHomeServer.Services.Llm;

// Возможности LLM-провайдера. Provider — wire-токен для фронта ("claude" | ключ из
// LlmProviders). Отдаётся в GET /api/models (блок providers) и в session_started.
// CLI-провайдеры строятся из конфига (LlmProviderRegistry.CapabilitiesOf).
public sealed record LlmCapabilities(
    string Provider,
    string DisplayName,            // имя провайдера для UI («Claude», «DeepSeek», «GLM»)
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
        DisplayName: "Claude",
        SupportsPlanMode: true,
        SupportsCompact: true,
        SupportsMcp: true,
        SupportsEffort: true,
        SupportsPermissionModes: true,
        SupportsImages: true,
        SupportsAgents: true);
}
