using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Composition;

// Боевая рассылка статуса рук локального проекта (бейдж «ИИ управляет компьютером») — событие
// ленты чата. Живёт в Main, а не в вертикали Desktop: нужен веер ядра сессий
// (`SessionManager.BroadcastSessionMessageAsync`), интерфейс вертикаль объявляет сама.
// Имя устройства — человеку, а не GUID; канала устройств нет — без имени.
public sealed class LocalHandsNotifier(SessionManager sessions, IDeviceExecChannel? devices = null) : ILocalHandsNotifier
{
    public Task HandsStatusAsync(string ownerId, string deviceId, string sessionId, string state, string? reason,
        CancellationToken ct = default) =>
        sessions.BroadcastSessionMessageAsync(sessionId,
            new HandsStatusMessage(state, devices?.GetStatus(ownerId, deviceId)?.DeviceName, reason));
}
