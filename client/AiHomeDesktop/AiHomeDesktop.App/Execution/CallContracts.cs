using System.Text.Json;

namespace AiHomeDesktop.App.Execution;

/// <summary>
/// Команда сервера устройству (зеркало <c>DesktopCallCommand</c> из
/// <c>backend/ClaudeHomeServer/Protocol/DesktopProtocol.cs</c>). Числа и имена полей —
/// часть протокола: менять их в одиночку нельзя, источник правды на сервере.
/// </summary>
public sealed record DesktopCall(
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
    /// <summary>Имя чата для человека. Пустым не бывает: в тосте оно обязано быть всегда.</summary>
    public string ChatTitle => string.IsNullOrWhiteSpace(ChatName) ? SessionId : ChatName!;
}

/// <summary>
/// Исход вызова, который устройство отдаёт серверу POST'ом. Индекс последнего применённого
/// шага возвращается В ЛЮБОМ исходе: -1 — неизвестно, 0 — ни один шаг не применён.
/// </summary>
public sealed record DesktopCallOutcome(
    string Outcome,
    int LastAppliedStep,
    string? Message = null,
    bool Partial = false,
    JsonElement? Payload = null,
    int? AwaitMinutes = null);

/// <summary>Виды вызовов канала. Имена совпадают с серверными: <c>screen | ui | act | open | run</c>.</summary>
public static class DesktopCallKinds
{
    public const string Screen = "screen";
    public const string Ui = "ui";
    public const string Act = "act";
    public const string Open = "open";
    public const string Run = "run";
}

/// <summary>
/// Исходы, которые вправе прислать устройство. Список сервера шире — здесь то, что реально
/// ставит склейка вызова; исходы самой машины (окно свёрнуто, экран заблокирован) приходят
/// строкой от исполнителя и передаются как есть.
/// </summary>
public static class DesktopOutcomes
{
    public const string Ok = "ok";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
    public const string DeadlineExceeded = "deadline_exceeded";

    /// <summary>
    /// Чем кончилось — неизвестно. Формулировки «повтори» рядом с ним быть не должно:
    /// авто-ретраев в этой грани нет нигде.
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>Что этот клиент умеет и на какой версии протокола говорит.</summary>
public static class ClientProtocol
{
    /// <summary>Версия протокола, которую клиент объявляет в Hello.</summary>
    public const int Version = 1;

    /// <summary>Ack за 2 секунды — иначе сервер закроет вызов исходом <c>no_ack</c>.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Потолок ожидания человека у сервера — больше просить бессмысленно.</summary>
    public const int MaxConfirmationWaitMinutes = 10;

    /// <summary>
    /// Грань этой версии клиента — ровно два инструмента. Состав <c>tools/list</c> от этого
    /// НЕ зависит: неподдержанный вид вызова получает честный исход, а не исчезает из списка.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedKinds = [DesktopCallKinds.Screen, DesktopCallKinds.Open];

    public static bool Supports(string kind) => SupportedKinds.Contains(kind);

    /// <summary>Текст отказа для неподдержанного вида вызова — без подсказки «повтори».</summary>
    public static string UnsupportedMessage(string kind) =>
        $"Клиент устройства версии {Version} не умеет вызов «{kind}»: в этой версии есть только кадр экрана "
        + "(desktop_screen) и открытие приложения, файла или ссылки (desktop_open). "
        + "Повторять вызов бессмысленно — состав не изменится до обновления клиента.";
}

/// <summary>
/// Донесения устройства серверу по каналу хаба (<c>Hubs/DeviceHub</c>). Реализация —
/// SignalR-соединение клиента; здесь только то, чем пользуется склейка вызова.
/// </summary>
public interface IDeviceChannelClient
{
    /// <summary>Приём команды подтверждён. Не успели за 2 с — вызов кончится <c>no_ack</c>.</summary>
    Task AckAsync(string callId, CancellationToken ct = default);

    /// <summary>Разговор с человеком затянулся: просим у сервера минут (в пределах потолка).</summary>
    Task AwaitingAsync(string callId, int minutes, CancellationToken ct = default);

