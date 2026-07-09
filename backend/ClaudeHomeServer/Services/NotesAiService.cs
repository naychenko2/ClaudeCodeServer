using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// ИИ-помощь по заметкам одноразовыми вызовами claude --print: предложение связей,
// авто-теги, конспект дня. Модель — Notes:AiModel (дефолт haiku).
public class NotesAiService(NotesService notes, IConfiguration config, Llm.OneShotClaudeRunner runner)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public record SuggestedLink(string Title, string Why);

    // Предложить связи: с какими существующими заметками стоит связать текущую
    public async Task<IReadOnlyList<SuggestedLink>> SuggestLinksAsync(string userId, string noteId, CancellationToken ct)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");
        var all = notes.GetSummaries(userId, null, null);
        var linked = new HashSet<string>(note.Links.Select(l => l.TargetTitle), StringComparer.OrdinalIgnoreCase);
        var candidates = all
            .Where(s => s.Id != noteId && !linked.Contains(s.Title))
            .Select(s => s.Title).Take(120).ToList();
        if (candidates.Count == 0) return [];

        var sb = new StringBuilder();
        sb.AppendLine("У пользователя база заметок со связями [[Заголовок]]. Ниже — текст текущей заметки и " +
                      "заголовки остальных. Выбери до 5 заметок, с которыми ПО СМЫСЛУ стоит связать текущую. " +
                      "Ответь ТОЛЬКО JSON-массивом объектов {\"title\": \"точный заголовок из списка\", \"why\": \"почему, 5-10 слов\"} " +
                      "без пояснений и markdown-обёртки. Если связывать не с чем — верни [].");
        sb.AppendLine();
        sb.AppendLine($"Текущая заметка «{note.Title}»:");
        sb.AppendLine(Truncate(note.Content, 4000));
        sb.AppendLine();
        sb.AppendLine("Заголовки остальных заметок:");
        foreach (var t in candidates) sb.AppendLine($"- {t}");

        var raw = await RunAsync(sb.ToString(), ct);
        var parsed = ParseArray<SuggestedLink>(raw);
        // Отсекаем галлюцинации: только реально существующие заголовки
        var valid = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        return parsed.Where(l => valid.Contains(l.Title)).Take(5).ToList();
    }

    // Предложить до 5 тегов для заметки (существующие теги базы — приоритетны)
    public async Task<IReadOnlyList<string>> SuggestTagsAsync(string userId, string noteId, CancellationToken ct)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");
        var existingTags = notes.GetSummaries(userId, null, null)
            .SelectMany(s => s.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Предложи до 5 коротких тегов (одно слово или слова-через-дефис, без #, по-русски) " +
                      "для заметки ниже. Если подходят теги из списка существующих — используй их. " +
                      "Ответь ТОЛЬКО JSON-массивом строк без пояснений.");
        sb.AppendLine();
        sb.AppendLine($"Заметка «{note.Title}»:");
        sb.AppendLine(Truncate(note.Content, 4000));
        if (existingTags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Существующие теги базы: " + string.Join(", ", existingTags));
        }

        var raw = await RunAsync(sb.ToString(), ct);
        return ParseArray<string>(raw)
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length is > 1 and <= 30 && !note.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Take(5).ToList();
    }

    // Конспект дня: сводка по заметкам, изменённым сегодня, дописывается в daily note
    public async Task<NoteDetail> DailySummaryAsync(string userId, string? date, CancellationToken ct)
    {
        var daily = notes.GetOrCreateDaily(userId, date);
        var day = string.IsNullOrWhiteSpace(date) ? DateTime.Now.ToString("yyyy-MM-dd") : date!.Trim();

        var changedToday = notes.GetSummaries(userId, null, null)
            .Where(s => s.Id != daily.Id && s.UpdatedAt.StartsWith(day))
            .Take(30).ToList();

        string summary;
        if (changedToday.Count == 0)
        {
            summary = "_Сегодня заметки не менялись._";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("Составь краткий конспект дня по заметкам, изменённым сегодня (ниже). " +
                          "3-6 пунктов маркированного списка: суть изменений и мыслей, по-русски, " +
                          "названия заметок оформляй ссылками [[Заголовок]]. " +
                          "Ответь ТОЛЬКО markdown-списком, без вступлений.");
            sb.AppendLine();
            foreach (var s in changedToday)
            {
                var d = notes.GetDetail(userId, s.Id);
                if (d is null) continue;
                sb.AppendLine($"### {d.Title}");
                sb.AppendLine(Truncate(d.Content, 1500));
                sb.AppendLine();
            }
            summary = await RunAsync(sb.ToString(), ct);
        }

        // Секцию «Итоги дня» заменяем при повторном вызове, иначе дописываем
        var content = daily.Content;
        const string header = "## Итоги дня";
        var idx = content.IndexOf(header, StringComparison.Ordinal);
        content = (idx >= 0 ? content[..idx].TrimEnd() : content.TrimEnd())
                  + $"\n\n{header}\n\n{summary.Trim()}\n";
        return notes.Update(userId, daily.Id, new UpdateNoteRequest(Content: content))
            ?? throw new InvalidOperationException("Дневниковая заметка не обновилась");
    }

    private Task<string> RunAsync(string prompt, CancellationToken ct) =>
        runner.RunAsync(prompt, runner.NormalizeModel(config["Notes:AiModel"] ?? "haiku"), ct: ct);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";

    // JSON-массив из ответа модели (текст вокруг отбрасывается; мусор → пустой список)
    private static IReadOnlyList<T> ParseArray<T>(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try { return JsonSerializer.Deserialize<List<T>>(raw[start..(end + 1)], JsonOpts) ?? []; }
        catch (JsonException) { return []; }
    }
}
