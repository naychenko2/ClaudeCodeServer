using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.AudioEditor.Catalog;

// Каталог звука Higgsfield (ADR-021 §2) — не зашит: строится из живого ответа models_explore
// (action=list, type=audio). Наше здесь только правило разбора: операции — по тегам, виды голоса — по
// параметру voice_type и роли audio_references, языки — по вариантам параметра language, потолок
// текста — из описания. Схема параметров модели остаётся при ней: по ней драйвер пропускает Params
// (белый список имён и значений) и проверяет обязательные до траты.
//
// Модели «Game pipeline only» (решение Андрея) не открываем: они в каталоге серыми с причиной, но не
// подбираются и не запускаются — это fal-модели за игровым конвейером Higgsfield.
public static partial class HiggsfieldAudioCatalog
{
    public const string SeedAudio = "seed_audio";
    public const string Text2SpeechV2 = "text2speech_v2";

    public const string GamePipelineReason = "Только для игрового конвейера Higgsfield — эта модель есть у fal";

    // Известные на 2026-10-01 модели игрового конвейера; новые узнаются по пометке в описании
    public static readonly IReadOnlySet<string> GamePipelineIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sonilo_music", "mirelo_text_to_audio", "inworld_text_to_speech" };

    // Параметры, которые из Params не пропускаются никогда: варианты у нас — отдельные прогоны,
    // а оплату, число и входы задаёт сам драйвер
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "prompt", "count", "get_cost", "use_unlim", "medias", "folder_id", "batch_size",
        };

    public sealed record Param(
        string Name,
        bool Required,
        string Type,
        IReadOnlyList<string> Options,
        double? Min,
        double? Max);

    public sealed record Model(AudioModelInfo Info, IReadOnlyDictionary<string, Param> Params, bool AcceptsAudioReference)
    {
        // Модель с репликами (ElevenLabs v4): текст и голос уходят в dialogue, а не в prompt
        public bool IsDialogue => Params.ContainsKey("dialogue");

        public bool IsGamePipeline => Info.DisabledReason is not null;
    }

    // Разбор ответа models_explore; пункт, из которого не вышло ни одной операции, пропускается
    public static IReadOnlyList<Model> Parse(JsonNode? json)
    {
        var items = json?["items"] as JsonArray ?? json as JsonArray;
        if (items is null) return [];
        var models = new List<Model>();
        foreach (var item in items.OfType<JsonObject>())
            if (ParseItem(item) is { } model)
                models.Add(model);
        return models;
    }

    private static Model? ParseItem(JsonObject item)
    {
        var id = item["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (item["output_type"]?.ToString() is { } output && output != "audio") return null;

        var description = item["description"]?.ToString() ?? "";
        var tags = (item["tags"] as JsonArray)?.Select(t => t?.ToString()?.ToLowerInvariant() ?? "").ToHashSet() ?? [];
        var parameters = ParseParams(item["parameters"] as JsonArray);
        var audioReference = (item["medias"] as JsonArray)?.OfType<JsonObject>()
            .Any(m => (m["roles"] as JsonArray)?.Any(r => r?.ToString() == "audio_references") == true) == true;

        var ops = new List<AudioOp>();
        var speech = tags.Contains("tts") || tags.Contains("text-to-speech") || tags.Contains("speech");
        if (speech) ops.Add(AudioOp.Speak);
        if (speech && parameters.ContainsKey("dialogue")) ops.Add(AudioOp.Dialogue);
        if (speech && audioReference) ops.Add(AudioOp.CloneVoice);
        if (tags.Contains("music") || tags.Contains("text-to-music")) ops.Add(AudioOp.Song);
        if (tags.Contains("sfx") || tags.Contains("sound-effects")) ops.Add(AudioOp.Sfx);
        if (ops.Count == 0) return null;

        var kinds = new List<AudioVoiceKind>();
        var voiceTypes = parameters.TryGetValue("voice_type", out var vt) ? vt.Options : [];
        if (voiceTypes.Contains("preset") || parameters.ContainsKey("voice")) kinds.Add(AudioVoiceKind.Preset);
        if (voiceTypes.Contains("element")) kinds.Add(AudioVoiceKind.Element);
        if (speech && audioReference) kinds.Add(AudioVoiceKind.Clone);

        var languages = parameters.TryGetValue("language", out var lang) ? lang.Options : [];
        var disabled = GamePipelineIds.Contains(id) || description.Contains("game pipeline only", StringComparison.OrdinalIgnoreCase)
            ? GamePipelineReason
            : null;

        var caps = new AudioCaps(ops, languages, kinds, [AudioOutputs.Audio], AudioLicenses.NotStated, AudioPriceUnits.Credits,
            MaxTextChars: MaxChars(description));
        var label = item["name"]?.ToString() is { Length: > 0 } name ? name : id;
        return new Model(new AudioModelInfo(id, label, caps, null, disabled), parameters, audioReference);
    }

    private static IReadOnlyDictionary<string, Param> ParseParams(JsonArray? array)
    {
        var result = new Dictionary<string, Param>(StringComparer.Ordinal);
        foreach (var p in array?.OfType<JsonObject>() ?? [])
        {
            if (p["name"]?.ToString() is not { Length: > 0 } name) continue;
            var options = (p["options"] as JsonArray)?.Select(o => Text(o)).Where(o => o is not null).Select(o => o!).ToList() ?? [];
            result[name] = new Param(name, p["required"]?.ToString() == "required", p["type"]?.ToString() ?? "string",
                options, Number(p["min"]), Number(p["max"]));
        }
        return result;
    }

    // «limited to 10,000 characters» → 10000
    private static int? MaxChars(string description)
    {
        var m = CharsPattern().Match(description);
        return m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    [GeneratedRegex(@"(\d[\d,]*)\s+characters", RegexOptions.IgnoreCase)]
    private static partial Regex CharsPattern();

    // Значение варианта или параметра строкой в инвариантной культуре: 24000, а не «24 000»
    public static string? Text(JsonNode? node) => node switch
    {
        _ when Number(node) is { } d => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? "true" : "false",
        JsonValue v => v.ToString(),
        _ => null,
    };

    // Число любого происхождения: из разобранного JSON (JsonElement) и из кода (JsonValue<int>, <double>)
    public static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
}
