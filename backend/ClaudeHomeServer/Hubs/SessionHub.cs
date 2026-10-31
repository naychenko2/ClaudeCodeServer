using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ProjectServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Hubs;

[Authorize]
public class SessionHub : Hub
{
    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly IProjectAccess _access;
    private readonly FileWatcherService _watcher;
    private readonly ConnectionDiagnostics _diag;
    private readonly DevServerService _devServer;
    private readonly UserConnectionRegistry _userConnections;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(SessionManager sessions, ProjectManager projects, IProjectAccess access,
        FileWatcherService watcher, ConnectionDiagnostics diag, DevServerService devServer,
        UserConnectionRegistry userConnections, ILogger<SessionHub> logger)
    {
        _sessions = sessions;
        _projects = projects;
        _access = access;
        _watcher = watcher;
        _diag = diag;
        _devServer = devServer;
        _userConnections = userConnections;
        _logger = logger;
    }

    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => Context.User?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Единственная точка доступа хаба (S0-6): чат резолвится через свой проект, чат вне проекта —
    // через владение самим чатом (SessionAccess.CanSee). Чат без владельца (легаси/удалённый
    // проект) и проект без владельца остаются недоступными никому: резолв fail-closed, как в
    // REST-контроллерах. Инлайн-сравнений «владелец == id соединения» в хабе больше нет; дальше
    // идёт только RequestScope, и ключ хранилищ проекта берётся из его TenantOwnerId, а не из id
    // вызывающего — иначе в общем проекте участник читал бы и правил своё отражение данных
    // (ловушка ключа, разрез §6.6 п. 2). В S0 роль одна (владелец), поведение продукта не
    // меняется; право названо явно (просмотр — Read, действия — Write), чтобы при появлении
    // ролей (S1) хаб не пришлось перечитывать.
    private RequestScope? ScopeOfSession(string sessionId, ProjectRight right = ProjectRight.Read) =>
        _access.ResolveSession(UserId, sessionId, right);

    // Управление идущим ходом (S2a-10): RunTurn плюс «владелец или автор хода». Участник чужой ход
    // не останавливает и на чужие вопросы агента не отвечает.
    private bool OwnerOrTurnAuthor(string sessionId) =>
        ScopeOfSession(sessionId, ProjectRight.RunTurn) is not null && UserId is { } uid
        && _sessions.IsOwnerOrTurnAuthor(sessionId, uid);

    private RequestScope? ScopeOfProject(string projectId, ProjectRight right = ProjectRight.Read) =>
        _access.Resolve(UserId, projectId, right);

    // Текст «Доступ запрещён» — контракт фронта для отказов без правила (подписка, чужой ресурс,
    // чат не виден). Отказ логируется: sub соединения и владелец ресурса сразу показывают
    // рассинхрон аккаунта (токен/query-строку не пишем никогда). Отказы правила «автор или
    // владелец» (для тех, у кого есть право вести ход) и права Configure несут код — ниже,
    // в DeniedTurnAuthor/DeniedOwnerSettings.
    private HubException DeniedSession(string sessionId, [CallerMemberName] string method = "")
    {
        var info = _sessions.GetById(sessionId);
        var ownerId = info is null ? null : _sessions.ResolveOwnerId(info);
        return LogDenied(method, "session", sessionId, ownerId);
    }

    private HubException DeniedProject(string projectId, [CallerMemberName] string method = "") =>
        LogDenied(method, "project", projectId, _projects.GetById(projectId)?.OwnerId);

    // Отказ по userId: владелец ресурса — сам запрошенный userId
    private HubException DeniedUser(string userId, [CallerMemberName] string method = "") =>
        LogDenied(method, "user", userId, userId);

