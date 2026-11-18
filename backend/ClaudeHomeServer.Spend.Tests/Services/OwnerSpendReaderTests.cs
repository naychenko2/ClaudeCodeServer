using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Spend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Шва чтения расхода владельца (IOwnerSpendReader, Core; реализация OwnerSpendReader,
// шаг KM-B5a): «сегодня» и «месяц» по ТАЙМЗОНЕ вызывающего, а не по UTC; ключ — ownerId;
// нет двойного счёта деталей и дневных агрегатов; складывается только USD (рублей/кредиты
// — отдельные валюты); чтение под блокировкой стора без «рваной» суммы.
//
// «Сейчас» читателя — заменяемый час (internal-конструктор с Func<DateTimeOffset>):
// границы дня/месяца проверяются на фиксированных instant, детерминированно в любой
// момент прогона (тот же приём, что QuietHttpLogger/ClaudeCliUpdateWatcher).
public class OwnerSpendReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-owner-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendStore NewStore(int detailDays = 30) => new(_dir, detailDays);

    private static Func<DateTimeOffset> ClockAt(int y, int month, int day, int hourUtc) =>
        () => new DateTimeOffset(y, month, day, hourUtc, 0, 0, TimeSpan.Zero);

    private OwnerSpendReader NewReader(SpendStore store, Func<DateTimeOffset>? clock = null) =>
        new(store, clock);

    private static SpendRecord Rec(DateTime ts, string owner = "u1",
        string source = SpendSources.ChatTurn, double? cost = 1.0, double? costRub = null,
        long input = 10, int generations = 0) => new()
    {
        Timestamp = ts,
        OwnerId = owner,
        Source = source,
        CostUsd = cost,
        CostRub = costRub,
        InputTokens = input,
        Generations = generations,
    };

    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    // --- владелец: ключ чтения ---

    [Fact]
    public void TodayUsd_ЧужойВладелец_НеПопадает()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, owner: "u1", cost: 1.0));
        store.Record(Rec(ts, owner: "u2", cost: 5.0));

        // «Сейчас» того же дня, что и запись: u1 — 1.00; без фильтра владельца было бы 6.00.
        var reader = NewReader(store, ClockAt(2026, 11, 17, 12));

        reader.TodayUsd("u1", TimeZoneInfo.Utc).Should().Be(1.00m);
    }

    // --- таймзона, а не UTC (критерий c1) ---
    // «Сейчас» = 17.11 22:00 UTC = 18.11 01:00 МСК: в МСК сегодня — 18-е, в UTC — 17-е.
    // Запись 20:30 UTC = 23:30 МСК 17-го («вчера» по МСК, «сегодня» по UTC) — не входит.
    // Запись 21:30 UTC = 00:30 МСК 18-го («сегодня» по МСК, «вчера» по UTC-границам чтения)
    // — входит: без расширения UTC-окна на ±1 день её вообще не прочитать.
    // Наивная UTC-реализация дала бы 3.00 (обе записи «сегодня» по UTC) — тест краснеет.
    [Fact]
    public void TodayUsd_ГраницаДня_ПоТаймзонеАНеПоUTC()
    {
        var store = NewStore();
        // 23:30 МСК «вчера» (17.11 20:30 UTC) — чужой локальный день.
        store.Record(Rec(new DateTime(2026, 11, 17, 20, 30, 0, DateTimeKind.Utc), cost: 1.0));
        // 00:30 МСК «сегодня» (17.11 21:30 UTC) — сегодняшний локальный день.
        store.Record(Rec(new DateTime(2026, 11, 17, 21, 30, 0, DateTimeKind.Utc), cost: 2.0));

        var reader = NewReader(store, ClockAt(2026, 11, 17, 22));

        reader.TodayUsd("u1", Moscow).Should().Be(2.00m);
    }

    // --- месяц: календарный месяц ТАЙМЗОНЫ, границы — по локальной дате записи ---
    // Фиксированные даты середины месяца: «текущий месяц» не зависит от момента прогона
    // ни в какой реальной таймзоне (смещение ≤ 14 ч). Две пограничные записи проверяют
    // оба направления: 31.10 23:30 UTC = 01.11 02:30 МСК — октябрь по UTC, НО ноябрь по
    // МСК; 30.11 23:30 UTC = 30.11 по UTC, НО 01.12 02:30 МСК — ноябрь по UTC, не по МСК.
    [Fact]
    public void MonthUsd_ГраницыМесяца_ПоЛокальнойДатеЗаписи()
    {
        var store = NewStore();
        store.Record(Rec(new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc), cost: 1.0));   // середина месяца — в обеих
        store.Record(Rec(new DateTime(2026, 11, 1, 0, 30, 0, DateTimeKind.Utc), cost: 0.5));    // 01.11 00:30 UTC = 03:30 МСК — в обеих
        store.Record(Rec(new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Utc), cost: 5.0));  // октябрь по UTC, ноябрь по МСК
        store.Record(Rec(new DateTime(2026, 11, 30, 23, 30, 0, DateTimeKind.Utc), cost: 2.0));  // ноябрь по UTC, декабрь по МСК
        store.Record(Rec(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), cost: 7.0));   // глубина прошлого месяца — ни в какой

        var reader = NewReader(store, ClockAt(2026, 11, 15, 12));

        // Ноябрь по UTC: 1.00 + 0.50 + 2.00; 31.10 (октябрь) и 15.10 — нет. Без month-фильтра — 15.50.
        reader.MonthUsd("u1", TimeZoneInfo.Utc).Should().Be(3.50m);
        // Ноябрь по МСК: 1.00 + 0.50 + 5.00; записная 30.11 23:30 UTC — уже 01.12 МСК, вне месяца.
        // Та же сумма по UTC была бы 3.50 — месяц режется локальной датой, а не UTC-датой.
        reader.MonthUsd("u1", Moscow).Should().Be(6.50m);
    }

    // --- нет двойного счёта: свёрнутый день учитывается один раз через агрегаты ---
    [Fact]
    public void MonthUsd_СвёрнутыйДень_УчитываетсяОдинРаз()
    {
        var store = NewStore();
        store.Record(Rec(new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc), cost: 1.0));
        store.Record(Rec(new DateTime(2026, 11, 15, 13, 0, 0, DateTimeKind.Utc), cost: 0.5));
        // Свернуть всё (дни строго до 16.11) в дневные агрегаты — детали удалены.
        store.RollupOlderThan(new DateOnly(2026, 11, 16));

        var reader = NewReader(store, ClockAt(2026, 11, 15, 12));

        // 1.50 ровно один раз: без чтения агрегатов — 0.00, с двойным счётом — 3.00.
        reader.MonthUsd("u1", TimeZoneInfo.Utc).Should().Be(1.50m);
    }

    // --- деньги: только USD; Source не фильтруется; битая цена не ломает шву ---
    [Fact]
    public void TodayUsd_ТесЭсВСумме_РублиИБитаяЦена_Нет()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, cost: 1.0));                                        // обычный ход
        store.Record(Rec(ts, source: SpendSources.Tts, cost: 0.5,
            costRub: 10.0, input: 0, generations: 1));                         // TTS: тот же стор, Source не фильтр
        store.Record(Rec(ts, cost: null, input: 100));                         // ход без цены — 0, не пропуск записи
        store.Record(Rec(ts, cost: double.NaN, costRub: 3.0, input: 0, generations: 1)); // битая цена — 0, не исключение

        var reader = NewReader(store, ClockAt(2026, 11, 17, 12));

        // 1.00 + 0.50; рублей (10.0 + 3.0) и NaN в USD нет. Без IsFinite — OverflowException на (decimal)NaN.
        reader.TodayUsd("u1", TimeZoneInfo.Utc).Should().Be(1.50m);
    }

    // --- гонка (критерий c2): 100 параллельных Record по 0,01 и чтения — итог ровно 1,00 ---
    // Чтение идёт через DetailsBetween — под тем же lock(list) дня, под которым пишет
    // Record(): каждая промежуточная сумма — сумма целых записей, монотонно неубывающая;
    // «рваная» запись (видна половина) невозможна. Итог точный: decimal-аккумуляция
    // 100 × 0.01 = 1.00 ровно (double-аккумуляция дала бы 1.0000000000000002).
    [Fact]
    public async Task TodayUsd_DuringConcurrentRecord_IsMonotonic()
    {
        var store = NewStore();
        var reader = NewReader(store, ClockAt(2026, 11, 17, 12));
        var ts = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        var all = Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => store.Record(Rec(ts, cost: 0.01)))));

        // Горячий цикл чтения без паузы: чтения идут всё время записи — окно пересечения
        // с незащищённой записью ловится чаще (без паузы тест мутации «без lock» зелёный).
        var readings = new List<decimal>();
        while (!all.IsCompleted)
            readings.Add(reader.TodayUsd("u1", TimeZoneInfo.Utc));
        await all;
        var final = reader.TodayUsd("u1", TimeZoneInfo.Utc);

        readings.Should().NotBeEmpty("чтения во время записи — суть теста");
        for (var i = 1; i < readings.Count; i++)
            readings[i].Should().BeGreaterThanOrEqualTo(readings[i - 1],
                "сумма во время записи не убывает: чтение под блокировкой стора, без «рваной» суммы");
        readings.Should().OnlyContain(v => v <= 1.00m, "ни одна промежуточная сумма не больше итога");
        final.Should().Be(1.00m);
    }

    // --- регистрация: активная подсистема — шва есть; выключенная — швы нет (законно) ---

    private static ServiceCollection BuildServices(bool spendEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                ["Subsystems:spend:Enabled"] = spendEnabled ? "true" : "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSubsystems(config, new SpendSubsystem());
        return services;
    }

    [Fact]
    public void Register_ПодсистемаАктивна_ШваЗарегистрирована()
    {
        BuildServices(true).Should().Contain(s => s.ServiceType == typeof(IOwnerSpendReader));
    }

    [Fact]
    public void Register_ПодсистемаВыключена_ШвыНет()
    {
        // Выключенная подсистема не зовёт Register: швы нет, потребитель (KM-B5c) резолвит
        // null и решает «бюджет не проверить» (fail-closed) — это законное состояние.
        var services = BuildServices(false);
        services.Should().NotContain(s => s.ServiceType == typeof(IOwnerSpendReader));
        using var sp = services.BuildServiceProvider();
        sp.GetService<IOwnerSpendReader>().Should().BeNull();
    }
}
