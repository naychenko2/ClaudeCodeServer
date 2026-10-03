using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Mcp;

/// <summary>
/// Схемы инструментов сервера video-editor (ADR-022 §5). Вызовы — VideoEditorToolset.cs. Состав и описания
/// фиксированы: не зависят от хода, фокуса, поставщика, вида чата и сцен; частные параметры модели едут в
/// <c>params</c> и проверяются на бэкенде по схеме модели. Схемы одни для чата проекта и личного: фильмовые
/// инструменты в личном чате есть, но отвечают отказом на вызове.
///
/// Инструменты «сохранить» и «собрать» здесь есть (в отличие от звука): сохранение агентом — отдельное право,
/// только в video/** и music/**, только CreateNew; .film правится только под ревизией. Имена не однокоренные:
/// shoot, save_scene, film_edit и film_build модель не путает.
/// </summary>
public sealed partial class VideoEditorToolset
{
    public const string ServerName = ClaudeHomeServer.Services.McpEndpoints.VideoEditorName;

    public const string ToolState = "video_state";
    public const string ToolFocus = "video_focus";
    public const string ToolNew = "video_new";
    public const string ToolSceneSet = "video_scene_set";
    public const string ToolSuggestPrompt = "video_suggest_prompt";
    public const string ToolShoot = "video_shoot";
    public const string ToolCancel = "video_cancel";
    public const string ToolWait = "video_wait";
    public const string ToolSaveScene = "video_save_scene";
    public const string ToolFilmEdit = "video_film_edit";
    public const string ToolFilmBuild = "video_film_build";

    // Что работает без запуска агентом: чтение, фокус, заготовка сцены и её настройки — ничего не тратит и не пишет в проект
    private static readonly string[] ReadOnlyTools = [ToolState, ToolFocus, ToolNew, ToolSceneSet, ToolSuggestPrompt];

    private static readonly string[] FilmOps =
        [FilmPatchOps.Add, FilmPatchOps.Remove, FilmPatchOps.Move, FilmPatchOps.Cut, FilmPatchOps.Trim, FilmPatchOps.Music];

    private static readonly string[] CutTypes = [FilmCutTypes.Butt, FilmCutTypes.Dissolve, FilmCutTypes.Fade];

    private static JsonArray StrEnum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

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
        "Не указывай без просьбы человека — по умолчанию берётся его выбор: настройки сцены, затем префы области";

    private static JsonObject Frame(string which) => new()
    {
        ["type"] = new JsonArray("object", "null"),
        ["description"] = "Кадр " + which + ": { \"kind\": \"file\", \"path\": \"…\" } — картинка проекта или "
            + "{ \"kind\": \"image\", \"threadId\": \"…\", \"versionId\": \"…\", \"follow\": true } — версия нити «Картинок». "
            + "null снимает кадр; не указан — как есть",
    };

    // Поля настроек сцены: общие у video_new и video_scene_set
    private static JsonObject SettingsProperties() => new()
    {
        ["text"] = Str("Что происходит в кадре: движение, камера, свет"),
        ["frameA"] = Frame("A (первый)"),
        ["frameB"] = Frame("B (последний)"),
        ["provider"] = Str("Поставщик (ключ из video_state): fal, higgsfield, local. " + NotWithoutAsk),
        ["model"] = Str("Модель поставщика из video_state или auto. " + NotWithoutAsk),
        ["durationSec"] = Int("Длительность клипа, с (из durations модели)"),
        ["aspect"] = Str("Пропорции кадра: 16:9, 9:16… (из aspects модели)"),
        ["sound"] = Bool("Со звуком, если модель умеет"),
        ["count"] = new JsonObject
        {
            ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 4,
            ["description"] = "Сколько вариантов (1–4). " + NotWithoutAsk,
        },
    };