    // Отказ правила «владелец или автор идущего хода» (S2a-10c) и права Configure (настройки чата):
    // тому, кому чат ВИДЕН (тот же Read-гейт точки доступа, что решает в REST 403 вместо 404),
    // отказ несёт машинный код префиксом — тот же, что в REST body.code: DenyTurnAuthor →
    // ChatAuthorGate.not_chat_author, DenyOwnerTurn → OwnerSettingsGate.owner_settings. Фронт по коду
    // различает «не автор хода» и «настройки — владельцу» вместо общего отказа. Чужому чат не виден
    // → прежний «Доступ запрещён» без кода (существование чата не раскрываем, как 404 в REST).
    // Текст называет то правило, которое отработало: здесь — авторство идущего хода (у этого же
    // кода в REST есть и другой случай — авторские действия над чатом, там свой текст).
    // Читатель (права RunTurn нет вовсе) — не «не автор хода»: он не может вести ход, и ему —
    // прежний общий отказ без кода (п. 2 вердикта R-S2a-10c). not_chat_author несёт только тот,
    // кто ход в этом чате вести может, но не является автором этого хода и не владелец.
    private HubException DeniedTurnAuthor(string sessionId, [CallerMemberName] string method = "") =>
        ScopeOfSession(sessionId, ProjectRight.RunTurn) is null
            ? DeniedSession(sessionId, method)
            : DeniedByRight(sessionId, ChatAuthorGate.ErrorCode, "Только автор хода или владелец проекта", method);

    // Текст — тот же, что у REST-ответа этого же кода (OwnerSettingsGate.Denied): одному коду
    // соответствует одна человеческая строка, где бы она ни пришла.
    private HubException DeniedOwnerSettings(string sessionId, [CallerMemberName] string method = "") =>
        DeniedByRight(sessionId, OwnerSettingsGate.ErrorCode, "Настройки чата выбирает владелец", method);

    // Клиент опознаёт код по маркеру «HubException: <код>:» — SignalR оборачивает текст:
    // DefaultHubDispatcher и ErrorMessageHelper отдают клиенту
    // «An unexpected error occurred invoking '<метод>' on the server. HubException: <код>: <текст>»,
    // поэтому код доходит не началом строки, а после маркера. Разбор фронта (S2a-F7) ищет ровно
    // это: подстроку «HubException: <код>:» или «<код>:» в начале строки (последнее — только для
    // REST body.code, там обёртки нет).
    public const string DenialMarker = "HubException: ";

    private HubException DeniedByRight(string sessionId, string code, string reason, string method)
    {
        var info = _sessions.GetById(sessionId);
        var visible = ScopeOfSession(sessionId) is not null;
        return LogDenied(method, "session", sessionId, info is null ? null : _sessions.ResolveOwnerId(info),
            visible ? code : null, visible ? reason : null);
    }

    private HubException LogDenied(string method, string resource, string resourceId, string? ownerId,
        string? code = null, string? reason = null)
    {
        // Отказ без кода пишется с кодом=- : строка журнала читается одинаково во всех ветках
        // («код=<что-то>» — отказ правила, «код=-» — общий), пустое значение делало её похожей
        // на оборванную запись (п. 3 вердикта R-S2a-10c)
        _logger.LogWarning(
            "Хаб отказал в доступе: метод={Method} ресурс={Resource} id={ResourceId} sub={UserId} владелец={OwnerId} соединение={ConnectionId} код={Code}",
            method, resource, resourceId, UserId, ownerId, Context.ConnectionId, code ?? "-");
        // SignalR доносит до клиента только строку error и притом оборачивает текст HubException
        // в свою обёртку (см. DenialMarker): @microsoft/signalr собирает из этой строки
        // Error(invocationEvent.error) и errorData клиента не читает (в dist/esm/HubConnection.js нет
        // обращения к errorData), поэтому код — часть текста. Форма стабильная: «код: пояснение»,
        // код равен REST body.code. По маркеру «HubException: <код>:» отказ опознаёт фронт
        // (participantGate.denialCodeOf, S2a-F7), а прежнему «Доступ запрещён» остался путь прочих
        // отказов — он и дальше приходит без кода.
        return code is null
            ? new HubException("Доступ запрещён")
            : new HubException($"{code}: {reason}");
    }

    // «Чат не найден» отдельно от «Доступ запрещён»: на «не найден» фронт сбрасывает
    // activeSession из localStorage (лечение чата-призрака), на чужой — нет.
    // Информации о владельце не утекает: null из GetById — id не существует ни у кого.
    private static HubException NotFound() => new("Чат не найден");

