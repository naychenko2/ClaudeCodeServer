using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// Фоновый поллер метрик локального стенда (vLLM) + кольцо истории на 1 час в памяти.
// На каждую дорожку EngineStatsTrack (состояние и расчёты — в ней) — свой цикл: дорожка ждёт
// свой интервал и не блокирует соседние — период main не зависит от наличия и живости
// других дорожек (M1 ревью Б2b). Дорожка `main` — основная модель, её отдаёт
// ILocalModelStatsReader. Дополнительно — дорожка на каждый именованный движок
// LocalEngines:{name} с MetricsUrl (имя дорожки = имя движка; движок с именем «main»
// пропускается — main однозначен, хвост m1 ревью Б2b): источник — LlamaServerMetricsSource
// через тихий клиент EngineMetricsHttpClientName.
// Источник метрик main выбирается по провайдеру, помеченному IsLocal, и его секции Stats:
//   • нет локального провайдера / нет Stats → notConnected (configured=false), сеть не трогаем;
//   • Stats.Engine = "vllm" + MetricsUrl → VllmMetricsSource (именованный HttpClient);
//   • Stats.Engine = "llama-server" | "ollama" (или пусто) → NullEngineMetricsSource.
// Автоугадывания движка нет: он задаётся явно.
//
// Состояния: working (нет запросов) | busy (running>0 или waiting>0) | down (HTTP-сбой,
// DownSince — момент ПЕРВОГО сбоя) | yielded (дежурная модель под арбитром: /metrics молчит,
// арбитр отвечает yielded/starting/waiting-vram — DownSince не ставится, Б3b) | notConnected.
// Интервалы: PollInterval (5 с) в покое, ActivePollInterval (1 с) пока есть запросы,
// DownPollInterval (15 с) когда движок мёртв или выгружен. Клиент состояния арбитра
// (IEngineArbiterStateReader) достается только дорожкам движков с StateUrl; дорожка main
// его не получает вовсе — основная модель не стоит ни под чьим управлением.
public sealed class LocalModelStatsPoller : BackgroundService, ILocalModelStatsReader, ILocalEngineStatsDirectory
{
    // Имя дорожки основной модели.
    public const string MainTrackName = "main";

    // Имя именованного HttpClient для /metrics (регистрация — в LlmSubsystem, AddQuietHttpClient).
    public const string HttpClientName = "local-model-stats";

    // Имя тихого HttpClient для /metrics именованных движков (регистрация — в LlmSubsystem):
    // своя категория, чтобы мёртвый сосед не глушил жалобы основного стенда (и наоборот).
    public const string EngineMetricsHttpClientName = "local-engine-metrics";

    // Тип движка в снимке дорожки: именованные LocalEngines — всегда llama-server
    // (транспорт NamedLocalEngines.ReadOptions).
    private const string EngineKindLlamaServer = "llama-server";

    // При Enabled=false не ходим в сеть: цикл ждёт и перечитывает настройку раз в 5 с.
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(5);

    // Пауза между Warning об одной и той же падающей дорожке: повторные сбои — Debug,
    // после паузы или после успешного опроса — снова Warning.
    private static readonly TimeSpan WarningCooldown = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<LocalModelStatsOptions> _options;
    private readonly LlmProviderRegistry _providers;
    private readonly IEngineArbiterStateReader _arbiter;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalModelStatsPoller> _log;
    private readonly EngineStatsTrack _main;

    // Дорожки в порядке регистрации; main — первая. Список иммутабельный: AddTrack под
    // _tracksGate заменяет ссылку новым массивом, старт циклов и читатели берут снимок под тем
    // же замком — конкурентной мутации списка нет (H3 ревью Б1).
    private readonly object _tracksGate = new();
    private EngineStatsTrack[] _tracks;

    // Момент последнего Warning по имени дорожки (для частоты Warning) + её замок:
    // обработчики сбойов живут в продолжениях циклов разных дорожек, на разных потоках.
    private readonly object _warningGate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastWarningAt = new();

    // Ожидаемый интервал: по умолчанию Task.Delay; тест подменяет, чтобы гонять цикл
    // на фейковых часах (без реального Task.Delay).
    internal Func<TimeSpan, CancellationToken, Task>? WaitFunc { get; set; }

    public LocalModelStatsPoller(
        IOptionsMonitor<LocalModelStatsOptions> options,
        LlmProviderRegistry providers,
        NamedLocalEngines engines,
        IEngineArbiterStateReader arbiter,
        IHttpClientFactory httpFactory,
        TimeProvider time,
        ILogger<LocalModelStatsPoller> log)
    {
        _options = options;
        _providers = providers;
        _arbiter = arbiter;
        _httpFactory = httpFactory;
        _time = time;
        _log = log;
        // main — без клиента арбитра: основная модель не стоит ни под чьим управлением,
        // её молчание — обычный down (Б3b).
        _main = new EngineStatsTrack(MainTrackName, options, ResolveSource, time, log);
        _tracks = [_main];
        // Дорожка на каждый именованный движок с MetricsUrl (Б2b); без MetricsUrl — дорожки нет.
        // Добавляем здесь, до старта циклов: ExecuteAsync к этому моменту ещё не запущен.
        foreach (var engine in engines.Engines)
        {
            if (string.IsNullOrWhiteSpace(engine.Spec.MetricsUrl)) continue;
            // Имя дорожки должно быть однозначным: движок LocalEngines с именем «main» дал бы
            // вторую main — Find и каталог перестали быть однозначными (хвост m1 ревью Б2b).
            if (string.Equals(engine.Name, MainTrackName, StringComparison.Ordinal))
            {
                _log.LogDebug("Поллер метрик локального стенда: движок LocalEngines:{Name} совпадает с именем основной дорожки — пропущен", engine.Name);
                continue;
            }
            AddTrack(BuildEngineTrack(engine));
        }
    }

