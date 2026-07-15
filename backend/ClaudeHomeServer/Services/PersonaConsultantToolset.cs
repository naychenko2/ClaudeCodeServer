using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Набор инструментов персоны-консультанта (файлового сабагента). ЯВНЫЙ allow-list —
// deny-by-default: всё, что не перечислено, сабагенту физически недоступно (поле tools
// определения агента). Это ОТДЕЛЬНАЯ политика от PersonaAccessPolicy: та задаёт запреты
// персоны-собеседника, чьи действия одобряет живой пользователь; у сабагента-консультанта
// permission-канала нет вовсе (фоновый контекст), поэтому набор жёстко read-only — вопрос
// разрешения не возникает. Write-исключение одно: собственная память персоны (pmem-сервер).
public static class PersonaConsultantToolset
{
    // Встроенные read-инструменты CLI: чтение и поиск по файлам зоны сессии
    public static readonly string[] BuiltIn = ["Read", "Grep", "Glob"];

    // Веб — read-safe; включается по возможности «web» самой персоны
    public static readonly string[] Web = ["WebSearch", "WebFetch"];

    public static readonly string[] TasksRead =
    [
        "mcp__tasks__tasks_list", "mcp__tasks__tasks_search",
        "mcp__tasks__tasks_get", "mcp__tasks__tasks_board_columns",
    ];

    public static readonly string[] NotesRead =
    [
        "mcp__notes__notes_list", "mcp__notes__notes_search", "mcp__notes__notes_read",
        "mcp__notes__notes_backlinks", "mcp__notes__notes_graph", "mcp__notes__notes_semantic_search",
    ];

    public static readonly string[] PersonasRead =
    [
        "mcp__personas__personas_list", "mcp__personas__personas_get",
        "mcp__personas__personas_bindings_list", "mcp__personas__personas_suggest_bindings",
        "mcp__personas__knowledge_search", "mcp__personas__personas_automation_list",
    ];

    public static readonly string[] WspRead =
    [
        "mcp__wsp__projects_list", "mcp__wsp__projects_get",
        "mcp__wsp__files_tree", "mcp__wsp__files_read", "mcp__wsp__files_search",
        "mcp__wsp__knowledge_search", "mcp__wsp__knowledge_status",
        "mcp__wsp__search_unified", "mcp__wsp__chats_list", "mcp__wsp__chats_history",
    ];

    // Ключ выделенного memory-сервера консультанта в MCP-конфиге хода.
    // Общий префикс "pmem_" завязан на BuiltInMcpServerPrefixes ("mcp__pmem_") в ClaudeSession.
    public static string PmemServerKey(string handle)
    {
        var slug = new string(handle.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());
        return "pmem_" + slug;
    }

    // Инструменты собственного memory-сервера консультанта: чтение + запись личной памяти
    // (её собственное решение, что запомнить) + чтение командной памяти проекта.
    // team_memory_remember/forget НЕ выдаются — общие данные команды не для фонового write.
    public static IReadOnlyList<string> MemoryTools(string pmemKey, bool teamMemory)
    {
        var tools = new List<string>
        {
            $"mcp__{pmemKey}__memory_search", $"mcp__{pmemKey}__memory_list",
            $"mcp__{pmemKey}__memory_remember", $"mcp__{pmemKey}__memory_forget",
        };
        if (teamMemory) tools.Add($"mcp__{pmemKey}__team_memory_list");
        return tools;
    }

    // Полный allow-list консультанта. Custom.DisallowedTools персоны только СУЖАЕТ набор
    // (точное совпадение имени); Access расширить его не может — безопасность сабагента
    // не зависит от профиля, рассчитанного на живой надзор.
    public static IReadOnlyList<string> Build(Persona persona, bool webAllowed)
    {
        var tools = new List<string>(BuiltIn);
        if (webAllowed) tools.AddRange(Web);
        tools.AddRange(TasksRead);
        tools.AddRange(NotesRead);
        tools.AddRange(PersonasRead);
        tools.AddRange(WspRead);
        if (persona.MemoryEnabled)
            tools.AddRange(MemoryTools(PmemServerKey(persona.Handle),
                teamMemory: persona.Scope == PersonaScope.Project && persona.ProjectId is not null));

        IEnumerable<string> result = tools.Distinct(StringComparer.Ordinal);
        if (persona.Access == PersonaAccess.Custom && persona.DisallowedTools is { Count: > 0 } denied)
        {
            var deniedSet = denied.Select(t => t.Trim()).Where(t => t.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            result = result.Where(t => !deniedSet.Contains(t));
        }
        return result.ToList();
    }
}
