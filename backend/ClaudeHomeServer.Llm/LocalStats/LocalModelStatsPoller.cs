using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// Фоновый поллер метрик локального стенда (vLLM) + кольцо истории на 1 час в памяти.
// Один цикл гоняет дорожки EngineStatsTrack (состояние и расчёты — в них); сбой одной
// дорожки не мешает остальным. Дорожка `main` — основная модель, её отдаёт ILocalModelStatsReader.
// Источник метрик main выбирается по провайдеру, помеченному IsLocal, и его секции Stats:
//   • нет локального провайдера / нет Stats → notConnected (configured=false), сеть не трогаем;
//   • Stats.Engine = "vllm" + MetricsUrl → VllmMetricsSource (именованный HttpClient);
//   • Stats.Engine = "llama-server" | "ollama" (или пусто) → NullEngineMetricsSource.
// Автоугадывания движка нет: он задаётся явно.
//
// Состояния: working (нет запросов) | busy (running>0 или waiting>0) | down (HTTP-сбой,
// DownSince — момент ПЕРВОГО сбоя) | notConnected. Интервалы: PollInterval (5 с) в покое,
// ActivePollInterval (1 с) пока есть запросы, DownPollInterval (15 с) когда движок мёртв.
public sealed class LocalModelStatsPoller : BackgroundService, ILocalModelStatsReader, ILocalEngineStatsDirectory
{
    // Имя дорожки основной модели.
    public const string MainTrackName = "main";

    // Имя именованного HttpClient для /metrics (регистрация — в LlmSubsystem, AddQuietHttpClient).
    public const string HttpClientName = "local-model-stats";

    // При Enabled=false не ходим в сеть: цикл ждёт и перечитывает настройку раз в 5 с.
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<LocalModelStatsOptions> _options;
    private readonly LlmProviderRegistry _providers;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LocalModelStatsPoller> _log;
    private readonly EngineStatsTrack _main;

    // Дорожки в порядке регистрации; main — первая. Меняется только до старта цикла.
    private readonly List<EngineStatsTrack> _tracks = [];

    // Ожидаемый интервал: по умолчанию Task.Delay; тест подменяет, чтобы гонять цикл
    // на фейковых часах (без реального Task.Delay).
    internal Func<TimeSpan, CancellationToken, Task>? WaitFunc { get; set; }

    public LocalModelStatsPoller(
        IOptionsMonitor<LocalModelStatsOptions> options,
        LlmProviderRegistry providers,
        IHttpClientFactory httpFactory,
        TimeProvider time,
        ILogger<LocalModelStatsPoller> log)
    {
        _options = options;
        _providers = providers;
        _httpFactory = httpFactory;
        _log = log;
        _main = new EngineStatsTrack(MainTrackName, options, ResolveSource, time, log);
        _tracks.Add(_main);
    }

    public LocalModelStatsCurrent Current => _main.Current;

    // Коэффициент живой скорости (ADR-029): токенов на шаг декодирования основной модели.
    public double? TokensPerStep => _main.TokensPerStep;

    public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null) =>
        _main.GetHistory(window, sinceUnixMs);

    public IReadOnlyList<string> Names => _tracks.Select(t => t.Name).ToList();

    public LocalModelStatsCurrent? CurrentOf(string name) => Find(name)?.Current;

    public IReadOnlyList<LocalModelStatsPoint> HistoryOf(string name, TimeSpan window, long? sinceUnixMs = null) =>
        Find(name)?.GetHistory(window, sinceUnixMs) ?? [];

    private EngineStatsTrack? Find(string name) =>
        _tracks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    // Добавляет дорожку; вызывать до старта цикла.
    internal void AddTrack(EngineStatsTrack track) => _tracks.Add(track);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var opts = _options.CurrentValue;
                if (!opts.Enabled)
                {
                    // Не ходим в сеть: ждём и перечитываем настройку.
                    await WaitAsync(DisabledPollInterval, stoppingToken);
                    continue;
                }

                var interval = await PollAllAsync(stoppingToken);
                await WaitAsync(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Нормальная остановка хоста.
        }
    }

    // Опрос основной дорожки: тесты гоняют опросы напрямую, двигая фейковые часы.
    internal Task<TimeSpan> PollOnceAsync(CancellationToken ct = default) => _main.PollOnceAsync(ct);

    // Один тик цикла: опрашивает все дорожки, сбой одной (кроме отмены) не мешает остальным.
    // Возвращает наименьший из интервалов дорожек; у упавшей дорожки — PollInterval.
    internal async Task<TimeSpan> PollAllAsync(CancellationToken ct = default)
    {
        TimeSpan? min = null;
        foreach (var track in _tracks.ToArray())
        {
            TimeSpan interval;
            try
            {
                interval = await track.PollOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Поллер метрик локального стенда: сбой дорожки {Track}", track.Name);
                interval = _options.CurrentValue.PollInterval;
            }
            min = min is { } m && m <= interval ? m : interval;
        }
        return min ?? _options.CurrentValue.PollInterval;
    }

    // Фабрика источника: по умолчанию ResolveSource (по провайдеру). Тест подменяет,
    // чтобы подставить фейковый IEngineMetricsSource.
    internal Func<(IEngineMetricsSource, string?, string?, int?, bool)>? SourceFactoryOverride { get; set; }

    // Источник метрик по локальному провайдеру с секцией Stats.
    // (source, engine, model, tensorParallel, configured): configured=false — notConnected.
    private (IEngineMetricsSource, string?, string?, int?, bool) ResolveSource()
    {
        if (SourceFactoryOverride is { } f)
            return f();
        var local = _providers.All.FirstOrDefault(p => p.IsLocal);
        if (local is null || local.Stats is null)
            return (new NullEngineMetricsSource(), null, FirstModel(local), null, false);

        var engine = local.Stats.Engine.Trim().ToLowerInvariant();
        var model = FirstModel(local);
        var tensorParallel = local.Stats.TensorParallel;
        if (engine == "vllm" && !string.IsNullOrWhiteSpace(local.Stats.MetricsUrl))
            return (new VllmMetricsSource(_httpFactory.CreateClient(HttpClientName), local.Stats.MetricsUrl), engine, model, tensorParallel, true);
        // llama-server / ollama / пусто — заглушка (метрики не отдают Prometheus).
        return (new NullEngineMetricsSource(), string.IsNullOrEmpty(engine) ? null : engine, model, tensorParallel, true);
    }

    private static string? FirstModel(LlmProviderConfig? p) =>
        p?.Models.FirstOrDefault()?.Id;

    // Ожидание: по умолчанию Task.Delay; тест подменяет WaitFunc, чтобы гонять цикл
    // на фейковых часах. OperationCanceledException пробрасывается (нормальная остановка).
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (WaitFunc is { } f)
        {
            await f(delay, ct);
            return;
        }
        await Task.Delay(delay, ct);
    }
}
