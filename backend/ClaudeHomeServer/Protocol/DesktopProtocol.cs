using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeHomeServer.Protocol;

/// <summary>
/// Протокол канала десктопного агента — ADR-008, раздел «Протокол канала».
/// Одна точка правды по версии протокола, генерации callId, дедлайнам фаз, потолкам и
/// составу исходов: те же числа читает клиент устройства (вторая волна) и MCP-сервер.
/// </summary>
public static class DesktopProtocol
{
    /// <summary>Версия протокола сервера. Объявляется явно — устройство присылает свою в Hello.</summary>
    public const int Version = 1;

    /// <summary>Минимальная версия клиента, которую сервер согласен обслуживать.</summary>
    public const int MinClientVersion = 1;

    /// <summary>
    /// Схема авторизации канала устройств. /api/devices/* и /hubs/devices НЕ принимают
    /// дефолтную JwtBearer и сервисный JWT владельца (ADR-008, «Авторизация канала»);
    /// сама схема регистрируется в слое авторизации устройств.
    /// </summary>
    public const string DeviceTokenScheme = "device-token";

    // Claims токена устройства/capability-токена. Значения — контракт с выдающей стороной.
    public const string OwnerIdClaim = "ownerId";
    public const string DeviceIdClaim = "deviceId";
    public const string SessionIdClaim = "sessionId";

    /// <summary>Ack на команду: нет за 2 с — честная ошибка, а не висение до таймаута MCP.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ожидание человека разведено с дедлайном исполнения: пока висит тост подтверждения,
    /// часы исполнения не идут, а ожидание меряется минутами.
    /// </summary>
    public static readonly TimeSpan DefaultConfirmationWait = TimeSpan.FromMinutes(3);

    /// <summary>Потолок ожидания человека, сколько бы минут ни попросило устройство.</summary>
    public static readonly TimeSpan MaxConfirmationWait = TimeSpan.FromMinutes(10);

    /// <summary>Потолок тела результата (~8 МБ) — это лимит HTTP, а не лимит кадра.</summary>
    public const int MaxResultBytes = 8 * 1024 * 1024;

    /// <summary>Потолок шагов в одном батче desktop_act.</summary>
    public const int MaxBatchSteps = 10;

    /// <summary>Сколько держим завершённый вызов, чтобы клиент забрал результат при реконнекте.</summary>
    public static readonly TimeSpan ResultRetention = TimeSpan.FromMinutes(15);

    /// <summary>callId — 128 бит случайности, генерирует бэкенд (устройство своих не придумывает).</summary>
    public static string NewCallId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Совместима ли версия клиента с сервером.</summary>
    public static bool IsSupportedClientVersion(int version) =>
        version >= MinClientVersion && version <= Version;

    /// <summary>
    /// Дедлайн исполнения ПОСЛЕ встречного go (ADR: screen 15 с, ui 20 с, act 30 с, run 120 с).
    /// desktop_open отдельного числа в ADR не имеет — по поведению он ближе к act.
    /// </summary>
    public static TimeSpan DeadlineFor(string kind) => kind switch
    {
        DesktopCallKinds.Screen => TimeSpan.FromSeconds(15),
        DesktopCallKinds.Ui => TimeSpan.FromSeconds(20),
        DesktopCallKinds.Act => TimeSpan.FromSeconds(30),
        DesktopCallKinds.Open => TimeSpan.FromSeconds(30),
        DesktopCallKinds.Run => TimeSpan.FromSeconds(120),
        _ => TimeSpan.FromSeconds(30)
    };
}

/// <summary>Виды вызовов, которые уезжают на устройство (desktop_devices обслуживает сервер).</summary>
public static class DesktopCallKinds
{
    public const string Screen = "screen";
    public const string Ui = "ui";
    public const string Act = "act";
    public const string Open = "open";
    public const string Run = "run";

    public static readonly IReadOnlyList<string> All = [Screen, Ui, Act, Open, Run];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// Исходы вызова. Явный исход вместо тихого no-op — требование ADR: модель обязана
/// понимать, что произошло, и не строить догадок.
/// </summary>
public static class DesktopOutcomes
{
    /// <summary>Вызов исполнен.</summary>
    public const string Ok = "ok";

    // --- исходы устройства (ADR, «Протокол канала») ---
    public const string SessionLocked = "session_locked";
    public const string SecureDesktop = "secure_desktop";
    public const string TargetElevated = "target_elevated";
    public const string InputBlocked = "input_blocked";
    public const string SelfTargetDenied = "self_target_denied";
    public const string WindowNotAvailable = "window_not_available";
    public const string WindowMinimized = "window_minimized";

    /// <summary>Чем кончилось — неизвестно. Формулировка НЕ содержит подсказки «повтори».</summary>
    public const string Unknown = "unknown";

    /// <summary>Снапшот, на который ссылается вызов, устарел.</summary>
    public const string SnapshotStale = "snapshot_stale";

    /// <summary>Шаг применён, но адресной улики не нашлось; повтор запрещён.</summary>
    public const string AppliedUnverified = "applied_unverified";

    /// <summary>Видимых изменений не произошло; повтор запрещён.</summary>
    public const string NoVisibleChange = "no_visible_change";

