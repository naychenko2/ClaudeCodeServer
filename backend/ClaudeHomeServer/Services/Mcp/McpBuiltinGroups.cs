namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Группы экрана «MCP-серверы» для серверов вне личного реестра. Ось классификации —
/// кто подключил сервер и кто им управляет: пользователю важно видеть разницу между
/// частью продукта, памятью его персон и наследством из конфигов CLI. Значения уходят
/// в ответ GET /api/mcp/servers/builtin, фронт разносит плитки по группам.
/// </summary>
public static class McpBuiltinGroups
{
    /// <summary>Сервисы AI Home: продуктовые серверы (tasks, notes, wsp…).</summary>
    public const string Product = "product";

    /// <summary>Интеграции продукта с внешними сервисами (dify, fal-ai, glif).</summary>
    public const string Integration = "integration";

    /// <summary>Выделенные серверы памяти персон-консультантов (pmem_&lt;handle&gt;).</summary>
    public const string PersonaMemory = "persona-memory";

    /// <summary>Подключено вне продукта: CLI принёс сервер из своих конфигов или плагинов.</summary>
    public const string External = "external";
}
