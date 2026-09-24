using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Composition;

/// <summary>
/// Очередь чата при офлайн-устройстве (ADR-016, вариант А плана §5): сообщения, припаркованные
/// <see cref="SessionManager"/> в <see cref="Session.DeviceWaitQueue"/>, уходят в работу при
/// выходе устройства в онлайн; ждущие дольше 24 ч снимаются с уведомлением владельцу.
/// </summary>
public sealed class ChatDeviceWaitHandler(
    SessionManager sessions,
    IProjectManager projects,
    IProjectDeviceGate gate,
    NotificationService notifications,
    ILogger<ChatDeviceWaitHandler> log) : IDeviceOnlineHandler
{
    public async Task OnDeviceOnlineAsync(string ownerId, string deviceId, CancellationToken ct = default)
    {
        foreach (var session in sessions.GetDeviceWaitingSessions())
        {
            if (sessions.ResolveOwnerId(session) != ownerId || ReadyGate(session) is not { } ready
                || ready.DeviceId != deviceId)
                continue;
            await sessions.ReleaseDeviceWaitAsync(session.Id);
        }
    }

    public async Task SweepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        foreach (var session in sessions.GetDeviceWaitingSessions())
        {
            var expired = await sessions.ExpireDeviceWaitAsync(session.Id, nowUtc - ProjectCapabilities.DeviceWaitCeiling);
            if (expired.Count > 0) await NotifyExpiredAsync(session, expired.Count);
            // Событие онлайна могло потеряться (рестарт, гонка с парковкой) — догоняем проходом
            if (ReadyGate(session) is not null) await sessions.ReleaseDeviceWaitAsync(session.Id);
        }
    }

    // Готово ли устройство чата прямо сейчас; null — нет (ждём дальше) или чат не проектный
    private ProjectBackgroundGate? ReadyGate(Session session)
    {
        if (session.ProjectId is not { } pid || projects.GetById(pid) is not { } project) return null;
        var verdict = gate.Check(project);
        return verdict.IsReady ? verdict : null;
    }

    private async Task NotifyExpiredAsync(Session session, int count)
    {
        log.LogWarning("Чат {Session}: {Count} сообщ. не дождались устройства за {Hours} ч и сняты",
            session.Id, count, (int)ProjectCapabilities.DeviceWaitCeiling.TotalHours);
        // У проектного чата владелец — владелец проекта (Session.OwnerId там не хранится)
        if (sessions.ResolveOwnerId(session) is not { } owner) return;
        await notifications.SendNotificationMessageAsync(owner, new NotificationMessage(
            Title: "Сообщения не доставлены",
            Body: $"{session.Name ?? "Чат"}: {count} сообщ. ждали устройство проекта " +
                  $"{(int)ProjectCapabilities.DeviceWaitCeiling.TotalHours} ч и сняты — устройство так и не вышло в сеть",
            Url: session.ProjectId is { } pid ? $"/project/{pid}/chat/{session.Id}" : $"/chats/{session.Id}",
            Kind: "info",
            ProjectId: session.ProjectId,
            Tag: "Система") { SessionId = session.Id }, sendPush: true);
    }
}
