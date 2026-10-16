using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты поллера метрик локального стенда: фейковый источник метрик + фейковые часы
/// (ManualTime, без Task.Delay). Проверяются: разрыв на рестарте счётчика, фиксация
/// DownSince на первом сбое (не сбрасывается повторными), возврат к working,
/// Enabled=false — ни одного обращения к источнику, кольцо не растёт выше окна.
/// </summary>
public class LocalModelStatsPollerTests
{
    // Фейковые часы: время двигает тест, а не Task.Delay.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // IOptionsMonitor с переключаемым значением («выключение без рестарта»).
    private sealed class FakeOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Фейковый источник метрик: тест задаёт очередь снимков; ReadAsync дёргает счётчик.
    private sealed class FakeSource : IEngineMetricsSource
    {
        public Queue<EngineSnapshot?> Snapshots { get; } = new();
        public int ReadCalls { get; private set; }
        public Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
        {
            ReadCalls++;
            var snap = Snapshots.Count > 0 ? Snapshots.Dequeue() : null;
            return Task.FromResult(snap);
        }
    }

    // Заглушка IHttpClientFactory: в тестах источник подменяется, HttpClient не создаётся.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
        public HttpClient CreateClient(Type type, string name) => new();
    }

    // Управляемое ожидание: каждая итерация цикла блокируется, пока тест не вызовет Release().
    // Позволяет гонять ExecuteAsync (StartAsync) на фейковых часах, без реального Task.Delay:
    // тест «продвигает» цикл итерациями, а не календарём.
    private sealed class GateWait
    {
        private TaskCompletionSource<bool>? _gate;
        private TaskCompletionSource<bool>? _pendingBlocked; // ставится Release, завершается следующим Wait
        private readonly object _sync = new();

        public Task Wait(TimeSpan t, CancellationToken ct)
        {
            lock (_sync)
            {
                _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // новое ожидание создано: если тест ждал «следующий блок» — разрешаем его
                _pendingBlocked?.TrySetResult(true);
                if (ct.IsCancellationRequested)
                    _gate.TrySetResult(true);
                else
                    ct.Register(() => _gate.TrySetResult(true));
                return _gate.Task;
            }
        }

        // Разрешает текущее ожидание и возвращает Task, завершающийся, когда цикл
        // сделает итерацию и создаст НОВОЕ ожидание (т.е. снова заблокируется).
        public Task Release()
        {
            lock (_sync)
            {
                _pendingBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var wait = _pendingBlocked.Task;
                _gate?.TrySetResult(true);
                return wait;
            }
        }
    }

    private sealed class Harness
    {
        public ManualTime Time { get; } = new();
        public FakeOptionsMonitor<LocalModelStatsOptions> Options { get; }
        public FakeSource Source { get; } = new();
        public LocalModelStatsPoller Poller { get; }

        public Harness(bool enabled = true)
        {
            Options = new FakeOptionsMonitor<LocalModelStatsOptions>(
                new LocalModelStatsOptions { Enabled = enabled });
            var registry = new LlmProviderRegistry(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(Path.GetTempPath(), "lms_" + Guid.NewGuid().ToString("N"), "projects.json"),
                    ["LlmProviders:local-qwen:IsLocal"] = "true",
                    ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
                    ["LlmProviders:local-qwen:Stats:Engine"] = "vllm",
                    ["LlmProviders:local-qwen:Stats:MetricsUrl"] = "http://127.0.0.1:18020/metrics",
                    ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
                }).Build());
            Poller = new LocalModelStatsPoller(
                Options, registry,
                new FakeHttpClientFactory(), // не используется: SourceFactoryOverride подменяет источник
                Time,
                NullLogger<LocalModelStatsPoller>.Instance);
            // Фейковый источник: подменяем фабрику, чтобы не ходить в реальную сеть.
            Poller.SourceFactoryOverride = () => (Source, "vllm", "qwen3.8-27b", null, true);
        }
    }

    // (1) Разрыв на рестарте счётчика: отрицательная дельта generation_tokens → точка с null.
    [Fact]
    public async Task Ring_NegativeCounterDelta_ProducesNullThroughputPoint()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000, Running = 1 });
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 2000, Running = 1 });
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 500, Running = 0 }); // рестарт: счётчик упал

        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history.Should().HaveCount(3);
        // Первая точка: нет предыдущей → null. Вторая: (2000-1000)/5 = 200. Третья: рестарт → null.
        history[0].GenThroughput.Should().BeNull();
        history[1].GenThroughput.Should().Be(200.0);
        history[2].GenThroughput.Should().BeNull("отрицательная дельта (рестарт движка) = разрыв, а не отрицательная скорость");
    }

    // (2) DownSince фиксируется на ПЕРВОМ сбое и не сбрасывается повторными сбоями.
    [Fact]
    public async Task DownSince_FixedOnFirstFailure_NotResetByRepeatedFailures()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(null); // сбой 1
        h.Source.Snapshots.Enqueue(null); // сбой 2
        h.Source.Snapshots.Enqueue(null); // сбой 3

        await h.Poller.PollOnceAsync();
        var firstDownSince = h.Poller.Current.DownSince;
        firstDownSince.Should().Be(h.Time.GetUtcNow());

        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Status.Should().Be("down");
        h.Poller.Current.DownSince.Should().Be(firstDownSince, "повторные сбои не двигают момент ПЕРВОГО сбоя");
    }

    // (3) Возврат к working: после сбоя успешный снимок → status=working, DownSince=null.
    [Fact]
    public async Task Recovery_AfterFailure_ReturnsToWorkingAndClearsDownSince()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(null); // сбой
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 0, Waiting = 0 }); // возврат

        await h.Poller.PollOnceAsync();
        h.Poller.Current.Status.Should().Be("down");
        h.Poller.Current.DownSince.Should().NotBeNull();

        h.Time.Advance(TimeSpan.FromSeconds(15));
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Status.Should().Be("working");
        h.Poller.Current.DownSince.Should().BeNull();
    }

    // (4) Enabled=false — ни одного обращения к источнику; Enabled=true — обращения появляются.
    // Настоящий прогон цикла (StartAsync) на управляемом ожидании: продвигаем итерациями,
    // а не календарём. Мутация: убрать ветку if (!opts.Enabled) в ExecuteAsync — тест краснеет
    // (при Enabled=false ReadCalls станет > 0).
    [Fact]
    public async Task Disabled_NoCallToSource()
    {
        var h = new Harness(enabled: false);
        var gate = new GateWait();
        h.Poller.WaitFunc = gate.Wait;

        var cts = new CancellationTokenSource();
        var run = h.Poller.StartAsync(cts.Token);

        // Три итерации при Enabled=false: цикл ждёт и перечитывает настройку, источник не ходит.
        // await Release() — дожидаемся, пока цикл сделает итерацию и снова заблокируется.
        for (var i = 0; i < 3; i++)
        {
            await gate.Release();
        }
        h.Source.ReadCalls.Should().Be(0, "при Enabled=false цикл не дёргает источник");

        // Включаем: следующая итерация ходит в источник.
        h.Options.CurrentValue = new LocalModelStatsOptions { Enabled = true };
        await gate.Release();
        h.Source.ReadCalls.Should().BeGreaterThan(0, "после включения Enabled=true источник дёргается");

        cts.Cancel();
        await run;
    }

    // (5) Кольцо не растёт выше окна: HistoryWindow=10 с, шаг 5 с → максимум 2 точки.
    [Fact]
    public async Task Ring_DoesNotGrowBeyondWindow()
    {
        var h = new Harness();
        h.Options.CurrentValue = new LocalModelStatsOptions
        {
            Enabled = true,
            HistoryWindow = TimeSpan.FromSeconds(10), // 10 с / 5 с = 2 точки
        };

        for (var i = 0; i < 10; i++)
        {
            h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000 + i, Running = 0 });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        h.Poller.GetHistory(TimeSpan.FromHours(1)).Should().HaveCount(2, "кольцо ограничено HistoryWindow/шаг");
    }

    // (6) busy: running>0 → status=busy.
    [Fact]
    public async Task Busy_WhenRunningOrWaitingPositive()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 3, Waiting = 1 });
        await h.Poller.PollOnceAsync();
        h.Poller.Current.Status.Should().Be("busy");
        h.Poller.Current.Running.Should().Be(3);
        h.Poller.Current.Waiting.Should().Be(1);
    }

    // (7) Оконный hit-rate: 10 опросов по 1 с дают ту же долю, что 2 точки (старт/конец) —
    // окно 60 с, а не «от предыдущей точки». queries линейны, hits — квадратичны, поэтому
    // мгновенная (per-previous-point) доля «пилит» (растёт), а оконная стабильна.
    // Мутация: вернуть дельту «от предыдущей точки» — тест краснеет (A=0.17 ≠ B=0.09).
    [Fact]
    public async Task HitRate_Windowed_NotPerInterval()
    {
        // Сценарий A: 10 опросов по 1 с; queries=100*i (линейно), hits=i*i (квадратично).
        var a = new Harness();
        for (var i = 0; i < 10; i++)
        {
            a.Source.Snapshots.Enqueue(new EngineSnapshot
            {
                PrefixCacheHits = (long)i * i,
                PrefixCacheQueries = 100L * i,
                Running = 0,
            });
            await a.Poller.PollOnceAsync();
            a.Time.Advance(TimeSpan.FromSeconds(1));
        }
        var rateA = a.Poller.Current.Kv!.PrefixHitPerc;

        // Сценарий B: 2 опроса (t=0 и t=9), те же финальные счётчики (hits=81, queries=900).
        var b = new Harness();
        b.Source.Snapshots.Enqueue(new EngineSnapshot { PrefixCacheHits = 0, PrefixCacheQueries = 0, Running = 0 });
        await b.Poller.PollOnceAsync();
        b.Time.Advance(TimeSpan.FromSeconds(9));
        b.Source.Snapshots.Enqueue(new EngineSnapshot { PrefixCacheHits = 81, PrefixCacheQueries = 900, Running = 0 });
        await b.Poller.PollOnceAsync();
        var rateB = b.Poller.Current.Kv!.PrefixHitPerc;

        // Оконная доля: (81-0)/(900-0) = 0.09 — одинакова для 10 точек и для 2 точек.
        rateA.Should().Be(0.09, "10 точек: Δhits/Δqueries за окно 60 с = 81/900");
        rateB.Should().Be(0.09, "2 точки (старт/конец): та же оконная доля — нет «пилы»");
        rateA.Should().Be(rateB, "частота опроса не меняет оконную долю");
    }

    // (8) Скорость на поток: Δgeneration_tokens/Δinter_token_latency за окно 60 с.
    //     ADR-029: знаменатель — inter_token_latency (пишется на каждом шаге), а не
    //     decode_time (только при завершении запроса): окна совпадают, «—» не бывает.
    [Fact]
    public async Task GenPerStream_NormalCase()
    {
        var h = new Harness();
        // t=0: 0 токенов, 0 inter-token-времени. t=60: 600 токенов за 10 с inter-token-времени.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencySum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencySum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Kv!.GenPerStream.Should().Be(60.0, "600 токенов / 10 с inter-token = 60 ток/с на поток");
    }

    // (8b) ADR-029: один длинный ход (300 токенов, ещё не завершён) плюс служебные
    //      запросы на 1 токен. Старая формула (Δgen/Δdecode) давала «—»: decode_time
    //      длинного хода не записан (запрос не завершён), 1-токеновый запрос даёт
    //      decode_time +0. Новая формула (Δgen/Δinter_token_latency): inter_token_latency
    //      растёт на каждом шаге длинного хода → знаменатель > 0 → «—» нет.
    //      Мутация: вернуть знаменатель на DecodeTimeSum → «—» (null) → тест краснеет.
    [Fact]
    public async Task GenPerStream_LongRequestPlusOneTokenRequests_NotDash()
    {
        var h = new Harness();
        // t=0: старт, всё 0.
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 0,
            InterTokenLatencySum = 0,
            DecodeTimeSum = 0,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        // t=30: длинный ход сгенерировал 300 токенов (inter_token_latency = 5 с),
        // плюс 3 служебных запроса по 1 токену (decode_time +0, inter_token_latency +0).
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 303, // 300 (длинный) + 3 (1-токеновые)
            InterTokenLatencySum = 5, // только длинный ход (1-токеновые дают 0)
            DecodeTimeSum = 0, // 1-токеновые дают 0, длинный ещё не завершён
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        // Старая формула: Δgen/Δdecode = 303/0 = «—».
        // Новая формула: Δgen/ΔITL = 303/5 = 60.6 ток/с.
        h.Poller.Current.Kv!.GenPerStream.Should().NotBeNull(
            "длинный ход + 1-токеновые: inter_token_latency растёт → «—» нет");
        h.Poller.Current.Kv!.GenPerStream!.Should().BeApproximately(303.0 / 5.0, 0.01,
            "303 токена / 5 с inter-token = 60.6 ток/с (не «—»)");
    }

    // (9) Скорость на поток: деление на ноль (inter_token_latency не меняется) → null.
    [Fact]
    public async Task GenPerStream_DivideByZero_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencySum = 5, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        // inter_token_latency не вырос (5 → 5), но токены выросли: дельта = 0 → деление на ноль.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, InterTokenLatencySum = 5, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Kv!.GenPerStream.Should().BeNull("Δinter_token_latency = 0 → деление на ноль → null");
    }

    // (10) Скорость на поток: отрицательная дельта (рестарт движка, счётчик упал) → null.
    [Fact]
    public async Task GenPerStream_NegativeDelta_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000, InterTokenLatencySum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        // Рестарт: generation_tokens упал (1000 → 500), inter_token_latency тоже — отрицательная дельта.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 500, InterTokenLatencySum = 5, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Kv!.GenPerStream.Should().BeNull("отрицательная дельта (рестарт) → null");
    }

    // (11) Вытеснения за 15 мин: дельта счётчика preemptions за PreemptionsWindow.
    [Fact]
    public async Task Preemptions_OverFifteenMinutes()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        // 10 мин (в пределах окна 15 мин): preemptions 0 → 7.
        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = 7, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Kv!.Preemptions.Should().Be(7, "Δpreemptions за окно 15 мин = 7");
    }

    // (12) GetHistory с явным sinceUnixMs: фильтр «только новее этого момента».
    [Fact]
    public async Task GetHistory_ExplicitSinceUnixMs()
    {
        var h = new Harness();
        var t0 = h.Time.GetUtcNow();
        for (var i = 0; i < 4; i++)
        {
            h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100 * i, Running = 0 });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        // sinceUnixMs = t0 + 10 с: точки t0, t0+5s включаются; t0+10s, t0+15s — нет.
        var since = (long)(t0.AddSeconds(10).ToUnixTimeMilliseconds());
        var history = h.Poller.GetHistory(TimeSpan.FromHours(1), since);
        history.Should().HaveCount(2, "явный sinceUnixMs фильтрует кольцо по моменту, а не по окну");
    }

    // (13) M6: частый опрос (1×/с) 20 минут. Без прореживания кольцо (потолок 720 = 1 ч / 5 с)
    // держало бы 720 точек × 1 с = 12 минут. С прореживанием по PointStep: ≤ 720 точек,
    // интервал ~5 с, покрыт весь период опроса (а не сжатые 12 минут).
    [Fact]
    public async Task Ring_FastPolling_CoversFullWindow_NotCompressed()
    {
        var h = new Harness();
        var t0 = h.Time.GetUtcNow();

        // 1200 опросов по 1 с = 20 минут
        for (var i = 0; i < 1200; i++)
        {
            h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000L + i, Running = 0 });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));

        // Не больше потолка окна/шаг (720), и не сжато до 12 минут
        history.Should().HaveCountLessOrEqualTo(720, "потолок = HistoryWindow/PointStep = 720");
        // Покрыт весь период: старейшая точка ~20 мин назад, а не ~12 мин (720 × 1 с)
        var span = history[^1].Timestamp - history[0].Timestamp;
        span.Should().BeGreaterOrEqualTo(TimeSpan.FromMinutes(15),
            "прореживание по 5 с: кольцо покрывает весь опрос (20 мин), а не 12 мин");
        // Интервал между точками ~5 с (PointStep), а не 1 с
        if (history.Count >= 2)
        {
            var gap = (history[1].Timestamp - history[0].Timestamp).TotalSeconds;
            gap.Should().BeApproximately(5.0, 0.5, "точки в кольце не чаще PointStep (5 с)");
        }

        // Живое «сейчас» (Speed) — свежее: дельта последнего опроса, а не точка 5 с назад
        h.Poller.Current.Speed.Should().NotBeNull("живое «сейчас» доступно при частом опросе");
        _ = t0;
    }

    // ─── Средняя скорость на поток за окно (GetAverageSpeed) ─────────────────────────────
    //
    // Средняя = Δgeneration_tokens/Δinter_token_latency (генерация) и
    // Δprefill_kv_computed_tokens/Δprefill_time (чтение промпта) за окно.
    // ADR-029: знаменатель генерации — inter_token_latency (пишется на каждом шаге),
    // поэтому простой (движок молчит) в среднее не входит. Нет данных/рестарт → null, не 0.

    // (14) Обычное окно: 600 токенов за 10 с inter-token-времени за 60 с wall-clock → 60 ток/с.
    // Мутация: делить на wall-clock (60 с) вместо inter_token_latency (10 с) → 10, тест краснеет.
    [Fact]
    public async Task AvgGenTps_NormalWindow()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencySum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencySum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).GenTps
            .Should().Be(60.0, "600 токенов / 10 с inter-token = 60 ток/с (средняя за окно)");
    }

    // (15) Простой не тянет среднее вниз: 10 с генерации внутри 300 с окна. Средняя считается
    // по inter-token-времени (10 с), а не по wall-clock (300 с) → 60, а не 20.
    // Мутация: знаменатель = wall-clock → 600/300 = 2, тест краснеет.
    [Fact]
    public async Task AvgGenTps_IdleDoesNotDilute()
    {
        var h = new Harness();
        // t=0: старт счётчиков.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencySum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        // t=10: 600 токенов за 10 с inter-token-времени (генерация реально шла).
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencySum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();
        // t=300: 290 с простоя — счётчики не двигаются, но окно растянулось.
        h.Time.Advance(TimeSpan.FromSeconds(290));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencySum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).GenTps
            .Should().Be(60.0, "простой (290 с) не входит в знаменатель: средняя = 600/10, а не 600/300");
    }

    // (16) Рестарт счётчика: generation_tokens упал (600 → 100) → отрицательная дельта → null.
    // Мутация: не резать по разрыву (усреднить по всему окну) → 500/10 = 50, тест краснеет.
    [Fact]
    public async Task AvgGenTps_Restart_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencySum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencySum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();
        // Рестарт: счётчики упали (600 → 100, 10 → 2) → отрицательная дельта.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, InterTokenLatencySum = 2, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).GenTps
            .Should().BeNull("рестарт счётчика (отрицательная дельта) = разрыв, средняя null, а не отрицательная");
    }

    // (16b) ADR-029: ход, перешедший границу окна. Быстрая фаза (300 ток/5с=60) до окна
    //      (t=0…60), медленная фаза (600 ток/20с=30) внутри окна (t=60…960).
    //      Окно 15 мин, now=t=960: basePt = t=60 (старейшая в окне, since=t=60).
    //      Δ за окно = (900−300)/(25−5) = 600/20 = 30 ток/с (только медленная фаза).
    //      Мутация: basePt = t=0 (весь период) → 900/25 = 36 ≠ 30 → тест краснеет.
    [Fact]
    public async Task AvgGenTps_TurnCrossingWindowBoundary()
    {
        var h = new Harness();
        // t=0: старт.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencySum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        // t=60: 300 токенов за 5 с inter-token (быстрая фаза, до окна).
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 300, InterTokenLatencySum = 5, Running = 0 });
        await h.Poller.PollOnceAsync();
        // t=960 (16 мин): ещё 600 токенов за 20 с inter-token (медленная фаза, внутри окна).
        // Окно 15 мин: since = t=960−900 = t=60 → basePt = t=60.
        h.Time.Advance(TimeSpan.FromMinutes(15));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 900, InterTokenLatencySum = 25, Running = 0 });
        await h.Poller.PollOnceAsync();

        // Окно 15 мин: basePt = t=60 (300 токенов, 5 с ITL).
        // Δ за окно = (900−300) / (25−5) = 600/20 = 30 ток/с (только медленная фаза).
        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).GenTps
            .Should().BeApproximately(30.0, 0.01,
                "средняя за окно: только токены внутри окна (600/20=30), не весь период (900/25=36)");
    }

    // (17) Нет данных: ни одного опроса → null (не 0). Мутация: вернуть 0 вместо null → краснеет.
    [Fact]
    public async Task AvgGenTps_NoData_IsNull()
    {
        var h = new Harness();
        // Ни одного PollOnceAsync: буфер счётчиков пуст, _last null.
        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).GenTps
            .Should().BeNull("нет данных за окно → null, а не 0");
        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).PromptTps
            .Should().BeNull("нет данных за окно → null, а не 0");
    }

    // (18) Чтение промпта: средняя скорость prefill за окно = Δprefill_kv_computed_tokens/Δprefill_time.
    // Мутация: перепутать числитель/знаменатель → тест краснеет.
    [Fact]
    public async Task AvgPromptTps_NormalWindow()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { PrefillKvComputedTokens = 0, PrefillTimeSum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { PrefillKvComputedTokens = 3000, PrefillTimeSum = 3, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.GetAverageSpeed(TimeSpan.FromMinutes(15)).PromptTps
            .Should().Be(1000.0, "3000 prefill-токенов / 3 с prefill-времени = 1000 ток/с (средняя)");
    }

    // ─── Серии кэша в истории (v2.1) ─────────────────────────────────────────────────────
    //
    // Точка кольца несёт шесть полей: kvUsage (максимум по опросам с прошлой точки) и пять
    // приращений счётчиков (prefixHits/prefixQueries/ramHits/ramQueries/preemptions) с прошлой
    // ТОЧКИ. Приращения — null при отсутствии прошлой точки, отсутствии метрики или рестарте
    // (счётчик упал). Отрицательных значений в JSON быть не должно.

    // (19) Обычный интервал: пять счётчиков дают приращение с прошлой точки, kvUsage — максимум.
    // Мутация: считать приращение с предыдущего ОПРОСА (а не точки) → значения совпадут,
    // но при частом опросе тест (21) краснеет; здесь проверяем базовый случай.
    [Fact]
    public async Task CacheSeries_NormalInterval_IncrementsAndMax()
    {
        var h = new Harness();
        // t=0: старт счётчиков (первая точка — приращения null, нет прошлой точки).
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            PrefixCacheHits = 100, PrefixCacheQueries = 1000,
            RamTierHits = 10, RamTierQueries = 100,
            Preemptions = 1, KvUsagePerc = 0.2, Running = 0,
        });
        await h.Poller.PollOnceAsync();

        // t=5: счётчики выросли, kvUsage подскочил до 0.5.
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            PrefixCacheHits = 150, PrefixCacheQueries = 1200,
            RamTierHits = 15, RamTierQueries = 130,
            Preemptions = 3, KvUsagePerc = 0.5, Running = 0,
        });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history.Should().HaveCount(2);

        // Первая точка: прошлой точки нет → все пять приращений null; kvUsage — максимум с старта.
        history[0].PrefixHits.Should().BeNull("нет прошлой точки → null, не 0");
        history[0].PrefixQueries.Should().BeNull();
        history[0].RamHits.Should().BeNull();
        history[0].RamQueries.Should().BeNull();
        history[0].Preemptions.Should().BeNull();
        history[0].KvUsage.Should().Be(0.2, "максимум по опросам до первой точки = 0.2");

        // Вторая точка: приращения с прошлой точки, kvUsage — максимум (0.5).
        history[1].PrefixHits.Should().Be(50, "150 − 100 = 50");
        history[1].PrefixQueries.Should().Be(200, "1200 − 1000 = 200");
        history[1].RamHits.Should().Be(5, "15 − 10 = 5");
        history[1].RamQueries.Should().Be(30, "130 − 100 = 30");
        history[1].Preemptions.Should().Be(2, "3 − 1 = 2");
        history[1].KvUsage.Should().Be(0.5, "максимум по опросам с прошлой точки = 0.5");
    }

    // (20) kvUsage — максимум по НЕСКОЛЬКИМ опросам с прошлой точки: при опросе 1×/с
    // пик давления (0.9) между двумя точками (5 с) не теряется.
    // Мутация: брать kvUsage последнего опроса (0.3) вместо максимума → тест краснеет.
    [Fact]
    public async Task CacheSeries_KvUsage_MaxAcrossMultiplePolls()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { KvUsagePerc = 0.1, Running = 0 });
        await h.Poller.PollOnceAsync();

        // Между двумя точками (5 с) — три опроса: 0.1 → 0.9 (пик) → 0.3.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { KvUsagePerc = 0.9, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { KvUsagePerc = 0.3, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(3));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { KvUsagePerc = 0.4, Running = 0 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history.Should().HaveCount(2);
        history[1].KvUsage.Should().Be(0.9, "пик 0.9 между точками не теряется (максимум, не последний)");
    }

    // (21) Приращение — с прошлой ТОЧКИ, не с предыдущего опроса: при опросе 1×/с между
    // точками (5 с) четыре промежуточных опроса не должны «съесть» приращение.
    // Мутация: считать дельту от предыдущего опроса → приращение = 1 (последний шаг),
    // а не 5 (с прошлой точки) → тест краснеет.
    [Fact]
    public async Task CacheSeries_IncrementFromPreviousPoint_NotPreviousPoll()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = 0, Running = 0 });
        await h.Poller.PollOnceAsync();

        // Пять опросов по 1 с: preemptions 0 → 1 → 2 → 3 → 4 → 5.
        for (var i = 1; i <= 5; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = i, Running = 0 });
            await h.Poller.PollOnceAsync();
        }

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        // Точки: t=0 (старт) и t=5 (после 5 с). Промежуточные опросы точку не создают.
        history.Should().HaveCount(2);
        history[1].Preemptions.Should().Be(5, "приращение с прошлой ТОЧКИ (0→5), не с предыдущего опроса (4→5=1)");
    }

    // (22) Рестарт счётчика: счётчик упал (50 → 10) → приращение null, не отрицательное.
    // Мутация: не резать по разрыву (отрицательное приращение) → тест краснеет.
    [Fact]
    public async Task CacheSeries_CounterRestart_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { PrefixCacheHits = 100, PrefixCacheQueries = 1000, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        // Рестарт: счётчики упали (100→10, 1000→100) → отрицательная дельта.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { PrefixCacheHits = 10, PrefixCacheQueries = 100, Running = 0 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].PrefixHits.Should().BeNull("рестарт (счётчик упал) → null, не −90");
        history[1].PrefixQueries.Should().BeNull("рестарт → null, не −900");
    }

    // (23) Метрики нет (движок не отдал счётчик) → приращение null, не 0.
    // Мутация: вернуть 0 вместо null → тест краснеет.
    [Fact]
    public async Task CacheSeries_MetricAbsent_IsNull()
    {
        var h = new Harness();
        // t=0: preemptions задан, prefix-счётчики не заданы (null).
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        // t=5: preemptions вырос, prefix-счётчики по-прежнему null.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = 4, Running = 0 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].Preemptions.Should().Be(4, "счётчик есть → приращение 4");
        history[1].PrefixHits.Should().BeNull("метрики нет (null на обеих точках) → null, не 0");
        history[1].PrefixQueries.Should().BeNull();
    }

    // (24) Окно 5 мин: GetHistory(5m) отдаёт только точки за последние 5 мин (60 точек по 5 с).
    // Мутация: резать окно по 15 мин → тест краснеет (больше точек).
    [Fact]
    public async Task CacheSeries_Window5m_ReturnsOnlyLastFiveMinutes()
    {
        var h = new Harness();
        // 20 точек по 5 с = 100 с (~1.7 мин) — все в пределах 5 мин.
        for (var i = 0; i < 20; i++)
        {
            h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = i, Running = 0 });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }
        // Ещё 10 точек по 5 с = 50 с → суммарно 150 с = 2.5 мин. Всё ещё < 5 мин.
        for (var i = 20; i < 30; i++)
        {
            h.Source.Snapshots.Enqueue(new EngineSnapshot { Preemptions = i, Running = 0 });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        // Все 30 точек (150 с) в пределах 5-минутного окна.
        h.Poller.GetHistory(TimeSpan.FromMinutes(5)).Should().HaveCount(30, "150 с < 5 мин → все точки");

        // Окно 1 мин: только последние 12 точек (60 с / 5 с).
        h.Poller.GetHistory(TimeSpan.FromMinutes(1)).Should().HaveCount(12, "60 с / 5 с = 12 точек");
    }

    // (25) Мусор в /metrics: источник бросает (сбой парсера на недоверенном тексте) →
    // исключение не выходит из поллера (PollOnceAsync ловит), статус → down.
    // Мутация: убрать try/catch вокруг source.ReadAsync → исключение выходит, тест краснеет.
    [Fact]
    public async Task SourceThrows_GarbageMetrics_DoesNotEscapePoller()
    {
        var h = new Harness();
        // Подменяем фабрику: источник бросает (симуляция мусора, на котором парсер упал).
        h.Poller.SourceFactoryOverride = () => (new ThrowingSource(), "vllm", "qwen", null, true);

        await h.Poller.PollOnceAsync(); // не бросает наружу
        h.Poller.Current.Status.Should().Be("down", "сбой источника → down, а не исключение из BackgroundService");
    }

    // ─── Скорость на поток: приращения для второй линии графика «Нагрузка и кэш» ─────────
    //
    // Точка кольца несёт три приращения с прошлой ТОЧКИ: genTokens (generation_tokens_total),
    // itlSeconds (inter_token_latency_seconds_sum) и itlCount (inter_token_latency_seconds_count
    // = число шагов декодирования). Фронт сводит точки в интервалы (15/60 с) и считает
    // среднюю скорость на поток: ΣgenTokens/ΣitlSeconds (ток/с) — та же формула «генерации
    // на поток» (ADR-029) — и «шаг модели»: время шага ΣitlSeconds/ΣitlCount (мс), токенов
    // за шаг ΣgenTokens/ΣitlCount, агентов в генерации ΣitlSeconds/длина интервала.
    // Правило null то же, что у серий кэша: нет прошлой точки, метрика не на прошлой/текущей
    // точке, либо счётчик упал (рестарт) → null; отрицательных значений нет.

    // (26) Обычный интервал: genTokens/itlSeconds — приращения с прошлой ТОЧКИ.
    // Мутация: считать приращение с предыдущего ОПРОСА (а не точки) → при частом опросе
    // тест краснеет; здесь базовый случай (опрос = точка, шаг 5 с).
    [Fact]
    public async Task PerStreamSpeed_NormalInterval_Increments()
    {
        var h = new Harness();
        // t=0: старт счётчиков (первая точка — приращения null, нет прошлой точки).
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 100,
            InterTokenLatencySum = 2,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        // t=5: счётчики выросли.
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 700,
            InterTokenLatencySum = 12,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history.Should().HaveCount(2);

        // Первая точка: прошлой точки нет → оба приращения null.
        history[0].GenTokens.Should().BeNull("нет прошлой точки → null, не 0");
        history[0].ItlSeconds.Should().BeNull();

        // Вторая точка: приращения с прошлой точки: 700−100=600, 12−2=10.
        history[1].GenTokens.Should().Be(600, "700 − 100 = 600");
        history[1].ItlSeconds.Should().Be(10.0, "12 − 2 = 10");
    }

    // (27) Рестарт счётчика: счётчики упали (1000→100, 10→1) → приращения null, не отрицательные.
    // Мутация: не резать по разрыву (отрицательное приращение) → тест краснеет.
    [Fact]
    public async Task PerStreamSpeed_CounterRestart_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000, InterTokenLatencySum = 10, Running = 1 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        // Рестарт: счётчики упали (1000→100, 10→1) → отрицательная дельта.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, InterTokenLatencySum = 1, Running = 0 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].GenTokens.Should().BeNull("рестарт (счётчик упал) → null, не −900");
        history[1].ItlSeconds.Should().BeNull("рестарт → null, не −9");
    }

    // (28) Метрики нет (движок не отдал счётчик) → приращение null, не 0.
    // Мутация: вернуть 0 вместо null → тест краснеет.
    [Fact]
    public async Task PerStreamSpeed_MetricAbsent_IsNull()
    {
        var h = new Harness();
        // t=0: generation_tokens задан, inter_token_latency не задан (null).
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, Running = 1 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        // t=5: generation_tokens вырос, inter_token_latency по-прежнему null.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 400, Running = 1 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].GenTokens.Should().Be(300, "счётчик есть → приращение 400 − 100 = 300");
        history[1].ItlSeconds.Should().BeNull("метрики нет (null на обеих точках) → null, не 0");
    }

    // (29) ItlCount: приращение числа шагов декодирования (inter_token_latency_seconds_count)
    // с прошлой ТОЧКИ. Фронт: ΣitlSeconds/ΣitlCount = время шага, ΣgenTokens/ΣitlCount =
    // токенов за шаг (график «Шаг модели»).
    // Мутация: считать с предыдущего ОПРОСА (а не точки) → при частом опросе краснеет;
    // здесь базовый случай (опрос = точка, шаг 5 с).
    [Fact]
    public async Task ItlCount_NormalInterval_IncrementFromPreviousPoint()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 100,
            InterTokenLatencySum = 2,
            InterTokenLatencyCount = 4,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 700,
            InterTokenLatencySum = 12,
            InterTokenLatencyCount = 14,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history.Should().HaveCount(2);
        history[0].ItlCount.Should().BeNull("нет прошлой точки → null, не 0");
        history[1].ItlCount.Should().Be(10, "14 − 4 = 10 шагов");
    }

    // (30) ItlCount: рестарт счётчика (14 → 2) → null, не отрицательное.
    // Мутация: не резать по разрыву → −12, тест краснеет.
    [Fact]
    public async Task ItlCount_CounterRestart_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { InterTokenLatencyCount = 14, Running = 1 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { InterTokenLatencyCount = 2, Running = 0 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].ItlCount.Should().BeNull("рестарт (счётчик упал) → null, не −12");
    }

    // (31) ItlCount: метрики нет (null на обеих точках) → null, не 0.
    // Мутация: вернуть 0 вместо null → тест краснеет.
    [Fact]
    public async Task ItlCount_MetricAbsent_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, Running = 1 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 400, Running = 1 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].ItlCount.Should().BeNull("метрики нет (null на обеих точках) → null, не 0");
    }

    // Фейковый источник, который бросает (мусор в /metrics, на котором парсер не выдержал).
    private sealed class ThrowingSource : IEngineMetricsSource
    {
        public Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
            => throw new FormatException("мусор в /metrics");
    }
}
