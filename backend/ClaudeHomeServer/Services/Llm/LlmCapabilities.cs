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
        SupportsPlanMode: false,
        SupportsCompact: false,
        SupportsMcp: false,
        SupportsEffort: false,
        SupportsPermissionModes: false,
        SupportsImages: false,
        SupportsAgents: false);

    public static LlmCapabilities For(LlmProvider provider) =>
        provider == LlmProvider.DeepSeek ? DeepSeek : Claude;
}
