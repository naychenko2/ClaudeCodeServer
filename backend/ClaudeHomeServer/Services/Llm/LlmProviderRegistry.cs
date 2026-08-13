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

    private readonly Dictionary<string, LlmProviderConfig> _providers;
    // Папка изолированных профилей CLI (CLAUDE_CONFIG_DIR) — по одному на провайдера
    private readonly string _profilesDir;
    // Пользовательский профиль CLI (~/.claude) — источник общих настроек для профилей
    // провайдеров; переопределяется ключом ClaudeUserProfileDir (тесты, docker)
    private readonly string _userProfileDir;

    public LlmProviderRegistry(IConfiguration config)
    {
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
        _inheritSystemEnv = config.GetValue("Claude:InheritSystemEnv", false);
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
        var small = string.IsNullOrWhiteSpace(p.SmallModel) ? main : p.SmallModel;
        var env = new Dictionary<string, string>
        {
            // Изолированный профиль CLI: при живом OAuth-логине по подписке CLI предпочитает
            // сохранённый токен и игнорирует ANTHROPIC_AUTH_TOKEN → 401 у провайдера.
            // Отдельный CLAUDE_CONFIG_DIR не видит ~/.claude с OAuth (там же живут
            // транскрипты провайдера для --resume — консистентно, провайдер у сессии фиксирован)
            ["CLAUDE_CONFIG_DIR"] = ProfileDir(p.Key),
            ["ANTHROPIC_BASE_URL"] = p.AnthropicBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = p.ApiKey,
            ["ANTHROPIC_API_KEY"] = p.ApiKey,
            ["ANTHROPIC_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = main,
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = small,
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = small,
        };
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
    // дешёвый инкрементальный синк на каждый ход с троттлингом)
    private void SyncUserProfile(string profileDir)
    {
        if (!Directory.Exists(_userProfileDir)) return;

        foreach (var name in SyncFiles)
            CopyIfNewer(Path.Combine(_userProfileDir, name), Path.Combine(profileDir, name));

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
                CopyIfNewer(src, Path.Combine(profileDir, rel));
            }
        }

        SeedDefaultWorkflows(profileDir);
        EnsureInstalledPluginsEnabled(profileDir);
    }

    // Встроенные механики «Обсудить с командой» (панель экспертов, командный спринт,
    // ревью-консилиум, красная команда) кладутся в профиль ИЗ ПОСТАВКИ приложения, поверх
    // всего, что приехало синком с хоста. Хостовый ~/.claude/workflows живёт вне репозитория:
    // однажды туда попали копии, перекодированные мимо UTF-8, синк разнёс их по всем профилям,
    // и запуск механики падал ещё до первого агента — в перекодированном тексте появляются
    // управляющие символы C1, а CLI отбивает такой скрипт («script contains control characters»).
    // Источник истины — claude-defaults; перезаписываются только одноимённые файлы,
    // личные workflow-скрипты владельца (другие имена) остаются нетронутыми.
    private static void SeedDefaultWorkflows(string profileDir)
    {
        try
        {
            var src = Path.Combine(AppContext.BaseDirectory, "claude-defaults", "workflows");
            if (!Directory.Exists(src)) return;

            var target = Path.Combine(profileDir, "workflows");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(src, "*.js"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Сидинг встроенных механик в {profileDir} не удался: {ex.Message}");
        }
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

    private static void CopyIfNewer(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return;
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dst)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LlmProviders] Синк настройки {src} → {dst} не удался: {ex.Message}");
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
