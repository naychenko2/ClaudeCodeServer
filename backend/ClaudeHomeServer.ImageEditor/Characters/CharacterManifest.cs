using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Формат characters/<slug>/character.json (ADR-017, раздел 10). Персонаж — папка в проекте,
// а не запись в data/: он переносится вместе с проектом и виден Claude в чате.
//
// Providers — задел под Soul ID без переделки формата: ключ — поставщик, значение — его
// привязка к персонажу. Новый персонаж получает пустую заготовку providers.higgsfield.
// Читатель обязан переносить незнакомые ключи как есть — и внутри providers, и на
// верхнем уровне (Extra), иначе запись волны 1 молча сотрёт данные волны 2.
public sealed class CharacterManifest
{
    public const int CurrentFormatVersion = 1;
    public const string HiggsfieldProvider = "higgsfield";
    public const string LocalProvider = "local";
    public const double DefaultLoraStrength = 0.8;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public List<CharacterPhoto> Photos { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public JsonObject Providers { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    // Заготовка Higgsfield: soulId и статус обучения появятся в волне 2, photosHash покажет,
    // что набор фото поменялся после обучения
    public static JsonObject EmptyHiggsfield() => new()
    {
        ["soulId"] = null,
        ["status"] = null,
        ["trainedAt"] = null,
        ["photosHash"] = null,
    };

    // LoRA персонажа из providers.local; null — блока нет или не хватает lora/trigger
    public CharacterLora? ReadLora()
    {
        if (Providers[LocalProvider] is not JsonObject local) return null;
        var name = StringOf(local["lora"]);
        var trigger = StringOf(local["trigger"]);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(trigger)) return null;
        var strength = DefaultLoraStrength;
        if (local["strength"] is JsonValue v && v.TryGetValue<double>(out var parsed)) strength = parsed;
        return new CharacterLora(name, trigger, strength);
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    // null — файл битый или не персонаж: такой каталог в списке просто не показывается
    public static CharacterManifest? Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<CharacterManifest>(json, Json);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name)) return null;
            manifest.Photos ??= [];
            manifest.Providers ??= [];
            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// LoRA персонажа для локальной генерации; имя сверяет с ComfyUI адаптер, не сервер редактора
public sealed record CharacterLora(string Name, string Trigger, double Strength);

// File — только имя файла внутри папки персонажа, без каталогов
// Метки (ADR-027, §1): null — разметка не пробовалась, "unknown" — пробовали, ответа нет.
// LabelSource — только human (ручная правка: фото не трогаем целиком). Auto/NoRoute — по месту
// разметки, а не на фото: AngleSource (ракурс+наклон) и EmotionSource (эмоция) независимы,
// общий источник их путал (находка Александра, дефекты а/б).
// Extra переносит незнакомые поля фото как есть, чтобы откат кода не стёр их при правке.
public sealed record CharacterPhoto(
    string File, bool Primary = false,
    string? Angle = null, string? Pitch = null, string? Emotion = null,
    string? LabelSource = null, string? AngleSource = null, string? EmotionSource = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

// Словари меток фото и источник метки. Auto/NoRoute — значения источника МЕСТА разметки
// (AngleSource/EmotionSource), а не фото; Human — источник фото (LabelSource).
public static class PhotoLabelValues
{
    public const string Unknown = "unknown";
    public const string Auto = "auto";
    public const string Human = "human";
    // Разметить было нечем (маршрута нет или ComfyUI лежит): повторим, когда станет чем.
    // Отличается от Auto+unknown («модель ответила, но не определила» — повторно не размечаем)
    public const string NoRoute = "noroute";

    public static readonly IReadOnlySet<string> Angles =
        new HashSet<string> { "front", "three-quarter", "profile", Unknown };
    public static readonly IReadOnlySet<string> Pitches =
        new HashSet<string> { "level", "up", "down", Unknown };
    public static readonly IReadOnlySet<string> Emotions =
        new HashSet<string> { "neutral", "smile", "laugh", "serious", "sad", "surprised", Unknown };
}

// Метки одного фото; null в поле — не менять
public sealed record CharacterPhotoLabels(string? Angle = null, string? Pitch = null, string? Emotion = null);

// То, что видит фронт. Providers наружу не отдаём: там привязки к аккаунту поставщика.
public sealed record CharacterDto(
    string Slug,
    string Name,
    string? Description,
    string Path,
    IReadOnlyList<CharacterPhotoDto> Photos,
    DateTime CreatedAt,
    CharacterLocalDto? Local = null,
    // Сколько фото ложится в один запрос: окно выбора фронта берёт предел отсюда. Только при включённом
    // флаге character-smart-photos, иначе поля в JSON нет
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PhotosPerRequest = null)
{
    // withLabels (ADR-027 §10) — флаг character-smart-photos у владельца: без него фото отдаются как до фичи
    // (file, primary, angle), без pitch/emotion/labelSource
    public static CharacterDto From(CharacterManifest m, bool withLabels = false) =>
        new(m.Slug, m.Name, m.Description, $"{CharacterStore.Folder}/{m.Slug}",
            [.. m.Photos.Select(p => CharacterPhotoDto.From(p, withLabels))], m.CreatedAt,
            m.ReadLora() is { } lora ? new CharacterLocalDto(lora.Name, lora.Trigger, lora.Strength) : null,
            withLabels ? CharacterStore.PhotosPerRequest : null);
}

// Фото персонажа для фронта: Extra манифеста наружу не идёт. Pitch/Emotion/LabelSource у фото без метки
// (и при выключенном флаге) в JSON отсутствуют, а не null. LabelSource — единый для фронта:
// human (ручная) → auto (хотя бы одно место авто) → noroute (хотя бы одно без маршрута) → null.
public sealed record CharacterPhotoDto(
    string File,
    bool Primary,
    string? Angle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pitch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Emotion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LabelSource = null)
{
    public static CharacterPhotoDto From(CharacterPhoto p, bool withLabels) =>
        withLabels ? new(p.File, p.Primary, p.Angle, p.Pitch, p.Emotion, DtoLabelSource(p)) : new(p.File, p.Primary, p.Angle);

    // Единый источник для подписи/иконки фронта: ручная правка главнее, затем «авто» (хотя бы
    // одно место размечено моделью), затем «без маршрута», иначе null. Фронт не различает, какое
    // именно место auto/noroute — ему достаточно показать «Авто»/«Без меток»/«Вручную».
    private static string? DtoLabelSource(CharacterPhoto p) =>
        p.LabelSource == PhotoLabelValues.Human ? PhotoLabelValues.Human
        : p.AngleSource == PhotoLabelValues.Auto || p.EmotionSource == PhotoLabelValues.Auto ? PhotoLabelValues.Auto
        : p.AngleSource == PhotoLabelValues.NoRoute || p.EmotionSource == PhotoLabelValues.NoRoute ? PhotoLabelValues.NoRoute
        : null;
}

// LoRA персонажа для фронта: из providers.local наружу уходят только эти три поля
public sealed record CharacterLocalDto(string Lora, string Trigger, double Strength);

// Фото из запроса: байты как загружены, без перекодирования
public sealed record CharacterPhotoUpload(byte[] Bytes, string? Angle = null);

public sealed record CharacterDraft(string Name, string? Description, IReadOnlyList<CharacterPhotoUpload> Photos);

// null в поле — не менять. RemovePhotos — имена файлов из манифеста.
public sealed record CharacterPatch(
    string? Name,
    string? Description,
    IReadOnlyList<CharacterPhotoUpload> AddPhotos,
    IReadOnlyList<string> RemovePhotos,
    string? PrimaryPhoto,
    CharacterLoraPatch? Lora = null,
    IReadOnlyDictionary<string, CharacterPhotoLabels>? PhotoLabels = null);

// Три состояния LoRA в патче: Lora == null у CharacterPatch — не трогать; Remove — убрать;
// иначе задать Name/Trigger/Strength. Known — список LoRA ComfyUI от контроллера
// (null — ComfyUI недоступен, проверить имя нельзя).
public sealed record CharacterLoraPatch(
    bool Remove,
    string? Name = null,
    string? Trigger = null,
    double? Strength = null,
    IReadOnlyList<string>? Known = null)
{
    public static CharacterLoraPatch Removal() => new(true);

    public static CharacterLoraPatch Set(
        string? name, string? trigger, double? strength, IReadOnlyList<string>? known) =>
        new(false, name, trigger, strength, known);
}
// Персонаж для запроса генерации: ссылка в ImageEditRequest.Character и фото образцами
public sealed record CharacterForRequest(
    CharacterRef Ref,
    IReadOnlyList<ReferenceImage> Photos);

// Итог изменения: персонаж и относительные пути затронутых файлов (для NotifyMutated)
public sealed record CharacterChange(CharacterManifest Manifest, IReadOnlyList<string> Written, IReadOnlyList<string> Deleted);
