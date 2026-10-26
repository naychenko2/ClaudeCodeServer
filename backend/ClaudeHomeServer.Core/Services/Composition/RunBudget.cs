namespace ClaudeHomeServer.Services.Composition;

// Общий потолок прогона: меньше MCP_TOOL_TIMEOUT хода (600 с), чтобы ответ модели
// успел уйти до того, как CLI оборвёт вызов. Число живёт здесь, потому что его
// берут две сборки (Core — шов предгейта, вертикаль TestRuns — потолок прогона),
// а ссылка между ними по построению невозможна. Менять — здесь.
public static class RunBudget
{
    public const int MaxSeconds = 540;
}
