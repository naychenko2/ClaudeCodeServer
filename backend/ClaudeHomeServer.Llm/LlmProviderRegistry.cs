using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Реестр CLI-провайдеров (секция конфига "LlmProviders"). Провайдер вычисляется
// из Session.Model и НЕ персистится: единственный источник правды — модель.
// null от ResolveByModel = родной Claude (подписка, без env-оверрайдов).
public class LlmProviderRegistry
{
    public const string Section = "LlmProviders";

    // Заглушка токена для локального провайдера (vLLM/llama.cpp). Локальный сервер
    // авторизацию игнорирует, но claude CLI требует непустой ANTHROPIC_AUTH_TOKEN —
    // иначе отбивает «Not logged in» ещё до запроса. Говорящее значение: при разборе
    // инцидента сразу видно, что это авто-заглушка под IsLocal, а не забытый ключ.
    // Явный ApiKey у локального провайдера (если хозяин всё-таки его задал)
    // не перетирается — на случай прокси перед локальным сервером с реальной авторизацией.
    internal const string LocalNoAuthToken = "local-no-auth";

    private readonly Dictionary<string, LlmProviderConfig> _providers;
    // Папка изолированных профилей CLI (CLAUDE_CONFIG_DIR) — по одному на провайдера
    private readonly string _profilesDir;
    // Пользовательский профиль CLI (~/.claude) — источник общих настроек для профилей
    // провайдеров; переопределяется ключом ClaudeUserProfileDir (тесты, docker)
    private readonly string _userProfileDir;
    // Проба локального эндпоинта для подстановки живого max_model_len (см. BuildCliEnv).
    // null — старый конструктор без DI (тесты без проб); fail-open, поведение прежнее.
    private readonly ILocalEndpointProbe? _localProbe;

    public LlmProviderRegistry(IConfiguration config, ILocalEndpointProbe? localProbe = null)
    {
        _localProbe = localProbe;
        _providers = new Dictionary<string, LlmProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in config.GetSection(Section).GetChildren())
        {
            var cfg = child.Get<LlmProviderConfig>();
            if (cfg is null) continue;
            cfg.Key = child.Key.ToLowerInvariant();
            _providers[cfg.Key] = cfg;
        }

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _profilesDir = Path.Combine(dataDir, "claude-profiles");
        _userProfileDir = config["ClaudeUserProfileDir"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        // Корень поставки (claude-defaults). Тестам нужен собственный, чтобы воспроизвести
        // fail-safe «нет поставки → зона выключена»; у остальных — AppContext.BaseDirectory/claude-defaults.
        _defaultsRoot = config["Claude:DefaultsRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "claude-defaults");
        _inheritSystemEnv = config.GetValue("Claude:InheritSystemEnv", false);
        // ADR-015 §1/§8: рубильник зеркала — off | dryRun | on. Дефолт off; в этой задаче
        // включаем не дальше dryRun. Удаления (`on`) — отдельный этап с корзиной.
        _mirrorMode = config.GetValue("Claude:ProfileSync:Mirror", ProfileMirrorMode.Off);
        // Корзина синка — рядом с data/, вне claude-profiles (ADR-015 §5.3). Здесь же
        // инициализируется, чтобы путь был единым и у NormalizeAll, и у тестов.
        _trashStore = new SyncTrashStore(Path.Combine(dataDir, SyncTrashStore.RootDirName));
    }

    public IReadOnlyCollection<LlmProviderConfig> All => _providers.Values;

    // Пользовательский .claude.json (сосед папки ~/.claude) — источник user-scope
    // MCP-серверов (claude mcp add), которые изолированный профиль провайдера не видит
    public string UserClaudeJsonPath => _userProfileDir + ".json";

    // Папка, где лежат изолированные профили CLI-провайдеров (claude-profiles/{key})
    public string ProfilesDir => _profilesDir;

    // Пользовательский профиль CLI (~/.claude) — корень транскриптов основной подписки
    // (ходы без CLAUDE_CONFIG_DIR-оверрайда); нужен TranscriptMigrator при фейловере
    public string UserProfileDir => _userProfileDir;

    // Возвращает пути к projects/ внутри профилей ВСЕХ сконфигурированных провайдеров —
    // для WorkflowAgentParser (транскрипты workflow и завершения фоновых задач лежат там,
    // а не в ~/.claude/projects/ при использовании стороннего провайдера).
    // Без фильтра по существованию: профиль провайдера создаётся ЛЕНИВО при первом ходе, а
    // регистрация корней — одноразовая на старте. Отфильтруй по Directory.Exists — и провайдер,
    // впервые использованный после старта (как Kimi), остался бы без разрешённого корня, и
    // MainTranscriptTailer/SubagentStreamWatcher не нашли бы его транскрипт (спиннеры навсегда).
    // Несуществующий корень безвреден: резолверы путей всё равно проверяют Directory/File.Exists.
    public IEnumerable<string> GetProviderProjectsDirs() =>
        _providers.Keys.Select(k => Path.Combine(_profilesDir, k, "projects"));

    // Все корни профилей CLI, где может лежать транскрипт: пользовательский ~/.claude плюс
    // РЕАЛЬНО существующие подпапки claude-profiles. Именно с диска, а не по ключам конфига:
    // профили подписок пула зовутся sub-{key} и в _providers их нет, а профиль провайдера
    // создается лениво. Нужен уборке транскриптов при удалении чата (TranscriptMigrator.
    // DeleteEverywhere) — там важно обойти ВСЕ профили, потому что переезды между ними
    // (TryMigrate) оставляют копии. Не путать с GetProviderProjectsDirs: та отдает готовые
    // …/projects для белого списка WorkflowAgentParser и sub-* не покрывает.
    public IEnumerable<string> GetAllConfigRoots()
    {
        yield return _userProfileDir;
        string[] profiles;
        try { profiles = Directory.Exists(_profilesDir) ? Directory.GetDirectories(_profilesDir) : []; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviderRegistry] Не удалось перечислить профили CLI: {ex.Message}");
            yield break;
        }
        foreach (var dir in profiles) yield return dir;
    }

    public IEnumerable<LlmProviderConfig> Enabled => _providers.Values.Where(p => p.Enabled);

    public LlmProviderConfig? GetByKey(string? key) =>
        key is not null && _providers.TryGetValue(key, out var p) ? p : null;

    // Провайдер по модели: сперва точное совпадение с каталогом провайдера,
    // затем по префиксу (модели из GET /models, не описанные в конфиге).
    // null → Claude. Выключенные провайдеры тоже резолвятся — доступность
    // проверяется отдельно (IsAvailable), чтобы отличать «не Claude» от «не настроен».
    // Среди префиксов выигрывает САМЫЙ ДЛИННЫЙ: id агрегатора («deepseek/deepseek-v4-pro»
    // у OpenRouter) начинается с ключа прямого провайдера («deepseek») и без этого
    // правила уходил бы к нему — на чужой эндпоинт с чужим ключом.
    public LlmProviderConfig? ResolveByModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        foreach (var p in _providers.Values)
            if (p.FindModel(model) is not null)
                return p;

