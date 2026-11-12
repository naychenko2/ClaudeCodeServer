using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Sessions;

namespace ClaudeHomeServer.Controllers;

[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/sessions")]
public class SessionsController(SessionManager sessions, ProjectManager projects,
    DefaultAssistantProvisioner provisioner,
    PersonaManager personas, SessionContextResolver contextResolver,
    PrefixFingerprintCache prefixFpCache, IProjectAccess access) : ControllerBase
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
        // S2d-4a: список чатов проекта — человеческая ручка, видимая часть (GetVisibleByProject):
        // чужой приватный чат не отдаётся ни id, ни названием; свой приватный и общий — как раньше.
        return new ETagJsonResult(sessions.GetVisibleByProject(projectId, UserId));
    }

    // Создание чата — действие хода (S2d-1c): редактор общего проекта создаёт чат и становится
    // его автором (CreatedByUserId — только из sub вызывающего, не из тела); читателю 403,
    // постороннему 404. Автор на видимость чата не влияет (S2d-1).
    [HttpPost]
    [ProjectScope(Right = ProjectRight.RunTurn)]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateSessionRequest req)
    {
        // FIN3-2 (FINAL-3, п. 3 ревью b13f2105): resume принимается только для транскрипта чата,
        // видимого вызывающему. ClaudeSessionId — ключ истории (HistoryKeyOf, SessionManager.cs:557)
        // и транскрипта CLI: новый общий чат с csid чужого приватного получил бы его разговор
        // через эту дверь. Отказ — тот же, что на неизвестный id в теле («Персона не найдена»
        // ниже): существование чужого приватного не выдаётся. Неизвестный ключ не трогается —
        // продолжение чата (ветвление) подаёт свой новый csid до регистрации чата
        // (ChatCreation.cs:160), внешний транскрипт — законный случай.
        if (req.ResumeSessionId is { Length: > 0 } resumeId && sessions.IsTranscriptReservedForOthers(resumeId, UserId))
            return NotFound(new { error = "Чат не найден" });
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
            // Ключ руководителя — арендатор (W3-B4b, И-2): участник получает руководителя
            // проекта, а не свою подмену. Фолбэк на личного ассистента — право владельца:
            // участнику провижн не запускается — инвариант «участник не получает чат общего
            // проекта с глобальной персоной (своей или владельца)».
            var scope = HttpContext.GetRequestScope();
            var lead = projects.GetById(projectId)?.DefaultPersonaId is { } leadId
                ? personas.Get(leadId, scope.TenantOwnerId)
                : null;
            // Руководитель вне своей зоны (персона сферы, проект вышел из сферы) не считается
            if (lead is not null && !sessions.PersonaVisibleIn(lead, projectId)) lead = null;
            if (lead is not null) personaId = lead.Id;
            else if (scope.Role == ProjectRole.Owner)
            {
                var provisioned = await provisioner.EnsureAsync(UserId, HttpContext.RequestAborted);
                if (provisioned is null)
                    return BadRequest(new { error = "Новый чат создаётся только с персоной: укажите personaId" });
                personaId = provisioned.Id;
            }
            else return NoProjectPersonaDenied();
        }
        try
        {
            // Явное назначение собеседника: одна точка правды «персона → область» — ResolvePersona
            // с правом хода (И-1, W3-B4b). Двойной поиск «по вызывающему, потом по арендатору» снят:
            // его вторая точка молча резолвила участнику персону владельца из другого проекта.
            // Глобальная своей проходит дальше рамки (ниже): отказ — тот же 400
            // global_persona_in_shared (S2a-8). Чужая глобальная (владельца) участнику не видна
            // вовсе (И-3) — 404, неотличимо от несуществующей.
            if (!string.IsNullOrWhiteSpace(req.PersonaId))
            {
                var pScope = access.ResolvePersona(UserId, req.PersonaId, ProjectRight.RunTurn);
                if (pScope is null) return NotFound(new { error = "Персона не найдена" });
                var assigned = personas.Get(req.PersonaId, pScope.TenantOwnerId);
                if (assigned is null) return NotFound(new { error = "Персона не найдена" });
                // Проектная персона назначается только чату своего проекта (разрез §3.2):
                // персона другого проекта — не собеседник этого, даже когда оба доступны вызывающему
                if (PersonaZone.OwnProjectId(assigned) is { } ownProject && ownProject != projectId)
                    return NotFound(new { error = "Персона не найдена" });
                sessions.EnsurePersonasVisibleInFrame(projectId, new[] { assigned });
            }
            var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.AcceptEdits;
            var session = await sessions.CreateAsync(projectId, mode, req.ResumeSessionId, req.Name,
                req.Model, req.AgentName, req.Effort, personaId: personaId, createdByUserId: UserId);
            return CreatedAtAction(nameof(GetAll), new { projectId }, session);
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        // Глобальная персона в общем чате (R-S2a-8): 400 с машинным кодом — тот же, что при смене собеседника.
        catch (GlobalPersonaInSharedException ex)
        { return BadRequest(new { error = ex.Message, code = GlobalPersonaInSharedException.Code }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Переименование и прочие правки чата: QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2) —
    // право записи (редактор и выше), а не авторство чата: участник упирался даже в своём чате.
    // Область — Read: постороннего отбивает атрибут (404, проект не виден), участнику остаётся
    // цепочка тела: видимость чата (404 на чужом приватном) → режим чата (409) → право записи (403)
    // → настройки владельца чата (403 owner_settings, доработка QA-P1: ниже)
    [HttpPut("{sessionId}")]
    [ProjectScope]
    public async Task<IActionResult> Update(string projectId, string sessionId, [FromBody] UpdateSessionRequest req)
    {
        var session = sessions.GetById(sessionId);
        if (session == null || session.ProjectId != projectId) return NotFound();
        // Модель и её настройки (effort) — выбор владельца чата (S2a-10b): тот же гейт, что у режима
        // прав и провайдера (DenyOwnerTurn → 403 owner_settings), отдельного гейта «на модель» нет.
        // Порядок до проверки права записи важен: запрос с моделью отвечает о более узком праве, чем
        // запрос о переименовании. Явная очистка модели (null в теле) от отсутствия поля не
        // различима в контракте — такое поле ниже решается общим правом записи (модель не трогается).
        if ((req.Model is not null || req.Effort is not null) && sessions.DenyOwnerTurn(sessionId, UserId) is { } denied)
            return denied;
        // S2d-3f.1 (R-S2d-3f §1): постороннему (чат не виден по CanSee) — то же, что на несуществующем,
        // существование не выдаётся кодом. Приём тот же, что у DenyTurnAuthor: сначала гейт видимости
        // GetAccessible, решение о праве — после него.
        if (sessions.GetAccessible(sessionId, UserId) is null) return NotFound();
        // QA-D45 (D5): отказ режима идёт раньше отказа права (как прежде — раньше авторского 403).
        // Порядок безопасен по видимости: приватный чат виден только автору (CanSee), поэтому 409
        // не выдаёт существование постороннему; автор-участник своего приватного чата остаётся на
        // 409 (до права записи дело не доходит), и чужой флаг он не пишет — запись ниже идёт после
        // режима и после гейта права.
        if (req.ExcludeFromDossiers is false && sessions.IsPrivateChat(sessionId))
            return PrivateChatDossiersDenied();
        // QA-P1 (решение Андрея, продуктовый вопрос 2 плана v2): переименование и метки чата —
        // право WriteContent (редактор и выше), читателю — 403 project_scope. Авторство чата здесь
        // больше не решает ничего; модель и effort — на владельном гейте DenyOwnerTurn выше.
        if (sessions.DenyContentWrite(sessionId, UserId) is { } noWrite) return noWrite;
        // QA-P1 (доработка, возврат Глеба a21170f7): WriteContent открывает редактору ТОЛЬКО name и
        // tags. Остальные поля запроса — настройки владельца чата: срок жизни, мьют уведомлений,
        // озвучка со стилем, opt-out «Не сохранять решения». Им — тот же гейт владельца-арендатора,
        // что у модели и режима (DenyOwnerTurn → 403 owner_settings): право записи контента их не
        // открывает, срок жизни чата ставит только владелец. Без этого редактор с
        // {"expiresAfterMinutes":1} на чате владельца снимал его через ChatExpiryService
        // (SetExpiry → SessionManager.cs:2474, удаление — ChatExpiryService.cs:35,47-50) — обход
        // правила удаления этого же шага. Гейт стоит ДО любой записи поля: смешанный запрос
        // (name + срок) отказывается целиком, имя не записано. Предикат — ровно тот, что у
        // записи ниже: поле считается запрошенным, когда его запись состоялась бы.
        if ((req.ExpiresAfterMinutes is not -1 || req.NotificationsMuted is not null
                || req.VoiceMode is not null || req.VoiceStyle is not null
                || req.ExcludeFromDossiers is not null)
            && sessions.DenyOwnerTurn(sessionId, UserId) is { } notOwner)
            return notOwner;
        // S2d-6c: «Не сохранять решения» нельзя выключить, пока чат приватный. Зовётся раньше
        // остальных Set*: отказ 409 не должен применять половину запроса (остальные поля —
        // mute/озвучка/срок/имя — тогда не трогаются вовсе). true принимается всегда. Ниже это
        // тот же отказ на гонке с тумблером: приватность встала после проверки выше.
        if (req.ExcludeFromDossiers is { } optOut && !sessions.TrySetExcludeFromDossiers(sessionId, optOut))
            return PrivateChatDossiersDenied();
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
        try
        {
            var updated = await sessions.UpdateAsync(sessionId, UserId, req.Name, req.Model, req.Effort, req.Tags);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Отказ режима «приватный чат + «Не сохранять решения» выкл» (S2d-6c, QA-D45): один текст и код
    // для обеих точек Update — проверки порядка и записи флага (тот же контракт формы отказа, что у
    // ChatAuthorGate.Denied: ObjectResult с code).
    private static ObjectResult PrivateChatDossiersDenied() =>
        new(new { error = "Пока чат приватный, «Не сохранять решения» нельзя выключить", code = "private_chat_dossiers" })
            { StatusCode = StatusCodes.Status409Conflict };

    // В общем проекте нет руководителя, а личного ассистента участнику не подставляют (W3-B4b):
    // провижн глобальной персоны — право владельца. 400 с машинным кодом — фронт по нему
    // показывает текст «попросите владельца назначить» (W3-F2a).
    private static ObjectResult NoProjectPersonaDenied() =>
        new(new { error = "В проекте нет руководителя — попросите владельца назначить", code = "no_project_persona" })
            { StatusCode = StatusCodes.Status400BadRequest };

    // Назначить/сменить/снять собеседника (персону или .md-агента) у проектной сессии — в т.ч. по ходу разговора.
    // Гейт права — [ProjectScope(Right = RunTurn)] (W3-B4b): читатель — 403 project_scope («нет права
    // вести ход», тот же отказ, что у отправки сообщения). Дальше — видимость чата (GetAccessible,
    // S2d-3e: чужой приватный неотличим от «нет такого») и авторство: менять собеседника участник
    // может только в чате, автор которого он (CreatedByUserId; неизвестный автор автором не считается
    // — тот же fail-closed, что у удаления чата); владельцу — в любом чате проекта. Иначе — 403
    // owner_settings, как прочие настройки чата (S2a-10b). Персона — ResolvePersona(Read) и сверка
    // с проектом (И-1); запись — SetPersona с ключом арендатора (И-2), под блокировкой спикера в SessionManager.
    [HttpPost("{sessionId}/persona")]
    [ProjectScope(Right = ProjectRight.RunTurn)]
    public IActionResult SetPersona(string projectId, string sessionId, [FromBody] SetPersonaRequest req)
    {
        if (sessions.GetAccessible(sessionId, UserId) is not { } found || found.Session.ProjectId != projectId)
            return NotFound();
        var session = found.Session;
        var scope = found.Scope;
        if (session.CreatedByUserId != UserId && scope.Role != ProjectRole.Owner)
            return OwnerSettingsGate.Denied();
        try
        {
            if (!string.IsNullOrWhiteSpace(req.PersonaId))
            {
                var pScope = access.ResolvePersona(UserId, req.PersonaId, ProjectRight.Read);
                if (pScope is null) return NotFound(new { error = "Персона не найдена" });
                var persona = personas.Get(req.PersonaId, pScope.TenantOwnerId);
                if (persona is null) return NotFound(new { error = "Персона не найдена" });
                // Персона другого проекта — не собеседник этого (та же сверка, что при создании чата)
                if (PersonaZone.OwnProjectId(persona) is { } ownProject && ownProject != projectId)
                    return NotFound(new { error = "Персона не найдена" });
                // Глобальной персоне общего проекта отказывает рамка (S2a-8) — и автору-участнику,
                // и владельцу: тот же машинный код, что при создании чата. Под try: без catch она
                // давала бы 500 вместо 400 (тот же порядок, что в Create).
                sessions.EnsurePersonasVisibleInFrame(projectId, new[] { persona });
            }
            var updated = sessions.SetPersona(sessionId, scope.TenantOwnerId, req.PersonaId, req.AgentName);
            return updated == null ? NotFound() : Ok(updated);
        }
        // Глобальная персона чату общего проекта (S2a-8): 400 с машинным кодом, чтобы фронт
        // различал этот отказ среди прочих 400 смены собеседника.
        catch (GlobalPersonaInSharedException ex)
        {
            return BadRequest(new { error = ex.Message, code = GlobalPersonaInSharedException.Code });
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
    // Гейт (S2d-3e) — путь видимости GetAccessible (ResolveSession → SessionAccess.CanSee, тот же,
    // через который ходит [ProjectScope]): чужой приватный чат неотличим от «нет такого», автору
    // своего приватного и общему чату — как раньше. GetOwned здесь не годился бы: участник-автор
    // своего приватного чата не арендатор, а историю своего чата он читал и до шага.
    [HttpGet("{sessionId}/history")]
    [ProjectScope]
    public async Task<IActionResult> GetHistory(
        string projectId, string sessionId,
        [FromQuery] int? limit = null,
        [FromQuery] int? before = null,
        [FromQuery] int? after = null,
        [FromQuery] string? fp = null)
    {
        if (sessions.GetAccessible(sessionId, UserId) is not { } found || found.Session.ProjectId != projectId)
            return NotFound();
        var session = found.Session;

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
        // S2d-3f.1 (R-S2d-3f §1): постороннему (чат не виден по CanSee) — тот же 204, что и несуществующему:
        // прежний контракт идемпотентного удаления (строка выше) сохранён, существование не выдаётся кодом,
        // сам чат не тронут. Видимость — первым шагом, как требует порядок «решение прав после видимости».
        if (sessions.GetAccessible(sessionId, UserId) is null) return NoContent();
        // W4-C1 (решение Андрея, 12.11): общий чат удаляет только владелец проекта (GetOwnedVisible —
        // владение проектом + видимость), даже если создал его редактор. Автор своего приватного чата
        // (PrivateToUserId == CreatedByUserId == вызывающий) удаляет его по-прежнему: владелец чужой
        // приватный не видит, иначе его не удалил бы никто. Авторство — CreatedByUserId (только это
        // поле; nil — автор неизвестен и автором не считается, тот же fail-closed, что у DenyTurnAuthor).
        // Видимому не-автору (читателю, редактору чужого чата) — прежний 403 по контракту ChatAuthorGate, чат цел.
        if (sessions.GetOwnedVisible(sessionId, UserId) is null &&
            (session.PrivateToUserId != UserId || session.CreatedByUserId != UserId))
            return ChatAuthorGate.Denied();
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
    // Гейт (S2d-3e) — тот же путь видимости, что у истории: GetAccessible (ResolveSession → CanSee).
    // Состав контекста чужого приватного чата не отдаётся никому, кроме его автора; отказ
    // неотличим от «нет такого чата».
    [HttpGet("{sessionId}/context")]
    [ProjectScope]
    public IActionResult GetContext(string projectId, string sessionId)
    {
        if (sessions.GetAccessible(sessionId, UserId) is not { } found || found.Session.ProjectId != projectId)
            return NotFound();

        return Ok(contextResolver.Resolve(found.Session, UserId));
    }

    // PUT — идемпотентная замена состава целиком. Валидация: Type из белого списка,
    // непустой Id, потолок 50. Записи с несуществующими адресами НЕ отбиваются —
    // «не найден» отображается (missing в GET), а PUT описывает желание человека,
    // а не состояние файловой системы.
    // Гейт (S2d-3e) — запись, поэтому GetOwnedVisible (автор-арендатор + видимость), а не путь
    // чтения: участнику запись в проект открыта только RunTurn (S2a-10), и до шага она здесь
    // отбивалась фильтром Write — для него отказ тот же. Без этого GET контекста был бы закрыт
    // на чужом приватном чате, а PUT — открыт.
    [HttpPut("{sessionId}/context")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> PutContext(string projectId, string sessionId,
        [FromBody] List<SessionContextItemRequest> req)
    {
        var session = sessions.GetOwnedVisible(sessionId, UserId);
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

// Name/Tags — право записи контента чата (WriteContent: редактор и выше). Остальные поля —
// настройки владельца чата: они открыты только владельцу-арендатору, остальным — 403
// owner_settings (доработка QA-P1, возврат Глеба a21170f7).
// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать сессию постоянной;
// N > 0 — временная, авто-удаление через N минут после последней активности
// ExcludeFromDossiers: null (поле не прислано) — не менять; иначе — признак opt-out
// «Истории решений» (ADR-004 §6, тумблер «Не сохранять решения из этого чата»)
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
// VoiceMode: null — не менять; иначе — озвучка ответов включена/выключена
// VoiceStyle: null — не менять; иначе VoiceStyles.Talk | Digest (см. UpdateChatRequest)
public record UpdateSessionRequest(string? Name = null, string? Model = null, string? Effort = null, int? ExpiresAfterMinutes = -1, List<string>? Tags = null, bool? ExcludeFromDossiers = null, bool? NotificationsMuted = null, bool? VoiceMode = null, string? VoiceStyle = null);
