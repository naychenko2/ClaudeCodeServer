using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// Заметка-триггер: новая/изменённая заметка в источнике (личный vault "personal" или notes/ проекта),
// опционально с фильтром по тегам/разделу. Snapshot-дифф по SHA256(title\ntags\nupdatedAt) —
// паттерн NotesKnowledgeService 1:1. updatedAt (mtime файла) меняется при любом сохранении →
// контент-правки ловятся. Снапшот обновляем синхронно с детекцией (встроенный дедуп).
//
// Args: source ("personal"|projectId), tags?:["#тег"], section?:папка
public sealed class NoteTriggerSource(NotesService notes) : ITriggerSource
{
    public AutomationTriggerType Type => AutomationTriggerType.Note;

    public Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        var args = TriggerArgs.Of(ctx.Rule.Trigger);
        var source = args.GetString("source") ?? "personal";
        var wantTags = args.GetStringList("tags");
        var section = args.GetString("section");

        IReadOnlyList<NoteSummary> summaries;
        try { summaries = notes.GetSummaries(ctx.User.Id, source, null); }
        catch { return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>()); }

        IEnumerable<NoteSummary> filtered = summaries;
        if (wantTags is { Count: > 0 })
        {
            var want = wantTags.Select(NormalizeTag).ToHashSet();
            filtered = filtered.Where(n => n.Tags.Any(t => want.Contains(NormalizeTag(t))));
        }
        if (!string.IsNullOrWhiteSpace(section))
        {
            var sec = section.Trim().Trim('/').Replace('\\', '/');
            filtered = filtered.Where(n => n.Path.Replace('\\', '/')
                .StartsWith(sec, StringComparison.OrdinalIgnoreCase));
        }
        var list = filtered.ToList();

        var prev = ctx.State.NoteHashes ?? new Dictionary<string, string>();
        var cur = new Dictionary<string, string>();
        var changed = new List<NoteSummary>();
        foreach (var n in list)
        {
            var hash = Hash($"{n.Title}\n{string.Join(',', n.Tags)}\n{n.UpdatedAt}");
            cur[n.Id] = hash;
            if (!prev.TryGetValue(n.Id, out var old) || old != hash) changed.Add(n);
        }
        ctx.State.NoteHashes = cur;

        if (changed.Count == 0) return Task.FromResult<IReadOnlyList<TriggerEvent>>(Array.Empty<TriggerEvent>());

        var summary = changed.Count == 1
            ? $"Заметка «{changed[0].Title}» создана/изменена"
            : $"Заметок создано/изменено: {changed.Count}";
        var sample = changed.Take(15).Select(n => n.Title).ToList();
        var details = new Dictionary<string, string>
        {
            ["notes"] = string.Join("\n", sample) + (changed.Count > 15 ? $"\n…и ещё {changed.Count - 15}" : ""),
        };
        return Task.FromResult<IReadOnlyList<TriggerEvent>>(new[]
        {
            new TriggerEvent(ctx.Rule.Id, AutomationTriggerType.Note, summary, details),
        });
    }

    private static string NormalizeTag(string t) => t.Trim().TrimStart('#').ToLowerInvariant();
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
