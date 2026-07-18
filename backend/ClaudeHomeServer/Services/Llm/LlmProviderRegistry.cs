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
    }

    public IReadOnlyCollection<LlmProviderConfig> All => _providers.Values;

    // Пользовательский .claude.json (сосед папки ~/.claude) — источник user-scope
    // MCP-серверов (claude mcp add), которые изолированный профиль провайдера не видит
    public string UserClaudeJsonPath => _userProfileDir + ".json";

    // Папка, где лежат изолированные профили CLI-провайдеров (claude-profiles/{key})
    public string ProfilesDir => _profilesDir;

    // Возвращает пути к projects/ внутри профилей всех включённых провайдеров —
    // для WorkflowAgentParser (транскрипты workflow лежат там, а не в ~/.claude/projects/
    // при использовании стороннего провайдера).
    public IEnumerable<string> GetProviderProjectsDirs() =>
        _providers.Keys.Select(k => Path.Combine(_profilesDir, k, "projects"))
            .Where(d => Directory.Exists(d));

    public IEnumerable<LlmProviderConfig> Enabled => _providers.Values.Where(p => p.Enabled);

    public LlmProviderConfig? GetByKey(string? key) =>
        key is not null && _providers.TryGetValue(key, out var p) ? p : null;

    // Провайдер по модели: сперва точное совпадение с каталогом провайдера,
    // затем по префиксу (модели из GET /models, не описанные в конфиге).
    // null → Claude. Выключенные провайдеры тоже резолвятся — доступность
    // проверяется отдельно (IsAvailable), чтобы отличать «не Claude» от «не настроен».
    public LlmProviderConfig? ResolveByModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        foreach (var p in _providers.Values)
            if (p.FindModel(model) is not null)
                return p;
        foreach (var p in _providers.Values)
            if (model.StartsWith(p.EffectiveModelPrefix, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    // Wire-токен провайдера модели ("claude" | key) — для guard смены провайдера и фронта
    public string ProviderKey(string? model) => ResolveByModel(model)?.Key ?? "claude";

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

    public LlmCapabilities CapabilitiesFor(string? model) =>
        ResolveByModel(model) is { } p ? CapabilitiesOf(p) : LlmCapabilitiesCatalog.Claude;

    // CLI-провайдер наследует весь функционал claude CLI; провайдеро-специфичны
    // только изображения (ограничение API), имя для UI и наличие балансового API
    public static LlmCapabilities CapabilitiesOf(LlmProviderConfig p) => LlmCapabilitiesCatalog.Claude with
    {
        Provider = p.Key,
        DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
        SupportsImages = p.SupportsImages,
        HasBalance = !string.IsNullOrWhiteSpace(p.Balance) && !string.IsNullOrWhiteSpace(p.ApiBaseUrl),
    };

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
    private static readonly string[] SyncFiles = ["CLAUDE.md", "settings.json"];
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
        // ТОЛЬКО полные id: тир-алиасы (opus/sonnet/haiku) CLI резолвит лишь во флаге
        // --model — из ANTHROPIC_MODEL алиас уходит в API сырым id и валит ход
        // «There's an issue with the selected model (opus)» (воспроизведено на проде:
        // чат Алисы с пином opus на подписке my-second). Алиас в env не ставим —
        // модель задаёт --model, который ClaudeSession передаёт всегда.
        if (!string.IsNullOrWhiteSpace(model) && !IsClaudeTierAlias(model))
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
