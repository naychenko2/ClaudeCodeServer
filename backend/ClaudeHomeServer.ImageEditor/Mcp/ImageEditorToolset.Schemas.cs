using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.ImageEditor.Mcp;

/// <summary>
/// Схемы инструментов сервера image-editor (ADR-019 §4): нити картинок чата, выбор картинки
/// агентом, генерация в нить. Вызовы — ImageEditorToolset.cs. Состав фиксирован и не зависит от
/// хода, фокуса и нитей: сервер либо есть в чате целиком, либо его нет. Схемы одни для чата
/// проекта и личного — проектные аргументы в личном чате отказывают на вызове.
/// Инструмента «сохранить» здесь нет и быть не должно — это решение человека (решение Андрея 1);
/// «взять вариант» не нужен: каждый вариант запуска сразу версия (изменение 27.09).
/// </summary>
public sealed partial class ImageEditorToolset
{
    public const string ServerName = McpEndpoints.ImageEditorName;

    public const string ToolState = "image_state";
    public const string ToolFocus = "image_focus";
    public const string ToolNew = "image_new";
    public const string ToolGenerate = "image_generate";
    // Имя с запасом от image_focus, чтобы LLM их не путала (правило про однокоренные имена)
    public const string ToolFindFaces = "image_find_faces";
    public const string ToolSuggestPrompt = "image_suggest_prompt";
    public const string ToolCancel = "image_cancel";

    // Значения в тех же строках, что уходят по REST (enum'ы camelCase)
    private static readonly string[] Modes = ["auto", "fast", "precise", "photoreal"];
    // Операции, берущие образцы-референсы (контекст чата): у остальных образцов нет
    internal static readonly string[] RefOps = ["generate", "edit", "inpaint"];

    internal static readonly string[] Ops = ["generate", "edit", "inpaint", "outpaint", "removeBackground", "upscale", "enhanceFaces", "crop"];
    private static readonly string[] Qualities = ["standard", "high", "max"];
    private static readonly string[] Anchors = ["left", "center", "right", "top", "bottom"];
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

    private const string NotWithoutAsk =
        "Не указывай без просьбы человека — по умолчанию берётся выбор человека из строки контекста";

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
            + "с папкой), версии (versionId, «версия N», какая текущая, от какой выросла), идущие запуски, "
            + "настройки запуска; поставщики и модели с возможностями. Ничего не меняет.",
            Obj(new JsonObject())),

        new(ToolFocus,
            "Берёт картинку в работу — человек увидит в ленте строку «Claude взял в работу: …» и "
            + "в строке контекста над полем ввода. threadId — картинка этого чата из image_state; file — "
            + "путь картинки проекта (нить по нему заведётся или найдётся; только в чате проекта); без обоих — снять выбор. "
            + "versionId вместе с threadId — ещё и сделать эту версию текущей: от неё пойдёт следующая правка. "
            + "Денег не тратит.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Картинка этого чата (threadId из image_state)"),
                ["file"] = Str("Путь картинки от корня проекта, если в чате её ещё нет; только в чате проекта"),
                ["versionId"] = Str("Версия картинки threadId (versionId из image_state), от которой править дальше"),
            })),

        new(ToolNew,
            "Заводит карточку «Новая картинка» (черновик без файла) и берёт её в работу — строкой в "
            + "ленте. Нарисовать её — image_generate с threadId черновика; сохранит в проект (в чате вне "
            + "проекта — скачает) человек.",
            Obj(new JsonObject
            {
                ["folder"] = Str("Папка проекта, куда человек сохранит картинку; пусто — корень. Только в чате проекта"),
            })),

        new(ToolGenerate,
            "Сразу запускает генерацию в картинку threadId — без вопроса человеку, это тратит деньги. "
            + "threadId обязателен: человек мог сменить картинку посреди хода. У черновика без файла "
            + "рисует новую по тексту. Поставщик, модель, число вариантов и персонаж по умолчанию — "
            + "выбор человека в строке контекста: не передавай их без его просьбы. Не больше двух запусков "
            + "за ход. Основа правки — текущая версия картинки или versionId («поправь вторую»). Возвращает "
            + "{ jobId, threadId, baseVersion, quote }. Каждый вариант станет новой версией внизу ленты; "
            + "сохранить в проект (или скачать) может только человек.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Картинка этого чата (threadId из image_state)"),
                ["versionId"] = Str("Версия картинки (versionId из image_state), от которой править; "
                    + "не указана — текущая. Станет текущей"),
                ["prompt"] = Str("Промпт генерации"),
                ["provider"] = Str("Поставщик (ключ из image_state): fal, higgsfield или local — «Локальные модели» "
                    + "на своей видеокарте, бесплатно, но с очередью. " + NotWithoutAsk),
                ["model"] = Str("Модель поставщика из image_state или auto. " + NotWithoutAsk),
                ["mode"] = OneOf(Modes, "Режим при модели auto"),
                ["count"] = Count(),
                ["references"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Образцы: пути файлов проекта с ролями; только в чате проекта. "
                        + "Не передан — берутся образцы контекста чата (если у человека включена строка контекста); "
                        + "пустой массив — без образцов",
                    ["items"] = Obj(new JsonObject
                    {
                        ["path"] = Str("Путь от корня проекта"),
                        ["role"] = OneOf(Roles, "Роль образца"),
                    }, "path", "role"),
                },
                ["character"] = Str("Персонаж проекта (slug папки characters/; только в чате проекта). "
                    + "Не передан — персонаж из контекста чата, пустая строка — без персонажа. " + NotWithoutAsk),
                ["face"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["description"] = "Номер лица из image_find_faces, на которое подставить персонажа. Только вместе с character, "
                        + "только для локальных моделей (provider local). Маску строит сервер, свою не передавай. "
                        + "Персонаж из строки контекста на чужое лицо не подставляется сам — укажи character явно.",
                },
                ["op"] = OneOf(Ops, "Операция: правка, фон, апскейл, дорисовка за края, улучшить лица (enhanceFaces, "
                    + "только local); crop — обрезать картинку под пропорции aspect без ИИ и бесплатно (результат — новая версия); "
                    + "по умолчанию — правка, а у новой картинки без файла — generate"),
                // Формат (ADR-025). Схема статична: пропорции и ступени модели в enum не попадают, они в image_state
                ["aspect"] = Str("Пропорции «W:H». У generate — формат новой картинки (запишется как формат проекта, человек увидит ✦); "
                    + "у outpaint — до каких пропорций дорисовать; у crop — до каких обрезать. Допустимые пропорции модели — в image_state. "
                    + NotWithoutAsk),
                ["quality"] = OneOf(Qualities, "Ступень качества новой картинки: размер зависит от модели, ступени — в image_state. "
                    + NotWithoutAsk),
                ["width"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Точная ширина новой картинки в px, только вместе с height; сервер округлит под модель. " + NotWithoutAsk,
                },
                ["height"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Точная высота новой картинки в px, только вместе с width. " + NotWithoutAsk,
                },
                ["anchor"] = OneOf(Anchors, "Только у op crop: к какой стороне прижать рамку обрезки; по умолчанию center"),
                ["matchSourceSize"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Вернуть вариантам размер исходника, если пропорции совпадают",
                },
            }, "threadId")),

        new(ToolFindFaces,
            "Находит лица на картинке версии и возвращает их номера слева направо. Нужен перед image_generate "
            + "с аргументом face. Ничего не меняет и бесплатен.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Картинка этого чата (threadId из image_state)"),
                ["versionId"] = Str("Версия картинки (versionId из image_state), на которой искать лица; "
                    + "не указана — текущая"),
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
