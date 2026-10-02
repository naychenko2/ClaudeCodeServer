using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.AudioEditor.Mcp;

/// <summary>
/// Схемы инструментов сервера audio-editor (ADR-021 §5). Вызовы — AudioEditorToolset.cs. Состав и
/// описания фиксированы: не зависят от хода, фокуса, режима, выбранного поставщика и нитей; частные
/// параметры модели едут в <c>params</c> и проверяются на бэкенде по схеме модели. Схемы одни для чата
/// проекта и личного — проектные аргументы в личном чате отказывают на вызове.
///
/// Склейка — отдельный audio_concat, а не op у audio_generate: у неё другой вход (список кусков, а не
/// нить), другой итог (новая нить, а не версия) и нет поставщика, модели и цены — в одной схеме
/// threadId стал бы «обязательным, кроме склейки». Имена не однокоренные: generate и concat модель не
/// путает. Инструмента «сохранить» здесь нет и быть не должно — сохраняет только человек.
/// </summary>
public sealed partial class AudioEditorToolset
{
    public const string ServerName = McpEndpoints.AudioEditorName;

    public const string ToolState = "audio_state";
    public const string ToolFocus = "audio_focus";
    public const string ToolNew = "audio_new";
    public const string ToolVoices = "audio_voices";
    public const string ToolGenerate = "audio_generate";
    public const string ToolConcat = "audio_concat";
    public const string ToolSuggestPrompt = "audio_suggest_prompt";
    public const string ToolCancel = "audio_cancel";

    private static readonly string[] Modes = [AudioModes.Voice, AudioModes.Music, AudioModes.Process];

    // Все операции, кроме склейки (она — audio_concat), в тех же строках, что уходят по REST
    internal static readonly string[] Ops =
        [.. Enum.GetValues<AudioOp>().Where(op => op != AudioOp.Concat).Select(AudioEditJobService.OpName)];

    private static readonly string[] JointKinds = ["butt", "pause", "crossfade"];
    private static readonly string[] Formats = ["wav", "mp3", "flac", "ogg"];

    private static JsonArray StrEnum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Num(string description) =>
        new() { ["type"] = "number", ["description"] = description };

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
        "Не указывай без просьбы человека — по умолчанию берётся выбор человека: настройки звука, затем полоса «Звук»";

    private static JsonObject Count() => new()
    {
        ["type"] = "integer",
        ["minimum"] = 1,
        ["maximum"] = 4,
        ["description"] = "Сколько вариантов (1–4). " + NotWithoutAsk,
    };

