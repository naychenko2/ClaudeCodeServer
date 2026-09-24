namespace ClaudeHomeServer.Protocol;

/// <summary>
/// Контракт localhost-API агента устройства (ADR-016 §5, план §2 п. 10): браузер на машине
/// проекта ходит к агенту напрямую, по короткому билету сервера. Своего ключа подписи у
/// агента нет — билет он проверяет интроспекцией через канал управления и кэширует на срок
/// билета. Единый источник правды для сервера, агента и (через копию констант) фронта.
/// </summary>
public static class DeviceAgentApi
{
    /// <summary>Порт по умолчанию: фронт обнаруживает агента на нём.</summary>
    public const int DefaultPort = 47318;

    /// <summary>Заголовок с билетом. Не <c>Authorization</c>: тот у браузера занят JWT сервера.</summary>
    public const string TicketHeader = "X-Agent-Ticket";

    /// <summary>Срок жизни билета: короткий, фронт перевыпускает его до истечения.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Метод хаба устройств: интроспекция билета (устройство → сервер).</summary>
    public const string IntrospectMethod = "IntrospectAgentTicket";

    /// <summary>Метод хаба устройств: события ватчера проекта (устройство → сервер → веб-морда).</summary>
    public const string FilesChangedMethod = "ProjectFilesChanged";

    /// <summary>Потолок путей в одном донесении ватчера; больше — признак полной пересинхронизации.</summary>
    public const int MaxChangedPaths = 500;
}

/// <summary>
/// Ответ сервера на интроспекцию билета. Выдаётся только устройству, к которому билет
/// привязан; чужому устройству, просроченному или неизвестному билету — <c>null</c>.
/// <see cref="RootPath"/> — корень проекта на этом устройстве: агент всё равно сверяет его
/// со своими разрешёнными корнями и серверу не доверяет.
/// </summary>
public sealed record AgentTicketIntrospection(
    string OwnerId,
    string ProjectId,
    string RootPath,
    DateTimeOffset ExpiresAt);

/// <summary>Донесение ватчера агента: пути относительно корня проекта, прямые слэши.</summary>
public sealed record DeviceFilesChanged(string ProjectId, IReadOnlyList<string> Paths, bool Full);
