using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// Триггер смены статуса задачи: переход Todo→InProgress→Done (опционально с фильтром from/to и проектом).
// Нет лога событий — только текущий TaskItem.Status, поэтому snapshot-дифф (как NotesKnowledgeService,
// но по enum без хеша): taskId→status. Снапшот обновляем СИНХРОННО с детекцией → собственная мутация
// экшена (если правило через персону меняет задачу) «невидима» следующему тику (anti-loop).
//
// Args: projectId?, from?:"Todo"|"InProgress"|"Done", to?:..., assignee?:"me"|"claude" (Phase 2)
public sealed class TaskStatusTriggerSource(TaskManager tasks) : ITriggerSource
{
    public AutomationTriggerType Type => AutomationTriggerType.TaskStatus;

    public Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        var args = TriggerArgs.Of(ctx.Rule.Trigger);
        var projectId = args.GetString("projectId");
        var fromFilter = args.GetString("from");
        var toFilter = args.GetString("to");

        var all = tasks.GetByOwner(ctx.User.Id);
        IEnumerable<TaskItem> filtered = all;
        if (!string.IsNullOrWhiteSpace(projectId))
            filtered = filtered.Where(t => t.ProjectId == projectId);
        var list = filtered.ToList();

        var prev = ctx.State.TaskStatusSnapshot ?? new Dictionary<string, string>();
        var cur = new Dictionary<string, string>();
        var transitions = new List<(TaskItem Task, string From, string To)>();
        foreach (var t in list)
        {
            var status = t.Status.ToString();
            cur[t.Id] = status;
            if (prev.TryGetValue(t.Id, out var old) && old != status
                && (fromFilter is null || old.Equals(fromFilter, StringComparison.OrdinalIgnoreCase))
                && (toFilter is null || status.Equals(toFilter, StringComparison.OrdinalIgnoreCase)))
            {
                transitions.Add((t, old, status));
            }
        }
        ctx.State.TaskStatusSnapshot = cur;   // синхронно с детекцией (anti-loop)

        if (transitions.Count == 0) return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var sample = transitions.Take(15).Select(x => $"• «{x.Task.Title}»: {x.From} → {x.To}").ToList();
        var summary = transitions.Count == 1
            ? $"Задача «{transitions[0].Task.Title}» перешла {transitions[0].From} → {transitions[0].To}"
            : $"Переходов статуса задач: {transitions.Count}";
        var details = new Dictionary<string, string>
        {
            ["transitions"] = string.Join("\n", sample) + (transitions.Count > 15 ? $"\n…и ещё {transitions.Count - 15}" : ""),
        };
        return Task.FromResult<IReadOnlyList<TriggerEvent>>(new[]
        {
            new TriggerEvent(ctx.Rule.Id, AutomationTriggerType.TaskStatus, summary, details),
        });
    }
}
