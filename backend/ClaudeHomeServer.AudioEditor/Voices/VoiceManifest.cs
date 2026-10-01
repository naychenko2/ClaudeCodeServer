using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.AudioEditor.Voices;

// Формат voices/<slug>/voice.json (ADR-021 §2, «Голоса»). Голос — папка в проекте, а не запись в
// data/: он переезжает вместе с проектом и виден Claude в чате. Как у персонажей картинок, читатель
// переносит незнакомые ключи как есть — и внутри Providers, и на верхнем уровне (Extra), иначе запись
// старой версией молча сотрёт данные новой.
//
// Kind: samples — образцы и расшифровка (клон у поставщиков создаётся по ним), rvc — обученная модель
// RVC (пара .pth + .index, ссылки на неё — в Providers.rvc). Providers — кеш id у поставщиков: ключ —
// поставщик, значение — его привязка к голосу (VoiceProviders).
public sealed class VoiceManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Kind { get; set; } = VoiceKinds.Samples;
    public List<VoiceSample> Samples { get; set; } = [];
    public string? Transcript { get; set; }
    public DateTime CreatedAt { get; set; }
    public JsonObject Providers { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    // null — файл битый или не голос: такой каталог в списке просто не показывается
    public static VoiceManifest? Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<VoiceManifest>(json, Json);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name)) return null;
            manifest.Samples ??= [];
            manifest.Providers ??= [];
            manifest.Kind ??= VoiceKinds.Samples;
            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class VoiceKinds
{
    public const string Samples = "samples";
    public const string Rvc = "rvc";
}

// File — только имя файла внутри папки голоса, без каталогов
public sealed record VoiceSample(string File);

// Кеш id у поставщиков (ADR-021 §2). Здесь только данные и признаки; создание и пересоздание клона за
// деньги — отдельный шаг и только кнопкой человека с ценой.
//
// - higgsfield: { elementId, createdAt } — элемент воркспейса Higgsfield;
// - minimax: { customVoiceId, createdAt, lastUsedAt } — MiniMax удаляет клон через 7 дней без
//   использования, поэтому по дате последнего использования голос получает признак «проверить»;
// - falQwen: { embeddingUrl, createdAt } — эмбеддинг Qwen-клона fal;
// - rvc: { model, index, trainedAt } — имена .pth и .index в папке голоса.
public static class VoiceProviders
{
    public const string Higgsfield = "higgsfield";
    public const string MiniMax = "minimax";
    public const string FalQwen = "falQwen";
    public const string Rvc = "rvc";

    // Через сколько без использования MiniMax удаляет клон
    public static readonly TimeSpan MiniMaxTtl = TimeSpan.FromDays(7);

    public static void SetHiggsfield(JsonObject providers, string elementId, DateTime now) =>
        providers[Higgsfield] = new JsonObject { ["elementId"] = elementId, ["createdAt"] = now };

    public static void SetMiniMax(JsonObject providers, string customVoiceId, DateTime now) =>
        providers[MiniMax] = new JsonObject { ["customVoiceId"] = customVoiceId, ["createdAt"] = now, ["lastUsedAt"] = now };

    // Клон ушёл в запуск: отсчёт 7 дней начинается заново. false — клона MiniMax у голоса нет
    public static bool TouchMiniMax(JsonObject providers, DateTime now)
    {
        if (providers[MiniMax] is not JsonObject entry || Str(entry, "customVoiceId") is null) return false;
        entry["lastUsedAt"] = now;
        return true;
    }

    public static void SetFalQwen(JsonObject providers, string embeddingUrl, DateTime now) =>
        providers[FalQwen] = new JsonObject { ["embeddingUrl"] = embeddingUrl, ["createdAt"] = now };

    public static void SetRvc(JsonObject providers, string model, string index, DateTime trainedAt) =>
        providers[Rvc] = new JsonObject { ["model"] = model, ["index"] = index, ["trainedAt"] = trainedAt };

    public static (string Model, string Index)? RvcPair(JsonObject providers) =>
        providers[Rvc] is JsonObject entry && Str(entry, "model") is { } model && Str(entry, "index") is { } index
            ? (model, index)
            : null;

    // Состояние кеша у каждого поставщика с клоном: none — не создан, ok — есть, stale — MiniMax мог
    // удалить клон (7 дней без использования): перед запуском его надо проверить или пересоздать
    public static IReadOnlyList<VoiceProviderState> States(JsonObject providers, DateTime now)
    {
        var minimax = providers[MiniMax] as JsonObject;
        var minimaxId = minimax is null ? null : Str(minimax, "customVoiceId");
        var lastUsed = minimax is null ? null : Date(minimax, "lastUsedAt") ?? Date(minimax, "createdAt");
        var minimaxState = minimaxId is null ? VoiceProviderStates.None
            : lastUsed is null || now - lastUsed.Value >= MiniMaxTtl ? VoiceProviderStates.Stale
            : VoiceProviderStates.Ok;

        return
        [
            new(Higgsfield, Has(providers, Higgsfield, "elementId"), Date(providers[Higgsfield], "createdAt"), null),
            new(MiniMax, minimaxState, Date(minimax, "createdAt"), lastUsed),
            new(FalQwen, Has(providers, FalQwen, "embeddingUrl"), Date(providers[FalQwen], "createdAt"), null),
            new(Rvc, RvcPair(providers) is null ? VoiceProviderStates.None : VoiceProviderStates.Ok,
                Date(providers[Rvc], "trainedAt"), null),
        ];
    }

    private static string Has(JsonObject providers, string key, string field) =>
        providers[key] is JsonObject entry && Str(entry, field) is not null ? VoiceProviderStates.Ok : VoiceProviderStates.None;

    private static string? Str(JsonObject entry, string field) =>
        entry[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static DateTime? Date(JsonNode? node, string field) =>
        node is JsonObject entry && entry[field] is JsonValue v && v.TryGetValue<DateTime>(out var d) ? d : null;
}

public static class VoiceProviderStates
{
    public const string None = "none";
    public const string Ok = "ok";
    public const string Stale = "stale";
}

// Id поставщиков наружу не отдаём: это привязки к аккаунту. Фронту — состояние и даты
public sealed record VoiceProviderState(string Provider, string State, DateTime? CreatedAt, DateTime? LastUsedAt);

public sealed record VoiceDto(
    string Slug,
    string Name,
    string Kind,
    string Path,
    IReadOnlyList<VoiceSample> Samples,
    string? Transcript,
    DateTime CreatedAt,
    IReadOnlyList<VoiceProviderState> Providers,
    bool NeedsAttention)
{
    public static VoiceDto From(VoiceManifest m, DateTime now)
    {
        var states = VoiceProviders.States(m.Providers, now);
        return new(m.Slug, m.Name, m.Kind, $"{VoiceStore.Folder}/{m.Slug}", m.Samples, m.Transcript, m.CreatedAt,
            states, states.Any(s => s.State == VoiceProviderStates.Stale));
    }
}

// Образец из запроса: байты как есть, формат определяется по сигнатуре
public sealed record VoiceSampleUpload(byte[] Bytes);

// Итог изменения: голос и относительные пути затронутых файлов (для NotifyMutated)
public sealed record VoiceChange(VoiceManifest Manifest, IReadOnlyList<string> Written, IReadOnlyList<string> Deleted);