    // --- исходы, которые ставит сам бэкенд ---
    /// <summary>Человек ещё не подтвердил: ожидание меряется минутами, не дедлайном исполнения.</summary>
    public const string AwaitingConfirmation = "awaiting_confirmation";

    /// <summary>Человек отказал.</summary>
    public const string Denied = "denied";

    /// <summary>Устройство не подтвердило приём команды за 2 с.</summary>
    public const string NoAck = "no_ack";

    /// <summary>Устройство не на связи.</summary>
    public const string DeviceOffline = "device_offline";

    /// <summary>Дедлайн исполнения после go истёк.</summary>
    public const string DeadlineExceeded = "deadline_exceeded";

    /// <summary>Вызов отменён (interrupt пользователя, погасший сеанс, выключенная грань).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Отказ протокола: неизвестный вид вызова, битые аргументы, канал не принял команду.</summary>
    public const string ProtocolError = "protocol_error";

    /// <summary>Исходы, которые устройство вправе прислать в результате.</summary>
    public static readonly IReadOnlySet<string> FromDevice = new HashSet<string>(StringComparer.Ordinal)
    {
        Ok, SessionLocked, SecureDesktop, TargetElevated, InputBlocked, SelfTargetDenied,
        WindowNotAvailable, WindowMinimized, Unknown, SnapshotStale, AppliedUnverified,
        NoVisibleChange, Denied, Cancelled, DeadlineExceeded,
        // Устройство вправе само сообщить, что человек не отвечает, не дожидаясь окна сервера
        AwaitingConfirmation
    };
}

/// <summary>
/// Человеческие формулировки исходов, которые ставит бэкенд. Текст устройства (если пришёл)
/// имеет приоритет — здесь честный дефолт. Правило ADR: у unknown нет подсказки «повтори»,
/// авто-ретраев в этой грани нет нигде, клик и ввод не идемпотентны.
/// </summary>
public static class DesktopOutcomeText
{
    public static string For(string outcome, string? deviceName = null, int? waitMinutes = null)
    {
        var device = string.IsNullOrWhiteSpace(deviceName) ? "устройство" : $"устройство {deviceName}";
        return outcome switch
        {
            DesktopOutcomes.DeviceOffline => $"{Cap(device)} офлайн — команда не отправлена.",
            DesktopOutcomes.NoAck => $"{Cap(device)} не подтвердило приём команды за 2 секунды; ни один шаг не применён.",
            DesktopOutcomes.AwaitingConfirmation => waitMinutes is > 0
                ? $"Действие ждёт подтверждения человека на устройстве; ждали {waitMinutes} мин, ответа пока нет."
                : "Действие ждёт подтверждения человека на устройстве, ответа пока нет.",
            DesktopOutcomes.Denied => "Человек отклонил действие на устройстве.",
            DesktopOutcomes.DeadlineExceeded => "Дедлайн исполнения истёк; устройство результат не прислало.",
            DesktopOutcomes.Cancelled => "Вызов отменён.",
            // Ровно то, что произошло, и ни слова о повторе.
            DesktopOutcomes.Unknown => "Связь с устройством оборвалась во время вызова; чем он закончился — неизвестно.",
            DesktopOutcomes.ProtocolError => "Канал устройства не принял команду.",
            _ => outcome
        };
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

// ---------- сервер → устройство ----------

/// <summary>
/// Команда устройству. Исполнение не начинается по ней: устройство подтверждает приём
/// (Ack), спрашивает человека и ждёт встречного go.
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
    long IssuedAt);

/// <summary>Встречный go: с этого момента идут часы дедлайна исполнения.</summary>
public sealed record DesktopGoCommand(string CallId, int DeadlineSeconds);

/// <summary>Отмена: гасит ожидание и невыполненные шаги; уже отправленный ввод не откатывается.</summary>
public sealed record DesktopCancelCommand(string CallId, string Reason);

/// <summary>Ответ на Hello: версия сервера и потолки протокола.</summary>
public sealed record DeviceHelloAck(
    int ProtocolVersion,
    int AckTimeoutSeconds,
    int MaxResultBytes,
    int MaxBatchSteps);

// ---------- устройство → сервер ----------

/// <summary>
/// Представление устройства при подключении: версия протокола и поддерживаемые типы шагов
/// (сервер не додумывает состав — устройство объявляет его само).
/// </summary>
public sealed record DeviceHello(
    int ProtocolVersion,
    IReadOnlyList<string>? SupportedSteps,
    string? ClientVersion);

/// <summary>
/// Результат вызова. Приезжает HTTP-POST'ом мимо 32-КБ лимита сообщения хаба.
/// LastAppliedStep возвращается В ЛЮБОМ исходе: -1 — неизвестно, 0 — ни один шаг не применён,
/// N — применён N-й шаг батча (нумерация с единицы).
/// </summary>
public sealed record DesktopCallResult(
    string CallId,
    string Outcome,
    int LastAppliedStep,
    string? Message = null,
    bool Partial = false,
    JsonElement? Payload = null,
    int? AwaitMinutes = null)
{
    public static DesktopCallResult Server(string callId, string outcome, int lastAppliedStep,
        string? deviceName = null, int? waitMinutes = null) =>
        new(callId, outcome, lastAppliedStep,
            DesktopOutcomeText.For(outcome, deviceName, waitMinutes),
            AwaitMinutes: waitMinutes);
}
