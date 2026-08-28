using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Профили доступа персон (P6): превращают Persona.Access в список запрещённых
// инструментов сессии (ExtraDisallowedTools поверх конфига Claude:DisallowedTools).
// Сюда же перенесено прежнее правило «web выключен → запрет WebSearch/WebFetch».
public static class PersonaAccessPolicy
{
    // Профиль «Только чтение»: смотрит и советует, но ничего не меняет.
    // Файловые мутации + Bash целиком + мутирующие инструменты наших MCP-серверов
    // (имена сверены с mcp/*-server/index.js; ключи серверов — из BuildTurnMcpConfig).
    // memory_remember/memory_forget НЕ запрещаем: долгая память — её собственная.
    public static readonly string[] ReadOnlyDisallowed =
    [
        // Файловые мутации CLI
        // MultiEdit убран: в CLI 2.1.x такого инструмента нет, а неизвестное имя в deny-правиле
        // роняет запуск claude с кодом 1. Прямой замены у него нет — правки идут вызовами Edit,
        // который и так в списке, так что запрет на мутации файлов не ослаб.
        "Edit", "Write", "NotebookEdit",
        // Bash целиком (и фоновые процессы)
        "Bash", "KillShell",
        // MCP задач (mcp__tasks__*)
        "mcp__tasks__tasks_create", "mcp__tasks__tasks_update", "mcp__tasks__tasks_complete",
        "mcp__tasks__tasks_delete", "mcp__tasks__tasks_add_subtask", "mcp__tasks__tasks_toggle_subtask",
        // MCP заметок (mcp__notes__*)
        "mcp__notes__notes_create", "mcp__notes__notes_update", "mcp__notes__notes_delete",
        // MCP персон (mcp__personas__*)
        "mcp__personas__personas_create", "mcp__personas__personas_update",
        "mcp__personas__personas_delete", "mcp__personas__personas_generate_avatar",
        // Десктопная грань (ADR-008): читающие desktop_devices/desktop_screen/desktop_ui
        // персоне «только чтение» остаются, всё меняющее чужой рабочий стол — нет.
        // Имена MCP-инструментов в deny безопасны и когда сервер в ход не доставлен:
        // CLI не сверяет mcp__* со списком известных инструментов (в отличие от встроенных
        // имён — см. MultiEdit выше). Проверено живым прогоном CLI — см. тест
        // DesktopMcpToolsetStabilityTests.DenyИменаДесктопа_НеРоняютЗапускCli.
        "mcp__desktop__desktop_act", "mcp__desktop__desktop_open", "mcp__desktop__desktop_run",
        // Рабочее пространство (wsp, ADR-012 волна 3): все МУТИРУЮЩИЕ инструменты — файлы
        // (включая files_to_markdown: он сохраняет .md в проекте), git-запись, проекты и
        // теги, базы знаний, создание/переименование чатов и деструктив. Находка приёмки
        // волны 3.1: шапка тулсета и ADR утверждали этот гейт, а списка не было — профиль
        // «Только чтение» спокойно звал files_write. Чтение и ОБЩЕНИЕ (chats_send/
        // chats_report_up — как память memory_remember выше) не запрещаем.
        // Соответствие имён каталогу инструментов держит WorkspaceToolsetParityTests.
        "mcp__wsp__files_write", "mcp__wsp__files_mkdir", "mcp__wsp__files_rename",
        "mcp__wsp__files_to_markdown", "mcp__wsp__files_delete",
        "mcp__wsp__git_commit", "mcp__wsp__git_stage",
        "mcp__wsp__projects_create", "mcp__wsp__projects_update", "mcp__wsp__tags_apply",
        "mcp__wsp__tags_remove",
        "mcp__wsp__knowledge_index", "mcp__wsp__kb_add_document",
        "mcp__wsp__chats_create", "mcp__wsp__chats_update", "mcp__wsp__chats_delete",
    ];

    // Итоговый список дополнительных запретов сессии персоны:
    // выключенный «web» + профиль доступа (ReadOnly-список или пользовательский
    // список при Custom). null — запретов нет (или персоны нет).
    // webAllowed — готовое решение по «web» от вызывающего (PersonaBindingsService.
    // EffectiveToolEnabled: Tool-привязка приоритетнее Persona.Tools); null — фолбэк
    // по Persona.Tools для путей без привязок. Запреты складываются: побеждает более строгий.
    public static IReadOnlyList<string>? BuildExtraDisallowed(Persona? persona, bool? webAllowed = null)
    {
        if (persona is null) return null;

        var result = new List<string>();

        // Возможность «web» выключена — запрещаем встроенные веб-тулы CLI
        var webEnabled = webAllowed
            ?? persona.Tools is null || persona.Tools.Contains("web", StringComparer.OrdinalIgnoreCase);
        if (!webEnabled)
        {
            result.Add("WebSearch");
            result.Add("WebFetch");
        }

        switch (persona.Access)
        {
            case PersonaAccess.ReadOnly:
                result.AddRange(ReadOnlyDisallowed);
                break;
            case PersonaAccess.Custom when persona.DisallowedTools is { Count: > 0 }:
                result.AddRange(persona.DisallowedTools);
                break;
        }

        var clean = result
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return clean.Count > 0 ? clean : null;
    }
}
