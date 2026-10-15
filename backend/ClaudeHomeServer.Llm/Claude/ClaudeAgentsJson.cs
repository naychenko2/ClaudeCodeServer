using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm;

// Per-run файл --agents (CLI ≥ 2.1.281 принимает путь к JSON при -p): объект, где ключи —
// имена агентов (handle персон), значения — определения. Порядок ключей и полей фиксирован:
// содержимое детерминировано по сессии (владелец + персоны + их настройки), а имя файла —
// хэш содержимого — входит в сигнатуру запуска (смена состава персон → новый путь →
// перезапуск процесса, как при любой смене окружения).
//
// Поля определения — по схеме CLI: description/prompt/tools/model/mcpServers/maxTurns/
// effort/omitClaudeMd. `color` и `experimental` в JSON-форме CLI игнорирует, поэтому
// не пишем. omitClaudeMd — аналог BareMode для сабагента: сессия, где CLAUDE.md проекта
// не грузится целиком (локальные модели), не должна тянуть его и в контекст консультанта.
public static class ClaudeAgentsJson
{
    public static string Serialize(IReadOnlyList<PersonaAgentDef> agents)
    {
        var root = new JsonObject();
        foreach (var a in agents.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var def = new JsonObject
            {
                ["description"] = a.Description,
                ["prompt"] = a.Prompt,
                ["tools"] = new JsonArray(a.Tools.Select(t => (JsonNode)t).ToArray()),
            };
            if (!string.IsNullOrWhiteSpace(a.Model)) def["model"] = a.Model;
            if (a.McpServers is { Count: > 0 } servers)
                def["mcpServers"] = new JsonArray(servers.Select(s => (JsonNode)s).ToArray());
            def["maxTurns"] = a.MaxTurns;
            if (!string.IsNullOrWhiteSpace(a.Effort)) def["effort"] = a.Effort;
            if (a.OmitClaudeMd) def["omitClaudeMd"] = true;
            root[a.Name] = def;
        }
        return root.ToJsonString();
    }
}
