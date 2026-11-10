namespace ClaudeHomeServer.Services.Tasks.Projector;

// Правило материала фичи (FS-B0, волна 3 «Обзор фич», docs/research/
// features-simplify-architecture-2026-11.md §7.1): чистая функция — какой файл является
// материалом фичи, а какой шумом в группе «Прочие документы». Код, тесты, конфиги и файлы
// вне docs/ материалом не являются никогда; индексы и макеты (группа «Макеты») — тоже.
public static class FeatureMaterials
{
    // Общий потолок путей на фичу: 30 (вместо прежнего 20 на задачу — у живой фичи их
    // набегало 110).
    public const int Cap = 30;

    // IsMaterial: материал — файл под docs/, не макет (группа «Макеты»), не индекс
    // (docs/CHANGELOG.md, docs/README.md, любой README.md/index.md под docs/), без «..».
    // Сравнение регистронезависимое — как схлопывание дублей (пути на Windows-машине
    // живут без учёта регистра).
    public static bool IsMaterial(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // «..» — выход за границы (pathtraversal), как в прежнем фильтре MockupPaths.
        if (path.Contains("..")) return false;
        if (!path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsMockup(path)) return false;
        // Имя файла после последнего «/»; пустое («docs/») — не файл.
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Length == 0) return false;
        if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("index.md", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Equals("docs/CHANGELOG.md", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // IsMockup: территория группы «Макеты» — docs/mockups/ и сам каталог (без слэша).
    public static bool IsMockup(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.StartsWith("docs/mockups/", StringComparison.OrdinalIgnoreCase) ||
         path.Equals("docs/mockups", StringComparison.OrdinalIgnoreCase));
}
