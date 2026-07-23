using System.Text;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Claude-исполнитель задач: запускает отдельную чат-сессию по задаче (кнопкой или
// автозапуском по сроку), следит за её ходом через SessionManager.OnSessionMessage
// и уведомляет пользователя (тост + push) о завершении и запросах разрешений.
public class TaskExecutionService
{
    private readonly TaskManager _tasks;
    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;
    private readonly IHubContext<SessionHub> _hub;
    private readonly PushService _push;
    private readonly NotificationService _notif;
    private readonly NotesKnowledgeService _kb;
    private readonly ILogger<TaskExecutionService> _log;
    // Модель сессии-исполнителя (Tasks:ExecutorModel): null → дефолт Claude;
    // deepseek-модель тоже валидна — задачи доступны ей через MCP tasks-server
    private readonly string? _executorModel;

    public TaskExecutionService(
        TaskManager tasks, SessionManager sessions, PersonaManager personas,
        IHubContext<SessionHub> hub, PushService push,
        NotesKnowledgeService kb,
        NotificationService notif,
        ILogger<TaskExecutionService> log, IConfiguration config)
    {
        _tasks = tasks;
        _sessions = sessions;
        _personas = personas;
        _hub = hub;
        _push = push;
        _kb = kb;
        _log = log;
        _notif = notif;
        _executorModel = config["Tasks:ExecutorModel"];
        _sessions.OnSessionMessage += OnSessionMessageAsync;
    }

    /// <summary>
    /// Запуск выполнения задачи Claude-ом: отдельная сессия в проекте задачи
    /// (личная — чат вне проекта) в режиме acceptEdits, первым сообщением — постановка.
    /// </summary>
    /// <exception cref="InvalidOperationException">задача не подходит или уже выполняется</exception>
    public async Task<TaskItem> ExecuteAsync(TaskItem task, bool auto)
    {
        if (task.Status == TaskItemStatus.Done)
            throw new InvalidOperationException("Задача уже завершена");
        if (task.OwnerId is null)
            throw new InvalidOperationException("У задачи нет владельца");

        // Не более одной живой сессии на задачу
        if (task.LinkedSessionId is not null &&
            _sessions.GetById(task.LinkedSessionId) is { } linked &&
            linked.Status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting)
            throw new InvalidOperationException("По задаче уже работает сессия");

        // Персона-исполнитель: чужая/удалённая — мягкая деградация в обычный режим
        Persona? persona = null;
        if (task.PersonaId is not null)
        {
            persona = _personas.Get(task.PersonaId, task.OwnerId);
            if (persona is null)
                _log.LogWarning("Персона {PersonaId} задачи {TaskId} не найдена или чужая — выполняю обычным Claude",
                    task.PersonaId, task.Id);
        }

        var name = "Задача: " + (task.Title.Length > 60 ? task.Title[..60] + "…" : task.Title);
        var model = persona?.Model ?? _executorModel;
        // taskExecution: true — форсирует tasks-MCP даже у персоны с ограничением Persona.Tools
        // (без «tasks»): исполнитель обязан управлять задачей через mcp__tasks__*.
        var session = task.ProjectId is not null
            ? await _sessions.CreateAsync(task.ProjectId, ClaudeMode.AcceptEdits, name: name, model: model,
                effort: persona?.Effort, personaId: persona?.Id, taskExecution: true, taskId: task.Id)
            : await _sessions.CreateChatAsync(task.OwnerId, ClaudeMode.AcceptEdits, name: name, model: model,
                effort: persona?.Effort, personaId: persona?.Id, taskExecution: true, taskId: task.Id);
        if (task.ExecutionExpiresAfterMinutes is { } ttl) _sessions.SetExpiry(session.Id, ttl);

        var updated = _tasks.MarkClaudeStarted(task.Id, session.Id, DateTime.UtcNow)
            ?? throw new InvalidOperationException("Задача удалена");
        await _hub.BroadcastTaskChangedAsync(task.OwnerId, "updated", updated);

        var prompt = BuildPrompt(updated, persona);
        // Обогащение контекста семантически близкими заметками
        prompt += await BuildNotesContextAsync(updated);
        await _sessions.SendMessageAsync(session.Id, prompt, [], auto: true, senderPersonaId: persona?.Id);

        if (auto)
            await NotifyAsync(updated, new NotificationMessage(
                Title: "Взял задачу в работу",
                Body: updated.Title,
                Url: TaskSchedulerService.TaskUrl(updated),
                Kind: "claude",
                PersonaId: persona?.Id,
                ProjectId: updated.ProjectId,
                TaskId: updated.Id,
                Tag: "Исполнитель"));

        _log.LogInformation("Claude-исполнитель запущен ({Trigger}): задача {TaskId} «{Title}», сессия {SessionId}",
            auto ? "автозапуск" : "вручную", updated.Id, updated.Title, session.Id);
        return updated;
    }

    // Постановка задачи для Claude: контекст + правила ведения статуса через MCP tasks_*.
    // С персоной — структурированный 6-секционный контракт (персона-исполнитель);
    // без персоны — прежний формат (обратная совместимость).
    internal static string BuildPrompt(TaskItem task, Persona? persona = null)
    {
        if (persona is not null) return BuildPersonaPrompt(task);

        var sb = new StringBuilder();
        sb.AppendLine($"Выполни задачу из трекера (id задачи: {task.Id}).");
        sb.AppendLine();
        sb.AppendLine($"# {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine();
            sb.AppendLine(task.Description);
        }
        if (task.Subtasks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Подзадачи:");
            foreach (var s in task.Subtasks)
                sb.AppendLine($"- [{(s.IsDone ? "x" : " ")}] {s.Title} (id: {s.Id})");
        }
        if (task.LinkedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Связанные файлы:");
            foreach (var f in task.LinkedFiles)
                sb.AppendLine($"- {f}");
        }
        sb.AppendLine();
        sb.AppendLine("Правила:");
        sb.AppendLine("- Задача уже переведена в статус inProgress; веди её через MCP-инструменты tasks_*.");
        sb.AppendLine("- Выполненные подзадачи отмечай через tasks_toggle_subtask.");
        sb.AppendLine("- Когда всё сделано и проверено — заверши задачу через tasks_complete, передав resultMarkdown " +
                      "(короткий итог сделанного) и linkedFiles (пути итоговых файлов проекта, если есть).");
        sb.AppendLine("- Если выполнить невозможно — не завершай задачу, а кратко опиши причину.");
        return sb.ToString();
    }

    // 6-секционный контракт постановки для персоны-исполнителя. Характер персоны
    // инжектится системным промптом сессии (персона-слой) — здесь только постановка.
    // Секция КОНТЕКСТ идёт последней: блок заметок (BuildNotesContextAsync)
    // дописывается после и попадает в неё же.
    private static string BuildPersonaPrompt(TaskItem task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ЗАДАЧА");
        sb.AppendLine($"Выполни задачу из трекера (id задачи: {task.Id}).");
        sb.AppendLine();
        sb.AppendLine($"# {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine();
            sb.AppendLine(task.Description);
        }
        sb.AppendLine();
        sb.AppendLine("## ОЖИДАЕМЫЙ РЕЗУЛЬТАТ");
        sb.AppendLine("- Задача выполнена, проверена и завершена в трекере.");
        sb.AppendLine("- Завершая через tasks_complete, прикрепи resultMarkdown — короткий итог сделанного " +
                      "от твоего лица, и linkedFiles — пути итоговых файлов проекта (если есть).");
        sb.AppendLine();
        sb.AppendLine("## ИНСТРУМЕНТЫ");
        sb.AppendLine("- Статус задачи веди через MCP-инструменты tasks_*.");
        sb.AppendLine("- Выполненные подзадачи отмечай через tasks_toggle_subtask.");
        sb.AppendLine("- Делегируя часть работы другой персоне через tasks_create (personaId), " +
                      "сразу запусти её исполнение через tasks_execute — сама она не стартует.");
        sb.AppendLine();
        sb.AppendLine("## ОБЯЗАТЕЛЬНО");
        sb.AppendLine("- Задача уже переведена в статус inProgress — поддерживай статус актуальным.");
        // Верификационная дисциплина и правило остановки — из oh-my-openagent
        // (Hephaestus/Sisyphus-Junior, см. docs/omo-adoption.md)
        sb.AppendLine("- НЕТ СВИДЕТЕЛЬСТВ = НЕ ГОТОВО: перед завершением прогони фактическую проверку " +
                      "(сборка, тесты, реальный результат) и приведи её вывод в итоге.");
        sb.AppendLine("- Делегировал часть работы субагенту — не доверяй его отчёту на слово, проверь результат сам.");
        sb.AppendLine("- Когда всё сделано и проверено — заверши задачу через tasks_complete с resultMarkdown " +
                      "(итог сделанного) и linkedFiles (итоговые файлы проекта, если есть).");
        sb.AppendLine();
        sb.AppendLine("## НЕЛЬЗЯ");
        sb.AppendLine("- Не выходи за рамки задачи и не трогай несвязанное.");
        sb.AppendLine("- ОСТАНОВИСЬ после первой успешной верификации: не полируй сделанное и не выдумывай " +
                      "дополнительную работу сверх постановки.");
        sb.AppendLine("- Не заявляй завершение раньше времени: «почти готово» — это не готово.");
        sb.AppendLine("- Если выполнить невозможно — не завершай задачу, а кратко опиши причину.");
        sb.AppendLine();
        // Справочник категорий делегирования (OmO): как резать крупную работу на субагентов
        sb.AppendLine("## ДЕЛЕГИРОВАНИЕ");
        sb.AppendLine("Крупную задачу режь на субагентов по справочнику категорий ниже " +
                      "(это профили постановки, а не имена инструментов); мелкую делай сам.");
        sb.AppendLine();
        sb.AppendLine(Prompts.OmoPrompts.DelegationCategories);
        sb.AppendLine();
        sb.AppendLine("## КОНТЕКСТ");
        if (task.Subtasks.Count > 0)
        {
            sb.AppendLine("Подзадачи:");
            foreach (var s in task.Subtasks)
                sb.AppendLine($"- [{(s.IsDone ? "x" : " ")}] {s.Title} (id: {s.Id})");
        }
        if (task.LinkedFiles.Count > 0)
        {
            sb.AppendLine("Связанные файлы:");
            foreach (var f in task.LinkedFiles)
                sb.AppendLine($"- {f}");
        }
        if (task.Subtasks.Count == 0 && task.LinkedFiles.Count == 0)
            sb.AppendLine("Дополнительного контекста нет.");
        return sb.ToString();
    }

    // Подпись персоны «Роль (Имя)» — единый формат отображения
    internal static string PersonaLabel(Persona persona) =>
        string.IsNullOrWhiteSpace(persona.Role) ? persona.Name : $"{persona.Role} ({persona.Name})";

    // Блок «релевантные заметки» — семантический поиск по базе знаний владельца
    // (флаг task-exec-context). Тихо пусто, если Dify не настроен или ничего не нашлось.
    private async Task<string> BuildNotesContextAsync(TaskItem task)
    {
        if (!_kb.Available || task.OwnerId is null) return "";
        var query = string.IsNullOrWhiteSpace(task.Description)
            ? task.Title
            : $"{task.Title}\n{task.Description}";

        IReadOnlyList<NoteSemanticHit> hits;
        try { hits = await _kb.SearchAsync(task.OwnerId, query, topK: 5); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось получить контекст заметок для задачи {TaskId}", task.Id);
            return "";
        }
        if (hits.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Возможно релевантные заметки из базы знаний");
        sb.AppendLine("(семантически близкие к задаче выдержки — используй как контекст, если полезно; не полагайся слепо)");
        foreach (var h in hits)
        {
            sb.AppendLine();
            sb.AppendLine($"### {h.Title} ({h.SourceLabel})");
            sb.AppendLine(h.Snippet.Trim());
        }
        return sb.ToString();
    }

    // Наблюдатель сообщений всех сессий: реагируем только на сессии, привязанные к задачам
    private async Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not (ResultMessage or PermissionRequestMessage or AskQuestionMessage)) return;

        // Ищем задачу этой сессии с незавершённым запуском исполнителя
        var task = FindTracked(session.Id);
        if (task is null) return;

        // Уведомления от лица персоны-исполнителя (если назначена и всё ещё своя)
        var persona = task.PersonaId is not null ? _personas.Get(task.PersonaId, task.OwnerId!) : null;

        switch (msg)
        {
            case ResultMessage result:
            {
                var ok = IsSuccess(result);
                var updated = _tasks.MarkClaudeResult(task.Id, ok ? "success" : "error");
                if (updated is null) return;
                await _hub.BroadcastTaskChangedAsync(updated.OwnerId!, "updated", updated);
                // Финальное уведомление — только когда задача реально завершена (done) либо ход упал.
                // Промежуточные успешные ходы многошаговой задачи не спамят «завершил работу» (②-2.1).
                if (!ok || updated.Status == TaskItemStatus.Done)
                {
                    await NotifyAsync(updated, BuildResultNotification(updated, ok, persona));
                    await NotifyDelegatorAsync(updated, ok);
                }
                // Модель Z: активный доклад в чат (в дополнение к L0-тосту выше) — только
                // при реальном завершении делегированной задачи, не при упавшем ходе
                if (updated.Status == TaskItemStatus.Done)
                    await ReportToDelegatorAsync(updated, persona);
                break;
            }
            case PermissionRequestMessage or AskQuestionMessage:
                await NotifyAsync(task, BuildWaitingNotification(task, persona));
                break;
        }
    }

    // --- Чистая логика маппинга (извлечена для юнит-тестов) ---

    // Итог хода успешен, если result не error
    internal static bool IsSuccess(ResultMessage result) => result.Subtype != "error";

    // По задаче идёт незавершённый запуск исполнителя (ждём result её сессии)
    internal static bool IsAwaitingResult(TaskItem task) =>
        task.ClaudeStartedAt is not null && task.ClaudeResult is null;

    // Уведомление о завершении хода. Claude завершает задачу сам через tasks_complete;
    // если статус не done — результат требует внимания пользователя.
    // С персоной-исполнителем — уведомление от её лица («Роль (Имя)»).
    internal static NotificationMessage BuildResultNotification(TaskItem updated, bool ok, Persona? persona = null)
    {
        var body = updated.Status == TaskItemStatus.Done
            ? updated.Title
            : $"{updated.Title} — проверь результат в чате";
        var title = ok ? "Завершил работу над задачей" : "Не смог выполнить задачу";
        return new NotificationMessage(
            Title: title,
            Body: body,
            Url: TaskSchedulerService.TaskUrl(updated),
            Kind: ok ? "success" : "claude",
            PersonaId: persona?.Id,
            ProjectId: updated.ProjectId,
            TaskId: updated.Id,
            Tag: "Исполнитель");
    }

    // L0-доставка постановщику: задача делегирована персоной из чата → отдельное уведомление
    // от её лица со ссылкой на исходный чат (без агентского хода — бесплатный дефолт).
    // Скип: постановщик не задан, совпадает с исполнителем (дубль «Завершил работу») или удалён.
    private async Task NotifyDelegatorAsync(TaskItem task, bool ok)
    {
        if (task.CreatedByPersonaId is null || task.CreatedByPersonaId == task.PersonaId) return;
        var delegator = _personas.Get(task.CreatedByPersonaId, task.OwnerId!);
        if (delegator is null) return;
        // SourceSessionId приходит из тела POST и мог указать на чужой чат — ссылку строим
        // только по сессии владельца задачи, иначе fallback на TaskUrl
        var sourceSession = task.SourceSessionId is not null ? _sessions.GetById(task.SourceSessionId) : null;
        if (sourceSession is not null && _sessions.ResolveOwnerId(sourceSession) != task.OwnerId)
            sourceSession = null;
        await NotifyAsync(task, BuildDelegatorNotification(task, ok, delegator, sourceSession));
    }

    // Уведомление постановщику о завершении делегированной задачи: Url — исходный чат
    // (SourceSessionId); чат удалён/неизвестен → ссылка на задачу
    internal static NotificationMessage BuildDelegatorNotification(
        TaskItem task, bool ok, Persona delegator, Session? sourceSession) => new(
        Title: ok ? "Делегированная задача выполнена" : "Делегированная задача не выполнена",
        Body: task.Title,
        Url: sourceSession is null
            ? TaskSchedulerService.TaskUrl(task)
            : string.IsNullOrEmpty(sourceSession.ProjectId)
                ? $"/chats/{sourceSession.Id}"
                : $"/project/{sourceSession.ProjectId}/chat/{sourceSession.Id}",
        Kind: ok ? "success" : "claude",
        PersonaId: delegator.Id,
        ProjectId: task.ProjectId,
        TaskId: task.Id,
        Tag: "Постановщик");

    // Модель Z: активный доклад о завершении делегированной задачи — в отличие от L0-тоста
    // (NotifyDelegatorAsync) кладёт репорт прямо в чат постановщика. ШАГ 1 — гостевая реплика
    // исполнителя B с готовым resultMarkdown (0 токенов, без агентского хода); ШАГ 2 — сразу
    // за ней платный авто-ход постановщика A с реакцией (--resume). Исходный чат S мёртв/чужой/
    // не найден → fallback в новый чат A. Применимо только когда исполнитель — персона
    // (без неё нет «лица» для гостевой реплики; L0-тост выше это уже покрывает).
    private async Task ReportToDelegatorAsync(TaskItem task, Persona? executor)
    {
        if (executor is null) return;
        if (task.CreatedByPersonaId is null || task.CreatedByPersonaId == executor.Id) return;
        var delegator = _personas.Get(task.CreatedByPersonaId, task.OwnerId!);
        if (delegator is null) return;

        // Владелец S — как в NotifyDelegatorAsync: чужая/неизвестная сессия не годится
        var sourceSession = task.SourceSessionId is not null ? _sessions.GetById(task.SourceSessionId) : null;
        if (sourceSession is not null && _sessions.ResolveOwnerId(sourceSession) != task.OwnerId)
            sourceSession = null;

        string targetSessionId;
        if (sourceSession is not null)
            targetSessionId = sourceSession.Id;
        else
        {
            var title = task.Title.Length > 60 ? task.Title[..60] + "…" : task.Title;
            var fresh = await _sessions.CreatePersonaChatAsync(task.OwnerId!, delegator.Id,
                ClaudeMode.AcceptEdits, name: $"Отчёт: {title}");
            targetSessionId = fresh.Id;
        }

        // ШАГ 1: гостевая реплика исполнителя — StoredTextMessage.PersonaId=B рендерит её его
        // лицом; текст с маркера «↩ Отчёт по делегированной задаче: …» первой строкой —
        // контракт для фронта (формат карточки/маркера — фронт)
        var reportText = BuildDelegationReportText(task);
        await _sessions.AppendStoredAsync(targetSessionId,
            new StoredTextMessage(reportText, personaId: executor.Id),
            new GuestTextMessage(reportText, executor.Id));

        // ШАГ 2: постановщик реагирует ВСЕГДА — платный авто-ход с контекстом отчёта
        await _sessions.SendMessageAsync(targetSessionId, BuildDelegatorReactionPrompt(task, executor),
            [], auto: true, senderPersonaId: delegator.Id);
    }

    // Маркер гостевой реплики-доклада: контракт для фронта — по нему отличают доклад
    // делегированной задачи от обычной реплики персоны в ленте
    internal const string DelegationReportMarker = "↩ Отчёт по делегированной задаче: ";

    // Тело resultMarkdown задачи для доклада — фолбэк на случай, если исполнитель завершил
    // задачу (done) не через tasks_complete с итогом (напр. вручную через UI/PUT)
    private static string DelegationReportBody(TaskItem task) =>
        string.IsNullOrWhiteSpace(task.ResultMarkdown) ? "(итог не указан)" : task.ResultMarkdown;

    // Текст гостевой реплики B: маркер + пустая строка + итог задачи
    internal static string BuildDelegationReportText(TaskItem task) =>
        $"{DelegationReportMarker}{task.Title}\n\n{DelegationReportBody(task)}";

    // Промпт авто-хода постановщика A: контекст отчёта B + просьба отреагировать
    internal static string BuildDelegatorReactionPrompt(TaskItem task, Persona executor) =>
        $"Персона-исполнитель {PersonaLabel(executor)} завершила делегированную тобой задачу " +
        $"«{task.Title}». Её отчёт: {DelegationReportBody(task)}\n\n" +
        "Отреагируй и продолжи работу при необходимости.";

    // Уведомление «ждёт ответа» (permission_request / AskUserQuestion)
    internal static NotificationMessage BuildWaitingNotification(TaskItem task, Persona? persona = null) => new(
        Title: "Ждёт ответа по задаче",
        Body: task.Title,
        Url: TaskSchedulerService.TaskUrl(task),
        Kind: "claude",
        PersonaId: persona?.Id,
        ProjectId: task.ProjectId,
        TaskId: task.Id,
        Tag: "Исполнитель");

    // Задача, привязанная к сессии, по которой идёт незавершённый запуск исполнителя
    private TaskItem? FindTracked(string sessionId) =>
        _tasks.GetBySession(sessionId) is { } t && IsAwaitingResult(t) ? t : null;

    private async Task NotifyAsync(TaskItem task, NotificationMessage message)
    {
        await _notif.SendNotificationMessageAsync(task.OwnerId!, message, sendPush: true);
    }
}
