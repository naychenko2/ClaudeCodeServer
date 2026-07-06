using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

// История wire-формата messages[] DeepSeek-сессии. API stateless — контекст живёт здесь.
// Persist: data/sessions/{engineSessionId}/deepseek-messages.json — рядом с history.json
// UI-истории (её как и раньше пишет TurnAccumulator). ВАЖНО: reasoning_content в assistant-
// сообщения НЕ сохраняется — API вернёт 400, если отдать его обратно.
public sealed class DeepSeekConversationStore(string sessionsBasePath)
{
    public JsonArray Messages { get; private set; } = [];

    private string? _engineSessionId;

    private string PathFor(string engineId) =>
        System.IO.Path.Combine(sessionsBasePath, engineId, "deepseek-messages.json");

    // Привязка к engine id + загрузка с диска (после рестарта сервера). Повторные вызовы — no-op.
    public void Bind(string engineSessionId)
    {
        if (_engineSessionId == engineSessionId) return;
        _engineSessionId = engineSessionId;
        try
        {
            var path = PathFor(engineSessionId);
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray arr)
                Messages = arr;
        }
        catch (Exception ex)
        {
            // История не загрузилась — продолжаем с чистого листа, но сообщаем
            Console.Error.WriteLine($"[DeepSeekConversationStore] Не удалось загрузить историю {engineSessionId}: {ex.Message}");
        }
    }

    public void Append(JsonNode message) => Messages.Add(message);

    public async Task SaveAsync()
    {
        if (_engineSessionId is null) return;
        try
        {
            var path = PathFor(_engineSessionId);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, Messages.ToJsonString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DeepSeekConversationStore] Не удалось сохранить историю {_engineSessionId}: {ex.Message}");
        }
    }

    // Оценка токенов: для кириллицы ~2 символа на токен — считаем консервативно (больше токенов)
    internal static int EstimateTokens(string text) => text.Length / 2 + 1;

    // Обрезка старых сообщений под бюджет контекста. Удаляем целыми «связками»
    // (assistant с tool_calls + его tool-ответы), после system-сообщения; на месте
    // удалённого — одна заглушка. Последнее user-сообщение не трогаем.
    public void TrimToFit(int contextWindow, int maxTokens)
    {
        var budget = contextWindow - maxTokens;
        budget -= budget / 20; // запас 5%
        if (budget <= 0) return;

        var trimmed = false;
        // Индекс первого удаляемого: после system (и уже вставленной заглушки)
        while (EstimateTotal() > budget)
        {
            var start = 0;
            if (Messages.Count > start && RoleOf(Messages[start]) == "system") start++;
            if (Messages.Count > start && IsTrimPlaceholder(Messages[start])) start++;
            // Нечего удалять: остались только system/заглушка и хвост из одного сообщения
            if (start >= Messages.Count - 1) break;

            // Связка: assistant с tool_calls + следующие tool-сообщения удаляются вместе
            var hadToolCalls = Messages[start] is JsonObject o && o["tool_calls"] is JsonArray { Count: > 0 };
            Messages.RemoveAt(start);
            while (hadToolCalls && Messages.Count > start && RoleOf(Messages[start]) == "tool")
                Messages.RemoveAt(start);
            trimmed = true;
        }

        if (trimmed)
        {
            var insertAt = Messages.Count > 0 && RoleOf(Messages[0]) == "system" ? 1 : 0;
            if (!(Messages.Count > insertAt && IsTrimPlaceholder(Messages[insertAt])))
                Messages.Insert(insertAt, new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = TrimPlaceholderText,
                });
        }
    }

    // Замена истории сводкой (/compact): system-промпт сохраняется, остальное — одним user-сообщением
    public void ReplaceWithSummary(string summary)
    {
        var fresh = new JsonArray();
        if (Messages.Count > 0 && RoleOf(Messages[0]) == "system")
            fresh.Add(Messages[0]!.DeepClone());
        fresh.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "[Сводка предыдущего диалога — используй как контекст]\n\n" + summary,
        });
        Messages = fresh;
    }

    // Оценка текущего размера истории в токенах (для pre/post compact-статистики)
    public int EstimateTotalTokens() => EstimateTotal();

    private const string TrimPlaceholderText = "[ранняя часть диалога усечена из-за лимита контекста]";

    private static string? RoleOf(JsonNode? msg) => msg?["role"]?.GetValue<string>();

    private static bool IsTrimPlaceholder(JsonNode? msg) =>
        RoleOf(msg) == "user" && msg?["content"]?.GetValue<string>() == TrimPlaceholderText;

    private int EstimateTotal()
    {
        var total = 0;
        foreach (var m in Messages)
            total += EstimateTokens(m?.ToJsonString() ?? "");
        return total;
    }
}