    private static JsonObject FilmOpSchema() => Obj(new JsonObject
    {
        ["op"] = OneOf(FilmOps, "add — добавить клип; remove — убрать строку; move — переставить; cut — стык после строки; "
            + "trim — обрезать клип; music — музыка фильма (music: null убирает)"),
        ["file"] = Str("add: клип проекта в video/** (путь от корня)"),
        ["index"] = Int("add: куда вставить (по умолчанию в конец); remove, trim: номер строки с нуля; cut: стык после этой строки"),
        ["from"] = Int("move: откуда"),
        ["to"] = Int("move: куда"),
        ["trim"] = new JsonObject
        {
            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2,
            ["description"] = "[начало, конец] в секундах клипа: add (необязательно) и trim",
        },
        ["cutType"] = OneOf(CutTypes, "cut: butt — встык, dissolve — наплыв, fade — через затемнение"),
        ["sec"] = Num("cut: длительность наплыва или затемнения, с"),
        ["scene"] = Obj(new JsonObject
        {
            ["text"] = Str("Текст сцены"),
            ["frameA"] = Str("Кадр A — файл проекта"),
            ["frameB"] = Str("Кадр B — файл проекта"),
            ["provider"] = Str("Поставщик"),
            ["model"] = Str("Модель"),
            ["durationSec"] = Int("Длительность, с"),
        }, "text"),
        ["music"] = new JsonObject
        {
            ["type"] = new JsonArray("object", "null"),
            ["description"] = "music: { file (music/**), volume 0..100, fadeOut — секунды } или null — убрать",
        },
    }, "op");

