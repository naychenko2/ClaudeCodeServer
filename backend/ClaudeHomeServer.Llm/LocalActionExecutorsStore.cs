using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Исполнители-движки места (разрез «ночь вики на нескольких локальных исполнителях» §2):
// что админ дал месту, которое умеет вести несколько работ одновременно, — список локальных
// движков, потолок слотов одного владельца и параллельность каждого движка.
//
// Пишется ТОЛЬКО месту с LocalAction.ParallelExecutors (сейчас это одно wiki-nightly):
// остальным местам список не нужен — у них одна работа за один проход цепочки.
//
// Файл data/local-action-executors.json, по ключу места:
//   { "wiki-nightly": { "maxTotal": 2, "executors": [
//       { "engine": "local", "parallel": 1 }, { "engine": "gemma", "parallel": 1 } ] } }
// • engine — только «local» (основной LocalLlm) или имя LocalEngines:*. Ни id модели, ни
//   tier:, ни preset:, ни direct: (И3, путь 5: маршрутом исполнителя ночи становится только
//   локальный движок; эти формы — то, чем админ зовёт облачного исполнителя в другом месте,
//   LocalActionOverridesStore).
// • parallel — 1..MaxConcurrency движка, по умолчанию 1: больше одного окна на движке, чем
//   выдерживает его счётчик слотов, — очередь, а не ускорение (пилот: ×1,07 вместо ×1,13).
// • maxTotal — потолок одновременных слотов одного владельца, 1..4.
// • Порядок списка — приоритет: свободный слот первого получает чат раньше.
// • Пустой список = ключ снят: место идёт по своему маршруту, как до фичи (миграции нет).
//
// Снимок неизменяемый и заменяется ЦЕЛИКОМ (как LocalActionOverridesStore): читатели никогда
// не видят полумутированного состояния. Запись на диск — временный файл с уникальным именем
// и атомарный rename под статическим замком по пути (как FilmStore): читатель во время
// записи видит старую или новую версию файла, но не обрывок.
public sealed class LocalActionExecutorsStore
{
    // Потолок слотов одного владельца (разрез §2): больше — ночь одного владельца спорится
    // за общие карты с чужими ночами и ходами, а не со своей же очередью.
    public const int MaxTotalLimit = 4;

    // Замок СТАТИЧЕСКИЙ по полному пути файла, а не по экземпляру: два PUT в одном процессе
    // и два стора на одном пути (тесты, будущие точки записи) не должны перемешивать снимки —
    // без него последний выигравший в памяти и в файле оказался бы разным.
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Файл пишет camelCase (как ProfileSync/SpecialtySettingsStore); читает любое —
        // его правят и руками, и без регистра ключей.
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _storePath;
    private readonly ILogger<LocalActionExecutorsStore>? _log;
    private readonly IConfiguration _config;

    // Именованные движки — источник имён и их потолка параллельности; реестр провайдеров —
    // потолок основной локали (тот же MaxConcurrentTurns, что берёт её счётчик слотов).
    // Оба опциональны, как в LocalActionOverridesStore/LocalActionRouter: в DI — синглтоны,
    // в тестах — передаются руками. Без них стор знает только «local» и его дефолтный потолок.
    private readonly NamedLocalEngines? _engines;
    private readonly LlmProviderRegistry? _providers;

    private volatile Dictionary<string, LocalActionExecutorsConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    // Имена, о которых уже сказали: список читается на каждую выдачу чата ночи, warning на
    // каждое чтение был бы шумом вместо одного сигнала. Состав пропускаемых имён изменился —
    // говорим снова.
    private volatile HashSet<string> _warnedUnknown = new(StringComparer.OrdinalIgnoreCase);

