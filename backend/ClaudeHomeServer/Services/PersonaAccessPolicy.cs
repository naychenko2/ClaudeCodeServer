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
        "Edit", "Write", "MultiEdit", "NotebookEdit",
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
    ];

    // Итоговый список дополнительных запретов сессии персоны:
    // выключенный «web» (Persona.Tools) + профиль доступа (ReadOnly-список или
    // пользовательский список при Custom). null — запретов нет (или персоны нет).
    public static IReadOnlyList<string>? BuildExtraDisallowed(Persona? persona)
    {
        if (persona is null) return null;

        var result = new List<string>();

        // Возможность «web» выключена — запрещаем встроенные веб-тулы CLI
        if (persona.Tools is not null && !persona.Tools.Contains("web", StringComparer.OrdinalIgnoreCase))
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
