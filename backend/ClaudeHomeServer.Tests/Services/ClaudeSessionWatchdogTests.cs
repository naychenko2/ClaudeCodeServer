using ClaudeHomeServer.Services.Llm.Claude;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Выбор таймаута тишины активного хода: короткий во время генерации, щедрый во время инструмента.
// Так зависшая генерация (обрыв провайдера после thinking) прерывается быстро, а долгие
// инструменты (Bash/сборка, молчащие в stdout) не рубятся преждевременно.
public class ClaudeSessionWatchdogTests
{
    private static readonly TimeSpan WhileTool = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WhileGenerating = TimeSpan.FromMinutes(2);

    [Fact]
    public void Generation_UsesShortTimeout()
    {
        // Инструмент не выполняется (0 pending) → короткий таймаут: молчащий стрим = обрыв
        Assert.Equal(WhileGenerating, ClaudeSession.ActiveTurnWatchdog(0, WhileTool, WhileGenerating));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ToolRunning_UsesLongTimeout(int pending)
    {
        // Есть незавершённые инструменты → щедрый таймаут (сборки/тесты молчат легитимно)
        Assert.Equal(WhileTool, ClaudeSession.ActiveTurnWatchdog(pending, WhileTool, WhileGenerating));
    }
}