    /// <summary>Человек подтвердил — сервер ответит встречным Go.</summary>
    Task ConfirmAsync(string callId, CancellationToken ct = default);

    /// <summary>Человек отклонил — отказ уходит модели текстом, результат мы не шлём.</summary>
    Task DeclineAsync(string callId, CancellationToken ct = default);
}

/// <summary>Чем кончилась отдача результата серверу.</summary>
public enum CallResultDelivery
{
    /// <summary>Результат принят.</summary>
    Accepted,

    /// <summary>Дубль: результат по этому callId сервер уже принял. Досылать нечего.</summary>
    Duplicate,

    /// <summary>Сервер про такой вызов не знает — он давно закрыт и вытеснен из реестра.</summary>
    UnknownCall,

    /// <summary>Отказ авторизации: пара владелец+устройство не сошлась.</summary>
    Refused,

    /// <summary>Связи нет. Не потеря: результат остаётся в журнале до реконнекта.</summary>
    Unreachable
}

/// <summary>Что сервер знает про вызов (путь реконнекта: GET /api/devices/calls/{callId}).</summary>
public enum ServerResultState
{
    /// <summary>Результат у сервера уже есть.</summary>
    Found,

    /// <summary>Вызов жив, результата сервер ещё не получил.</summary>
    Pending,

    /// <summary>Вызова у сервера нет.</summary>
    UnknownCall,

    /// <summary>Спросить не удалось — связи нет.</summary>
    Unreachable
}

/// <summary>HTTP-половина канала: результат едет мимо 32-КБ лимита сообщения хаба.</summary>
public interface IDeviceCallsApi
{
    /// <summary>POST /api/devices/calls/{callId}/result.</summary>
    Task<CallResultDelivery> PostResultAsync(string callId, DesktopCallOutcome outcome, CancellationToken ct = default);

    /// <summary>GET /api/devices/calls/{callId} — сверка после реконнекта, доехал ли результат.</summary>
    Task<ServerResultState> GetResultStateAsync(string callId, CancellationToken ct = default);
}

/// <summary>
/// Исполнитель грани на самой машине (проект <c>AiHomeDesktop.Windows</c>). Склейка вызова
/// про WinAPI не знает ничего — так её можно гонять тестами на любой платформе.
/// </summary>
public interface IDesktopExecutor
{
    /// <summary>Умеет ли эта сборка такой вид вызова.</summary>
    bool Supports(string kind);

    /// <summary>
    /// Исполнить вызов в пределах отданного токена (дедлайн вида вызова уже наложен).
    /// Исключений наружу лучше не бросать: склейка превратит их в честный исход.
    /// </summary>
    Task<DesktopCallOutcome> ExecuteAsync(DesktopCall call, CancellationToken ct);
}

/// <summary>Запись локального журнала вызовов — по ней после реконнекта досылается результат.</summary>
public sealed record CallJournalEntry(string CallId, string Kind, DateTimeOffset StartedAt, DesktopCallOutcome? Outcome);

/// <summary>
/// Локальный журнал вызовов по callId. Держит две вещи: «этот вызов мы уже видели»
/// (повторно пришедший вызов не исполняется) и «результат готов, но не доехал».
/// </summary>
public interface ICallJournal
{
    /// <summary>Начать вызов. false — callId уже в журнале, то есть это повтор.</summary>
    Task<bool> TryBeginAsync(string callId, string kind, CancellationToken ct = default);

    /// <summary>Записать готовый результат (ещё не отданный серверу).</summary>
    Task CompleteAsync(string callId, DesktopCallOutcome outcome, CancellationToken ct = default);

    /// <summary>Результат доехал (или досылать его больше некуда) — запись закрыта.</summary>
    Task MarkDeliveredAsync(string callId, CancellationToken ct = default);

    /// <summary>Записи, результат которых серверу не отдан.</summary>
    Task<IReadOnlyList<CallJournalEntry>> UndeliveredAsync(CancellationToken ct = default);

    /// <summary>Запись по callId — чтобы на повторную команду ответить тем же исходом.</summary>
    Task<CallJournalEntry?> FindAsync(string callId, CancellationToken ct = default);
}
