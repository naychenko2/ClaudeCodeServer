using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Hubs;

[Authorize]
public class SessionHub : Hub
{
    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly FileWatcherService _watcher;
    private readonly ConnectionDiagnostics _diag;

    public SessionHub(SessionManager sessions, ProjectManager projects, FileWatcherService watcher, ConnectionDiagnostics diag)
    {
        _sessions = sessions;
        _projects = projects;
        _watcher = watcher;
        _diag = diag;
    }

    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Владелец сессии: у проектной — владелец проекта, у чата вне проекта — сама сессия.
    // Сессии без владельца (легаси/удалённый проект) недоступны никому — как в REST-контроллерах.
    private bool OwnsSession(string sessionId)
    {
        var info = _sessions.GetById(sessionId);
        if (info is null) return false;
        var ownerId = info.ProjectId is not null
            ? _projects.GetById(info.ProjectId)?.OwnerId
            : info.OwnerId;
        return ownerId is not null && ownerId == UserId;
    }

    private bool OwnsProject(string projectId)
    {
        var ownerId = _projects.GetById(projectId)?.OwnerId;
        return ownerId is not null && ownerId == UserId;
    }

    private static HubException Denied() => new("Доступ запрещён");

    public override Task OnConnectedAsync()
    {
        var transport = Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "unknown";
        _diag.RecordConnect(Context.ConnectionId, transport, UserId ?? Context.User?.Identity?.Name);
        return base.OnConnectedAsync();
    }

    public async Task JoinSession(string sessionId)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.AddViewer(sessionId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);

        // Новый клиент сразу получает текущий статус (чтобы не пропустить working при workflow)
        var info = _sessions.GetSessionInfo(sessionId);
        if (info is { Status: SessionStatus.Working or SessionStatus.Waiting or SessionStatus.Starting })
        {
            var statusMsg = new StatusChangedMessage(info.Status.ToString().ToLower(), info.LastMessage, info.MessageCount)
                with { SessionId = sessionId };
            await Clients.Caller.SendAsync("message", statusMsg);
        }

        // Кэшированные workflow_progress
        foreach (var msg in _sessions.GetWorkflowProgress(sessionId))
            await Clients.Caller.SendAsync("message", msg with { SessionId = sessionId });

        // Последний манифест recall (F3) — иначе «использовано сейчас» видно только тем,
        // кто был на связи в момент самого хода (актуально для персон-автоматизаций)
        if (_sessions.GetLastRecallManifest(sessionId) is { } recall)
            await Clients.Caller.SendAsync("message", recall with { SessionId = sessionId });

        // Ожидающая карточка (разрешение/вопрос/план): CLI ждёт ответа до часового таймаута —
        // без replay клиент после F5 видел бы лишь «Claude печатает…» без возможности ответить
        if (_sessions.GetPendingInteraction(sessionId) is { } pending)
            await Clients.Caller.SendAsync("message", pending with { SessionId = sessionId });
    }

    public async Task LeaveSession(string sessionId)
    {
        _sessions.RemoveViewer(sessionId, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task JoinProject(string projectId)
    {
        if (!OwnsProject(projectId)) throw Denied();
        await Groups.AddToGroupAsync(Context.ConnectionId, "project_" + projectId);
        _watcher.Watch(projectId, Context.ConnectionId);
    }

    public async Task LeaveProject(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "project_" + projectId);
        _watcher.Unwatch(projectId, Context.ConnectionId);
    }

    // Группа для realtime-обновления списка чатов вне проекта (без файлового watcher).
    // Подписаться можно только на самого себя.
    public Task JoinUser(string userId) =>
        userId == UserId
            ? Groups.AddToGroupAsync(Context.ConnectionId, "user_" + userId)
            : throw Denied();

    public Task LeaveUser(string userId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, "user_" + userId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _watcher.RemoveConnection(Context.ConnectionId);
        // Обрыв без LeaveSession (закрытая вкладка, потеря сети) — иначе зритель «застревает»
        // и навсегда глушит проактивные уведомления сессии (HasViewers)
        _sessions.RemoveConnectionViewers(Context.ConnectionId);
        _diag.RecordDisconnect(Context.ConnectionId, exception);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(string sessionId, string text, List<string>? attachedPaths = null, string? mode = null, bool auto = false)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        // auto — сообщение опубликовано автоматически (например, «Обсудить с командой»):
        // UI покажет источник вместо пузыря пользователя
        await _sessions.SendMessageAsync(sessionId, text, attachedPaths ?? [], mode, auto: auto);
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.RespondPermission(sessionId, requestId, behavior);
    }

    public void Interrupt(string sessionId)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.Interrupt(sessionId);
    }

    // Ручное сворачивание контекста сессии (/compact)
    public async Task CompactSession(string sessionId)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        await _sessions.CompactAsync(sessionId);
    }

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.AnswerQuestion(sessionId, toolUseId, answerText);
    }

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback = null)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.RespondPlan(sessionId, requestId, approve, feedback);
    }
}
