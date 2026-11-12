using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services.Tasks;

// Граф маршрута фичи (разрез docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.4–1.5, 1.7):
// чистые статические функции без состояния и хранилища. Единственная точка правды на вопросы
// «какие этапы в маршруте», «в какой колонке этап», «какой этап у задачи» и «пройден ли этап» —
// второй реализации не заводим. Порядок списка этапов — топологический (After ссылается только
// на более ранние), поэтому все проходы линейны.
public static class WorkflowGraph
{
    // Текст отказа «этап снят маршрутом» — тот же, что TaskManager.StageSkippedText (B2).
    public const string StageSkippedText = "этап пропущен маршрутом";

    // Потолок этапов в шаблоне (разрез §1.6).
    public const int MaxStages = 16;

    // Допустимые линии этапа step (лексика FeatureProjector.NormalizeLane).
    public static readonly IReadOnlyList<string> Lanes = ["back", "front", "other"];

    // Этап шаблона после нормализации: Id и After уже подставлены.
    public sealed record Node(string Id, string Role, string? Lane, string Presence, List<string> After);

    // Состояния этапа маршрута (разрез §1.7).
    public static class StageStates
    {
        public const string Done = "done";
        public const string Active = "active";
        public const string Pending = "pending";
        public const string Absent = "absent";
        public const string Skipped = "skipped";
    }

    // Результат выбора этапа задачи: либо id, либо текст ошибки (без исключения — проекция
    // зовёт StageFor на данных, которые не должны её ронять).
    public readonly record struct StageChoice(string? StageId, string? Error)
    {
        public bool Ok => StageId is not null;
    }

