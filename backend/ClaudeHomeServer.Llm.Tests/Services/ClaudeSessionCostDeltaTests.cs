using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Дельта хода (задачи ad52316b + e487db76): с 24.09 claude CLI на --resume восстанавливает
// накопленный cost-state (modelUsage/total_cost_usd), поэтому result несёт НАКОПЛЁННЫЙ итог
// сессии, а не расход хода. Расход хода = дельта между двумя накопленными снимками (текущим и
// прошлым из Session.LastCostState). Правила (финальное ревью Александра):
//   * prev == null (первый ход) → сам снимок;
//   * смена csid (новый разговор/сброс/смена провайдера) → сам снимок (итог с нуля);
//   * та же csid → дельта; уменьшение (гонка двух процессов CLI) → max(0, cur − prev), не весь снимок;
//   * форк (ветка наследует снимок родителя с чужой csid) → дельта, не «весь снимок»;
//   * нет modelUsage (сторонний поток) → usage хода как есть, без дельты, базу не трогаем.
// Чистые функции (BuildCostState/ComputeTurnDelta/BuildCostStateFromTranscript/PrevCostStateForDelta)
// — тест без процесса CLI, платформонезависим (CI на Linux): ни Task.Delay, ни spawn.
public class ClaudeSessionCostDeltaTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    // result с накопленным modelUsage (одна модель) и total_cost_usd. csid — опционально:
    // session_id в result, к которому относится снимок. Конкатенация, а не интерполяция в
    // raw-строке: литеральные }} в JSON конфликтовали бы с дырами {{...}}.
    private static string ResultJson(long input, long output, long cacheRead, long cacheCreate,
        double cost, string? csid = null)
    {
        var sid = csid is null ? "" : $", \"session_id\": \"{csid}\"";
        return "{\"modelUsage\": {\"claude-sonnet-5-5\": {\"inputTokens\": " + input +
            ", \"outputTokens\": " + output +
            ", \"cacheReadInputTokens\": " + cacheRead +
            ", \"cacheCreationInputTokens\": " + cacheCreate +
            "}}, \"total_cost_usd\": " + cost.ToString("0.0", CultureInfo.InvariantCulture) + sid + "}";
    }

    // 1. Два подряд result с РАСТУЩИМ modelUsage (та же csid): первый ход (prev=null) засчитывает
    //    весь снимок, второй — только РАЗНИЦУ. Ядро фикса: накопленный итог не пересчитывается
    //    целиком на каждом ходе.
    [Fact]
    public void Дельта_РастущийИтог_ВторойХодТолькоРазница()
    {
        var first = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 1.0, "csid-A")));
        var (u1, c1, _) = ClaudeSession.ComputeTurnDelta(first, prev: null);
        Assert.NotNull(u1);
        Assert.Equal(100, u1!.InputTokens);
        Assert.Equal(50, u1.OutputTokens);
        Assert.Equal(1000, u1.CacheReadTokens);
        Assert.Equal(100, u1.CacheCreationTokens);
        Assert.Equal(1.0, c1!.Value, precision: 6);

        // Ход 2: prev=ход1 (та же csid), итог вырос → расход = только разница
        var second = ClaudeSession.BuildCostState(El(ResultJson(150, 80, 2000, 150, 2.0, "csid-A")));
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
        var baseState = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 1.0, "csid-A")));
        var repeat = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 1.0, "csid-A")));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(repeat, baseState);
        if (u is not null)
        {
            Assert.Equal(0, u.InputTokens);
            Assert.Equal(0, u.OutputTokens);
            Assert.Equal(0, u.CacheReadTokens);
            Assert.Equal(0, u.CacheCreationTokens);
        }
        Assert.Equal(0.0, c!.Value, precision: 6);
    }

    // 3. ТА ЖЕ csid и УМЕНЬШЕНИЕ (гонка двух процессов CLI одной сессии: фоновое продолжение,
    //    ре-аттемпт, ход-продолжение пишут cost-state вперемешку): дельта была бы отрицательной →
    //    max(0, cur − prev) по каждому полю, а НЕ весь снимок (недосчёт пары процентов честнее,
    //    чем пересчёт всей истории). Это и есть исправление ошибки постановки v1.
    [Fact]
    public void Дельта_ТаЖеCsid_Уменьшение_Ноль()
    {
        var prev = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 5.0, "csid-A")));
        // небольшое уменьшение итога (589,08M против 590,19M в артефакте)
        var cur = ClaudeSession.BuildCostState(El(ResultJson(99, 49, 999, 99, 4.9, "csid-A")));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // не весь снимок (99/49/999/99), а max(0, …) = 0
        Assert.NotNull(u);
        Assert.Equal(0, u!.InputTokens);
        Assert.Equal(0, u.OutputTokens);
        Assert.Equal(0, u.CacheReadTokens);
        Assert.Equal(0, u.CacheCreationTokens);
        Assert.Equal(0.0, c!.Value, precision: 6);
    }

    // 4. ДРУГАЯ csid (новый разговор/сброс/смена провайдера): накопленный итог начинается с нуля,
    //    дельта бессмысленна → расход = сам текущий снимок целиком.
    [Fact]
    public void Дельта_ДругаяCsid_ВесьСнимок()
    {
        var prev = ClaudeSession.BuildCostState(El(ResultJson(100, 50, 1000, 100, 5.0, "csid-A")));
        var cur = ClaudeSession.BuildCostState(El(ResultJson(40, 20, 300, 40, 2.0, "csid-B")));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // не разница, а сам текущий снимок целиком (40/20/300/40)
        Assert.NotNull(u);
        Assert.Equal(40, u!.InputTokens);
        Assert.Equal(20, u.OutputTokens);
        Assert.Equal(300, u.CacheReadTokens);
        Assert.Equal(40, u.CacheCreationTokens);
        Assert.Equal(2.0, c!.Value, precision: 6);
    }

    // 5. Доминирующая модель — по Дельте, а не по накопленному итогу: модель, которой ход
    //    реально потратил больше, и есть главная (субагенты могли считать другой моделью).
    [Fact]
    public void Дельта_ДоминирующаяМодельПоДельте()
    {
        var prevJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 900, "outputTokens": 900, "cacheReadInputTokens": 900, "cacheCreationInputTokens": 900},
                             "haiku":  {"inputTokens": 100, "outputTokens": 100, "cacheReadInputTokens": 100, "cacheCreationInputTokens": 100},
                             "session_id": "csid-A"},
             "total_cost_usd": 9.0}
            """;
        var curJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 950, "outputTokens": 950, "cacheReadInputTokens": 950, "cacheCreationInputTokens": 950},
                             "haiku":  {"inputTokens": 300, "outputTokens": 300, "cacheReadInputTokens": 300, "cacheCreationInputTokens": 300},
                             "session_id": "csid-A"},
             "total_cost_usd": 12.0}
            """;
        var prev = ClaudeSession.BuildCostState(El(prevJson));
        var cur = ClaudeSession.BuildCostState(El(curJson));
        var (u, _, model) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // дельта: sonnet +50 на счётчик, haiku +200 на счётчик → haiku больше
        Assert.NotNull(u);
        Assert.Equal("haiku", model);
    }

    // 6. Новая модель (в prev её не было): весь её вклад засчитывается (это не «уменьшение»).
    [Fact]
    public void Дельта_НоваяМодель_ВесьВклад()
    {
        var prevJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 100, "outputTokens": 100, "cacheReadInputTokens": 100, "cacheCreationInputTokens": 100},
                             "session_id": "csid-A"},
             "total_cost_usd": 1.0}
            """;
        var curJson = """
            {"modelUsage": {"sonnet": {"inputTokens": 150, "outputTokens": 150, "cacheReadInputTokens": 150, "cacheCreationInputTokens": 150},
                             "opus":   {"inputTokens": 500, "outputTokens": 500, "cacheReadInputTokens": 500, "cacheCreationInputTokens": 500},
                             "session_id": "csid-A"},
             "total_cost_usd": 3.0}
            """;
        var prev = ClaudeSession.BuildCostState(El(prevJson));
        var cur = ClaudeSession.BuildCostState(El(curJson));
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(cur, prev);
        // sonnet: дельта +50 на счётчик; opus: новый → весь вклад 500 на счётчик
        Assert.NotNull(u);
        Assert.Equal(550, u!.InputTokens);
        Assert.Equal(550, u.OutputTokens);
        Assert.Equal(550, u.CacheReadTokens);
        Assert.Equal(550, u.CacheCreationTokens);
        Assert.Equal(2.0, c!.Value, precision: 6); // 3.0 - 1.0
    }

    // 7. Снимок без modelUsage (сторонний поток, только usage) → расход = usage хода как есть,
    //    БЕЗ дельты (usage описывает последнюю итерацию и не копится), базу не трогаем.
    //    Это возврат фолбэка, потерянного в v1 (тест Дельта_БезModelUsage_NullUsage его узаконил).
    [Fact]
    public void ФолбэкUsage_БезModelUsage_КакЕсть()
    {
        // result без modelUsage, только usage (snake_case, как в result)
        var root = El("""{"usage": {"input_tokens": 10, "output_tokens": 20, "cache_read_input_tokens": 30, "cache_creation_input_tokens": 40}, "total_cost_usd": 0.5}""");
        var state = ClaudeSession.BuildCostState(root);
        // modelUsage нет → снимок пуст
        Assert.Empty(state.Models);
        // usage хода как есть (ParseUsage), не дельта
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(state, prev: null);
        Assert.Null(u); // ComputeTurnDelta на пустом снимке → null; вызывающий берёт ParseUsage
        // ParseUsage(root) — сам фолбэк, который ход применяет при пустом снимке
        var fallback = ClaudeSession.ParseUsage(root);
        Assert.NotNull(fallback);
        Assert.Equal(10, fallback!.InputTokens);
        Assert.Equal(20, fallback.OutputTokens);
        Assert.Equal(30, fallback.CacheReadTokens);
        Assert.Equal(40, fallback.CacheCreationTokens);
        Assert.Equal(0.5, c!.Value, precision: 6);
    }

    // 8. Форк: ветка наследует снимок родителя (SessionManager.BranchAsync) с ЧУЖОЙ csid, но это
    //    продолжение (транскрипт ветки — копия префикса родителя), а не смена csid.
    //    PrevCostStateForDelta нормализует csid базы на csid текущего снимка → дельта, не «весь снимок».
    [Fact]
    public void Форк_ПервыйХод_ДельтаАНеВесьСнимок()
    {
        var info = new Session
        {
            ClaudeSessionId = "branch-csid",
            BranchedFromSessionId = "parent-session",
            // унаследованный снимок родителя — с ЧУЖОЙ csid
            LastCostState = new SessionCostState
            {
                ClaudeSessionId = "parent-csid",
                TotalCostUsd = 5.0,
                Models = new Dictionary<string, ModelUsageSnapshot>
                {
                    ["claude-sonnet-5-5"] = new ModelUsageSnapshot
                    { InputTokens = 100, OutputTokens = 50, CacheReadInputTokens = 1000, CacheCreationInputTokens = 100 },
                },
            },
        };
        var session = new ClaudeSession(info, new LlmSessionContext(
            RootPath: "/tmp", OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: "",
            PermissionRules: null, TasksMcp: null));

        // текущий снимок ветки: та же csid-линия (накопленный итог чуть вырос), csid = branch-csid
        var current = ClaudeSession.BuildCostState(El(ResultJson(150, 80, 2000, 150, 6.0, "branch-csid")));
        // база после нормализации: csid = branch-csid (как у current) → ComputeTurnDelta считает дельту
        var prevForDelta = session.PrevCostStateForDelta(current);
        Assert.NotNull(prevForDelta);
        Assert.Equal("branch-csid", prevForDelta!.ClaudeSessionId);
        var (u, c, _) = ClaudeSession.ComputeTurnDelta(current, prevForDelta);
        // дельта (150-100=50, 80-50=30, 2000-1000=1000, 150-100=50), а не весь снимок (150/80/2000/150)
        Assert.NotNull(u);
        Assert.Equal(50, u!.InputTokens);
        Assert.Equal(30, u.OutputTokens);
        Assert.Equal(1000, u.CacheReadTokens);
        Assert.Equal(50, u.CacheCreationTokens);
        Assert.Equal(1.0, c!.Value, precision: 6); // 6.0 - 5.0
    }

    // 9. Парсинг cost-state-записи транскрипта (camelCase: sessionId/totalCostUSD, в отличие от
    //    result: session_id/total_cost_usd). Используется для засева базы (SeedTurnBaseFromTranscript).
    [Fact]
    public void Транскрипт_ПарсингCostState()
    {
        var record = El("""
            {"type":"cost-state","sessionId":"ea1192f3","totalCostUSD":0.475,
             "modelUsage":{"claude-opus-5-5[1m]":{"inputTokens":512,"outputTokens":299,
               "thinkingTokens":90,"cacheReadInputTokens":161613,"cacheCreationInputTokens":54350,
               "webSearchRequests":0,"costUSD":0.475}},
             "hasUnknownModelCost":false}
            """);
        var state = ClaudeSession.BuildCostStateFromTranscript(record);
        Assert.Equal("ea1192f3", state.ClaudeSessionId);
        Assert.Equal(0.475, state.TotalCostUsd!.Value, precision: 6);
        Assert.True(state.Models.ContainsKey("claude-opus-5-5[1m]"));
        var m = state.Models["claude-opus-5-5[1m]"];
        Assert.Equal(512, m.InputTokens);
        Assert.Equal(299, m.OutputTokens);
        Assert.Equal(161613, m.CacheReadInputTokens);
        Assert.Equal(54350, m.CacheCreationInputTokens);
    }
}
