using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Шов выдачи папки проекта (решение владельца 2026-09-27, ADR-016 §5): тот же канал
/// исполнения, что у ходов и ретранслятора, с назначением <see cref="DeviceExecPurposes.BindFolder"/>.
/// Реализует вертикаль Desktop.
/// </summary>
public interface IDeviceFolderBindChannel
{
    /// <summary>
    /// Открывает канал одной выдачи. Устройство офлайн или без возможности
    /// <see cref="DeviceCapabilities.BindFolder"/> — <see cref="DeviceExecRefusedException"/> сразу.
    /// </summary>
    Task<IDeviceExecStream> OpenBindFolderAsync(string ownerId, string deviceId, CancellationToken ct = default);
}
