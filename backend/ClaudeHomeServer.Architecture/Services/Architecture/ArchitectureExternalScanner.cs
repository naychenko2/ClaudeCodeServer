using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>Итог поиска кандидатов: прошедшие отсечку и отброшенные (последние — только в сводку).</summary>
public sealed record ExternalScanResult(
    IReadOnlyList<ExternalCandidate> Accepted,
    IReadOnlyList<ExternalCandidate> Rejected);

/// <summary>
/// Кандидаты во внешние системы L1 — дешёвые стек-нейтральные эвристики по дереву проекта:
/// <list type="bullet">
/// <item><c>appsettings.json</c> и <c>appsettings.{Env}.json</c> — секции верхнего уровня
///   с ключом-адресом внутри. <c>appsettings.Local.json</c> и прочие неотслеживаемые
///   вариации НЕ читаются (решение Григория 2026-09-27): только общие дефолты под git.</item>
/// <item><c>*.cs</c> — строковые имена <c>AddHttpClient("…")</c> / <c>AddQuietHttpClient("…")</c>.</item>
/// <item><c>docker-compose*.yml</c> / <c>compose*.yml</c> — сервисы без <c>build:</c>.</item>
/// </list>
/// Секрет-инвариант: из конфига берутся только ИМЕНА ключей, значения не читаются никогда —
/// в модель не может утечь ни адрес, ни ключ.
/// </summary>
public static partial class ArchitectureExternalScanner
{
    /// <summary>Потолок внешних систем в модели (канва и без того тесная).</summary>
    public const int MaxExternalSystems = 20;

    /// <summary>Ниже этой уверенности кандидат — только строка в сводке, не элемент.</summary>
    public const int MinConfidence = 40;

    /// <summary>Исходники крупнее не читаем: сгенерированный код, не регистрации.</summary>
    private const long MaxSourceBytes = 512 * 1024;

    private const string HttpApi = "HTTP API";
    private const string DockerService = "docker-сервис";

