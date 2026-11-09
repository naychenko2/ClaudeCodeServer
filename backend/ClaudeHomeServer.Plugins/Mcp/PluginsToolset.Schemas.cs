using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.Plugins.Mcp;

/// <summary>
/// Схемы инструментов сервера plugins (ADR-031, шаг 4.1, пачка D «создание плагина по запросу»):
/// агент собирает плагин build-it-yourself в черновик владельца. Состав фиксирован (5 инструментов)
/// и не зависит от флага, хода, фокуса и сессии — ограничения (флаг plugins-build-yourself,
/// выключенная подсистема, локальный проект) проверяет бэкенд на вызове, а не в составе
/// (инвариант McpToolsetStabilityTests). Инструментов установки/согласия/выдачи прав нет:
/// принять и выдать права может только человек (шаг 4.3).
/// </summary>
public sealed partial class PluginsToolset
{
    public const string ServerName = McpEndpoints.PluginsName;

    // Справочник шима для агента (поле instructions ответа initialize): сводка API ccsPlugin +
    // правило высоты + набор классов + ссылка на полный документ. Отдельное поле initialize, а не
    // tools/list: не раздувает состав и не зависит от хода (McpToolsetStabilityTests); потолок
    // ~1950 символов держит McpInstructionsLengthGuardTests (CLI усекает instructions на ~2 КБ).
    public string? Instructions => KitReference;

    private const string KitReference =
        "Панель плагина — один HTML в sandbox-iframe (без allow-same-origin); единственный канал к хосту — "
        + "window.ccsPlugin (шим встраивает хост перед <body>). "
        + "API: ccsPlugin.ready() (шим шлёт сам), ccsPlugin.call(op, args) → Promise (данные на успех, "
        + "{code,message} на сбой), ccsPlugin.resize(h) (желаемая высота px), "
        + "ccsPlugin.on('init'|'theme'|'context', cb) (возвращает отписку), "
        + "ccsPlugin.state ({plugin,slot,context,theme,locale,granted}). "
        + "Хост шлёт init/result/theme/context. Операции: notes.search, notes.read, tasks.list, net.fetch, "
        + "storage.get, storage.set, storage.remove, storage.keys (право storage, данные в браузере человека). "
        + "Коды ошибок: denied/not_found/invalid/unavailable/upstream/timeout/limit. "
        + "ПРАВИЛО ВЫСОТЫ: не задавай height body и не используй 100vh — прокрутка у хоста, фрейм "
        + "растягивается по контенту; шим сам шлёт resize с высотой body. "
        + "КЛАССЫ (опт-ин, голые button/input не стилизуются): .cc-btn (--primary/--ghost/--danger/--sm), "
        + ".cc-input, .cc-list/.cc-list-item (.is-active), .cc-card, .cc-badge, .cc-empty (__title/__text), "
        + ".cc-notice, .cc-skeleton. "
        + "Шрифт: латиница Hanken Grotesk, кириллица — системный фолбэк. "
        + "Полный справочник: docs/features/plugin-kit.md";

    // Имена не однокоренные, чтобы модель их не путала: create / write_file / validate / preview / delete_file.
    public const string ToolCreate = "draft_create";
    public const string ToolWriteFile = "draft_write_file";
    public const string ToolValidate = "draft_validate";
    public const string ToolPreview = "draft_preview";
    public const string ToolDeleteFile = "draft_delete_file";

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (required.Length > 0)
        {
            var arr = new JsonArray();
            foreach (var r in required) arr.Add(r);
            schema["required"] = arr;
        }
        schema["properties"] = properties;
        return schema;
    }

    // slug — грамматика PluginId: [a-z0-9-]+ (часть id пакета после '/'), потолок длины
    // PluginId.MaxSlugLength (64) — тот же, что клиентская проверка isDraftSlug. maxLength
    // меняет текст схемы, но не состав tools/list (число/имена инструментов те же).
    private static JsonObject Slug()
    {
        var schema = Str("slug плагина: строчные латинские буквы, цифры и дефис, без «/» (например my-plugin)");
        schema["maxLength"] = PluginId.MaxSlugLength;
        return schema;
    }

    internal static readonly IReadOnlyList<McpToolSchema> Schemas =
    [
        new(ToolCreate,
            "Создаёт черновик плагина из шаблона (ccs-plugin.json + .claude-plugin/plugin.json, "
            + "version 0.1.0, contributes пуст). Повторный create того же slug — ошибка, а не перезапись. "
            + "Денег не тратит.",
            Obj(new JsonObject { ["slug"] = Slug() }, "slug")),

        new(ToolWriteFile,
            "Записывает файл в черновик (перезапись существующего допустима — так агент правит). "
            + "path — относительный путь внутри черновика. Запись в запрещённые места (hooks/, agents/, "
            + "bin/, .mcp.json, settings.json, symlink, файл вне allow-list) и превышение лимитов "
            + "(размер файла, число файлов, общий размер) — отказ с кодом проблемы валидатора. "
            + "Денег не тратит.",
            Obj(new JsonObject
            {
                ["slug"] = Slug(),
                ["path"] = Str("Относительный путь файла внутри черновика (например skills/my-skill/SKILL.md)"),
                ["content"] = Str("Содержимое файла (текст)"),
            }, "slug", "path", "content")),

        new(ToolValidate,
            "Проверяет черновик валидатором пакета (тот же код, что при установке) и возвращает список "
            + "проблем с кодами и путями. Пустой список — черновик готов к установке. Денег не тратит.",
            Obj(new JsonObject { ["slug"] = Slug() }, "slug")),

        new(ToolPreview,
            "Сводка «что будет после установки» для карточки: вклады (панели/команды/навыки), запрошенные "
            + "права и секреты (с признаком обязательного), дерево файлов, результат последней проверки. "
            + "Без содержимого секретов, без хостовых путей. Денег не тратит.",
            Obj(new JsonObject { ["slug"] = Slug() }, "slug")),

        new(ToolDeleteFile,
            "Удаляет файл из черновика. path — относительный путь. Денег не тратит.",
            Obj(new JsonObject
            {
                ["slug"] = Slug(),
                ["path"] = Str("Относительный путь файла внутри черновика"),
            }, "slug", "path")),
    ];
}
