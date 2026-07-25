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

    // ParseUsage обязан брать агрегат modelUsage (сумма по всем итерациям хода), а не usage
    // последней итерации — иначе стоимость ходов у сторонних провайдеров занижена в разы
    [Fact]
    public void ParseUsage_ModelUsage_АгрегатПоВсемМоделям()
    {
        var e = El("""
            {
              "usage": {"input_tokens": 10, "output_tokens": 5, "cache_read_input_tokens": 1, "cache_creation_input_tokens": 2},
              "modelUsage": {
                "deepseek-v4-pro": {"inputTokens": 1000, "outputTokens": 500, "cacheReadInputTokens": 300, "cacheCreationInputTokens": 200},
                "deepseek-v4-flash": {"inputTokens": 100, "outputTokens": 50, "cacheReadInputTokens": 30, "cacheCreationInputTokens": 20}
              }
            }
            """);
        var u = ClaudeSession.ParseUsage(e);
        Assert.NotNull(u);
        Assert.Equal(1100, u!.InputTokens);
        Assert.Equal(550, u.OutputTokens);
        Assert.Equal(330, u.CacheReadTokens);
        Assert.Equal(220, u.CacheCreationTokens);
    }

    [Fact]
    public void ParseUsage_ПустойModelUsage_ФолбэкНаUsage()
    {
        var e = El("""
            {
              "modelUsage": {},
              "usage": {"input_tokens": 10, "output_tokens": 5, "cache_read_input_tokens": 1, "cache_creation_input_tokens": 2}
            }
            """);
        var u = ClaudeSession.ParseUsage(e);
        Assert.NotNull(u);
        Assert.Equal(10, u!.InputTokens);
        Assert.Equal(5, u.OutputTokens);
    }

    [Fact]
    public void ParseUsage_NullПоля_НеКидает()
    {
        // openrouter-совместимый поток шлёт числовые поля как JSON null
        var e = El("""
            {"modelUsage": {"m": {"inputTokens": null, "outputTokens": 7}}}
            """);
        var u = ClaudeSession.ParseUsage(e);
        Assert.NotNull(u);
        Assert.Equal(0, u!.InputTokens);
        Assert.Equal(7, u.OutputTokens);
    }

    [Fact]
    public void ParseUsage_БезВсего_Null()
    {
        Assert.Null(ClaudeSession.ParseUsage(El("""{"type": "result"}""")));
    }
}
