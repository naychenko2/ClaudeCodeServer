using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Хранит историю снимков использования лимитов подписки (data/usage.json) для экрана usage.
// Снимки приходят с каждым ходом (через RateLimitMessage) — троттлим, чтобы не плодить дубли,
// и прунем старые. In-memory список + ленивое сохранение в файл под локом (как sessions.json).
public class UsageService
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(8); // покрывает недельное окно + запас
    private const double UtilEpsilon = 0.01;

    private readonly string _storePath;
    private readonly object _lock = new();
    private readonly List<UsageSnapshot> _snapshots = new();
    private static readonly JsonSerializerOptions _opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Версия набора снимков: растёт на каждом РЕАЛЬНОМ записи (Record, когда снимок добавлен,
    // а не отброшен троттлингом). Нужна инвалидации кэша /api/usage: ответ меняется только
    // когда записан новый снимок (состояние пула/опроса — отдельные версии, см. UsageResponseCache).
    private long _version;
    public long Version => Interlocked.Read(ref _version);

    public UsageService(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "usage.json");
        Load();
    }

    // Регистрирует снимок. Троттлинг: пропускаем, если для этого окна последний снимок свежий
    // (<3 мин) И значение/статус практически не изменились. Иначе — добавляем, прунем, сохраняем.
    public void Record(string limitType, double? utilization, string? status, bool isUsingOverage,
        string? resetsAt, string? overageStatus = null, string? overageResetsAt = null,
        string? subscriptionKey = null, string? source = null, string? overageDisabledReason = null)
    {
        if (string.IsNullOrEmpty(limitType) && utilization is null) return;
        var subKey = subscriptionKey ?? "claude";
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var last = _snapshots.LastOrDefault(s => s.LimitType == limitType && s.SubscriptionKey == subKey);
            if (last is not null
                && now - last.Timestamp < Throttle
                && last.Status == status
                && last.OverageStatus == overageStatus
                && Math.Abs((last.Utilization ?? 0) - (utilization ?? 0)) < UtilEpsilon)
                return; // дубль в окне троттлинга — не пишем

            _snapshots.Add(new UsageSnapshot(now, limitType, utilization, status, isUsingOverage, resetsAt, overageStatus, overageResetsAt, subKey, source, overageDisabledReason));

            var cutoff = now - Retention;
            _snapshots.RemoveAll(s => s.Timestamp < cutoff);

            // Снимок реально добавлен — версия набора изменилась (кэш /api/usage инвалидируется).
            Interlocked.Increment(ref _version);

            Save();
        }
    }

    // Тариф подписки из ~/.claude/.credentials.json (subscriptionType + rateLimitTier → ярлык).
    // Читаем при каждом запросе — файл маленький, может обновиться после re-login.
    public PlanInfo? GetPlan()
    {
        try
        {
            var credsPath = CredsPath();
            if (!File.Exists(credsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(credsPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            var subType = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null;
            var tier = oauth.TryGetProperty("rateLimitTier", out var rt) ? rt.GetString() : null;
            return new PlanInfo(subType, tier, PlanLabel(subType, tier));
        }
        catch { return null; }
    }

    // Путь к ~/.claude/.credentials.json — единственная точка (GetPlan и PlanVersion).
    private static string CredsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    // Версия файла тарифа (mtime в тиках, 0 — файла нет): для инвалидации кэша /api/usage.
    // Тариф меняется редко (re-login), но при смене кэш обязан пересобраться, а не ждать
    // следующего снимка.
    public long PlanVersion
    {
        get
        {
            try
            {
                var credsPath = CredsPath();
                return File.Exists(credsPath) ? new FileInfo(credsPath).LastWriteTimeUtc.Ticks : 0;
            }
            catch { return 0; }
        }
    }

    private static string PlanLabel(string? subType, string? tier)
    {
        var t = tier ?? "";
        if (string.Equals(subType, "max", StringComparison.OrdinalIgnoreCase))
        {
            if (t.Contains("20x")) return "Max 20×";
            if (t.Contains("5x")) return "Max 5×";
            return "Max";
        }
        if (string.Equals(subType, "pro", StringComparison.OrdinalIgnoreCase)) return "Pro";
        return string.IsNullOrEmpty(subType) ? "—" : subType!;
    }

    public IReadOnlyList<UsageSnapshot> GetAll()
    {
        lock (_lock) return _snapshots.ToList();
    }

    // Снимки, сгруппированные по ключу подписки ("claude", "my-second", …).
    // Для каждой подписки — её снимки, отсортированные по времени.
    public Dictionary<string, List<UsageSnapshot>> GetAllBySubscription()
    {
        lock (_lock)
        {
            return _snapshots
                .GroupBy(s => s.SubscriptionKey)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToList());
        }
    }

    // Проекция «последний снимок на (подписку, окно)» — для ЛЁГКОГО ответа /api/usage?scope=limits.
    // Шапке чата нужны только ТЕКУЩИЕ состояния окон (latestPerWindow/withAccountFallback на фронте),
    // а не 8-дневная история: она весит мегабайты, а в ответе дублируется дважды (плоско и per-sub).
    // На каждую (подписку, окно) отдаём не более ТРЁХ снимков:
    //   • самый свежий в целом (resetsAt/статус/сброс — от него шапка строит окно);
    //   • самый свежий С ПРОЦЕНТОМ, если он не тот же (latestPerWindow подставляет процент того же
    //     окна, когда свежее событие пришло без utilization);
    //   • самый свежий из УЖЕ СБРОШЕННЫХ окон, если он не совпадает с двумя выше (маркер stale:
    //     окно сбросилось, а свежего снимка ещё нет — фронт рисует прочерк «данные устарели»).
    // Всё остальное из истории для этих трёх потребностей не нужно — отсюда в десятки раз меньше.
    public Dictionary<string, List<UsageSnapshot>> GetLatestPerWindow()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var byKey = new Dictionary<string, List<UsageSnapshot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _snapshots.GroupBy(s => (Key: s.SubscriptionKey, Window: s.LimitType)))
            {
                UsageSnapshot? latest = null, latestWithUtil = null, latestExpired = null;
                foreach (var s in group)
                {
                    if (latest is null || s.Timestamp > latest.Timestamp) latest = s;
                    if (s.Utilization is not null && (latestWithUtil is null || s.Timestamp > latestWithUtil.Timestamp))
                        latestWithUtil = s;
                    if (IsExpired(s.ResetsAt, now) && (latestExpired is null || s.Timestamp > latestExpired.Timestamp))
                        latestExpired = s;
                }
                if (latest is null) continue;

                if (!byKey.TryGetValue(group.Key.Key, out var list))
                {
                    list = new List<UsageSnapshot>();
                    byKey[group.Key.Key] = list;
                }
                list.Add(latest);
                if (latestWithUtil is not null && !ReferenceEquals(latestWithUtil, latest))
                    list.Add(latestWithUtil);
                if (latestExpired is not null
                    && !ReferenceEquals(latestExpired, latest)
                    && !ReferenceEquals(latestExpired, latestWithUtil))
                    list.Add(latestExpired);
            }
            return byKey;
        }
    }

    // Окно «сброшено»: resetsAt в прошлом (тот же признак, что expired() на фронте).
    private static bool IsExpired(string? resetsAt, DateTime now)
        => resetsAt is not null
            && DateTime.TryParse(resetsAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            && t <= now;

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<UsageSnapshot>>(json, _opts);
            if (list is not null)
            {
                var cutoff = DateTime.UtcNow - Retention;
                _snapshots.AddRange(list.Where(s => s.Timestamp >= cutoff));
            }
        }
        catch { /* первый запуск или повреждённый файл */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_snapshots, _opts));
        }
        catch { /* не критично — потеряем только историю usage */ }
    }
}