        LlmProviderConfig? best = null;
        var bestLen = 0;
        foreach (var p in _providers.Values)
            foreach (var prefix in p.EffectiveModelPrefixes)
                if (prefix.Length > bestLen
                    && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    best = p;
                    bestLen = prefix.Length;
                }
        return best;
    }

    // Wire-токен провайдера модели ("claude" | key) — для guard смены провайдера и фронта
    public string ProviderKey(string? model) => ResolveByModel(model)?.Key ?? "claude";

    // Алиас-тир модели ("opus"|"sonnet"|"haiku") для пина сабагента в frontmatter .md
    // и для чипа модели на карточке персоны-агента. Пинится только тир Claude-модели:
    // алиас безопасен у всех провайдеров — Claude-чат резолвит его в настоящий тир,
    // сторонние маппят env-переменными BuildCliEnv. ID сторонних провайдеров и
    // незнакомые Claude-ID не пинятся (null — без пина). Единая точка — используется
    // PersonaAgentFileSync (генерация .md) и ModelAssignmentResolver (чип карточки).
    public string? ModelTierAlias(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!string.Equals(ProviderKey(model), "claude", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = model.ToLowerInvariant();
        if (m.Contains("opus")) return "opus";
        if (m.Contains("sonnet")) return "sonnet";
        if (m.Contains("haiku")) return "haiku";
        return null;
    }

    // Канонический дефолт родного Claude (подписка) для spend-аналитики: совпадает с алиасом
    // "default" из ClaudeCatalog (ModelCatalogService.Fallback) — стабилен и узнаваем фронтом.
    public const string DefaultClaudeModel = "default";

    // Резолв модели для spend-аналитики: гарантирует непустой SpendRecord.Model. Если модель
    // известна (явный выбор сессии или фактическая из modelUsage) — отдаём как есть. Иначе
    // подтягиваем дефолт: null-модель по построению системы — всегда родной Claude по подписке
    // (сторонние провайдеры требуют модель при создании сессии, см. BuildCliEnv; без env CLI
    // идёт на дефолт подписки). Теоретический сторонний провайдер без модели (невозможно на
    // практике) резолвится в первую модель его каталога — аналог строки 172. Конкретный тир
    // Claude (opus/sonnet/haiku) намеренно НЕ фиксируется: дефолт подписки зависит от контекста
    // CLI (sonnet в основном ходе, haiku на compact/малых операциях) — подстановка sonnet
    // систематически врала бы. Маркер "default" — честное «дефолт подписки, тир неизвестен».
    public string ResolveModelOrDefault(string? model, string? providerKey)
    {
        if (!string.IsNullOrWhiteSpace(model)) return model.Trim();
        if (!string.IsNullOrEmpty(providerKey)
            && GetByKey(providerKey) is { Models: { Count: > 0 } } p)
            return p.Models[0].Id;
        return DefaultClaudeModel;
    }

    // Claude-каталог CLI отдаёт Opus только базовым алиасом с суффиксом окна ("opus[1m]").
    // Базовый алиас ("opus") резолвится надёжно в любом окружении/аккаунте, а "opus[1m]"
    // требует доступа к 1M-окну И прогретого каталога — иначе CLI отбивает «model may not
    // exist / no access» (наблюдалось у проактивности глобальной персоны на проде: ход шёл
    // с --model opus[1m] и падал, хотя прямой вызов той же модели работал). Перед передачей
    // в --model сводим тир-алиас+суффикс к базовому алиасу. Полные id (claude-fable-5[1m]) и
    // модели сторонних провайдеров (glm-5.2[1m]) НЕ трогаем — паттерн матчит только базовые
    // Claude-тир-алиасы opus/sonnet/haiku, у которых базовый алиас гарантированно существует.
    private static readonly System.Text.RegularExpressions.Regex ClaudeTierWindowAlias =
        new(@"^(opus|sonnet|haiku)\[1m\]$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string? StripClaudeWindowAlias(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;
        var match = ClaudeTierWindowAlias.Match(model.Trim());
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : model;
    }

    // Базовый Claude-тир-алиас с суффиксом 1M-окна (opus[1m]/sonnet[1m]/haiku[1m]).
    // Только такие модели требуют проверки способности подписки перед --model: полные id
    // (claude-fable-5[1m]) и модели сторонних провайдеров (glm-5.2[1m]) CLI разбирает сам.
    public static bool IsClaudeTierWindowAlias(string? model) =>
        !string.IsNullOrWhiteSpace(model) && ClaudeTierWindowAlias.IsMatch(model.Trim());

    // Окно контекста РОДНОГО Claude, которое объявляем CLI (CLAUDE_CODE_MAX_CONTEXT_TOKENS).
    // Считается по модели, КОТОРАЯ РЕАЛЬНО УЕДЕТ В --model, т.е. уже после
    // ClaudeSubscriptionPool.ResolveWindowAlias: суффикс [1m] у тир-алиаса срезается, когда
    // в пуле нет живого кандидата с доступом к 1M, и объявить в этом случае 1M хуже, чем не
    // объявлять ничего — CLI не сожмёт контекст вовремя и ход упадёт «Prompt is too long»
    // вместо компакта. Зачем объявлять вообще: суффикс окна живёт только во флаге --model и
    // внутрь сабагента не передаётся (в транскрипте agent-*.jsonl модель идёт без суффикса) —
    // CLI ведёт сабагента в предполагаемых 200k, и точки его обрывов жмутся к этой границе.
    // Значение env наследуется всеми ходами процесса, включая сабагентов.
    public const int ClaudeWindow1M = 1_000_000;
    public const int ClaudeWindowDefault = 200_000;

    // Маржа на расхождение счётчиков токенов между CLI и vLLM. В инциденте промах
    // был ровно 1 токен, 512 ≪ резерва 8192. Единственный регулятор, если расхождение
    // окажется больше.
    public const int WindowSafetyMargin = 512;

    // Шкала усилий рассуждений (от самого лёгкого к самому тяжёлому). Используется в EffortFor
    // для подмены неподдерживаемого провайдером значения на ближайшее СНИЗУ: так «max» у
    // провайдера, который знает только [low, medium, xhigh], становится «xhigh», а не «low»
    // (подмена «сверху» была бы агрессивнее заявленного пользователем уровня).
    private static readonly string[] EffortScale = ["low", "medium", "high", "xhigh", "max"];

    // Подобрать effort, который CLI передаст в --effort, с учётом SupportedEfforts провайдера
    // модели. Точка подмены ОДНА — оба места (ClaudeSession, OneShotClaudeRunner) обязаны
    // звать её, дублировать логику нельзя.
    //
    // Пустой effort (null / "" / пробелы) — это не «не трогать», а «нет запроса»: для
    // провайдера с непустым SupportedEfforts возвращаем самый лёгкий поддерживаемый уровень,
    // иначе CLI подставит свой дефолт (напр. «high» у qwen3.8-27b через vLLM → 400).
    // Для родного Claude и провайдеров с пустым SupportedEfforts (glm/kimi/minimax) — null,
    // флаг --effort не ставится: пусть CLI берёт свой дефолт, как было до подмены.
    //
    // Непустой effort — обычная подмена: EffortMap приоритетнее SupportedEfforts (конфиг
    // доверенный), затем точное совпадение, иначе ближайший снизу по шкале.
    public string? EffortFor(string? model, string? effort)
    {
        var p = ResolveByModel(model);
        var supported = p?.SupportedEfforts
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(effort))
        {
            // Пустой effort: для провайдера с SupportedEfforts — самый лёгкий уровень;
            // иначе null (флаг не ставится).
            return supported.Count > 0 ? PickLightestSupported(supported) : null;
        }

        // Непустой effort: родной Claude (провайдер не нашёлся) — отдать как есть,
        // как и провайдер с пустым SupportedEfforts (fail-open для glm/kimi/minimax).
        if (p is null || supported.Count == 0) return effort;

        // Явная карта EffortMap приоритетнее SupportedEfforts: конфиг доверенный, и
        // «high → medium» декларативнее правила «ближайший снизу». Без проверки значения
        // карты на шкалу/SupportedEfforts: провайдер сам отвечает за корректность подмены
        // (иначе вернётся 400, и фолбэк сам разберётся).
        if (p.EffortMap is { Count: > 0 }
            && p.EffortMap.TryGetValue(effort, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
            return mapped;

        // Точное совпадение (с учётом регистра) — не трогаем
        foreach (var s in supported)
            if (string.Equals(s, effort, StringComparison.OrdinalIgnoreCase))
                return effort;

        // Индекс запрошенного уровня в шкале; -1 — незнакомый CLI-уровень
        // (CLI может завести новый, и значение уйдёт в API сырым — вернётся 400).
        var requestedIdx = -1;
        for (var i = 0; i < EffortScale.Length; i++)
            if (string.Equals(EffortScale[i], effort, StringComparison.OrdinalIgnoreCase))
            { requestedIdx = i; break; }

        // Индексы поддерживаемых уровней в шкале. Усилия, которых в шкале нет
        // (провайдер прислал что-то левое) — игнорируем: они не помогут подменой.
        var supportedIdxs = supported
            .Select(s => Array.FindIndex(EffortScale,
                x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
            .Where(i => i >= 0)
            .ToList();

        // Незнакомый CLI-уровень: ближайший снизу — самый лёгкий из поддерживаемых,
        // чтобы не превысить то, на что провайдер рассчитан.
        if (requestedIdx < 0)
            return supportedIdxs.Count > 0 ? EffortScale[supportedIdxs.Min()] : supported[0];

        // Ближайший снизу: максимальный из supportedIdxs, не превышающий requestedIdx.
        // Если все поддерживаемые «тяжелее» запроса — тоже берём самый лёгкий
        // (а не возвращаем effort как есть: иначе он снова уедет в 400).
        var pick = supportedIdxs.Where(i => i <= requestedIdx).DefaultIfEmpty(-1).Max();
        if (pick < 0) return EffortScale[supportedIdxs.Min()];
        return EffortScale[pick];
    }

    // Самый лёгкий поддерживаемый уровень по шкале EffortScale: для ["low"] → "low",
    // для ["medium","xhigh"] → "medium". Значения вне шкалы (провайдер прислал что-то
    // левое) — игнорируем; если все вне шкалы, возвращаем первый элемент списка,
    // как и существующее правило про «незнакомый CLI-уровень» в EffortFor.
    private static string PickLightestSupported(IReadOnlyList<string> supported)
    {
        var bestIdx = -1;
        var best = supported[0];
        foreach (var s in supported)
        {
            for (var i = 0; i < EffortScale.Length; i++)
                if (string.Equals(EffortScale[i], s, StringComparison.OrdinalIgnoreCase)
                    && (bestIdx < 0 || i < bestIdx))
                {
                    bestIdx = i;
                    best = EffortScale[i];
                    break;
                }
        }
        return bestIdx < 0 ? supported[0] : best;
    }

    public static int ClaudeContextWindow(string? cliModel) =>
        !string.IsNullOrWhiteSpace(cliModel)
        && cliModel.Contains("[1m]", StringComparison.OrdinalIgnoreCase)
            ? ClaudeWindow1M : ClaudeWindowDefault;

    public static string ClaudeContextWindowValue(string? cliModel) =>
        ClaudeContextWindow(cliModel).ToString(CultureInfo.InvariantCulture);

    public LlmCapabilities CapabilitiesFor(string? model) =>
        ResolveByModel(model) is { } p ? CapabilitiesOf(p) : LlmCapabilitiesCatalog.Claude;

    // CLI-провайдер наследует весь функционал claude CLI; провайдеро-специфичны
    // только изображения (ограничение API), имя для UI и наличие балансового API.
    // Тройки тиров — из конфига (чипсы быстрого заполнения слотов на фронте).
    public static LlmCapabilities CapabilitiesOf(LlmProviderConfig p) => LlmCapabilitiesCatalog.Claude with
    {
        Provider = p.Key,
        DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
        SupportsImages = p.SupportsImages,
        // ApiBaseUrl либо BalanceUrl: у alibabacloud ApiBaseUrl пуст (ход идёт через
        // AnthropicBaseUrl, а квота — на отдельном хосте консоли в BalanceUrl)
        HasBalance = !string.IsNullOrWhiteSpace(p.Balance)
            && (!string.IsNullOrWhiteSpace(p.ApiBaseUrl) || !string.IsNullOrWhiteSpace(p.BalanceUrl)),
        Configured = p.Enabled,
        TierStrong = p.TierStrong,
        TierMedium = p.TierMedium,
        TierWeak = p.TierWeak,
    };

    // Переменные «провайдерского режима» — те, которыми мы САМИ рулим маршрутом CLI
    // (см. BuildCliEnv). Их надо вычищать из унаследованного окружения на КАЖДОМ запуске
    // claude, а не только под сторонним провайдером: если такая переменная задана глобально
    // на машине (мастер-рубильник «весь Claude Code на GLM», чужой эксперимент, забытый setx),
    // то ход «на Claude» унаследует чужой эндпоинт и уедет туда с токеном подписки — молча,
    // без единой ошибки в логах. Продукт обязан сам определять свой маршрут целиком.
    //
    // CLAUDE_CODE_OAUTH_TOKEN сюда НЕ входит осознанно: на нём держится вход по подписке,
    // его пробрасывают снаружи (Runner берёт из реестра, docker — per-exec фолбэком
    // в DockerProcessRunner.BuildTurnEnv из окружения бэкенда).
    public static readonly string[] ProviderEnvKeys =
    [
        "CLAUDE_CONFIG_DIR",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_API_KEY",              // перебивает подписку и включает pay-per-token
        "ANTHROPIC_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        "CLAUDE_CODE_SUBAGENT_MODEL",
        "CLAUDE_CODE_AUTO_COMPACT_WINDOW", // окно автокомпакта задают вместе с моделью 1M
        "CLAUDE_CODE_MAX_CONTEXT_TOKENS",  // окно контекста модели, ставим сами (см. BuildCliEnv)
        "MAX_THINKING_TOKENS", // лимит токенов на блок thinking (см. BuildCliEnv)
        "ANTHROPIC_CUSTOM_HEADERS", // заголовки к запросам API, ставим сами (id сессии для прокси)
    ];

    // Что реально вычищаем на запуске. Аварийный выключатель Claude:InheritSystemEnv=true
    // возвращает прежнее поведение (наследовать системные переменные) без пересборки —
    // на случай машины, где ANTHROPIC_* заданы намеренно: свой шлюз к Anthropic или работа
    // по ANTHROPIC_API_KEY вместо подписки. По умолчанию выключено: маршрут определяем мы.
    public IReadOnlyList<string> EnvKeysToClear => _inheritSystemEnv ? [] : ProviderEnvKeys;
    private readonly bool _inheritSystemEnv;

    // Env процесса claude CLI для стороннего провайдера (per-turn: модель может меняться).
    // null → модель родная Claude, env не нужны.
    public IReadOnlyDictionary<string, string>? BuildCliEnv(string? model)
    {
        var p = ResolveByModel(model);
        if (p is null) return null;
        if (!p.Enabled)
            throw new InvalidOperationException(
                $"Провайдер «{p.DisplayName}» не настроен: задай LlmProviders:{p.Key}:ApiKey в appsettings.Local.json");

        var main = string.IsNullOrWhiteSpace(model) ? p.Models.FirstOrDefault()?.Id ?? "" : model!;
        var medium = string.IsNullOrWhiteSpace(p.MediumModel) ? main : p.MediumModel;
        var small = string.IsNullOrWhiteSpace(p.SmallModel) ? main : p.SmallModel;
        // Локальный провайдер без ApiKey: подставляем заглушку (см. LocalNoAuthToken).
        // CLI на пустой строке отбивает «Not logged in» — это и был блокер этапа 1.
        var authToken = string.IsNullOrWhiteSpace(p.ApiKey) && p.IsLocal
            ? LocalNoAuthToken
            : p.ApiKey;
        var env = new Dictionary<string, string>
        {
            // Изолированный профиль CLI: при живом OAuth-логине по подписке CLI предпочитает
            // сохранённый токен и игнорирует ANTHROPIC_AUTH_TOKEN → 401 у провайдера.
            // Отдельный CLAUDE_CONFIG_DIR не видит ~/.claude с OAuth (там же живут
            // транскрипты провайдера для --resume — консистентно, провайдер у сессии фиксирован)
            ["CLAUDE_CONFIG_DIR"] = ProfileDir(p.Key),
            ["ANTHROPIC_BASE_URL"] = p.AnthropicBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = authToken,
            ["ANTHROPIC_API_KEY"] = authToken,
            ["ANTHROPIC_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = main,
            // sonnet-слот — средняя модель провайдера: без неё алиас sonnet (тир-пин
            // персоны-сабагента) схлопывался в модель сессии, и тир strong/medium не различался
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = medium,
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = small,
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = small,
        };
        // Реальное окно контекста модели. Id сторонних моделей CLI не знает и тогда считает
        // окно равным 200k, запуская auto-compact на ~167k при реальном окне до 1M; сам он об
        // этом и предупреждает: «"kimi-k3" is not a model this version of Claude Code
        // recognizes, so auto-compact will keep this session within 200k tokens (the context
        // window it assumes). If the model accepts more, append [1m] to the model name for 1M,
        // or set CLAUDE_CODE_MAX_CONTEXT_TOKENS to its real window». Берём окно из каталога
        // провайдера — системно, вместо суффикса [1m] у каждой модели. Модели в каталоге нет
        // или окно не задано — ключ НЕ ставим (fail-open: пусть CLI решает сам, как раньше).
        // Завышать значение нельзя: тогда CLI не сожмёт контекст вовремя и ход упадёт с
        // ошибкой лимита вместо компакта — числа каталога проверяются живой пробой.
        var contextWindow = p.FindModel(main)?.ContextWindow ?? 0;
        // Локальный провайдер (vLLM/llama.cpp) — окно плавает по стенду (за сутки 71 680 →
        // 61 440 → 65 536 на одной и той же модели), и ручная правка конфига не поспевает.
        // LocalEndpointProbe уже ходит в /v1/models перед ходом и запоминает живое значение
        // в кэше — берём его оттуда синхронно через TryGetKnownContextWindow. Проба не
        // спрашивала / поле отсутствовало → fail-open на каталог (прежнее поведение). Облачных
        // провайдеров (p.IsLocal=false) ветка не касается — там окно из конфига остаётся.
        var liveWindowUsed = false;
        if (p.IsLocal && _localProbe is not null
            && _localProbe.TryGetKnownContextWindow(p.Key, out var liveWindow)
            && liveWindow > 0)
        {
            contextWindow = liveWindow;
            liveWindowUsed = true;
        }
        // vLLM сверяет prompt + max_tokens ≤ max_model_len ДО генерации, поэтому
        // объявляемое CLI окно = max_model_len − резерв под ответ − маржа;
        // иначе auto-compact не успевает и ход падает 400 вместо сжатия.
        var reserve = 0;
        if (p.ExtraEnv.TryGetValue("CLAUDE_CODE_MAX_OUTPUT_TOKENS", out var reserveStr)
            && int.TryParse(reserveStr, out var rv) && rv > 0)
            reserve = rv;
        var declared = (contextWindow > 0 && reserve > 0)
            ? Math.Max(0, contextWindow - reserve - WindowSafetyMargin)
            : contextWindow;

        // Фолбэк на каталог у ЛОКАЛЬНОГО провайдера — единственный путь, где окно можно
        // объявить завышенным: каталожное число статично, а стенд меняет режим (CTX=fast/
        // long/huge дают 65 536 / 98 304 / 245 760). Завышение не деградирует мягко — vLLM
        // сверяет prompt + max_tokens с max_model_len ДО генерации и отвечает мгновенным
        // HTTP 400, то есть ход падает вместо auto-compact. Путь редкий (пустой кэш пробы:
        // первый ход после старта либо таймаут 1.5 с при холодном стенде, а холодный старт
        // занимает минуты), потому и не виден в логах без этой строки — на её отсутствии
        // разбор «какое окно реально объявили» упирался в тупик.
        if (p.IsLocal && !liveWindowUsed && contextWindow > 0)
            Console.Error.WriteLine(
                $"[LlmProviders] {p.Key}: живое окно от пробы недоступно, объявляем "
                + $"declared={declared} (каталог={contextWindow}"
                + (reserve > 0
                    ? $", вычтен резерв={reserve}+маржа={WindowSafetyMargin}"
                    : "")
                + ") — если стенд поднят в другом режиме, ход упадёт "
                + "«Prompt is too long» вместо сжатия контекста");
        if (declared > 0)
            env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = declared.ToString(CultureInfo.InvariantCulture);

        // Лимит токенов на блок thinking. Только локальным/медленным провайдерам сейчас
        // задаём явно (qwen3.8-27b на llama.cpp/vLLM без потолка уходит в минутные размышления);
        // остальные провайдеры (glm/kimi/minimax) оставляем дефолт CLI — null/0.
        if (p.MaxThinkingTokens is int mtt && mtt > 0)
            env["MAX_THINKING_TOKENS"] = mtt.ToString(CultureInfo.InvariantCulture);

        foreach (var (k, v) in p.ExtraEnv)
            env[k] = v;
        return env;
    }

    // Общие настройки пользователя, докладываемые в профиль провайдера (ТОЛЬКО белый
    // список: глобальная память, настройки, правила, скиллы, агенты, команды, workflow-скрипты,
    // плагины). Креденшалы (.credentials.json) НЕ копируем никогда — иначе изоляция теряет смысл
    // и OAuth-токен подписки утёк бы на сторонний эндпоинт.
    // settings.json в этом списке НЕТ намеренно: он не копируется файлом, а мержится
    // по ключам (MergeSettingsInto) — у профиля там свои значения, копия хостового их стирала
    private static readonly string[] SyncFiles = ["CLAUDE.md"];
    private static readonly string[] SyncDirs = ["rules", "skills", "agents", "commands", "workflows", "plugins"];

    // Троттлинг синка: не чаще раза в 5 минут на провайдера
    private static readonly TimeSpan SyncTtl = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastSync = new();

    // ADR-015: режим зеркала (off по умолчанию). В этой задаче включаем не дальше dryRun.
    private readonly ProfileMirrorMode _mirrorMode;

    // Корзина синка профилей (ADR-015 §5.3). Все удаления в режиме on идут через неё.
    private readonly SyncTrashStore _trashStore;

    internal SyncTrashStore TrashStore => _trashStore;

    // Корень поставки claude-defaults (по ADR-015 §3 fail-safe). Дефолт — рядом с exe;
    // тесты подставляют свой, чтобы воспроизвести отсутствие поставки.
    private readonly string _defaultsRoot;

    // Для тестов и подписок: читают адрес профиля
    public string GetProfileDir(string key) => Path.Combine(_profilesDir, key);

    private string ProfileDir(string key)
    {
        var dir = Path.Combine(_profilesDir, key);
        try
        {
            Directory.CreateDirectory(dir);
            var last = _lastSync.GetOrAdd(key, DateTime.MinValue);
            if (DateTime.UtcNow - last >= SyncTtl && _lastSync.TryUpdate(key, DateTime.UtcNow, last))
                SyncUserProfile(dir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Не удалось подготовить профиль CLI {dir}: {ex.Message}");
        }
        return dir;
    }

    // Копирует общие настройки из ~/.claude в профиль провайдера (только новее по mtime —
    // дешёвый инкрементальный синк на каждый ход с троттлингом). Дополнительно ведёт
    // .sync-manifest.json (ADR-015 §3): для каждого доставленного файла заносится запись
    // {отн.путь, зона, источник, размер, mtime}. Только host-источник; defaults-файлы
    // (поставка) заносятся в SeedDefaultWorkflows.
    private void SyncUserProfile(string profileDir)
    {
        if (!Directory.Exists(_userProfileDir)) return;

        var manifest = ProfileSyncManifestStore.LoadOrEmpty(profileDir);
        var delivered = 0;

        foreach (var name in SyncFiles)
        {
            if (CopyAndRecord(
                    Path.Combine(_userProfileDir, name),
                    Path.Combine(profileDir, name),
                    profileDir, name, "host", manifest))
                delivered++;
        }

        MergeSettingsInto(
            Path.Combine(_userProfileDir, "settings.json"),
            Path.Combine(profileDir, "settings.json"));

        foreach (var sub in SyncDirs)
        {
            var srcDir = Path.Combine(_userProfileDir, sub);
            if (!Directory.Exists(srcDir)) continue;
            foreach (var src in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_userProfileDir, src);
                // .git клонов marketplace в plugins/ — десятки тысяч объектов, CLI они не нужны
                if (rel.Split('\\', '/').Contains(".git")) continue;
                if (CopyAndRecord(src, Path.Combine(profileDir, rel), profileDir, sub, "host", manifest))
                    delivered++;
            }
        }

        var seededCount = SeedDefaultWorkflows(profileDir, manifest);
        EnsureInstalledPluginsEnabled(profileDir);

        if (delivered > 0 || seededCount > 0)
            ProfileSyncManifestStore.Save(profileDir, manifest);

        Console.WriteLine($"[ProfileSync] {Path.GetFileName(profileDir)}: доставлено={delivered}, seed из поставки={seededCount}");
    }

    // Копирует src→dst, если источник новее, и при успехе заносит/обновляет запись в манифесте.
    // Возвращает true, если файл был доставлен (скопирован) в этом вызове.
    private static bool CopyAndRecord(string src, string dst, string profileDir, string zone, string source,
        ProfileSyncManifest manifest)
    {
        try
        {
            if (!File.Exists(src)) return false;
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dst)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Синк настройки {src} → {dst} не удался: {ex.Message}");
            return false;
        }

        try
        {
            var info = new FileInfo(dst);
            var rel = MakeManifestKey(profileDir, dst);
            manifest.Files[rel] = new ProfileSyncEntry
            {
                Zone = zone,
                Source = source,
                Size = info.Length,
                Mtime = info.LastWriteTimeUtc,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileSync] Запись манифеста для {dst} не удалась: {ex.Message}");
        }
        return true;
    }

    // Относительный путь от корня профиля через "/". Path.GetRelativePath на Linux
    // чувствителен к регистру — а у нас ключи без учёта регистра; через "/"
    // получаем кросс-платформенный канонический вид.
    private static string MakeManifestKey(string profileDir, string fullPath) =>
        Path.GetRelativePath(profileDir, fullPath).Replace('\\', '/');

    // Зоны mirror без поставки — источник всегда host (~/.claude).
    // Для них fail-safe не применяется.
    private static readonly string[] MirrorDirs = ["rules", "commands", "agents"];

    // Зеркальные зоны с двумя источниками: defaults + host. По ADR-015 §3 файл,
    // принадлежащий поставке (source="defaults"), не считается кандидатом на удаление
    // даже при отсутствии в host-источнике. Fail-safe: нет claude-defaults/{зона}
    // (дев-стенд из bin/Debug) → вся зона выключается на этом проходе, иначе мы бы
    // удалили default-файлы, которых seed не вернёт.
    // В этой задаче `skills` пропускается в расчёте выводов целиком (ADR-015 §9.1 — открытый
    // вопрос владельца каталога). `workflows` считается штатно.
    private static readonly string[] SeedMirrorDirs = ["skills", "workflows"];

    // ─── ADR-015 §5.4: триггер нормализации ────────────────────────────────────
    // Обход всех подпапок _profilesDir. На ходе профиля SyncUserProfile пишет только
    // то, что доставил сейчас; openrouter без обхода _profilesDir не вылечится никогда.
    // Что делает:
    //   1. Усыновление — если манифеста нет, текущий состав mirror-зон заносится
    //      в манифест (ADR-015 §5.1). Один проход на профиль, дальше — инкрементально.
    //   2. Mirror-расчёт — для каждого пути из манифеста проверяет три условия §3:
    //      (1) путь в манифесте; (2) в источнике больше нет; (3) в профиле не изменился
    //      (size+mtime == манифесту). Только в dryRun/on; в режиме off — только
    //      усыновление и выход.
    //   3. В dryRun — без записи (только отчёт). В on — реальное удаление по манифесту.
    public List<ProfileMirrorReport> NormalizeAll()
    {
        var reports = new List<ProfileMirrorReport>();
        if (!Directory.Exists(_profilesDir))
        {
            Console.WriteLine($"[ProfileSync] Нет каталога профилей {_profilesDir}");
            return reports;
        }

        // Уборка корзины — один раз за проход, а не на каждом профиле: ретенция общая.
        var purged = _trashStore.PurgeOld(DateTime.UtcNow - SyncTrashStore.Retention);
        if (purged > 0)
            Console.WriteLine($"[ProfileSync] Корзина {_trashStore.RootPath}: убрано {purged} устаревших проходов (>{SyncTrashStore.Retention.TotalDays:F0}д)");

        foreach (var profileDir in Directory.GetDirectories(_profilesDir))
        {
            try { reports.Add(NormalizeOneProfile(profileDir)); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ProfileSync] Нормализация {profileDir} не удалась: {ex.Message}");
            }
        }
        return reports;
    }

    private ProfileMirrorReport NormalizeOneProfile(string profileDir)
    {
        var name = Path.GetFileName(profileDir);
        var report = new ProfileMirrorReport { Profile = name };
        var manifest = ProfileSyncManifestStore.LoadOrEmpty(profileDir);

        // 1. Усыновление: пустой манифест — заносим текущий состав mirror-зон профиля.
        var adopted = false;
        if (manifest.Files.Count == 0)
        {
            AdoptProfileFiles(profileDir, manifest);
            ProfileSyncManifestStore.Save(profileDir, manifest);
            adopted = true;
        }

        if (_mirrorMode == ProfileMirrorMode.Off)
        {
            // В режиме off — только усыновление, без зеркального расчёта.
            if (adopted)
                Console.WriteLine($"[ProfileSync] {name}: усыновление {manifest.Files.Count} файлов (mirror=off, расчёт пропущен)");
            return report;
        }

        // 2. Зеркальный расчёт.
        var manifestSizeBefore = manifest.Files.Count;
        CalculateMirrorDivergence(profileDir, manifest, report);

        // В dryRun ничего реально не меняется; в on — удалённые файлы вычищаются из манифеста.
        var dirty = _mirrorMode == ProfileMirrorMode.On
                    || manifest.Files.Count != manifestSizeBefore;
        if (dirty) ProfileSyncManifestStore.Save(profileDir, manifest);

        PrintSummary(name, report, adopted);
        return report;
    }

    // Усыновление: обходим mirror-зоны профиля и заносим все существующие файлы
    // в манифест как «принесённые синком». Source: «defaults» если файл лежит в
    // claude-defaults/{зона}/{тот же путь}, иначе «host». plugins/ и прочие
    // hands-off зоны не трогаем — там территория CLI.
    private void AdoptProfileFiles(string profileDir, ProfileSyncManifest manifest)
    {
        var zones = new (string zone, bool isFile)[]
        {
            ("CLAUDE.md", true),
            ("rules", false),
            ("commands", false),
            ("agents", false),
            ("skills", false),
            ("workflows", false),
        };

        // Снимок путей, принадлежащих поставке, для разметки source=defaults.
        var defaultsKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in zones)
        {
            if (z.isFile) continue;
            var dDir = Path.Combine(_defaultsRoot, z.zone);
            if (!Directory.Exists(dDir)) continue;
            foreach (var f in Directory.EnumerateFiles(dDir, "*", SearchOption.AllDirectories))
            {
                var rel = MakeManifestKey(dDir, f);
                defaultsKeys.Add(z.zone + "/" + rel);
            }
        }

        foreach (var (zone, isFile) in zones)
        {
            var profilePath = Path.Combine(profileDir, zone);
            if (isFile)
            {
                if (!File.Exists(profilePath)) continue;
                AddAdoptedEntry(profileDir, manifest, zone, profilePath, "host");
            }
            else
            {
                if (!Directory.Exists(profilePath)) continue;
                foreach (var f in Directory.EnumerateFiles(profilePath, "*", SearchOption.AllDirectories))
                {
                    if (f.Split('\\', '/').Contains(".git")) continue;
                    var rel = MakeManifestKey(profileDir, f);
                    var source = defaultsKeys.Contains(rel) ? "defaults" : "host";
                    AddAdoptedEntry(profileDir, manifest, zone, f, source);
                }
            }
        }
    }

    private static void AddAdoptedEntry(string profileDir, ProfileSyncManifest manifest, string zone,
        string fullPath, string source)
    {
        try
        {
            var info = new FileInfo(fullPath);
            var rel = MakeManifestKey(profileDir, fullPath);
            manifest.Files[rel] = new ProfileSyncEntry
            {
                Zone = zone,
                Source = source,
                Size = info.Length,
                Mtime = info.LastWriteTimeUtc,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileSync] Усыновление {fullPath} не удалось: {ex.Message}");
        }
    }

    // Зеркальный расчёт. В dryRun — без записи; в on — реальное удаление.
    private void CalculateMirrorDivergence(string profileDir, ProfileSyncManifest manifest,
        ProfileMirrorReport report)
    {
        var skippedDueToFailsafe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seedZone in SeedMirrorDirs)
        {
            var dDir = Path.Combine(_defaultsRoot, seedZone);
            if (!Directory.Exists(dDir))
                skippedDueToFailsafe.Add(seedZone);
        }

        var toDelete = new List<string>();
        var toPruneFromManifest = new List<string>();

        foreach (var (rel, entry) in manifest.Files.ToArray())
        {
            // Skills пропускаем в этой задаче — ADR-015 §9.1 владелец открыт.
            if (entry.Zone == "skills") continue;

            // Только mirror-зоны (CLAUDE.md, rules, commands, agents, workflows).
            // plugins/projects/sessions/settings.json — hands-off, в манифесте их нет.
            var isMirror = entry.Zone == "CLAUDE.md"
                          || MirrorDirs.Contains(entry.Zone)
                          || entry.Zone == "workflows";
            if (!isMirror) continue;

            // defaults-файлы принадлежат поставке — никогда не удалять (ADR-015 §3).
            if (entry.Source == "defaults") continue;

            // Fail-safe: для seed-mirror-зон без claude-defaults/{зона} — пропустить.
            if (SeedMirrorDirs.Contains(entry.Zone) && skippedDueToFailsafe.Contains(entry.Zone))
            {
                if (!report.SkippedZones.Contains(entry.Zone))
                    report.SkippedZones.Add(entry.Zone);
                continue;
            }

            // (2) В источнике больше нет?
            var srcPath = Path.Combine(_userProfileDir, rel);
            if (File.Exists(srcPath)) continue;

            // Файла в источнике нет. Проверяем профиль и условие (3).
            var dstPath = Path.Combine(profileDir, rel);
            if (!File.Exists(dstPath))
            {
                // В профиле тоже нет — по факту уже удалён. вычищаем запись из манифеста,
                // чтобы следующий проход не считал его divergent.
                toPruneFromManifest.Add(rel);
                continue;
            }

            var info = new FileInfo(dstPath);
            if (info.Length != entry.Size || info.LastWriteTimeUtc != entry.Mtime)
            {
                // (3) нарушено — ручная правка в профиле. Не удалять, в WARN.
                report.Divergent.Add(rel);
                continue;
            }

            // Все три условия: путь в манифесте; в источнике нет; профиль не менялся.
            toDelete.Add(rel);
        }

        // В режиме on — реальное удаление; в dryRun — только список «удалил бы».
        if (_mirrorMode == ProfileMirrorMode.On)
        {
            var stamp = DateTime.UtcNow;
            var profileName = Path.GetFileName(profileDir);
            foreach (var rel in toDelete)
            {
                var dstPath = Path.Combine(profileDir, rel);
                // Сначала переносим в корзину; если перенос не удался — НЕ удаляем
                // из профиля и НЕ убираем из манифеста (следующий проход попробует снова).
                if (!_trashStore.Move(profileName, profileDir, rel, stamp))
                    continue;
                try
                {
                    File.Delete(dstPath);
                }
                catch (Exception ex)
                {
                    // Копия в корзине уже есть, из профиля убрать не смогли — оставляем
                    // оба места, в манифесте НЕ убираем: следующий проход повторит попытку.
                    Console.Error.WriteLine($"[ProfileSync] Удаление {dstPath} после переноса в корзину не удалось: {ex.Message}");
                    continue;
                }
                toPruneFromManifest.Add(rel);
                report.Trashed.Add(rel);
            }
        }
        report.WouldDelete.AddRange(toDelete);

        // Чистим манифест от записей, чьих файлов уже нет (удалили в on, или были удалены снаружи).
        foreach (var rel in toPruneFromManifest)
            manifest.Files.Remove(rel);
    }

    private void PrintSummary(string profile, ProfileMirrorReport report, bool adopted)
    {
        var tag = adopted ? "усыновлён " : "";
        // В on-сводке виден и счётчик «унесено в корзину» — без него пришлось бы
        // сверять WouldDelete с реальным удалением через лог.
        Console.WriteLine(
            $"[ProfileSync] {profile} {tag}: wouldDelete={report.WouldDelete.Count} " +
            $"trashed={report.Trashed.Count} divergent={report.Divergent.Count} " +
            $"skippedZones=[{string.Join(",", report.SkippedZones)}]");
    }

    // Встроенные механики «Обсудить с командой» (панель экспертов, командный спринт,
    // ревью-консилиум, красная команда) кладутся в профиль ИЗ ПОСТАВКИ приложения, поверх
    // всего, что приехало синком с хоста. Хостовый ~/.claude/workflows живёт вне репозитория:
    // однажды туда попали копии, перекодированные мимо UTF-8, синк разнёс их по всем профилям,
    // и запуск механики падал ещё до первого агента — в перекодированном тексте появляются
    // управляющие символы C1, а CLI отбивает такой скрипт («script contains control characters»).
    // Источник истины — claude-defaults; перезаписываются только одноимённые файлы,
    // личные workflow-скрипты владельца (другие имена) остаются нетронутыми.
    //
    // Если передан манифест, заносим defaults-файлы в него с source="defaults" — иначе
    // mirror-расчёт (ADR-015 §3) мог бы счесть их «удалил бы»: они же не в ~/.claude.
    private int SeedDefaultWorkflows(string profileDir, ProfileSyncManifest? manifest)
    {
        var count = 0;
        try
        {
            var src = Path.Combine(_defaultsRoot, "workflows");
            if (!Directory.Exists(src)) return 0;

            var target = Path.Combine(profileDir, "workflows");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(src, "*.js"))
            {
                var name = Path.GetFileName(file);
                var dst = Path.Combine(target, name);
                File.Copy(file, dst, overwrite: true);
                count++;
                if (manifest != null)
                {
                    try
                    {
                        var info = new FileInfo(dst);
                        manifest.Files["workflows/" + name] = new ProfileSyncEntry
                        {
                            Zone = "workflows",
                            Source = "defaults",
                            Size = info.Length,
                            Mtime = info.LastWriteTimeUtc,
                        };
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ProfileSync] Запись defaults в манифест {dst} не удалась: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Сидинг встроенных механик в {profileDir} не удался: {ex.Message}");
        }
        return count;
    }

    // Плагины CLI, установленные владельцем, включаются профилю самим сервером. Раньше
    // enabledPlugins попадал в профиль только копией из хостового settings.json — стоило тому
    // обеднеть (а CLI переписывает его целиком, например при смене модели), и во всех профилях
    // плагины оказывались выключены при живой установке: половина механик («Автопилот»,
    // «Консенсус-план», «Интервью», «QA-цикл», «Трассировка», «Анализ кода») зовёт скиллы
    // oh-my-claudecode и отвечала «Unknown command», хотя карточка механики была активна.
    // Ставим true только отсутствующим ключам — осознанное выключение плагина переживает синк.
    private static void EnsureInstalledPluginsEnabled(string profileDir)
    {
        try
        {
            var manifest = Path.Combine(profileDir, "plugins", "installed_plugins.json");
            if (!File.Exists(manifest)) return;
            if (JsonNode.Parse(File.ReadAllText(manifest)) is not JsonObject root ||
                root["plugins"] is not JsonObject installed || installed.Count == 0) return;

            var settingsPath = Path.Combine(profileDir, "settings.json");
            JsonObject? settings = null;
            if (File.Exists(settingsPath))
                try { settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject; }
                catch (JsonException) { }
            settings ??= [];

            if (settings["enabledPlugins"] is not JsonObject enabled)
            {
                enabled = [];
                settings["enabledPlugins"] = enabled;
            }

            var changed = false;
            foreach (var plugin in installed)
            {
                if (enabled.ContainsKey(plugin.Key)) continue;
                enabled[plugin.Key] = true;
                changed = true;
            }
            if (!changed) return;

            File.WriteAllText(settingsPath, settings.ToJsonString(SettingsJsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Включение плагинов в профиле {profileDir} не удалось: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };

    // Синк settings.json — НЕ копия файла, а мерж по ключам. Профильный settings.json
    // не копия хостового: в нём живут собственные значения профиля — env маршрута
    // провайдера, permissions.allow, enabledPlugins. File.Copy поверх стирал их молча
    // (отвал ANTHROPIC_BASE_URL = ход провайдера уходит на родной эндпоинт или падает).
    //
    // ПРАВИЛО КОНФЛИКТА (ключ есть и в хостовом, и в профильном файле):
    //   • ветка env — сильнее ПРОФИЛЬНОЕ значение: оно задаёт маршрут CLI, а хостовое
    //     там заведомо неверно (оно про подписку, а профиль — про сторонний эндпоинт);
    //   • все остальные ключи — сильнее ХОСТОВОЕ: это общая настройка пользователя,
    //     профиль обязан её подхватывать.
    // Вложенные объекты сливаются рекурсивно (поэтому профильный permissions.allow
    // переживает хостовый permissions.deny), массивы и скаляры заменяются целиком.
    // Ключи, которых нет в источнике, в профиле сохраняются всегда.
    private static void MergeSettingsInto(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return;
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dst)) return;

            if (JsonNode.Parse(File.ReadAllText(src)) is not JsonObject host) return;

            JsonObject? profile = null;
            if (File.Exists(dst))
            {
                // Битый профильный файл — не повод потерять синк: перезаписываем хостовым
                try { profile = JsonNode.Parse(File.ReadAllText(dst)) as JsonObject; }
                catch (JsonException) { }
            }

            var merged = profile is null ? (JsonObject)host.DeepClone() : MergeSettings(profile, host);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllText(dst, merged.ToJsonString(SettingsJsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Мерж settings.json {src} → {dst} не удался: {ex.Message}");
        }
    }

    private static JsonObject MergeSettings(JsonObject profile, JsonObject host)
    {
        var result = MergeObjects(profile, host, profileWins: false);
        // env — единственная ветка, где профильное значение сильнее хостового
        if (profile["env"] is JsonObject profileEnv && host["env"] is JsonObject hostEnv)
            result["env"] = MergeObjects(profileEnv, hostEnv, profileWins: true);
        return result;
    }

    // Накладывает host на profile: объекты рекурсивно, прочее — целиком.
    // profileWins=true — при конфликте остаётся профильное значение (ветка env).
    private static JsonObject MergeObjects(JsonObject profile, JsonObject host, bool profileWins)
    {
        var result = (JsonObject)profile.DeepClone();
        foreach (var (key, hostValue) in host)
        {
            if (!result.TryGetPropertyValue(key, out var profileValue) || profileValue is null)
            {
                result[key] = hostValue?.DeepClone();
                continue;
            }
            if (profileValue is JsonObject po && hostValue is JsonObject ho)
                result[key] = MergeObjects(po, ho, profileWins);
            else if (!profileWins)
                result[key] = hostValue?.DeepClone();
        }
        return result;
    }

    // Построить env для дополнительной OAuth-подписки Claude (см. ClaudeSubscriptionPool).
    // Изолированный CLAUDE_CONFIG_DIR + .credentials.json из OAuthToken, БЕЗ ANTHROPIC_AUTH_TOKEN
    // и ANTHROPIC_BASE_URL — процесс использует родной эндпоинт Anthropic.
    // Если у подписки есть ApiKey — используем ANTHROPIC_AUTH_TOKEN (как CLI-провайдеры,
    // но без ANTHROPIC_BASE_URL, т.к. эндпоинт родной).
    // null → подписка не найдена или неактивна.
    public IReadOnlyDictionary<string, string>? BuildOAuthCliEnv(
        string subKey, string oauthToken, string? apiKey = null, string? model = null)
    {
        var env = new Dictionary<string, string>();
        var profileDir = ProfileDir("sub-" + subKey);
        env["CLAUDE_CONFIG_DIR"] = profileDir;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // API-ключ: ставим ANTHROPIC_AUTH_TOKEN, как для CLI-провайдеров
            env["ANTHROPIC_AUTH_TOKEN"] = apiKey;
            env["ANTHROPIC_API_KEY"] = apiKey;
        }
        else if (!string.IsNullOrWhiteSpace(oauthToken))
        {
            // Токен от claude setup-token: передаём через CLAUDE_CODE_OAUTH_TOKEN (env),
            // изолированный профиль не видит родительский OAuth-токен основного аккаунта
            env["CLAUDE_CODE_OAUTH_TOKEN"] = oauthToken;
        }
        else
        {
            return null; // нечего ставить — неактивна
        }

        // Модель-дефолты (как для CLI-провайдеров, но без ANTHROPIC_BASE_URL).
        // ТОЛЬКО полные id: тир-алиасы (opus/sonnet/haiku, в т.ч. с суффиксом окна
        // opus[1m]/sonnet[1m]/haiku[1m]) CLI резолвит лишь во флаге --model — из
        // ANTHROPIC_MODEL алиас уходит в API сырым id и валит ход «There's an issue
        // with the selected model (opus[1m])» (воспроизведено на проде). Полные id
        // с окном (claude-fable-5[1m]) и модели сторонних провайдеров (glm-5.2[1m])
        // сюда не относятся — их суффикс разбирает сам CLI, env им нужен.
        // Модель задаёт --model, который ClaudeSession передаёт всегда.
        if (!string.IsNullOrWhiteSpace(model)
            && !IsClaudeTierAlias(model)
            && !IsClaudeTierWindowAlias(model))
        {
            env["ANTHROPIC_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
        }

        return env;
    }

    // Тир-алиас Claude (opus/sonnet/haiku, регистронезависимо) — не полный id модели
    internal static bool IsClaudeTierAlias(string model) =>
        model.Equals("opus", StringComparison.OrdinalIgnoreCase)
        || model.Equals("sonnet", StringComparison.OrdinalIgnoreCase)
        || model.Equals("haiku", StringComparison.OrdinalIgnoreCase);

    // Модель РОДНОГО Claude (подписка), а не неизвестный id. Нужна там, где null от
    // ResolveByModel надо прочитать однозначно: он означает и «родной Claude», и «такой
    // модели нет ни у одного провайдера» — а последствия у этих двух случаев разные
    // (переезд на подписку против честного отказа). Форма id: пусто (решает CLI), алиас
    // каталога default/opus/sonnet/haiku (в т.ч. с суффиксом окна opus[1m]) и полный
    // id Anthropic claude-* (claude-fable-5[1m] и пр.). Знание о форме id живёт здесь же,
    // рядом с остальными разборщиками алиасов.
    //
    // Две оговорки для того, кто будет править:
    // 1) Корректность держится на ПОРЯДКЕ вызовов — ResolveByModel зовётся ПЕРЕД этим
    //    предикатом (SessionManager.MigrateProviderAsync). Сторонний провайдер, объявивший
    //    модель с id claude-* или перехвативший алиас, резолвится в себя, и до предиката
    //    управление не доходит; переставленные местами проверки дадут ложноположительные.
    // 2) Список форм — ручная копия ModelCatalogService.Fallback: реальный каталог родных
    //    моделей приходит от живого CLI (QueryCliAsync → models[].value), а сюда он не
    //    заглядывает. Появился новый алиас в каталоге CLI — дописать и здесь, иначе выбор
    //    родной модели из выпадающего списка даст ложное «модель не найдена».
    public static bool IsNativeClaudeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        var m = model.Trim();
        return m.Equals(DefaultClaudeModel, StringComparison.OrdinalIgnoreCase)
            || IsClaudeTierAlias(m)
            || IsClaudeTierWindowAlias(m)
            || m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);
    }

    // Стоимость хода по ценам конфига модели. CLI на чужом эндпоинте считает
    // total_cost_usd по ценам Anthropic — доверять ему нельзя, пересчитываем сами.
    // null — модель родная Claude или цены не заданы (стоимость не показываем).
    public double? ComputeCost(string? model, UsageInfo? usage)
    {
        if (usage is null) return null;
        var m = ResolveByModel(model)?.FindModel(model);
        if (m is null || (m.PriceInMissPer1M == 0 && m.PriceOutPer1M == 0)) return null;
        // cache_creation тарифицируется как обычный (miss) вход
        return (usage.InputTokens * m.PriceInMissPer1M
                + usage.CacheCreationTokens * m.PriceInMissPer1M
                + usage.CacheReadTokens * m.PriceInHitPer1M
                + usage.OutputTokens * m.PriceOutPer1M) / 1_000_000;
    }
}
