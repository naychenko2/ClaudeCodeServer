using System.Text.Json;

namespace ClaudeHomeServer.Services.Plugins;

// Единая точка разбора манифеста пакета (rework C3): plugin.json / ccs-plugin.json /
// .claude-plugin/plugin.json. Читает файл, парсит сырой JSON и проверяет топ-уровневые
// поля по <see cref="ManifestFieldPolicy"/>. Отдельно от модели PluginManifest: та
// forward-compat (неизвестные поля игнорирует), а здесь — строгий allow-list полей.
// Stateless — singleton; вызывается валидатором пакета (PluginPackageValidator).
public class PluginManifestReader
{
    // Имена манифеста в порядке приоритета (совпадает с allow-list валидатора).
    private static readonly string[] ManifestNames =
    [
        "ccs-plugin.json",
        "plugin.json",
        ".claude-plugin/plugin.json",
    ];

    // Найти ВСЕ манифесты пакета (ccs-plugin.json → plugin.json → .claude-plugin/plugin.json),
    // прочитать их и проверить топ-уровневые поля. Возвращает (список сырых JSON, проблемы).
    // ([], []) — ни одного манифеста; ([raw1, ..], [..]) — разобраны все найденные, проблемы
    // по полям каждого. M3a: список (а не только первый) — чтобы валидатор сверял `name`
    // у каждого манифеста, а не только у ccs-plugin.json.
    // C3: поля проверяются у КАЖДОГО найденного манифеста (цикл без раннего return), а не
    // только у первого: CLI читает .claude-plugin/plugin.json, поэтому «чистый»
    // ccs-plugin.json + hooks/mcpServers в нативном манифесте не должен проходить.
    public (IReadOnlyList<string> RawJsons, IReadOnlyList<PluginPackageProblem> Problems) Read(string packageRoot)
    {
        var raws = new List<string>();
        var problems = new List<PluginPackageProblem>();
        foreach (var name in ManifestNames)
        {
            var path = Path.Combine(packageRoot, name);
            if (!File.Exists(path)) continue;
            var raw = File.ReadAllText(path);
            raws.Add(raw);
            problems.AddRange(ValidateFields(raw));
        }
        return (raws, problems);
    }

    // Проверить топ-уровневые поля сырого JSON манифеста. Каждый ключ вне белого списка
    // (ManifestFieldPolicy) → deny-manifest-field с именем поля. Не-объект (массив/строка)
    // и битый JSON — без полей (модель PluginManifest отчитается отдельно).
    public IReadOnlyList<PluginPackageProblem> ValidateFields(string rawJson)
    {
        var problems = new List<PluginPackageProblem>();
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            // Clone — копия, живёт после Dispose документа (EnumerateObject ниже).
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return problems;
        }

        if (root.ValueKind != JsonValueKind.Object) return problems;
        foreach (var prop in root.EnumerateObject())
            if (!ManifestFieldPolicy.IsAllowed(prop.Name))
                problems.Add(new PluginPackageProblem(
                    "deny-manifest-field", $"Запрещённое поле манифеста: {prop.Name}"));
        return problems;
    }
}
