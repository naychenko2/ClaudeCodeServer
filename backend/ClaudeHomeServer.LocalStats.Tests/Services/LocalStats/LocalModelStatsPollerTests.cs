using System.Net;
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

    // Заглушка IHttpClientFactory: HttpClient с ответом 503 — «движок не отвечает».
    // Тесты Б3b гоняют реальный LlamaServerMetricsSource через этот обработчик:
    // отказ /metrics без обращения к живой сети.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefusingHandler());
        public HttpClient CreateClient(Type type, string name) => CreateClient(name);

        private sealed class RefusingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
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

    // Фейковый читатель состояния арбитра (Б3b): тест задаёт очередь ответов и видит счётчик
    // обращений (c2: дорожка main никогда не зовёт клиент — CalledFor пустой).
    private sealed class FakeArbiter : IEngineArbiterStateReader
    {
        public Queue<EngineArbiterSnapshot?> Results { get; } = new();
        public List<string> CalledFor { get; } = [];
        public int Calls => CalledFor.Count;

        public Task<EngineArbiterSnapshot?> GetAsync(string engineName, CancellationToken ct = default)
        {
            CalledFor.Add(engineName);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : null);
        }
    }

    // Снимок арбитра для тестов Б3b: значимы Reachable и Bonsai, остальное не нужно дорожке.
    private static EngineArbiterSnapshot ArbiterState(string state, bool reachable = true) =>
        new(reachable, state, null, null, null, null, null, null, null, null, null, null);

    private sealed class Harness
    {
        public ManualTime Time { get; } = new();
        public FakeOptionsMonitor<LocalModelStatsOptions> Options { get; }
        public FakeSource Source { get; } = new();
        public FakeArbiter Arbiter { get; } = new();
        public LocalModelStatsPoller Poller { get; }

        public Harness(
            bool enabled = true,
            NamedLocalEngines? engines = null,
            ILogger<LocalModelStatsPoller>? log = null)
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
                engines ?? new NamedLocalEngines([]), // без именованных движков: их заводят отдельные тесты Б2b
                Arbiter, // Б3b: поллер получает клиент арбитра; он достается только дорожкам с StateUrl
                new FakeHttpClientFactory(), // main-источник подменяется Override; именованные дорожки идут через 503
                Time,
                log ?? NullLogger<LocalModelStatsPoller>.Instance);
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

    // ─── Полоса «Куда уходит время агентов» (ADR-029, v2.3) ─────────────────────────────
    //
    // Точка кольца несёт три новых приращения с прошлой ТОЧКИ: PrefillSeconds (prefill_time),
    // QueueSeconds (queue_time) и PromptTokens (prompt_tokens). Правило null то же, что у
    // itl-приращений: нет прошлой точки, метрика не на прошлой/текущей точке, либо счётчик
    // упал (рестарт) → null; отрицательных значений нет.

    // (32) Обычный интервал: PrefillSeconds/QueueSeconds/PromptTokens — приращения с прошлой ТОЧКИ.
    // Мутация: считать с предыдущего ОПРОСА (а не точки) → при частом опросе краснеет;
    // здесь базовый случай (опрос = точка, шаг 5 с).
    [Fact]
    public async Task TimeShares_NormalInterval_Increments()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 100,
            InterTokenLatencySum = 2,
            InterTokenLatencyCount = 4,
            PrefillTimeSum = 1,
            QueueTimeSum = 0.5,
            PromptTokens = 1000,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot
        {
            GenerationTokens = 700,
            InterTokenLatencySum = 12,
            InterTokenLatencyCount = 14,
            PrefillTimeSum = 6,
            QueueTimeSum = 2.5,
            PromptTokens = 3000,
            Running = 1,
        });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history.Should().HaveCount(2);
        history[0].PrefillSeconds.Should().BeNull("нет прошлой точки → null, не 0");
        history[0].QueueSeconds.Should().BeNull();
        history[0].PromptTokens.Should().BeNull();
        history[1].PrefillSeconds.Should().Be(5.0, "6 − 1 = 5");
        history[1].QueueSeconds.Should().Be(2.0, "2.5 − 0.5 = 2");
        history[1].PromptTokens.Should().Be(2000, "3000 − 1000 = 2000");
    }

    // (33) Рестарт счётчика: prefill/queue/prompt упали → приращения null, не отрицательные.
    // Мутация: не резать по разрыву (отрицательное приращение) → тест краснеет.
    [Fact]
    public async Task TimeShares_CounterRestart_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { PrefillTimeSum = 10, QueueTimeSum = 5, PromptTokens = 1000, Running = 1 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { PrefillTimeSum = 1, QueueTimeSum = 0.5, PromptTokens = 100, Running = 0 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].PrefillSeconds.Should().BeNull("рестарт (счётчик упал) → null, не −9");
        history[1].QueueSeconds.Should().BeNull("рестарт → null, не −4.5");
        history[1].PromptTokens.Should().BeNull("рестарт → null, не −900");
    }

    // (34) Метрики нет (null на обеих точках) → приращения null, не 0.
    // Мутация: вернуть 0 вместо null → тест краснеет.
    [Fact]
    public async Task TimeShares_MetricAbsent_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, Running = 1 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 400, Running = 1 });
        await h.Poller.PollOnceAsync();

        var history = h.Poller.GetHistory(TimeSpan.FromHours(1));
        history[1].GenTokens.Should().Be(300, "счётчик есть → приращение 300");
        history[1].PrefillSeconds.Should().BeNull("метрики нет (null на обеих точках) → null, не 0");
        history[1].QueueSeconds.Should().BeNull();
        history[1].PromptTokens.Should().BeNull();
    }

    // ─── «Ускорение угадыванием» (ADR-029, v2.3) ─────────────────────────────────────────
    //
    // SpeculativeSpeedup = Δgeneration_tokens/Δinter_token_latency_count за окно ~60 с
    // (StreamWindow, как GenPerStream): во сколько раз DFlash ускоряет генерацию против
    // выдачи по одному токену. null — нет данных/рестарт или Σitl_count = 0.

    // (35) Обычное окно: 600 токенов за 200 шагов за 60 с → 3 токена/шаг (ускорение ≈ 3×).
    // Мутация: знаменатель → itlSeconds (время) → 600/10 = 60 ≠ 3 → тест краснеет.
    [Fact]
    public async Task SpeculativeSpeedup_TokensPerStepOverWindow()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencyCount = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencyCount = 200, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.SpeculativeSpeedup.Should().Be(3.0, "600 токенов / 200 шагов = 3 (ускорение угадыванием)");
    }

    // (36) Σitl_count = 0 (шагов не было) → null, не деление на ноль.
    // Мутация: вернуть 0 вместо null → тест краснеет.
    [Fact]
    public async Task SpeculativeSpeedup_NoSteps_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencyCount = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        // Токены выросли, но шагов не было (itl_count не двигается) → деление на ноль → null.
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, InterTokenLatencyCount = 0, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.SpeculativeSpeedup.Should().BeNull("Σitl_count = 0 → null, не деление на ноль");
    }

    // (37) Рестарт счётчика: generation_tokens упал → отрицательная дельта → null.
    // Мутация: не резать по разрыву → отрицательное ускорение, тест краснеет.
    [Fact]
    public async Task SpeculativeSpeedup_Restart_IsNull()
    {
        var h = new Harness();
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 0, InterTokenLatencyCount = 0, Running = 0 });
        await h.Poller.PollOnceAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 600, InterTokenLatencyCount = 200, Running = 0 });
        await h.Poller.PollOnceAsync();
        // Рестарт: счётчики упали (600 → 100, 200 → 10) → отрицательная дельта.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, InterTokenLatencyCount = 10, Running = 0 });
        await h.Poller.PollOnceAsync();

        h.Poller.Current.SpeculativeSpeedup.Should().BeNull("рестарт счётчика (отрицательная дельта) → null");
    }

    // (38) Стабильная база шага декода (ADR-029, v2.5): поллер публикует Current.CleanStepMs,
    // и база НЕ зависит от окна запроса — «долгие» шаги в хвосте кольца её не двигают
    // (если бы база считалась по последнему окну, она была бы ≈250 мс).
    // Мутация: считать базу по хвосту кольца (окно 1 мин) — тест краснеет.
    [Fact]
    public async Task CleanStep_PublishedBase_IgnoresRecentSlowSteps()
    {
        var h = new Harness();
        double sum = 0;
        long count = 0;

        // 12 точек с шагом 50 мс (1 с itl на 20 шагов декодирования).
        for (var i = 0; i < 12; i++)
        {
            sum += 1.0;
            count += 20;
            h.Source.Snapshots.Enqueue(new EngineSnapshot
            {
                GenerationTokens = 1000 + i,
                InterTokenLatencySum = sum,
                InterTokenLatencyCount = count,
                Running = 1,
            });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        h.Poller.Current.CleanStepMs.Should().BeApproximately(50.0, 0.01, "10-й перцентиль шагов кольца");

        // Хвост кольца: 12 «долгих» шагов (5 с на 20 шагов = 250 мс) — целая минута.
        for (var i = 0; i < 12; i++)
        {
            sum += 5.0;
            count += 20;
            h.Source.Snapshots.Enqueue(new EngineSnapshot
            {
                GenerationTokens = 2000 + i,
                InterTokenLatencySum = sum,
                InterTokenLatencyCount = count,
                Running = 1,
            });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        h.Poller.Current.CleanStepMs.Should().BeApproximately(50.0, 0.01, "хвост кольца не двигает базу");
    }

    // (39) База следует за движком, когда старые точки вышли из кольца (окно 10 с = 2 точки):
    // опубликованная база обновляется, если новое значение отличается больше чем на 10 %.
    [Fact]
    public async Task CleanStep_UpdatesWhenRingTurnsOver()
    {
        var h = new Harness();
        h.Options.CurrentValue = new LocalModelStatsOptions
        {
            Enabled = true,
            HistoryWindow = TimeSpan.FromSeconds(10), // 10 с / 5 с = 2 точки
        };

        double sum = 0;
        long count = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += 1.0;
            count += 20;
            h.Source.Snapshots.Enqueue(new EngineSnapshot
            {
                GenerationTokens = 1000 + i,
                InterTokenLatencySum = sum,
                InterTokenLatencyCount = count,
                Running = 1,
            });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }
        h.Poller.Current.CleanStepMs.Should().BeApproximately(50.0, 0.01);

        // Движок стал в 10 раз медленнее: старые точки выходят из кольца → база новая.
        for (var i = 0; i < 6; i++)
        {
            sum += 10.0;
            count += 20;
            h.Source.Snapshots.Enqueue(new EngineSnapshot
            {
                GenerationTokens = 3000 + i,
                InterTokenLatencySum = sum,
                InterTokenLatencyCount = count,
                Running = 1,
            });
            await h.Poller.PollOnceAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
        }

        h.Poller.Current.CleanStepMs.Should().BeApproximately(500.0, 0.01, "старые точки вышли из кольца — база новая");
    }

    // Дорожка поверх фейкового источника для тестов цикла по дорожкам.
    private static EngineStatsTrack MakeTrack(Harness h, string name, Func<(IEngineMetricsSource, string?, string?, int?, bool)> resolve)
        => new(name, h.Options, resolve, h.Time, NullLogger.Instance);

    // Сбой дорожки A не мешает опросу дорожки B: PollTrackAsync ловит сбой, следующие
    // опросы идут.
    // Мутация: убрать try/catch вокруг track.PollOnceAsync в PollTrackAsync — тест краснеет.
    [Fact]
    public async Task PollTrack_TrackThrows_OtherTracksStillUpdated()
    {
        var h = new Harness();
        var good = new FakeSource();
        good.Snapshots.Enqueue(new EngineSnapshot { Running = 2 });
        var broken = MakeTrack(h, "broken", () => throw new InvalidOperationException("сбой дорожки"));
        var bonsaiTrack = MakeTrack(h, "bonsai", () => (good, "llama-server", "bonsai", null, true));
        h.Poller.AddTrack(broken);
        h.Poller.AddTrack(bonsaiTrack);
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 1 });

        await h.Poller.PollTrackAsync(broken); // сбой не вышел наружу
        await h.Poller.PollOnceAsync();
        await h.Poller.PollTrackAsync(bonsaiTrack);

        h.Poller.CurrentOf("main")!.Running.Should().Be(1);
        h.Poller.CurrentOf("bonsai")!.Running.Should().Be(2, "дорожка после упавшей опросилась");
    }

    [Fact]
    public async Task Directory_ReportsTracks_MainFirst_AndMainIsTheReaderTrack()
    {
        var h = new Harness();
        h.Poller.AddTrack(MakeTrack(h, "bonsai", () => (new FakeSource(), null, null, null, false)));
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 0 });

        await h.Poller.PollOnceAsync();

        h.Poller.Names.Should().Equal("main", "bonsai");
        h.Poller.CurrentOf("main").Should().BeSameAs(h.Poller.Current);
        h.Poller.HistoryOf("main", TimeSpan.FromHours(1)).Should().HaveCount(1);
        h.Poller.CurrentOf("nope").Should().BeNull();
        h.Poller.HistoryOf("nope", TimeSpan.FromHours(1)).Should().BeEmpty();
    }

    // Фейковый источник, который бросает (мусор в /metrics, на котором парсер не выдержал).
    private sealed class ThrowingSource : IEngineMetricsSource
    {
        public Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
            => throw new FormatException("мусор в /metrics");
    }

    // ─── Б2b: дорожка именованного движка + хвосты ревью Б1 (H1–H3) ────────────────────────

    // Именованные движки из конфига: BaseUrl+Model заданы, MetricsUrl и StateUrl — по параметру.
    // Реальный конструктор NamedLocalEngines (тот же путь, что на бою); HTTP при построении
    // не делается — сеть трогает только опрос дорожки.
    private static NamedLocalEngines EnginesFromConfig(params (string Name, string? MetricsUrl, string? StateUrl)[] engines)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (name, metricsUrl, stateUrl) in engines)
        {
            dict[$"LocalEngines:{name}:BaseUrl"] = "http://127.0.0.1:18030";
            dict[$"LocalEngines:{name}:Model"] = "bonsai2-27b";
            if (metricsUrl is not null)
                dict[$"LocalEngines:{name}:MetricsUrl"] = metricsUrl;
            if (stateUrl is not null)
                dict[$"LocalEngines:{name}:StateUrl"] = stateUrl;
        }
        return new NamedLocalEngines(
            new FakeHttpClientFactory(), new ConfigurationBuilder().AddInMemoryCollection(dict).Build(),
            NullLoggerFactory.Instance);
    }

    // (40) c1: движок с MetricsUrl получает дорожку (имя = имя движка); без MetricsUrl — не получает.
    // Мутация: убрать проверку MetricsUrl в конструкторе поллера — тест краснеет ("no-metrics" в Names).
    // Тест гоняет конструктор поллера напрямую (без StartAsync): StartAsync — явный IHostedService
    // вызов, и он не выполняется синхронно на тестовом потоке.
    [Fact]
    public void EngineWithMetricsUrl_GetsTrack_WithoutMetricsUrl_DoesNot()
    {
        var engines = EnginesFromConfig(
            ("bonsai", "http://127.0.0.1:18030/metrics", null),
            ("no-metrics", null, null));
        var h = new Harness(engines: engines);

        // main — первой, затем движок с MetricsUrl; без MetricsUrl — дорожки нет.
        h.Poller.Names.Should().Equal("main", "bonsai");
        h.Poller.Names.Should().NotContain("no-metrics");
    }

    // (40b) m1 хвост ревью Б2b: движок LocalEngines с именем «main» не даёт второй дорожки main
    // (Find и каталог остаются однозначными): такой движок пропускается с Debug.
    // Мутация: убрать проверку имени в конструкторе — Names = ["main", "main"], тест краснеет.
    [Fact]
    public void EngineNamedMain_IsSkipped_MainStaysUnique()
    {
        var log = new CapturingLogger<LocalModelStatsPoller>();
        var engines = EnginesFromConfig(
            ("main", "http://127.0.0.1:18030/metrics", null),
            ("bonsai", "http://127.0.0.1:18031/metrics", null));
        var h = new Harness(engines: engines, log: log);

        h.Poller.Names.Should().Equal(["main", "bonsai"], "движок с именем main пропущен — дорожка main одна");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("main"),
            "пропуск движка виден в логе (Debug)");
    }

    // (41) c2: рестарт движка в дорожке (счётчики упали, PromptTokens убывает) — окно рвётся,
    // отрицательной скорости нет: приращения null, оконная скорость после разрыва null.
    // Мутация: убрать детект разрыва в UpdateCounters/CounterDelta — краснеет на null-ожиданиях.
    [Fact]
    public async Task EngineTrack_RestartBreaksWindow_NoNegativeSpeed()
    {
        var h = new Harness();
        var bonsai = new FakeSource();
        var bonsaiTrack = MakeTrack(h, "bonsai", () => (bonsai, "llama-server", "bonsai2-27b", null, true));
        h.Poller.AddTrack(bonsaiTrack);

        // t=0: живой движок.
        bonsai.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1000, PromptTokens = 5000, InterTokenLatencySum = 10, Running = 1 });
        await h.Poller.PollTrackAsync(bonsaiTrack);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        // t=5: обычный интервал: +500 токенов.
        bonsai.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 1500, PromptTokens = 6000, InterTokenLatencySum = 15, Running = 1 });
        await h.Poller.PollTrackAsync(bonsaiTrack);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        // t=10: рестарт Bonsai — счётчики упали (GenerationTokens 1500→100, PromptTokens 6000→200).
        bonsai.Snapshots.Enqueue(new EngineSnapshot { GenerationTokens = 100, PromptTokens = 200, InterTokenLatencySum = 1, Running = 0 });
        await h.Poller.PollTrackAsync(bonsaiTrack);

        var history = h.Poller.HistoryOf("bonsai", TimeSpan.FromHours(1));
        history.Should().HaveCount(3);
        history[1].GenThroughput.Should().Be(100.0, "(1500 − 1000)/5 = 100 ток/с");
        history[2].GenThroughput.Should().BeNull("рестарт (счётчик упал) → разрыв окна, а не отрицательная скорость");
        history[2].PromptTokens.Should().BeNull("PromptTokens убывает (рестарт) → приращения нет, не −5800");
        history.Should().NotContain(p => p.GenThroughput < 0, "отрицательных скоростей в кольце не бывает");

        // После разрыва буфера окно не продолжается: базовой точки нет → оконная скорость null.
        h.Poller.CurrentOf("bonsai")!.Kv!.GenPerStream.Should().BeNull("разрыв обрывает буфер — окна нет");
    }

    // (42) M1/H1: опрос висящей дорожки не задерживает соседние: main и bonsai опрашиваются,
    // пока «slow» висит в сети — тик чужой дорожки не ждёт.
    // Мутация: сделать опросы последовательными (сначала `await PollTrackAsync(slowTrack)`) —
    // main/bonsai не опросились бы, пока slow висит: краснеет на Running=1/2.
    [Fact]
    public async Task PollTrack_HangingTrackDoesNotDelayNeighbours()
    {
        var h = new Harness();
        var good = new FakeSource();
        good.Snapshots.Enqueue(new EngineSnapshot { Running = 2 });
        var slow = new BlockingSource();
        var slowTrack = MakeTrack(h, "slow", () => (slow, "llama-server", "m", null, true));
        var bonsaiTrack = MakeTrack(h, "bonsai", () => (good, "llama-server", "bonsai", null, true));
        h.Poller.AddTrack(slowTrack);
        h.Poller.AddTrack(bonsaiTrack);
        h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 1 });

        var slowPoll = h.Poller.PollTrackAsync(slowTrack); // не ждём: медленная дорожка ещё «в сети»
        await h.Poller.PollOnceAsync();
        await h.Poller.PollTrackAsync(bonsaiTrack);

        h.Poller.CurrentOf("main")!.Running.Should().Be(1, "main опросился, не дожидаясь висящей дорожки");
        h.Poller.CurrentOf("bonsai")!.Running.Should().Be(2, "соседняя дорожка опросилась, пока медленная в сети");
        h.Poller.CurrentOf("slow")!.Status.Should().Be("notConnected", "медленная ещё не ответила");

        slow.Release();
        await slowPoll;
        h.Poller.CurrentOf("slow")!.Running.Should().Be(7, "после ответа медленной её опрос завершился");
    }

    // (42b) M1: темп main в настоящем цикле не зависит от висящей второй дорожки: цикл main
    // опрашивает main и ждёт СВОЙ интервал (ActivePollInterval), пока чужая дорожка «в сети».
    // Настоящий цикл (StartAsync) на управляемом ожидании и фейковых часах; таймаут ожидания —
    // реальный (5 с), только чтобы тест не висел вечно при регрессии.
    // Инвариант теста: темп main не зависит от чужой дорожки и не зависит от порядка потоков:
    // фон может опередить Release теста или отстать от него — утверждения только об интервалах.
    // Мутация: вернуть старый цикл (PollAllAsync + общий Wait) — main не доходит до своего Wait,
    // пока висит чужая дорожка: gate не создан, Release() не завершается — TimeoutException.
    [Fact]
    public async Task MainCycleCadence_NotDelayedByHangingTrack()
    {
        var h = new Harness();
        var slow = new BlockingSource();
        h.Poller.AddTrack(MakeTrack(h, "slow", () => (slow, "llama-server", "m", null, true)));
        // main «занят» (Running>0): его интервал — ActivePollInterval (1 с).
        // Снапшоты с запасом (5): к моменту утверждений цикл мог сделать и два опроса, и три —
        // это зависит от порядка потоков, а не от поведения цикла. Лишний опрос обязан остаться
        // на своём интервале, а не упереться в пустую очередь (down → DownPollInterval).
        for (var i = 0; i < 5; i++)
            h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 1 });

        var delays = new List<TimeSpan>();
        var gate = new GateWait();
        h.Poller.WaitFunc = (t, ct) => { delays.Add(t); return gate.Wait(t, ct); };

        var cts = new CancellationTokenSource();
        var run = h.Poller.StartAsync(cts.Token);
        // Цикл живёт на своём потоке: первый опрос main и первый Wait могут случиться и до
        // первого Release (фон опередил), и после него (тест опередил) — порядок не зафиксирован.

        // Каждый Release разрешает текущий Wait и ждёт создания следующего. К моменту
        // утверждений цикл сделал 2 или 3 опроса — в зависимости от порядка потоков; точное
        // число не утверждаем (оно от планирования потоков), утверждаем только интервалы.
        // Таймаут — страховка от вечного ожидания при регрессии.
        await gate.Release().WaitAsync(TimeSpan.FromSeconds(5));
        await gate.Release().WaitAsync(TimeSpan.FromSeconds(5));

        h.Source.ReadCalls.Should().BeGreaterOrEqualTo(2, "main опрашивается снова, не дожидаясь висящей дорожки");
        delays.Should().OnlyContain(t => t == h.Options.CurrentValue.ActivePollInterval,
            "main ждёт свой интервал, а не общий тик по всем дорожкам");
        h.Poller.CurrentOf("slow")!.Status.Should().Be("notConnected", "висящая дорожка не ответила — на main не влияет");

        cts.Cancel();
        await run;
    }

    // (43) H2: посторонний OperationCanceledException от источника — не отмена stoppingToken:
    // PollTrackAsync ловит его как сбой дорожки и не выбрасывает наружу; опросы main живые —
    // каждый следующий снова опрашивает main (рост ReadCalls). Тест зовёт опросы напрямую, а не
    // фоновый цикл: StartAsync не выполняется синхронно на тестовом потоке, и счётчик опросов
    // по релизам gate не сходится.
    // Мутация: убрать OCE-ветку в PollTrackAsync (вернуть `when (ex is not OCE)`) — OCE выходит
    // из опроса, первый же await бросает — тест краснеет.
    [Fact]
    public async Task PollTrack_ForeignCancellationDoesNotKillLoop()
    {
        var h = new Harness();
        var cancellingTrack = MakeTrack(h, "cancelling", () => (new CancellingSource(), "llama-server", "m", null, true));
        h.Poller.AddTrack(cancellingTrack);
        for (var i = 0; i < 3; i++)
            h.Source.Snapshots.Enqueue(new EngineSnapshot { Running = 1 });

        await h.Poller.PollTrackAsync(cancellingTrack); // посторонний OCE не вышел наружу
        await h.Poller.PollOnceAsync();
        var afterFirst = h.Source.ReadCalls;
        afterFirst.Should().BeGreaterThan(0, "main опросился после сбоя чужой дорожки");

        await h.Poller.PollOnceAsync();
        await h.Poller.PollOnceAsync();
        h.Source.ReadCalls.Should().BeGreaterThan(afterFirst, "цикл жив: каждый следующий опрос снова опрашивает main");
        h.Poller.CurrentOf("main")!.Status.Should().Be("busy");
    }

    // (44) H2: Warning об падающей дорожке ограничен по частоте: первая неудача — Warning,
    // повторные в пределах cooldown — Debug; после cooldown — снова Warning.
    // Мутация: писать Warning без cooldown — тест краснеет (Warning = 3).
    [Fact]
    public async Task PollTrack_RepeatedFailure_WarnsOncePerCooldown()
    {
        var log = new CapturingLogger<LocalModelStatsPoller>();
        var h = new Harness(log: log);
        var broken = MakeTrack(h, "broken", () => throw new InvalidOperationException("сбой дорожки"));
        h.Poller.AddTrack(broken);

        await h.Poller.PollTrackAsync(broken);
        await h.Poller.PollTrackAsync(broken);

        log.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(1, "первый сбой — Warning");
        log.Entries.Count(e => e.Level == LogLevel.Debug).Should().Be(1, "повторный сбой в пределах cooldown — Debug");

        h.Time.Advance(TimeSpan.FromMinutes(6));
        await h.Poller.PollTrackAsync(broken);
        log.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(2, "после cooldown сбой виден снова");
    }

    // (45) H3: одновременные AddTrack не теряют дорожек: список иммутабельный, замена ссылки
    // под _tracksGate. Мутация: снять lock в AddTrack (`_tracks = [.. _tracks, track]` без замка) —
    // гонка read-copy-write теряет добавления, счётчик меньше n+1 — тест краснеет.
    [Fact]
    public async Task AddTrack_ConcurrentAdds_LosesNoTracks()
    {
        var h = new Harness();
        const int n = 50;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adds = new Task[n];
        for (var i = 0; i < n; i++)
        {
            var idx = i;
            adds[i] = Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken.None); // все стартуют одним барьером — гонка на _tracks обеспечена
                h.Poller.AddTrack(MakeTrack(h, $"t{idx}", () => (new FakeSource(), null, null, null, false)));
            });
        }
        start.SetResult(true);
        await Task.WhenAll(adds);

        h.Poller.Names.Should().Contain("main");
        h.Poller.Names.Should().HaveCount(n + 1, "все одновременные добавления сохранены (main + n)");
    }

    // ─── Б3b: статус yielded — клиент состояния арбитра в дорожке ────────────────────────────

    // (46) c1: /metrics отказ + арбитр yielded/starting/waiting-vram → статус yielded,
    // DownSince == null. Полная связка: движок из конфига (MetricsUrl+StateUrl) → дорожка,
    // созданная конструктором поллера, получает клиент арбитра; реальный LlamaServerMetricsSource
    // через фейковый обработчик получает 503 → null; фейковый арбитр отвечает состояние.
    // Мутация: передать в BuildEngineTrack null вместо клиента — статус «down», тест краснеет.
    [Theory]
    [InlineData(EngineArbiterSnapshot.StateYielded)]
    [InlineData(EngineArbiterSnapshot.StateStarting)]
    [InlineData(EngineArbiterSnapshot.StateWaitingVram)]
    public async Task EngineTrack_MetricsDownArbiterHolds_StatusYieldedNoDownSince(string state)
    {
        var h = new Harness(engines: EnginesFromConfig(
            ("bonsai", "http://127.0.0.1:18030/metrics", "http://127.0.0.1:19000/state")));
        h.Arbiter.Results.Enqueue(ArbiterState(state));
        var track = h.Poller.Find("bonsai");
        track.Should().NotBeNull("движок с MetricsUrl получил дорожку из конструктора поллера");

        var interval = await h.Poller.PollTrackAsync(track!);

        var cur = h.Poller.CurrentOf("bonsai")!;
        cur.Status.Should().Be("yielded");
        cur.DownSince.Should().BeNull("движок не мёртв: он под арбитром");
        cur.Configured.Should().BeTrue();
        cur.Engine.Should().Be("llama-server");
        cur.Model.Should().Be("bonsai2-27b");
        h.Arbiter.CalledFor.Should().HaveCount(1, "один отказ /metrics — один запрос к арбитру");
        h.Arbiter.CalledFor[0].Should().Be("bonsai", "дорожка спросила арбитра под именем своего движка");
        interval.Should().Be(h.Options.CurrentValue.DownPollInterval,
            "молчащий /metrics опрашивается с интервалом down");
    }

    // (47) c2: без StateUrl — клиента у дорожки нет: молчащий /metrics — обычный down, как у main,
    // DownSince ставится, к арбитру никто не ходил.
    // Мутация: убрать проверку StateUrl в BuildEngineTrack (клиент всем) — Calls = 1, тест краснеет.
    [Fact]
    public async Task EngineTrack_NoStateUrl_MetricsFailureIsDownLikeMain()
    {
        var h = new Harness(engines: EnginesFromConfig(("no-state", "http://127.0.0.1:18030/metrics", null)));
        var track = h.Poller.Find("no-state")!;

        await h.Poller.PollTrackAsync(track);

        var cur = h.Poller.CurrentOf("no-state")!;
        cur.Status.Should().Be("down");
        cur.DownSince.Should().Be(h.Time.GetUtcNow(), "без арбитра — первый сбой фиксируется как у main");
        h.Arbiter.Calls.Should().Be(0, "нет StateUrl — у дорожки нет клиента арбитра");
    }

    // (48) c2: дорожка main никогда не зовёт клиент арбитра — даже когда в реестре есть движок
    // с именем «main» и StateUrl (поллер пропускает его дорожку по m1, но имя живёт в реестре:
    // если бы клиент передали main, GetAsync("main") дошёл бы до фейка).
    // Мутация: передать клиент дорожке main — Calls = 1, тест краснеет.
    [Fact]
    public async Task MainTrack_NeverCallsArbiterClient()
    {
        var h = new Harness(engines: EnginesFromConfig(
            ("main", "http://127.0.0.1:18030/metrics", "http://127.0.0.1:19000/state"),
            ("bonsai", "http://127.0.0.1:18031/metrics", "http://127.0.0.1:19001/state")));
        // main-метрики отказывают: SourceFactoryOverride → пустая очередь → null.

        await h.Poller.PollOnceAsync();

        h.Poller.Current.Status.Should().Be("down", "у main нет арбитра: отказ — обычный down");
        h.Arbiter.Calls.Should().Be(0, "основная модель не стоит ни под чьим управлением: main не зовёт клиент");
    }

    // (49) Ответ арбитра — не подтверждение: unreachable (даже с последним удачным yielded —
    // текущего ответа нет), ready (движок должен отвечать, а /metrics молчит — настоящий сбой),
    // unknown. Статус down, DownSince ставится.
    [Theory]
    [InlineData(false, EngineArbiterSnapshot.StateYielded)]
    [InlineData(true, EngineArbiterSnapshot.StateReady)]
    [InlineData(true, EngineArbiterSnapshot.StateUnknown)]
    public async Task EngineTrack_ArbiterNotConfirming_StatusDownWithDownSince(bool reachable, string state)
    {
        var h = new Harness(engines: EnginesFromConfig(
            ("bonsai", "http://127.0.0.1:18030/metrics", "http://127.0.0.1:19000/state")));
        h.Arbiter.Results.Enqueue(ArbiterState(state, reachable));
        var track = h.Poller.Find("bonsai")!;

        await h.Poller.PollTrackAsync(track);

        var cur = h.Poller.CurrentOf("bonsai")!;
        cur.Status.Should().Be("down");
        cur.DownSince.Should().Be(h.Time.GetUtcNow());
    }

    // (50) down → yielded: первые опросы арбитр не подтвердил (движок считался мёртвым),
    // потом ответил уступку — движок не мёртв: статус yielded, DownSince снимается
    // (ложная тревога не остаётся в снимке).
    // Мутация: не обнулять _downSince в yielded-ветке — DownSince остаётся, тест краснеет.
    [Fact]
    public async Task EngineTrack_DownThenArbiterHolds_ClearsDownSince()
    {
        var h = new Harness(engines: EnginesFromConfig(
            ("bonsai", "http://127.0.0.1:18030/metrics", "http://127.0.0.1:19000/state")));
        var track = h.Poller.Find("bonsai")!;

        h.Arbiter.Results.Enqueue(null); // в этот раз арбитр не ответил
        await h.Poller.PollTrackAsync(track);
        h.Poller.CurrentOf("bonsai")!.Status.Should().Be("down");
        h.Poller.CurrentOf("bonsai")!.DownSince.Should().NotBeNull();

        h.Time.Advance(TimeSpan.FromSeconds(15));
        h.Arbiter.Results.Enqueue(ArbiterState(EngineArbiterSnapshot.StateYielded));
        await h.Poller.PollTrackAsync(track);

        h.Poller.CurrentOf("bonsai")!.Status.Should().Be("yielded");
        h.Poller.CurrentOf("bonsai")!.DownSince.Should().BeNull("движок не мёртв — «момент сбоя» из снимка уходит");
    }

    // Источник, который висит в сети, пока тест не разрешит (медленный движок).
    private sealed class BlockingSource : IEngineMetricsSource
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult(true);

        public async Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
        {
            await _gate.Task.WaitAsync(ct);
            return new EngineSnapshot { Running = 7 };
        }
    }

    // Источник, который бросает посторонний OperationCanceledException (не отмена stoppingToken).
    private sealed class CancellingSource : IEngineMetricsSource
    {
        public Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
            => Task.FromException<EngineSnapshot?>(new OperationCanceledException("посторонняя отмена"));
    }

    // Логгер, пишущий записи в список: для проверки частоты Warning.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
