using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// QA-1 (очередь 2, выкат 1): замер производительности и связности WikiConnectivity.Measure
// на копии боевого корпуса (снимок 19.11). Только чтение — боевые данные не трогаются.
//
// Каталог копии — WIKI_DRYRUN_DIR (ожидается <корень>/data, внутри pages/ и keeper/); без
// переменной берётся копия из репозитория (.cc-attachments/wiki-q2-dryrun/data-19-11).
// Каталога нет — прогон ПРОПУСКАЕТСЯ (как сухие прогоны A0/T0: считать нечего).
//
// Что меряется:
// - время WikiConnectivity.Measure на всех страницах знаний корпуса (задача QA-1: «замер
//   времени на 300+ страницах»); меряется и второй (тёплый) прогон — индекс «По теме»
//   строится заново на каждый Measure;
// - K0/K1/K2 тем же Measure, что у ночного итога (A6), и сверка K1 с BaselineK1 = 0,404
//   (константа ночного критерия, WikiNightlyService.BaselineK1).
//
// Артефакт (json) — в WIKI_QA_OUT (по умолчанию <корень>/.cc-attachments/wiki-q2-qa1):
// числа замера, чтобы отчёт QA опирался на фактический прогон, а не на пересказ.
[Trait("Category", "Bench")]
public class WikiConnectivityBenchTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "backend")) &&
                                    Directory.Exists(Path.Combine(dir.FullName, "frontend"))))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public void Замер_Связности_На_Копии_Корпуса()
    {
        var root = RepoRoot();
        var dir = Environment.GetEnvironmentVariable("WIKI_DRYRUN_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(root, ".cc-attachments", "wiki-q2-dryrun", "data-19-11");
        var pagesRoot = Path.Combine(dir, "pages");
        if (!Directory.Exists(pagesRoot))
            return; // пропуск: копии корпуса нет — считать нечего

        var drafts = new List<WikiGate.Draft>();
        var knowledge = new List<string>();
        foreach (var full in Directory.EnumerateFiles(pagesRoot, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pagesRoot, full).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(full);
            drafts.Add(new WikiGate.Draft(rel, text));
            if (WikiSections.Parse(rel) is null || WikiPaths.IsProjection(rel))
                continue;
            if (WikiPage.Parse(text).Model is not { } model)
                continue;
            if (model.Frontmatter.Type is WikiFrontmatter.TypeSource or WikiFrontmatter.TypeChronicle)
                continue;
            knowledge.Add(rel);
        }
        drafts.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        knowledge.Sort(StringComparer.Ordinal);

        var sw = Stopwatch.StartNew();
        var cold = WikiConnectivity.Measure(drafts, knowledge);
        sw.Stop();
        var coldMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var warm = WikiConnectivity.Measure(drafts, knowledge);
        sw.Stop();
        var warmMs = sw.ElapsedMilliseconds;

        var artifact = new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            copyDir = dir,
            pagesTotal = drafts.Count,
            knowledgePages = knowledge.Count,
            coldMs,
            warmMs,
            msPerPage = knowledge.Count == 0 ? 0 : (double)warmMs / knowledge.Count,
            k0 = warm.K0,
            k1 = warm.K1,
            k2 = warm.K2,
            linkedShare = warm.LinkedShare,
            baselineK1 = WikiNightlyService.BaselineK1,
            baselineK1Slack = WikiNightlyService.BaselineK1Slack,
            k1DeltaToBaseline = warm.K1 - WikiNightlyService.BaselineK1,
            topicShareGlobalIndex = TopicShare(drafts, knowledge, globalIndex: true),
            topicShareSectionIndex = TopicShare(drafts, knowledge, globalIndex: false),
        };
        var outDir = Environment.GetEnvironmentVariable("WIKI_QA_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(root, ".cc-attachments", "wiki-q2-qa1");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "connectivity-measure.json"),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(knowledge.Count >= 300, $"в замере должно быть не меньше 300 страниц, а их {knowledge.Count}");
        Assert.Equal(knowledge.Count, warm.Count);
        Assert.Equal(cold.K1, warm.K1, 9); // замер детерминирован
        Assert.InRange(warm.K0, 0, 100);
        Assert.InRange(warm.K1, 0, 1);
        Assert.InRange(warm.K2, 0, 1);
        // Индекс — по видимым разделам, как при показе: K1 боевого кода сходится с T0 (0,404).
        Assert.InRange(warm.K1, WikiNightlyService.BaselineK1 - 0.02, WikiNightlyService.BaselineK1 + 0.02);
    }

    // Диагностика BaselineK1: доля страниц знаний, у которых есть хотя бы один «по теме»-сосед,
    // при ГЛОБАЛЬНОМ индексе снимка (так считал продукт до доработки A6-1: один индекс на весь
    // снимок; теперь запрещён) и при индексе СВОЕГО РАЗДЕЛА (так считала приёмка T0, откуда
    // взята константа BaselineK1 = 0,404). Разница индексов — разный df (N и df), а значит
    // разный отбор редких основ и разные рёбра.
    private static double TopicShare(List<WikiGate.Draft> drafts, List<string> knowledge, bool globalIndex)
    {
        var bySection = drafts
            .Where(d => WikiSections.Parse(d.Path) is not null)
            .GroupBy(d => WikiSections.Parse(d.Path)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var knowledgeSet = new HashSet<string>(knowledge, StringComparer.Ordinal);
        var global = WikiTopicScore.BuildIndex(drafts
            .Where(d => WikiSections.Parse(d.Path) is not null && !WikiPaths.IsProjection(d.Path))
            .Select(EntryOf).OfType<WikiTopicScore.Entry>());
        var perSection = bySection.ToDictionary(
            kv => kv.Key,
            kv => WikiTopicScore.BuildIndex(kv.Value.Select(EntryOf).OfType<WikiTopicScore.Entry>()),
            StringComparer.Ordinal);
        var withEdge = 0;
        foreach (var path in knowledge)
        {
            var section = WikiSections.Parse(path)!;
            if (!bySection.TryGetValue(section, out var peers))
                continue;
            var index = globalIndex ? global : perSection[section];
            if (peers.Any(p => p.Path != path && knowledgeSet.Contains(p.Path) &&
                    WikiTopicScore.Score(index, path, p.Path) is not null))
                withEdge++;
        }
        return knowledge.Count == 0 ? 0 : (double)withEdge / knowledge.Count;
    }

    private static WikiTopicScore.Entry? EntryOf(WikiGate.Draft d)
    {
        if (WikiPage.Parse(d.Content).Model is not { } model)
            return null; // неразобранная страница в индекс не входит (как у индекса показа)
        return new WikiTopicScore.Entry(d.Path, model.Title,
            WikiProjections.SummaryOf(model.Frontmatter, d.Content));
    }
}
