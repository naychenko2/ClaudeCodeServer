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
    private readonly DevServerService _devServer;

    public SessionHub(SessionManager sessions, ProjectManager projects, FileWatcherService watcher,
        ConnectionDiagnostics diag, DevServerService devServer)
    {
        _sessions = sessions;
        _projects = projects;
        _watcher = watcher;
        _diag = diag;
        _devServer = devServer;
    }

    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Владелец сессии: у проектной — владелец проекта, у чата вне проекта — сама сессия.
    // Сессии без владельца (легаси/удалённый проект) недоступны никому — как в REST-контроллерах.
    private bool OwnsSession(string sessionId)
    {
        var info = _sessions.GetById(sessionId);
        if (info is null) return false;
        var ownerId = _sessions.ResolveOwnerId(info);
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

        // Новый клиент сразу получает текущий статус — и активный (чтобы не пропустить
        // working при workflow), и завершённый: клиент, пропустивший конец хода вне группы,
        // иначе навсегда остаётся с зависшим ожиданием ответа.
        var info = _sessions.GetSessionInfo(sessionId);
        if (info is not null)
        {
            var statusMsg = new StatusChangedMessage(info.Status.ToString().ToLower(), info.LastMessage, info.MessageCount)
                with
            { SessionId = sessionId };
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

        // Очередь сообщений от агентов, ждущих конца хода: живёт только в памяти сервера,
        // в историю не пишется — без replay подключившийся клиент не увидел бы их вовсе
        if (_sessions.GetVisiblePending(sessionId) is { Count: > 0 } queued)
            await Clients.Caller.SendAsync("message",
                new PendingMessagesMessage(queued) with { SessionId = sessionId });
    }

    // Уход из чата: зрителя снимаем всегда (сервер снова вправе слать push/тост о конце хода),
    // а вот из группы выходим ТОЛЬКО когда ход не идёт. Иначе события начатого хода (дельты,
    // tool_use, result) летят мимо вкладки, и восстановить ленту можно лишь снимком истории —
    // ровно так терялся хвост ответа, когда пользователь уходил в другой чат, не дождавшись
    // ответа. Оставшийся в группе клиент копит события в модульном сторе useSession, поэтому
    // при возврате лента уже целая. Группа отпустится сама при обрыве связи (OnDisconnectedAsync).
    public async Task LeaveSession(string sessionId)
    {
        _sessions.RemoveViewer(sessionId, Context.ConnectionId);
        if (TurnInProgress(sessionId)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    // Идёт ли ход прямо сейчас — та же тройка статусов, что считают живой сессию
    // BoardService и TaskExecutionService. Waiting входит: чат ждёт ответа на карточку
    // разрешения/вопроса, и она приходит в группу.
    private bool TurnInProgress(string sessionId) =>
        _sessions.GetSessionInfo(sessionId)?.Status
            is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting;

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

    // Подписка на вывод дев-сервера (вкладка «Логи» панели «Сервисы»). Группа — на
    // конкретный сервис, поэтому подписок ровно столько, сколько открытых вкладок логов.
    //
    // Порядок важен: накопленный буфер уходит АДРЕСНО вызывающему ДО входа в группу.
    // Наоборот — и строки, пришедшие между реплеем и подпиской, задвоятся. Тот же приём,
    // что в TerminalService.ConnectAsync.
    public async Task JoinPreviewLog(string projectId, string serviceId)
    {
        if (!OwnsProject(projectId)) throw Denied();
        var buffered = _devServer.GetLogBuffer(projectId, serviceId, UserId!);
        if (!string.IsNullOrEmpty(buffered))
            await Clients.Caller.SendAsync("message", new PreviewLogMessage(serviceId, buffered));
        await Groups.AddToGroupAsync(Context.ConnectionId, DevServerService.LogGroup(projectId, serviceId));
    }

    public Task LeavePreviewLog(string projectId, string serviceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DevServerService.LogGroup(projectId, serviceId));

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

    // Возвращает исход постановки: "started" — ход запущен (рисуем оптимистичный баллон);
    // "queued" — чат занят, сообщение встало в серверную очередь (баллон НЕ рисуем: карточка
    // придёт снимком pending_messages, а доставленное — событием user_message).
    public async Task<string> SendMessage(string sessionId, string text, List<string>? attachedPaths = null, string? mode = null, bool auto = false)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        // auto — сообщение опубликовано автоматически (например, «Обсудить с командой»):
        // UI покажет источник вместо пузыря пользователя
        var outcome = await _sessions.SendMessageAsync(sessionId, text, attachedPaths ?? [], mode, auto: auto);
        return outcome == Services.SessionManager.SendUserOutcome.Started ? "started" : "queued";
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.RespondPermission(sessionId, requestId, behavior);
    }

    // Смена режима прав на лету (переключатель в композере) — применяется к идущему ходу,
    // не дожидаясь следующего сообщения.
    public void SetMode(string sessionId, string mode)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        _sessions.SetMode(sessionId, mode);
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

    // Ответ по карточке плана «Командной реализации» (Э2). decision: run — запустить
    // (единственное согласование итерации), reassign — сменить исполнителя под-задачи
    // (нужны subtaskId + executorPersonaId, карточка остаётся открытой), cancel — отменить.
    public async Task RespondTeamPlan(string sessionId, string planId, string decision,
        string? subtaskId = null, string? executorPersonaId = null)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        var parsed = decision?.ToLowerInvariant() switch
        {
            "run" => TeamPlanDecision.Run,
            "reassign" => TeamPlanDecision.Reassign,
            "cancel" => TeamPlanDecision.Cancel,
            _ => (TeamPlanDecision?)null,
        };
        if (parsed is null) throw new HubException($"Неизвестное решение по плану: {decision}");
        await _sessions.RespondTeamPlanAsync(sessionId, planId, parsed.Value, subtaskId, executorPersonaId);
    }

    // Решение человека по карточке остановки «Командной реализации» (Э4). actionId — id
    // кнопки карточки (answer/reassign/addBudget/runNext/…), comment — необязательное
    // пояснение. Кнопка равносильна обычному сообщению в чат: координатор получает ход
    // с решением. Действие приходит ТОЛЬКО отсюда — у агента такого пути нет, поэтому
    // расширить бюджет («Добавить бюджет») может лишь человек.
    public async Task RespondTeamEscalation(string sessionId, string escalationId,
        string? actionId = null, string? comment = null)
    {
        if (!OwnsSession(sessionId)) throw Denied();
        await _sessions.RespondTeamEscalationAsync(sessionId, escalationId, actionId, comment);
    }
}
