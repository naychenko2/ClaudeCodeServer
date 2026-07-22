using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// ИИ-помощь по заметкам одноразовыми вызовами: предложение связей, авто-теги, конспект дня.
// Идут через «дешёвый» раннер — локальная модель Ollama (если действие на неё заведено) или
// claude (модель Notes:AiModel, дефолт haiku) как фолбэк/по умолчанию.
public class NotesAiService(NotesService notes, IConfiguration config, Llm.ICheapTextRunner cheap)
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

        var raw = await RunAsync(Llm.LocalActionCatalog.NotesLinks, sb.ToString(), ct);
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

        var raw = await RunAsync(Llm.LocalActionCatalog.NotesTags, sb.ToString(), ct);
        return ParseArray<string>(raw)
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length is > 1 and <= 30 && !note.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Take(5).ToList();
    }

    // Предложить короткий заголовок заметки по её содержимому (для «Без названия»)
    public async Task<string> SuggestTitleAsync(string userId, string noteId, CancellationToken ct)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");
        var prompt =
            "Придумай короткий заголовок (3-6 слов, по-русски, без кавычек и точки в конце) " +
            "для заметки по её содержимому. " + Llm.TitleExtraction.JsonHint + "\n\n" +
            Truncate(note.Content, 2000);
        var raw = await RunAsync(Llm.LocalActionCatalog.NoteTitle, prompt, ct, Llm.TitleExtraction.Schema);
        // Заголовок из строгого JSON (или первой строки — фолбэк), снимаем обрамление/маркеры
        var line = Llm.TitleExtraction.Extract(raw) ?? "";
        if (line.Length > 80) line = line[..80].TrimEnd() + "…";
        return line;
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
            summary = await RunAsync(Llm.LocalActionCatalog.NotesDailySummary, sb.ToString(), ct);
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

    // Оглавление: генерирует markdown-список разделов и вставляет секцию «## Оглавление» в начало заметки
    public async Task<NoteDetail> SuggestTocAsync(string userId, string noteId, CancellationToken ct)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");

        var prompt =
            "Составь оглавление для заметки ниже. Ответь ТОЛЬКО markdown-списком (маркеры «-»), " +
            "по одному пункту на смысловой раздел, с отступами по уровню вложенности. " +
            "Пункты — короткие названия разделов, без нумерации и ссылок, по-русски. Без вступлений и обёртки.\n\n" +
            Truncate(note.Content, 6000);
        var toc = (await RunAsync(Llm.LocalActionCatalog.NoteToc, prompt, ct)).Trim();
        if (toc.Length == 0) throw new InvalidOperationException("Не удалось собрать оглавление");

        const string header = "## Оглавление";
        var body = RemoveSection(note.Content, header);
        var content = InsertAfterFrontmatter(body, $"{header}\n\n{toc}\n");
        return notes.Update(userId, noteId, new UpdateNoteRequest(Content: content))
            ?? throw new InvalidOperationException("Заметка не обновилась");
    }

    // Перевод: переводит заметку (RU↔EN автоопределением) и дописывает секцию «## Перевод» в конце
    public async Task<NoteDetail> TranslateAsync(string userId, string noteId, CancellationToken ct)
    {
        var note = notes.GetDetail(userId, noteId)
            ?? throw new KeyNotFoundException("Заметка не найдена");

        var prompt =
            "Переведи текст заметки ниже: если он преимущественно на русском — на английский, иначе — на русский. " +
            "Сохрани markdown-разметку и структуру. Первой строкой ответа выведи ровно «LANG: English» или «LANG: Русский» " +
            "(язык перевода), затем с новой строки — сам перевод. Без пояснений и обёртки.\n\n" +
            Truncate(note.Content, 6000);
        var raw = (await RunAsync(Llm.LocalActionCatalog.NoteTranslate, prompt, ct)).Trim();
        if (raw.Length == 0) throw new InvalidOperationException("Не удалось перевести заметку");

        var (lang, translated) = SplitLang(raw);
        var body = RemoveSection(note.Content, "## Перевод");
        var content = body.TrimEnd() + $"\n\n## Перевод ({lang})\n\n{translated.Trim()}\n";
        return notes.Update(userId, noteId, new UpdateNoteRequest(Content: content))
            ?? throw new InvalidOperationException("Заметка не обновилась");
    }

    // Отделяет строку-маркер «LANG: X» от тела перевода
    private static (string Lang, string Body) SplitLang(string raw)
    {
        var nl = raw.IndexOf('\n');
        if (nl > 0 && raw[..nl].TrimStart().StartsWith("LANG:", StringComparison.OrdinalIgnoreCase))
        {
            var lang = raw[..nl].Trim()[5..].Trim().Trim(':').Trim();
            if (lang.Length is > 0 and <= 20) return (lang, raw[(nl + 1)..]);
        }
        return ("перевод", raw);
    }

    // Удаляет секцию «## Header … » (от заголовка до следующего «## » или конца) — для идемпотентного пере-запуска
    private static string RemoveSection(string content, string header)
    {
        var idx = content.IndexOf(header, StringComparison.Ordinal);
        if (idx < 0) return content;
        var after = content.IndexOf("\n## ", idx + header.Length, StringComparison.Ordinal);
        var before = content[..idx].TrimEnd();
        var rest = after < 0 ? "" : content[(after + 1)..];
        if (before.Length == 0) return rest;
        return rest.Length == 0 ? before + "\n" : before + "\n\n" + rest;
    }

    // Вставляет блок сразу после frontmatter (--- … ---), иначе в начало заметки
    private static string InsertAfterFrontmatter(string content, string block)
    {
        content = content.TrimStart('\n');
        if (content.StartsWith("---\n", StringComparison.Ordinal) || content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end >= 0)
            {
                var fmEnd = content.IndexOf('\n', end + 1);
                if (fmEnd >= 0)
                {
                    var fm = content[..(fmEnd + 1)];
                    var rest = content[(fmEnd + 1)..].TrimStart('\n');
                    return fm + "\n" + block + (rest.Length > 0 ? "\n" + rest : "");
                }
            }
        }
        return block + (content.Length > 0 ? "\n" + content : "");
    }

    private Task<string> RunAsync(string actionKey, string prompt, CancellationToken ct, object? jsonFormat = null) =>
        cheap.RunAsync(actionKey, prompt, config["Notes:AiModel"] ?? "haiku", jsonFormat: jsonFormat, ct: ct);

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
