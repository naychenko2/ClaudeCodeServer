namespace ClaudeHomeServer.Services.Llm;

// Именованные локальные движки для фоновых мест — ВТОРОЙ и далее llama-server рядом с основным
// `LocalLlm` (тот один на инстанс и делит GPU с ходами исполнителя). Зачем отдельно: дежурная
// модель на другой машине (Bonsai 2 на «Video») живёт под арбитром GPU и в любой момент может
// быть выгружена ради медиазадачи — место обязано узнать об этом мгновенно (проба готовности),
// а не 20-секундным таймаутом, и уйти по своей цепочке дальше.
//
// Конфиг — секция `LocalEngines:{name}`: поля транспорта как у `LocalLlm` (BaseUrl, Model,
// TextModel, TimeoutMs, DisableThinking, ApiKey), плюс:
//   • `ReadyUrl`      — GET, 200 = движок готов (у арбитра GPU — `/ready`); пусто — без пробы;
//   • `StateUrl`      — полный ответ арбитра GPU (`/state`), читает LocalEngineStateClient
//                       (статус «уступила/вернулась», причины); пусто — арбитра нет;
//   • `MetricsUrl`    — GET /metrics движка (флаг --metrics у llama-server): с ним поллер
//                       LocalModelStatsPoller заводит дорожку статистики на этот движок (Б2b);
//                       пусто — движок в панели «Локальные модели» не показывается;
//   • `FallbackRoute` — шаг после неудачи движка, перед финальным claude: `direct:<model>`
//                       (прямой адаптер, например MiniMax) — других значений нет;
//   • `NumCtx`        — потолок контекста движка (окно слота llama-server): профиль места
//                       с большим NumCtx режется до него при обрезке промпта;
//   • `Actions`       — массив ключей мест каталога, которые идут на этот движок, пока админ не
//                       переопределил место в UI (приоритет: выбор админа → `Ollama:Actions` →
//                       `LocalEngines:*:Actions` → дефолт каталога).
// Движок без BaseUrl/Model или без мест — не регистрируется (warning), остальные работают.
// MetricsUrl и Model — init-свойства, не позиционные параметры: у spec positional-конструкторы
// живут в чужих тестах (графли Б3a: новый позиционный аргумент занял чужую позицию), а эти поля
// нужны только дорожке статистики (Б2b) и не меняют существующие вызовы.
public sealed record LocalEngineSpec(
    string Name,
    string? ReadyUrl,
    string? StateUrl,
    string? FallbackRoute,
    int? NumCtx,
    IReadOnlySet<string> Actions)
{
    // GET /metrics движка (--metrics у llama-server): пусто — дорожки статистики нет.
    public string? MetricsUrl { get; init; }

    // Имя модели для снимка дорожки (Model из конфига движка); на отображение, не для запроса.
    public string? Model { get; init; }
}

public sealed class LocalEngine(LocalEngineSpec spec, ILocalLlmClient client,
    Func<CancellationToken, Task<bool>>? readyProbe)
{
    public LocalEngineSpec Spec { get; } = spec;
    public ILocalLlmClient Client { get; } = client;
    public string Name => Spec.Name;
    public bool Enabled => Client.Enabled;

    // Готов ли движок прямо сейчас. Без ReadyUrl — считаем готовым (решит сам вызов).
    // Результат кэшируется на ReadyCacheMs: фон бьёт десятками мест подряд, а проба — HTTP.
    private readonly Func<CancellationToken, Task<bool>>? _probe = readyProbe;
    private bool _lastReady = true;
    private long _lastProbeTicks;
    private const int ReadyCacheMs = 1000;

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        if (_probe is null) return true;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProbeTicks) < ReadyCacheMs) return _lastReady;
        _lastReady = await _probe(ct);
        Interlocked.Exchange(ref _lastProbeTicks, now);
        return _lastReady;
    }
}

public sealed class NamedLocalEngines
{
    public const string SectionName = "LocalEngines";
    public const string HttpClientName = "local-engine-ready";

    private readonly Dictionary<string, LocalEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byAction = new(StringComparer.OrdinalIgnoreCase);

