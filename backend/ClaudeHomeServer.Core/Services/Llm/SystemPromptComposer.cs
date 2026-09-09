using System.Text;

namespace ClaudeHomeServer.Services.Llm;

// Kind: builtin | user | auto
public record SystemPromptPart(string Kind, string Content);

// Сборка эффективного системного промпта проекта из частей. Чистая функция без стора
// и мутаций — по природе спина, в ProjectManager лежала исторически. Вынесена в Core,
// чтобы вертикаль Llm (ClaudeSession) собирала промпт хода, не ссылаясь на ProjectManager.
//
// Встроенная часть промпта (builtInPrompt) сознательно ОСТАЁТСЯ в Main
// (ProjectManager.BuiltInSystemPrompt) и приходит параметром: это контент продукта
// (русскоязычные правила общения, MCP-генераторы медиа, схемы Mermaid), а не общая
// инфраструктура — в спине рядом с SsrfGuard ему не место.
public static class SystemPromptComposer
{
    // Части эффективного системного промпта в порядке отправки:
    // builtin — встроенная константа, user — промпт проекта, auto — автодополнения (Dify, теги).
    // Единственный источник состава промпта: и реальная отправка (ход ClaudeSession),
    // и просмотр на UI (/effective-prompt) собираются отсюда.
    public static List<SystemPromptPart> GetSystemPromptParts(string builtInPrompt, string? userPrompt,
        bool hasDify, Dictionary<string, List<string>>? documentTags = null)
    {
        var parts = new List<SystemPromptPart> { new("builtin", builtInPrompt) };

        if (!string.IsNullOrWhiteSpace(userPrompt))
            parts.Add(new("user", userPrompt));

        if (hasDify)
        {
            var combined = string.Join("\n\n", parts.Select(p => p.Content));
            if (!combined.Contains("mcp__dify__search_knowledge"))
                parts.Add(new("auto",
                    "В этом проекте настроена база знаний Dify. Используй инструмент mcp__dify__search_knowledge для поиска по ней при ответе на вопросы о документации проекта. dataset_id уже настроен — указывать его не нужно.\n\n" +
                    "Если пользователь просит найти, поискать или проверить информацию — используй MCP-сервер Dify (search_knowledge) в первую очередь, до ответа из памяти."));

            var tagInstruction = BuildTagInstruction(documentTags);
            if (!string.IsNullOrEmpty(tagInstruction))
                parts.Add(new("auto", tagInstruction));
        }

        return parts;
    }

    public static string BuildSystemPrompt(string builtInPrompt, string? userPrompt, bool hasDify,
        Dictionary<string, List<string>>? documentTags = null) =>
        string.Join("\n\n",
            GetSystemPromptParts(builtInPrompt, userPrompt, hasDify, documentTags).Select(p => p.Content));

    private static string BuildTagInstruction(Dictionary<string, List<string>>? documentTags)
    {
        if (documentTags is null || documentTags.Count == 0) return "";

        // Инвертируем: tag → список путей
        var byTag = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, tags) in documentTags)
            foreach (var tag in tags)
            {
                if (!byTag.TryGetValue(tag, out var list))
                    byTag[tag] = list = [];
                list.Add(path);
            }

        if (byTag.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("Теги документов в базе знаний:");
        foreach (var (tag, paths) in byTag.OrderBy(x => x.Key))
            sb.AppendLine($"  тег \"{tag}\": {string.Join(", ", paths)}");
        sb.Append("Если пользователь просит искать по тегу, вызови mcp__dify__search_knowledge, " +
                  "затем оставь только результаты, где segment.document.name входит в список выше для нужного тега.");
        return sb.ToString();
    }
}
