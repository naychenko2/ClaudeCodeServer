namespace ClaudeHomeServer.Protocol;

/// <summary>
/// Протокол хаба устройств (/hubs/devices): версия, схема авторизации и claims токена
/// устройства. Заведён ADR-008, сегодня это канал управления агента локальных проектов
/// (ADR-016); фазы вызова рук ADR-008 удалены вместе с десктопным клиентом.
/// </summary>
public static class DesktopProtocol
{
    /// <summary>Версия протокола сервера. Объявляется явно — устройство присылает свою в Hello.</summary>
    public const int Version = 1;

    /// <summary>Минимальная версия клиента, которую сервер согласен обслуживать.</summary>
    public const int MinClientVersion = 1;

    /// <summary>
    /// Схема авторизации канала устройств. /api/devices/* и /hubs/devices НЕ принимают
    /// дефолтную JwtBearer и сервисный JWT владельца; сама схема регистрируется в слое
    /// авторизации устройств. Источник правды — здесь: вертикаль Desktop ссылается на этот
    /// литерал, а не наоборот (иначе контракт WS-канала зависел бы от вертикали, что ломает
    /// Ф3 Этапа 5).
    /// </summary>
    public const string DeviceTokenScheme = "DesktopDevice";

    // Claims токена устройства. Имена НЕ свои: их выдаёт сторона авторизации
    // (DeviceAuthHandler), и разъехавшиеся литералы означали бы пустого владельца
    // в хабе при формально успешной проверке токена. Источник правды — здесь,
    // `DeviceAuthHandler` ссылается на эти литералы, а не объявляет свои.
    public const string OwnerIdClaim = "sub";
    public const string DeviceIdClaim = "did";

    // Три числа ниже — только поля ответа на Hello (DeviceHelloAck). Фаз вызова, которыми
    // они управляли, больше нет, но поля оставлены, чтобы не менять формат ответа Hello
    // для уже установленных агентов (SignalR сериализует JSON по именам, агент их не читает).

    /// <summary>Ack на команду (поле ответа Hello).</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Потолок тела результата вызова (поле ответа Hello).</summary>
    public const int MaxResultBytes = 8 * 1024 * 1024;

    /// <summary>Потолок шагов в одном батче (поле ответа Hello).</summary>
    public const int MaxBatchSteps = 10;

    /// <summary>Совместима ли версия клиента с сервером.</summary>
    public static bool IsSupportedClientVersion(int version) =>
        version >= MinClientVersion && version <= Version;
}

// ---------- сервер → устройство ----------

/// <summary>
/// Ответ на Hello: версия сервера и потолки протокола (первые четыре поля остались от рук
/// ADR-008 и сохранены, чтобы не менять формат ответа Hello для уже установленных агентов: SignalR сериализует JSON по именам, агент этих полей не читает). Поля агента локальных проектов (ADR-016)
/// — аддитивные.
/// <see cref="RequiredCliVersion"/> — версия управляемой копии CLI, которую агент обязан
/// держать (null — сервер её не задал); <see cref="HarnessReady"/> и
/// <see cref="HarnessProblem"/> — вердикт сервера по объявленной копии. Поставив нужную
/// версию, агент повторяет Hello — вердикт пересчитывается.
///
/// Раздача агента (agent-distribution Р9): <see cref="AgentLatestVersion"/> — версия текущей
/// выкатки (null — сервер агента не раздаёт), <see cref="AgentMinVersion"/> — минимальная
/// совместимая. Хеш, размер и путь архива (относительно <c>/agent/</c>) — под RID из Hello;
/// архива под этот RID нет — поля пустые. Хеш едет по аутентифицированному каналу
/// устройства, сам архив агент качает анонимной ручкой. Мост рук отдельного архива не имеет:
/// он едет внутри архива агента под win-x64.
/// </summary>
public sealed record DeviceHelloAck(
    int ProtocolVersion,
    int AckTimeoutSeconds,
    int MaxResultBytes,
    int MaxBatchSteps,
    string? RequiredCliVersion = null,
    bool HarnessReady = false,
    string? HarnessProblem = null,
    int ExecProtocolVersion = DeviceExecProtocol.Version,
    string? AgentLatestVersion = null,
    string? AgentMinVersion = null,
    string? AgentArchiveSha256 = null,
    long? AgentArchiveSize = null,
    string? AgentArchivePath = null);

/// <summary>
/// Открыть канал исполнения: устройство отвечает WebSocket-подключением на
/// /api/devices/exec?execId={ExecId}. Что именно запускать, едет первым кадром
/// <see cref="DeviceExecFrameChannel.Control"/> уже по самому каналу.
/// <see cref="Purpose"/> — зачем открыт канал: null — ход CLI, <see cref="DeviceExecPurposes.Relay"/>
/// — запрос ретранслятора чтения (ADR-016 §5). Назначение выбирает обработчик на агенте ДО
/// первого кадра: канал ретранслятора ход не запустит, канал хода чтение не исполнит.
/// </summary>
public sealed record DeviceExecOpenCommand(string ExecId, int ExecProtocolVersion, string? Purpose = null);

/// <summary>Назначения канала исполнения, кроме хода CLI (у него назначение null).</summary>
public static class DeviceExecPurposes
{
    /// <summary>Ретранслятор чтения для других устройств: протокол — <see cref="RelayProtocol"/>.</summary>
    public const string Relay = "relay";