    public LocalActionExecutorsStore(IConfiguration config,
        ILogger<LocalActionExecutorsStore>? log = null,
        NamedLocalEngines? engines = null, LlmProviderRegistry? providers = null)
    {
        _config = config;
        _log = log;
        _engines = engines;
        _providers = providers;
        // Путь — ТОЛЬКО от DataPath (как у LocalActionOverridesStore): иначе стор лёг бы рядом
        // с исполняемым файлом и настройка потерялась бы при следующем деплое.
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "local-action-executors.json");
        Load();
    }

    // Конфиг исполнителей места; null — исполнителей нет, и место идёт по своему маршруту,
    // как до фичи (пустой список — то же поведение, что отсутствующий).
    //
    // Неизвестное имя движка пропускается ЗДЕСЬ, а не в файле: файл остаётся тем, что в него
    // написал человек, а маршрутом ночи становится только живой движок (И3, путь 6).
    public LocalActionExecutorsConfig? TryGet(string actionKey)
    {
        if (!_configs.TryGetValue(actionKey, out var cfg) || cfg.Executors.Count == 0) return null;

        var kept = new List<LocalActionExecutorConfig>(cfg.Executors.Count);
        List<string>? unknown = null;
        foreach (var e in cfg.Executors)
        {
            if (IsKnownEngine(e.Engine)) kept.Add(e);
            else (unknown ??= []).Add(e.Engine);
        }
        if (unknown is not null) WarnUnknown(unknown);
        // Все имена чужие — живых исполнителей нет: это «нет списка», а не «пустой список».
        return kept.Count == 0 ? null : cfg with { Executors = kept };
    }

    // Ошибки конфигурации; пустой список — конфигурация допустима. Контроллер админа (W2b)
    // по непустому отвечает 400 этим списком: админ обязан видеть, ЧТО именно не принято,
    // а не «запрос отклонён».
    public IReadOnlyList<string> Validate(string actionKey, LocalActionExecutorsConfig config)
    {
        var errors = new List<string>();
        var action = LocalActionCatalog.Find(actionKey);
        if (action is null)
            return ["Места «" + actionKey + "» нет в каталоге фоновых действий"];
        if (!action.ParallelExecutors)
            return ["Место «" + action.Key + "» не ведёт нескольких исполнителей: список движков "
                    + "разрешён только месту с ParallelExecutors"];

        // Пустой список — «снять ключ»: maxTotal при нём не значится и не может блокировать
        // снятие (UI присылает то, что показывает, — и при пустом списке это не 1..4).
        if (config.Executors.Count > 0 && config.MaxTotal is < 1 or > MaxTotalLimit)
            errors.Add($"maxTotal {config.MaxTotal} — допустимо 1..{MaxTotalLimit}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in config.Executors)
        {
            var engine = (e.Engine ?? "").Trim();
            if (engine.Length == 0)
            {
                errors.Add("Исполнитель без имени движка");
                continue;
            }
            if (!seen.Add(engine))
            {
                errors.Add($"Исполнитель «{engine}» назван дважды: один движок — одна строка списка");
                continue;
            }
            // И3, путь 5: исполнителем может быть только локальный движок. Каждая чужая форма
            // называется отдельно — «неизвестный движок» по id модели или слоту админ читал бы
            // как опечатку в имени, а не как отказ от облачного исполнителя.
            if (engine.StartsWith(LocalActionOverridesStore.TierPrefix, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Исполнитель «{engine}» — слот тира: месту с исполнителями годятся только "
                           + "«local» и имена LocalEngines");
            else if (LocalActionOverridesStore.IsPresetRoute(engine))
                errors.Add($"Исполнитель «{engine}» — ссылка на пресет: месту с исполнителями годятся только "
                           + "«local» и имена LocalEngines");
            else if (CloudCheapClient.IsDirectRoute(engine))
                errors.Add($"Исполнитель «{engine}» — прямой облачный адаптер: место с исполнителями "
                           + "не уходит в облако (И3)");
            else if (!IsKnownEngine(engine))
                errors.Add($"Неизвестный локальный движок «{engine}»: есть «local» (основная локаль) "
                           + "и имена LocalEngines:*");

            if (e.Parallel < 1)
                errors.Add($"parallel {e.Parallel} у исполнителя «{engine}» — не меньше 1");
            else if (IsKnownEngine(engine) && e.Parallel > MaxConcurrencyOf(engine))
                errors.Add($"parallel {e.Parallel} у движка «{engine}» — потолок движка "
                           + $"{MaxConcurrencyOf(engine)}");
        }
        return errors;
    }

    // Применить конфигурацию места; пустой список исполнителей — снять ключ.
    // false — конфигурация не прошла Validate (защита: вызывающий обязан калировать через
    // Validate и получить 400, а не молча записать мусор) или запись на диск не удалась:
    // in-memory откатывается к прежнему снимку, иначе настройка выглядела бы применённой
    // и бесследно пропала бы при рестарте (ловушка финальной приёмки LocalActionOverridesStore).
    public bool Save(string actionKey, LocalActionExecutorsConfig config)
    {
        if (LocalActionCatalog.Find(actionKey) is not { } action) return false;
        var errors = Validate(actionKey, config);
        if (errors.Count > 0)
        {
            _log?.LogWarning("Исполнители места «{Action}» не приняты: {Errors}",
                actionKey, string.Join("; ", errors));
            return false;
        }

        var next = new Dictionary<string, LocalActionExecutorsConfig>(_configs, StringComparer.OrdinalIgnoreCase);
        if (config.Executors.Count == 0) next.Remove(action.Key);
        else
        {
            var executors = new List<LocalActionExecutorConfig>(config.Executors.Count);
            foreach (var e in config.Executors) executors.Add(e with { Engine = e.Engine.Trim() });
            next[action.Key] = config with { MaxTotal = config.MaxTotal, Executors = executors };
        }

        lock (FileLocks.GetOrAdd(_storePath, _ => new object()))
        {
            var prev = _configs;
            _configs = next;
            if (!Persist(next))
            {
                _configs = prev;
                return false;
            }
        }
        // Снимок сменился — прежнее «уже сказали» больше не действует.
        _warnedUnknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _log?.LogInformation("Исполнители места «{Action}»: {Count} движков, maxTotal {MaxTotal}",
            action.Key, next.TryGetValue(action.Key, out var w) ? w.Executors.Count : 0,
            next.TryGetValue(action.Key, out var w2) ? w2.MaxTotal : 0);
        return true;
    }

    // Имя живого исполнителя: основная локаль или зарегистрированный LocalEngines:*.
    private bool IsKnownEngine(string? engine) =>
        !string.IsNullOrWhiteSpace(engine)
        && (IsMainLocal(engine) || _engines?.Get(engine) is not null);

    // Ключ основной локали — то же значение, которым админ называет её маршрут
    // (LocalActionOverridesStore.LocalRoute), как в CheapTextRunner.
    private static bool IsMainLocal(string? engine) =>
        string.Equals(engine, LocalActionOverridesStore.LocalRoute, StringComparison.OrdinalIgnoreCase);

    // Потолок параллельности движка — ТОТ ЖЕ, что у его счётчика слотов, иначе валидатор
    // разрешал бы то, что физика не выдержит: у именованного — LocalEngines:{name}:
    // MaxConcurrency (дефолт — DefaultLimit того же TrackedLocalLlmClient), у основной
    // локали — MaxConcurrentTurns провайдера по LocalLlm:EngineKey (тот же расчёт, что у
    // фабрики ILocalLlmClient в LlmSubsystem). Без реестра (юнит-тесты) — дефолт.
    private int MaxConcurrencyOf(string engine) =>
        IsMainLocal(engine)
            ? _providers?.GetByKey(TrackedLocalLlmClient.ReadEngineKey(_config))?.MaxConcurrentTurns
              ?? TrackedLocalLlmClient.DefaultLimit
            : _engines?.Get(engine)?.Spec.MaxConcurrency ?? TrackedLocalLlmClient.DefaultLimit;

    private void WarnUnknown(List<string> unknown)
    {
        var warned = _warnedUnknown;
        if (unknown.All(warned.Contains)) return;
        _log?.LogWarning(
            "{Path}: исполнитель «{Engines}» — не живой локальный движок, пропускаю (место идёт без него)",
            _storePath, string.Join(", ", unknown));
        _warnedUnknown = new HashSet<string>(unknown, StringComparer.OrdinalIgnoreCase);
    }

    private bool Persist(Dictionary<string, LocalActionExecutorsConfig> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            // Временное имя уникально на каждую запись, дальше — rename: два писателя не
            // затирают чужой temp, а читатель не видит половину снимка.
            var tmp = _storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, _storePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Не удалось записать {Path} — настройка исполнителей не сохранена на диск", _storePath);
            return false;
        }
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LocalActionExecutorsConfig>>(
                File.ReadAllText(_storePath), JsonOpts);
            if (loaded is null) return;

            // Ключ вне каталога или без флага ParallelExecutors — мёртвый груз (место
            // переименовали или флаг сняли): его игнорируем, но файл не переписываем —
            // чтение не правит чужую настройку.
            var clean = new Dictionary<string, LocalActionExecutorsConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, cfg) in loaded)
            {
                if (LocalActionCatalog.Find(key) is not { ParallelExecutors: true } action)
                {
                    _log?.LogWarning("{Path}: место «{Action}» не ведёт нескольких исполнителей, игнорирую",
                        _storePath, key);
                    continue;
                }
                clean[action.Key] = cfg;
            }
            _configs = clean;
        }
        catch (Exception ex)
        {
            // Битый файл — ни у кого нескольких исполнителей: места идут по своим маршрутам,
            // как до фичи. Исключение наружу не летит: стор читается на старте контейнера.
            _log?.LogWarning(ex, "Не удалось прочитать {Path}, продолжаю без исполнителей", _storePath);
        }
    }
}

// Строка списка: один движок и сколько работ на нём одновременно. parallel по умолчанию 1 —
// «назначить движок» не означает «разогнать на нём несколько окон» (подсказка UI: больше 1
// ускоряет, только если KV вмещает второе окно).
public sealed record LocalActionExecutorConfig
{
    public string Engine { get; init; } = "";
    public int Parallel { get; init; } = 1;
}

// Конфиг места: потолок слотов одного владельца и исполнители в порядке приоритета.
public sealed record LocalActionExecutorsConfig
{
    public int MaxTotal { get; init; }
    public IReadOnlyList<LocalActionExecutorConfig> Executors { get; init; } = [];
}
