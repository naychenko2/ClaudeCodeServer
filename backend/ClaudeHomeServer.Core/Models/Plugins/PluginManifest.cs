using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Манифест плагина владельца <c>ccs-plugin.json</c> (ADR-031 §2.2). Лежит рядом с нативным
/// <c>.claude-plugin/plugin.json</c> CLI. Разбирается в Core; валидация по allow-list файлов
/// пакета — в вертикали Plugins (шаг 1.3). Неизвестные поля игнорируются (forward-compat,
/// как <see cref="ModuleManifest"/>).
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Глобальный id пакета: <c>{publisher}/{slug}</c>. В MVP publisher = <c>local-{ownerShort}</c>.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    /// <summary>Версия моста панели, с которой пакет собран (ADR-031 §3.5).</summary>
    [JsonPropertyName("bridge")] public PluginBridge? Bridge { get; set; }
    /// <summary>Вклады пакета. Любой из массивов может быть пуст, хотя бы один вклад обязателен.</summary>
    [JsonPropertyName("contributes")] public PluginContributes? Contributes { get; set; }
    /// <summary>Запрошенные права (потолок); человек выдаёт подмножество при установке (ADR-031 §4).</summary>
    [JsonPropertyName("permissions")] public PluginPermissions? Permissions { get; set; }

    /// <summary>
    /// Структурная валидация манифеста (не файлов пакета): есть ли хотя бы один вклад,
    /// все ли запрошенные права из известного набора, есть ли обязательные секреты.
    /// Возвращает список проблем; пустой список — манифест корректен.
    /// </summary>
    public PluginManifestValidation Validate() => new(this);
}

/// <summary>Версия моста панели (ADR-031 §3.5). Хост, не поддерживающий версию, панель не монтирует.</summary>
public sealed class PluginBridge
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
}

/// <summary>Вклады пакета (ADR-031 §2.1): навыки/команды, панели, зависимости.</summary>
public sealed class PluginContributes
{
    /// <summary>Пути к skill/команде (нативный формат CLI). Команда = skill с <c>user-invocable</c>.</summary>
    [JsonPropertyName("skills")] public List<string>? Skills { get; set; }
    [JsonPropertyName("commands")] public List<string>? Commands { get; set; }
    [JsonPropertyName("panels")] public List<PluginPanel>? Panels { get; set; }
    /// <summary>Зависимости: MCP-серверы личного реестра и секреты.</summary>
    [JsonPropertyName("requires")] public PluginRequires? Requires { get; set; }

    /// <summary>Есть ли хотя бы один вклад (ADR-031 §2.2: хотя бы один вклад обязателен).</summary>
    public bool HasAnyContribution =>
        (Skills is { Count: > 0 })
        || (Commands is { Count: > 0 })
        || (Panels is { Count: > 0 });
}

/// <summary>Панель плагина (ADR-031 §3.1): хост монтирует её в sandbox-iframe по <c>entry</c>.</summary>
public sealed class PluginPanel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Слот интерфейса (например <c>sidebar</c>).</summary>
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
    /// <summary>Относительный путь entry-HTML панели внутри пакета.</summary>
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

/// <summary>Зависимости пакета (ADR-031 §2.2): MCP-серверы и секреты.</summary>
public sealed class PluginRequires
{
    /// <summary>Ключи MCP-серверов личного реестра владельца.</summary>
    [JsonPropertyName("mcp")] public List<string>? Mcp { get; set; }
    /// <summary>Секреты, на которые ссылается пакет (имя + признак «обязательный»).</summary>
    [JsonPropertyName("secrets")] public List<PluginSecret>? Secrets { get; set; }
}

/// <summary>
/// Секрет, на который ссылается пакет. <see cref="Required"/> — решение В2 макетов:
/// обязательный секрет, не заданный владельцем, блокирует установку.
/// </summary>
public sealed class PluginSecret
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; }
}

/// <summary>Запрошенные права пакета (потолок, ADR-031 §4). Выдаёт человек при установке.</summary>
public sealed class PluginPermissions
{
    /// <summary>Крупные права: <c>notes:read</c>, <c>tasks:read</c>, <c>net:{exact-host}</c>, <c>secret:{name}</c>.</summary>
    [JsonPropertyName("requested")] public List<string>? Requested { get; set; }
}

