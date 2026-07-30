using System.Text.Json;

namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>Что мы помним о разосланном алерте — нужно, чтобы осмысленно сказать «восстановлено».</summary>
public sealed record AlertMemo(string Title, DateTimeOffset FiredAt);

/// <summary>
/// Помнит, о каких алертах уже сообщили (<c>data/alert-state.json</c>).
///
/// Без этого состояния горящий часами алерт слал бы уведомление на каждом опросе:
/// минута — уведомление, и через полчаса их отключат совсем. Переживает перезапуск
/// намеренно: после рестарта сервера повторять старые тревоги не нужно.
/// </summary>
public sealed class AlertStateStore
{
    private readonly string _path;
    private readonly ILogger<AlertStateStore> _log;
    private readonly Lock _lock = new();
    private Dictionary<string, AlertMemo> _known = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AlertStateStore(IConfiguration config, ILogger<AlertStateStore> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "alert-state.json");
        Load();
    }

    public IReadOnlySet<string> KnownFingerprints
    {
        get { lock (_lock) return _known.Keys.ToHashSet(StringComparer.Ordinal); }
    }

    public AlertMemo? Recall(string fingerprint)
    {
        lock (_lock) return _known.GetValueOrDefault(fingerprint);
    }

    public void Remember(string fingerprint, AlertMemo memo)
    {
        lock (_lock)
        {
            _known[fingerprint] = memo;
            Save();
        }
    }

    public void Forget(IEnumerable<string> fingerprints)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var f in fingerprints) changed |= _known.Remove(f);
            if (changed) Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, AlertMemo>>(json, JsonOpts);
            if (loaded is not null) _known = new Dictionary<string, AlertMemo>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Битый файл состояния — не повод не стартовать. Худшее последствие:
            // однократный повтор уведомлений по горящим алертам.
            _log.LogWarning(ex, "Не удалось прочитать состояние алертов {Path}", _path);
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_known, JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось сохранить состояние алертов {Path}", _path);
        }
    }
}