    // Дорожка именованного движка: источник — LlamaServerMetricsSource (единственный транспорт
    // LocalEngines), имя дорожки = имя движка, тип в снимке — llama-server.
    // Клиент состояния арбитра достается только движкам с StateUrl (Б3b): без StateUrl арбитра
    // нет — молчание /metrics для такой дорожки — обычный down, как у main.
    private EngineStatsTrack BuildEngineTrack(LocalEngine engine)
    {
        var metricsUrl = engine.Spec.MetricsUrl!;
        var model = engine.Spec.Model;
        Func<CancellationToken, Task<EngineArbiterSnapshot?>>? arbiter =
            string.IsNullOrWhiteSpace(engine.Spec.StateUrl)
                ? null
                : ct => _arbiter.GetAsync(engine.Name, ct);
        return new EngineStatsTrack(engine.Name, _options, () => (
            new LlamaServerMetricsSource(_httpFactory.CreateClient(EngineMetricsHttpClientName), metricsUrl),
            EngineKindLlamaServer, model, null, true), _time, _log, arbiter);
    }

    public LocalModelStatsCurrent Current => _main.Current;

    // Коэффициент живой скорости (ADR-029): токенов на шаг декодирования основной модели.
    public double? TokensPerStep => _main.TokensPerStep;

    public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null) =>
        _main.GetHistory(window, sinceUnixMs);

    public IReadOnlyList<string> Names => TracksSnapshot().Select(t => t.Name).ToList();

    public LocalModelStatsCurrent? CurrentOf(string name) => Find(name)?.Current;

    public IReadOnlyList<LocalModelStatsPoint> HistoryOf(string name, TimeSpan window, long? sinceUnixMs = null) =>
        Find(name)?.GetHistory(window, sinceUnixMs) ?? [];

    // internal — тесты Б3b берут дорожку, созданную конструктором поллера (полная связка
    // «движок из конфига → дорожка с клиентом арбитра»), а не собранную MakeTrack.
    internal EngineStatsTrack? Find(string name) =>
        TracksSnapshot().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    // Добавляет дорожку; вызывать до старта цикла. Список не мутируется на ходу:
    // под _tracksGate создаётся новый массив, старое чтение видит старый снимок.
    internal void AddTrack(EngineStatsTrack track)
    {
        lock (_tracksGate)
            _tracks = [.. _tracks, track];
    }

    private EngineStatsTrack[] TracksSnapshot()
    {
        lock (_tracksGate)
            return _tracks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Свой цикл на каждую дорожку (M1 ревью Б2b): дорожка ждёт свой интервал и не блокирует
        // соседние — период main не зависит от наличия и живости других дорожек. Снимок берётся
        // один раз: AddTrack вызывается до старта циклов.
        await Task.WhenAll(TracksSnapshot().Select(t => RunTrackAsync(t, stoppingToken)));
    }

    // Цикл одной дорожки: опрос → ожидание интервала, который вернула сама дорожка, → снова опрос.
    // Настоящая остановка хоста — нормальное завершение цикла; всё остальное, включая посторонний
    // OperationCanceledException от источника, гасится в PollTrackAsync как сбой дорожки —
    // цикл жив (H2 ревью Б1).
    private async Task RunTrackAsync(EngineStatsTrack track, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_options.CurrentValue.Enabled)
                {
                    // Не ходим в сеть: ждём и перечитываем настройку.
                    await WaitAsync(DisabledPollInterval, ct);
                    continue;
                }

                var interval = await PollTrackAsync(track, ct);
                await WaitAsync(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Нормальная остановка хоста.
        }
    }

    // Опрос основной дорожки: тесты гоняют опросы напрямую, двигая фейковые часы.
    internal Task<TimeSpan> PollOnceAsync(CancellationToken ct = default) => _main.PollOnceAsync(ct);

    // Опрос одной дорожки. Настоящая остановка хоста (отмена ct) выходит как есть — её ловит
    // RunTrackAsync как нормальное завершение. Всё остальное, включая посторонний
    // OperationCanceledException от источника, — сбой дорожки: цикл продолжается (H2 ревью Б1).
    // internal — тесты гоняют опрос конкретной дорожки напрямую, на фейковых часах.
    internal async Task<TimeSpan> PollTrackAsync(EngineStatsTrack track, CancellationToken ct = default)
    {
        try
        {
            var interval = await track.PollOnceAsync(ct);
            lock (_warningGate)
                _lastWarningAt.Remove(track.Name); // дорожка ответила: следующий сбой снова Warning
            return interval;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogTrackFailure(track, ex);
            return _options.CurrentValue.PollInterval;
        }
    }

    // Warning об одной дорожке не чаще раза в WarningCooldown: повторные сбои того же тика и
    // последующих — Debug. Первая неудача после успеха (или после паузы) — снова Warning.
    private void LogTrackFailure(EngineStatsTrack track, Exception ex)
    {
        var now = _time.GetUtcNow();
        lock (_warningGate)
        {
            var warn = !_lastWarningAt.TryGetValue(track.Name, out var last) || now - last >= WarningCooldown;
            if (warn)
            {
                _lastWarningAt[track.Name] = now;
                _log.LogWarning(ex, "Поллер метрик локального стенда: сбой дорожки {Track}", track.Name);
            }
            else
            {
                _log.LogDebug(ex, "Поллер метрик локального стенда: повторный сбой дорожки {Track}", track.Name);
            }
        }
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
