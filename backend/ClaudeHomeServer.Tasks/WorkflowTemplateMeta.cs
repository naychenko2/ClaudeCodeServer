using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks;

// Мета строки списка шаблонов (B08): чистые функции без хранилища — число этапов, колонки
// параллели и отличие встроенного шаблона от кодового пресета. Колонки считает тот же
// WorkflowGraph.Columns, что и граф процесса: второй реализации нет.
public static class WorkflowTemplateMeta
{
    private static readonly JsonSerializerOptions Cmp = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int StageCount(WorkflowTemplate t) => t.Stages.Count;

    // Число колонок графа, где два и более этапов (все этапы шаблона, независимо от presence).
    public static int ParallelColumns(WorkflowTemplate t)
    {
        var route = WorkflowGraph.Normalize(t.Stages.Cast<WorkflowStage?>().ToList())
            .Select(n => new RouteStage { Id = n.Id, Role = n.Role, After = n.After })
            .ToList();
        return WorkflowGraph.Columns(route).Values.GroupBy(c => c).Count(g => g.Count() >= 2);
    }

    // Для встроенного: сколько позиций этапов отличается от кодового пресета (любое поле этапа;
    // лишний или недостающий этап тоже считается). 0 — «как исходный». Не встроенный — null.
    public static int? DiffFromPreset(WorkflowTemplate t)
    {
        if (t.BuiltInKey is null || !WorkflowTemplateStore.BuiltInKeys.Contains(t.BuiltInKey)) return null;
        var preset = Canonical(WorkflowTemplateStore.BuiltInPreset(t.BuiltInKey));
        var actual = Canonical(t);
        var diff = 0;
        for (var i = 0; i < Math.Max(preset.Count, actual.Count); i++)
            if (i >= preset.Count || i >= actual.Count || preset[i] != actual[i]) diff++;
        return diff;
    }

    // Этап как строка с подставленными по умолчанию Id и After — запись «id = роль» и явный id равны.
    private static List<string> Canonical(WorkflowTemplate t)
    {
        var nodes = WorkflowGraph.Normalize(t.Stages.Cast<WorkflowStage?>().ToList());
        return t.Stages.Select((s, i) => JsonSerializer.Serialize(new
        {
            s.Role, nodes[i].Id, nodes[i].After, s.Lane, s.Title, s.Presence, s.Specialty, s.PersonaId,
            s.Prompt, s.TierStrong, s.TierMedium, s.TierWeak, s.DefaultTier, s.DisallowedTools,
            s.McpAllow, s.StallMinutes, s.StallPolicy,
        }, Cmp)).ToList();
    }
}
