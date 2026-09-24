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
    /// сама схема регистрируется в слое авторизации устройств. Источник правды —
    /// здесь: вертикаль Desktop ссылается на этот литерал, а не наоборот (иначе
    /// контракт WS-канала зависел бы от вертикали, что ломает Ф3 Этапа 5).
    /// </summary>
    public const string DeviceTokenScheme = "DesktopDevice";

    // Claims токена устройства. Имена НЕ свои: их выдаёт сторона авторизации
    // (DesktopDeviceAuthHandler), и разъехавшиеся литералы означали бы пустого владельца
    // в хабе при формально успешной проверке токена. Источник правды — здесь,
    // `DesktopDeviceAuthHandler` ссылается на эти литералы, а не объявляет свои.
    // Claim чата (sid) сюда НЕ выносим: его читает только DesktopCaller.FromPrincipal —
    // единственная точка разбора capability-токена, а висящий алиас читался бы как
    // отдельная проверка чата.
    public const string OwnerIdClaim = "sub";
    public const string DeviceIdClaim = "did";

    /// <summary>
    /// Audience capability-токена канала (ADR-008, «Авторизация канала»). Отдельный от
    /// "ClaudeHomeServer" — в этом весь смысл: сервисный JWT владельца /api/devices/* не
    /// открывает. Живёт здесь, а не у выдающей стороны: литерал сверяют обе стороны —
    /// и выдача (JwtService в Main), и схема DesktopCapability в вертикали.
    /// </summary>
    public const string CapabilityAudience = "desktop";

    /// <summary>
    /// TTL capability-токена — минуты: сторона доверия здесь физическая машина владельца,
    /// а конфиг хода лежит в общем /turn-tmp песочницы (принятый остаточный риск ADR-008).
    /// Короткий срок сужает окно, поэтому токен обновляется на каждом запуске хода, а не
    /// живёт днями. Читают обе стороны: выдача и кеш токенов чата.
    /// </summary>
    public static readonly TimeSpan CapabilityTokenLifetime = TimeSpan.FromMinutes(10);

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

/// <summary>
/// Ответ на Hello: версия сервера и потолки протокола. Поля агента локальных проектов
/// (ADR-016) — аддитивные: клиент рук ADR-008 их не читает.
/// <see cref="RequiredCliVersion"/> — версия управляемой копии CLI, которую агент обязан
/// держать (null — сервер её не задал); <see cref="HarnessReady"/> и
/// <see cref="HarnessProblem"/> — вердикт сервера по объявленной копии. Поставив нужную
/// версию, агент повторяет Hello — вердикт пересчитывается.
/// </summary>
public sealed record DeviceHelloAck(
    int ProtocolVersion,
    int AckTimeoutSeconds,
    int MaxResultBytes,
    int MaxBatchSteps,
    string? RequiredCliVersion = null,
    bool HarnessReady = false,
    string? HarnessProblem = null,
    int ExecProtocolVersion = DeviceExecProtocol.Version);

/// <summary>
/// Открыть канал исполнения: устройство отвечает WebSocket-подключением на
/// /api/devices/exec?execId={ExecId}. Что именно запускать, едет первым кадром
/// <see cref="DeviceExecFrameChannel.Control"/> уже по самому каналу.
/// </summary>
public sealed record DeviceExecOpenCommand(string ExecId, int ExecProtocolVersion);

// ---------- устройство → сервер ----------

