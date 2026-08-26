using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Тулсет MCP-over-HTTP: один «сервер» в терминах конфига хода, живущий внутри Kestrel
/// (ADR-012). Новый сервер добавляется реализацией этого интерфейса — контроллер
/// <c>McpTransportController</c> для всех тулсетов один и не копируется.
///
/// ИНВАРИАНТ: <see cref="Tools"/> не зависит от свойств хода. Состав tools/list входит в
/// сигнатуру запуска CLI ровно так же, как у stdio-серверов: как только он начнёт «мерцать»
/// между ходами, процесс claude перезапустится со всеми серверами разом («Stream closed»,
/// «No such tool available»). Ограничения по ходу живут на бэкенде, а не в составе.
/// Сторож — <c>McpToolsetStabilityTests</c>.
/// </summary>
public interface IMcpToolset
{
    /// <summary>Ключ сервера: он же имя в конфиге хода и сегмент маршрута <c>POST /mcp/{name}</c>.</summary>
    string Name { get; }

    /// <summary>Версия для serverInfo ответа initialize.</summary>
    string Version { get; }

    /// <summary>Полный состав инструментов. Один и тот же на любом ходу любого владельца.</summary>
    IReadOnlyList<McpToolSchema> Tools { get; }

    /// <summary>
    /// Вызов инструмента. Исключение наружу не выпускаем — контроллер обернёт его в
    /// content-ошибку (isError), как это делают stdio-серверы: модель должна получить
    /// текст, а не разрыв транспорта.
    /// </summary>
    Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments, McpToolCallContext context,
        CancellationToken ct);
}

/// <summary>Описание инструмента для tools/list. InputSchema — JSON Schema как есть.</summary>
public sealed record McpToolSchema(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// Ответ инструмента: текстовый content. IsError — «инструмент отработал, но результат
/// отрицательный» (валидация не прошла), а не отказ протокола.
/// </summary>
public sealed record McpToolCallResult(string Text, bool IsError = false);

/// <summary>
/// Кто зовёт: владелец из сервисного JWT (заголовок Authorization) и чат-вызыватель из
/// <c>X-Caller-Session-Id</c>, если сервер его прислал. Изоляция данных строится на
/// OwnerId — заголовку чата верить как источнику прав нельзя (он подставляется конфигом хода).
/// </summary>
public sealed record McpToolCallContext(string OwnerId, string? CallerSessionId);
