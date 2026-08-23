using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiHomeDesktop.Core.Protocol;

// Формы сообщений канала. Имена свойств повторяют серверные записи один в один: и хаб, и
// MVC сериализуют camelCase'ом, поэтому расходиться им нельзя даже в мелочи.

/// <summary>
/// Команда устройству. Исполнение по ней НЕ начинается: клиент подтверждает приём (Ack),
/// спрашивает человека и ждёт встречного Go.
/// </summary>
public sealed record DesktopCallCommand(
    int ProtocolVersion,
    string CallId,
    string Kind,
    JsonElement? Args,
    int DeadlineSeconds,
    bool RequiresConfirmation,
    int ConfirmationWaitMinutes,
    string SessionId,
    string? ChatName,
    long IssuedAt)
{
    /// <summary>Дедлайн исполнения: число сервера, а при пустом — дефолт протокола по виду вызова.</summary>
    public TimeSpan Deadline => DeadlineSeconds > 0
        ? TimeSpan.FromSeconds(DeadlineSeconds)
        : DesktopProtocol.DeadlineFor(Kind);
}

/// <summary>Встречный go: с этого момента идут часы дедлайна исполнения.</summary>
public sealed record DesktopGoCommand(string CallId, int DeadlineSeconds);

/// <summary>Отмена: гасит ожидание и невыполненные шаги. Уже отправленный ввод не откатывается.</summary>
public sealed record DesktopCancelCommand(string CallId, string Reason);

/// <summary>
/// Представление устройства при подключении: версия протокола и поддерживаемые типы шагов
/// (сервер состав не додумывает — устройство объявляет его само).
/// </summary>
public sealed record DeviceHello(
    int ProtocolVersion,
    IReadOnlyList<string>? SupportedSteps,
    string? ClientVersion);

/// <summary>Ответ сервера на Hello: его версия протокола и потолки.</summary>
public sealed record DeviceHelloAck(
    int ProtocolVersion,
    int AckTimeoutSeconds,
    int MaxResultBytes,
    int MaxBatchSteps);

/// <summary>
/// Тело результата вызова: уезжает POST'ом на /api/devices/calls/{callId}/result мимо
/// 32-КБ лимита сообщения хаба.
///
/// LastAppliedStep обязателен по смыслу и возвращается В ЛЮБОМ исходе: -1 — неизвестно,
/// 0 — ни один шаг не применён, N — применён N-й шаг батча (нумерация с единицы).
/// </summary>
public sealed record DeviceCallResultBody(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("lastAppliedStep")] int LastAppliedStep,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("partial")] bool Partial = false,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null,
    [property: JsonPropertyName("awaitMinutes")] int? AwaitMinutes = null)
{
    /// <summary>Исход без применённых шагов — самый частый отказ клиента.</summary>
    public static DeviceCallResultBody Refused(string outcome, string message) =>
        new(outcome, 0, message);
}

/// <summary>
/// Результат вызова так, как его отдаёт сервер по GET /api/devices/calls/{callId}: тем же
/// телом плюс callId. Нужен на реконнекте — сверить, доехал ли результат из журнала.
/// </summary>
public sealed record DesktopCallResultView(
    string CallId,
    string Outcome,
    int LastAppliedStep,
    string? Message = null,
    bool Partial = false,
    JsonElement? Payload = null,
    int? AwaitMinutes = null);

/// <summary>Заявка чата на сеанс рук: имя чата, проекта и персоны — по ним человек и выбирает.</summary>
public sealed record DesktopHandsRequestView(
    string ChatSessionId,
    string? Chat,
    string? Project,
    string? Persona,
    DateTimeOffset RequestedAt);

/// <summary>Текущий сеанс рук этого устройства (null — руки никому не отданы).</summary>
public sealed record DesktopHandsSessionView(
    string ChatSessionId,
    string? Chat,
    string? Device,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? IdleDeadlineAt,
    DateTimeOffset? HardDeadlineAt);

/// <summary>Поводы остановки сеанса, которые называет клиент (жизнь в трее закрытием НЕ считается).</summary>
public static class DesktopHandsStopReasons
{
    /// <summary>Человек нажал «Стоп».</summary>
    public const string Stopped = "stopped";

    /// <summary>Окно оболочки закрыто.</summary>
    public const string ClientClosed = "client_closed";
}

/// <summary>Учётные данные устройства, выданные при сопряжении. Хранятся под DPAPI CurrentUser.</summary>
public sealed record DeviceCredentials(
    string ServerUrl,
    string DeviceId,
    string DeviceName,
    string DeviceToken,
    int TokenVersion,
    string Fingerprint);
