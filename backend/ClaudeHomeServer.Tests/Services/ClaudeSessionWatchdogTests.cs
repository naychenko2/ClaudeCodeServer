using System.Text.Json;
using ClaudeHomeServer.Services.Llm.Claude;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Безопасное чтение чисел из stream-json стороннего провайдера (openrouter шлёт usage/стоимость
// как JSON null). Регрессия: TryGetInt32 на Null-элементе КИДАЕТ, а не возвращает false —
// хелперы обязаны проверять ValueKind == Number, иначе роняют весь цикл чтения хода.
public class ClaudeSessionNumberParsingTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void IntProp_NullOrMissing_ReturnsDefault_NoThrow()
    {
        var e = El("""{"n": 5, "z": null, "s": "x"}""");
        Assert.Equal(5, ClaudeSession.IntProp(e, "n"));
        Assert.Equal(0, ClaudeSession.IntProp(e, "z"));   // JSON null — не кидать
        Assert.Equal(0, ClaudeSession.IntProp(e, "s"));   // строка — не кидать
        Assert.Equal(0, ClaudeSession.IntProp(e, "missing"));
    }

    [Fact]
    public void LongAndDouble_NullSafe()
    {
        var e = El("""{"d": 1.5, "dz": null, "l": 42, "lz": null}""");
        Assert.Equal(42L, ClaudeSession.LongProp(e, "l"));
        Assert.Equal(0L, ClaudeSession.LongProp(e, "lz"));
        Assert.Equal(1.5, ClaudeSession.DoubleProp(e, "d"));
        Assert.Null(ClaudeSession.DoubleProp(e, "dz"));
        Assert.Null(ClaudeSession.DoubleProp(e, "missing"));
    }
}

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
        // Инструмент не выполняется, пользователя не ждём → короткий таймаут: молчащий стрим = обрыв
        Assert.Equal(WhileGenerating, ClaudeSession.ActiveTurnWatchdog(0, false, WhileTool, WhileGenerating));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ToolRunning_UsesLongTimeout(int pending)
    {
        // Есть незавершённые инструменты → щедрый таймаут (сборки/тесты молчат легитимно)
        Assert.Equal(WhileTool, ClaudeSession.ActiveTurnWatchdog(pending, false, WhileTool, WhileGenerating));
    }

    [Fact]
    public void AwaitingUser_UsesLongTimeout()
    {
        // Ход ждёт ответа пользователя (AskUserQuestion/ExitPlanMode) — не рубить коротким таймаутом,
        // даже когда инструмент не выполняется: пользователь думает сколько угодно
        Assert.Equal(WhileTool, ClaudeSession.ActiveTurnWatchdog(0, true, WhileTool, WhileGenerating));
    }
}
