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
public class ChatsController(SessionManager sessions, FileService files, ILogger<ChatsController> logger) : ControllerBase
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
        var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
        try
        {
            var chat = await sessions.CreateChatAsync(UserId, mode, req.ResumeSessionId, req.Name, req.Model, req.Effort);
            return CreatedAtAction(nameof(GetAll), new { }, chat);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Групповой чат (флаг persona-group-chats): 2-8 персон, первая — ведущая.
    // Зона — по ведущей: проектная персона → сессия её проекта, глобальная → чат вне проекта.
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
    public IActionResult Update(string id, [FromBody] UpdateChatRequest req)
    {
        if (OwnedChat(id) is null) return NotFound();
        if (req.Pinned is bool pinned) sessions.SetPinned(id, pinned);
        if (req.NotificationsMuted is bool muted) sessions.SetNotificationsMuted(id, muted);
        if (req.ExpiresAfterMinutes is not -1)
        {
            if (req.ExpiresAfterMinutes is <= 0) return BadRequest(new { error = "Срок жизни чата должен быть положительным" });
            sessions.SetExpiry(id, req.ExpiresAfterMinutes);
        }
        try
        {
            var updated = sessions.Update(id, req.Name, req.Model, req.Effort);
            return updated is null ? NotFound() : Ok(updated);
        }
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

    // Проставить значки темы всем чатам владельца без них (действие AI-палитры
    // «Проставить значки тем»). Идёт по всем чатам, не только вне проекта: значки нужны
    // и в проектных сессиях. Возвращает счётчики для тоста.
    [HttpPost("emoji-batch")]
    public async Task<IActionResult> EmojiBatch(CancellationToken ct)
    {
        try
        {
            var result = await sessions.SetChatEmojisAsync(UserId, ct);
            return Ok(result);
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

public record CreateChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null, string? Model = null, string? Effort = null);

// ExpiresAfterMinutes: -1 (поле не прислано) — не менять; null — сделать чат постоянным;
// N > 0 — временный, авто-удаление через N минут после последней активности.
// NotificationsMuted: null — не менять; true — заглушить уведомления чата
public record UpdateChatRequest(string? Name = null, string? Model = null, string? Effort = null, bool? Pinned = null, int? ExpiresAfterMinutes = -1, bool? NotificationsMuted = null);

// ParentId: null — вынести чат в корень списка; иначе id чата-родителя
public record SetParentRequest(string? ParentId = null);

public record SetPersonaRequest(string? PersonaId = null, string? AgentName = null);

public record CreateGroupChatRequest(List<string>? PersonaIds, string Mode = "auto", string? Name = null);

public record SetParticipantsRequest(List<string>? PersonaIds);

public record SetWorkLoopRequest(bool Enabled);

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

public record MigrateProviderRequest(string Model, string? SubscriptionKey = null);

public record SetModeRequest(string Mode);
