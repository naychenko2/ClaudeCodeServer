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

    /// <summary>
    /// Параметр запроса с билетом потока: <c>&lt;video src&gt;</c>/<c>&lt;img src&gt;</c> заголовок не
    /// ставят, а основной билет в URL не попадает никогда (история, логи, Referer). Билет потока
    /// выдаёт сам агент по основному билету, на один путь и на <see cref="StreamTicketLifetime"/>.
    /// </summary>
    public const string StreamTicketQuery = "streamTicket";

    /// <summary>Срок билета потока: хватает, чтобы плеер начал и докачивал диапазонами.</summary>
    public static readonly TimeSpan StreamTicketLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Выдача билета потока, относительно <c>api/projects/{projectId}/</c>; вне контракта файлов сервера.</summary>
    public const string StreamTicketRoute = "agent/stream-ticket";

    /// <summary>
    /// Порт превью дев-серверов (задача 4.3): отдельный от API, чтобы страница дев-сайта не
    /// оказалась одного origin с localhost-API агента. Слушается тоже только loopback.
    /// </summary>
    public const int DefaultPreviewPort = DefaultPort + 1;

    /// <summary>Хаб агента: терминалы и логи дев-серверов — те же методы, что у серверных хабов.</summary>
    public const string HubPath = "/hubs/agent";

    /// <summary>
    /// Выдача билета хаба (относительно <c>api/projects/{projectId}/</c>): WebSocket из браузера
    /// заголовок не ставит, и подключение несёт в URL узкий билет, а не основной.
    /// </summary>
    public const string HubTicketRoute = "agent/hub-ticket";

    /// <summary>Срок билета хаба: только на рукопожатие, переподключение берёт новый.</summary>
    public static readonly TimeSpan HubTicketLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Выдача билета превью (относительно <c>api/projects/{projectId}/</c>).</summary>
    public const string PreviewTicketRoute = "agent/preview-ticket";

    /// <summary>Параметр адреса iframe с билетом превью: первая загрузка ставит по нему куку.</summary>
    public const string PreviewTicketQuery = "previewTicket";

    /// <summary>Кука превью на пути <c>/preview/{projectId}/</c>: её несут подресурсы дев-сайта.</summary>
    public const string PreviewCookie = "cc_agent_preview";

    /// <summary>
    /// Срок билета превью. Не привязан к основному: превью — это страница, которую смотрят
    /// часами, а сам дев-сервер слушает loopback без всякой защиты, и билет не открывает
    /// ничего сверх того, что любой локальный процесс видит и так.
    /// </summary>
    public static readonly TimeSpan PreviewTicketLifetime = TimeSpan.FromHours(8);

    /// <summary>Приём вложения чата локального проекта (относительно <c>api/projects/{projectId}/</c>).</summary>
    public const string AttachmentsRoute = "agent/attachments";

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
