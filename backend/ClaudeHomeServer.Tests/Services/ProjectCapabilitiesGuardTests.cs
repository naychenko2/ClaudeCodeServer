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
        + @"|\bIsLocal\b"
        // C# 8+ property pattern: is { DeviceId: not null } — DeviceId внутри скобок,
        // а не перед is (направление «expr is { Prop: not null }»). Не берём общее
        // is { ... not null }: в коде много таких (SessionManager, VideoEditor), не про
        // локальность проекта.
        + @"|\bis\s*\{[^{}]*\bDeviceId\b[^{}]*\bnot\s+null\b",
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
        ["backend/ClaudeHomeServer.Llm/LlmProviderRegistry.cs"] = (9, "LLM-провайдер"),
        // Признак локальности LLM-провайдера в /api/models (LlmCapabilities.IsLocal) и его
        // отдача наружу (ModelsController) — про провайдера, не про локальность проекта.
        ["backend/ClaudeHomeServer.Core/Services/Llm/LlmCapabilities.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer/Controllers/ModelsController.cs"] = (3, "LLM-провайдер"),
        // Адаптер шва IModelResolver и one-shot-раннер: IsLocal — признак локального
        // LLM-провайдера для классификации расхода (Spend), не локальность проекта.
        ["backend/ClaudeHomeServer.Llm/LlmModelResolverAdapter.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/OneShotClaudeRunner.cs"] = (2, "LLM-провайдер"),
        // UseServerGateway спрашивает provider.IsLocal (локальный LLM-провайдер), не локальность
        // проекта; гейт допуска ходов к локали (B1b) переехал в FallbackLlmSessionAdapter, из
        // ClaudeSession ушли два прежних вхождения — осталось 3.
        ["backend/ClaudeHomeServer.Llm/Claude/ClaudeSession.cs"] = (3, "LLM-провайдер"),
        // Классификация расхода one-shot: IsLocal источника — локальный LLM-провайдер, не проект.
        ["backend/ClaudeHomeServer.Llm/CloudCheapClient.cs"] = (1, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/FallbackLlmSessionAdapter.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/Gateway/UpstreamSelector.cs"] = (1, "LLM-провайдер"),
        // Шлюз: наблюдатель метрик создаётся, когда маршрут — локальный LLM-провайдер
        // (LlmProviderConfig.IsLocal), не «локальный проект» — чужой смысл того же слова.
        ["backend/ClaudeHomeServer.Llm/Gateway/LlmGatewayEndpoints.cs"] = (2, "LLM-провайдер"),
        // Поллер: источник метрик выбирается по провайдеру, помеченному IsLocal (LLM-провайдер).
        ["backend/ClaudeHomeServer.Llm/LocalStats/LocalModelStatsPoller.cs"] = (2, "LLM-провайдер"),
        // Расход: локальные LLM-провайдеры считаются бесплатными (1444df06e) — IsLocal везде про провайдера, не проект.
        ["backend/ClaudeHomeServer.Llm/LlmModelResolverAdapter.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Llm/OneShotClaudeRunner.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Core/Services/Llm/IModelResolver.cs"] = (3, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Core/Models/SpendRecord.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer/Services/SpendMapping.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Spend/SpendStore.cs"] = (1, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Spend/SpendMaintenanceService.cs"] = (4, "LLM-провайдер"),
        // Тестовый хелпер шлюза: IsLocal — признак локального LLM-провайдера (vLLM), не проекта.
        // Жил в ClaudeHomeServer.Tests (сторож его пропускал как .Tests); переехал в TestKit.Host (шаг 3.3).
        ["backend/ClaudeHomeServer.TestKit.Host/GatewayTestKit.cs"] = (2, "LLM-провайдер"),
        // Учёт расхода: «бесплатный» определяется по IsLocal провайдера (LlmProviderConfig),
        // не по локальности проекта — вертикаль Spend и её швы.
        ["backend/ClaudeHomeServer.Core/Services/Llm/IModelResolver.cs"] = (3, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Core/Models/SpendRecord.cs"] = (2, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Spend/SpendStore.cs"] = (1, "LLM-провайдер"),
        ["backend/ClaudeHomeServer.Spend/SpendMaintenanceService.cs"] = (4, "LLM-провайдер"),
        ["backend/ClaudeHomeServer/Services/SpendMapping.cs"] = (2, "LLM-провайдер"),
        // Ценовой класс провайдера (/api/models): локальный LLM-провайдер → бесплатно.
        ["backend/ClaudeHomeServer/Services/ModelCatalogService.cs"] = (1, "LLM-провайдер"),
        // Фронт: признак локальности LLM-провайдера (isLocal в capabilities провайдера)
        // — про провайдера, не про локальность проекта.
        ["frontend/src/lib/models.ts"] = (1, "LLM-провайдер"),
        ["frontend/src/lib/spendSources.ts"] = (6, "LLM-провайдер"),
        ["frontend/src/features/modelsSpend/QuotasTab.tsx"] = (10, "LLM-провайдер"),
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
    // C# 8+ property pattern: оба направления — «expr.Prop is { not null }» (свойство
    // перед is) и «expr is { Prop: not null }» (свойство внутри скобок).
    [InlineData("if (project.DeviceId is { not null }) { return false; }")]
    [InlineData("if (project is { DeviceId: not null }) { return false; }")]
    public void Шаблон_ЛовитИнлайновуюПроверку(string line) =>
        BackendPattern.IsMatch(line).Should().BeTrue();

    [Theory]
    [InlineData("if (project.deviceId !== null) {}")]
    [InlineData("const on = !!project.deviceId;")]
    [InlineData("if (isLocal) {}")]
    public void ШаблонФронта_ЛовитИнлайновуюПроверку(string line) =>
        FrontendPattern.IsMatch(line).Should().BeTrue();
}
