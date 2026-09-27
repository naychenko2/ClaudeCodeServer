using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Регрессионный тест: после включения IL-скана в сторожах (задача <c>8beee75e</c>,
/// волна 1) IL-скан ОБЯЗАН находить 7 известных швов, которые прежние рефлексионные
/// сторожа не видели. Без этого теста свежее изменение логики обхода тел методов
/// или вложенных типов могло бы пройти молча — сторож выглядел бы работающим,
/// но фактически стал декоративным (3 из 7 швов живут во вложенных типах:
/// async-state-машинах <c>Foo+&lt;BarAsync&gt;d__12</c>, замыканиях
/// <c>Foo+&lt;&gt;c__DisplayClass3_0</c>).
///
/// Полная разведка и таблица «шв → где нашлось» — в
/// <c>docs/research/il-boundary-scan-2026-09.md</c> §«Вердикт по слепым пятнам».
/// </summary>
public class IlBoundaryRegressionTests
{
    private readonly ITestOutputHelper _out;

    // Форс-загрузка сборок вынесенных вертикалей: .NET 5+ лодит сборку по первому
    // использованию типа, а не из каталога. Без явного typeof() AppDomain.GetAssemblies()
    // не видит сборку, если ни один тип из неё не вызывался в этом процессе.
    static IlBoundaryRegressionTests()
    {
        _ = typeof(ClaudeHomeServer.Services.Execution.DockerProcessRunner).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Deploy.DeployService).Assembly;
        // Files — отдельная сборка (ADR-016, задача 4.1): форс-загрузка нужна, чтобы
        // сторож видел FileService и проверял границы вертикали по Files.dll.
        _ = typeof(ClaudeHomeServer.Services.Files.FileService).Assembly;
    }

    public IlBoundaryRegressionTests(ITestOutputHelper output) => _out = output;

    private static Type? Find(IEnumerable<System.Reflection.Assembly> asms, string full) =>
        asms.Select(a => a.GetType(full, false)).FirstOrDefault(t => t is not null);

    [Fact]
    public void IlScan_ВидитВсеСемьИзвестныхШвов()
    {
        var asms = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } n
                && (n == "ClaudeHomeServer" || n.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                && !n.EndsWith(".Tests", StringComparison.Ordinal))
            .ToList();

        // 6 известных швов из CLAUDE.md «Известное ограничение», которые раньше
        // объявлялись «вслепую, для будущего IL-скана». Теперь это живой гейт.
        // После Этапа 5, Ф4 «шов вещания» extension-метод TaskHubExtensions.BroadcastTaskChangedAsync
        // удалён (все потребители переехали на ISessionBroadcaster), case
        // «Tasks → Hubs.TaskHubExtensions» снят — соответствующая статическая ссылка
        // больше не существует.
        var checks = new (string Label, string SourceType, string Needle)[]
        {
            ("DeployHost → IGitRepoChecker", "ClaudeHomeServer.Services.Deploy.DeployHost", "ClaudeHomeServer.Services.Composition.IGitRepoChecker"),
            ("DeployAgentLockAdapter → Backup.InstanceLock", "ClaudeHomeServer.Services.Composition.Deploy.DeployAgentLockAdapter", "ClaudeHomeServer.Services.Backup.InstanceLock"),
            ("ReaderService → SsrfGuard", "ClaudeHomeServer.Services.Reader.ReaderService", "ClaudeHomeServer.Services.SsrfGuard"),
            // Этап 5, волна 3: прежняя проба этой строки — `Memory → SessionSummaryService` —
            // умерла вместе со швом: чистая функция сборки транскрипта переехала из корня
            // `Services/` в спину (`Services.SessionTranscript`), потому что после выноса
            // Memory в отдельную сборку обращение к Main не собиралось вовсе. Проба
            // проверяет ВИДИМОСТЬ IL-скана, а не политику границ, поэтому цель в Core
            // годится ровно так же: форма та же — статический вызов из тела метода того же
            // исходного типа, и подмена обхода тел по-прежнему красит тест.
            ("Memory → SessionTranscript", "ClaudeHomeServer.Services.Memory.PersonaMemoryAutolearnService", "ClaudeHomeServer.Services.SessionTranscript"),
            ("Execution → TranscriptRoots", "ClaudeHomeServer.Services.Execution.DockerProcessRunner", "ClaudeHomeServer.Services.TranscriptRoots"),
            ("Llm → SpecialtyCatalog", "ClaudeHomeServer.Services.Llm.SpecialtySettingsStore", "ClaudeHomeServer.Services.SpecialtyCatalog"),
        };

        var missed = new List<string>();
        foreach (var (label, typeName, needle) in checks)
        {
            var t = Find(asms, typeName);
            if (t is null) { missed.Add($"{label}: тип {typeName} не найден"); continue; }

            // Единый сбор (CollectAllReferencedTypes) — тот же вызов, что и в
            // Theory-сторожах: если обход nested-типов в нём сломать, тест станет
            // красным. 3 из 7 швов живут во вложенных async-state-машинах и замыканиях.
            var referenced = BoundaryIlScanner.CollectAllReferencedTypes(t)
                .Where(r => r.FullName == needle || (r.FullName?.StartsWith(needle + "+", StringComparison.Ordinal) ?? false))
                .Select(r => r.Name)
                .Distinct().ToList();

            if (referenced.Count == 0)
            {
                missed.Add($"{label}: IL-скан НЕ видит");
                continue;
            }
            _out.WriteLine($"{label}: ВИДИТ — {string.Join(", ", referenced.Take(3))}");
        }

        Assert.True(missed.Count == 0,
            "IL-скан должен видеть все 7 известных швов. Без этого теста сторож " +
            "может стать декоративным при изменении логики обхода тел/вложенных типов. " +
            "Не найдено: " + string.Join("; ", missed));
    }
}
