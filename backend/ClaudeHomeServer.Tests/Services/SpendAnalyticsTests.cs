using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Tests.Services;

// Ключевая логика аналитики расхода: rollup и границы детального окна (SpendStore),
// авторизация admin/mine (SpendAccess), группировка pivot (GroupRaw), дедуп backfill (Spread)
public class SpendAnalyticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendStore NewStore(int detailDays = 30) => new(_dir, detailDays);

    private static SpendRecord Rec(DateTime ts, string owner = "u1", string? project = "p1",
        string? session = "s1", string provider = "claude", string? model = "opus",
        string source = SpendSources.ChatTurn, long input = 10, long output = 5,
        long cacheRead = 100, long cacheCreate = 20, int generations = 0, string? id = null) => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Timestamp = ts,
            OwnerId = owner,
            ProjectId = project,
            SessionId = session,
            Provider = provider,
            Model = model,
            Source = source,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreate,
            Generations = generations,
        };

    // --- rollup и границы окна ---

    [Fact]
    public void Rollup_СтарыеДниСворачиваются_ДеньНаГраницеОстаётсяДетальным()
    {
        var store = NewStore();
        var cutoff = new DateOnly(2026, 7, 10);
        var oldDay = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        var edgeDay = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        store.Record(Rec(oldDay));
        store.Record(Rec(oldDay, model: "sonnet"));
        store.Record(Rec(edgeDay));

        store.RollupOlderThan(cutoff);

        // Старый день ушёл в агрегаты (по строке на модель), детали удалены вместе с jsonl
        var daily = store.DailyBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5));
        Assert.Equal(2, daily.Count);
        Assert.Empty(store.DetailsBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5)));
        Assert.False(File.Exists(Path.Combine(_dir, "turns-2026-07-05.jsonl")));
        Assert.True(store.IsAggregated(new DateOnly(2026, 7, 5)));

        // День ровно на cutoff — строго старше не является, остаётся детальным
        Assert.Single(store.DetailsBetween(cutoff, cutoff));
        Assert.False(store.IsAggregated(cutoff));
    }

    [Fact]
    public void Rollup_Идемпотентен_ПовторНеДублирует()
    {
        var store = NewStore();
        var oldDay = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(oldDay));
        var cutoff = new DateOnly(2026, 7, 10);

        store.RollupOlderThan(cutoff);
        store.RollupOlderThan(cutoff);

        var daily = store.DailyBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5));
        var row = Assert.Single(daily);
        Assert.Equal(1, row.Turns);
        Assert.Equal(10, row.InputTokens);
        Assert.Equal(135, row.InputTokens + row.OutputTokens + row.CacheReadTokens + row.CacheCreationTokens);
    }

    [Fact]
    public void Store_ПереживаетПерезапуск_ДеталиИАгрегаты()
    {
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var oldTs = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = NewStore();
        store.Record(Rec(ts));
        store.Record(Rec(oldTs));
        store.RollupOlderThan(new DateOnly(2026, 7, 10));

        var reloaded = NewStore();
        Assert.Single(reloaded.DetailsBetween(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20)));
        Assert.Single(reloaded.DailyBetween(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void Record_ПустаяЗаписьОтбрасывается()
    {
        var store = NewStore();
        store.Record(new SpendRecord { Timestamp = DateTime.UtcNow });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Empty(store.DetailsBetween(today, today));
    }

    [Fact]
    public void Aggregate_СуммируетПоСоставномуКлючу()
    {
        var day = new DateTime(2026, 7, 5, 10, 0, 0, DateTimeKind.Utc);
        var rows = SpendStore.Aggregate("2026-07-05",
        [
            Rec(day), Rec(day), Rec(day, model: "sonnet"),
            Rec(day, source: SpendSources.Fal, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, generations: 1),
        ]);
        Assert.Equal(3, rows.Count);
        var opus = rows.Single(r => r.Model == "opus" && r.Source == SpendSources.ChatTurn);
        Assert.Equal(2, opus.Turns);
        Assert.Equal(20, opus.InputTokens);
        Assert.Equal(1, rows.Single(r => r.Source == SpendSources.Fal).Generations);
    }

    // --- авторизация admin/mine ---

    [Fact]
    public void Access_НеАдмин_ScopeAll_Запрещён()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: "all",
            null, null, null, null, null, null, null, null);
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void Access_НеАдмин_ЧужойUserФильтр_Запрещён()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: null,
            user: "u2", null, null, null, null, null, null, null);
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void Access_НеАдмин_РазрезUser_Запрещён()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: null,
            null, null, null, null, null, null, null, null, groupBy: "user");
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void Access_НеАдмин_ВладелецПринудительный()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: null,
            null, project: "p1", null, null, null, null, null, null);
        Assert.Null(res.Error);
        Assert.False(res.AllUsers);
        Assert.Equal("u1", res.Filter.Owner);
        Assert.Equal("p1", res.Filter.Project);
    }

    [Fact]
    public void Access_Админ_ScopeAll_БезВладельца_ИСужениеФильтром()
    {
        var all = SpendAccess.Resolve(isAdmin: true, "admin", scope: "all",
            null, null, null, null, null, null, null, null);
        Assert.Null(all.Error);
        Assert.True(all.AllUsers);
        Assert.Null(all.Filter.Owner);

        var narrowed = SpendAccess.Resolve(isAdmin: true, "admin", scope: "all",
            user: "u2", null, null, null, null, null, null, null);
        Assert.Equal("u2", narrowed.Filter.Owner);

        // Админ без scope=all — тот же режим «моё», что у всех
        var mine = SpendAccess.Resolve(isAdmin: true, "admin", scope: null,
            null, null, null, null, null, null, null, null);
        Assert.Equal("admin", mine.Filter.Owner);
        Assert.False(mine.AllUsers);
    }

    // --- группировка pivot ---

    [Fact]
    public void Pivot_ГруппируетПоРазрезуИСортируетПоОбъёму()
    {
        var d = new DateOnly(2026, 7, 20);
        List<SpendSlice> slices =
        [
            new(d, "u1", "p1", "s1", null, null, "claude", "opus", SpendSources.ChatTurn,
                100, 50, 0, 0, 0, 0, 1, Detailed: true),
            new(d, "u1", "p1", "s2", null, null, "claude", "opus", SpendSources.ChatTurn,
                10, 5, 0, 0, 0, 0, 1, Detailed: true),
            // Свёрнутый день той же модели и день другой модели
            new(d.AddDays(-40), "u1", "p1", "s1", null, null, "claude", "opus", SpendSources.ChatTurn,
                1000, 0, 0, 0, 0, 0, 7, Detailed: false),
            new(d, "u1", "p1", "s1", null, null, "deepseek", "deepseek-chat", SpendSources.ChatTurn,
                1, 1, 0, 0, 0, 0, 1, Detailed: true),
        ];

        var byModel = SpendAnalyticsService.GroupRaw(slices, "model");
        Assert.Equal(2, byModel.Count);
        Assert.Equal("opus", byModel[0].Key); // крупнее — первым
        Assert.Equal(1165, byModel[0].Tokens.Total);
        Assert.Equal(9, byModel[0].Turns);
        Assert.True(byModel[0].HasDetail);

        var byChat = SpendAnalyticsService.GroupRaw(slices, "chat");
        Assert.Equal(2, byChat.Count);
        Assert.Equal("s1", byChat[0].Key);
    }

    // --- резолв дефолтной модели (SpendRecord.Model никогда не пустой) ---

    [Fact]
    public void Pivot_ДефолтнаяМодель_ГруппируетсяБезПустогоКлюча()
    {
        // После резолва null-модели в точке записи slice всегда несёт конкретный id
        // (дефолт подписки "default" или id модели провайдера) — пустого ключа и группы
        // «Модель по умолчанию» в pivot быть не должно.
        var d = new DateOnly(2026, 7, 20);
        List<SpendSlice> slices =
        [
            new(d, "u1", "p1", "s1", null, null, "claude", "default", SpendSources.ChatTurn,
                100, 50, 0, 0, 0, 0, 1, Detailed: true),
            new(d, "u1", "p1", "s2", null, null, "claude", "default", SpendSources.ChatTurn,
                10, 5, 0, 0, 0, 0, 1, Detailed: true),
            new(d, "u1", "p1", "s3", null, null, "claude", "opus", SpendSources.ChatTurn,
                5, 5, 0, 0, 0, 0, 1, Detailed: true),
        ];

        var byModel = SpendAnalyticsService.GroupRaw(slices, "model");

        Assert.Equal(2, byModel.Count);
        Assert.DoesNotContain(byModel, n => n.Key.Length == 0); // нет пустой «Модели по умолчанию»
        var def = byModel.Single(n => n.Key == "default");
        Assert.Equal(2, def.Turns);
        Assert.Equal(165, def.Tokens.Total);
    }

    // --- классификация источников ---

    [Fact]
    public void Sources_БесплатныеИПодписки()
    {
        Assert.True(SpendSources.IsFree("ollama", "qwen3:14b"));
        Assert.True(SpendSources.IsFree("openrouter-direct", "any"));
        Assert.True(SpendSources.IsFree("freellmapi-direct", "auto:fast"));
        Assert.True(SpendSources.IsFree("openrouter", "nvidia/nemotron:free"));
        Assert.False(SpendSources.IsFree("claude", "opus"));
        Assert.Equal("claude", SpendSources.NormalizeProvider("sub-work"));
        Assert.Equal("deepseek", SpendSources.NormalizeProvider("deepseek"));
    }

    // --- дедуп backfill ---

    [Fact]
    public void Record_ПовторныйIdОтбрасывается_ИПослеПерезапускаСтора()
    {
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var day = new DateOnly(2026, 7, 20);
        var id = SpendMaintenanceService.BackfillId("cs1", 0);

        var store = NewStore();
        store.Record(Rec(ts, id: id));
        store.Record(Rec(ts, id: id));
        Assert.Single(store.DetailsBetween(day, day));

        // Перезапуск: индекс Id восстанавливается из jsonl — дубль не проходит и после него
        var reloaded = NewStore();
        reloaded.Record(Rec(ts, id: id));
        Assert.Single(reloaded.DetailsBetween(day, day));

        // Запись с другим Id — не дубль
        reloaded.Record(Rec(ts, id: SpendMaintenanceService.BackfillId("cs1", 1)));
        Assert.Equal(2, reloaded.DetailsBetween(day, day).Count);
    }

    [Fact]
    public void BackfillId_ДетерминированПоСессииИИндексу()
    {
        Assert.Equal(SpendMaintenanceService.BackfillId("cs1", 0), SpendMaintenanceService.BackfillId("cs1", 0));
        Assert.NotEqual(SpendMaintenanceService.BackfillId("cs1", 0), SpendMaintenanceService.BackfillId("cs1", 1));
        Assert.NotEqual(SpendMaintenanceService.BackfillId("cs1", 0), SpendMaintenanceService.BackfillId("cs2", 0));
        // Формат совпадает с Guid.ToString("N") живых записей
        Assert.Equal(32, SpendMaintenanceService.BackfillId("cs1", 0).Length);
    }

    [Fact]
    public void Spread_РаспределяетВнутриИнтервалаИНеПересекаетT0()
    {
        var from = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

        var first = SpendMaintenanceService.Spread(from, to, 0, 3);
        var last = SpendMaintenanceService.Spread(from, to, 2, 3);
        Assert.True(first > from && last < to);
        Assert.True(first < last);

        // Вырожденные случаи: пустой интервал → from (и гарантированно < t0 при t0 > from)
        Assert.Equal(from, SpendMaintenanceService.Spread(from, from, 0, 5));
    }
}
