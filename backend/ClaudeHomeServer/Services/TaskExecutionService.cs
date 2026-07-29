using System.Text;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Алиасы тиров моделей владельца (opus/sonnet/haiku) для таблицы уровней в постановке.
/// Пустой алиас = за слотом стоит модель, чей тир алиасом не выражается (сторонний
/// провайдер, незнакомый ID) — строку уровня в промпт не выводим, чтобы не врать.
/// </summary>
internal sealed record ModelTierAliases(string? Strong, string? Medium, string? Weak)
{
    public static readonly ModelTierAliases None = new(null, null, null);

    public bool Any => Strong is not null || Medium is not null || Weak is not null;
}

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
    // Слоты тиров владельца + реестр провайдеров: только ради алиасов в таблице уровней
    // постановки (какой model= передавать в Task). null — постановка без этой таблицы.
    private readonly Llm.UserModelTierResolver? _tiers;
    private readonly Llm.LlmProviderRegistry? _providers;
    // Справочник профилей категорий: даёт абсолютный путь для ссылки в постановке
    // и гарантирует, что файл на диске есть. null — ссылки в промпте не будет.
    private readonly PersonaAgentFileSync? _agentFiles;
    // Среда исполнения владельца: путь справочника в постановке должен быть адресуем
    // ИЗ неё, а не с хоста. null — считаем среду локальной (перевод тождественный).
    private readonly Execution.ILauncherFactory? _launchers;

    public TaskExecutionService(
        TaskManager tasks, SessionManager sessions, PersonaManager personas,
        IHubContext<SessionHub> hub, PushService push,
        NotesKnowledgeService kb,
        NotificationService notif,
        ILogger<TaskExecutionService> log, IConfiguration config,
        Llm.UserModelTierResolver? tiers = null, Llm.LlmProviderRegistry? providers = null,
        PersonaAgentFileSync? agentFiles = null, Execution.ILauncherFactory? launchers = null)
    {
        _tiers = tiers;
        _providers = providers;
        _agentFiles = agentFiles;
        _launchers = launchers;
        _tasks = tasks;
        _sessions = sessions;
        _personas = personas;
        _hub = hub;
        _push = push;
        _kb = kb;
        _log = log;
        _notif = notif;
        _sessions.OnSessionMessage += OnSessionMessageAsync;
        // Точка B join-а (CT-8): D-сигнал (Status=Done из tasks_complete/PUT/UI) может прийти
        // раньше или позже R-сигнала (ResultMessage хода, точка A ниже) — TaskManager.Update
        // единственный путь в Done, поднимает событие ровно на переходе
        _tasks.TaskCompleted += OnTaskCompleted;
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
        var model = ResolveExecutorModel(task, persona);
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

        var prompt = BuildPrompt(updated, persona, ResolveTierAliases(task.OwnerId),
            ResolveCategoryProfilesPath(task));
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

    /// <summary>
    /// Модель чата-исполнителя: уровень задачи (её ставит постановщик под конкретную работу)
    /// сильнее конкретной модели персоны, та — сильнее уровня персоны. Уровень отдаём маркером
    /// «tier:*»: в модель его развернёт ModelAssignmentResolver (единая точка склейки слотов).
    /// null — модель не задана: сессия возьмёт её по назначению места (tasks-executor).
    /// </summary>
    internal static string? ResolveExecutorModel(TaskItem task, Persona? persona)
    {
        if (task.ModelTier is { } taskTier) return Llm.LocalActionOverridesStore.TierRoute(taskTier);
        if (!string.IsNullOrWhiteSpace(persona?.Model)) return persona.Model;
        return persona?.ModelTier is { } personaTier
            ? Llm.LocalActionOverridesStore.TierRoute(personaTier)
            : null;
    }

    // Алиасы тиров владельца для таблицы уровней в постановке: слот → модель → её алиас
    // (пин алиаса, а не ID — он резолвится у любого провайдера, см. PersonaAgentFileSync)
    private ModelTierAliases ResolveTierAliases(string? ownerId)
    {
        if (_tiers is null || _providers is null) return ModelTierAliases.None;
        string? Alias(ModelTier tier) =>
            PersonaAgentFileSync.ModelAliasFor(_providers, _tiers.ModelFor(tier, ownerId));
        return new ModelTierAliases(Alias(ModelTier.Strong), Alias(ModelTier.Medium), Alias(ModelTier.Weak));
    }

    // Путь справочника категорий для ссылки в постановке. Бэкенд работает с хостовыми
    // путями, а читать файл будет процесс исполнителя в среде владельца: у container-
    // пользователя рабочая папка смонтирована под другим корнем, и хостовый адрес там
    // не существует. Перевод — единственным способом, IPathMapper среды владельца.
    private string? ResolveCategoryProfilesPath(TaskItem task)
    {
        var hostPath = _agentFiles?.EnsureCategoryProfiles(task.OwnerId!, task.ProjectId);
        if (hostPath is null) return null;
        var paths = _launchers?.ForOwner(task.OwnerId).Paths ?? Execution.IdentityPathMapper.Instance;
        var runtimePath = ToRuntimeOrNull(paths, hostPath);
        if (runtimePath is null)
            _log.LogDebug("Справочник категорий {Path} недоступен в среде исполнения владельца {Owner} — " +
                          "ссылку в постановку не кладу", hostPath, task.OwnerId);
        return runtimePath;
    }

    // null — путь вне монтирований среды (аналог SafeJoin, см. DockerPathMapper):
    // ссылаться на такой адрес нельзя, лучше не давать ссылки вовсе.
    internal static string? ToRuntimeOrNull(Execution.IPathMapper paths, string hostPath)
    {
        try { return paths.ToRuntime(hostPath); }
        catch { return null; }
    }

    // Постановка задачи для Claude: контекст + правила ведения статуса через MCP tasks_*.
    // С персоной — структурированный 6-секционный контракт (персона-исполнитель);
    // без персоны — прежний формат (обратная совместимость).
    internal static string BuildPrompt(TaskItem task, Persona? persona = null,
        ModelTierAliases? aliases = null, string? categoryProfilesPath = null)
    {
        if (persona is not null)
            return BuildPersonaPrompt(task, aliases ?? ModelTierAliases.None, categoryProfilesPath);

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
    private static string BuildPersonaPrompt(TaskItem task, ModelTierAliases aliases,
        string? categoryProfilesPath = null)
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
                      "сразу запусти её исполнение через tasks_run_executor — сама она не стартует.");
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
        // Как резать крупную работу на субагентов: короткая таблица профилей (OmO) +
        // выбор канала и уровня модели. Полные профили — файлом на диске, не в промпте.
        sb.AppendLine("## ДЕЛЕГИРОВАНИЕ");
        sb.AppendLine("Крупную задачу режь на субагентов по таблице ниже; мелкую делай сам.");
        sb.AppendLine();
        sb.AppendLine("Канал делегирования выбирай по характеру работы:");
        sb.AppendLine();
        sb.AppendLine("| Что нужно | Канал | Кто ставит уровень |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| Мнение, разведка, ревью куска — read-only | `Task(персона, model=…)` | ты в вызове |");
        sb.AppendLine("| Полноценная работа с правками и отчётом | `tasks_create(personaId, modelTier)` " +
                      "+ `tasks_run_executor` | ты в задаче |");
        if (aliases.Any)
        {
            sb.AppendLine();
            sb.AppendLine("Уровень — поправка к дефолту места (без него субагент идёт назначением " +
                          "«сабагенты-консультанты», задача — «исполнитель задач»):");
            sb.AppendLine();
            sb.AppendLine("| Уровень | `model=` в вызове `Task` | `modelTier` в задаче | Когда |");
            sb.AppendLine("|---|---|---|---|");
            if (aliases.Strong is { } strong)
                sb.AppendLine($"| сильная | `{strong}` | `strong` | тяжёлое рассуждение, запутанная архитектура |");
            if (aliases.Medium is { } medium)
                sb.AppendLine($"| средняя | `{medium}` | `medium` | обычная работа |");
            if (aliases.Weak is { } weak)
                sb.AppendLine($"| слабая | `{weak}` | `weak` | рутина |");
        }
        sb.AppendLine();
        sb.AppendLine(Prompts.OmoPrompts.DelegationCategories);
        // Путь абсолютный: Read относительный не принимает. Не смогли обеспечить файл —
        // ссылку не даём вовсе, чтобы исполнитель не бился в несуществующий путь.
        if (categoryProfilesPath is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Нужен развёрнутый профиль категории (промпт-довесок, ворота выбора, " +
                          $"предупреждения вызывающему) — прочитай файл `{categoryProfilesPath}`.");
        }
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

    // Наблюдатель сообщений всех сессий: реагируем только на сессии, привязанные к задачам.
    // internal — для юнит-тестов join-логики (TaskExecutionServiceJoinTests), вызывающих
    // метод напрямую с синтетическими Session/ResultMessage вместо живого хода claude.exe.
    internal async Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        if (msg is not (ResultMessage or PermissionRequestMessage or AskQuestionMessage)) return;

        // Ищем задачу этой сессии с незавершённым запуском исполнителя
        var task = FindTracked(session.Id);
        if (task is null) return;

        switch (msg)
        {
            case ResultMessage result:
                {
                    var ok = IsSuccess(result);
                    var updated = _tasks.MarkClaudeResult(task.Id, ok ? "success" : "error");
                    if (updated is null) return;
                    await _hub.BroadcastTaskChangedAsync(updated.OwnerId!, "updated", updated);
                    if (!ok)
                    {
                        // Провал хода — L0 «не выполнена» требует только сигнал R (этот ход),
                        // join с сигналом D не нужен — задача обычно даже не Done
                        var persona = updated.PersonaId is not null ? _personas.Get(updated.PersonaId, updated.OwnerId!) : null;
                        await NotifyAsync(updated, BuildResultNotification(updated, ok, persona));
                        await NotifyDelegatorAsync(updated, ok);
                    }
                    else
                    {
                        // Успех хода — точка A join-а: сигнал R есть, доставка (L0 + Z) зависит
                        // от сигнала D (Status=Done). Промежуточные успешные ходы многошаговой
                        // задачи (Status ещё не Done) молча пропускаются гейтом внутри — не спамят.
                        await TryDeliverCompletionAsync(updated);
                    }
                    break;
                }
            case PermissionRequestMessage or AskQuestionMessage:
                {
                    var persona = task.PersonaId is not null ? _personas.Get(task.PersonaId, task.OwnerId!) : null;
                    await NotifyAsync(task, BuildWaitingNotification(task, persona));
                    break;
                }
        }
    }

    // Точка B join-а: TaskManager.TaskCompleted — синхронное событие (Update не может стать
    // async без переделки всех вызывающих — UI/MCP/NoteTaskSyncService), поэтому доставку
    // уводим в фон. Ошибки логируем — фоновая доставка не должна ронять вызывающий PUT/MCP-запрос.
    private void OnTaskCompleted(TaskItem task)
    {
        _ = Task.Run(async () =>
        {
            try { await TryDeliverCompletionAsync(task); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка доставки завершения задачи {TaskId}", task.Id);
            }
        });
    }

    // Единая точка доставки завершения задачи — join двух независимых сигналов: R (конец хода,
    // ClaudeResult) и D (Status=Done, из tasks_complete/PUT/UI). Вызывается из двух мест —
    // конца успешного хода (OnSessionMessageAsync) и перехода в Done (TaskManager.TaskCompleted) —
    // в любом порядке их прихода. Идемпотентность — CAS-флаг TaskItem.CompletionDelivered.
    // internal — для юнит-тестов (TaskExecutionServiceJoinTests).
    internal async Task TryDeliverCompletionAsync(TaskItem task)
    {
        if (task.ClaudeResult is null)
        {
            _log.LogInformation("Доклад задачи {TaskId}: пропуск — ждём конца хода (сигнал R)", task.Id);
            return;
        }
        if (task.Status != TaskItemStatus.Done)
        {
            _log.LogInformation("Доклад задачи {TaskId}: пропуск — статус не Done (сигнал D)", task.Id);
            return;
        }
        if (!_tasks.TryMarkCompletionDelivered(task.Id))
        {
            _log.LogInformation("Доклад задачи {TaskId}: пропуск — уже доставлено", task.Id);
            return;
        }

        var persona = task.PersonaId is not null ? _personas.Get(task.PersonaId, task.OwnerId!) : null;
        // L0: тост владельцу «завершил работу» + отдельное уведомление постановщику (если делегировано)
        await NotifyAsync(task, BuildResultNotification(task, ok: true, persona));
        await NotifyDelegatorAsync(task, ok: true);
        // Модель Z: активный доклад в чат постановщика (в дополнение к L0-тосту выше)
        await ReportToDelegatorAsync(task, persona);
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
        if (task.CreatedByPersonaId is null || task.CreatedByPersonaId == task.PersonaId)
        {
            _log.LogInformation("L0 задачи {TaskId}: пропуск — не делегирование (постановщик не задан или совпадает с исполнителем)", task.Id);
            return;
        }
        var delegator = _personas.Get(task.CreatedByPersonaId, task.OwnerId!);
        if (delegator is null)
        {
            _log.LogInformation("L0 задачи {TaskId}: пропуск — постановщик {PersonaId} не найден", task.Id, task.CreatedByPersonaId);
            return;
        }
        // SourceSessionId приходит из тела POST и мог указать на чужой чат — ссылку строим
        // только по сессии владельца задачи, иначе fallback на TaskUrl
        var sourceSession = task.SourceSessionId is not null ? _sessions.GetById(task.SourceSessionId) : null;
        if (sourceSession is not null && _sessions.ResolveOwnerId(sourceSession) != task.OwnerId)
            sourceSession = null;
        await NotifyAsync(task, BuildDelegatorNotification(task, ok, delegator, sourceSession));
        _log.LogInformation("L0 задачи {TaskId}: отправлено постановщику {PersonaId}", task.Id, delegator.Id);
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

    // Применим ли активный доклад (модель Z) к завершённой задаче: исполнитель — персона,
    // отличная от постановщика, и (MAJOR 2) есть живой SourceSessionId. У 2-го+ экземпляра
    // регулярной делегированной задачи SourceSessionId не переносится SpawnNextOccurrence
    // (конкретная сессия начинается заново каждый раз) — без него уходили бы в fallback
    // НОВЫЙ чат + платный ход на каждый повтор (30/месяц у ежедневной); ограничиваемся уже
    // отправленным L0-тостом (NotifyDelegatorAsync).
    //
    // Сам АДРЕСАТ доклада берётся не отсюда, а из эффективного родителя чата-исполнителя
    // (ReportToDelegatorAsync) — ручная группировка чатов может увести доклад в другой чат
    // либо погасить его вовсе. Здесь SourceSessionId работает только как признак «задача
    // вообще делегирована из чата».
    // Куда докладывать: эффективный родитель чата-исполнителя — он учитывает ручную группировку
    // (ParentOverrideId побеждает связь по задаче, ParentDetached гасит её вовсе). Чата-исполнителя
    // нет (задача закрыта без запуска) — остаётся сырой SourceSessionId. null — докладывать некуда.
    // internal — для юнит-тестов.
    internal static string? ResolveReportTarget(Session? executorSession, string? sourceSessionId) =>
        executorSession is not null ? executorSession.ParentSessionId : sourceSessionId;

    // Доклад применим, когда задача вообще делегирована из чата (есть SourceSessionId) и
    // исполнитель не докладывает сам себе. Персоны с обеих сторон больше НЕ обязательны:
    // раньше без них отчёт не отправлялся вовсе, и задача, поставленная человеком, тихо
    // завершалась без следа в родительском чате. Лицо для карточки есть всегда: персона
    // исполнителя либо нейтральная карточка с именем его чата.
    internal static bool ShouldReportToDelegator(TaskItem task, Persona? executor) =>
        task.SourceSessionId is not null &&
        (task.CreatedByPersonaId is null || executor is null || task.CreatedByPersonaId != executor.Id);

    // MINOR 2: реакцию постановщика A можно слать, только если S — не групповой чат, либо A
    // входит в его участников (иначе переключить спикера не на кого — реагировать некому,
    // а ход по текущему/ведущему спикеру пришёл бы не от лица постановщика)
    internal static bool CanSendDelegatorReaction(IReadOnlyList<string>? participants, string delegatorPersonaId) =>
        participants is not { Count: > 1 } || participants.Contains(delegatorPersonaId);

    // Платный авто-ход постановщика (ШАГ 2 модели Z) пускаем только при живой персоне-постановщике.
    // Задачу мог поставить человек из обычного чата (CreatedByPersonaId=null): ШАГ 1 (гостевая
    // реплика исполнителя) идёт и без персоны, а вот авто-ход не от чьего лица — его запуск
    // оживлял бы рабочий чат пользователя скрытым платным расходом на каждое завершение задачи.
    internal static bool ShouldSendDelegatorReaction(Persona? delegator) => delegator is not null;

    // Модель Z: активный доклад о завершении делегированной задачи — в отличие от L0-тоста
    // (NotifyDelegatorAsync) кладёт репорт прямо в чат постановщика. ШАГ 1 — гостевая реплика
    // исполнителя B с готовым resultMarkdown (0 токенов, без агентского хода); ШАГ 2 — сразу
    // за ней платный авто-ход постановщика A с реакцией (--resume). Исходный чат S мёртв/чужой/
    // не найден → fallback в новый чат A. Применимо только когда исполнитель — персона
    // (без неё нет «лица» для гостевой реплики; L0-тост выше это уже покрывает).
    private async Task ReportToDelegatorAsync(TaskItem task, Persona? executor)
    {
        if (!ShouldReportToDelegator(task, executor))
        {
            _log.LogInformation("Доклад Z задачи {TaskId}: пропуск — задача не делегирована из чата либо исполнитель сам себе постановщик", task.Id);
            return;
        }
        // Постановщик-персона нужен только чтобы реакция шла от её лица и чтобы был fallback
        // в новый чат. Его отсутствие (задачу поставил человек) доклад больше не отменяет.
        var delegator = task.CreatedByPersonaId is not null
            ? _personas.Get(task.CreatedByPersonaId, task.OwnerId!)
            : null;

        // Цель доклада — ЭФФЕКТИВНЫЙ родитель чата-исполнителя, а не сырой SourceSessionId:
        // ручная группировка (перетаскивание в списке чатов) побеждает связь по задаче, а явный
        // вынос в корень её гасит — «вынес из группы» значит «не докладывай туда». Чата-исполнителя
        // нет (задача закрыта без запуска) — остаётся SourceSessionId, как было.
        var executorSession = task.LinkedSessionId is not null ? _sessions.GetById(task.LinkedSessionId) : null;
        var targetId = ResolveReportTarget(executorSession, task.SourceSessionId);
        if (targetId is null)
        {
            _log.LogInformation("Доклад Z задачи {TaskId}: пропуск — у чата-исполнителя нет родителя (вынесен в корень)", task.Id);
            return;
        }

        // Владелец S — как в NotifyDelegatorAsync: чужая/неизвестная сессия не годится
        var sourceSession = _sessions.GetById(targetId);
        if (sourceSession is not null && _sessions.ResolveOwnerId(sourceSession) != task.OwnerId)
        {
            _log.LogInformation("Доклад Z задачи {TaskId}: родительский чат {SessionId} чужой — fallback в новый чат", task.Id, targetId);
            sourceSession = null;
        }

        string targetSessionId;
        Session? targetSession;
        if (sourceSession is not null)
        {
            targetSessionId = sourceSession.Id;
            targetSession = sourceSession;
        }
        else if (delegator is not null)
        {
            var title = task.Title.Length > 60 ? task.Title[..60] + "…" : task.Title;
            var fresh = await _sessions.CreatePersonaChatAsync(task.OwnerId!, delegator.Id,
                ClaudeMode.AcceptEdits, name: $"Отчёт: {title}");
            targetSessionId = fresh.Id;
            targetSession = fresh;
        }
        else
        {
            // Родительский чат мёртв/чужой, а персоны-постановщика нет — заводить новый чат
            // не от чьего лица; ограничиваемся L0-тостом, отправленным выше
            _log.LogInformation("Доклад Z задачи {TaskId}: родительский чат недоступен, постановщика-персоны нет — только тост", task.Id);
            return;
        }

        // ШАГ 1: гостевая реплика исполнителя — StoredTextMessage.PersonaId=B рендерит её его
        // лицом; текст с маркера «↩ Отчёт по делегированной задаче: …» первой строкой —
        // контракт для фронта (формат карточки/маркера — фронт)
        // Лицо доклада: персона-исполнитель, иначе — нейтральная карточка с именем её чата
        // («Задача: починить билд»), тот же вид, что у входящих сообщений без персоны
        var reportText = BuildDelegationReportText(task);
        var executorChatName = executorSession?.Name;
        await _sessions.AppendStoredAsync(targetSessionId,
            executor is not null
                ? new StoredTextMessage(reportText, personaId: executor.Id)
                : new StoredUserMessage(reportText, viaAgent: true, senderChatName: executorChatName),
            executor is not null
                ? new GuestTextMessage(reportText, executor.Id)
                : new UserMessageMessage(reportText, null, null, true, null, executorChatName));

        // ШАГ 2 (платный авто-ход постановщика) — только при живом постановщике-персоне.
        // Задачу мог поставить человек из обычного чата (без персоны): тогда ход не от чьего
        // лица, и скрытый платный авто-ход в рабочий чат пользователя на каждое завершение
        // задачи запускать нельзя. Гостевая реплика ШАГ 1 уже в ленте (она и есть польза от
        // связи), плюс L0-тост отправлен выше в NotifyDelegatorAsync.
        if (!ShouldSendDelegatorReaction(delegator))
        {
            _log.LogInformation("Доклад Z задачи {TaskId}: нет постановщика-персоны — гостевая реплика без авто-хода", task.Id);
            return;
        }

        // MINOR 2: S — групповой чат. Без @упоминания реакция построилась бы по текущему/
        // ведущему спикеру, а не по постановщику A (senderPersonaId — только атрибуция
        // реплики, не смена спикера). A ∈ участников — переключаем спикера на него перед
        // ходом; иначе реагировать некому — ограничиваемся гостевой репликой B + L0-тостом.
        if (delegator is not null && !CanSendDelegatorReaction(targetSession?.Participants, delegator.Id))
        {
            _log.LogInformation("Доклад Z задачи {TaskId}: групповой чат {SessionId} без постановщика среди участников — реакция не отправлена", task.Id, targetSessionId);
            return;
        }
        if (delegator is not null && targetSession?.Participants is { Count: > 1 })
            _sessions.SetPersona(targetSessionId, task.OwnerId!, delegator.Id);

        // ШАГ 2: постановщик реагирует ВСЕГДА — платный авто-ход с контекстом отчёта.
        // MAJOR 1: tasks_run_executor запрещён на этом ходу — A может отреагировать и даже
        // создать новую задачу (tasks_create), но не самозапустить её. Без запрета A по
        // промпту «продолжи работу» мог бы tasks_create+tasks_run_executor → новая задача
        // глубины 0 → новый доклад → новая реакция → бесконечный платный цикл A↔B
        // (гард DelegationDepth<3 цепочку исполнителей ловит, а не переделегирование A).
        // Чат постановщика может быть занят своим ходом — тогда реакция встаёт в очередь
        // сессии и уйдёт после него. Раньше ход полагался на неявную очередь семафора
        // в адаптере: она невидима и молча теряет ходы при Interrupt. Гостевая реплика
        // (ШАГ 1) при этом уже в ленте, поэтому призраком реакцию не показываем (silent).
        var deferred = await _sessions.SendOrEnqueueAsync(targetSessionId,
            BuildDelegatorReactionPrompt(task, executor),
            senderPersonaId: delegator?.Id, silent: true, suppressTasksExecute: true);
        _log.LogInformation("Доклад Z задачи {TaskId}: отправлен (гостевая реплика + реакция{Deferred}) в чат {SessionId}",
            task.Id, deferred ? " отложена — чат занят" : "", targetSessionId);
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

    // Промпт авто-хода постановщика A: выжимка (id/название) + просьба отреагировать.
    // MINOR 1: полное тело resultMarkdown сюда НЕ дублируем — оно уже перед этим ходом
    // легло в ленту гостевой репликой B (ШАГ 1), A видит его при resume без пересказа
    internal static string BuildDelegatorReactionPrompt(TaskItem task, Persona? executor)
    {
        // Исполнитель может быть Claude без персоны (assignee=Claude, PersonaId=null) — тогда
        // PersonaLabel падал бы на null. Подпись nullable: персона → «Роль (Имя)», без персоны —
        // нейтральная (ход идёт от постановщика, так что безличный исполнитель в промпте —
        // только субъект события, а не спикер).
        var who = executor is not null
            ? $"Персона-исполнитель {PersonaLabel(executor)} завершила"
            : "Исполнитель-Claude (без персоны) завершил";
        return
            $"{who} делегированную тобой задачу «{task.Title}» (id: {task.Id}). " +
            "Отчёт исполнителя только что появился выше в ленте.\n\n" +
            "Отреагируй и продолжи работу при необходимости.";
    }

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