    internal static readonly IReadOnlyList<McpToolSchema> Schemas =
    [
        new(ToolState,
            "Звуки этого чата: какой в работе (focus), у каждого threadId, файл (или черновик с папкой), версии "
            + "(versionId, «версия N», файлы по ролям: main, stem:vocals, subtitles…, какая текущая), идущие запуски, "
            + "настройки звука; выбор человека по режимам (голос, музыка, обработка); поставщики и модели с "
            + "возможностями. Ничего не меняет.",
            Obj(new JsonObject())),

        new(ToolFocus,
            "Берёт звук в работу — человек увидит его в полосе «Звук». threadId — звук этого чата из audio_state; "
            + "file — путь звукового файла проекта (нить по нему заведётся или найдётся; только в чате проекта); "
            + "без обоих — снять выбор. versionId вместе с threadId — ещё и сделать эту версию текущей: от неё "
            + "пойдёт следующая операция. Денег не тратит.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Звук этого чата (threadId из audio_state)"),
                ["file"] = Str("Путь звукового файла от корня проекта, если в чате его ещё нет; только в чате проекта"),
                ["versionId"] = Str("Версия звука threadId (versionId из audio_state), от которой работать дальше"),
            })),

        new(ToolNew,
            "Заводит карточку «Новый звук» (черновик без файла) и берёт её в работу. Записать в неё — "
            + "audio_generate с threadId черновика; сохранит в проект (в чате вне проекта — скачает) человек.",
            Obj(new JsonObject
            {
                ["mode"] = OneOf(Modes, "Режим: voice — голос, music — музыка, process — обработка; настройки "
                    + "черновика возьмутся из выбора человека в этом режиме"),
                ["folder"] = Str("Папка проекта, куда человек сохранит звук; пусто — корень. Только в чате проекта"),
            })),

        new(ToolVoices,
            "Дикторы поставщиков для озвучки: id диктора передаётся в voice у audio_generate. Без provider — "
            + "по всем доступным поставщикам. Плюс голоса проекта из библиотеки «Голоса», когда она есть. "
            + "Ничего не меняет.",
            Obj(new JsonObject
            {
                ["provider"] = Str("Поставщик (ключ из audio_state)"),
                ["model"] = Str("Модель поставщика из audio_state"),
                ["language"] = Str("Код языка ISO (ru, en…), чтобы сузить список"),
            })),

        new(ToolGenerate,
            "Сразу запускает операцию со звуком threadId — без вопроса человеку, облачные поставщики тратят "
            + "деньги. threadId обязателен: человек мог сменить звук посреди хода. Поставщик, модель, операция и "
            + "число вариантов по умолчанию — выбор человека: не передавай их без его просьбы. Не больше двух "
            + "запусков за ход. Основа — текущая версия звука или versionId. Операции trim, gainFade, normalize, "
            + "mixStems — монтаж без ИИ: бесплатно, итог — новая версия. Частные параметры модели — в params "
            + "(неизвестный ключ — отказ с именем поля). Возвращает { jobId, threadId, baseVersion, quote }. "
            + "Каждый вариант станет новой версией звука; сохранить в проект (или скачать) может только человек. "
            + "Склеить куски — audio_concat.",
            Obj(new JsonObject
            {
                ["threadId"] = Str("Звук этого чата (threadId из audio_state)"),
                ["versionId"] = Str("Версия звука (versionId из audio_state), от которой работать; не указана — "
                    + "текущая. Станет текущей"),
                ["mode"] = OneOf(Modes, "Режим: voice — голос, music — музыка, process — обработка; не указан — по "
                    + "операции или настройкам звука"),
                ["op"] = OneOf(Ops, "Операция. " + NotWithoutAsk),
                ["provider"] = Str("Поставщик (ключ из audio_state), local — «Локальные модели» на своей "
                    + "видеокарте, бесплатно, но с очередью. " + NotWithoutAsk),
                ["model"] = Str("Модель поставщика из audio_state или auto. " + NotWithoutAsk),
                ["text"] = Str("Что озвучить (голос) или стиль и описание (музыка, обработка)"),
                ["lyrics"] = Str("Слова песни с секциями [Verse], [Chorus]…"),
                ["voice"] = Str("Диктор: id из audio_voices"),
                ["language"] = Str("Код языка ISO (ru, en…)"),
                ["range"] = Obj(new JsonObject
                {
                    ["start"] = Num("Начало куска, с"),
                    ["end"] = Num("Конец куска, с"),
                }, "start", "end"),
                ["durationSeconds"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["description"] = "Длина результата, с (музыка)",
                },
                ["count"] = Count(),
                ["params"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Частные параметры модели (у local — как у инструментов local-media: speaker, "
                        + "bpm, key, strength, track…). Проверяются по схеме модели",
                },
            }, "threadId")),

        new(ToolConcat,
            "Склеивает куски в НОВЫЙ звук без ИИ — бесплатно, сразу. Кусок — версия звука этого чата (threadId, "
            + "versionId — не указан: текущая) или файл проекта (file; только в чате проекта). Итог — новая "
            + "карточка «Новый звук» с версией 1; сохранит её в проект (или скачает) человек.",
            Obj(new JsonObject
            {
                ["pieces"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 2,
                    ["description"] = "Куски по порядку: у каждого ровно одно — threadId или file",
                    ["items"] = Obj(new JsonObject
                    {
                        ["threadId"] = Str("Звук этого чата (threadId из audio_state)"),
                        ["versionId"] = Str("Версия этого звука; не указана — текущая"),
                        ["file"] = Str("Путь звукового файла от корня проекта"),
                    }),
                },
                ["joint"] = Obj(new JsonObject
                {
                    ["kind"] = OneOf(JointKinds, "Стык: butt — встык, pause — пауза, crossfade — плавный переход"),
                    ["seconds"] = Num("Длина паузы или перехода, с"),
                }, "kind"),
                ["normalizeLoudness"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Выровнять громкость кусков (по умолчанию да)",
                },
                ["name"] = Str("Имя нового файла без папок"),
                ["format"] = OneOf(Formats, "Формат результата (по умолчанию wav)"),
                ["folder"] = Str("Папка проекта, куда человек сохранит звук; только в чате проекта"),
            }, "pieces")),

        new(ToolSuggestPrompt,
            "Только предлагает текст запуска человеку: ничего не запускает и не меняет.",
            Obj(new JsonObject
            {
                ["prompt"] = Str("Предлагаемый текст: что озвучить или стиль музыки"),
                ["mode"] = OneOf(Modes, "Режим, для которого предложение"),
                ["model"] = Str("Модель, которую советуешь"),
            }, "prompt")),

        new(ToolCancel,
            "Отменяет задачу звука этого чата. Готовые варианты остаются версиями.",
            Obj(new JsonObject
            {
                ["jobId"] = Str("jobId из результата audio_generate"),
            }, "jobId")),
    ];
}
