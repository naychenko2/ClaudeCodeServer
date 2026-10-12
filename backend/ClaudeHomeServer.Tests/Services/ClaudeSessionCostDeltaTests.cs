using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm.Claude;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Дельта хода (задача ad52316b): с 24.09 claude CLI на --resume восстанавливает накопленный
// cost-state (modelUsage/total_cost_usd), поэтому result несёт НАКОПЛЁННЫЙ итог сессии, а не
// расход хода. Расход хода = дельта между двумя накопленными снимками (текущим и прошлым из
// Session.LastCostState). Чистые функции (BuildCostState/ComputeTurnDelta/AggregateUsage/
// DominantModel) — тест без процесса CLI, платформонезависим (CI на Linux): ни Task.Delay,
// ни spawn.
public class ClaudeSessionCostDeltaTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    // result с накопленным modelUsage (одна модель) и total_cost_usd. Конкатенация, а не
    // интерполяция в raw-строке: литеральные }} в JSON конфликтовали бы с дырами {{...}}.
    private static string ResultJson(long input, long output, long cacheRead, long cacheCreate, double cost) =>
        "{\"modelUsage\": {\"claude-sonnet-5-5\": {\"inputTokens\": " + input +
        ", \"outputTokens\": " + output +
        ", \"cacheReadInputTokens\": " + cacheRead +
        ", \"cacheCreationInputTokens\": " + cacheCreate +
        "}}, \"total_cost_usd\": " + cost.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}";

    // 1. Два подряд result с РАСТУЩИМ modelUsage: первый ход (prev=null) засчитывает весь
    //    снимок, второй — только РАЗНИЦУ. Это и есть ядро фикса: накопленный итог не
    //    пересчитывается целиком на каждом ходе.
    [Fact]
    public void Дельта_РастущийИтог_ВторойХодТолькоРазница()
    {
        // Ход 1: первый (prev=null) → расход = сам снимок
        var first = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 1.0)));
        var (u1, c1, _) = ClaudeSession.ComputeTurnDelta(first, prev: null);
        Assert.NotNull(u1);
        Assert.Equal(100, u1!.InputTokens);
        Assert.Equal(50, u1.OutputTokens);
        Assert.Equal(1000, u1.CacheReadTokens);
        Assert.Equal(100, u1.CacheCreationTokens);
        Assert.Equal(1.0, c1!.Value, precision: 6);

        // Ход 2: prev=ход1, итог вырос → расход = только разница
        var second = ClaudeSession.BuildCostState(El(ResultJson(150, 80, 2000, 150, 2.0)));
        var (u2, c2, _) = ClaudeSession.ComputeTurnDelta(second, first);
        Assert.NotNull(u2);
        Assert.Equal(50, u2!.InputTokens);        // 150 - 100
        Assert.Equal(30, u2.OutputTokens);         // 80 - 50
        Assert.Equal(1000, u2.CacheReadTokens);    // 2000 - 1000
        Assert.Equal(50, u2.CacheCreationTokens);  // 150 - 100
        Assert.Equal(1.0, c2!.Value, precision: 6);       // 2.0 - 1.0
    }

    // 2. Пустой повтор: накопленный итог НЕ изменился (микро-ход task-notification на --resume)
    //    → дельта = ноль (а не весь накопленный итог, как было до фикса).
    [Fact]
    public void Дельта_ПустойПовтор_Ноль()
    {
        var baseState = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 1.0)));
        // тот же накопленный итог (повтор)
        var repeat = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 1.0)));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(repeat, baseState);
        // usage может быть null (нет моделей) либо нулями; в обоих случаях расход = 0
        if (u is not null)
        {
            Assert.Equal(0, u.InputTokens);
            Assert.Equal(0, u.OutputTokens);
            Assert.Equal(0, u.CacheReadTokens);
            Assert.Equal(0, u.CacheCreationTokens);
        }
        Assert.Equal(0.0, c!.Value, precision: 6);
    }

    // 3. Уменьшение итога (новая csid / форк / сброс CLI): накопленный итог стал МЕНЬШЕ
    //    прошлого → дельта была бы отрицательной → расход = сам текущий снимок (целиком).
    [Fact]
    public void Дельта_УменьшениеИтога_ПолныйСнимок()
    {
        var prev = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 5.0)));
        // сброс: итог стал меньше (например, новая csid с меньшим накопленным итогом)
        var cur = ClaudeSession.BuildCostState(El(ResultJson(40, 20, 300, 40, 2.0)));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // не разница (30/20/...), а сам текущий снимок целиком
        Assert.NotNull(u);
        Assert.Equal(40, u!.InputTokens);
        Assert.Equal(20, u.OutputTokens);
        Assert.Equal(300, u.CacheReadTokens);
        Assert.Equal(40, u.CacheCreationTokens);
        Assert.Equal(2.0, c!.Value, precision: 6);
    }

    // 4. Доминирующая модель — по Дельте, а не по накопленному итогу: модель, которой ход
    //    реально потратил больше, и есть главная (субагенты могли считать другой моделью).
    [Fact]
    public void Дельта_ДоминирующаяМодельПоДельте()
    {
        // prev: обе модели с большим накопленным итогом
        var prevJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 900, "outputTokens": 900, "cacheReadInputTokens": 900, "cacheCreationInputTokens": 900},
                             "haiku":  {"inputTokens": 100, "outputTokens": 100, "cacheReadInputTokens": 100, "cacheCreationInputTokens": 100}},
             "total_cost_usd": 9.0}
            """;
        // current: sonnet чуть вырос, haiku вырос БОЛЬШЕ (дельта) → haiku доминирует по дельте
        var curJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 950, "outputTokens": 950, "cacheReadInputTokens": 950, "cacheCreationInputTokens": 950},
                             "haiku":  {"inputTokens": 300, "outputTokens": 300, "cacheReadInputTokens": 300, "cacheCreationInputTokens": 300}},
             "total_cost_usd": 12.0}
            """;
        var prev = ClaudeSession.BuildCostState(El(prevJson));
        var cur = ClaudeSession.BuildCostState(El(curJson));
        var (u, _, model) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // дельта: sonnet +50 на счётчик, haiku +200 на счётчик → haiku больше
        Assert.NotNull(u);
        Assert.Equal("haiku", model);
    }

    // 5. Новая модель (в prev её не было): весь её вклад засчитывается (это не «уменьшение»).
    [Fact]
    public void Дельта_НоваяМодель_ВесьВклад()
    {
        var prevJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 100, "outputTokens": 100, "cacheReadInputTokens": 100, "cacheCreationInputTokens": 100}},
             "total_cost_usd": 1.0}
            """;
        var curJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 150, "outputTokens": 150, "cacheReadInputTokens": 150, "cacheCreationInputTokens": 150},
                             "opus":   {"inputTokens": 500, "outputTokens": 500, "cacheReadInputTokens": 500, "cacheCreationInputTokens": 500}},
             "total_cost_usd": 3.0}
            """;
        var prev = ClaudeSession.BuildCostState(El(prevJson));
        var cur = ClaudeSession.BuildCostState(El(curJson));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // sonnet: дельта +50 на счётчик; opus: новый → весь вклад 500 на счётчик
        Assert.NotNull(u);
        // суммарно: sonnet 50*4 + opus 500*4 = 200 + 2000 = 2200 на каждый тип? нет:
        // input = 50 (sonnet) + 500 (opus) = 550
        Assert.Equal(550, u!.InputTokens);
        Assert.Equal(550, u.OutputTokens);
        Assert.Equal(550, u.CacheReadTokens);
        Assert.Equal(550, u.CacheCreationTokens);
        Assert.Equal(2.0, c!.Value, precision: 6); // 3.0 - 1.0
    }

    // 6. Снимок без modelUsage (сторонний поток, только usage) → BuildCostState пуст,
    //    дельта = null usage (как ParseUsage без modelUsage/usage): потребитель понимает
    //    null как «токенов не было», не роняя ход.
    [Fact]
    public void Дельта_БезModelUsage_NullUsage()
    {
        var cur = ClaudeSession.BuildCostState(El("""{"usage": {"input_tokens": 10}}"""));
        var (u, c, model) = ClaudeSession.ComputeTurnDelta(cur, prev: null);
        Assert.Null(u);
        Assert.Null(c);   // total_cost_usd не прислан
        Assert.Null(model);
    }
}
