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
    public void Record(string limitType, double? utilization, string? status, bool isUsingOverage, string? resetsAt)
    {
        if (string.IsNullOrEmpty(limitType) && utilization is null) return;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var last = _snapshots.LastOrDefault(s => s.LimitType == limitType);
            if (last is not null
                && now - last.Timestamp < Throttle
                && last.Status == status
                && Math.Abs((last.Utilization ?? 0) - (utilization ?? 0)) < UtilEpsilon)
                return; // дубль в окне троттлинга — не пишем

            _snapshots.Add(new UsageSnapshot(now, limitType, utilization, status, isUsingOverage, resetsAt));

            var cutoff = now - Retention;
            _snapshots.RemoveAll(s => s.Timestamp < cutoff);

            Save();
        }
    }

    public IReadOnlyList<UsageSnapshot> GetAll()
    {
        lock (_lock) return _snapshots.ToList();
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
