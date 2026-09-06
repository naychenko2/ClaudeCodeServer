using System.Reflection;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Регрессионный тест: после включения IL-скана в сторожах (задача <c>8beee75e</c>,
/// волна 1) IL-скан ОБЯЗАН находить 7 известных швов, которые прежние рефлексионные
/// сторожа не видели. Без этого теста свежее изменение логики обхода тел методов
/// или вложенных типов могло бы пройти молча — сторож выглядел бы работающим,
/// но фактически стал декоративным (5 из 7 швов живут во вложенных типах:
/// async-state-машинах <c>Foo+&lt;BarAsync&gt;d__12</c>, замыканиях
/// <c>Foo+&lt;&gt;c__DisplayClass3_0</c>).
///
/// Полная разведка и таблица «шв → где нашлось» — в
/// <c>docs/research/il-boundary-scan-2026-09.md</c> §«Вердикт по слепым пятнам».
/// </summary>
public class IlBoundaryRegressionTests
{
    private readonly ITestOutputHelper _out;

    public IlBoundaryRegressionTests(ITestOutputHelper output) => _out = output;

    private static Type? Find(IEnumerable<System.Reflection.Assembly> asms, string full) =>
        asms.Select(a => a.GetType(full, false)).FirstOrDefault(t => t is not null);

    [Fact]
    public void IlScan_ВидитВсеСемьИзвестныхШвов()
    {
        var asms = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } n
                && (n == "ClaudeHomeServer" || n.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                && n != "ClaudeHomeServer.Tests"
                && !n.StartsWith("ClaudeHomeServer.Tests.", StringComparison.Ordinal))
            .ToList();

        // 7 известных швов из CLAUDE.md «Известное ограничение», которые раньше
        // объявлялись «вслепую, для будущего IL-скана». Теперь это живой гейт.
        var checks = new (string Label, string SourceType, string Needle)[]
        {
            ("DeployHost → GitService", "ClaudeHomeServer.Services.Deploy.DeployHost", "ClaudeHomeServer.Services.Git.GitService"),
            ("DeployHost → Backup.InstanceLock", "ClaudeHomeServer.Services.Deploy.DeployHost", "ClaudeHomeServer.Services.Backup.InstanceLock"),
            ("ReaderService → SsrfGuard", "ClaudeHomeServer.Services.Reader.ReaderService", "ClaudeHomeServer.Services.SsrfGuard"),
            ("Memory → SessionSummaryService", "ClaudeHomeServer.Services.Memory.PersonaMemoryAutolearnService", "ClaudeHomeServer.Services.SessionSummaryService"),
            ("Execution → TranscriptRoots", "ClaudeHomeServer.Services.Execution.DockerProcessRunner", "ClaudeHomeServer.Services.TranscriptRoots"),
            ("Llm → SpecialtyCatalog", "ClaudeHomeServer.Services.Llm.SpecialtySettingsStore", "ClaudeHomeServer.Services.SpecialtyCatalog"),
            ("Tasks → TaskHubExtensions", "ClaudeHomeServer.Services.Tasks.TaskSchedulerService", "ClaudeHomeServer.Controllers.TaskHubExtensions"),
        };

        var missed = new List<string>();
        foreach (var (label, typeName, needle) in checks)
        {
            var t = Find(asms, typeName);
            if (t is null) { missed.Add($"{label}: тип {typeName} не найден"); continue; }

            // Обход nested-типов обязателен: 5 из 7 швов живут во вложенных типах.
            var family = new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)).ToList();
            var hits = family
                .SelectMany(t2 => BoundaryIlScanner.AllMethodsWithNested(t2))
                .SelectMany(m => BoundaryIlScanner.TypesFromBody(m).Select(r => (m, r)))
                .Where(x => x.r.FullName == needle || (x.r.FullName?.StartsWith(needle + "+", StringComparison.Ordinal) ?? false))
                .Select(x => $"{x.m.DeclaringType?.Name}.{x.m.Name}")
                .Distinct().ToList();

            if (hits.Count == 0)
            {
                missed.Add($"{label}: IL-скан НЕ видит");
                continue;
            }
            _out.WriteLine($"{label}: ВИДИТ — {string.Join(", ", hits.Take(3))}");
        }

        Assert.True(missed.Count == 0,
            "IL-скан должен видеть все 7 известных швов. Без этого теста сторож " +
            "может стать декоративным при изменении логики обхода тел/вложенных типов. " +
            "Не найдено: " + string.Join("; ", missed));
    }
}
