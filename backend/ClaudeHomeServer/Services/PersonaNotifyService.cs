using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Уведомления «от лица персоны» (toast + web-push в группу user_{userId}): единое место
// для proactive-уведоманий ②-2.1 — спавн следующей регулярной задачи, выученные факты и т.п.
// Подпись персоны «Роль (Имя)» резолвится через PersonaManager; нет персоны — нейтрально.
// Ошибки отправки тихо логируются: proactive-уведомление не должно ронять бизнес-логику.
public class PersonaNotifyService
{
    private readonly IHubContext<SessionHub> _hub;
    private readonly PushService _push;
    private readonly PersonaManager _personas;
    private readonly ILogger<PersonaNotifyService>? _log;

    public PersonaNotifyService(IHubContext<SessionHub> hub, PushService push, PersonaManager personas,
        ILogger<PersonaNotifyService>? log = null)
    {
        _hub = hub;
        _push = push;
        _personas = personas;
        _log = log;
    }

    // Подпись персоны по id (null — null) — для построения заголовков «Роль (Имя) …» снаружи
    public string? LabelOf(string? personaId) =>
        personaId is not null && _personas.GetByIdInternal(personaId) is { } p
            ? PersonaManager.PersonaLabel(p) : null;

    // Отправить toast + web-push от лица персоны (если personaId задан — подпись резолвится внутри,
    // но заголовок caller формирует сам, чтобы контролировать падеж/фразулировку).
    public async Task SendAsync(string userId, string title, string body, string? url = null, string kind = "info")
    {
        if (string.IsNullOrEmpty(userId)) return;
        try
        {
            var msg = new NotificationMessage(Title: title, Body: body, Url: url, Kind: kind);
            await _hub.Clients.Group("user_" + userId).SendAsync("message", msg);
            await _push.SendToUserAsync(userId, msg);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "PersonaNotify: не удалось отправить уведомление «{Title}»", title);
        }
    }
}
