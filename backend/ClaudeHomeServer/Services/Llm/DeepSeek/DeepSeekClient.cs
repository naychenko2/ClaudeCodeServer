using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

// События SSE-стрима chat completions (OpenAI-совместимый формат DeepSeek)
public abstract record DsStreamEvent;
public sealed record DsReasoningDelta(string Text) : DsStreamEvent;
public sealed record DsContentDelta(string Text) : DsStreamEvent;
// id/name приходят только в первом фрагменте tool_call; дальше — фрагменты arguments по index
public sealed record DsToolCallStart(int Index, string Id, string Name) : DsStreamEvent;
public sealed record DsToolCallArgsDelta(int Index, string Fragment) : DsStreamEvent;
public sealed record DsFinish(string Reason) : DsStreamEvent;
// Usage приходит последним чанком перед [DONE] (stream_options.include_usage)
public sealed record DsUsage(int PromptTokens, int CompletionTokens, int CacheHitTokens) : DsStreamEvent;

public sealed record DsChatRequest(
    string Model,
    JsonArray Messages,
    JsonArray? Tools,
    int MaxTokens,
    // null → параметр thinking не отправляем (совместимость с моделями без него)
    bool? Thinking);

public class DeepSeekApiException(int statusCode, string body)
    : Exception(FriendlyMessage(statusCode, body))
{
    public int StatusCode { get; } = statusCode;

    private static string FriendlyMessage(int status, string body)
    {
        var hint = status switch
        {
            401 => "неверный API-ключ (DeepSeek:ApiKey в appsettings.Local.json)",
            402 => "недостаточно средств на балансе DeepSeek",
            429 => "превышен лимит запросов DeepSeek",
            _ => null,
        };
        var detail = TryExtractError(body);
        return $"DeepSeek API {status}" +
               (hint is null ? "" : $" — {hint}") +
               (detail is null ? "" : $": {detail}");
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                ? msg.GetString()
                : null;
        }
        catch (JsonException) { return body.Length > 300 ? body[..300] : (body.Length > 0 ? body : null); }
    }
}

// Чистый HTTP/SSE-слой DeepSeek API. Таймаутов сам не держит —
// ими управляет per-turn CancellationToken вызывающего (DeepSeekSession).
public class DeepSeekClient(IHttpClientFactory httpFactory, IOptions<DeepSeekOptions> options)
{
    public async IAsyncEnumerable<DsStreamEvent> StreamChatAsync(
        DsChatRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var opts = options.Value;
        var body = new JsonObject
        {
            ["model"] = req.Model,
            // DeepClone: JsonNode не может иметь двух родителей, а messages живёт в истории сессии
            ["messages"] = req.Messages.DeepClone(),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = req.MaxTokens,
        };
        if (req.Tools is { Count: > 0 })
        {
            body["tools"] = req.Tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (req.Thinking is { } thinking)
            body["thinking"] = new JsonObject { ["type"] = thinking ? "enabled" : "disabled" };

        var client = httpFactory.CreateClient("deepseek");
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var httpReq = new HttpRequestMessage(HttpMethod.Post,
            $"{opts.BaseUrl.TrimEnd('/')}/chat/completions");
        httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
        httpReq.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new DeepSeekApiException((int)resp.StatusCode, errBody);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // пустые строки/комментарии SSE
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;
            foreach (var evt in ParseChunk(payload))
                yield return evt;
        }
    }

    // Разбор одного SSE-чанка в события. Выделен статически для юнит-тестов на записанных чанках.
    internal static List<DsStreamEvent> ParseChunk(string json)
    {
        var events = new List<DsStreamEvent>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return events; } // повреждённый чанк не должен ронять весь стрим

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("reasoning_content", out var rc)
                        && rc.ValueKind == JsonValueKind.String && rc.GetString() is { Length: > 0 } reasoning)
                        events.Add(new DsReasoningDelta(reasoning));

                    if (delta.TryGetProperty("content", out var c)
                        && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } content)
                        events.Add(new DsContentDelta(content));

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCalls.EnumerateArray())
                        {
                            var index = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : 0;
                            var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                ? idEl.GetString() : null;
                            string? name = null, args = null;
                            if (tc.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                                    name = n.GetString();
                                if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                    args = a.GetString();
                            }
                            if (id is { Length: > 0 })
                                events.Add(new DsToolCallStart(index, id, name ?? ""));
                            if (args is { Length: > 0 })
                                events.Add(new DsToolCallArgsDelta(index, args));
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr)
                    && fr.ValueKind == JsonValueKind.String && fr.GetString() is { Length: > 0 } reason)
                    events.Add(new DsFinish(reason));
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var prompt = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : 0;
                var completion = usage.TryGetProperty("completion_tokens", out var cmt) && cmt.TryGetInt32(out var cv) ? cv : 0;
                var cacheHit = usage.TryGetProperty("prompt_cache_hit_tokens", out var ch) && ch.TryGetInt32(out var chv) ? chv : 0;
                events.Add(new DsUsage(prompt, completion, cacheHit));
            }
        }
        return events;
    }
}