    public override Task OnConnectedAsync()
    {
        var transport = Context.Features.Get<IHttpTransportFeature>()?.TransportType.ToString() ?? "unknown";
        _diag.RecordConnect(Context.ConnectionId, transport, UserId ?? Context.User?.Identity?.Name);
        // Реестр соединений (S1-4): соединение известно по своему userId с момента открытия —
        // точка отзыва членства обязана покрыть все вкладки человека, а не только те, что уже
        // успели войти в группы
        _userConnections.Add(UserId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public async Task JoinSession(string sessionId)
    {
        if (_sessions.GetById(sessionId) is null) throw NotFound();
        if (ScopeOfSession(sessionId) is null) throw DeniedSession(sessionId);
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

        // Очередь сообщений, ждущих конца хода: живёт только в памяти сервера, в историю не
        // пишется — без replay подключившийся клиент не увидел бы их вовсе. Шлём ВСЕГДА, в том
        // числе пустой список: снимок заменяет очередь у клиента целиком, а пропуск пустого
        // оставлял бы призраков от сообщений, доставленных пока клиент был офлайн (событие
        // доставки в группу он пропустил, REST-эндпоинта очереди у фронта нет). Такой призрак
        // висел бы рядом с уже доставленным пузырём до следующего изменения очереди.
        await Clients.Caller.SendAsync("message",
            new PendingMessagesMessage(_sessions.GetVisiblePending(sessionId)) with { SessionId = sessionId });
    }

    // Уход из чата: зрителя снимаем всегда (сервер снова вправе слать push/тост о конце хода),
    // а из группы теперь НЕ выходим вовсе. Раньше выходили, если ход не идёт — это спасало от
    // потери хвоста ответа при уходе в другой чат посреди хода, но топило внеходовые события
    // (отчёт делегированной задачи, эскалацию, team_plan): они шлются один раз live-рассылкой
    // в группу без ретрансляции и без replay в JoinSession, а клиент вне группы просто не видит
    // сообщение до следующего хода. Та же болезнь, только вне хода — поэтому исключение снято,
    // группа отпускается только при обрыве связи (OnDisconnectedAsync).
    public Task LeaveSession(string sessionId)
    {
        _sessions.RemoveViewer(sessionId, Context.ConnectionId);
        return Task.CompletedTask;
    }

    // Группа проекта — платформа (списки чатов, задачи); ватчер файлов отказывает сам
    [ProjectCapability(ProjectCapabilityArea.Platform)]
    public async Task JoinProject(string projectId)
    {
        if (ScopeOfProject(projectId) is null) throw DeniedProject(projectId);
        await Groups.AddToGroupAsync(Context.ConnectionId, "project_" + projectId);
        if (_watcher.Watch(projectId, Context.ConnectionId))
        {
            // Watcher поднят заново (пробел в наблюдении: disconnect → rejoin).
            // За время его отсутствия правки не отслеживались — клиенту нужен full-ресинк.
            // Caller, а не группа: пробел был у этого одного соединения; при групповой
            // рассылке чужие живые вкладки получили бы лишний ресинк.
            await Clients.Caller.SendAsync("filesChanged",
                new { projectId, paths = Array.Empty<string>(), full = true });
        }
    }

    [ProjectCapability(ProjectCapabilityArea.Platform)]
    public async Task LeaveProject(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "project_" + projectId);
        _watcher.Unwatch(projectId, Context.ConnectionId);
    }

    // Подписка на вывод дев-сервера (вкладка «Логи» панели «Сервисы»). Группа — на
    // конкретный сервис, поэтому подписок ровно столько, сколько открытых вкладок логов.
    //
    // Накопленный буфер ВОЗВРАЩАЕТСЯ вызывающему, а не рассылается сообщением. Рассылка
    // (даже адресная, Clients.Caller) приходит в соединение, а не в конкретный вьюер:
    // при быстром пере-монтировании — а в dev его гарантирует StrictMode — свежий вьюер
    // ловил и свой реплей, и чужой, и весь лог показывался дважды.
    //
    // Снимок берём ДО входа в группу: строка, пришедшая ровно в этот зазор, потеряется,
    // но задвоиться не может. Из двух зол в логе виднее второе.
    [ProjectCapability(ProjectCapabilityArea.FileBound)]
    public async Task<string?> JoinPreviewLog(string projectId, string serviceId)
    {
        var scope = ScopeOfProject(projectId);
        if (scope is null) throw DeniedProject(projectId);
        // Буфер лога принадлежит пространству проекта (процесс запущен в его папке), поэтому его
        // ключ — арендатор, а не id соединения: в общем проекте участник читает тот же лог, что
        // и владелец, а не своё пустое «отражение» (ловушка ключа, разрез §6.6 п. 2).
        var buffered = _devServer.GetLogBuffer(projectId, serviceId, scope.TenantOwnerId);
        var group = DevServerService.LogGroup(projectId, serviceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        // Подписка записывается в реестр соединений (S1-4b, хвост №1 ревью R-S1-4): точка
        // отзыва членства не видит хабовых соединений и не перечисляет сервисы проекта —
        // снять отозванного со всех его групп логов можно только по записи подписки
        _userConnections.AddGroup(Context.ConnectionId, group);
        return buffered;
    }

    [ProjectCapability(ProjectCapabilityArea.Platform)]
    public Task LeavePreviewLog(string projectId, string serviceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DevServerService.LogGroup(projectId, serviceId));

    // Группа для realtime-обновления списка чатов вне проекта (без файлового watcher).
    // Подписаться можно только на самого себя.
    public Task JoinUser(string userId) =>
        userId == UserId
            ? Groups.AddToGroupAsync(Context.ConnectionId, "user_" + userId)
            : throw DeniedUser(userId);

    public Task LeaveUser(string userId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, "user_" + userId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _watcher.RemoveConnection(Context.ConnectionId);
        // Обрыв без LeaveSession (закрытая вкладка, потеря сети) — иначе зритель «застревает»
        // и навсегда глушит проактивные уведомления сессии (HasViewers)
        _sessions.RemoveConnectionViewers(Context.ConnectionId);
        // Реестр соединений (S1-4): мёртвое соединение не должно пережить свою связь —
        // выкидывание из групп по несуществующему соединению безопасно, но чистить надо здесь
        _userConnections.Remove(UserId, Context.ConnectionId);
        _diag.RecordDisconnect(Context.ConnectionId, exception);
        return base.OnDisconnectedAsync(exception);
    }

    // Возвращает исход постановки: "started" — ход запущен (рисуем оптимистичный баллон);
    // "queued" — чат занят, сообщение встало в серверную очередь (баллон НЕ рисуем: карточка
    // придёт снимком pending_messages, а доставленное — событием user_message).
    public async Task<string> SendMessage(string sessionId, string text, List<string>? attachedPaths = null, string? mode = null, bool auto = false)
    {
        if (ScopeOfSession(sessionId, ProjectRight.RunTurn) is null) throw DeniedSession(sessionId);
        // auto — сообщение опубликовано автоматически (например, «Обсудить с командой»):
        // UI покажет источник вместо пузыря пользователя
        // cause=User: ход, прерванный ради этого сообщения, помечается в истории как
        // остановленный человеком — так же, как его помечает клиент в живой ленте.
        // senderConnectionId: реплику получают остальные устройства, а отправитель её уже показал.
        var outcome = await _sessions.SendMessageAsync(sessionId, text, attachedPaths ?? [], mode, auto: auto,
            senderConnectionId: Context.ConnectionId, cause: Services.SessionManager.DeliveryCause.User,
            senderUserId: UserId);
        return outcome switch
        {
            Services.SessionManager.SendUserOutcome.Started => "started",
            // Ход пришлось прервать ради этого сообщения — клиент отметит это в ленте, иначе
            // голый exited убитого хода прочитается как авария
            Services.SessionManager.SendUserOutcome.QueuedPreempted => "queued-preempted",
            _ => "queued",
        };
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (ScopeOfSession(sessionId, ProjectRight.Configure) is null) throw DeniedOwnerSettings(sessionId);
        _sessions.RespondPermission(sessionId, requestId, behavior);
    }

    // Смена режима прав на лету (переключатель в композере) — применяется к идущему ходу,
    // не дожидаясь следующего сообщения.
    public void SetMode(string sessionId, string mode)
    {
        if (ScopeOfSession(sessionId, ProjectRight.Configure) is null) throw DeniedOwnerSettings(sessionId);
        _sessions.SetMode(sessionId, mode);
    }

    // executions — из DI параметром метода (сервис хабу нужен только здесь, конструктор не трогаем)
    public async Task Interrupt(string sessionId, TaskExecutionService executions)
    {
        if (!OwnerOrTurnAuthor(sessionId)) throw DeniedTurnAuthor(sessionId);
        _sessions.Interrupt(sessionId);
        // «Стоп» в чате исполнителя задачи: прерванный ход result не пришлёт — фиксируем
        // остановку сами, как на доске агентов (BoardController.InterruptAgent)
        await executions.MarkStoppedByUserAsync(sessionId);
    }

    // Ручное сворачивание контекста сессии (/compact)
    public async Task CompactSession(string sessionId)
    {
        if (ScopeOfSession(sessionId, ProjectRight.Configure) is null) throw DeniedOwnerSettings(sessionId);
        await _sessions.CompactAsync(sessionId);
    }

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!OwnerOrTurnAuthor(sessionId)) throw DeniedTurnAuthor(sessionId);
        _sessions.AnswerQuestion(sessionId, toolUseId, answerText);
    }

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback = null)
    {
        if (ScopeOfSession(sessionId, ProjectRight.Configure) is null) throw DeniedOwnerSettings(sessionId);
        _sessions.RespondPlan(sessionId, requestId, approve, feedback);
    }

    // Ответ по карточке плана «Командной реализации» (Э2). decision: run — запустить
    // (единственное согласование итерации), reassign — сменить исполнителя под-задачи
    // (нужны subtaskId + executorPersonaId, карточка остаётся открытой), cancel — отменить,
    // edit — правка плана человеком (нужен feedback): сервер сам пересобирает план и
    // публикует версию vN+1, погасив текущую карточку как заменённую.
    public async Task RespondTeamPlan(string sessionId, string planId, string decision,
        string? subtaskId = null, string? executorPersonaId = null, string? feedback = null)
    {
        if (ScopeOfSession(sessionId, ProjectRight.Configure) is null) throw DeniedOwnerSettings(sessionId);
        var parsed = decision?.ToLowerInvariant() switch
        {
            "run" => TeamPlanDecision.Run,
            "reassign" => TeamPlanDecision.Reassign,
            "cancel" => TeamPlanDecision.Cancel,
            "edit" => TeamPlanDecision.Edit,
            _ => (TeamPlanDecision?)null,
        };
        if (parsed is null) throw new HubException($"Неизвестное решение по плану: {decision}");
        await _sessions.RespondTeamPlanAsync(sessionId, planId, parsed.Value, subtaskId, executorPersonaId,
            userId: UserId, feedback: feedback);
    }

    // Решение человека по карточке остановки «Командной реализации» (Э4). actionId — id
    // кнопки карточки (answer/reassign/addBudget/runNext/…), comment — необязательное
    // пояснение. Кнопка равносильна обычному сообщению в чат: координатор получает ход
    // с решением. Действие приходит ТОЛЬКО отсюда — у агента такого пути нет, поэтому
    // расширить бюджет («Добавить бюджет») может лишь человек.
    public async Task RespondTeamEscalation(string sessionId, string escalationId,
        string? actionId = null, string? comment = null)
    {
        if (ScopeOfSession(sessionId, ProjectRight.Configure) is null) throw DeniedOwnerSettings(sessionId);
        await _sessions.RespondTeamEscalationAsync(sessionId, escalationId, actionId, comment,
            actorUserId: UserId);
    }
}
