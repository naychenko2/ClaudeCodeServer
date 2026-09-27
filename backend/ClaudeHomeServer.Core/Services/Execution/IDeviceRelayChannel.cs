using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Шов ретранслятора чтения (ADR-016 §5, задача 5.1): реализует вертикаль Desktop тем же
/// каналом исполнения, что и ходы (<see cref="IDeviceExecChannel"/>), — нового канала нет.
/// Отличие — назначение <see cref="DeviceExecPurposes.Relay"/>: агент отдаёт такой канал
/// обработчику чтения, а не исполнителю ходов, и готовность харнеса не нужна.
/// </summary>
public interface IDeviceRelayChannel
{
    /// <summary>
    /// Открывает канал одного запроса ретранслятора. Устройство офлайн или без возможности
    /// <see cref="DeviceCapabilities.Relay"/> — <see cref="DeviceExecRefusedException"/> сразу,
    /// до команды устройству.
    /// </summary>
    Task<IDeviceExecStream> OpenRelayAsync(string ownerId, string deviceId, CancellationToken ct = default);
}