    // Id ??= Role, After ??= [предыдущий в списке] (у первого — []), trim. null-элемент даёт узел
    // с пустыми Id/Role, чтобы индексы списка совпадали с исходным (отчёт об ошибках — по номеру).
    public static List<Node> Normalize(IReadOnlyList<WorkflowStage?> stages)
    {
        var result = new List<Node>(stages.Count);
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var role = stage?.Role?.Trim() ?? "";
            var id = string.IsNullOrWhiteSpace(stage?.Id) ? role : stage!.Id!.Trim();
            var lane = string.IsNullOrWhiteSpace(stage?.Lane) ? null : stage!.Lane!.Trim();
            var presence = stage?.Presence?.Trim() ?? "";
            var after = stage?.After is { } explicitAfter
                ? explicitAfter.Select(a => a?.Trim() ?? "").ToList()
                : i == 0 ? [] : [result[i - 1].Id];
            result.Add(new Node(id, role, lane, presence, after));
        }
        return result;
    }

    // Подтверждаемый маршрут: этапы Presence=off и снятые optional убираются, рёбра обходят убранный
    // этап — After наследника = ∪ After убранного (транзитивно). skipped — id этапов; снять можно
    // только optional (id несъёмного или неизвестного этапа игнорируется, его отсев — дело ручки B4b).
    // Шаблон должен быть валиден (WorkflowRules.Validate): на битых данных результат не определён.
    public static List<RouteStage> Resolve(WorkflowTemplate template, IEnumerable<string>? skipped) =>
        Resolve(Normalize(template.Stages), skipped is null ? [] : new HashSet<string>(skipped, StringComparer.Ordinal));

    internal static List<RouteStage> Resolve(List<Node> nodes, HashSet<string> skipped)
    {
        var route = new List<RouteStage>();
        // Для убранного этапа — его After, уже развёрнутый через ранее убранные.
        var removed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var after = new List<string>();
            foreach (var parent in node.After)
            {
                if (removed.TryGetValue(parent, out var inherited))
                {
                    foreach (var p in inherited)
                        if (!after.Contains(p, StringComparer.Ordinal)) after.Add(p);
                }
                else if (!after.Contains(parent, StringComparer.Ordinal))
                {
                    after.Add(parent);
                }
            }

            var drop = node.Presence == WorkflowStagePresence.Off
                || (node.Presence == WorkflowStagePresence.Optional && skipped.Contains(node.Id));
            if (drop) removed[node.Id] = after;
            else route.Add(new RouteStage { Id = node.Id, Role = node.Role, Lane = node.Lane, After = after });
        }
        return route;
    }

    // Не-последние этапы, на которые никто не ссылается: «висящие ветки». Пусто — единый сток.
    public static List<string> DanglingIds(IReadOnlyList<RouteStage> route)
    {
        var referenced = new HashSet<string>(route.SelectMany(s => s.After), StringComparer.Ordinal);
        return route.Take(Math.Max(route.Count - 1, 0))
            .Where(s => !referenced.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();
    }

    // Колонка этапа = длиннейший путь от корня (корень — 0).
    public static Dictionary<string, int> Columns(IReadOnlyList<RouteStage> route)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stage in route)
        {
            var column = 0;
            foreach (var parent in stage.After)
                if (depth.TryGetValue(parent, out var d)) column = Math.Max(column, d + 1);
            depth[stage.Id] = column;
        }
        return depth;
    }

    // Однозначный выбор этапа задачи (правила §1.5 п.1–4). role — РОЛЬ ЭТАПА (rework уже
    // отображён на step вызывающим), lane — линия задачи как есть; п.5 (неявная роль дефекта
    // слепка не получает) — дело вызывающего: сюда он не доходит.
    public static StageChoice StageFor(IReadOnlyList<RouteStage> route, string role, string? lane, string? explicitStageId)
    {
        if (!string.IsNullOrWhiteSpace(explicitStageId))
        {
            var id = explicitStageId.Trim();
            var explicitStage = route.FirstOrDefault(s => s.Id == id);
            if (explicitStage is null)
                return Error($"этап «{id}» не найден в маршруте фичи.");
            return explicitStage.Role == role
                ? new StageChoice(explicitStage.Id, null)
                : Error($"этап «{id}» имеет роль «{explicitStage.Role}», а задача — роль «{role}».");
        }

        var candidates = route.Where(s => s.Role == role).ToList();
        if (candidates.Count == 0)
            return Error($"{StageSkippedText}: роль «{role}» снята в маршруте фичи.");
        if (candidates.Count == 1)
            return new StageChoice(candidates[0].Id, null);

        var ids = string.Join(", ", candidates.Select(s => s.Id));
        if (role != TaskRoles.Step)
            return Error($"в маршруте несколько этапов роли «{role}»: укажите stageId ({ids}).");

        var taskLane = FeatureProjector.NormalizeLane(lane);
        var byLane = candidates.Where(s => s.Lane is not null && FeatureProjector.NormalizeLane(s.Lane) == taskLane).ToList();
        if (byLane.Count == 1) return new StageChoice(byLane[0].Id, null);
        if (byLane.Count == 0)
        {
            var laneless = candidates.Where(s => s.Lane is null).ToList();
            if (laneless.Count == 1) return new StageChoice(laneless[0].Id, null);
        }
        return Error($"в маршруте несколько этапов step, линия «{taskLane}» не выбирает один: укажите stageId ({ids}).");
    }

    // Состояние каждого этапа маршрута по DAG (§1.7): done — задачи есть и все Done; active — есть
    // идущая; absent — задач нет, а наследник в графе уже начат (пустой план тестирования не
    // «пропадает» оттого, что начался фронт); иначе pending. skipped — id снятых этапов: они в
    // маршруте не лежат, но состояние skipped отдаётся им же. Задача попадает в этап по Stage.StageId;
    // задачи без слепка (созданные до подтверждения) сюда не входят — их раскладывает проекция (G3).
    public static Dictionary<string, string> States(
        IReadOnlyList<RouteStage> route, IEnumerable<TaskItem> tasks, IEnumerable<string>? skipped = null)
    {
        var byStage = tasks
            .Where(t => !string.IsNullOrEmpty(t.Stage?.StageId))
            .GroupBy(t => t.Stage!.StageId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // descendantStarted: хотя бы у одного потомка этапа есть задачи. Идём с конца: потомки
        // стоят позже в списке, поэтому к моменту этапа его отметка уже полная.
        var descendantStarted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = route.Count - 1; i >= 0; i--)
        {
            var anyStarted = byStage.ContainsKey(route[i].Id) || descendantStarted.Contains(route[i].Id);
            if (!anyStarted) continue;
            foreach (var parent in route[i].After) descendantStarted.Add(parent);
        }

        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in skipped ?? [])
            states[id] = StageStates.Skipped;
        foreach (var stage in route)
        {
            byStage.TryGetValue(stage.Id, out var own);
            states[stage.Id] =
                own is { Count: > 0 } && own.All(t => t.Status == TaskItemStatus.Done) ? StageStates.Done
                : own is { Count: > 0 } && own.Any(t => t.Status == TaskItemStatus.InProgress) ? StageStates.Active
                : own is null && descendantStarted.Contains(stage.Id) ? StageStates.Absent
                : StageStates.Pending;
        }
        return states;
    }

    private static StageChoice Error(string text) => new(null, text);
}
