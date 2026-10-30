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

    // Актор идущего хода по чату: к ResultMessage ядро уже сняло статус хода и обнулило актора
    // (GetTurnActor вернул бы null), поэтому запоминаем его на сообщениях внутри хода
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _turnActors = new();

    private Task OnMsgAsync(Session session, ServerMessage msg)
    {
        if (string.IsNullOrEmpty(session.ProjectId)) return Task.CompletedTask;
        if (msg is not ResultMessage)
        {
            // Внутри хода актор ещё жив: обновляем запомненного на каждом сообщении
            var tenant = session.OwnerId ?? _projects.GetById(session.ProjectId)?.OwnerId;
            if (!string.IsNullOrEmpty(tenant)
                && _sessions.GetTurnActor(session.Id, tenant) is { } live)
                _turnActors[session.Id] = live;
            return Task.CompletedTask;
        }

        var owner = session.OwnerId;
        if (string.IsNullOrEmpty(owner))
            owner = _projects.GetById(session.ProjectId)?.OwnerId;
        if (string.IsNullOrEmpty(owner)) return Task.CompletedTask;

        var label = session.PersonaId is not null && _personas.GetByIdInternal(session.PersonaId) is { } p
            ? PersonaManager.PersonaLabel(p) : null;

        // Запись — вне потока диспетчеризации: синхронный SQLite-append под _writeLock
        // задерживал бы broadcast результата хода (Append сам глотает свои ошибки)
        var projectId = session.ProjectId;
        var sessionId = session.Id;
        var actor = session.PersonaId ?? "user";
        // Человек-инициатор хода (общий проект); null — неизвестен, запись остаётся без него
        _turnActors.TryRemove(sessionId, out var remembered);
        var actorUserId = remembered ?? _sessions.GetTurnActor(sessionId, owner!);
        _ = Task.Run(() => _events.Append(projectId, owner!, ProjectEventTypes.ChatTurn,
            actor, label is not null ? $"{label} ответил" : "Ход чата завершён", sessionId, actorUserId));
        return Task.CompletedTask;
    }
}
