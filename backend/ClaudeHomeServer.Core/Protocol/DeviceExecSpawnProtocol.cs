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

/// <summary>Кадр управления исполнением: JSON в данных кадра <see cref="DeviceExecFrameChannel.Control"/>.</summary>
public sealed record DeviceExecControl(string Op, string TurnId, DeviceExecSpawn? Spawn = null);

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
/// сайдкара вместо <see cref="DeviceExecPlaceholders.Sidecar"/>.
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

/// <summary>Данные кадра <see cref="DeviceExecFrameChannel.Exit"/>: код выхода или сигнал.</summary>
public sealed record DeviceExecExit(int? Code, string? Signal = null);

/// <summary>Подстановки, которые агент разворачивает у себя.</summary>
public static class DeviceExecPlaceholders
{
    /// <summary>Базовый адрес сайдкара агента (<c>http://127.0.0.1:{порт}</c>), без завершающего слэша.</summary>
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
