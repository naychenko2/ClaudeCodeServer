using System.Text.Json;
using System.Text.RegularExpressions;

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

    // Вариант со значком темы: {"title": "…", "iconName": "Cat"} — iconName это ИМЯ компонента
    // lucide-react в PascalCase. Каталога тем больше нет: модель свободно выбирает иконку под
    // предмет разговора (Cat, Dog, Bug, User, MousePointerClick…), фронт рисует icons[iconName].
    // Не required: без иконки заголовок всё равно годен.
    public static readonly object SchemaWithIcon = new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            iconName = new { type = "string" },
        },
        required = new[] { "title" },
    };

    // Вариант «только значок»: для проставления иконки существующим чатам, где имя уже сложилось
    // и переименовывать не нужно.
    public static readonly object SchemaIcon = new
    {
        type = "object",
        properties = new { iconName = new { type = "string" } },
        required = new[] { "iconName" },
    };

    // Модель выбирает имя lucide-компонента под предмет разговора. Доступны все ~1700 иконок
    // lucide-react (модели знают их по обучению). Примеры помогают попасть в формат PascalCase.
    public static string JsonHintWithIcon =>
        "Ответь СТРОГО одним JSON-объектом вида {\"title\": \"…\", \"iconName\": \"…\"} и ничем больше. " +
        "iconName — имя компонента иконок lucide-react в PascalCase, подходящее предмету разговора " +
        "(напр. Bug для бага, Code для кода, Cat для кошки, Dog для собаки, User для человека, " +
        "Palette для дизайна, Rocket для деплоя, Settings для настройки, FileText для документов, " +
        "Search для поиска, Database для данных, Wallet для денег). Если подходящей иконки нет — " +
        "опусти iconName.";

    public static string IconHint =>
        "Определи предмет разговора и подбери ИМЯ компонента иконок lucide-react в PascalCase, " +
        "которое его изображает (Bug — баг, Code — код, Cat — кошка, Dog — собака, User — человек, " +
        "Palette — дизайн, Rocket — деплой, Settings — настройка, FileText — документы, Search — " +
        "поиск, Database — данные, Wallet — деньги, MessageCircle — разговор, и т.п.; всего доступно " +
        "~1700 имён). Ответь СТРОГО одним JSON-объектом вида {\"iconName\": \"…\"} и ничем больше. " +
        "Если подходящей иконки нет — ответь пустым {}.";

    // Начинается ли имя с эмодзи. Осталось от прежней реализации значка (эмодзи в самом
    // имени) — нужно миграции, которая чистит такие имена.
    public static bool HasEmoji(string? title)
        => !string.IsNullOrEmpty(title) && IsEmojiStart(title.Trim());

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

    // Имя lucide-иконки из ответа модели. Белого списка НЕТ (всё ~1700 имён): бэк доверяет
    // модели, а фронт проверит icons[iconName] и не нарисует то, чего нет. Sanity отсекает
    // явный мусор — не PascalCase (Cat, MousePointerClick), с пробелами, слишком длинное.
    // null — имени нет (поле отсутствует, пустое или не похоже на имя компонента).
    private static readonly Regex IconNamePattern = new(@"^[A-Z][A-Za-z0-9]{1,39}$", RegexOptions.Compiled);
    public static string? ExtractIconName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = TryJsonProp(raw.Trim(), "iconName")?.Trim();
        return !string.IsNullOrEmpty(value) && IconNamePattern.IsMatch(value) ? value : null;
    }

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
