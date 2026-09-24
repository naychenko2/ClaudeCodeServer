using System.Text.Json;

namespace ClaudeHomeServer.Protocol;

// ---------- содержимое кадров Control/Exit канала исполнения (ADR-016, задачи 2.2/2.3) ----------

/// <summary>
/// Операции кадра <see cref="DeviceExecFrameChannel.Control"/>. Первый кадр исполнения —
/// всегда <see cref="Spawn"/>; <see cref="Kill"/> убивает группу процессов хода на устройстве.
/// </summary>
public static class DeviceExecControlOps
{
    public const string Spawn = "spawn";
    public const string Kill = "kill";
}

/// <summary>
/// Кадр управления исполнением: JSON в данных кадра <see cref="DeviceExecFrameChannel.Control"/>.
/// Один тип для обеих сторон канала: сервер (<c>RemoteProcessRunner</c>) его собирает, агент
/// устройства разбирает. <see cref="Gateway"/> едет только в spawn.
/// </summary>
public sealed record DeviceExecControl(
    string Op,
    string TurnId,
    DeviceExecSpawn? Spawn = null,
    DeviceExecGateway? Gateway = null);

/// <summary>
/// Выдача шлюза на ход: <see cref="TurnId"/> — ход в маршруте шлюза (<c>/gw/t/{TurnId}/…</c>),
/// <see cref="Token"/> — его секрет. Секрет едет только этим полем кадра spawn и живёт в
/// памяти агента: в env, argv и файлы CLI он не попадает (сторож G3). <see cref="TurnId"/>
/// шлюза — не <see cref="DeviceExecControl.TurnId"/> исполнения: у них разные хозяева.
/// </summary>
public sealed record DeviceExecGateway(string TurnId, string Token)
{
    // Секрет не печатается ни в лог, ни в исключение
    public override string ToString() => $"DeviceExecGateway {{ TurnId = {TurnId} }}";
}

/// <summary>
/// Что запустить на устройстве. Собирает <c>RemoteProcessRunner</c> по allow-list: учётных
/// данных сервера здесь нет по построению (ни ключей провайдеров, ни токенов подписки, ни
/// сервисного JWT). Окружение CLI агент собирает сам с нуля и добавляет поверх только
/// <see cref="Env"/>.
///
/// Файлы, которые spec сервера передаёт путями (MCP-конфиг хода, системный промпт), едут
/// в <see cref="Files"/>; в <see cref="Args"/> на их месте стоит
/// <see cref="DeviceExecPlaceholders.File"/> — агент материализует файл во временном
/// каталоге хода и подставляет его путь. В содержимом файлов агент подставляет адрес своего
/// хода в сайдкаре вместо <see cref="DeviceExecPlaceholders.Sidecar"/>.
///
/// <see cref="FileName"/> агент не исполняет как есть: запускается только его управляемая
/// копия CLI, а имя сверяется с <see cref="DeviceExecCli.Name"/>.
/// </summary>
public sealed record DeviceExecSpawn(
    string FileName,
    IReadOnlyList<string> Args,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<DeviceExecFile> Files,
    bool RedirectStdin);

/// <summary>Файл spec для материализации на устройстве. <see cref="Name"/> — только имя, без пути.</summary>
public sealed record DeviceExecFile(string Id, string Name, string Content);

/// <summary>
/// Данные кадра <see cref="DeviceExecFrameChannel.Exit"/>: код выхода или сигнал.
/// <see cref="Error"/> — причина, по которой ход не запустился на устройстве.
/// </summary>
public sealed record DeviceExecExit(int? Code, string? Signal = null, string? Error = null);

/// <summary>Что агент согласен запускать по команде сервера.</summary>
public static class DeviceExecCli
{
    /// <summary>Единственная программа spawn: управляемая копия CLI агента.</summary>
    public const string Name = "claude";
}

/// <summary>
/// Адресация хода в сайдкаре агента: <c>{сайдкар}/t/{ключ}/{llm|mcp}/…</c>. Ключ — случайность
/// агента, а не id хода сервера. Одно определение для обеих сторон: сервер пишет адреса
/// MCP через <see cref="DeviceExecPlaceholders.Sidecar"/>, агент разворачивает плейсхолдер в
/// <see cref="TurnUrl"/> и принимает запросы по тем же сегментам.
/// </summary>
public static class DeviceSidecarRoutes
{
    public const string TurnSegment = "t";
    public const string Llm = "llm";
    public const string Mcp = "mcp";

    /// <summary>Адрес хода в сайдкаре: <c>{sidecarUrl}/t/{key}</c>, без завершающего слэша.</summary>
    public static string TurnUrl(string sidecarUrl, string key) => $"{sidecarUrl}/{TurnSegment}/{key}";
}

/// <summary>
/// Выход наружу собственного трафика CLI (ADR-016 §2, задача 2.9): <c>CONNECT host:port</c>,
/// пришедший в сайдкар по <c>HTTPS_PROXY</c>, едет на сервер WebSocket'ом
/// <c>/gw/t/{ход}/egress?host=…&amp;port=…</c> с токеном устройства и токеном хода, наружу его
/// выпускает сервер. Ход в CONNECT сайдкар узнаёт по <c>Proxy-Authorization</c>: CLI шлёт её
/// из учётки в адресе прокси, паролем в ней стоит ключ хода сайдкара.
/// </summary>
public static class DeviceEgressRoutes
{
    public const string Segment = "egress";
    public const string HostQuery = "host";
    public const string PortQuery = "port";
    public const string ProxyUser = "turn";

    /// <summary>Путь входа шлюза относительно адреса сервера, без ведущего слэша.</summary>
    public static string GatewayPath(string turnId) => $"gw/t/{Uri.EscapeDataString(turnId)}/{Segment}";

    /// <summary><c>HTTPS_PROXY</c> хода: адрес сайдкара с учёткой <c>turn:{ключ}</c>.</summary>
    public static string ProxyUrl(string sidecarUrl, string key) =>
        sidecarUrl.Replace("://", $"://{ProxyUser}:{key}@", StringComparison.Ordinal);
}

/// <summary>Подстановки, которые агент разворачивает у себя.</summary>
public static class DeviceExecPlaceholders
{
    /// <summary>
    /// Адрес хода в сайдкаре агента, <see cref="DeviceSidecarRoutes.TurnUrl"/>, без завершающего
    /// слэша. Сервер дописывает к нему <c>/mcp/{имя}/…</c> или <c>/llm/…</c>.
    /// </summary>
    public const string Sidecar = "{{ccs-sidecar}}";

    public const string FilePrefix = "{{ccs-file:";
    public const string FileSuffix = "}}";

    /// <summary>Аргумент, вместо которого агент ставит путь материализованного файла.</summary>
    public static string File(string id) => FilePrefix + id + FileSuffix;
}

/// <summary>Единые опции JSON кадров Control/Exit для обеих сторон канала.</summary>
public static class DeviceExecJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}
