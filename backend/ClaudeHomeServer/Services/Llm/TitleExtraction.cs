using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Извлечение короткого заголовка из ответа фонового действия (заголовок чата/заметки).
// Слабые локальные модели (qwen3:4b) на просьбу «ответь ТОЛЬКО заголовком» выдают
// рассуждения вслух — первая строка получается длиннее лимита, и заголовок молча теряется.
// Поэтому заголовочные действия просят СТРОГИЙ JSON {"title":"…"}: на локальном пути его
// гарантирует structured output Ollama (schema), на claude/direct-пути — сам промпт (JsonHint).
// Парсер устойчив к обоим форматам: сперва JSON.title, иначе — первая непустая строка (как было).
public static class TitleExtraction
{
    // JSON-схема для structured output Ollama: {"title": string}.
    public static readonly object Schema = new
    {
        type = "object",
        properties = new { title = new { type = "string" } },
        required = new[] { "title" },
    };

    // Единый контракт ответа для промпта (локаль/claude/direct — все возвращают один JSON).
    public const string JsonHint = "Ответь СТРОГО одним JSON-объектом вида {\"title\": \"…\"} и ничем больше.";

    // Достаёт заголовок: сперва JSON.title, иначе первая непустая строка. Снимает обрамление
    // (кавычки, markdown-маркеры). null — ничего осмысленного не нашлось.
    public static string? Extract(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var title = TryJsonTitle(text)
                    ?? text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (title is null) return null;
        title = title.Trim().Trim('"', '«', '»', '#', '*', ' ').Trim();
        return title.Length == 0 ? null : title;
    }

    // JSON {"title":"…"} — даже если модель обернула его в прозу или ```json-блок,
    // берём фрагмент от первой { до последней }.
    private static string? TryJsonTitle(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(text[start..(end + 1)]);
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { /* не JSON — вызывающий возьмёт первую строку */ }
        return null;
    }
}
