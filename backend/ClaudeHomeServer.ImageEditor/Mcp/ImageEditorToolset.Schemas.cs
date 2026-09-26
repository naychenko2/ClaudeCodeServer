using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.ImageEditor.Mcp;

/// <summary>
/// Схемы инструментов сервера image-editor (ADR-018 §2): чат картинки, генерация агентом.
/// Вызовы — ImageEditorToolset.cs. Состав фиксирован и не зависит от хода: сервер либо есть
/// в чате картинки целиком, либо его нет.
/// </summary>
public sealed partial class ImageEditorToolset
{
    public const string ServerName = McpEndpoints.ImageEditorName;

    public const string ToolState = "image_state";
    public const string ToolGenerate = "image_generate";
    public const string ToolSuggestPrompt = "image_suggest_prompt";
    public const string ToolCancel = "image_cancel";

    // Значения в тех же строках, что уходят по REST (enum'ы camelCase)
    private static readonly string[] Modes = ["auto", "fast", "precise", "photoreal"];
    private static readonly string[] Ops = ["generate", "edit", "inpaint", "outpaint", "removeBackground", "upscale"];
    private static readonly string[] Roles = ["character", "style", "object"];

    private static JsonArray StrEnum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject OneOf(string[] values, string description) =>
        new() { ["type"] = "string", ["enum"] = StrEnum(values), ["description"] = description };

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (required.Length > 0) schema["required"] = StrEnum(required);
        schema["properties"] = properties;
        return schema;
    }

    private static JsonObject Count() => new()
    {
        ["type"] = "integer",
        ["minimum"] = 1,
        ["maximum"] = 4,
        ["description"] = "Сколько вариантов (1–4)",
    };

    internal static readonly IReadOnlyList<McpToolSchema> Schemas =
    [
        new(ToolState,
            "Текущее состояние редактора этой картинки: файл и его размеры, промпт, поставщик и модель, "
            + "число вариантов, образцы с ролями, персонаж, пометки словами, путь последнего снимка, "
            + "последние задачи и их итог, доступные модели с возможностями. Ничего не меняет.",
            Obj(new JsonObject())),

        new(ToolGenerate,
            "Сразу запускает генерацию по картинке этого чата — без вопроса человеку, это тратит деньги. "
            + "Все параметры необязательные: что не передано, берётся из состояния редактора; "
            + "переданное меняет состояние (человек увидит, что поменял ты). Не больше двух запусков за ход. "
            + "Возвращает { jobId, quote, changes[] }. Сохранить результат в проект может только человек.",
            Obj(new JsonObject
            {
                ["prompt"] = Str("Промпт генерации"),
                ["provider"] = Str("Поставщик (ключ из image_state), например fal или higgsfield"),
                ["model"] = Str("Модель поставщика из image_state или auto"),
                ["mode"] = OneOf(Modes, "Режим при модели auto"),
                ["count"] = Count(),
                ["references"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Образцы: пути файлов проекта с ролями; заменяют образцы редактора",
                    ["items"] = Obj(new JsonObject
                    {
                        ["path"] = Str("Путь от корня проекта"),
                        ["role"] = OneOf(Roles, "Роль образца"),
                    }, "path", "role"),
                },
                ["op"] = OneOf(Ops, "Операция: правка, фон, апскейл, дорисовка за края; по умолчанию — из режима редактора"),
                ["matchSourceSize"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Вернуть вариантам размер исходника, если пропорции совпадают",
                },
            })),

        new(ToolSuggestPrompt,
            "Только предлагает промпт: ничего не запускает и не меняет. Человек увидит карточку "
            + "с кнопками «Вставить в промпт» и «Сгенерировать».",
            Obj(new JsonObject
            {
                ["prompt"] = Str("Предлагаемый промпт"),
                ["count"] = Count(),
                ["model"] = Str("Модель, которую советуешь"),
            }, "prompt")),

        new(ToolCancel,
            "Отменяет задачу генерации этого чата. Настройки, которые ты поменял, при отмене не откатываются.",
            Obj(new JsonObject
            {
                ["jobId"] = Str("jobId из результата image_generate"),
            }, "jobId")),
    ];
}
