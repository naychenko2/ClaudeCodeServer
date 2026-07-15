using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Переводы для реестра навыков (реестр skills.sh — на английском):
//  • запрос поиска RU→EN (иначе fuzzy/semantic-поиск по английским именам мимо);
//  • описания навыков EN→RU для показа в LLM-подборе.
// Модель — Skills:AiModel (дефолт haiku), one-shot через OneShotClaudeRunner. Всё кэшируется.
public class SkillTranslationService(
    IOneShotRunner runner,
    IConfiguration config,
    ILogger<SkillTranslationService> log)
{
    private string? AiModel => config["Skills:AiModel"] is { Length: > 0 } m ? m : "haiku";
    private TimeSpan Timeout =>
        TimeSpan.FromMilliseconds(int.TryParse(config["Skills:TranslateTimeoutMs"], out var ms) ? ms : 60_000);

    // Кэш переводов запросов (RU-запрос → EN)
    private readonly ConcurrentDictionary<string, string> _queryCache = new(StringComparer.OrdinalIgnoreCase);
    // Кэш переводов описаний: ключ source@skill → (хэш англ. текста, русский перевод).
    // Хэш защищает от устаревания, если описание в реестре изменится.
    private readonly ConcurrentDictionary<string, (int Hash, string Ru)> _descCache = new();

    private static bool HasCyrillic(string s) => s.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    // --- Перевод запроса RU→EN ---

    // Латинский запрос возвращается как есть. Ошибка перевода → исходный запрос (поиск не падает).
    public async Task<string> TranslateQueryAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0 || !HasCyrillic(q)) return q;
        if (_queryCache.TryGetValue(q, out var cached)) return cached;

        var prompt =
            "Переведи поисковый запрос на английский язык — коротко, только ключевые слова для поиска " +
            "технических навыков (skills) агента. Верни ТОЛЬКО перевод одной строкой, без пояснений и кавычек.\n\n" +
            $"Запрос: {q}";
        try
        {
            var ans = await runner.RunAsync(prompt, runner.NormalizeModel(AiModel), Timeout, ct);
            var en = FirstLine(ans);
            if (en.Length == 0) return q;
            _queryCache[q] = en;
            return en;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Перевод запроса «{Query}» не удался — ищу по оригиналу", q);
            return q;
        }
    }

    // --- Перевод описаний EN→RU (батч) ---

    // items: (ключ source@skill, английский текст). Возвращает ключ → русский перевод
    // (только для успешно переведённых). Уже кэшированные (по неизменному тексту) не переводятся повторно.
    // timeout — переопределение дефолтного (Skills:TranslateTimeoutMs) для неспешных фоновых путей.
    public async Task<IReadOnlyDictionary<string, string>> TranslateDescriptionsAsync(
        IReadOnlyList<(string Key, string Text)> items, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var toTranslate = new List<(string Key, string Text)>();

        foreach (var (key, text) in items)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var hash = text.GetHashCode();
            if (_descCache.TryGetValue(key, out var c) && c.Hash == hash)
                result[key] = c.Ru;
            else
                toTranslate.Add((key, text));
        }
        if (toTranslate.Count == 0) return result;

        // Нумеруем описания (простые числовые id надёжнее для LLM, чем ключи с «/» и «@»)
        var sb = new StringBuilder();
        sb.AppendLine("Переведи описания навыков (skills) на русский язык — сохрани смысл, естественно и кратко.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом {\"<номер>\":\"<перевод>\"} без markdown и пояснений.");
        sb.AppendLine();
        for (var i = 0; i < toTranslate.Count; i++)
            sb.AppendLine($"[{i + 1}] {toTranslate[i].Text}");

        try
        {
            var ans = await runner.RunAsync(sb.ToString(), runner.NormalizeModel(AiModel), timeout ?? Timeout, ct);
            var map = ParseTranslations(ans);
            for (var i = 0; i < toTranslate.Count; i++)
            {
                var (key, text) = toTranslate[i];
                if (map.TryGetValue((i + 1).ToString(), out var ru) && !string.IsNullOrWhiteSpace(ru))
                {
                    result[key] = ru;
                    _descCache[key] = (text.GetHashCode(), ru);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Перевод описаний навыков не удался — показываю оригинал");
        }
        return result;
    }

    // --- Разбор ---

    internal static IReadOnlyDictionary<string, string> ParseTranslations(string answer)
    {
        var json = ExtractJsonObject(answer);
        if (json is null) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();
            var map = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
            return map;
        }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    private static string? ExtractJsonObject(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
    }

    private static string FirstLine(string s)
    {
        s = s.Trim();
        var nl = s.IndexOfAny(['\n', '\r']);
        var line = (nl >= 0 ? s[..nl] : s).Trim();
        return line.Trim('"', '\'', '`').Trim();
    }
}
