using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Логирование хода чата в проектный лог (①-L3): подписывается на SessionManager.OnSessionMessage
// и по завершении хода (ResultMessage) в проектной сессии пишет событие chat_turn — лента
// активности команды (①-L1) видит и разговорную активность, не только задачи/память.
// Вынесено в отдельный IHostedService, чтобы не раздувать конструктор SessionManager.
// Владалец проектной сессии берётся из session.OwnerId, иначе из проекта.
public sealed class ChatTurnLoggerService : IHostedService
{
    private readonly SessionManager _sessions;
    private readonly ProjectEventLogService _events;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;

    public ChatTurnLoggerService(SessionManager sessions, ProjectEventLogService events,
        ProjectManager projects, PersonaManager personas)
    {
        _sessions = sessions;
        _events = events;
        _projects = projects;
        _personas = personas;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage += OnMsgAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage -= OnMsgAsync;
        return Task.CompletedTask;
    }

    private Task OnMsgAsync(Session session, ServerMessage msg)
    {
        if (msg is not ResultMessage) return Task.CompletedTask;
        if (string.IsNullOrEmpty(session.ProjectId)) return Task.CompletedTask;

        var owner = session.OwnerId;
        if (string.IsNullOrEmpty(owner))
            owner = _projects.GetById(session.ProjectId)?.OwnerId;
        if (string.IsNullOrEmpty(owner)) return Task.CompletedTask;

        var label = session.PersonaId is not null && _personas.GetByIdInternal(session.PersonaId) is { } p
            ? PersonaManager.PersonaLabel(p) : null;

        _events.Append(session.ProjectId, owner!, ProjectEventTypes.ChatTurn,
            session.PersonaId ?? "user",
            label is not null ? $"{label} ответил(а)" : "Ход чата завершён",
            session.Id);
        return Task.CompletedTask;
    }
}
