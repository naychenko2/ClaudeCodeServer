using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.AudioEditor;

// Схема частных параметров модели (ADR-021 §2, «Дополнительно»): одна форма для всех поставщиков, чтобы
// автоформа на фронте и проверка params на бэкенде работали одинаково. У fal её строят по OpenAPI
// эндпоинта, у local — по описанию движка в каталоге. Fields — то, что можно передать в params;
// Reserved — поля, которыми владеют общие поля запроса (текст, голос, длительность, входы) или каталог:
// в params их не дублируют. Stale — схема из кеша, обновить её сейчас не удалось
public sealed record AudioParamSchema(
    string Provider,
    string Model,
    string Source,
    IReadOnlyList<AudioParamField> Fields,
    IReadOnlyList<string> Reserved,
    bool Stale = false);

// Поле автоформы. Type — AudioParamTypes.*; Enum — допустимые значения (строки или числа); Min/Max —
// границы числа; MaxLength — строки; Items — тип элемента массива; Fields — поля вложенного объекта
// (null — любые ключи). Passed=false — параметр движок знает, но шов поставщика его пока не принимает:
// форма показывает его серым с NotPassed, запуск с ним отказывает, а не теряет молча
public sealed record AudioParamField(
    string Key,
    string Type,
    string? Title = null,
    string? Description = null,
    JsonNode? Default = null,
    double? Min = null,
    double? Max = null,
    int? MaxLength = null,
    IReadOnlyList<JsonNode>? Enum = null,
    AudioParamField? Items = null,
    IReadOnlyList<AudioParamField>? Fields = null,
    bool Nullable = false,
    string? Format = null,
    bool Passed = true,
    string? NotPassed = null);

public static class AudioParamTypes
{
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string String = "string";
    public const string Array = "array";
    public const string Object = "object";
    // Тип не сводится к одному (anyOf из разных типов): значение не проверяем
    public const string Any = "any";
}

public static class AudioSchemaSources
{
    public const string FalOpenApi = "fal-openapi";
    public const string LocalCatalog = "local-catalog";
}

// Схема или причина, по которой её нет (отказ значением, а не исключение)
public sealed record AudioSchemaLookup(AudioParamSchema? Schema, string? Error)
{
    public static AudioSchemaLookup Ok(AudioParamSchema schema) => new(schema, null);
    public static AudioSchemaLookup Fail(string error) => new(null, error);
}

// Драйвер, который знает схему частных параметров своих моделей. Без этого шва params модели не
// проверяются: такого поставщика пока нет, Higgsfield заведёт его на этапе 4
public interface IAudioParamSchemas
{
    Task<AudioSchemaLookup> SchemaAsync(AudioModelInfo model, AudioOp op, CancellationToken ct);
}