    /// <summary>
    /// Выдача папки проекта (решение владельца 2026-09-27, ADR-016 §5): агент создаёт папку и
    /// добавляет её в корни машины. Отдельно от ретранслятора: тот не пишет по построению (G6).
    /// Протокол — <see cref="BindFolderProtocol"/>.
    /// </summary>
    public const string BindFolder = BindFolderProtocol.Operation;
}

// ---------- устройство → сервер ----------

/// <summary>
/// Представление устройства при подключении: версия протокола и поддерживаемые типы шагов
/// (сервер не додумывает состав — устройство объявляет его само).
///
/// Хвост — агент локальных проектов (ADR-016): признак агента — непустой
/// <see cref="AgentVersion"/>; Hello без этих полей сохранённые сведения об агенте не
/// затирает. <see cref="CliVersion"/> — версия
/// УПРАВЛЯЕМОЙ копии CLI в каталоге агента (null — копии нет), а не CLI из PATH.
/// <see cref="Rid"/> — RID сборки агента (<c>win-x64</c>, <c>linux-x64</c>): под него сервер
/// выбирает архив обновления. <see cref="AgentUpdate"/> — состояние самообновления.
/// </summary>
public sealed record DeviceHello(
    int ProtocolVersion,
    IReadOnlyList<string>? SupportedSteps,
    string? ClientVersion,
    string? Platform = null,
    string? AgentVersion = null,
    string? CliVersion = null,
    IReadOnlyList<string>? Capabilities = null,
    string? Rid = null,
    DeviceAgentUpdate? AgentUpdate = null);

/// <summary>
/// Состояние самообновления агента (agent-distribution AD-3/AD-5): <see cref="State"/> — одно
/// из <see cref="DeviceAgentUpdateStates"/>, <see cref="TargetVersion"/> — версия, к которой
/// идёт обновление, <see cref="Reason"/> — причина ожидания или провала, текстом для человека.
/// </summary>
public sealed record DeviceAgentUpdate(string State, string? TargetVersion = null, string? Reason = null)
{
    /// <summary>Потолок длины причины: текст приходит с устройства и уходит в стор и в UI.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Только известное состояние, версия — только разбираемая, причина — обрезанная.
    /// Незнакомое состояние — null: сервер не хранит того, чего не понимает.
    /// </summary>
    public static DeviceAgentUpdate? Normalize(DeviceAgentUpdate? declared)
    {
        if (declared is null) return null;
        var state = (declared.State ?? "").Trim().ToLowerInvariant();
        if (!DeviceAgentUpdateStates.All.Contains(state)) return null;

        var target = DeviceAgentVersion.TryParse(declared.TargetVersion?.Trim(), out var v) ? v.ToString() : null;
        var reason = string.IsNullOrWhiteSpace(declared.Reason) ? null : declared.Reason.Trim();
        if (reason is { Length: > MaxReasonLength }) reason = reason[..MaxReasonLength];
        return new DeviceAgentUpdate(state, target, reason);
    }
}

/// <summary>Состояния самообновления агента.</summary>
public static class DeviceAgentUpdateStates
{
    /// <summary>Обновлять нечего или обновление не начиналось.</summary>
    public const string Idle = "idle";

    /// <summary>Архив новой версии скачивается и проверяется.</summary>
    public const string Downloading = "downloading";

    /// <summary>Новая версия готова, переключение ждёт конца работы (ход, терминал…).</summary>
    public const string WaitingIdle = "waiting-idle";

    /// <summary>Обновление не удалось, причина — в <see cref="DeviceAgentUpdate.Reason"/>.</summary>
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Idle, Downloading, WaitingIdle, Failed];
}

/// <summary>
/// RID-ы, под которые сервер раздаёт агента (agent-distribution Р11). Белый список: строка
/// из запроса или из Hello, которой здесь нет, до каталога релизов не доходит.
/// </summary>
public static class DeviceAgentRids
{
    public const string WinX64 = "win-x64";
    public const string LinuxX64 = "linux-x64";

    public static readonly IReadOnlyList<string> Supported = [WinX64, LinuxX64];

    public static bool IsSupported(string? rid) => rid is not null && Supported.Contains(rid, StringComparer.Ordinal);
}

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

    /// <summary>
    /// Руки (ADR-016, раздел «Руки»): мост <c>HandsBridge</c> лежит в каталоге версии агента — его
    /// привозит архив агента под Windows. Агент старой версии возможность не объявляет. Сеанса на
    /// машине нет (решение владельца 1в): объявленная возможность и тумблер проекта — всё, что
    /// нужно ходу.
    /// </summary>
    public const string Hands = "hands";

    /// <summary>Выдача папки проекта (<see cref="DeviceExecPurposes.BindFolder"/>); старый агент её не объявляет.</summary>
    public const string BindFolder = "bind-folder";

    public static readonly IReadOnlyList<string> All = [Exec, Files, Relay, Hands, BindFolder];

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
/// управления, кадры exec идут мимо него. Устройство называет версию
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

    /// <summary>
    /// Потолок простоя исполнения без соединения: столько обе стороны ждут реконнекта,
    /// дальше исполнение считается оборванным (агент гасит CLI, сервер закрывает поток).
    /// </summary>
    public static readonly TimeSpan MaxOutage = TimeSpan.FromMinutes(10);

    public static bool IsSupportedClientVersion(int version) =>
        version >= MinClientVersion && version <= Version;

    /// <summary>execId — 128 бит случайности, генерирует бэкенд (устройство своих не придумывает).</summary>
    public static string NewExecId() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
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
