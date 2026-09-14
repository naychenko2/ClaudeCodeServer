using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;

namespace ClaudeHomeServer.Services.Composition;

// Боевая рассылка статуса сеанса рук (бейдж «руки на home») — событие ленты чата.
//
// Адаптер живёт в Main, а не в вертикали Desktop (Этап 5, вынос Desktop): нужен
// `SessionManager.BroadcastSessionMessageAsync` — веер по session/project/owner-группам с
// проставлением SessionId, а не «отправить в группу» (`ISessionBroadcaster`). Собрать этот
// веер в вертикали из `ISessionDirectory` + `ISessionBroadcaster` значило бы продублировать
// логику ядра — расхождение потом ловилось бы вручную. Заводить ради одного вызывателя ещё
// один шов в спине тоже незачем: интерфейс `IDesktopHandsNotifier` вертикаль объявляет сама,
// а Main его реализует — направление Main → вертикаль разрешено.
public sealed class DesktopHandsNotifier(SessionManager sessions) : IDesktopHandsNotifier
{
    public Task StatusAsync(DesktopHandsSession s, bool active, string? reason, CancellationToken ct = default) =>
        sessions.BroadcastSessionMessageAsync(s.ChatSessionId, new DesktopSessionMessage(
            active, s.DeviceName, s.ChatSessionId, s.ChatName, s.StartedAt, s.ExpiresAt, reason));
}
