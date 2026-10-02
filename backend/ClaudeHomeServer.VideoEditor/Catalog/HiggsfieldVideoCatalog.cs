using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.VideoEditor.Catalog;

// Каталог видео Higgsfield (ADR-022 §3) — не зашит: строится из живого ответа models_explore
// (action=list, type=video). Наше здесь только правило отбора по ролям medias: start_image и end_image —
// «кадр A → кадр B» (LastFrame), только start_image — кадр A, без start_image — только текст (FirstFrame=false).
// Длительности — из durations, duration_range или параметра duration (варианты либо min/max), пропорции —
// из aspect_ratios, звук — параметры generate_audio/sound. Схема параметров остаётся при модели: по ней
// драйвер пропускает Params (белый список имён и значений).
//
// Не берём: модели обработки готового ролика (входы input_video, video и прочие роли вне генерации),
// модели с обязательным параметром, который мы не заполняем (пресеты, маркетинговые типы), и модели без
// объявленных длительностей — их длину мы бы выдумали. Форма ответа зафиксирована фикстурой тестов
// (Fixtures/higgsfield-models-explore-video.json, 2026-10-02)
public static class HiggsfieldVideoCatalog
{
    public const string StartImage = "start_image";
    public const string EndImage = "end_image";

    // Роли медиа, с которыми модель годится для генерации сцены; всё прочее — обработка готового
    public static readonly IReadOnlySet<string> GenerationRoles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            StartImage, EndImage, "image", "image_references", "video_references", "audio_references",
        };

    // Параметры, которые из Params не пропускаются никогда: длительность, пропорции, звук, сид и входы
    // задаёт драйвер из общих полей запроса, варианты — отдельные прогоны, оплата — всегда кредитами
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "prompt", "count", "get_cost", "use_unlim", "medias", "folder_id", "batch_size",
            "duration", "aspect_ratio", "width", "height", "generate_audio", "sound", "seed",
        };

    public sealed record Param(
        string Name,
        bool Required,
        string Type,
        IReadOnlyList<string> Options,
        double? Min,
        double? Max);

    // SoundParam — имя параметра звука (generate_audio | sound) или null; SoundAsSwitch — звук строкой «on»/«off»
    public sealed record Model(VideoModelInfo Info, IReadOnlyDictionary<string, Param> Params, string? SoundParam,
        bool SoundAsSwitch);

    public static IReadOnlyList<Model> Parse(JsonNode? json)
    {
        var items = json?["items"] as JsonArray ?? json as JsonArray;
        if (items is null) return [];
        var models = new List<Model>();
        foreach (var item in items.OfType<JsonObject>())
            if (ParseItem(item) is { } model)
                models.Add(model);

        // Одинаковые имена у разных моделей (Cinema Studio Video и его v2) различаем по id
        var taken = models.GroupBy(m => m.Info.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. models.Select(m => taken.Contains(m.Info.Label)
            ? m with { Info = m.Info with { Label = $"{m.Info.Label} ({m.Info.Id})" } }
            : m)];
    }

    private static Model? ParseItem(JsonObject item)
    {
        var id = item["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (item["output_type"]?.ToString() is { } output && output != "video") return null;

        var roles = (item["medias"] as JsonArray)?.OfType<JsonObject>()
            .SelectMany(m => (m["roles"] as JsonArray)?.Select(r => r?.ToString() ?? "") ?? [])
            .Where(r => r.Length > 0).ToHashSet(StringComparer.Ordinal) ?? [];
        if (roles.Any(r => !GenerationRoles.Contains(r))) return null;

        var parameters = ParseParams(item["parameters"] as JsonArray);
        if (parameters.Values.Any(p => p.Required && !Reserved.Contains(p.Name))) return null;

        var durations = Durations(item, parameters);
        if (durations.Count == 0) return null;

        var aspects = (item["aspect_ratios"] as JsonArray)?.Select(a => a?.ToString() ?? "")
            .Where(a => a.Length > 0).ToList() ?? [];

        var soundParam = parameters.ContainsKey("generate_audio") ? "generate_audio"
            : parameters.ContainsKey("sound") ? "sound"
            : null;
        var soundAsSwitch = soundParam is not null && parameters[soundParam].Options.Contains("on");

        var first = roles.Contains(StartImage);
        var last = first && roles.Contains(EndImage);
        var caps = new VideoCaps(durations, aspects, soundParam is not null, last, VideoLicense.Commercial,
            VideoPriceUnits.Credits, FirstFrame: first);
        var label = item["name"]?.ToString() is { Length: > 0 } name ? name : id;
        return new Model(new VideoModelInfo(id, label, caps), parameters, soundParam, soundAsSwitch);
    }

    // Целые секунды: явный список, диапазон модели или параметр duration. Отрицательное и нулевое
    // («длину выберет модель») в список не идёт
    private static IReadOnlyList<int> Durations(JsonObject item, IReadOnlyDictionary<string, Param> parameters)
    {
        if (item["durations"] is JsonArray list)
            return Whole(list.Select(Number));
        if (item["duration_range"] is JsonObject range && Number(range["min"]) is { } lo && Number(range["max"]) is { } hi)
            return Span(lo, hi);
        if (parameters.TryGetValue("duration", out var p))
        {
            if (p.Options.Count > 0)
                return Whole(p.Options.Select(o => double.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null));
            if (p.Min is { } min && p.Max is { } max)
                return Span(min, max);
        }
        return [];
    }

    private static List<int> Whole(IEnumerable<double?> values) =>
        [.. values.Where(v => v is > 0 && v == Math.Floor(v.Value)).Select(v => (int)v!.Value).Distinct().Order()];

    private static List<int> Span(double min, double max)
    {
        var from = Math.Max(1, (int)Math.Ceiling(min));
        var to = (int)Math.Floor(max);
        return to < from ? [] : [.. Enumerable.Range(from, to - from + 1)];
    }

    private static IReadOnlyDictionary<string, Param> ParseParams(JsonArray? array)
    {
        var result = new Dictionary<string, Param>(StringComparer.Ordinal);
        foreach (var p in array?.OfType<JsonObject>() ?? [])
        {
            if (p["name"]?.ToString() is not { Length: > 0 } name) continue;
            var options = (p["options"] as JsonArray)?.Select(Text).Where(o => o is not null).Select(o => o!).ToList() ?? [];
            result[name] = new Param(name, p["required"]?.ToString() == "required", p["type"]?.ToString() ?? "string",
                options, Number(p["min"]), Number(p["max"]));
        }
        return result;
    }

    // Значение строкой в инвариантной культуре: 10, а не «10,0»
    public static string? Text(JsonNode? node) => node switch
    {
        _ when Number(node) is { } d => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? "true" : "false",
        JsonValue v => v.ToString(),
        _ => null,
    };

    public static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
}
