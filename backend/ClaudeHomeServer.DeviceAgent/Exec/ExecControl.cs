using System.Text.Json;

namespace ClaudeHomeServer.DeviceAgent.Exec;

// Содержимое кадров Control/Exit канала исполнения — сторона агента.
//
// Форма JSON совпадает с DeviceExecSpawnProtocol задачи 2.3 (RemoteProcessRunner): тот
// файл ещё не в этой ветке, поэтому здесь приёмная копия. После слияния 2.2 и 2.3 копия
// заменяется Core-типами, а поле Gateway переезжает в Core-контракт: без него серверу
// нечем передать агенту токен хода.

internal static class ExecControlOps
{
    public const string Spawn = "spawn";
    public const string Kill = "kill";
}

/// <summary>Кадр управления: первый — всегда spawn, дальше может прийти kill того же хода.</summary>
internal sealed record ExecControl(string Op, string TurnId, ExecSpawn? Spawn = null, ExecGatewayGrant? Gateway = null);

/// <summary>
/// Что запустить. <see cref="FileName"/> агент не исполняет как есть: запускается только
/// управляемая копия CLI из аренды, а имя сверяется с <see cref="ExecSpawnRules.CliName"/>.
/// </summary>
internal sealed record ExecSpawn(
    string FileName,
    IReadOnlyList<string> Args,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Env,
    IReadOnlyList<ExecFile>? Files,
    bool RedirectStdin = true);

/// <summary>Файл spec: <see cref="Name"/> — только имя, без пути.</summary>
internal sealed record ExecFile(string Id, string Name, string Content);

/// <summary>
/// Выдача шлюза на ход: адрес хода в шлюзе (<c>/gw/t/{TurnId}/…</c>) и его секрет.
/// Секрет живёт только в памяти агента — в env, argv и файлы CLI он не попадает.
/// </summary>
internal sealed record ExecGatewayGrant(string TurnId, string Token)
{
    // Секрет не печатается ни в лог, ни в исключение
    public override string ToString() => $"ExecGatewayGrant {{ TurnId = {TurnId} }}";
}

/// <summary>Данные кадра Exit.</summary>
internal sealed record ExecExit(int? Code, string? Signal = null, string? Error = null);

internal static class ExecSpawnRules
{
    /// <summary>Единственная программа, которую агент согласен запускать по команде сервера.</summary>
    public const string CliName = "claude";

    public const string SidecarPlaceholder = "{{ccs-sidecar}}";
    public const string FilePrefix = "{{ccs-file:";
    public const string FileSuffix = "}}";

    public static string FilePlaceholder(string id) => FilePrefix + id + FileSuffix;
}

internal static class ExecJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);
}
