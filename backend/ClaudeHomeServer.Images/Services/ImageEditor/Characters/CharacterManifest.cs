using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.Editing;

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

// File — только имя файла внутри папки персонажа, без каталогов
public sealed record CharacterPhoto(string File, bool Primary = false, string? Angle = null);

// То, что видит фронт. Providers наружу не отдаём: там привязки к аккаунту поставщика.
public sealed record CharacterDto(
    string Slug,
    string Name,
    string? Description,
    string Path,
    IReadOnlyList<CharacterPhoto> Photos,
    DateTime CreatedAt)
{
    public static CharacterDto From(CharacterManifest m) =>
        new(m.Slug, m.Name, m.Description, $"{CharacterStore.Folder}/{m.Slug}", m.Photos, m.CreatedAt);
}

// Фото из запроса: байты как загружены, без перекодирования
public sealed record CharacterPhotoUpload(byte[] Bytes, string? Angle = null);

public sealed record CharacterDraft(string Name, string? Description, IReadOnlyList<CharacterPhotoUpload> Photos);

// null в поле — не менять. RemovePhotos — имена файлов из манифеста.
public sealed record CharacterPatch(
    string? Name,
    string? Description,
    IReadOnlyList<CharacterPhotoUpload> AddPhotos,
    IReadOnlyList<string> RemovePhotos,
    string? PrimaryPhoto);

// Персонаж для запроса генерации: ссылка в ImageEditRequest.Character и фото образцами
public sealed record CharacterForRequest(
    CharacterRef Ref,
    IReadOnlyList<ReferenceImage> Photos);

// Итог изменения: персонаж и относительные пути затронутых файлов (для NotifyMutated)
public sealed record CharacterChange(CharacterManifest Manifest, IReadOnlyList<string> Written, IReadOnlyList<string> Deleted);
