using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Team;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Sessions;

namespace ClaudeHomeServer.Controllers;

// Чаты вне проекта: сессии Claude без привязки к проекту, рабочая папка — {домашняя папка}/Chats (UserHomeResolver)
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/chats")]
public class ChatsController(SessionManager sessions, ProjectManager projects, FileService files,
    DefaultAssistantProvisioner provisioner, TeamWaveService teamWaves,
    ChatDigestService digest, ChatArchiveService autoArchive,
    UserStore users, PrefixFingerprintCache prefixFpCache, ChatUserStateService userState,
    IProjectAccess access, ILogger<ChatsController> logger) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Кэш отпечатков префиксов истории (Перф-3 B8) — параметр первичного конструктора,
    // в методах доступен только через поле
    private readonly PrefixFingerprintCache _prefixFpCache = prefixFpCache;

    // Чат принадлежит текущему пользователю и не привязан к проекту
    private Session? OwnedChat(string id)
    {
        var s = sessions.GetById(id);
        return s is not null && s.ProjectId is null && s.OwnerId == UserId ? s : null;
    }

    // Чат проекта виден, но не свой: участнику — 403 not_chat_author, постороннему и
    // несуществующему — 404 (существование не раскрываем); владелец чата проекта на ручках
    // «только чат вне проекта» получает прежний 404
    private IActionResult NotAuthorOrNotFound(string id) =>
        sessions.GetOwnedVisible(id, UserId) is null && sessions.GetAccessible(id, UserId) is { Session.ProjectId: not null }
            ? ChatAuthorGate.Denied()
            : NotFound();

    // Ребёнку чат без ChildSpace неотличим от чужого и несуществующего: одно тело 404 (как в
    // редакторских гейтах ScopeGate) — существование чата не раскрываем (ADR-041, KM-B6f).
    private IActionResult ChildChatNotFound() =>
        NotFound(new { error = "Чат не найден", code = "chat_not_found" });

    [HttpGet]
    public IActionResult GetAll() =>
        Ok(sessions.GetProjectlessChats(UserId));

    // Снимок «у каких чатов прямо сейчас идёт фоновая работа»: id сессий владельца, включая
    // проектные — карточки чатов проекта рисует тот же стор. Нужен потому, что событие
    // bg_agents_presence приходит только на СМЕНЕ состояния: открывший список позже старта
    // иначе не узнал бы о нём до самого завершения. Два списка, а не один: агенты светятся,
    // фоновые команды (Bash с run_in_background) помечаются тихо, и наборы пересекаются.
    [HttpGet("agents-presence")]
    public IActionResult AgentsPresence() => Ok(new
    {
        agents = sessions.GetSessionsWithLiveAgents(UserId),
        commands = sessions.GetSessionsWithLiveBgCommands(UserId),
    });

    // Счётчик «под правило подпадёт N чатов» (настройка автоправила): та же функция отбора, что архивирует тик, — число превью
    // совпадает с результатом прохода при том же моменте времени. projectId — проект,
    // чьи чаты считаем (обязан принадлежать пользователю); без него — личный дефолт:
    // чаты вне проекта. Read-only: ничего не архивирует.
    [HttpGet("archive-preview")]
    public IActionResult ArchivePreview([FromQuery] int days, [FromQuery] string? projectId = null)
    {
        if (days <= 0) return BadRequest(new { error = "Порог архивации должен быть положительным (дней)" });
        if (projectId is not null && projects.GetById(projectId)?.OwnerId != UserId)
            return NotFound();
        var count = sessions.GetArchiveRuleCandidates(UserId, projectId, days, DateTime.UtcNow).Count;
        return Ok(new { count });
    }

    // === Автоправило архивации (шаг 6 плана v4) ===

    // Чтение настройки автоправила — первоначальное состояние экрана настройки:
    // личный порог (он же дефолт проектов без своего и правило чатов вне проектов)
    // и признак «первый проход уже был». Проектный порог сюда не входит — его отдаёт
    // GET /api/projects[/{id}] полем archiveAfterDays. Чтение ничего не меняет.
    [HttpGet("archive-settings")]
    public IActionResult ArchiveSettings()
    {
        var me = users.GetById(UserId);
        if (me is null) return Unauthorized();
        return Ok(new { archiveAfterDays = me.ArchiveAfterDays, hasFirstRun = me.ArchiveRuleFirstRunAt is not null });
    }

    // Личный порог правила: он же дефолт для проектов без своего, он же правило для чатов
    // вне проектов. days = null — сброс (для своей сферы правило выключено).
    [HttpPut("archive-days")]
    public IActionResult SetArchiveDays([FromBody] ArchiveDaysRequest req)
    {
        if (req.Days is not null && req.Days is not (>= 1 and <= 365))
            return BadRequest(new { error = "Порог должен быть от 1 до 365 дней" });
        if (!users.SetArchiveAfterDays(UserId, req.Days)) return Unauthorized();
        return Ok(new { archiveAfterDays = req.Days });
    }

    // Кнопка «Применить сейчас»: запускает РОВНО один проход правила по всем сферам
    // владельца, включая накопившиеся старые чаты (фоновый тик их не трогает до первого
    // прохода), и снимает гейт первого прохода. Повторный клик — снова один проход.
    [HttpPost("archive-run")]
    public async Task<IActionResult> ArchiveRun()
    {
        var (archived, batchId) = await autoArchive.RunNowAsync(UserId, DateTime.UtcNow);
        return Ok(new { archived, batchId });
    }

    // Откат пачки одного прохода правила (кнопка в уведомлении/разделе «Архив»): возвращает
    // ровно чаты этого batchId, а не всю историю правила. Это возврат
    // уже убранного, как ручной «Вернуть из архива».
    [HttpPost("archive-batch/{batchId}/restore")]
    public async Task<IActionResult> RestoreArchiveBatch(string batchId)
    {
        var restored = await sessions.RestoreArchiveBatchAsync(UserId, batchId);
        return Ok(new { restored });
    }

    // Получить сессию по ID независимо от типа (проектная / чат вне проекта).
    // Используется для ссылки «Связанная сессия» в карточке задачи без проекта.
    [ChildAllowed]
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var child = ChildSubjectAccess.IsChild(HttpContext, UserId);
        if (child)
        {
            // Ребёнку — только его ChildSpace-чаты, независимо от ProjectId (Б9d): урок — чат
            // проекта-предмета (Б9b), поэтому граница — владение (GetOwnedVisible: владельца
            // урока резолвит проект-предмета), а не «чат вне проекта». Свой без ChildSpace,
            // чужой и несуществующий — одно тело 404 (ADR-041).
            if (sessions.GetOwnedVisible(id, UserId) is { } own && own.ChildSpace is not null) return Ok(own);
            return ChildChatNotFound();
        }
        // Чтение: чат виден и участнику общего проекта (GetAccessible), запись остаётся на GetOwned
        if (sessions.GetAccessible(id, UserId) is { } found) return Ok(found.Session);
        return NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateChatRequest req)
    {
        // FIN3-2 (доработка по возврату ревью cea595f7): тот же гейт, что на POST /sessions
        // (SessionsController.cs:55). resume принимается только для транскрипта, видимого
        // вызывающему: ClaudeSessionId = resumeSessionId (ChatCreation.cs:559) — ключ истории
        // (HistoryKeyOf) и транскрипта CLI; новый чат с csid чужого приватного получил бы его
        // разговор через эту дверь. Отказ неотличим от несуществующего. Неизвестный ключ не
        // трогается: ветвление подаёт свой новый csid до регистрации чата, внешний транскрипт —
        // законный resume. Гейт в контроллере, не в сервисе: исключение из StartNewSessionAsync
        // дало бы 400 в catch ниже, а нужен 404.
        if (req.ResumeSessionId is { Length: > 0 } resumeId && sessions.IsTranscriptReservedForOthers(resumeId, UserId))
            return NotFound(new { error = "Чат не найден" });
        string? personaId = req.PersonaId;
        // Последний рубеж инварианта «новый чат человека — только с персоной»: без
        // personaId/resumeSessionId сначала провижним ассистента и продолжим создание с ним.
        // Работает в паре с фронтом — тот создаёт чаты через хелпер createChatWithContextPersona,
        // минуя этот гейт; вместе они лечат и пустой дефолт, и осиротевший. 400 остаётся только
        // когда провижн невозможен (сбой создания).
        // Групповые чаты (createGroup) и служебные пути сюда не доходят.
        if (string.IsNullOrWhiteSpace(personaId) && string.IsNullOrWhiteSpace(req.ResumeSessionId))
        {
            var provisioned = await provisioner.EnsureAsync(UserId, HttpContext.RequestAborted);
            if (provisioned is null)
                return BadRequest(new { error = "Новый чат создаётся только с персоной: укажите personaId" });
            personaId = provisioned.Id;
        }
        var mode = ClaudeModeExtensions.ForNewChat(req.Mode, personaId is not null);
        try
        {
            var chat = await sessions.CreateChatAsync(UserId, mode, req.ResumeSessionId, req.Name,
                req.Model, req.Effort, personaId: personaId);
            return CreatedAtAction(nameof(GetAll), new { }, chat);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Групповой чат (флаг persona-group-chats): 2-8 персон, первая — ведущая.
    // Зона — по ведущей: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    // Намеренно идёт мимо инварианта «новый чат человека — только с персоной» (гейт в Create):
    // инвариант ловит одиночный чат БЕЗ персоны, а группа по определению состоит из персон
    // (CreateGroupChatAsync сам валидирует состав), поэтому отдельная проверка здесь избыточна.
    [HttpPost("group")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupChatRequest req)
    {
        var mode = ClaudeModeExtensions.ForNewChat(req.Mode, hasPersona: true);
        var ids = req.PersonaIds ?? [];
        // W3-B4c (разрез §3.2): зона — по ведущей: ResolvePersona с правом хода (И-1), сервис зовётся
        // по арендатору проекта ведущей (И-2), вызывающий становится автором. Группа вне проекта
        // (своя глобальная ведущая или пустой состав) — прежний путь по UserId.
        var tenantId = UserId;
        if (ids.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) is { } leadId)
        {
            var leadScope = access.ResolvePersona(UserId, leadId, ProjectRight.RunTurn);
            if (leadScope is null)
                // Та же грань, что у фильтра [ProjectScope]: видимому с Read — нет права вести ход (403),
                // остальное — несуществующее (404, существование не раскрываем). Глобальная владельца
                // участнику не видна вовсе (И-3).
                return access.ResolvePersona(UserId, leadId, ProjectRight.Read) is not null
                    ? new ObjectResult(new { error = "Нет права вести ход", code = ProjectScopeAttribute.ErrorCode })
                        { StatusCode = StatusCodes.Status403Forbidden }
                    : NotFound();
            tenantId = leadScope.TenantOwnerId;
        }
        try
        {
            var chat = await sessions.CreateGroupChatAsync(tenantId, ids, mode, req.Name,
                createdByUserId: UserId);
            return Ok(chat);
        }
        // Групповой чат общего проекта с глобальной персоной в составе (доработка R-S2a-8):
        // 400 с машинным кодом — тот же, что при смене собеседника.
        catch (GlobalPersonaInSharedException ex)
        { return BadRequest(new { error = ex.Message, code = GlobalPersonaInSharedException.Code }); }
        // И-3 (W3-B4c): не-арендатору состав с персоной не этого проекта — 404, как несуществующая.
        catch (KeyNotFoundException) when (UserId != tenantId) { return NotFound(); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Обновить состав участников группового чата (спикер сохраняется, если остался,
    // иначе — новая ведущая). Работает и для проектной сессии группового чата.
    [HttpPut("{id}/participants")]
    public IActionResult SetParticipants(string id, [FromBody] SetParticipantsRequest req)
    {
        // S2d-3f (дефект f1e4666d): гейт видимости и права записи (тот же приём, что у /parent, /retitle,
        // /archived): на чужом приватном чате — 404, неотличимый от «нет такого чата»; читатель — 403:
        // состав группового чата — настройка чата, то же право, что у переименования и архива.
        // W3-B4c (разрез §3.2): сервис зовётся по арендатору чата (И-2): состав группового чата общего
        // проекта ведёт редактор, ключ — T, а не вызывающий.
        if (sessions.DenyContentWrite(id, UserId) is { } denied) return denied;
        var (visible, scope) = sessions.GetAccessible(id, UserId)!.Value;
        // Приватный чат остаётся личным: его состав меняет арендатор пространства чата. Автор приватного
        // чата общего проекта — не арендатор: тот же 404, что и до шага (S2d-3f).
        if (visible.PrivateToUserId is not null && scope.TenantOwnerId != UserId) return NotFound();
        // S2d-7b-3: приватный чат не становится групповым (общий состав персон — режим общего контура).
        if (FeatureChatDoor.DenyPrivateMode(visible) is { } privateMode)
            return Conflict(new { error = privateMode, code = FeatureChatDoor.PrivateChatCode });
        try
        {
            var updated = sessions.SetParticipants(id, scope.TenantOwnerId, req.PersonaIds ?? [],
                callerUserId: UserId);
            return updated is null ? NotFound() : Ok(updated);
        }
        // Гонка с тумблером видимости: приватность проверена повторно под замком (S2d-7b-3).
        catch (Services.PrivateChatModeException ex)
        { return Conflict(new { error = ex.Message, code = FeatureChatDoor.PrivateChatCode }); }
        // Глобальная персона в состав чата общего проекта (доработка R-S2a-8): тот же код отказа.
        catch (GlobalPersonaInSharedException ex)
        { return BadRequest(new { error = ex.Message, code = GlobalPersonaInSharedException.Code }); }
        // И-3 (W3-B4c): не-арендатору состав с персоной не этого проекта — 404, как несуществующая.
        catch (KeyNotFoundException) when (UserId != scope.TenantOwnerId) { return NotFound(); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateChatRequest req)
    {
        if (OwnedChat(id) is null) return NotAuthorOrNotFound(id);
        if (req.Pinned is bool pinned) sessions.SetPinned(id, pinned);
        if (req.NotificationsMuted is bool muted) sessions.SetNotificationsMuted(id, muted);
        // Хотя бы одно из двух: стиль приезжает и отдельным запросом, без флага (устройство
        // выправляет чужой стиль у чата с уже включённой озвучкой) — условие «есть VoiceMode»
        // такой запрос молча потеряло бы
        if (req.VoiceStyle is not null && !VoiceStyles.IsKnown(req.VoiceStyle))
            return BadRequest(new { error = "Неизвестный стиль озвучки" });
        if (req.VoiceMode is not null || req.VoiceStyle is not null)
            sessions.SetVoiceMode(id, req.VoiceMode, req.VoiceStyle);
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(id, req.ExpiresAfterMinutes);
        }
        try
        {
            var updated = await sessions.UpdateAsync(id, UserId, req.Name, req.Model, req.Effort);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // «Продолжить в стандартном окне 200 тысяч токенов» — кнопка под карточкой отказа
    // Window1MUnavailable. Серверный путь к StripClaudeWindowAlias: сам по себе срез окна в ходе
    // чата запрещён (тихая деградация длинного разговора в 200K — мина), поэтому снять суффикс
    // может только явное решение человека, и приходит оно сюда. Возвращает обновлённый чат:
    // фронт по нему перерисовывает выбранную модель, отдельного сигнала не нужно.
    // Как и MigrateProvider/SetArchived/SetWorkLoop, работает и для проектной сессии:
    // фронт рисует ту же кнопку (ChatPanel без ветвления по проекту), а GetOwnedVisible
    // резолвит владельца через проект — единственный разрез, где OwnedChat (только чаты вне
    // проекта) был бы неправ. На чужом приватном чате — 404, как на несуществующем (S2d-3a).
    [HttpPost("{id}/window-1m/drop")]
    public async Task<IActionResult> DropWindow1M(string id)
    {
        if (sessions.GetOwnedVisible(id, UserId) is null) return NotFound();
        try
        {
            var updated = await sessions.DropWindow1MAsync(id, UserId);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Ручная группировка чатов (drag-and-drop в списке): вложить чат в родительский либо
    // вынести в корень (parentId == null). Один эндпоинт на оба списка — GetOwnedVisible здесь
    // резолвит и проектную сессию (как /loop), поэтому дубля в SessionsController нет; внутри
    // SetParent остаётся сверка автора-арендатора (GetOwned в сервисе). На чужом приватном
    // чате — 404, как на несуществующем (S2d-3e).
    [HttpPut("{id}/parent")]
    public IActionResult SetParent(string id, [FromBody] SetParentRequest req)
    {
        if (sessions.GetOwnedVisible(id, UserId) is null) return NotFound();
        try
        {
            var updated = sessions.SetParent(id, req.ParentId, UserId);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Отметить чат прочитанным (синк непрочитанности между устройствами). Один эндпоинт
    // на оба списка (как /parent и /loop): вход — GetAccessible (Read: видимость, не владение),
    // поэтому дубля в SessionsController нет. Отметка не двигает UpdatedAt и не поднимает чат
    // в списке.
    // Участник общего проекта отмечает СВОЮ прочитанность (оверлей ChatUserStateStore): у владельца
    // и у других участников она не меняется. У владельца отметка идёт через MarkRead (внутри
    // сервиса — GetOwned: автор-арендатор).
    [HttpPut("{id}/read")]
    public IActionResult MarkRead(string id)
    {
        if (sessions.GetAccessible(id, UserId) is not { } found) return NotFound();
        if (userState.IsMember(found.Session, UserId))
        {
            userState.MarkRead(UserId, found.Session);
            return NoContent();
        }
        return sessions.MarkRead(id, UserId) ? NoContent() : NotFound();
    }

    // Личные отметки чата «закреплён» и «уведомления заглушены» (тело: pinned, notificationsMuted —
    // null не меняет). Нужны отдельной ручкой: PUT {id} правит и имя/модель, а проектный
    // PUT сессии требует права записи, которого у читателя нет, — личная отметка записью в проект
    // не является. Владелец пишет в поля Session (как прежде), участник — в свой оверлей; ответ
    // с сессией проходит конвертер и отдаёт вызывающему его собственные значения.
    [HttpPut("{id}/user-state")]
    public IActionResult SetUserState(string id, [FromBody] UserStateRequest req)
    {
        if (sessions.GetAccessible(id, UserId) is not { } found) return NotFound();
        if (userState.IsMember(found.Session, UserId))
        {
            if (req.Pinned is bool pinned) userState.SetPinned(UserId, id, pinned);
            if (req.NotificationsMuted is bool muted) userState.SetMuted(UserId, id, muted);
        }
        else
        {
            if (req.Pinned is bool pinned) sessions.SetPinned(id, pinned);
            if (req.NotificationsMuted is bool muted) sessions.SetNotificationsMuted(id, muted);
        }
        return Ok(found.Session);
    }

    // Убрать чат в архив (archived=true) / вернуть (false) — шаг 2 плана «Архив чатов»:
    // архив ПРЯЧЕТ чат, а не удаляет, история и claudeSessionId целы. Как /parent и /loop,
    // работает и для проектной сессии. QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2):
    // архив — право записи чата (DenyContentWrite: редактор и выше; читатель — 403 project_scope,
    // посторонний и чужой приватный — 404), а не владение. Сервис зовётся по арендатору из
    // области запроса (тот же приём, что у files/upload ниже): ключ архивной записи — арендатор,
    // решение о праве — в контроллере.
    // 409 на живом ходе/фоновых агентах: архивация идущей работы рвала бы её (пре-мортем
    // №3 плана v4); текст ошибки — человекочитаемый («в чате идёт ход»).
    [HttpPut("{id}/archived")]
    public async Task<IActionResult> SetArchived(string id, [FromBody] SetArchivedRequest req)
    {
        if (sessions.DenyContentWrite(id, UserId) is { } denied) return denied;
        var (_, scope) = sessions.GetAccessible(id, UserId)!.Value;
        try
        {
            var updated = await sessions.SetArchivedAsync(id, scope.TenantOwnerId, req.Archived);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Тумблер видимости чата (S2d-7a): { private: true } — сделать приватным (виден только автору),
    // { private: false } — снова общим. Первый писатель Session.PrivateToUserId.
    // Видимость — SessionManager.GetAccessible (CanSee): чужой приватный и несуществующий чат
    // неотличимы, оба 404. Это писатель для АВТОРА чата (редактора общего проекта в том числе), а не
    // арендатора, поэтому GetOwnedVisible (владелец проекта) не годится — он не пустил бы редактора.
    // Читатель — 403 reader, не автор — 403 not_chat_author. Остальное (общий проект, «сообщений нет»,
    // атомарность с отправкой) — в SessionManager.SetChatVisibility под замком видимости.
    [HttpPut("{id}/visibility")]
    public async Task<IActionResult> SetVisibility(string id, [FromBody] SetVisibilityRequest req)
    {
        if (sessions.GetAccessible(id, UserId) is not { } found) return NotFound();
        if (found.Scope.Role == ProjectRole.Reader)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Читатель проекта не меняет видимость чата", code = "reader" });
        var result = sessions.SetChatVisibility(id, UserId, req.Private);
        if (result.Outcome == ChatVisibilityOutcome.Ok && req.Private)
            await sessions.EvictForeignViewersAsync(id, UserId);
        return result.Outcome switch
        {
            // Карта «старый → новый путь» перенесённых вложений (S2d-6b-4) едет рядом с чатом: композеру
            // заменить ссылки в черновике. Без переноса поле отсутствует, форма ответа прежняя.
            ChatVisibilityOutcome.Ok => Ok(WithAttachmentMoves(result)),
            ChatVisibilityOutcome.AttachmentsMoveFailed => Conflict(new { error = "Не удалось перенести вложения чата — видимость не изменена", code = "attachments_move_failed" }),
            ChatVisibilityOutcome.NotAuthor => ChatAuthorGate.Denied(),
            ChatVisibilityOutcome.NotSharedProject => Conflict(new { error = "Приватным можно сделать только чат общего проекта", code = "not_shared_project" }),
            ChatVisibilityOutcome.BoundToFeature => Conflict(new { error = "Чат уже привязан к фиче — приватным его не сделать", code = FeatureChatDoor.PrivateChatCode }),
            ChatVisibilityOutcome.TeamOrGroup => Conflict(new { error = FeatureChatDoor.ModeChatPrivateReason, code = FeatureChatDoor.PrivateChatCode }),
            ChatVisibilityOutcome.HasMessages => Conflict(new { error = "В чате уже есть сообщения — приватным его не сделать", code = "chat_has_messages" }),
            _ => NotFound(),
        };
    }

    // Чат + карта переноса вложений (S2d-6b-4): поле attachmentMoves ({старый путь: новый}) рядом с полями
    // чата, композеру заменить ссылки черновика. Нет переноса — прежняя форма ответа (голый чат).
    private object? WithAttachmentMoves(ChatVisibilityResult result)
    {
        if (result.AttachmentMoves is not { Count: > 0 } moves) return result.Session;
        var options = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;
        var node = System.Text.Json.JsonSerializer.SerializeToNode(result.Session, options)!.AsObject();
        node["attachmentMoves"] = System.Text.Json.JsonSerializer.SerializeToNode(moves, options);
        return node;
    }

    // Сводка карточки архива (шаг 5 плана «Архив чатов»): кнопка «Собрать сводку» строит
    // 2–3 предложения о чём был разговор (место chat-digest) и кэширует их в чате — свежая
    // сводка отдаётся из кэша без обращения к модели. Как /archived, работает и для
    // проектной сессии: гейт — GetOwnedVisible (S2d-3a), внутри сервиса — GetOwned.
    // Это НЕ «Итог сессии»: вынос в заметки — отдельная кнопка через существующий
    // POST /api/sessions/{id}/summary, его контракт здесь не участвует.
    [HttpPost("{id}/digest")]
    public async Task<IActionResult> BuildDigest(string id, CancellationToken ct)
    {
        if (sessions.GetOwnedVisible(id, UserId) is null) return NotFound();
        try
        {
            return Ok(await digest.BuildDigestAsync(UserId, id, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DigestInProgressException ex) { return Conflict(new { error = ex.Message }); }
        catch (DigestGenerationException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Назначить/снять собеседника у чата ДО первого хода (селектор в пустом чате):
    // персону (personaId) или .md-агента (agentName) — взаимоисключающе; оба пустые = снять.
    // Начатую сессию менять нельзя (клиент делает форк).
    [HttpPost("{id}/persona")]
    public IActionResult SetPersona(string id, [FromBody] SetPersonaRequest req)
    {
        if (OwnedChat(id) is null) return NotFound();
        try
        {
            var updated = sessions.SetPersona(id, UserId, req.PersonaId, req.AgentName);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Цикл «до готово» (флаг work-loop): вкл/выкл автопродолжения хода до маркера
    // завершения. Работает и для проектной сессии: гейт — DenyOwnerTurn (право Configure через
    // IProjectAccess.ResolveSession, с видимостью чата: чужой приватный — 404, S2d-3a).
    [HttpPut("{id}/loop")]
    public async Task<IActionResult> SetWorkLoop(string id, [FromBody] SetWorkLoopRequest req)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        try
        {
            // manual: true — эндпоинт дёргает только человек через тумблер UI; явное
            // сообщение об остановке (B5) шлётся внутри SetWorkLoopAsync на переходе true→false
            var updated = await sessions.SetWorkLoopAsync(id, req.Enabled, manual: true);
            return updated is null ? NotFound() : Ok(updated);
        }
        // Гард B4: автопилот и «Командная реализация» не сочетаются в одном чате
        catch (Services.SessionModeConflictException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // Режим «Командная реализация»: вкл/выкл режима чата-штаба. При включении можно сразу
    // задать состав (пустой/null список исполнителей = вся команда проекта). Как loop,
    // работает и для проектной сессии (тот же гейт DenyOwnerTurn).
    [HttpPut("{id}/team-implement")]
    public async Task<IActionResult> SetTeamImplement(string id, [FromBody] SetTeamImplementRequest req)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        // S2d-7b-3: приватный чат не становится штабом; выключение режима разрешено всегда.
        if (req.Enabled && FeatureChatDoor.DenyPrivateMode(sessions.GetById(id)) is { } privateMode)
            return Conflict(new { error = privateMode, code = FeatureChatDoor.PrivateChatCode });
        try
        {
            var updated = await sessions.SetTeamImplementAsync(id, req.Enabled,
                req.AutoWaves, req.CoordinatorPersonaId, req.PlannerPersonaId,
                req.ExecutorPersonaIds, UserId, req.CoordinatorNoCode);
            return updated is null ? NotFound() : Ok(updated);
        }
        // Гонка с тумблером видимости: приватность проверена повторно под замком (S2d-7b-3).
        catch (Services.PrivateChatModeException ex)
        { return Conflict(new { error = ex.Message, code = FeatureChatDoor.PrivateChatCode }); }
        // Гард на входе (B2): нет координатора либо состава. Код отказа машинный — фронт по
        // нему показывает пикер и НЕ отправляет вводную обычным сообщением.
        catch (TeamImplementSetupException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        // Гард B4: автопилот и «Командная реализация» не сочетаются в одном чате
        catch (Services.SessionModeConflictException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // «Остановить» (Э4): текущие исполнители дорабатывают, новые волны не стартуют.
    // Снимается решением человека по карточке остановки («Продолжить»). Карточку возврата
    // публикует StopTeamImplementAsync — единая точка остановки вместе с кнопкой «Остановить»
    // информационной карточки добавочной волны.
    [HttpPut("{id}/team-implement/stop")]
    public async Task<IActionResult> StopTeamImplement(string id)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        var updated = await sessions.StopTeamImplementAsync(id, UserId);
        return updated is null ? NotFound() : Ok(updated);
    }

    // Переключение авто-волн на ходу (из бейджа режима): не трогает сам режим, только флаг.
    // Как loop, работает и для проектной сессии (тот же гейт DenyOwnerTurn).
    [HttpPut("{id}/team-implement/auto")]
    public async Task<IActionResult> SetTeamImplementAuto(string id, [FromBody] SetTeamImplementAutoRequest req)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        var updated = await sessions.SetTeamImplementAutoAsync(id, req.AutoWaves, UserId);
        return updated is null ? NotFound() : Ok(updated);
    }

    // Снапшот волны «Командной реализации» для поповера бейджа (КР-наблюдаемость, этап 1):
    // поля пульса (те же, что у WS-события team_wave_pulse) + задачи волны + применённые
    // пороги. 404 — чужой чат, режим выключен либо стадия не в работе (Wave/Checking):
    // снапшота нет, поповер открывается только у живого бейджа волны. Считается тем же
    // методом, что и пульс (TeamWaveService.BuildWaveSnapshot), — живая лента и REST
    // показывают одно и то же. startedAt задачи — ClaudeStartedAt (отметка запуска
    // исполнителя): отдельного поля старта у задачи нет.
    [HttpGet("{id}/team-wave-snapshot")]
    public IActionResult GetTeamWaveSnapshot(string id)
    {
        // Сессию берём из GetAccessible одним lookup'ом: повторный GetById — двойная работа
        // и NRE→500, если чат удалён между вызовами (ревью этапа 1).
        if (sessions.GetAccessible(id, UserId) is not { } found) return NotFound();
        if (teamWaves.BuildWaveSnapshot(found.Session) is not { } snap) return NotFound();
        return Ok(new
        {
            sessionId = id,
            stage = snap.Stage.ToWireToken(),
            waveNumber = snap.WaveNumber,
            plannedWaves = snap.PlannedWaves,
            tasksActive = snap.TasksActive,
            tasksTotal = snap.TasksTotal,
            lastActivityAt = snap.LastActivityAt,
            quietSeconds = snap.QuietSeconds,
            liveness = TeamWaveService.LivenessToken(snap.Liveness),
            tasks = snap.WaveTasks.Select(t => new
            {
                id = t.Id,
                title = t.Title,
                executorPersonaId = t.PersonaId,
                status = t.Status,
                updatedAt = t.UpdatedAt,
                startedAt = t.ClaudeStartedAt,
            }),
            thresholds = new { quietMinutes = snap.QuietMinutes, stalledMinutes = snap.StalledMinutes },
        });
    }

    // === КР-наблюдаемость, этап 3: перезапуск без потери работы ===

    // Перезапуск одной под-задачи волны (кнопка в строке задачи поповера): тот же путь
    // перевыдачи, что у провала хода координатора, — потолок перевыдач бюджета общий.
    // 409 с человеческим текстом — гейт (живая задача, завершённая, повторный клик).
    [HttpPost("{id}/team-wave/tasks/{taskId}/restart")]
    public async Task<IActionResult> RestartWaveTask(string id, string taskId)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        if (sessions.GetOwnedVisible(id, UserId) is not { } session) return NotFound();
        try
        {
            var result = await teamWaves.RestartWaveTaskAsync(session, taskId);
            return Ok(new { outcome = result.Outcome, message = result.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    // Перезапуск волны (кнопка в поповере при liveness stalled/dead): пере-раздача
    // незакрытого, Done не трогается. Живые исполнения — сначала предупреждение
    // (requiresConfirm + liveTasks), раздача идёт повторным вызовом с confirm=true.
    [HttpPost("{id}/team-wave/restart")]
    public async Task<IActionResult> RestartWave(string id, [FromBody] TeamWaveRestartRequest? req)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        if (sessions.GetOwnedVisible(id, UserId) is not { } session) return NotFound();
        try
        {
            var result = await teamWaves.RestartWaveAsync(session, req?.Confirm == true);
            return Ok(new
            {
                requiresConfirm = result.RequiresConfirm,
                liveTasks = result.LiveTasks,
                reissued = result.Reissued,
                escalated = result.Escalated,
                failed = result.Failed,
                message = result.Message,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    // Перезапуск зависшего хода штаба (главный сценарий): чат занят, процесс молчит,
    // написать в него нельзя. Kill → ожидание смерти → валидация транскрипта → новый ход
    // с --resume; отложенные сообщения уходят, режим и прогресс не теряются. Повреждённый
    // транскрипт — 409 c code=transcript_damaged: фронт предлагает «начать ход заново»
    // (повторный вызов с startFresh=true сбрасывает контекст и возвращает чат в работу).
    [HttpPost("{id}/team-wave/restart-turn")]
    public async Task<IActionResult> RestartTeamWaveTurn(string id, [FromBody] TeamTurnRestartRequest? req)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        try
        {
            var result = await sessions.RestartStuckTurnAsync(id, req?.StartFresh == true);
            return Ok(new { outcome = result.Resumed ? "restarted" : "fresh", resumed = result.Resumed, message = result.Message });
        }
        catch (TurnTranscriptDamagedException ex)
        {
            return Conflict(new { error = ex.Message, code = "transcript_damaged" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    public sealed record TeamWaveRestartRequest(bool? Confirm);
    public sealed record TeamTurnRestartRequest(bool? StartFresh);

    // Отдельное git worktree чата: вкл — сессия переезжает в изолированное дерево на новой
    // ветке (начатый чат — с переносом контекста), выкл — возврат в корень проекта.
    // Force подтверждает потерю несохранённых правок дерева. Как loop, работает и для
    // проектной сессии: гейт — GetOwnedVisible (S2d-3a).
    [HttpPut("{id}/worktree")]
    public async Task<IActionResult> SetWorktree(string id, [FromBody] SetWorktreeRequest req)
    {
        if (sessions.GetOwnedVisible(id, UserId) is null) return NotFound();
        try
        {
            var updated = await sessions.SetWorktreeAsync(id, req.Enabled, req.Branch, req.Force);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Services.Git.GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Режим прав (permission mode) чата. Отдельный эндпоинт нужен, чтобы выбор в Composer
    // сохранялся сразу, а не только вместе со следующим сообщением: иначе уход со страницы
    // до первого хода откатывал его на прежний. Как и loop, работает для проектной сессии.
    [HttpPut("{id}/mode")]
    public IActionResult SetMode(string id, [FromBody] SetModeRequest req)
    {
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        try
        {
            var updated = sessions.SetMode(id, req.Mode);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Миграция начатого чата на другого провайдера (кнопка «Продолжить на …» при
    // исчерпании лимита подписки): транскрипт CLI переносится в профиль целевого
    // провайдера, разговор продолжается через --resume с сохранением контекста.
    // Как и loop, работает и для проектной сессии (владелец резолвится через проект).
    // SubscriptionKey — явный выбор аккаунта пула подписок (кнопка карточки с
    // Kind="subscription"); пусто — старое поведение (сторонний провайдер по Model
    // либо автовыбор аккаунта пула).
    [HttpPost("{id}/migrate-provider")]
    public async Task<IActionResult> MigrateProvider(string id, [FromBody] MigrateProviderRequest req)
    {
        // Модель обязательна: у кнопки «Продолжить на …» она всегда есть, а пустая означала бы
        // для MigrateProviderAsync «переехать на родной Claude без закреплённой модели» — не то,
        // о чём просит эндпоинт.
        if (string.IsNullOrWhiteSpace(req.Model))
            return BadRequest(new { error = "Не указана модель" });
        if (sessions.DenyOwnerTurn(id, UserId) is { } denied) return denied;
        try
        {
            return Ok(await sessions.MigrateProviderAsync(id, UserId, req.Model, req.SubscriptionKey));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Ветвление чата (шаг 4 плана «Ветвление чата», контракт §6
    // docs/research/chat-branching-2026-09.md): новый чат с обрезанной по якорному
    // сообщению историей и транскриптом CLI. Эндпоинт — тонкая обёртка: гейты §9,
    // резак транскрипта и наследование полей §11 живут целиком в
    // SessionManager.BranchAsync, здесь их не дублируем. Как migrate-provider, один
    // эндпоинт покрывает и чаты вне проекта, и проектные сессии — GetOwnedVisible здесь
    // резолвит владельца через проект и не пускает на чужой приватный чат (404, S2d-3e):
    // ветвление не копирует его транскрипт и не плодит чат-преемник. Внутри BranchAsync
    // остаётся сверка автора-арендатора (GetOwned в сервисе).
    // Коды: 404 — чат не найден/чужой (KeyNotFoundException из BranchAsync).
    // 409 — ЛЮБОЙ отказ BranchAsync (InvalidOperationException → Conflict, конвенция
    // SetArchivedAsync). Известное ограничение: BranchAsync кидает один и тот же тип
    // исключения и для «идёт ход»/«граница не сопоставлена» (по документу — 409), и для
    // «ветвить нечего» — нет ClaudeSessionId, десктопный чат, групповой/штаб, транскрипт
    // не найден, потолок размера (по документу — 400); различить их в контроллере можно
    // только парсингом текста ошибки, а это и есть «логика гейтов в контроллере»,
    // которую задача прямо запрещает. Двойной клик — идемпотентности здесь нет
    // сознательно, кнопку блокирует фронт (шаг 7).
    [HttpPost("{id}/branch")]
    public async Task<IActionResult> Branch(string id, [FromBody] BranchChatRequest req)
    {
        // Гейт ДО BranchAsync: на чужом приватном чате не должно быть ни резака транскрипта,
        // ни нового чата-преемника с его содержимым (S2d-3e)
        if (sessions.GetOwnedVisible(id, UserId) is null) return NotFound();
        try
        {
            var result = await sessions.BranchAsync(
                id, UserId, req.UserMessageIndex, req.AnchorText, req.Include, req.Name);
            return Ok(new
            {
                chatId = result.Session.Id,
                projectId = result.Session.ProjectId,
                claudeSessionId = result.Session.ClaudeSessionId,
                draft = result.Draft,
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // История чата вне проекта. Без after — полный плоский массив (прежний контракт).
    // С after (+ fp) — докачка хвоста (Перф-2 B5): объект { reset, messages, anchorIndex,
    // anchorFp }, как у проектной сессии (SessionsController.GetHistory) — одна логика в
    // ChatHistoryTail. Изоляция per-owner: взрослому — OwnedChat (чат вне проекта), ребёнку —
    // GetOwnedVisible (Б9d: урок — чат проекта-предмета, граница — владение + ChildSpace).
    [ChildAllowed]
    [HttpGet("{id}/history")]
    public async Task<IActionResult> GetHistory(string id, [FromQuery] int? after = null, [FromQuery] string? fp = null)
    {
        var child = ChildSubjectAccess.IsChild(HttpContext, UserId);
        var session = child ? sessions.GetOwnedVisible(id, UserId) : OwnedChat(id);
        // Чужой и несуществующий ребёнку неотличимы: то же тело, что и «не детское пространство»
        if (session is null) return child ? ChildChatNotFound() : NotFound();
        // Ребёнку — только детские пространства (ADR-041): чат без ChildSpace неотличим от чужого
        if (child && session.ChildSpace is null) return ChildChatNotFound();
        var history = await sessions.GetHistoryAsync(id);
        if (after is null) return Ok(history);
        // Ключ кэша — csid (HistoryKeyOf): отпечатки префиксов не пересчитываются на каждый запрос
        var tail = ChatHistoryTail.Compute(history, after, fp,
            _prefixFpCache.GetOrCompute(session.ClaudeSessionId ?? id, history));
        return Ok(new
        {
            reset = tail.Reset,
            messages = tail.Messages,
            anchorIndex = tail.AnchorIndex,
            anchorFp = tail.AnchorFp,
        });
    }

    // Обновить название чата по текущей переписке (AI-хаб): в отличие от авто-имени по первому
    // сообщению — по всему транскрипту, с перезаписью. Как loop, работает и для проектной сессии.
    // QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2): гейт — право записи чата
    // (DenyContentWrite: редактор и выше; читатель — 403 project_scope), а не владение: редактор
    // переименовывает и чужой общий чат. На отказанном чате LLM не зовётся вовсе (и чужой
    // приватный — 404, как на несуществующем). Модель зовётся по арендатору из области запроса:
    // LLM-расход, как прежде, учитывается на арендаторе (тот же приём, что у files/upload ниже).
    [HttpPost("{id}/retitle")]
    public async Task<IActionResult> Retitle(string id, CancellationToken ct)
    {
        if (sessions.DenyContentWrite(id, UserId) is { } denied) return denied;
        var (_, scope) = sessions.GetAccessible(id, UserId)!.Value;
        try
        {
            var updated = await sessions.RetitleAsync(scope.TenantOwnerId, id, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    // Секция destructive: на делегированном ходу агент не удаляет чаты (см. FilesController.Delete)
    [DenyOnDelegatedTurn("Удаление чата")]
    public async Task<IActionResult> Delete(string id)
    {
        if (OwnedChat(id) is null) return NotAuthorOrNotFound(id);
        await sessions.DeleteAsync(id);
        return NoContent();
    }

    // Загрузка вложения в рабочую папку чата (в подпапку .cc-attachments) → относительный путь.
    // Единый путь для обоих типов чата: у чата вне проекта папка — {дом}/Chats, у проектного —
    // рабочая папка сессии (GetChatRoot). Гейт — право вести ход (DenyTurn, S2a-10), а не
    // владение: прежний GetOwnedVisible резолвил владельца по владельцу проекта и давал
    // редактору общего проекта 404 на его собственном чате (дефект 6bcdca12, QA-D3). Отказ
    // различим: невидимому/не-участнику — 404, видимому без права хода (читателю) — 403
    // project_scope. Корень берётся по арендатору из области запроса (scope.TenantOwnerId),
    // а не по вызывающему: у проектного чата ResolveOwnerId — владелец проекта. Запись в учёт
    // — по вызывающему: он автор private/{автор} (S2d-6b-1), и только после пройденного гейта.
    [HttpPost("{id}/files/upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(string id, IFormFile? file = null)
    {
        if (sessions.DenyTurn(id, UserId) is { } denied) return denied;
        var (chat, scope) = sessions.GetAccessible(id, UserId)!.Value;
        // Вложения локального проекта принимает агент устройства, не сервер (ADR-016 §4)
        if (chat.ProjectId is { } pid && projects.GetById(pid) is { } project
            && ProjectCapabilityGuard.Refusal(project, ProjectCapabilityArea.FileBound) is { } refusal)
            return ProjectCapabilityGuard.Denied(refusal);
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не выбран или пустой" });

        var root = sessions.GetChatRoot(id, scope.TenantOwnerId);
        if (root is null) return NotFound();

        // Правило пути одно с агентом устройства: {AttachmentsDir}/{guid}/{имя файла}.
        // Вложение приватного проектного чата (S2d-6b-1) — в закрытое поддерево
        // private/{автор}/…: принадлежность читается из пути, её закрывают на чтение 6b-2/6b-3.
        // Раскладку выбирает SessionManager.SaveChatAttachment под замком видимости (S2d-6b-4):
        // автор — только из JWT, а переключение видимости не может пройти между выбором пути и записью.

        // Вложения не должны светиться в git-статусе проекта и уезжать в историю по `git add -A`.
        // Лениво, до записи файла: у проекта со своим .gitignore дефолтный игнор не создавался.
        try { AttachmentsGitExclude.Ensure(root); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось записать игнор вложений для {Root}", root); }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var rel = sessions.SaveChatAttachment(id, UserId, file.FileName, r => files.WriteFileBytes(root, r, bytes));
        if (rel is null)
            return BadRequest(new { error = "Некорректное имя файла" });
        return Ok(new { path = rel });
    }
}

// PersonaId — собеседник нового чата; под флагом default-personas-onboarding обязателен
// (либо resumeSessionId — продолжение существующего разговора).
public record CreateChatRequest(string? Mode = null, string? ResumeSessionId = null, string? Name = null, string? Model = null, string? Effort = null, string? PersonaId = null);

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать чат постоянным;
// N > 0 — временный, авто-удаление через N минут после последней активности.
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
// VoiceMode: null — не менять; иначе — озвучка ответов включена/выключена
// VoiceStyle: null — не менять; иначе VoiceStyles.Talk (короткий ответ целиком) | Digest (полный
// ответ, вслух только выжимка). Приходит и без VoiceMode — стиль принадлежит устройству
public record UserStateRequest(bool? Pinned = null, bool? NotificationsMuted = null);
public record UpdateChatRequest(string? Name = null, string? Model = null, string? Effort = null, bool? Pinned = null, int? ExpiresAfterMinutes = -1, bool? NotificationsMuted = null, bool? VoiceMode = null, string? VoiceStyle = null);

// ParentId: null — вынести чат в корень списка; иначе id чата-родителя
public record SetParentRequest(string? ParentId = null);

public record SetPersonaRequest(string? PersonaId = null, string? AgentName = null);

public record CreateGroupChatRequest(List<string>? PersonaIds, string? Mode = null, string? Name = null);

public record SetParticipantsRequest(List<string>? PersonaIds);

public record SetWorkLoopRequest(bool Enabled);

// Archived: true — «Убрать в архив», false — «Вернуть из архива». Отдельного мутатора
// «снять архив» в API нет: повторная активность возвращает чат сама (признак производный),
// кнопка возврата — тот же эндпоинт. Полей архива в UpdateChatRequest/UpdateSessionRequest
// нет намеренно (план v4, шаг 2).
public record SetArchivedRequest(bool Archived);

public record SetVisibilityRequest(bool Private);

// Личный порог автоправила архивации (дней без активности); null — сброс правила
// для чатов вне проектов и наследуемого дефолта проектов
public record ArchiveDaysRequest(int? Days);

// Включение режима «Командная реализация». ExecutorPersonaIds null/пустой = вся команда
// проекта (планировщик подбирает по компетенциям в Э2). CoordinatorPersonaId/PlannerPersonaId
// опциональны — назначаются в Э2; здесь лишь сохраняются, если фронт уже их знает.
// CoordinatorNoCode — правило «координатор не пишет код сам» (по умолчанию включено):
// у чата-штаба отключаются инструменты правки файлов, работа идёт задачами.
public record SetTeamImplementRequest(
    bool Enabled,
    bool AutoWaves = true,
    string? CoordinatorPersonaId = null,
    string? PlannerPersonaId = null,
    IReadOnlyList<string>? ExecutorPersonaIds = null,
    bool CoordinatorNoCode = true);

// Переключение авто-волн на ходу (из бейджа режима)
public record SetTeamImplementAutoRequest(bool AutoWaves);

public record SetWorktreeRequest(bool Enabled, string? Branch = null, bool Force = false);

// Model допускает null: пустую модель отбивает сам эндпоинт своим сообщением, а не
// неявная валидация [ApiController] по non-nullable свойству
public record MigrateProviderRequest(string? Model, string? SubscriptionKey = null);

public record SetModeRequest(string Mode);

// Ветвление чата (шаг 4 плана «Ветвление чата», контракт §6 docs/research/chat-branching-2026-09.md).
// AnchorText обязателен: индекс — договорённость двух счётчиков (клиент/сервер), сверка текста
// на сервере страхует от расхождения (SessionManager.BranchAsync).
public record BranchChatRequest(int UserMessageIndex, string AnchorText, string Include, string? Name = null);
