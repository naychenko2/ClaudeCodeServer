using System.Text.Json;

namespace ClaudeHomeServer.Services.Personas;

// Черновик персоны по текстовому промпту (one-shot LLM → строгое JSON-тело). Вынесено из
// PersonasController (ai/quick-create), чтобы переиспользовать в страховке «Применить итоги
// разговора» онбординга (POST /api/onboarding/user/apply-transcript) без дублирования логики.
// Логика перенесена без изменений: те же промпт, парсер и устойчивость к преамбуле/markdown.
public sealed class PersonaDraftService
{
    // Промпт one-shot генерации: по описанию пользователя придумывает ВСЕ поля профиля персоны
    // (роль/имя/характер/приветствие/цвет/аватар-промпт) и требует строго JSON-объект.
    public string BuildDraftPrompt(string userPrompt)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Пользователь описывает ассистента-персону, которую хочет создать. " +
                      "Придумай и верни ВСЕ поля профиля персоны.");
        sb.AppendLine($"\nОписание пользователя: {userPrompt.Trim()}");
        sb.AppendLine("\nВерни ТОЛЬКО JSON-объект (без пояснений и markdown) с полями:");
        sb.AppendLine("  role — роль/профессия по-русски, 1-3 слова (напр. «Дизайнер», «Личный тренер»);");
        sb.AppendLine("  name — русское имя-человека (одно слово, подходит персоне);");
        sb.AppendLine("  description — краткое «кто это», 3-8 слов, по-русски;");
        sb.AppendLine("  character — характер и стиль общения: обращение на «ты» («Ты …»), живо, 2-5 предложений, по-русски;");
        sb.AppendLine("  tone — тон одной короткой фразой по-русски (напр. «тепло и на равных», «сухо и по делу»);");
        sb.AppendLine("  mustDo — массив из 2-4 правил «что делать всегда», по-русски, короткими предложениями;");
        sb.AppendLine("  mustNot — массив из 2-4 правил «чего не делать никогда», по-русски;");
        sb.AppendLine("  outputFormat — требования к формату ответов, 1-2 предложения, по-русски;");
        sb.AppendLine("  speechExamples — массив из 1-2 характерных реплик персоны от её лица, по-русски;");
        sb.AppendLine("  greeting — первое приветственное сообщение персоны пользователю, 1-2 предложения, по-русски, в её характере;");
        sb.AppendLine("  color — один из: yellow, orange, blue, green, purple, red, brown, cyan, pink (подходит образу);");
        sb.AppendLine("  avatarPrompt — описание внешности для фотопортрета, по-английски, 5-15 слов (пол, возраст, стиль, настроение, фон).");
        return sb.ToString();
    }

    // Парсинг черновика из сырого ответа модели (устойчив к преамбуле/markdown-fence).
    public DraftRaw? ParseDraft(string raw) => ParseJsonObject<DraftRaw>(raw);

    // Парс первого сбалансированного JSON-объекта из ответа модели
    // (устойчиво к преамбуле/markdown-fence). Общий для черновика персоны и прочих one-shot
    // контрактов (напр. PersonaContract в ai/character, TeamMemberDraft в ai/team).
    public static T? ParseJsonObject<T>(string raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(raw[start..(i + 1)],
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { return null; }
            }
        }
        return null;
    }
}

// Сырой черновик полей персоны из one-shot ответа модели. Nullable — модель может пропустить
// часть полей; потребитель заполняет дефолты (напр. color → "orange").
public sealed record DraftRaw(string? Role, string? Name, string? Description,
    string? Character, string? Tone, List<string>? MustDo, List<string>? MustNot,
    string? OutputFormat, List<string>? SpeechExamples,
    string? Greeting, string? Color, string? AvatarPrompt);
