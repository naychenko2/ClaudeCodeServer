using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Plugins;

// Per-owner архив секретов плагинов (M7, ADR-031 §4): <c>data/plugin-secrets.json</c>.
// Значения секретов НЕ лежат в <c>installed.json</c> (он едет в облачный архив), а здесь —
// в локальном архиве секретов (имя файла в <c>BackupPaths.SecretFileNames</c>, уходит только
// в зашифрованный архив). Структура: <c>{ owner → { pluginId → { secretName → value } } }</c>.
// По образцу <c>McpSecretStore</c> (Core/Services/Mcp): один файл в корне data, per-owner
// словарь внутри; в <c>installed.json</c> остаётся только имя + признак «заполнен».
public class PluginSecretsStore
{
    // Имя файла — в BackupPaths.SecretFileNames (не в основной архив).
    public const string FileName = "plugin-secrets.json";

    private readonly string _filePath;
    private Dictionary<string, Dictionary<string, Dictionary<string, string>>> _byOwner = new();
    private readonly object _lock = new();
    // S6: брокастер события «установка плагина изменилась» (plugins_changed) — необязательный:
    // DI авто-инъектит ISessionBroadcaster (Main), в тестах и выключенной подсистеме — null.
    private readonly ISessionBroadcaster? _broadcaster;

    public PluginSecretsStore(IConfiguration config, ISessionBroadcaster? broadcaster = null)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, FileName);
        _byOwner = JsonFileStore.Load<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>(
            _filePath, JsonOptions) ?? new();
        _broadcaster = broadcaster;
    }

    /// <summary>Значение секрета плагина владельца; null — не задан.</summary>
    public string? Get(string ownerId, string pluginId, string secretName)
    {
        lock (_lock)
            return _byOwner.TryGetValue(ownerId, out var byPlugin)
                && byPlugin.TryGetValue(pluginId, out var byName)
                && byName.TryGetValue(secretName, out var value)
                ? value : null;
    }

    /// <summary>Задать значение секрета (создаёт запись per-owner → plugin → name).</summary>
    public void Set(string ownerId, string pluginId, string secretName, string value)
    {
        lock (_lock)
        {
            var byPlugin = _byOwner.TryGetValue(ownerId, out var b) ? b : _byOwner[ownerId] = new();
            var byName = byPlugin.TryGetValue(pluginId, out var n) ? n : byPlugin[pluginId] = new();
            byName[secretName] = value;
            Save();
        }
        // S6: Set всегда пишет → событие после записи, вне lock'а.
        NotifyChanged(ownerId);
    }

    /// <summary>Удалить одно значение секрета (minor 5: grant перезаписывает record.Secrets,
    /// невключённые секреты не должны оставаться в plugin-secrets.json).</summary>
    public void RemoveSecret(string ownerId, string pluginId, string secretName)
    {
        bool changed;
        lock (_lock)
        {
            changed = _byOwner.TryGetValue(ownerId, out var byPlugin)
                && byPlugin.TryGetValue(pluginId, out var byName)
                && byName.Remove(secretName);
            if (changed)
                Save();
        }
        if (changed)
            NotifyChanged(ownerId);
    }

    /// <summary>Удалить все секреты плагина (каскад uninstall, M7).</summary>
    public void RemovePlugin(string ownerId, string pluginId)
    {
        bool changed;
        lock (_lock)
        {
            changed = _byOwner.TryGetValue(ownerId, out var byPlugin) && byPlugin.Remove(pluginId);
            if (changed)
                Save();
        }
        if (changed)
            NotifyChanged(ownerId);
    }

    // S6: после успешной записи/удаления секрета — событие «установка изменилась» ВЛАДЕЛЬЦУ
    // (ToOwner), вне lock'а. Без брокастера (тесты/выключенная подсистема) молчит.
    private void NotifyChanged(string ownerId)
    {
        if (_broadcaster is { } b)
            _ = b.ToOwner(ownerId, new PluginsChangedMessage());
    }

    // Плейсхолдер секрета в аргументах запроса (ADR-031 §3.4, шаг 3.3): {{secret:NAME}}.
    // Имя — [A-Za-z0-9_-]+ (как имя секрета в манифесте requires.secrets).
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{secret:([A-Za-z0-9_-]+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Подставить значения секретов в <paramref name="text"/> (шаг 3.3): каждый плейсхолдер
    /// <c>{{secret:NAME}}</c> заменяется значением из хранилища, только если <c>NAME</c> входит
    /// в <paramref name="allowedNames"/> (выданные права <c>secret:{name}</c> плагина). Нерешённый
    /// плейсхолдер (нет права) остаётся как есть — значение не подставляется, значит и не утекает.
    /// </summary>
    public string Resolve(string ownerId, string pluginId, string text, IReadOnlyCollection<string> allowedNames)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{secret:", StringComparison.Ordinal))
            return text;
        return PlaceholderPattern.Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            return allowedNames.Contains(name, StringComparer.Ordinal)
                && Get(ownerId, pluginId, name) is { } value
                ? value
                : m.Value;
        });
    }

    private void Save() => JsonFileStore.Save(_filePath, _byOwner, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