    internal static readonly IReadOnlyList<McpToolSchema> Schemas =
    [
        new(ToolState,
            "Сцены этого чата (версии, кадры, идущие запуски, «переснять»), фокус, фильмы проекта кратко, открытый "
            + "фильм целиком (строки, стыки, музыка, ревизия, «Потрачено на фильм», сборка), поставщики и модели с "
            + "возможностями, выбор человека. Ничего не меняет.",
            Obj(new JsonObject())),

        new(ToolFocus,
            "Берёт сцену и/или фильм в работу — человек увидит это в панели «Видео», сама панель не двигается. "
            + "sceneId — сцена этого чата из video_state; versionId вместе с sceneId — ещё и сделать версию текущей; "
            + "filmPath — фильм проекта (video/**.film). Что не указано — остаётся как есть, пустая строка снимает. "
            + "Денег не тратит.",
            Obj(new JsonObject
            {
                ["sceneId"] = Str("Сцена этого чата (sceneId из video_state)"),
                ["versionId"] = Str("Версия сцены sceneId, от которой работать дальше"),
                ["filmPath"] = Str("Фильм проекта: путь к .film от корня; только в чате проекта"),
            })),

        new(ToolNew,
            "Заводит сцену — один клип между кадром A и кадром B — и берёт её в работу. Снять её — video_shoot; "
            + "сохранить в проект — video_save_scene. Настройки не указаны — берутся префы области. "
            + "Ничего не тратит.",
            Obj(MergeProps(SettingsProperties(), new JsonObject
            {
                ["name"] = Str("Имя сцены; не указано — «Сцена N»"),
                ["folder"] = Str("Папка сцены в video/** проекта, например video/утро; пусто — не задана. Только в чате проекта"),
            }))),

        new(ToolSceneSet,
            "Меняет настройки сцены: текст, кадры, поставщика, модель, длительность, пропорции, звук, число вариантов. "
            + "Указанные поля заменяются, остальные остаются. Нельзя, пока у сцены идёт съёмка. Денег не тратит.",
            Obj(MergeProps(new JsonObject { ["sceneId"] = Str("Сцена этого чата (sceneId из video_state)") }, SettingsProperties()),
                "sceneId")),

        new(ToolSuggestPrompt,
            "Только предлагает текст сцены человеку: ничего не запускает и не меняет.",
            Obj(new JsonObject
            {
                ["prompt"] = Str("Предлагаемый текст сцены"),
                ["model"] = Str("Модель, которую советуешь"),
            }, "prompt")),

        new(ToolShoot,
            "Сразу снимает сцену sceneId — без вопроса человеку, облачные поставщики тратят деньги; цену считает "
            + "котировка внутри. sceneId обязателен: человек мог сменить сцену посреди хода. Поставщик, модель, "
            + "длительность и число вариантов по умолчанию — выбор человека: не передавай их без его просьбы. "
            + "Каждый вариант станет версией сцены. Идёт отдельно от хода; результат — в video_state. "
            + "Частные параметры модели — в params (неизвестный ключ — отказ с именем поля). "
            + "После каждой сцены спрашивай человека «Снимать следующую?»; подряд — только по явному «сними все» "
            + "(тогда дождись video_wait, сохрани и собери фильм без вопросов).",
            Obj(new JsonObject
            {
                ["sceneId"] = Str("Сцена этого чата (sceneId из video_state)"),
                ["provider"] = Str("Поставщик (ключ из video_state). " + NotWithoutAsk),
                ["model"] = Str("Модель поставщика или auto. " + NotWithoutAsk),
                ["durationSec"] = Int("Длительность клипа, с. " + NotWithoutAsk),
                ["aspect"] = Str("Пропорции кадра. " + NotWithoutAsk),
                ["sound"] = Bool("Со звуком. " + NotWithoutAsk),
                ["count"] = new JsonObject
                {
                    ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 4,
                    ["description"] = "Сколько вариантов (1–4). " + NotWithoutAsk,
                },
                ["seed"] = Int("Seed для повторяемости"),
                ["params"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Частные параметры модели (у local — как у инструментов local-media). Проверяются по схеме модели",
                },
            }, "sceneId")),

        new(ToolCancel,
            "Отменяет съёмку этого чата. Готовые варианты остаются версиями; остановленное до принятия поставщиком не списывается.",
            Obj(new JsonObject { ["jobId"] = Str("jobId из результата video_shoot") }, "jobId")),

        new(ToolWait,
            "Ждёт конца съёмок до timeoutSeconds (не больше 15 с) и возвращает их состояние. allDone=false — вызови ещё "
            + "раз, пока не станет true: съёмка идёт минутами и дольше хода, поэтому «Сними все» доводи циклом «video_wait → "
            + "video_shoot следующей сцены → … → video_save_scene → video_film_build», не заканчивая ход на ожидании. "
            + "jobIds не указаны — все идущие съёмки этого чата. Ничего не тратит и не меняет.",
            Obj(new JsonObject
            {
                ["jobIds"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 12,
                    ["description"] = "jobId из video_shoot; не указаны — все идущие съёмки этого чата",
                },
                ["timeoutSeconds"] = new JsonObject
                {
                    ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 15,
                    ["description"] = "Сколько ждать, с (по умолчанию 15, не больше 15)",
                },
            })),

        new(ToolSaveScene,
            "Сохраняет версию сцены в проект: клип video/<папка>/scene-NN.mp4 (занято — .v2, перезаписи нет) и кадры в "
            + "кадры/; сцена сама встаёт в фильм этой папки. Пишет только в video/** и music/**. Только в чате проекта. "
            + "В ленте — тихая строка с пометкой «✦ Claude».",
            Obj(new JsonObject
            {
                ["sceneId"] = Str("Сцена этого чата (sceneId из video_state)"),
                ["versionId"] = Str("Версия; не указана — текущая"),
                ["folder"] = Str("Папка фильма в video/**; не указана — папка сцены или по имени сцены"),
                ["fileName"] = Str("Имя файла .mp4; не указано — scene-NN.mp4"),
                ["filmPath"] = Str("Полный путь открытого фильма video/<папка>/<имя>.film: сцена встанет в него, папка = папка фильма"),
            }, "sceneId")),

        new(ToolFilmEdit,
            "Правит фильм (.film) атомарным патчем под ревизией: ops выполняются все или ни одна. revision — из "
            + "video_state; устарела (человек успел править) — отказ revision_conflict со свежим состоянием, перечитай и "
            + "повтори. Только в чате проекта. В ленте — тихая строка, а строки фильма получают пометку «✦ Claude».",
            Obj(new JsonObject
            {
                ["path"] = Str("Фильм: путь к .film в video/** от корня проекта"),
                ["revision"] = Str("Ревизия фильма из video_state"),
                ["ops"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["items"] = FilmOpSchema(),
                    ["description"] = "Операции патча по порядку",
                },
            }, "path", "revision", "ops")),

        new(ToolFilmBuild,
            "Собирает фильм в film.mp4 (ffmpeg, без ИИ, бесплатно) под тяжёлым слотом сервера: заявка принимается сразу, "
            + "сборка идёт в фоне, ход — в video_state (build). Готовый файл не перезаписывается: следующая сборка — "
            + "film.v2.mp4. Только в чате проекта.",
            Obj(new JsonObject { ["path"] = Str("Фильм: путь к .film в video/**; не указан — открытый фильм") })),
    ];

    private static JsonObject MergeProps(JsonObject first, JsonObject second)
    {
        var merged = new JsonObject();
        foreach (var (key, value) in first) merged[key] = value?.DeepClone();
        foreach (var (key, value) in second) merged[key] = value?.DeepClone();
        return merged;
    }
}