/// <summary>Одна проблема валидации манифеста: код (машиночитаемый) + текст для человека.</summary>
public sealed record PluginManifestProblem(string Code, string Message);

/// <summary>
/// Итог валидации манифеста (<see cref="PluginManifest.Validate"/>). <see cref="IsValid"/> —
/// нет проблем; <see cref="RequiredSecrets"/> — обязательные секреты, блокирующие установку
/// (решение В2 макетов), отдельно от остальных проблем.
/// </summary>
public sealed class PluginManifestValidation
{
    private readonly PluginManifest _manifest;
    private readonly List<PluginManifestProblem> _problems = [];

    public PluginManifestValidation(PluginManifest manifest)
    {
        _manifest = manifest;
        ValidateContributes();
        ValidatePermissions();
        ValidatePanelEntries();
        CollectRequiredSecrets();
    }

    public bool IsValid => _problems.Count == 0;

    public IReadOnlyList<PluginManifestProblem> Problems => _problems;

    /// <summary>Имена обязательных секретов пакета — блокируют установку, пока не заданы.</summary>
    public IReadOnlyList<string> RequiredSecrets { get; private set; } = [];

    private void ValidateContributes()
    {
        // ADR-031 §2.2: хотя бы один вклад обязателен.
        if (_manifest.Contributes is not { } c || !c.HasAnyContribution)
            _problems.Add(new PluginManifestProblem("no-contribution", "Пакет не вносит ни навыка, ни команды, ни панели"));
    }

    private void ValidatePermissions()
    {
        foreach (var p in _manifest.Permissions?.Requested ?? [])
            if (!PluginPermission.IsKnown(p))
                _problems.Add(new PluginManifestProblem("unknown-permission", $"Неизвестное право: {p}"));
    }

    // Форма entry панели (Глеб MAJOR 2, серверная половина): entry из манифеста идёт на фронт
    // и подставляется в URL запроса панели, поэтому мусорные значения (схема, «..», «?», «#»,
    // управляющие) должны отбиваться ДО фронта. Чистая проверка формы (существование файла
    // в пакете — отдельно, в PluginPackageValidator.ValidateFull). Код проблемы: bad-panel-entry.
    private void ValidatePanelEntries()
    {
        foreach (var panel in _manifest.Contributes?.Panels ?? [])
        {
            var entry = panel.Entry;
            if (string.IsNullOrEmpty(entry)) continue; // панели без entry — нет файла, форма не проверяется
            if (!IsValidPanelEntry(entry))
                _problems.Add(new PluginManifestProblem("bad-panel-entry",
                    $"entry панели «{panel.Id}» не по форме (ожидается panel/…): {entry}"));
        }
    }

    // entry панели — относительный путь panel/…: начинается с «panel/», без сегментов «..» и «.»,
    // без «\», «?», «#», «%», без управляющих символов, без начального «/» и схемы («http:»),
    // длина ≤ 200. Чистая проверка формы (без обращения к файловой системе).
    public static bool IsValidPanelEntry(string entry)
    {
        const int MaxLength = 200;
        if (entry.Length > MaxLength) return false;
        if (!entry.StartsWith("panel/", StringComparison.Ordinal)) return false; // и не начальный «/», и не схема
        if (entry.Contains('\\')) return false;
        if (entry.Contains('?') || entry.Contains('#') || entry.Contains('%')) return false;
        if (entry.Contains("http:", StringComparison.Ordinal)) return false; // схема
        foreach (var seg in entry.Split('/'))
            if (seg == ".." || seg == ".") return false;
        foreach (var c in entry)
            if (c < 0x20 || c == 0x7F) return false; // управляющие (C0 + DEL)
        return true;
    }

    private void CollectRequiredSecrets()
    {
        var names = _manifest.Contributes?.Requires?.Secrets
            ?.Where(s => s.Required)
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        if (names is not null) RequiredSecrets = names;
    }
}
