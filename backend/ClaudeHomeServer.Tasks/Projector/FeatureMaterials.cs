using ClaudeHomeServer.Models;

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

    // FeatureMaterial (FS-B3a, §7.4): одна строка materials[] ответа фичи. TaskId — задача,
    // в linkedFiles которой этот путь (для дубля — самая свежая); At = CompletedAt ?? UpdatedAt
    // этой задачи. По taskId фронт открывает файл из дерева автора задачи.
    public sealed record FeatureMaterial(string Path, string TaskId, DateTime At);

    // Build (FS-B3a): materials[] по задачам ЭТОЙ фичи (состав даёт вызывающий — AllRunTasks
    // контроллера, его полноту держит сторож AllRunTasks_и_BuildPlans_НесутЗадачиКаждойРоли).
    // Чистая функция: запись не меняется. Фильтр по проекту эпика — как в AllLinkedFiles
    // (linkedFiles задачи другого проекта — не из дерева этой фичи); отбор — IsMaterial
    // (код/тесты/индексы/макеты не материал никогда). Дубль пути (у нескольких задач) —
    // один раз, от самой свежей задачи (at больше); сравнение дублей регистронезависимое,
    // как в IsMaterial (пути на Windows-машине живут без учёта регистра). Порядок: at убыванием,
    // ties — путь (детерминизм, как в BuildMockups). Потолок — Cap на всю фичу.
    public static List<FeatureMaterial> Build(IEnumerable<TaskItem> tasks, string? epicProjectId)
    {
        var bestByPath = new Dictionary<string, FeatureMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            // Проект эпика — мерило: задача чужого проекта не даёт путь в materials (тот же
            // фильтр, что AllLinkedFiles; эпик без проекта (null) — только задачи без проекта).
            if (t.ProjectId != epicProjectId) continue;
            var at = t.CompletedAt ?? t.UpdatedAt;
            foreach (var path in t.LinkedFiles)
            {
                if (!IsMaterial(path)) continue;
                if (!bestByPath.TryGetValue(path, out var prev) || at > prev.At)
                    bestByPath[path] = new FeatureMaterial(path, t.Id, at);
            }
        }
        return bestByPath.Values
            .OrderByDescending(m => m.At)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .Take(Cap)
            .ToList();
    }
}