/// <summary>
/// Представление устройства при подключении: версия протокола и поддерживаемые типы шагов
/// (сервер не додумывает состав — устройство объявляет его само).
///
/// Хвост — агент локальных проектов (ADR-016): признак агента — непустой
/// <see cref="AgentVersion"/>; клиент рук ADR-008 эти поля не шлёт, и сохранённые
/// сведения об агенте его Hello не затирает. <see cref="CliVersion"/> — версия
/// УПРАВЛЯЕМОЙ копии CLI в каталоге агента (null — копии нет), а не CLI из PATH.
/// </summary>
public sealed record DeviceHello(
    int ProtocolVersion,
    IReadOnlyList<string>? SupportedSteps,
    string? ClientVersion,
    string? Platform = null,
    string? AgentVersion = null,
    string? CliVersion = null,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// Возможности агента устройства (ADR-016). Устройство объявляет их само; незнакомые
/// значения сервер не хранит.
/// </summary>
public static class DeviceCapabilities
{
    /// <summary>Исполнение процессов (CLI хода) через канал /api/devices/exec.</summary>
    public const string Exec = "exec";

    /// <summary>Файловые подсистемы на localhost (этап 4).</summary>
    public const string Files = "files";

    /// <summary>Ретранслятор чтения для других устройств (этап 5).</summary>
    public const string Relay = "relay";

    public static readonly IReadOnlyList<string> All = [Exec, Files, Relay];

    /// <summary>Только известные значения, без дублей, в порядке <see cref="All"/>.</summary>
    public static List<string> Normalize(IEnumerable<string>? declared)
    {
        var set = new HashSet<string>(
            (declared ?? []).Select(c => (c ?? "").Trim().ToLowerInvariant()), StringComparer.Ordinal);
        return All.Where(set.Contains).ToList();
    }
}

// ---------- канал исполнения /api/devices/exec (ADR-016) ----------

/// <summary>
/// Протокол потокового канала исполнения. Версия своя, отдельная от версии хаба: хаб — канал
/// управления, и его клиент рук ADR-008 о кадрах exec не знает. Устройство называет версию
/// заголовком <see cref="VersionHeader"/> при подключении; несовместимая — явный отказ 400
/// с кодом <see cref="UnsupportedVersionError"/> до апгрейда WebSocket.
/// </summary>
public static class DeviceExecProtocol
{
    public const int Version = 1;

    public const int MinClientVersion = 1;

    public const string Path = "/api/devices/exec";

    public const string VersionHeader = "X-Device-Exec-Protocol";

    public const string UnsupportedVersionError = "unsupported_exec_protocol";

    /// <summary>
    /// Заголовок кадра: канал (1 байт), номер (8 байт BE), длина (4 байта BE).
    /// Спайк нёс [канал][длина][данные]; номер добавлен ради досылки после реконнекта.
    /// </summary>
    public const int HeaderBytes = 1 + 8 + 4;

    /// <summary>Потолок данных одного кадра: крупный вывод режется отправителем на части.</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>
    /// Потолок неподтверждённого вывода одного исполнения: дальше отправитель ждёт
    /// подтверждений, а не растит буфер досылки без предела.
    /// </summary>
    public const long MaxUnackedBytes = 32L * 1024 * 1024;

    /// <summary>Сколько сервер ждёт подключения устройства к каналу после команды открытия.</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    public static bool IsSupportedClientVersion(int version) =>
        version >= MinClientVersion && version <= Version;
}

/// <summary>
/// Логический канал кадра. Номера каналов данных — как в спайке
/// (tools/local-projects-spike/frames.mjs); <see cref="Ack"/> добавлен для досылки.
/// </summary>
public enum DeviceExecFrameChannel : byte
{
    Stdin = 0,
    Stdout = 1,
    Stderr = 2,
    Exit = 3,
    StdinEof = 4,
    Info = 5,
    Control = 9,

    /// <summary>
    /// Подтверждение: номер кадра — последний принятый подряд номер встречной стороны
    /// (кумулятивно), данных нет. Сами подтверждения не нумеруются и не подтверждаются.
    /// </summary>
    Ack = 10,
}

/// <summary>Кадр канала исполнения. Номера данных идут с 1 подряд, у каждой стороны свои.</summary>
public readonly record struct DeviceExecFrame(DeviceExecFrameChannel Channel, ulong Sequence, ReadOnlyMemory<byte> Payload);

/// <summary>Кадровка [канал:1][номер:8 BE][длина:4 BE][данные]. Одно WS-сообщение — один кадр.</summary>
public static class DeviceExecFrames
{
    public static byte[] Encode(DeviceExecFrameChannel channel, ulong sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > DeviceExecProtocol.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "Кадр длиннее потолка канала исполнения");

        var buffer = new byte[DeviceExecProtocol.HeaderBytes + payload.Length];
        buffer[0] = (byte)channel;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1, 8), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(9, 4), (uint)payload.Length);
        payload.CopyTo(buffer.AsSpan(DeviceExecProtocol.HeaderBytes));
        return buffer;
    }

    public static byte[] Ack(ulong sequence) => Encode(DeviceExecFrameChannel.Ack, sequence, []);

    /// <summary>
    /// Разбор ровно одного кадра. Ложь — битый кадр: неизвестный канал, длина не совпала с
    /// данными, данные сверх потолка, ненулевые данные у подтверждения.
    /// </summary>
    public static bool TryDecode(ReadOnlyMemory<byte> message, out DeviceExecFrame frame)
    {
        frame = default;
        if (message.Length < DeviceExecProtocol.HeaderBytes) return false;

        var span = message.Span;
        var channel = (DeviceExecFrameChannel)span[0];
        if (!Enum.IsDefined(channel)) return false;

        var sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(span.Slice(1, 8));
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(9, 4));
        if (length > DeviceExecProtocol.MaxPayloadBytes) return false;
        if (message.Length - DeviceExecProtocol.HeaderBytes != length) return false;
        if (channel == DeviceExecFrameChannel.Ack && length != 0) return false;

        frame = new DeviceExecFrame(channel, sequence, message[DeviceExecProtocol.HeaderBytes..]);
        return true;
    }
}

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
