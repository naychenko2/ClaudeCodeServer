using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Сколько блоков/сообщений вычищено — для лога шлюза.
public readonly record struct LateToolScrubStats(int RemovedBlocks, int RemovedMessages);

// Чистка тела запроса Messages API от блоков фичи CLI «late tool additions» (beta
// mid-conversation-tool-changes): при resume-сессии, чей toolset отстал от текущего
// (после выкатки новых MCP-инструментов), CLI инжектит в messages role:system-сообщение с
// блоками tool_addition/tool_removal — block-типы, которые сторонние Anthropic-совместимые
// API (Kimi K3.1: дефект 21.11.2026) не понимают и отвечают generic 400 «Invalid request»
// без маркеров, по которым встроенный фолбэк CLI (объявление через ToolSearch или inline
// tools) смог бы откатиться. Для таких провайдеров (флаг ScrubLateToolBlocks) шлюз вычищает
// эти блоки из messages; role:system-сообщение, оставшееся пустым, удаляется целиком;
// не-system сообщение, оставшееся пустым, получает text-заглушку (сохраняет чередование ролей).
//
// Чистятся ровно два доказанных block-типа — tool_addition и tool_removal (тот же
// mid-conversation-tool-changes beta): это не слепая чистка «всех новых типов», а удаление
// того, чей 400 подтверждён A/B на том же эндпоинте, куда продукт уже отправлял запрос
// (system-сообщение с этими блоками — 400 «Invalid request Error», без них — 200).
// Прочие block-типы (text, thinking, tool_use/tool_result) сохраняются. Сбой разбора —
// всегда исходные байты: фильтр не имеет права сломать запрос хуже провайдера.
public static class LateToolBlockFilter
{
    // Block-типы фичи mid-conversation-tool-changes. CLI их кладёт в role:system-сообщение
    // (вместе с обычным text «# Environment»), поэтому чистка идёт по всем messages.
    private static readonly HashSet<string> LateToolBlockTypes = new(StringComparer.Ordinal)
    {
        "tool_addition", "tool_removal",
    };

    public static byte[] Scrub(byte[] body, out LateToolScrubStats stats)
    {
        stats = default;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root) return body;
            if (root["messages"] is not JsonArray messages) return body;
            var removedBlocks = 0;
            var removedMessages = 0;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is not JsonObject msg) continue;
                if (msg["content"] is not JsonArray content) continue;
                var kept = new JsonArray();
                foreach (var node in content)
                {
                    if (node is JsonObject b && b["type"] is JsonValue t
                        && t.TryGetValue<string>(out var type)
                        && LateToolBlockTypes.Contains(type))
                        continue;
                    kept.Add(node?.DeepClone());
                }
                var removedInMessage = content.Count - kept.Count;
                if (removedInMessage == 0) continue;
                removedBlocks += removedInMessage;
                if (kept.Count == 0)
                {
                    if (msg["role"] is JsonValue r && r.TryGetValue<string>(out var role)
                        && string.Equals(role, "system", StringComparison.Ordinal))
                    {
                        messages.RemoveAt(i);
                        removedMessages++;
                    }
                    else
                    {
                        kept.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "(объявление новых инструментов вычищено шлюзом)",
                        });
                        msg["content"] = kept;
                    }
                }
                else
                    msg["content"] = kept;
            }
            stats = new LateToolScrubStats(removedBlocks, removedMessages);
            if (removedBlocks == 0 && removedMessages == 0) return body;
            return JsonSerializer.SerializeToUtf8Bytes(root);
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
