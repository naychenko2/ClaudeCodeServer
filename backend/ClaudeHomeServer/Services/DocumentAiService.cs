using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// ИИ-помощь по документам: конвертирует бинарный документ в Markdown (MarkitdownService),
// затем локальной моделью (ICheapTextRunner, с фолбэком на claude) строит краткое содержание,
// выжимку (решения/даты/участники/действия) или теги. null от markitdown → null результат
// (документ не распознан / нет markitdown). Модель — Notes:AiModel/Tasks:AiModel (дефолт haiku).
public sealed class DocumentAiService(
    MarkitdownService markitdown, Llm.ICheapTextRunner cheap, IConfiguration config)
{
    private const int MdBudget = 12_000;   // символов Markdown в промпт (усечение хвоста)
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private string Model => config["Notes:AiModel"] ?? config["Tasks:AiModel"] ?? "haiku";

    public record DocExtractResult(
        IReadOnlyList<string> Decisions, IReadOnlyList<string> Dates,
        IReadOnlyList<string> People, IReadOnlyList<string> ActionItems);

    // Конвертация в Markdown без модели (детерминированно). null — не распознан / нет markitdown.
    public Task<string?> ConvertAsync(string absolutePath, CancellationToken ct) =>
        markitdown.ConvertAsync(absolutePath, ct);

    // Краткое содержание документа: 5-8 пунктов сути.
    public async Task<string?> SummaryAsync(string absolutePath, CancellationToken ct)
    {
        var md = await markitdown.ConvertAsync(absolutePath, ct);
        if (string.IsNullOrWhiteSpace(md)) return null;
        var prompt =
            "Ниже — документ в Markdown. Составь краткое содержание: 5-8 пунктов маркированного списка " +
            "по сути, по-русски. Ответь ТОЛЬКО markdown-списком, без вступлений.\n\n" + Truncate(md, MdBudget);
        return await cheap.RunAsync(Llm.LocalActionCatalog.DocSummary, prompt, Model, ct: ct);
    }

    // Структурная выжимка: решения, даты/сроки, участники, action items.
    public async Task<DocExtractResult?> ExtractAsync(string absolutePath, CancellationToken ct)
    {
        var md = await markitdown.ConvertAsync(absolutePath, ct);
        if (string.IsNullOrWhiteSpace(md)) return null;
        var prompt =
            "Ниже — документ в Markdown. Извлеки структурированную выжимку. Ответь ТОЛЬКО JSON-объектом " +
            "без пояснений: {\"decisions\":[],\"dates\":[],\"people\":[],\"actionItems\":[]}. " +
            "decisions — принятые решения; dates — важные даты/сроки (с контекстом); people — упомянутые " +
            "участники/ответственные; actionItems — задачи/следующие шаги. Пусто → []. По-русски.\n\n" +
            Truncate(md, MdBudget);
        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.DocExtract, prompt, Model, ct: ct);
        return ParseExtract(raw);
    }

    // До 6 тегов по содержимому документа.
    public async Task<IReadOnlyList<string>?> TagsAsync(string absolutePath, CancellationToken ct)
    {
        var md = await markitdown.ConvertAsync(absolutePath, ct);
        if (string.IsNullOrWhiteSpace(md)) return null;
        var prompt =
            "Ниже — документ в Markdown. Предложи до 6 коротких тегов (одно-два слова, по-русски, без #) " +
            "по теме документа. Ответь ТОЛЬКО JSON-массивом строк.\n\n" + Truncate(md, MdBudget);
        var raw = await cheap.RunAsync(Llm.LocalActionCatalog.DocTags, prompt, Model, ct: ct);
        return ParseStringArray(raw).Select(t => t.Trim().TrimStart('#').Trim())
            .Where(t => t.Length is > 1 and <= 30).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    private static DocExtractResult ParseExtract(string raw)
    {
        var empty = new DocExtractResult([], [], [], []);
        var json = ExtractJsonObject(raw);
        if (json is null) return empty;
        try
        {
            var d = JsonSerializer.Deserialize<ExtractRaw>(json, JsonOpts);
            if (d is null) return empty;
            return new DocExtractResult(
                Clean(d.Decisions), Clean(d.Dates), Clean(d.People), Clean(d.ActionItems));
        }
        catch (JsonException) { return empty; }
    }

    private static IReadOnlyList<string> Clean(List<string>? xs) =>
        xs?.Select(s => s?.Trim() ?? "").Where(s => s.Length > 0).Take(20).ToList() ?? [];

    private static IReadOnlyList<string> ParseStringArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try { return JsonSerializer.Deserialize<List<string>>(raw[start..(end + 1)], JsonOpts) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";

    private sealed record ExtractRaw(
        List<string>? Decisions, List<string>? Dates, List<string>? People, List<string>? ActionItems);
}
