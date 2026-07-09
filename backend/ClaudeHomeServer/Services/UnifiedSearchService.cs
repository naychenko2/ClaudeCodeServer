using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Единый поиск по рабочему пространству (флаг unified-search): заметки + задачи
// в одной выдаче. Заметки — семантика (Dify) с фолбэком на ключевой поиск; задачи —
// ключевой поиск (короткие тексты, семантика избыточна).
//
// MVP: чаты (транскрипты сессий) и файлы проектов пока НЕ индексируются — это следующий
// шаг (требует отдельного Dify-пайплайна по образцу NotesKnowledgeService).
public sealed class UnifiedSearchService(
    NotesService notes, NotesKnowledgeService kb, TaskManager tasks, ProjectManager projects)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string userId, string query, int topK)
    {
        var hits = new List<SearchHit>();

        // --- Заметки ---
        var noteHitsAdded = false;
        if (kb.Available)
        {
            try
            {
                foreach (var h in await kb.SearchAsync(userId, query, topK))
                    hits.Add(new SearchHit("note", h.Id, h.Title, h.SourceLabel, h.Snippet, h.Score, NoteUrl(h.Id)));
                noteHitsAdded = hits.Count > 0;
            }
            catch { /* Dify недоступен — ключевой фолбэк ниже */ }
        }
        if (!noteHitsAdded)
        {
            foreach (var s in notes.GetSummaries(userId, null, query).Take(topK))
                hits.Add(new SearchHit("note", s.Id, s.Title, s.SourceLabel, "", null, NoteUrl(s.Id)));
        }

        // --- Задачи (ключевой поиск) ---
        foreach (var t in MatchTasks(userId, query).Take(topK))
        {
            var ctx = t.ProjectId is null
                ? "Личная задача"
                : projects.GetById(t.ProjectId)?.Name ?? "Проект";
            hits.Add(new SearchHit("task", t.Id, t.Title, ctx, Snippet(t.Description, query), null, TaskUrl(t)));
        }

        return hits;
    }

    // Задачи владельца, где запрос встречается в названии/описании/метках.
    // Незавершённые — выше; затем по свежести.
    private IEnumerable<TaskItem> MatchTasks(string userId, string query) =>
        tasks.GetByOwner(userId)
            .Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Labels.Any(l => l.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Status == TaskItemStatus.Done ? 1 : 0)
            .ThenByDescending(t => t.UpdatedAt);

    private static string NoteUrl(string id) => "#/notes/" + Uri.EscapeDataString(id);

    private static string TaskUrl(TaskItem t) =>
        t.ProjectId is null
            ? $"#/calendar/task/{t.Id}"
            : $"#/project/{t.ProjectId}/task/{t.Id}";

    // Сниппет вокруг совпадения (для контекста в выдаче)
    private static string Snippet(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = text.ReplaceLineEndings(" ").Trim();
        var idx = flat.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return flat.Length <= 160 ? flat : flat[..160] + "…";
        var start = Math.Max(0, idx - 60);
        var len = Math.Min(flat.Length - start, 160);
        var frag = flat.Substring(start, len);
        return (start > 0 ? "…" : "") + frag + (start + len < flat.Length ? "…" : "");
    }
}

// Элемент единой выдачи. Type: "note" | "task". Url — hash-диплинк.
public record SearchHit(
    string Type, string Id, string Title, string Context, string Snippet, double? Score, string Url);
