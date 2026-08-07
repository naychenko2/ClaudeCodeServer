using System.Globalization;
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

    // Вариант со значком темы: {"title": "…", "emoji": "…"} — заголовок чата ставится в
    // списке рядом с десятками других, и один эмодзи впереди даёт узнавание быстрее текста.
    // Значок не required: модель, которая его не осилила, всё равно отдаёт годный заголовок.
    public static readonly object SchemaWithEmoji = new
    {
        type = "object",
        properties = new { title = new { type = "string" }, emoji = new { type = "string" } },
        required = new[] { "title" },
    };

    public const string JsonHintWithEmoji =
        "Ответь СТРОГО одним JSON-объектом вида {\"title\": \"…\", \"emoji\": \"…\"} и ничем больше. " +
        "В emoji — ровно один эмодзи, подходящий теме разговора (без текста и пояснений).";

    // Достаёт заголовок: сперва JSON.title, иначе первая непустая строка. Снимает обрамление
    // (кавычки, markdown-маркеры). null — ничего осмысленного не нашлось.
    public static string? Extract(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var title = TryJsonProp(text, "title")
                    ?? text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (title is null) return null;
        title = title.Trim().Trim('"', '«', '»', '#', '*', ' ').Trim();
        return title.Length == 0 ? null : title;
    }

    // Значок темы из того же ответа. Набор свободный (что модель придумала, то и берём),
    // но форма жёсткая: ровно одна графема, начинающаяся с эмодзи-символа. Слабая модель
    // на просьбу «один эмодзи» присылает и слово, и «:smile:», и пустую строку — всё это
    // отсекается, и чат остаётся с обычным заголовком. null — значка нет.
    public static string? ExtractEmoji(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = TryJsonProp(raw.Trim(), "emoji")?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        // Одна графема: составные значки (флаги, 👨‍💻 через ZWJ) — это тоже один элемент
        var elements = new StringInfo(value);
        if (elements.LengthInTextElements != 1) return null;
        return IsEmojiStart(value) ? value : null;
    }

    // Значок перед заголовком. Модель, которую попросили про emoji отдельным полем, нередко
    // ставит его ещё и в сам title — тогда второй не приклеиваем, иначе выйдет «🐛 🐛 Правка».
    public static string WithEmoji(string title, string? emoji)
        => emoji is null || IsEmojiStart(title) ? title : emoji + " " + title;

    // Первый кодпоинт — из блоков, где живут эмодзи. Диапазоны отсекают обычный текст любого
    // языка и «текстовые» знаки вроде стрелок U+2190…U+21FF или троеточия — их модель иногда
    // присылает вместо значка, и в имени чата они выглядят как опечатка.
    private static bool IsEmojiStart(string value)
    {
        // Непарный суррогат (обрезанный моделью значок) — не значок, а мусор
        if (value.Length == 0 || (char.IsSurrogate(value[0]) && !char.IsHighSurrogate(value[0]))) return false;
        if (char.IsHighSurrogate(value[0]) && value.Length < 2) return false;
        var cp = char.ConvertToUtf32(value, 0);
        return cp is >= 0x1F000 and <= 0x1FAFF   // пиктограммы, лица, флаги, символы
            or >= 0x2600 and <= 0x27BF           // разное + dingbats (☀ ✅ ✨)
            or >= 0x2B00 and <= 0x2BFF           // звёзды и крупные стрелки (⭐ ⬆)
            or 0x231A or 0x231B or 0x23F0 or 0x23F3 or 0x24C2 or 0x25B6 or 0x25C0;
    }

    // Строковое поле JSON-ответа ({"title":"…"}, {"emoji":"…"}) — даже если модель обернула
    // объект в прозу или ```json-блок, берём фрагмент от первой { до последней }.
    private static string? TryJsonProp(string text, string prop)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(text[start..(end + 1)]);
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty(prop, out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { /* не JSON — вызывающий возьмёт первую строку */ }
        return null;
    }
}
