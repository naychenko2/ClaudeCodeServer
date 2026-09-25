using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож G10 (ADR-016 §4, план §4): матрица возможностей — единственная точка правды о
/// локальности проекта. Инлайновые проверки <c>DeviceId != null</c>/<c>IsLocal</c> вне
/// <c>ProjectCapabilities</c> запрещены: каждая такая проверка — место, которое забудут
/// поправить, когда матрица изменится.
///
/// Allow-list — файл и ТОЧНОЕ число вхождений: новая проверка в уже разрешённом файле тоже
/// краснеет. Разрешены чужие смыслы тех же слов (признак локального LLM-провайдера
/// <c>LlmProviderConfig.IsLocal</c>, привязка токена хода к устройству) и разбор полей запроса.
/// </summary>
public class ProjectCapabilitiesGuardTests
{
    private static readonly Regex BackendPattern = new(
        @"\.DeviceId\s*(==|!=)\s*null"
        + @"|\.DeviceId\s+is\s+(not\s+)?null"
        + @"|\.DeviceId\s+is\s*\{"
        + @"|IsNullOr(Empty|WhiteSpace)\(\s*[A-Za-z_][A-Za-z0-9_.]*\.DeviceId\s*\)"
        + @"|\bIsLocal\b",
        RegexOptions.Compiled);

    private static readonly Regex FrontendPattern = new(
        @"\.deviceId\s*(===?|!==?)\s*(null|undefined)|!!\s*[A-Za-z_.]*\.deviceId\b|\bisLocal\b",
        RegexOptions.Compiled);

    // Путь от корня репозитория через «/» → число вхождений и причина
    private static readonly Dictionary<string, (int Count, string Why)> Allowed = new()
    {
        ["backend/ClaudeHomeServer.Core/Models/ProjectCapabilities.cs"] = (2, "сама матрица и её описание"),
        ["backend/ClaudeHomeServer/Controllers/ProjectsController.cs"] = (2, "разбор req.DeviceId в создании и перепривязке"),
        ["backend/ClaudeHomeServer.Llm/Gateway/TurnTokenService.cs"] = (1, "привязка токена хода к устройству"),
        ["backend/ClaudeHomeServer.DeviceAgent/Pairing/DevicePairing.cs"] = (1, "ответ сопряжения устройства, не проект"),
        // IsLocal — признак локального LLM-провайдера, к проектам не относится
        ["backend/ClaudeHomeServer.Core/Models/LlmProviderConfig.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Core/Services/Mcp/Http/LoopbackProxyBypass.cs"] = (1, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/LocalEndpointProbe.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/LlmSessionContext.cs"] = (1, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/LlmProviderRegistry.cs"] = (5, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/Claude/ClaudeSession.cs"] = (4, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/FallbackLlmSessionAdapter.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/Gateway/UpstreamSelector.cs"] = (1, "LLM-провайдер"),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    private static bool IsSkipped(string relative)
    {
        var parts = relative.Split('/');
        return parts.Any(p => p is "bin" or "obj" or "node_modules" or "__tests__")
            || parts.Any(p => p.EndsWith(".Tests", StringComparison.Ordinal))
            || relative.Contains(".test.", StringComparison.Ordinal);
    }

    // Файлы с вхождениями: относительный путь → число
    internal static Dictionary<string, int> Scan(string root)
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(string dir, string[] exts, Regex pattern)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
            {
                if (!exts.Contains(Path.GetExtension(file))) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsSkipped(relative)) continue;
                var count = pattern.Matches(File.ReadAllText(file)).Count;
                if (count > 0) hits[relative] = count;
            }
        }
        Walk("backend", [".cs"], BackendPattern);
        Walk(Path.Combine("frontend", "src"), [".ts", ".tsx"], FrontendPattern);
        return hits;
    }

    [Fact]
    public void ПроверкиЛокальностиПроекта_ТолькоВМатрице()
    {
        var hits = Scan(RepoRoot());

        var violations = hits
            .Where(h => !Allowed.TryGetValue(h.Key, out var a) || h.Value > a.Count)
            .Select(h => $"{h.Key}: {h.Value}")
            .ToList();

        violations.Should().BeEmpty(
            "локальность проекта спрашивают только через ProjectCapabilities (сторож G10, ADR-016 §4); "
            + "чужой смысл того же слова — строка в allow-list с точным числом вхождений");
    }

    [Fact]
    public void AllowList_НеПротух()
    {
        var hits = Scan(RepoRoot());

        // Разрешение, под которым вхождений стало меньше, — дыра на будущее: туда молча
        // встанет новая проверка. Число правится вниз вместе с кодом.
        var stale = Allowed
            // Запись с числом 0 («строго ни одного») протухнуть не может — она и есть ноль
            .Where(a => hits.GetValueOrDefault(a.Key) < a.Value.Count)
            .Select(a => $"{a.Key}: ожидалось {a.Value.Count}, найдено {hits.GetValueOrDefault(a.Key)}")
            .ToList();

        stale.Should().BeEmpty();
    }

    [Theory]
    [InlineData("if (project.DeviceId != null) return;")]
    [InlineData("if (p.DeviceId is not null) { }")]
    [InlineData("var local = p.DeviceId is { } d;")]
    [InlineData("if (!string.IsNullOrEmpty(project.DeviceId)) { }")]
    [InlineData("if (project.IsLocal) { }")]
    public void Шаблон_ЛовитИнлайновуюПроверку(string line) =>
        BackendPattern.IsMatch(line).Should().BeTrue();

    [Theory]
    [InlineData("if (project.deviceId !== null) {}")]
    [InlineData("const on = !!project.deviceId;")]
    [InlineData("if (isLocal) {}")]
    public void ШаблонФронта_ЛовитИнлайновуюПроверку(string line) =>
        FrontendPattern.IsMatch(line).Should().BeTrue();
}
