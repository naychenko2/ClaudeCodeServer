using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Сгенерированный навык: слаг-имя (латиница, для папки и frontmatter), описание и тело SKILL.md.
public record GeneratedSkill(string Name, string Description, string Body);

// LLM-генерация нового навыка (SKILL.md) по свободному запросу пользователя. В отличие от
// SkillSuggestService (подбор готовых навыков из каталога) — создаёт навык с нуля: модель
// возвращает {name, description, body}, имя нормализуется в безопасный слаг. Модель — Skills:AiModel
// (дефолт haiku), one-shot через общий OneShotClaudeRunner. Сервис только генерирует кандидата,
// сохранение — за вызывающим (POST api/skills → SaveGlobalSkill), чтобы показать превью для правки.
public class SkillGenerationService(
    IOneShotRunner runner,
    IConfiguration config,
    ILogger<SkillGenerationService> log)
{
    private string? AiModel => config["Skills:AiModel"] is { Length: > 0 } m ? m : "haiku";

    private TimeSpan Timeout =>
        TimeSpan.FromMilliseconds(int.TryParse(config["Skills:GenerateTimeoutMs"], out var ms) ? ms
            : int.TryParse(config["Skills:SuggestTimeoutMs"], out var ms2) ? ms2 : 120_000);

    public async Task<GeneratedSkill?> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var full = BuildPrompt(prompt);
        string answer;
        try
        {
            answer = await runner.RunAsync(full, runner.NormalizeModel(AiModel), Timeout, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "LLM-генерация навыка не удалась");
            return null;
        }

        var parsed = Parse(answer);
        if (parsed is null)
            log.LogWarning("generate skill: ответ не распознан; сырой ответ: {Raw}",
                answer.Length > 600 ? answer[..600] + "…" : answer);
        return parsed;
    }

    private static string BuildPrompt(string userPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты составляешь навык (skill) для агента Claude Code по запросу пользователя.");
        sb.AppendLine("Навык — это файл SKILL.md: набор инструкций, расширяющий возможности агента в конкретной области.");
        sb.AppendLine();
        sb.AppendLine("ЗАПРОС ПОЛЬЗОВАТЕЛЯ:");
        sb.AppendLine(userPrompt.Trim());
        sb.AppendLine();
        sb.AppendLine("Верни ТОЛЬКО JSON-объект (без пояснений и markdown-обрамления) с полями:");
        sb.AppendLine("  name — короткое имя-слаг латиницей в kebab-case (только a-z, 0-9 и дефис), напр. \"pdf-table-extract\";");
        sb.AppendLine("  description — одно предложение по-русски, что делает навык и когда применять (пойдёт во frontmatter);");
        sb.AppendLine("  body — тело SKILL.md в markdown БЕЗ frontmatter: понятные пошаговые инструкции агенту, ");
        sb.AppendLine("         как выполнять задачу; допустимы заголовки, списки, примеры кода. По-русски.");
        sb.AppendLine("Пиши содержательно и конкретно под запрос, без воды.");
        return sb.ToString();
    }

    // Парс первого сбалансированного JSON-объекта (устойчиво к преамбуле/markdown-fence),
    // та же логика, что ParseJsonObject у персон. Имя приводится к безопасному слагу.
    internal static GeneratedSkill? Parse(string raw)
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
                GeneratedSkillRaw? draft;
                try
                {
                    draft = JsonSerializer.Deserialize<GeneratedSkillRaw>(raw[start..(i + 1)],
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { return null; }

                if (draft is null || string.IsNullOrWhiteSpace(draft.Body)) return null;
                var name = Slugify(draft.Name);
                return new GeneratedSkill(name, draft.Description?.Trim() ?? "", draft.Body.Trim());
            }
        }
        return null;
    }

    // Приводит имя к безопасному слагу [a-z0-9-] (защита папки + валидное frontmatter name).
    // Кириллица/пробелы/спецсимволы → дефис, дубли дефисов схлопываются, пустой → "skill".
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "skill";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "skill" : slug;
    }

    private sealed record GeneratedSkillRaw(string? Name, string? Description, string? Body);
}
