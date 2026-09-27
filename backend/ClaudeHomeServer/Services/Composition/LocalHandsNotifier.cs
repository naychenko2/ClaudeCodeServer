using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Composition;

// Боевая рассылка статуса рук локального проекта (бейдж «ИИ управляет компьютером») — событие
// ленты чата. Живёт в Main по той же причине, что DesktopHandsNotifier: веер ядра сессий.
// Имя устройства — человеку, а не GUID; канала устройств нет — без имени.
public sealed class LocalHandsNotifier(SessionManager sessions, IDeviceExecChannel? devices = null) : ILocalHandsNotifier
{
    public Task HandsStatusAsync(string ownerId, string deviceId, string sessionId, string state, string? reason,
        CancellationToken ct = default) =>
        sessions.BroadcastSessionMessageAsync(sessionId,
            new HandsStatusMessage(state, devices?.GetStatus(ownerId, deviceId)?.DeviceName, reason));
}
