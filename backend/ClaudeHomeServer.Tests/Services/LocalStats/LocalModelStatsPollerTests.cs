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
            Poller.SourceFactoryOverride = () => (Source, "vllm", "qwen3.8-27b", true);
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

    // (8) Скорость на поток: Δgeneration_tokens/Δsum(decode_time) за окно 60 с.
    [Fact]
    public async Task GenPerStream_NormalCase()
    {
        var h = new Harness();
        // t=0: 0 токенов, 0 decode-времени. t=60: 600 токенов за 10 с decode-времени.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, DecodeTimeSum = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, DecodeTimeSum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Kv!.GenPerStream.Should().Be(60.0, "600 токенов / 10 с decode = 60 ток/с на поток");
    }

    // (9) Скорость на поток: деление на ноль (decode_time не меняется) → null.
    [Fact]
    public async Task GenPerStream_DivideByZero_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, DecodeTimeSum = 5, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        // decode_time не вырос (5 → 5), но токены выросли: дельта времени = 0 → деление на ноль.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, DecodeTimeSum = 5, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.Kv!.GenPerStream.Should().BeNull("Δdecode_time = 0 → деление на ноль → null");
    }

    // (10) Скорость на поток: отрицательная дельта (рестарт движка, счётчик упал) → null.
    [Fact]
    public async Task GenPerStream_NegativeDelta_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000, DecodeTimeSum = 10, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        // Рестарт: generation_tokens упал (1000 → 500), decode_time тоже — отрицательная дельта.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 500, DecodeTimeSum = 5, Running = 0 });
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
}