    // Секции каркаса хоста — не внешние системы
    private static readonly HashSet<string> FrameworkSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logging", "Kestrel", "AllowedHosts", "ConnectionStrings", "Serilog", "Urls",
        "HostFiltering", "Cors", "Jwt", "Authentication", "Auth",
    };

    public static ExternalScanResult Scan(string root, CancellationToken ct = default)
    {
        var fullRoot = Path.GetFullPath(root);
        var found = new List<ExternalCandidate>();
        var stack = new Stack<string>();
        stack.Push(fullRoot);
        var visited = 0;

        while (stack.Count > 0 && visited++ < ArchitectureSourceScanner.MaxDirectories)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var relDir = ArchitecturePathFolding.Normalize(Path.GetRelativePath(fullRoot, dir));
            if (relDir == ".") relDir = "";

            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var rel = relDir.Length > 0 ? relDir + "/" + name : name;
                if (ArchitecturePathFolding.IsTestPath(rel)) continue;
                if (IsTrackedAppSettings(name)) found.AddRange(FromAppSettings(rel, ReadText(file)));
                else if (IsCompose(name)) found.AddRange(FromCompose(rel, ReadText(file)));
                else if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) found.AddRange(FromSource(file));
            }

            foreach (var sub in subdirs)
            {
                var relSub = ArchitecturePathFolding.Normalize(Path.GetRelativePath(fullRoot, sub));
                if (!ArchitecturePathFolding.IsIgnored(relSub) && !ArchitectureSourceScanner.IsReparsePoint(sub)) stack.Push(sub);
            }
        }

        return Rank(found);
    }

    /// <summary>
    /// Дедуп по нормализованному имени (уверенность складывается, источники копятся),
    /// затем отсечка по порогу и потолку. Детерминирован: порядок — уверенность, потом имя.
    /// </summary>
    public static ExternalScanResult Rank(IEnumerable<ExternalCandidate> raw)
    {
        var merged = raw
            .GroupBy(c => NormalizeName(c.Name))
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                // Имя и технология — от самого уверенного источника (секция конфига главнее
                // имени сервиса в compose)
                var best = g.OrderByDescending(c => c.Confidence).ThenBy(c => c.Name, StringComparer.Ordinal).First();
                var sources = g.Select(c => c.Source).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);
                return new ExternalCandidate(best.Name, best.Technology ?? g.Select(c => c.Technology).FirstOrDefault(t => t is not null),
                    string.Join("; ", sources), Math.Min(100, g.Sum(c => c.Confidence)));
            })
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var accepted = merged.Where(c => c.Confidence >= MinConfidence).Take(MaxExternalSystems).ToList();
        var rejected = merged.Except(accepted).ToList();
        return new ExternalScanResult(accepted, rejected);
    }

    /// <summary>Ключ дедупа: нижний регистр, только буквы и цифры («Dify» == «dify» == «di-fy»).</summary>
    public static string NormalizeName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // appsettings.json и appsettings.{Env}.json с ОДНИМ сегментом окружения, кроме Local:
    // appsettings.Local.json и appsettings.Local.example.json не читаем
    public static bool IsTrackedAppSettings(string fileName)
    {
        if (fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = fileName.Split('.');
        return parts.Length == 3
            && parts[0].Equals("appsettings", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("json", StringComparison.OrdinalIgnoreCase)
            && parts[1].Length > 0
            && !parts[1].Equals("Local", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompose(string fileName) =>
        (fileName.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase)
         || fileName.StartsWith("compose", StringComparison.OrdinalIgnoreCase))
        && (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>Секции appsettings с ключом-адресом. Читаются только имена свойств.</summary>
    public static IEnumerable<ExternalCandidate> FromAppSettings(string relPath, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;
            foreach (var section in doc.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Object || FrameworkSections.Contains(section.Name)) continue;
                var keys = KeyNames(section.Value, depth: 2).ToList();
                if (!keys.Any(IsAddressKey)) continue;
                var confidence = 50 + (keys.Any(IsCredentialKey) ? 20 : 0);
                yield return new ExternalCandidate(section.Name, HttpApi,
                    $"{Path.GetFileName(relPath)}: секция {section.Name}", confidence);
            }
        }
    }

    // Имена свойств объекта и вложенных объектов (значения не читаются)
    private static IEnumerable<string> KeyNames(JsonElement obj, int depth)
    {
        foreach (var p in obj.EnumerateObject())
        {
            yield return p.Name;
            if (depth > 1 && p.Value.ValueKind == JsonValueKind.Object)
                foreach (var nested in KeyNames(p.Value, depth - 1)) yield return nested;
        }
    }

    private static bool IsAddressKey(string key) =>
        key.Equals("Url", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BaseUrl", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("ApiUrl", StringComparison.OrdinalIgnoreCase);

    private static bool IsCredentialKey(string key) =>
        key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Secret", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сервисы compose без <c>build:</c> (с build — само приложение, не внешняя система).</summary>
    public static IReadOnlyList<ExternalCandidate> FromCompose(string relPath, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = text.Replace("\r", "").Split('\n');
        var inServices = false;
        int? serviceIndent = null;
        string? current = null;
        var hasBuild = false;
        var result = new List<ExternalCandidate>();

        void Flush()
        {
            if (current is not null && !hasBuild)
                result.Add(new ExternalCandidate(current, DockerService,
                    $"{Path.GetFileName(relPath)}: сервис {current}", 40));
            current = null;
            hasBuild = false;
        }

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = raw.Length - trimmed.Length;
            if (indent == 0)
            {
                Flush();
                inServices = trimmed.StartsWith("services:", StringComparison.Ordinal);
                serviceIndent = null;
                continue;
            }
            if (!inServices) continue;
            serviceIndent ??= indent;
            if (indent == serviceIndent)
            {
                Flush();
                var m = ComposeKeyRegex().Match(trimmed);
                if (m.Success) current = m.Groups[1].Value;
            }
            else if (indent > serviceIndent && current is not null
                     && trimmed.StartsWith("build:", StringComparison.Ordinal))
            {
                hasBuild = true;
            }
        }
        Flush();
        return result;
    }

    /// <summary>Строковые имена HTTP-клиентов из регистраций (текстовый скан, не Roslyn).</summary>
    public static IEnumerable<ExternalCandidate> FromSourceText(string fileName, string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in HttpClientRegex().Matches(text))
            yield return new ExternalCandidate(m.Groups[1].Value, HttpApi,
                $"{fileName}: HTTP-клиент «{m.Groups[1].Value}»", 40);
    }

    private static IEnumerable<ExternalCandidate> FromSource(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxSourceBytes) return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return FromSourceText(Path.GetFileName(file), ReadText(file));
    }

    private static string? ReadText(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("""^([A-Za-z0-9][A-Za-z0-9._-]*)\s*:\s*(#.*)?$""")]
    private static partial Regex ComposeKeyRegex();

    [GeneratedRegex("""\bAdd(?:Quiet)?HttpClient\s*(?:<[^>()]*>)?\s*\(\s*"([^"\r\n]{1,64})"\s*[,)]""")]
    private static partial Regex HttpClientRegex();
}