    public NamedLocalEngines(IHttpClientFactory http, IConfiguration config, ILoggerFactory loggers,
        Spend.ISpendCollector? spend = null)
    {
        var log = loggers.CreateLogger<NamedLocalEngines>();
        foreach (var section in config.GetSection(SectionName).GetChildren())
        {
            var name = section.Key.Trim();
            var options = ReadOptions(section);
            if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.Model))
            {
                log.LogWarning("LocalEngines:{Name} — нет BaseUrl или Model, движок пропущен", name);
                continue;
            }
            var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in section.GetSection("Actions").GetChildren())
            {
                if (string.IsNullOrWhiteSpace(a.Value)) continue;
                if (!LocalActionCatalog.IsKnown(a.Value))
                {
                    log.LogWarning("LocalEngines:{Name}:Actions — неизвестный ключ места «{Action}», игнорирую", name, a.Value);
                    continue;
                }
                actions.Add(a.Value);
            }
            var fallback = section["FallbackRoute"]?.Trim();
            if (!string.IsNullOrWhiteSpace(fallback) && !CloudCheapClient.IsDirectRoute(fallback))
            {
                log.LogWarning(
                    "LocalEngines:{Name}:FallbackRoute «{Route}» — поддерживается только direct:<model>, шаг страховки выключен",
                    name, fallback);
                fallback = null;
            }
            var spec = new LocalEngineSpec(name, section["ReadyUrl"]?.Trim(), section["StateUrl"]?.Trim(),
                fallback, int.TryParse(section["NumCtx"], out var numCtx) && numCtx > 0 ? numCtx : null, actions)
            { MetricsUrl = section["MetricsUrl"]?.Trim() };
            // Свой ключ слота на каждый движок: потолок не делится ни с основным LocalLlm, ни с соседями
            var maxConcurrency = int.TryParse(section["MaxConcurrency"], out var mc) && mc > 0
                ? mc : TrackedLocalLlmClient.DefaultLimit;
            var client = new TrackedLocalLlmClient(
                new LlamaServerClient(http, options, loggers.CreateLogger<LlamaServerClient>(), spend),
                LocalEngineBusyTracker.Instance, name, () => maxConcurrency,
                loggers.CreateLogger<TrackedLocalLlmClient>());
            Func<CancellationToken, Task<bool>>? probe = null;
            if (!string.IsNullOrWhiteSpace(spec.ReadyUrl))
            {
                var url = spec.ReadyUrl;
                probe = async ct =>
                {
                    try
                    {
                        var c = http.CreateClient(HttpClientName);
                        c.Timeout = TimeSpan.FromMilliseconds(800);
                        using var r = await c.GetAsync(url, ct);
                        return r.IsSuccessStatusCode;
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        return false;
                    }
                };
            }
            spec = spec with { Model = options.TextModel };
            Add(new LocalEngine(spec, client, probe));
            log.LogInformation("LocalEngines:{Name} — {Url}, модель {Model}, мест {Count}, страховка {Fallback}",
                name, options.BaseUrl, options.TextModel, actions.Count, fallback ?? "нет");
        }
    }

    // Для тестов: готовые движки без конфига и HTTP.
    internal NamedLocalEngines(IEnumerable<LocalEngine> engines)
    {
        foreach (var e in engines) Add(e);
    }

    private void Add(LocalEngine engine)
    {
        _engines[engine.Name] = engine;
        foreach (var a in engine.Spec.Actions)
            _byAction.TryAdd(a, engine.Name);
    }

    private static LocalLlmOptions ReadOptions(IConfigurationSection s)
    {
        var model = (s["Model"] ?? "").Trim();
        var textModel = (s["TextModel"] ?? "").Trim();
        return new LocalLlmOptions(
            Provider: LocalLlmOptions.LlamaServer,
            BaseUrl: (s["BaseUrl"] ?? "").Trim().TrimEnd('/'),
            ApiKey: s["ApiKey"] ?? "",
            Model: model,
            TextModel: string.IsNullOrWhiteSpace(textModel) ? model : textModel,
            TimeoutMs: int.TryParse(s["TimeoutMs"], out var t) ? t : 20_000,
            KeepAlive: "-1",
            DisableThinking: !string.Equals(s["DisableThinking"], "false", StringComparison.OrdinalIgnoreCase));
    }

    public bool Any => _engines.Count > 0;

    // Движки в порядке регистрации (для поллера статистики: дорожка на каждый с MetricsUrl).
    public IReadOnlyCollection<LocalEngine> Engines => _engines.Values;

    // Имя движка, назначенного месту конфигом; null — место на движки не назначено.
    public string? EngineFor(string actionKey) =>
        _byAction.TryGetValue(actionKey, out var name) ? name : null;

    public LocalEngine? Get(string? name) =>
        name is not null && _engines.TryGetValue(name, out var e) ? e : null;

    public bool Enabled(string? name) => Get(name)?.Enabled == true;
}
