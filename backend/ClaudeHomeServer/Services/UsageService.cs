using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

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
        string? subscriptionKey = null)
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

            _snapshots.Add(new UsageSnapshot(now, limitType, utilization, status, isUsingOverage, resetsAt, overageStatus, overageResetsAt, subKey));

            var cutoff = now - Retention;
            _snapshots.RemoveAll(s => s.Timestamp < cutoff);

            Save();
        }
    }

    // Тариф подписки из ~/.claude/.credentials.json (subscriptionType + rateLimitTier → ярлык).
    // Читаем при каждом запросе — файл маленький, может обновиться после re-login.
    public PlanInfo? GetPlan()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var credsPath = Path.Combine(home, ".claude", ".credentials.json");
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
