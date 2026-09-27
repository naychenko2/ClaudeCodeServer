using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.ImageEditor.Mcp;

/// <summary>
/// Схемы инструментов сервера image-editor (ADR-019 §4): нити картинок чата проекта, выбор
/// картинки агентом, генерация в нить. Вызовы — ImageEditorToolset.cs. Состав фиксирован и не
/// зависит от хода, фокуса и нитей: сервер либо есть в чате проекта целиком, либо его нет.
/// Инструментов «взять вариант», «откатиться» и «сохранить» здесь нет и быть не должно — это
/// решения человека (решение Андрея 1).
/// </summary>
public sealed partial class ImageEditorToolset
{
    public const string ServerName = McpEndpoints.ImageEditorName;

    public const string ToolState = "image_state";
    public const string ToolFocus = "image_focus";
    public const string ToolNew = "image_new";
    public const string ToolGenerate = "image_generate";
    public const string ToolSuggestPrompt = "image_suggest_prompt";
    public const string ToolCancel = "image_cancel";

    // Значения в тех же строках, что уходят по REST (enum'ы camelCase)
    private static readonly string[] Modes = ["auto", "fast", "precise", "photoreal"];
    private static readonly string[] Ops = ["generate", "edit", "inpaint", "outpaint", "removeBackground", "upscale", "enhanceFaces"];
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
            "Картинки этого чата: какая в работе (focus), у каждой threadId, файл и размер (или черновик "
            + "с папкой), шаг на холсте, настройки запуска, задача, чьи варианты ждут выбора человека; "
            + "поставщики и модели с возможностями. Ничего не меняет.",
            Obj(new JsonObject())),

        new(ToolFocus,
            "Берёт картинку в работу — человек увидит в ленте строку «Claude взял в работу: …» и "
            + "полосу «Картинки» над полем ввода. threadId — картинка этого чата из image_state; file — "
            + "путь картинки проекта (нить по нему заведётся или найдётся); без обоих — снять выбор. "
            + "Денег не тратит.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Картинка этого чата (threadId из image_state)"),
                ["file"] = Str("Путь картинки от корня проекта, если в чате её ещё нет"),
            })),

        new(ToolNew,
            "Заводит карточку «Новая картинка» (черновик без файла) и берёт её в работу — строкой в "
            + "ленте. Нарисовать её — image_generate с threadId черновика; сохранит в проект человек.",
            Obj(new JsonObject
            {
                ["folder"] = Str("Папка проекта, куда человек сохранит картинку; пусто — корень"),
            })),

        new(ToolGenerate,
            "Сразу запускает генерацию в картинку threadId — без вопроса человеку, это тратит деньги. "
            + "threadId обязателен: человек мог сменить картинку посреди хода. У черновика без файла "
            + "рисует новую по тексту. Что не передано — из настроек картинки. Не больше двух запусков "
            + "за ход. Возвращает { jobId, threadId, quote }. Варианты появятся в карточке картинки: "
            + "взять вариант, откатиться и сохранить в проект может только человек.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Картинка этого чата (threadId из image_state)"),
                ["prompt"] = Str("Промпт генерации"),
                ["provider"] = Str("Поставщик (ключ из image_state): fal, higgsfield или local — «Локальные модели» "
                    + "на своей видеокарте, бесплатно, но с очередью"),
                ["model"] = Str("Модель поставщика из image_state или auto"),
                ["mode"] = OneOf(Modes, "Режим при модели auto"),
                ["count"] = Count(),
                ["references"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Образцы: пути файлов проекта с ролями",
                    ["items"] = Obj(new JsonObject
                    {
                        ["path"] = Str("Путь от корня проекта"),
                        ["role"] = OneOf(Roles, "Роль образца"),
                    }, "path", "role"),
                },
                ["character"] = Str("Персонаж проекта (slug папки characters/)"),
                ["op"] = OneOf(Ops, "Операция: правка, фон, апскейл, дорисовка за края, улучшить лица (enhanceFaces, "
                    + "только local); по умолчанию — правка, а у новой картинки без файла — generate"),
                ["matchSourceSize"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Вернуть вариантам размер исходника, если пропорции совпадают",
                },
            }, "threadId")),

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
            "Отменяет задачу генерации этого чата.",
            Obj(new JsonObject
            {
                ["jobId"] = Str("jobId из результата image_generate"),
            }, "jobId")),
    ];
}
