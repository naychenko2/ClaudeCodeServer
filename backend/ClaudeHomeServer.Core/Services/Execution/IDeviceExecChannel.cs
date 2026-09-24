using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Шов канала исполнения на устройстве (ADR-016, план §2 п. 5): реализует вертикаль
/// Desktop (ей принадлежат устройства, их авторизация и соединения), потребляет Execution
/// (<c>RemoteProcessRunner</c>). Прямой ссылки Execution → Desktop нет.
///
/// Отказ открыть канал — <see cref="DeviceExecRefusedException"/> с причиной СРАЗУ, до
/// старта процесса: ход на неготовом устройстве не должен падать посреди запуска.
/// </summary>
public interface IDeviceExecChannel
{
    /// <summary>
    /// Состояние устройства для матрицы возможностей и фронта: онлайн, возможности, версии
    /// и вердикт «харнес готов». null — устройства нет у владельца или оно отозвано.
    /// </summary>
    DeviceExecStatus? GetStatus(string ownerId, string deviceId);

    /// <summary>
    /// Открывает поток исполнения: команда устройству по каналу управления, ожидание его
    /// подключения к /api/devices/exec. Неготовое устройство — отказ без попытки открыть.
    /// </summary>
    Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default);
}

/// <summary>
/// Поток одного исполнения. Кадры данных нумеруются и подтверждаются: обрыв соединения
/// поток не закрывает — после реконнекта устройства неподтверждённое досылается, дубли
/// отбрасываются. Жизнью потока владеет потребитель: закрывает его <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
public interface IDeviceExecStream : IAsyncDisposable
{
    string ExecId { get; }

    /// <summary>Отправить кадр устройству. Подтверждений не ждёт, но упирается в потолок досылки.</summary>
    ValueTask SendAsync(DeviceExecFrameChannel channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>Входящие кадры данных по порядку, без дублей; подтверждения — внутри потока.</summary>
    IAsyncEnumerable<DeviceExecFrame> ReadAllAsync(CancellationToken ct = default);
}

/// <summary>Снимок состояния устройства для исполнения.</summary>
public sealed record DeviceExecStatus(
    string DeviceId,
    string DeviceName,
    bool Online,
    string? Platform,
    string? AgentVersion,
    string? CliVersion,
    string? RequiredCliVersion,
    IReadOnlyList<string> Capabilities,
    bool HarnessReady,
    string? HarnessProblem)
{
    public bool HasCapability(string capability) => Capabilities.Contains(capability);

    /// <summary>Можно ли прямо сейчас запускать ход на устройстве.</summary>
    public bool CanExec => Online && HasCapability(DeviceCapabilities.Exec) && HarnessReady;
}

/// <summary>Почему канал исполнения не открылся.</summary>
public enum DeviceExecRefusal
{
    UnknownDevice,
    Offline,
    NoExecCapability,
    HarnessNotReady,
    NoResponse,
}

/// <summary>Отказ открыть канал исполнения; <see cref="Exception.Message"/> — готовый текст для человека.</summary>
public sealed class DeviceExecRefusedException(DeviceExecRefusal reason, string message) : Exception(message)
{
    public DeviceExecRefusal Reason { get; } = reason;
}
