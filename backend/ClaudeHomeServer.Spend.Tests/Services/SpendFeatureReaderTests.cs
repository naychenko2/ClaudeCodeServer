using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Шов расхода по фиче (ISpendFeatureReader, волна 3, шаг Б9a): срез «задача фичи ИЛИ
// чат фичи», защита в глубину по владельцу, деньги по решению Андрея 20.10 (free —
// надёжно 0, ненадёжная цена CLI — токены в сумму, стоимость нет), без двойного счёта
// деталей и дневных агрегатов.
public class SpendFeatureReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-feature-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendStore NewStore(int detailDays = 30) => new(_dir, detailDays);

    private SpendFeatureReader NewReader(SpendStore store, string[]? unreliable = null) =>
        new(store, unreliable);

    private static SpendRecord Rec(DateTime ts, string owner = "u1", string? task = "t1",
        string? session = "s1", string? persona = "p1", string? model = "claude-opus-4",
        string source = SpendSources.ChatTurn, double? cost = 1.0,
        long input = 10, long output = 5, long cacheRead = 100, long cacheCreate = 20) => new()
    {
        Timestamp = ts,
        OwnerId = owner,
        TaskId = task,
        SessionId = session,
        PersonaId = persona,
        Model = model,
        Source = source,
        CostUsd = cost,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheCreate,
    };

    private static long TotalTokens(FeatureSpendTotals t) =>
        t.InputTokens + t.OutputTokens + t.CacheReadTokens + t.CacheCreationTokens;

    // --- владелец: защита в глубину ---

    [Fact]
    public void Totals_ЧужойВладелец_НеПопадает()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, owner: "u1"));
        store.Record(Rec(ts, owner: "u2"));

        var totals = NewReader(store).Totals("u1", ["t1"], ["s1"], null);

        // Только запись u1: 10+5+100+20 = 135; без фильтра владельца было бы 270.
        Assert.Equal(135, TotalTokens(totals));
    }

    // --- деньги: free надёжно 0, ненадёжная цена скрывается ---

    [Fact]
    public void Totals_FreeМодель_СтоимостьНуль_ДажеЕслиСыраяЗаписьНесётЦену()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, model: "qwen3:8b", source: SpendSources.Free, cost: 5.0));

        var totals = NewReader(store).Totals("u1", [], ["s1"], null);

        Assert.Equal(0, totals.CostUsd);
        Assert.False(totals.CostPartial);
        Assert.Empty(totals.HiddenCostModels);
    }

    [Fact]
    public void Totals_НенадёжнаяЦена_ТокеныВСумму_СтоимостьНеВходит_МодельВHidden()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, model: "deepseek-chat", cost: 2.5));
        store.Record(Rec(ts, model: "glm-4.5", cost: 3.0));
        store.Record(Rec(ts, model: "claude-opus-4", cost: 1.0));

        var totals = NewReader(store).Totals("u1", [], ["s1"], null);

        // Все токены в сумме (3 × 135), в деньги — только надёжная запись (1.0).
        Assert.Equal(405, TotalTokens(totals));
        Assert.Equal(1.0, totals.CostUsd);
        Assert.True(totals.CostPartial);
        Assert.Equal(["deepseek-chat", "glm-4.5"], totals.HiddenCostModels);
    }

    [Fact]
    public void Totals_КонфигUnreliableCostModels_ЗаменяетВстроенныйДефолт()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, model: "deepseek-chat", cost: 2.5));
        store.Record(Rec(ts, model: "kimi-k2", cost: 0.5));
        store.Record(Rec(ts, model: "claude-opus-4", cost: 1.0));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spend:UnreliableCostModels:0"] = "kimi",
            })
            .Build();
        var reader = new SpendFeatureReader(store, config);

        var totals = reader.Totals("u1", [], ["s1"], null);

        // Конфиг заменил дефолт: deepseek теперь «надёжен» (2.5 в сумму), kimi — нет.
        Assert.Equal(3.5, totals.CostUsd);
        Assert.True(totals.CostPartial);
        Assert.Equal(["kimi-k2"], totals.HiddenCostModels);
    }

    // --- срез: задача ИЛИ чат, оба фильтра, граница from ---

    [Fact]
    public void Totals_Срез_ЗадачаИлиЧат_ЧужаяФичаНеПопадает()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        // Исполнитель фичи: задача t1 + чат s1.
        store.Record(Rec(ts, task: "t1", session: "s1"));
        // Оркестратор фичи: задачи нет, чат s0.
        store.Record(Rec(ts, task: null, session: "s0"));
        // Чужая фича: задача t9, чат s9.
        store.Record(Rec(ts, task: "t9", session: "s9"));

        var totals = NewReader(store).Totals("u1", ["t1"], ["s0"], null);

        // Ход t1 (по задаче) + ход s0 (по чату); чужая фича t9/s9 — нет.
        Assert.Equal(270, TotalTokens(totals));
    }

    [Fact]
    public void Totals_ОбаНабораПустые_СрезПуст()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts));

        var totals = NewReader(store).Totals("u1", [], [], null);

        Assert.Equal(0, TotalTokens(totals));
        Assert.Null(totals.From);
    }

    [Fact]
    public void Totals_ГраницаFrom_ЗаписиДоНеПопадают_ОтметкаFrom()
    {
        var store = NewStore();
        var before = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(before));
        store.Record(Rec(at));
        store.Record(Rec(after));

        var from = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var totals = NewReader(store).Totals("u1", [], ["s1"], from);

        // Граница включительная: at + after, before — нет.
        Assert.Equal(270, TotalTokens(totals));
        Assert.Equal(new DateOnly(2026, 10, 1), totals.From);
    }

    // --- детали + дневные агрегаты: без двойного счёта, From помечает данные ---

    [Fact]
    public void Totals_ДеньВДеталяхиВАгрегатах_СчитаетсяОдинРаз()
    {
        var store = NewStore();
        var oldTs = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        var recentTs = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(oldTs));
        store.Record(Rec(recentTs));
        // День 2026-07-05 старше окна (30 дней) — свёрнут в дневной агрегат.
        store.RollupOlderThan(new DateOnly(2026, 9, 1));

        var totals = NewReader(store).Totals("u1", [], ["s1"], null);

        // Две записи (одна детальная, одна свёрнутая) = 270 токенов, без двойного счёта.
        Assert.Equal(270, TotalTokens(totals));
        // From — самая ранняя дата, с которой в срезе есть данные.
        Assert.Equal(new DateOnly(2026, 7, 5), totals.From);
    }

    [Fact]
    public void Totals_ФичаСтаршеДанных_FromПомечаетНачалоДанных()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts));

        // from задан раньше, чем есть данные: From — не from, а дата первой записи.
        var from = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var totals = NewReader(store).Totals("u1", [], ["s1"], from);

        Assert.Equal(new DateOnly(2026, 10, 1), totals.From);
    }

    // --- Records: данные для шага Б9c (деление расхода оркестратора) ---

    [Fact]
    public void Records_ОтдаётЗаписиПоХодам_СоВсёПолямиДляБ9c()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, task: "t1", session: "s1", persona: "p1", model: "claude-opus-4", cost: 1.0));
        store.Record(Rec(ts, task: null, session: "s0", persona: "p2", model: "deepseek-chat", cost: 2.0));

        var records = NewReader(store).Records("u1", ["t1"], ["s0"], null);

        // Оба хода в срезе (t1 по задаче, s0 по чату), порядок по времени.
        Assert.Equal(2, records.Count);
        var r0 = records[0];
        Assert.Equal("s1", r0.SessionId);
        Assert.Equal("t1", r0.TaskId);
        Assert.Equal("p1", r0.PersonaId);
        Assert.Equal("claude-opus-4", r0.Model);
        Assert.Equal(10, r0.InputTokens);
        Assert.Equal(5, r0.OutputTokens);
        Assert.Equal(100, r0.CacheReadTokens);
        Assert.Equal(20, r0.CacheCreationTokens);
        Assert.Equal(1.0, r0.CostUsd);
        Assert.Equal(SpendSources.ChatTurn, r0.Source);
        var r1 = records[1];
        Assert.Equal("s0", r1.SessionId);
        Assert.Null(r1.TaskId);
        Assert.Equal("deepseek-chat", r1.Model);
    }

    // --- минор Б9b: отрицательные/нулевые токены не дают отрицательных сумм ---

    [Fact]
    public void Totals_ОтрицательныеТокены_КлампВНоль_СуммаНеВМинус()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        // Запись с отрицательным входом/выходом (сбой CLI) и одна нормальная.
        store.Record(Rec(ts, input: -50, output: -10));
        store.Record(Rec(ts, input: 10, output: 5));

        var totals = NewReader(store).Totals("u1", ["t1"], ["s1"], null);

        // Отрицательный вклад кламится в 0: итог = только нормальная запись (10+5), не в минус.
        Assert.Equal(10, totals.InputTokens);
        Assert.Equal(5, totals.OutputTokens);
    }
}
