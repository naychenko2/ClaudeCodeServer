using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.Dossiers;

// Собирает точные значения секретов ИНСТАНСА для SecretRedactor (ADR-004 §3-а): ключи
// провайдеров/Dify, токены подписок Claude (конфиг + CLAUDE_CODE_OAUTH_TOKEN), содержимое
// .credentials.json профилей CLI и файлов из BackupPaths.SecretFileNames (jwt-secret.txt,
// vapid-keys.json, module-keys.json, mcp-secrets.json) — тот же перечень, что бэкап уводит
// в отдельный локальный архив: один список секретов инстанса на обе подсистемы, расходиться
// им нельзя. Перечитывается по времени (RefreshInterval) — читать файлы профилей на каждую
// редакцию слишком дорого.
public sealed class InstanceSecretsProvider
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly string[] ProfileRoots = ["claude-profiles", "sandbox-profiles"];

    private readonly IConfiguration _config;
    private readonly ILogger<InstanceSecretsProvider>? _log;
    private readonly string _dataDir;
    private readonly Lock _lock = new();
    private IReadOnlyList<string> _cached = [];
    private DateTime _builtAt = DateTime.MinValue;
    private bool _startupWarningLogged;

    public InstanceSecretsProvider(IConfiguration config, ILogger<InstanceSecretsProvider>? log = null)
    {
        _config = config;
        _log = log;
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;
    }

    public IReadOnlyList<string> GetExactSecrets()
    {
        lock (_lock)
        {
            if (_cached.Count > 0 && DateTime.UtcNow - _builtAt < RefreshInterval) return _cached;
            _cached = Build();
            _builtAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private IReadOnlyList<string> Build()
    {
        var values = new List<string>();
        var shortCount = 0;

        void Add(string? v)
        {
            if (string.IsNullOrEmpty(v)) return;
            if (v.Length < SecretRedactor.MinExactSecretLength) { shortCount++; return; }
            values.Add(v);
        }

        foreach (var section in _config.GetSection("LlmProviders").GetChildren())
            Add(section["ApiKey"]);

        foreach (var section in _config.GetSection("ClaudeSubscriptions").GetChildren())
        {
            Add(section["OAuthToken"]);
            Add(section["ApiKey"]);
        }

        Add(_config["Dify:ApiKey"]);
        Add(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"));

        foreach (var name in Backup.BackupPaths.SecretFileNames)
            AddFileStrings(Path.Combine(_dataDir, name), Add);

        foreach (var root in ProfileRoots)
        {
            var dir = Path.Combine(_dataDir, root);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in SafeFindCredentialFiles(dir))
                AddFileStrings(file, Add);
        }

        if (shortCount > 0 && !_startupWarningLogged)
        {
            _startupWarningLogged = true;
            _log?.LogWarning(
                "SecretRedactor: {Count} значений секретов инстанса короче {Min} симв. исключены из редактора " +
                "(пустой/тестовый конфиг) — не участвуют в замене-по-подстроке", shortCount, SecretRedactor.MinExactSecretLength);
        }

        return values.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> SafeFindCredentialFiles(string dir)
    {
        List<string> files;
        try { files = [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)]; }
        catch { return []; }
        return files.Where(f => f.EndsWith(".credentials.json", StringComparison.OrdinalIgnoreCase));
    }

    // Файл-секрет: если это валидный JSON — собираем все строковые листья (схема .credentials.json
    // и module-keys.json заранее неизвестна, надёжнее пройти дерево целиком); иначе — весь текст
    // как единое значение (jwt-secret.txt — голый секрет без обёртки).
    private static void AddFileStrings(string path, Action<string?> add)
    {
        if (!File.Exists(path)) return;
        string content;
        try { content = File.ReadAllText(path); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(content)) return;

        try
        {
            using var doc = JsonDocument.Parse(content);
            CollectJsonStrings(doc.RootElement, add);
        }
        catch (JsonException)
        {
            add(content.Trim());
        }
    }

    private static void CollectJsonStrings(JsonElement el, Action<string?> add)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject()) CollectJsonStrings(prop.Value, add);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) CollectJsonStrings(item, add);
                break;
            case JsonValueKind.String:
                add(el.GetString());
                break;
        }
    }
}
