using System.Text.Json;

namespace ClaudeHomeServer.Services.Http;

// Причина пустого успешного ответа fal (COMPLETED, а результата нет): результат скрыт фильтром
// (has_nsfw_concepts) или модель ответила текстом вместо файла (Gemini у nano-banana кладёт его в
// description). Общий для модулей картинок, звука и видео: вертикаль не ссылается на вертикаль
public static class FalEmptyResult
{
    private const int MaxTextLength = 300;
    private const int MaxLogLength = 2000;
    private static readonly string[] TextFields = ["description", "detail", "error", "message"];

    // null — причины в ответе нет. Rejected — отказ по содержанию (фильтр, политика модели)
    public static (bool Rejected, string Reason)? Explain(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;

        if (json.TryGetProperty("has_nsfw_concepts", out var nsfw) && nsfw.ValueKind == JsonValueKind.Array
            && nsfw.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.True))
            return (true, "результат скрыт фильтром безопасности");

        var text = TextFields
            .Select(k => json.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (text is null) return null;

        if (text.Length > MaxTextLength) text = text[..MaxTextLength] + "…";
        var lower = text.ToLowerInvariant();
        var rejected = lower.Contains("nsfw") || lower.Contains("content policy") || lower.Contains("moderation")
            || lower.Contains("safety");
        return (rejected, $"модель ответила: «{text}»");
    }

    // Тело пустого ответа для лога: по одной фразе человеку причину не всегда восстановить
    public static string LogBody(string body) => body.Length > MaxLogLength ? body[..MaxLogLength] : body;

    public static string LogBody(JsonElement json) =>
        json.ValueKind == JsonValueKind.Undefined ? "" : LogBody(json.GetRawText());

    public static (bool Rejected, string Reason)? Explain(string body)
    {
        try { return Explain(JsonDocument.Parse(body).RootElement); }
        catch (JsonException) { return null; }
    }
}
