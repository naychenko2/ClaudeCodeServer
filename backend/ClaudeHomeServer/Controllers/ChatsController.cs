using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Чаты вне проекта: сессии Claude без привязки к проекту, рабочая папка — {домашняя папка}/Chats (UserHomeResolver)
[ApiController]
[Authorize]
[Route("api/chats")]
public class ChatsController(SessionManager sessions, ProjectManager projects, FileService files,
    DefaultAssistantProvisioner provisioner, TeamWaveService teamWaves,
    Services.Llm.ChatDigestService digest, ChatArchiveService autoArchive,
    UserStore users, FeatureFlagService flags,
    ILogger<ChatsController> logger) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Чат принадлежит текущему пользователю и не привязан к проекту
    private Session? OwnedChat(string id)
    {
        var s = sessions.GetById(id);
        return s is not null && s.ProjectId is null && s.OwnerId == UserId ? s : null;
    }

    [HttpGet]
    public IActionResult GetAll() => Ok(sessions.GetProjectlessChats(UserId));

    // Снимок «у каких чатов прямо сейчас работают фоновые агенты»: id сессий владельца,
    // включая проектные — карточки чатов проекта рисует тот же стор. Нужен потому, что
    // событие bg_agents_presence приходит только на переходе 0↔N: открывший список позже
    // старта агентов иначе не узнал бы о них до самого их завершения.
    [HttpGet("agents-presence")]
    public IActionResult AgentsPresence() => Ok(sessions.GetSessionsWithLiveAgents(UserId));

    // Счётчик «под правило подпадёт N чатов» (настройка автоправила за флагом
    // chat-auto-archive): та же функция отбора, что архивирует тик, — число превью
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

    // === Автоправило архивации (флаг chat-auto-archive, шаг 6 плана v4) ===
    // Настройка и запуск правила — за флагом; откат пачки и ручной архив — без него.

    // Чтение настройки автоправила — первоначальное состояние экрана настройки:
    // личный порог (он же дефолт проектов без своего и правило чатов вне проектов)
    // и признак «первый проход уже был». Проектный порог сюда не входит — его отдаёт
    // GET /api/projects[/{id}] полем archiveAfterDays. Без гейта по флагу: чтение
    // ничего не меняет, а раздел «Архив» работает и без флага.
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
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChatAutoArchive))
            return BadRequest(new { error = "Автоправило архива выключено: включите «Автоправило архива чатов» в экспериментальных функциях" });
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
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChatAutoArchive))
            return BadRequest(new { error = "Автоправило архива выключено: включите «Автоправило архива чатов» в экспериментальных функциях" });
        var (archived, batchId) = await autoArchive.RunNowAsync(UserId, DateTime.UtcNow);
        return Ok(new { archived, batchId });
    }

    // Откат пачки одного прохода правила (кнопка в уведомлении/разделе «Архив»): возвращает
    // ровно чаты этого batchId, а не всю историю правила. Работает без флага — это возврат
    // уже убранного, как ручной «Вернуть из архива».
    [HttpPost("archive-batch/{batchId}/restore")]
    public async Task<IActionResult> RestoreArchiveBatch(string batchId)
    {
        var restored = await sessions.RestoreArchiveBatchAsync(UserId, batchId);
        return Ok(new { restored });
    }

    // Получить сессию по ID независимо от типа (проектная / чат вне проекта).
    // Используется для ссылки «Связанная сессия» в карточке задачи без проекта.
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var s = sessions.GetOwned(id, UserId);
        return s is null ? NotFound() : Ok(s);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateChatRequest req)
    {
        // Грань десктопного агента выдаётся по оси «проект + чат» (ADR-008), поэтому вне
        // проекта десктопного чата не бывает вовсе: он завёлся бы заведомо без грани.
        if (req.Desktop) return BadRequest(new { error = DesktopChatGuard.OutsideProject });
        // Транскрипт десктопного чата обычному чату не отдаётся — в нём кадры рабочего стола
        if (DesktopChatGuard.Refuse(sessions, desktop: false, req.ResumeSessionId) is string desktopRefusal)
            return BadRequest(new { error = desktopRefusal });
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
        var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
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
        var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
        try
        {
            var chat = await sessions.CreateGroupChatAsync(UserId, req.PersonaIds ?? [], mode, req.Name);
            return Ok(chat);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Обновить состав участников группового чата (спикер сохраняется, если остался,
    // иначе — новая ведущая). Работает и для проектной сессии группового чата.
    [HttpPut("{id}/participants")]
    public IActionResult SetParticipants(string id, [FromBody] SetParticipantsRequest req)
    {
        try
        {
            var updated = sessions.SetParticipants(id, UserId, req.PersonaIds ?? []);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateChatRequest req)
    {
        if (OwnedChat(id) is null) return NotFound();
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

    // Ручная группировка чатов (drag-and-drop в списке): вложить чат в родительский либо
    // вынести в корень (parentId == null). Один эндпоинт на оба списка — GetOwned внутри
    // SetParent резолвит и проектную сессию (как /loop), поэтому дубля в SessionsController нет.
    [HttpPut("{id}/parent")]
    public IActionResult SetParent(string id, [FromBody] SetParentRequest req)
    {
        try
        {
            var updated = sessions.SetParent(id, req.ParentId, UserId);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Отметить чат прочитанным (синк непрочитанности между устройствами). Один эндпоинт
    // на оба списка (как /parent и /loop) — GetOwned внутри MarkRead резолвит и проектную
    // сессию. Отметка не двигает UpdatedAt и не поднимает чат в списке.
    [HttpPut("{id}/read")]
    public IActionResult MarkRead(string id)
        => sessions.MarkRead(id, UserId) ? NoContent() : NotFound();

    // Убрать чат в архив (archived=true) / вернуть (false) — шаг 2 плана «Архив чатов»:
    // архив ПРЯЧЕТ чат, а не удаляет, история и claudeSessionId целы. Как /parent и /loop,
    // работает и для проектной сессии (GetOwned внутри резолвит владельца через проект).
    // 409 на живом ходе/фоновых агентах: архивация идущей работы рвала бы её (пре-мортем
    // №3 плана v4); текст ошибки — человекочитаемый («в чате идёт ход»).
    [HttpPut("{id}/archived")]
    public async Task<IActionResult> SetArchived(string id, [FromBody] SetArchivedRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        try
        {
            var updated = await sessions.SetArchivedAsync(id, UserId, req.Archived);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Сводка карточки архива (шаг 5 плана «Архив чатов»): кнопка «Собрать сводку» строит
    // 2–3 предложения о чём был разговор (место chat-digest) и кэширует их в чате — свежая
    // сводка отдаётся из кэша без обращения к модели. Как /archived, работает и для
    // проектной сессии (GetOwned внутри сервиса резолвит владельца через проект).
    // Это НЕ «Итог сессии»: вынос в заметки — отдельная кнопка через существующий
    // POST /api/sessions/{id}/summary, его контракт здесь не участвует.
    [HttpPost("{id}/digest")]
    public async Task<IActionResult> BuildDigest(string id, CancellationToken ct)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        try
        {
            return Ok(await digest.BuildDigestAsync(UserId, id, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Services.Llm.DigestInProgressException ex) { return Conflict(new { error = ex.Message }); }
        catch (Services.Llm.DigestGenerationException ex) { return StatusCode(502, new { error = ex.Message }); }
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
    // завершения. Работает и для проектной сессии (GetOwned резолвит владельца через проект).
    [HttpPut("{id}/loop")]
    public async Task<IActionResult> SetWorkLoop(string id, [FromBody] SetWorkLoopRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
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
    // работает и для проектной сессии (GetOwned резолвит владельца).
    [HttpPut("{id}/team-implement")]
    public async Task<IActionResult> SetTeamImplement(string id, [FromBody] SetTeamImplementRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        try
        {
            var updated = await sessions.SetTeamImplementAsync(id, req.Enabled,
                req.AutoWaves, req.CoordinatorPersonaId, req.PlannerPersonaId,
                req.ExecutorPersonaIds, UserId, req.CoordinatorNoCode);
            return updated is null ? NotFound() : Ok(updated);
        }
        // Гард на входе (B2): нет координатора либо состава. Код отказа машинный — фронт по
        // нему показывает пикер и НЕ отправляет вводную обычным сообщением.
        catch (Services.TeamImplementSetupException ex)
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
    // Снимается решением человека по карточке остановки («Продолжить»).
    [HttpPut("{id}/team-implement/stop")]
    public async Task<IActionResult> StopTeamImplement(string id)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        var updated = await sessions.StopTeamImplementAsync(id, UserId);
        if (updated is null) return NotFound();
        if (updated.TeamImplement is { } team && sessions.TeamEscalationRaiser is { } raise)
            await raise(updated, new Models.TeamEscalation
            {
                Kind = Models.TeamEscalationKind.Stopped,
                Title = "Практика остановлена",
                Details = "Новые волны не стартуют. Запущенные исполнители доработают начатое — " +
                          "нажмите «Продолжить», когда команде можно идти дальше.",
                Wave = team.WaveNumber,
                Actions = Models.TeamEscalationActions.For(Models.TeamEscalationKind.Stopped),
            });
        return Ok(updated);
    }

    // Переключение авто-волн на ходу (из бейджа режима): не трогает сам режим, только флаг.
    // Как loop, работает и для проектной сессии (GetOwned резолвит владельца через проект).
    [HttpPut("{id}/team-implement/auto")]
    public async Task<IActionResult> SetTeamImplementAuto(string id, [FromBody] SetTeamImplementAutoRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
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
        // Сессию берём из GetOwned одним lookup'ом: повторный GetById — двойная работа
        // и NRE→500, если чат удалён между вызовами (ревью этапа 1).
        if (sessions.GetOwned(id, UserId) is not { } session) return NotFound();
        if (teamWaves.BuildWaveSnapshot(session) is not { } snap) return NotFound();
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
        if (sessions.GetOwned(id, UserId) is not { } session) return NotFound();
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
        if (sessions.GetOwned(id, UserId) is not { } session) return NotFound();
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
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        try
        {
            var result = await sessions.RestartStuckTurnAsync(id, req?.StartFresh == true);
            return Ok(new { outcome = result.Resumed ? "restarted" : "fresh", resumed = result.Resumed, message = result.Message });
        }
        catch (SessionManager.TurnTranscriptDamagedException ex)
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
    // проектной сессии (GetOwned резолвит владельца через проект).
    [HttpPut("{id}/worktree")]
    public async Task<IActionResult> SetWorktree(string id, [FromBody] SetWorktreeRequest req)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
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
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
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
        try
        {
            return Ok(await sessions.MigrateProviderAsync(id, UserId, req.Model, req.SubscriptionKey));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("{id}/history")]
    public async Task<IActionResult> GetHistory(string id)
    {
        if (OwnedChat(id) is null) return NotFound();
        return Ok(await sessions.GetHistoryAsync(id));
    }

    // Обновить название чата по текущей переписке (AI-хаб): в отличие от авто-имени по первому
    // сообщению — по всему транскрипту, с перезаписью. Как loop, работает и для проектной сессии.
    [HttpPost("{id}/retitle")]
    public async Task<IActionResult> Retitle(string id, CancellationToken ct)
    {
        try
        {
            var updated = await sessions.RetitleAsync(UserId, id, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    // Секция destructive: на делегированном ходу агент не удаляет чаты (см. FilesController.Delete)
    [DenyOnDelegatedTurn("Удаление чата")]
    public async Task<IActionResult> Delete(string id)
    {
        if (OwnedChat(id) is null) return NotFound();
        await sessions.DeleteAsync(id);
        return NoContent();
    }

    // Загрузка вложения в рабочую папку чата (в подпапку .cc-attachments) → относительный путь.
    // Единый путь для обоих типов чата: у чата вне проекта папка — {дом}/Chats, у проектного —
    // рабочая папка сессии (GetChatRoot), поэтому здесь GetOwned, а не OwnedChat.
    [HttpPost("{id}/files/upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(string id, IFormFile? file = null)
    {
        if (sessions.GetOwned(id, UserId) is null) return NotFound();
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не выбран или пустой" });

        var root = sessions.GetChatRoot(id, UserId);
        if (root is null) return NotFound();

        // Path.GetFileName защищает от path-сегментов в имени файла (../evil)
        var safeName = Path.GetFileName(file.FileName);
        if (string.IsNullOrEmpty(safeName))
            return BadRequest(new { error = "Некорректное имя файла" });

        // Уникальность — через подпапку с GUID, чтобы сохранить оригинальное имя файла
        // (на плашке в чате показывается basename = оригинальное имя, и Claude видит его же)
        var rel = $"{FileService.AttachmentsDir}/{Guid.NewGuid():N}/{safeName}";

        // Вложения не должны светиться в git-статусе проекта и уезжать в историю по `git add -A`.
        // Лениво, до записи файла: у проекта со своим .gitignore дефолтный игнор не создавался.
        try { GitService.EnsureAttachmentsExcluded(root); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось записать игнор вложений для {Root}", root); }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        files.WriteFileBytes(root, rel, ms.ToArray());
        return Ok(new { path = rel });
    }
}

// PersonaId — собеседник нового чата; под флагом default-personas-onboarding обязателен
// (либо resumeSessionId — продолжение существующего разговора).
// Desktop — попытка завести десктопный чат вне проекта: 400 (грань живёт только в проекте)
public record CreateChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? Effort = null, string? PersonaId = null, bool Desktop = false);

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать чат постоянным;
// N > 0 — временный, авто-удаление через N минут после последней активности.
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
// VoiceMode: null — не менять; иначе — озвучка ответов включена/выключена
// VoiceStyle: null — не менять; иначе VoiceStyles.Talk (короткий ответ целиком) | Digest (полный
// ответ, вслух только выжимка). Приходит и без VoiceMode — стиль принадлежит устройству
public record UpdateChatRequest(string? Name = null, string? Model = null, string? Effort = null, bool? Pinned = null, int? ExpiresAfterMinutes = -1, bool? NotificationsMuted = null, bool? VoiceMode = null, string? VoiceStyle = null);

// ParentId: null — вынести чат в корень списка; иначе id чата-родителя
public record SetParentRequest(string? ParentId = null);

public record SetPersonaRequest(string? PersonaId = null, string? AgentName = null);

public record CreateGroupChatRequest(List<string>? PersonaIds, string Mode = "auto", string? Name = null);

public record SetParticipantsRequest(List<string>? PersonaIds);

public record SetWorkLoopRequest(bool Enabled);

// Archived: true — «Убрать в архив», false — «Вернуть из архива». Отдельного мутатора
// «снять архив» в API нет: повторная активность возвращает чат сама (признак производный),
// кнопка возврата — тот же эндпоинт. Полей архива в UpdateChatRequest/UpdateSessionRequest
// нет намеренно (план v4, шаг 2).
public record SetArchivedRequest(bool Archived);

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
