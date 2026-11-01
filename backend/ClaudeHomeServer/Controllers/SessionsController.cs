using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/sessions")]
public class SessionsController(SessionManager sessions, ProjectManager projects,
    DefaultAssistantProvisioner provisioner,
    PersonaManager personas, SessionContextResolver contextResolver,
    PrefixFingerprintCache prefixFpCache) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Кэш отпечатков префиксов истории (Перф-3 B8) — см. ChatsController._prefixFpCache
    private readonly PrefixFingerprintCache _prefixFpCache = prefixFpCache;

    [HttpGet]
    [ProjectScope]
    public IActionResult GetAll(string projectId)
    {
        // Вычисляемые parentSessionId/taskDone дописывает конвертер Session на границе
        // сериализации (SessionJsonConverter) — здесь ничего подставлять не нужно.
        // Условный GET: фронт опрашивает список каждые несколько секунд, а меняется он редко;
        // тег считается по готовому телу, так что updatedAt в ответе всегда точный
        return new ETagJsonResult(sessions.GetByProject(projectId).ToList());
    }

    // Создание чата — действие хода (S2d-1c): редактор общего проекта создаёт чат и становится
    // его автором (CreatedByUserId — только из sub вызывающего, не из тела); читателю 403,
    // постороннему 404. Автор на видимость чата не влияет (S2d-1).
    [HttpPost]
    [ProjectScope(Right = ProjectRight.RunTurn)]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateSessionRequest req)
    {
        string? personaId = req.PersonaId;
        // Последний рубеж инварианта «новый чат человека — только с персоной» (парная связь
        // с фронтом): без personaId/resumeSessionId сначала провижним ассистента и продолжим
        // с ним. 400 — только когда провижн невозможен. Служебные пути (задачи, one-shot)
        // REST не используют, поэтому гейт их не задевает.
        if (string.IsNullOrWhiteSpace(personaId) && string.IsNullOrWhiteSpace(req.ResumeSessionId))
        {
            // Правило «персона контекста» — то же, что на фронте (lib/defaultPersona.ts):
            // в проекте чат ведёт руководитель, и только если его нет — личный ассистент.
            // Сервер обязан повторять это правило, иначе не-браузерный потребитель REST
            // (скрипт, будущий адаптер мессенджера) получил бы в проекте с назначенным
            // руководителем чат от личного ассистента — и вместе с ним молча потерял бы
            // командные механики (SessionManager отдаёт их только персоне-руководителю).
            // Резолв в живую персону, а не проверка поля: сирота не должна подменять правило.
            var lead = projects.GetById(projectId)?.DefaultPersonaId is { } leadId
                ? personas.Get(leadId, UserId)
                : null;
            // Руководитель вне своей зоны (персона сферы, проект вышел из сферы) не считается
            if (lead is not null && !sessions.PersonaVisibleIn(lead, projectId)) lead = null;
            if (lead is not null) personaId = lead.Id;
            else
            {
                var provisioned = await provisioner.EnsureAsync(UserId, HttpContext.RequestAborted);
                if (provisioned is null)
                    return BadRequest(new { error = "Новый чат создаётся только с персоной: укажите personaId" });
                personaId = provisioned.Id;
            }
        }
        try
        {
            // Явное назначение собеседника (доработка R-S2a-8): глобальной персоне чат общего
            // проекта не назначается — общая проверка рамки SessionManager. Персона резолвится
            // по id вызывающего или владельца проекта: участник назначает свою, арендатор — свою;
            // чужой id вне проверки (и сегодня, и дальше). Фолбэк сервера ниже (без personaId в
            // теле) — не назначение вызывающим, проверка его не касается.
            if (!string.IsNullOrWhiteSpace(req.PersonaId))
            {
                var assigned = personas.Get(req.PersonaId, UserId)
                    ?? (projects.GetById(projectId)?.OwnerId is { } tenantId ? personas.Get(req.PersonaId, tenantId) : null);
                if (assigned is null) return NotFound(new { error = "Персона не найдена" });
                sessions.EnsurePersonasVisibleInFrame(projectId, new[] { assigned });
            }
            var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.AcceptEdits;
            var session = await sessions.CreateAsync(projectId, mode, req.ResumeSessionId, req.Name,
                req.Model, req.AgentName, req.Effort, personaId: personaId, createdByUserId: UserId);
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        // Глобальная персона в общем чате (R-S2a-8): 400 с машинным кодом — тот же, что при смене собеседника.
        catch (SessionManager.GlobalPersonaInSharedException ex)
        { return BadRequest(new { error = ex.Message, code = SessionManager.GlobalPersonaInSharedException.Code }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Переименование и прочие правки чата: до S2a-10 только автор. Область — Read, чтобы
    // участник дошёл до проверки автора и получил not_chat_author, а не общий project_scope
    [HttpPut("{sessionId}")]
    [ProjectScope]
    public async Task<IActionResult> Update(string projectId, string sessionId, [FromBody] UpdateSessionRequest req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        // Модель и её настройки (effort) — выбор владельца чата (S2a-10b): тот же гейт, что у режима
        // прав и провайдера (DenyOwnerTurn → 403 owner_settings), отдельного гейта «на модель» нет.
        // Порядок до проверки авторства важен: запрос с моделью отвечает о более узком праве, чем
        // запрос о переименовании. Явная очистка модели (null в теле) от отсутствия поля не
        // различима в контракте — она остаётся на авторском отказе ниже.
        if ((req.Model is not null || req.Effort is not null) && sessions.DenyOwnerTurn(sessionId, UserId) is { } denied)
            return denied;
        if (sessions.GetOwned(sessionId, UserId) is null) return ChatAuthorGate.Denied();
        if (req.NotificationsMuted is bool muted) sessions.SetNotificationsMuted(sessionId, muted);
        // Хотя бы одно из двух — см. тот же блок в ChatsController.Update
        if (req.VoiceStyle is not null && !VoiceStyles.IsKnown(req.VoiceStyle))
            return BadRequest(new { error = "Неизвестный стиль озвучки" });
        if (req.VoiceMode is not null || req.VoiceStyle is not null)
            sessions.SetVoiceMode(sessionId, req.VoiceMode, req.VoiceStyle);
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(sessionId, req.ExpiresAfterMinutes);
        }
        if (req.ExcludeFromDossiers is { } optOut)
            sessions.SetExcludeFromDossiers(sessionId, optOut);
        try
        {
            var updated = await sessions.UpdateAsync(sessionId, UserId, req.Name, req.Model, req.Effort, req.Tags);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Назначить/сменить/снять собеседника (персону или .md-агента) у проектной сессии — в т.ч. по ходу разговора
    [HttpPost("{sessionId}/persona")]
    [ProjectScope(Right = ProjectRight.Write)]
    public IActionResult SetPersona(string projectId, string sessionId, [FromBody] SetPersonaRequest req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        try
        {
            var updated = sessions.SetPersona(sessionId, UserId, req.PersonaId, req.AgentName);
            return updated == null ? NotFound() : Ok(updated);
        }
        // Глобальная персона чату общего проекта (S2a-8): 400 с машинным кодом, чтобы фронт
        // различал этот отказ среди прочих 400 смены собеседника.
        catch (SessionManager.GlobalPersonaInSharedException ex)
        {
            return BadRequest(new { error = ex.Message, code = SessionManager.GlobalPersonaInSharedException.Code });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // GET /api/projects/{id}/sessions/{sid}/history — история чата.
    // Без параметров пагинации отдаёт ПОЛНЫЙ плоский массив (прежний контракт — старые клиенты
    // не ломаются). С limit и/или before включает постраничный режим и возвращает объект
    // { messages, hasMore, cursor }: это режет длинную историю (5+ МБ целиком) до ~100
    // сообщений хвоста, а более ранние догружаются по курсору кнопкой «Показать ранее».
    // С after (+ fp) — докачка хвоста (Перф-2 B5): клиент, у которого лента уже в памяти,
    // просит только записи после курсора; ответ — объект { reset, messages, anchorIndex,
    // anchorFp }: reset=false — messages = хвост (дописывается), reset=true — messages =
    // полная история (замена), anchor* — новый курсор для следующего запроса.
    [HttpGet("{sessionId}/history")]
    [ProjectScope]
    public async Task<IActionResult> GetHistory(
        string projectId, string sessionId,
        [FromQuery] int? limit = null,
        [FromQuery] int? before = null,
        [FromQuery] int? after = null,
        [FromQuery] string? fp = null)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();

        var history = await sessions.GetHistoryAsync(sessionId);

        // Курсор докачки (after) — приоритетнее пагинации: клиент пришёл за хвостом, не за страницей
        if (after is not null)
        {
            // Ключ кэша — csid (HistoryKeyOf): отпечатки префиксов не пересчитываются на каждый запрос
            var tail = ChatHistoryTail.Compute(history, after, fp,
                _prefixFpCache.GetOrCompute(session.ClaudeSessionId ?? sessionId, history));
            return Ok(new
            {
                reset = tail.Reset,
                messages = tail.Messages,
                anchorIndex = tail.AnchorIndex,
                anchorFp = tail.AnchorFp,
            });
        }

        // Ни одного параметра пагинации — прежний контракт: полный список как плоский массив
        if (limit is null && before is null)
            return Ok(history);

        // before — индекс, ДО которого (эксклюзивно) отдать сообщения. Несуществующий индекс
        // (за пределами истории или отрицательный) — 400, как требует инвариант задачи
        if (before is not null && !ChatHistoryPaginator.IsCursorValid(history.Count, before.Value))
            return BadRequest(new { error = "Курсор before указывает за пределы истории" });

        var page = ChatHistoryPaginator.Slice(history, limit, before);
        return Ok(page);
    }

    [HttpDelete("{sessionId}")]
    [ProjectScope]
    // Секция destructive: на делегированном ходу агент не удаляет чаты (см. FilesController.Delete)
    [DenyOnDelegatedTurn("Удаление чата")]
    public async Task<IActionResult> Delete(string projectId, string sessionId)
    {
        var session = sessions.GetById(sessionId);
        if (session == null) return NoContent(); // идемпотентное удаление
        if (session.ProjectId != projectId) return NotFound(); // чужая сессия — не удаляем
        if (sessions.GetOwned(sessionId, UserId) is null) return ChatAuthorGate.Denied();
        await sessions.DeleteAsync(sessionId);
        return NoContent();
    }

    // --- Контекст чата (фича chat-context) ---

    // Потолок состава контекста: одна строка на запись, чат не витрина проекта
    // (вне объёма — «закреплённые материалы проекта» из исходного ADR).
    private const int MaxContextEntries = 50;

    // GET — состав контекста с признаками missing. Признак считает единая точка
    // SessionContextResolver: у неё же его берёт MCP-тул context_list, иначе фронт и тул
    // разошлись бы в оценке одной записи. Битая запись отдаётся с missing=true
    // (убрать/переуказать — решение клиента), молча не чистим: состав меняется только PUT.
    [HttpGet("{sessionId}/context")]
    [ProjectScope]
    public IActionResult GetContext(string projectId, string sessionId)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();

        return Ok(contextResolver.Resolve(session, UserId));
    }

    // PUT — идемпотентная замена состава целиком. Валидация: Type из белого списка,
    // непустой Id, потолок 50. Записи с несуществующими адресами НЕ отбиваются —
    // «не найден» отображается (missing в GET), а PUT описывает желание человека,
    // а не состояние файловой системы.
    [HttpPut("{sessionId}/context")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> PutContext(string projectId, string sessionId,
        [FromBody] List<SessionContextItemRequest> req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();

        if (req.Count > MaxContextEntries)
            return BadRequest(new { error = $"Контекст чата — не больше {MaxContextEntries} записей" });
        foreach (var e in req)
        {
            if (!SessionContextTypes.IsKnown(e.Type))
                return BadRequest(new { error = $"Неизвестный тип материала контекста: {e.Type}" });
            if (string.IsNullOrWhiteSpace(e.Id))
                return BadRequest(new { error = "Id материала контекста не может быть пустым" });
        }

        var entries = req.Select(e => new SessionContextEntry
        {
            Type = e.Type,
            Id = e.Id.Trim(),
            Title = string.IsNullOrWhiteSpace(e.Title) ? null : e.Title.Trim(),
        }).ToList();

        var updated = await sessions.SetContextAsync(sessionId, entries);
        return updated == null ? NotFound() : Ok(updated.Context);
    }

    // Подобрать значки-иконки чатам проекта без них (действие AI-палитры «Проставить значки
    // тем» в разделе проекта). Возвращает счётчики для тоста.
    [HttpPost("icon-batch")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> IconBatch(string projectId, CancellationToken ct)
    {
        try
        {
            var result = await sessions.SetChatIconsAsync(UserId, ct, projectId);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }
}

// PersonaId — собеседник нового чата; под флагом default-personas-onboarding обязателен
// (либо resumeSessionId — продолжение существующего разговора).
public record CreateSessionRequest(string Mode = "acceptEdits", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? AgentName = null, string? Effort = null, string? PersonaId = null);

// Запись контекста чата на вход PUT (Type/Id/Title — см. SessionContextEntry; Title
// необязателен — подпись со стороны добавившего, сервер только тримит).
public record SessionContextItemRequest(string Type, string Id, string? Title = null);

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать сессию постоянной;
// N > 0 — временная, авто-удаление через N минут после последней активности
// ExcludeFromDossiers: null (поле не прислано) — не менять; иначе — признак opt-out
// «Истории решений» (ADR-004 §6, тумблер «Не сохранять решения из этого чата»)
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
// VoiceMode: null — не менять; иначе — озвучка ответов включена/выключена
// VoiceStyle: null — не менять; иначе VoiceStyles.Talk | Digest (см. UpdateChatRequest)
public record UpdateSessionRequest(string? Name = null, string? Model = null, string? Effort = null, int? ExpiresAfterMinutes = -1, List<string>? Tags = null, bool? ExcludeFromDossiers = null, bool? NotificationsMuted = null, bool? VoiceMode = null, string? VoiceStyle = null);
