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

/// <summary>
/// Разбивка размера системного промпта исполнителя по секциям (символы и токены-экв).
/// Используется для аналитики расхода токенов на постановку задач.
/// </summary>
internal sealed record TaskPromptMetrics(int TotalChars, int TotalTokensEst,
    int TaskSectionChars, int ExpectedResultChars, int ToolsChars,
    int MandatoryChars, int RestrictionsChars, int DelegationChars,
    int OmOChars, int ContextChars, int NotesContextChars);

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
    // Стор настроек специальностей: матрицы моделей по уровням и DefaultTier специальности
    // (ADR-007 §2). null — настройка не подключена, матрицы специальности не участвуют.
    private readonly SpecialtySettingsStore? _specialtySettings;
    // Резолвер модели исполнителя (ADR-007 §5.3): единая точка разворота уровня по матрицам
    // «персона → специальность → слоты». null — фолбэк на статический ResolveExecutorModel
    // (маркеры tier:*; для тестов без поднятого резолвера).
    private readonly Llm.ModelAssignmentResolver? _assignments;
    // Волна 6 (живая приёмка волны 5): гард «не более одной живой сессии на задачу» ниже
    // читает task.LinkedSessionId ДО того, как CreateAsync/MarkClaudeStarted успеют его
    // выставить — секунды на подъём CLI-процесса. Второй конкурентный вызов ExecuteAsync той
    // же задачи (координатор ретраит tasks_run_executor, решив, что первый вызов завис/упал)
    // проскакивал в это окно мимо гарда и поднимал ВТОРОГО исполнителя на ту же задачу —
    // отсюда и «падает без причины» у первого вызова, и задвоенный доклад о завершении у
    // TaskExecutionService.ReportToDelegatorAsync (оба исполнителя её закрывают). In-memory
    // claim на входе метода закрывает окно: конкурентный вызов получает мгновенный явный отказ
    // вместо гонки, а не решает загадочную «первую» ошибку молча.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _launching = new();
    // Текст ошибки последнего хода по сессии-исполнителю: ErrorMessage приходит ПЕРЕД result,
    // а распознавание терминального отказа (ExecutorStopClassifier) читает и статус, и текст.
    // Ключей столько же, сколько живых чатов-исполнителей: запись кладётся только для сессии
    // с отслеживаемой задачей и снимается её result'ом либо удалением чата.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _turnErrors = new();
    // Потолок накопленного текста ошибки: маркеры отказа стоят в первых строках, а держать
    // в памяти простыню от многошагового хода незачем
    private const int TurnErrorTextLimit = 4000;
    // Учёт размера постановки по секциям (шаг 4 плана оптимизации токенов). null — стор
    // не подключён (тесты без DI): замер тогда идёт только в лог, запуск задачи не страдает.
    private readonly Spend.TaskPromptMetricsStore? _promptMetrics;
    // Фич-флаги владельца задачи: гейт `task-report-card` (доклад одним сообщением плюс
    // реакция-решение вместо пересказа). null — сервис не подключён (тесты без DI),
    // считаем флаг выключенным, то есть работает прежний доклад.
    private readonly FeatureFlagService? _flags;
    // Паспорта прогонов сабагентов: отсюда исполнитель узнаёт, что сабагент его хода замолчал
    // на середине. null — стор не подключён (тесты без DI): ходы разбираются как раньше.
    private readonly Llm.Claude.SubagentRunLog? _subagentRuns;
    // Сколько ходов подряд оборвалось сабагентом по этой сессии-исполнителю. Продукт добивает
    // агента не более двух раз (SessionManager.MaxSubagentNudges) — дальше молчать нельзя:
    // работа реально встала, и это уже случай «зовите человека».
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _truncations = new();

    public TaskExecutionService(
        TaskManager tasks, SessionManager sessions, PersonaManager personas,
        IHubContext<SessionHub> hub, PushService push,
        NotesKnowledgeService kb,
        NotificationService notif,
        ILogger<TaskExecutionService> log, IConfiguration config,
        Llm.UserModelTierResolver? tiers = null, Llm.LlmProviderRegistry? providers = null,
        PersonaAgentFileSync? agentFiles = null, Execution.ILauncherFactory? launchers = null,
        SpecialtySettingsStore? specialtySettings = null,
        Llm.ModelAssignmentResolver? assignments = null,
        Spend.TaskPromptMetricsStore? promptMetrics = null,
        FeatureFlagService? flags = null,
        Llm.Claude.SubagentRunLog? subagentRuns = null)
    {
        _subagentRuns = subagentRuns;
        _flags = flags;
        _promptMetrics = promptMetrics;
        _tiers = tiers;
        _providers = providers;
        _agentFiles = agentFiles;
        _launchers = launchers;
        _specialtySettings = specialtySettings;
        _assignments = assignments;
        _tasks = tasks;
        _sessions = sessions;
        _personas = personas;
        _hub = hub;
        _push = push;
        _kb = kb;
        _log = log;
        _notif = notif;
        _sessions.OnSessionMessage += OnSessionMessageAsync;
        // Чат-исполнитель удалён/протух по TTL, не дождавшись result — снимаем накопленный
        // текст ошибки, чтобы буфер не жил дольше самой сессии
        _sessions.OnSessionDeleted += s =>
        {
            _turnErrors.TryRemove(s.Id, out _);
            _truncations.TryRemove(s.Id, out _);
        };
        // Точка B join-а (CT-8): D-сигнал (Status=Done из tasks_complete/PUT/UI) может прийти
        // раньше или позже R-сигнала (ResultMessage хода, точка A ниже) — TaskManager.Update
        // единственный путь в Done, поднимает событие ровно на переходе
        _tasks.TaskCompleted += OnTaskCompleted;
    }

    // Хук провала хода исполнителя для режима «Командная реализация» (Э4): вешает
    // TeamWaveService при старте — тем же приёмом, что SessionManager.TeamWaveStarter.
    // null — режима нет либо сервис не поднят: провал остаётся обычным (тост владельцу).
    public Func<TaskItem, Task>? TeamTaskFailed { get; set; }

    /// <summary>
    /// Запуск выполнения задачи Claude-ом: отдельная сессия в проекте задачи
    /// (личная — чат вне проекта) в режиме acceptEdits, первым сообщением — постановка.
    /// </summary>
    /// <exception cref="InvalidOperationException">задача не подходит или уже выполняется</exception>
    public async Task<TaskItem> ExecuteAsync(TaskItem task, bool auto)
    {
        if (task.Status == TaskItemStatus.Done)
            throw new InvalidOperationException("Задача уже завершена");
        // Прод 2026-08-02 (находка Веры): дубль доклада о завершении после того, как координатор
        // перезапустил исполнение под-задачи в ответ на разблокировку карточки остановки —
        // Status==Done выше блокирует релонч, ПОКА статус не переоткрыт в обход этого метода
        // (например прямым PUT/tasks_update). CompletionDelivered — необратимый CAS-флаг
        // (TaskManager.TryMarkCompletionDelivered): раз доклад уже ушёл, перезапуск той же
        // задачи не должен порождать второй — независимая страховка поверх проверки статуса,
        // не завязанная на то, что Status и CompletionDelivered меняются синхронно.
        if (task.CompletionDelivered)
            throw new InvalidOperationException("Доклад по этой задаче уже доставлен — перезапуск отключён");
        if (task.OwnerId is null)
            throw new InvalidOperationException("У задачи нет владельца");

        // Claim ДО гарда «одна сессия на задачу»: сам гард читает LinkedSessionId, который
        // этот же метод выставит только через несколько строк ниже (после подъёма CLI-процесса)
        // — без claim'а конкурентный вызов проходит гард мимо (см. комментарий у поля _launching).
        if (!_launching.TryAdd(task.Id, 0))
            throw new InvalidOperationException("По задаче уже запускается исполнитель — подождите и повторите");
        try
        {
            return await ExecuteClaimedAsync(task, auto).ConfigureAwait(false);
        }
        finally
        {
            // Снимается и при успехе, и при провале: успешный запуск к этому моменту уже
            // выставил LinkedSessionId (MarkClaudeStarted ниже) — дальнейшие повторные вызовы
            // держит штатный гард «одна сессия на задачу», claim им больше не нужен.
            _launching.TryRemove(task.Id, out _);
        }
    }

    private async Task<TaskItem> ExecuteClaimedAsync(TaskItem task, bool auto)
    {
        // Инвариант проверен в ExecuteAsync ДО claim'а; повторяем ради nullability-анализа
        // этого метода (граница методов рвёт поток null-check из вызывающего)
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
        // Модель исполнителя (ADR-007 §5.3): единая точка резолвера — уровень задачи →
        // модель персоны → уровень с матрицами (персона → специальность → слоты). Разворачивается
        // здесь и замораживается в Session.Model (§5.2). _assignments null (тесты без резолвера) —
        // фолбэк на статический маркерный ResolveExecutorModel.
        var model = _assignments is not null
            ? _assignments.ExecutorModel(task, persona, task.OwnerId)
            : ResolveExecutorModel(task, persona);
        // taskExecution: true — форсирует tasks-MCP даже у персоны с ограничением Persona.Tools
        // (без «tasks»): исполнитель обязан управлять задачей через mcp__tasks__*.
        var session = task.ProjectId is not null
            ? await _sessions.CreateAsync(task.ProjectId, ClaudeMode.AcceptEdits, name: name, model: model,
                effort: persona?.Effort, personaId: persona?.Id, taskExecution: true, taskId: task.Id)
            : await _sessions.CreateChatAsync(task.OwnerId, ClaudeMode.AcceptEdits, name: name, model: model,
                effort: persona?.Effort, personaId: persona?.Id, taskExecution: true, taskId: task.Id);
        if (task.ExecutionExpiresAfterMinutes is { } ttl) _sessions.SetExpiry(session.Id, ttl);

        // Задача с деревом (штаб «Командной реализации» раздаёт исполнителям своё): чат
        // стартует сразу в нём. Дерево уже существует — только присваиваем поля свежей
        // сессии до первого хода, cwd подставит EnsureProcessAsync. Невалидный путь не
        // должен ронять запуск: мягко деградируем в корень проекта.
        if (task.WorktreePath is { } worktree && task.ProjectId is not null
            && !await _sessions.AttachWorktreeAsync(session.Id, worktree, task.WorktreeBranch))
            _log.LogWarning("Дерево {Worktree} задачи {TaskId} не подошло (нет на диске либо не числится " +
                "в git worktree list проекта) — исполнитель стартует в корне проекта", worktree, task.Id);

        var updated = _tasks.MarkClaudeStarted(task.Id, session.Id, DateTime.UtcNow)
            ?? throw new InvalidOperationException("Задача удалена");
        await _hub.BroadcastTaskChangedAsync(task.OwnerId, "updated", updated);

        var prompt = BuildPrompt(updated, persona, ResolveTierAliases(task.OwnerId),
            ResolveCategoryProfilesPath(task));
        // Обогащение контекста семантически близкими заметками
        var notesBlock = await BuildNotesContextAsync(updated);
        prompt += notesBlock;
        // Инструментация: замер размера промпта по секциям (не влияет на поведение).
        // В стор едут ТОЛЬКО размеры — ни символа текста постановки и заметок (инвариант
        // приватности TaskPromptMetricsStore, под тестом-сторожем)
        var metrics = MeasurePrompt(prompt, notesBlock);
        _log.LogInformation("Prompt metrics task={TaskId} totalChars={TotalChars} totalTokensEst={TotalTokens} " +
            "task={TaskC} expected={ExpC} tools={ToolsC} mandatory={ManC} restrictions={RestrC} " +
            "delegation={DelC} omo={OmOC} context={CtxC} notes={NotesC}",
            task.Id, metrics.TotalChars, metrics.TotalTokensEst,
            metrics.TaskSectionChars, metrics.ExpectedResultChars, metrics.ToolsChars,
            metrics.MandatoryChars, metrics.RestrictionsChars, metrics.DelegationChars,
            metrics.OmOChars, metrics.ContextChars, metrics.NotesContextChars);
        _promptMetrics?.Record(new Spend.TaskPromptMetricsStore.Entry(
            DateTime.UtcNow, updated.Id, updated.OwnerId!, updated.ProjectId, session.Id, persona?.Id,
            metrics.TotalChars, metrics.TotalTokensEst,
            metrics.TaskSectionChars, metrics.ExpectedResultChars, metrics.ToolsChars,
            metrics.MandatoryChars, metrics.RestrictionsChars, metrics.DelegationChars,
            metrics.OmOChars, metrics.ContextChars, metrics.NotesContextChars));
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
    /// Модель чата-исполнителя (статический фолбэк, ADR-007 §5.3): уровень задачи сильнее
    /// модели персоны, та — сильнее уровня персоны. Отдаёт МАРКЕРЫ tier:* (в модель их
    /// развернёт ModelAssignmentResolver по слотам владельца — БЕЗ матриц, т.к. стор тут не
    /// доступен). Применяется, когда резолвер не подключён (тесты); в бою используется
    /// ModelAssignmentResolver.ExecutorModel (с матрицами «персона → специальность → слоты»).
    /// null — ни один шаг не сработал: сессия возьмёт модель по назначению места (tasks-executor).
    /// </summary>
    internal static string? ResolveExecutorModel(TaskItem task, Persona? persona)
    {
        if (task.ModelTier is { } taskTier) return Llm.LocalActionOverridesStore.TierRoute(taskTier);
        if (!string.IsNullOrWhiteSpace(persona?.Model)) return persona.Model;
        if (persona?.ModelTier is { } personaTier) return Llm.LocalActionOverridesStore.TierRoute(personaTier);
        return null;
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
            // id — только у невыполненных (см. тот же приём в BuildPersonaPrompt)
            foreach (var s in task.Subtasks)
                sb.AppendLine(s.IsDone ? $"- [x] {s.Title}" : $"- [ ] {s.Title} (id: {s.Id})");
        }
        if (task.LinkedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Связанные файлы:");
            foreach (var f in task.LinkedFiles)
                sb.AppendLine($"- {f}");
        }
        sb.AppendLine();
        // Правила сведены к сути: три инструкции вместо четырёх с повторами. Смысл тот же —
        // как вести статус, чем закрывать, что делать при невозможности
        sb.AppendLine("Правила:");
        sb.AppendLine("- Статус веди через tasks_*: задача уже inProgress, подзадачи отмечай tasks_toggle_subtask.");
        sb.AppendLine("- Готово = проверено и закрыто через tasks_complete с resultMarkdown (итог) " +
                      "и linkedFiles (итоговые файлы, если есть).");
        // Тот же канал эскалации, что и у персоны (см. комментарий в BuildPersonaPrompt)
        sb.AppendLine("- Застрял или выполнить невозможно — не завершай задачу: эскалируй через " +
                      "chats_report_up тому, кто её поставил (blocker: true — работа встала).");
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
        // Секции ОЖИДАЕМЫЙ РЕЗУЛЬТАТ, ОБЯЗАТЕЛЬНО, НЕЛЬЗЯ и ИНСТРУМЕНТЫ слиты в одну:
        // они говорили об одном и том же (заверши через tasks_complete с итогом и файлами)
        // тремя разными формулировками. Содержание правил — защищённое, тронут только
        // повтор: верификационная дисциплина и границы задачи ниже дословно те же.
        sb.AppendLine("## ПРАВИЛА");
        sb.AppendLine("- Готово = задача проверена и закрыта через tasks_complete с resultMarkdown " +
                      "(итог от твоего лица) и linkedFiles (итоговые файлы, если есть).");
        sb.AppendLine("- Статус веди через tasks_*: задача уже inProgress, подзадачи отмечай " +
                      "tasks_toggle_subtask.");
        // Эскалация: канал chats_report_up в продукте есть, но в постановке о нём не
        // говорилось — исполнитель о нём просто не знал и заканчивал ход молча, оставляя
        // задачу в inProgress. Адресата вычисляет сервер (ReportUpAsync → ParentSessionId):
        // это чат, откуда пришла задача, — постановщик-персона либо человек, если задачу
        // ставил он. Одним каналом закрыты оба адресата, различать их в промпте не нужно.
        sb.AppendLine("- Застрял (блокер, нет доступа, нужно решение) — не заканчивай ход молча: " +
                      "эскалируй через chats_report_up тому, кто поставил задачу " +
                      "(blocker: true — работа встала и ждёт ответа). Задачу не завершай.");
        // Верификационная дисциплина и правило остановки — из oh-my-openagent
        // (Hephaestus/Sisyphus-Junior, см. docs/omo/adoption.md)
        sb.AppendLine("- НЕТ СВИДЕТЕЛЬСТВ = НЕ ГОТОВО: перед завершением прогони фактическую проверку " +
                      "(сборка, тесты, реальный результат) и приведи её вывод в итоге.");
        sb.AppendLine("- Делегировал часть работы субагенту — не доверяй его отчёту на слово, проверь результат сам.");
        sb.AppendLine("- ОСТАНОВИСЬ после первой успешной верификации: не полируй сделанное и не выдумывай " +
                      "дополнительную работу сверх постановки.");
        sb.AppendLine("- Не выходи за рамки задачи и не трогай несвязанное.");
        sb.AppendLine("- Не заявляй завершение раньше времени: «почти готово» — это не готово.");
        sb.AppendLine();
        // Как резать крупную работу на субагентов. Таблица категорий (тип работы → уровень)
        // в промпт НЕ идёт: она дублирует шапку справочника на диске, ссылка на который
        // даётся ниже. В промпте остаётся только то, что без него не вывести, — выбор
        // канала и алиасы тиров конкретного владельца.
        sb.AppendLine("## ДЕЛЕГИРОВАНИЕ");
        sb.AppendLine("Крупную работу режь на субагентов, мелкую делай сам. Канал:");
        sb.AppendLine("- мнение, разведка, ревью куска (read-only) — `Task(персона, model=…)`;");
        sb.AppendLine("- работа с правками и отчётом — `tasks_create(personaId, modelTier)` " +
                      "+ `tasks_run_executor` (сама задача не стартует).");
        if (aliases.Any)
        {
            // Когда какой уровень брать — в справочнике профилей (ссылка ниже);
            // здесь только сами алиасы владельца, вывести их больше неоткуда
            var levels = new List<string>(3);
            if (aliases.Strong is { } strong) levels.Add($"сильная `{strong}`/`strong`");
            if (aliases.Medium is { } medium) levels.Add($"средняя `{medium}`/`medium`");
            if (aliases.Weak is { } weak) levels.Add($"слабая `{weak}`/`weak`");
            sb.AppendLine($"Уровень (`model=` в `Task` / `modelTier` в задаче): {string.Join("; ", levels)}.");
        }
        // Путь абсолютный: Read относительный не принимает. Не смогли обеспечить файл —
        // ссылку не даём вовсе, чтобы исполнитель не бился в несуществующий путь.
        if (categoryProfilesPath is not null)
            sb.AppendLine($"Профили категорий (какой уровень и как формулировать) — `{categoryProfilesPath}`.");
        sb.AppendLine();
        sb.AppendLine("## КОНТЕКСТ");
        if (task.Subtasks.Count > 0)
        {
            sb.AppendLine("Подзадачи:");
            // id выводим только у невыполненных: он нужен ровно для tasks_toggle_subtask,
            // а по уже отмеченной подзадаче звать его незачем. На длинных списках это
            // заметная часть постановки (~40 символов на строку)
            foreach (var s in task.Subtasks)
                sb.AppendLine(s.IsDone ? $"- [x] {s.Title}" : $"- [ ] {s.Title} (id: {s.Id})");
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

    // Измерить размер промпта по секциям: символы + грубая оценка токенов (~4 байта на символ UTF-8).
    // Не влияет на поведение — только для аналитики расхода.
    // Заголовок блока категорий OmO — по нему MeasurePrompt отличает промпт с этим блоком
    // от промпта без него (см. комментарий у поля omo)
    private const string OmoCategoriesProbe = "# Категории делегирования";

    internal static TaskPromptMetrics MeasurePrompt(string fullPrompt, string notesBlock)
    {
        var lines = fullPrompt.Split('\n');
        int totalChars = fullPrompt.Length;
        int totalTokensEst = totalChars / 4;
        int taskSection = 0, expected = 0, tools = 0, mandatory = 0, restrictions = 0;
        int delegation = 0, omo = 0, context = 0, notes = notesBlock.Length;

        string? current = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
            {
                current = trimmed[3..].Trim();
                continue;
            }
            var len = line.Length;
            switch (current)
            {
                case "ЗАДАЧА": case "Задача":
                    taskSection += len; break;
                case "ОЖИДАЕМЫЙ РЕЗУЛЬТАТ":
                    expected += len; break;
                // ИНСТРУМЕНТЫ слиты в ПРАВИЛА; ключ оставлен для чтения прежних замеров
                case "ИНСТРУМЕНТЫ":
                    tools += len; break;
                // ОБЯЗАТЕЛЬНО и НЕЛЬЗЯ слиты в ПРАВИЛА — обе прежние секции продолжают
                // считаться, чтобы baseline, снятый до слияния, оставался сопоставимым
                case "ПРАВИЛА": case "ОБЯЗАТЕЛЬНО":
                    mandatory += len; break;
                case "НЕЛЬЗЯ":
                    restrictions += len; break;
                case "ДЕЛЕГИРОВАНИЕ":
                    delegation += len; break;
                case "КОНТЕКСТ":
                    context += len; break;
            }
        }
        // Блок категорий OmO: считаем ФАКТ его наличия в промпте, а не длину константы —
        // безусловная подстановка врала в замерах постановки без персоны, где блока нет.
        omo = fullPrompt.Contains(OmoCategoriesProbe, StringComparison.Ordinal)
            ? Prompts.OmoPrompts.DelegationCategories.Length
            : 0;
        // Промпт без персоны — секций нет вовсе, весь текст постановки идёт одним куском
        if (taskSection == 0 && expected == 0 && mandatory == 0)
        {
            taskSection = totalChars - notes;
        }
        return new TaskPromptMetrics(totalChars, totalTokensEst,
            taskSection, expected, tools, mandatory, restrictions,
            delegation, omo, context, notes);
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
        // topK=2 (было 5): блок заметок — самая крупная переменная часть постановки, а хвост
        // выдачи семантического поиска релевантен всё слабее. Два верхних попадания дают
        // контекст, пять — платный шум на каждом ходу исполнителя.
        try { hits = await _kb.SearchAsync(task.OwnerId, query, topK: 2); }
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
        if (msg is not (ResultMessage or ErrorMessage or PermissionRequestMessage or AskQuestionMessage)) return;

        // Ищем задачу этой сессии с незавершённым запуском исполнителя
        var task = FindTracked(session.Id);
        if (task is null) return;

        switch (msg)
        {
            // Текст ошибки хода приезжает ОТДЕЛЬНЫМ сообщением перед result (CLI кладёт
            // API-ошибку в result-поле при subtype=success + is_error) — копим его до result,
            // иначе терминальный отказ не с чем распознавать (ResultMessage текста не несёт).
            case ErrorMessage err:
                NoteTurnError(session.Id, err.Text);
                return;
            case ResultMessage result:
                {
                    var errorText = TakeTurnError(session.Id);
                    // Терминальный отказ («дальше работать нечем») сильнее subtype: 401 у CLI
                    // регулярно приходит как формально успешный ход
                    var stopReason = ExecutorStopClassifier.Classify(result, errorText,
                        TakeSubagentState(session.Id));
                    // Восстановимая остановка (сабагент оборвался на середине): работа НЕ
                    // закончена, но и человека звать рано — продолжение уже ушло добиванием.
                    // Молчим: ни итога задаче, ни уведомления, ход досчитается следующим result.
                    if (stopReason is not null && !ExecutorStopClassifier.IsTerminal(stopReason))
                    {
                        _log.LogWarning("Ход исполнителя задачи {TaskId} оборван сабагентом ({Reason}) — " +
                            "итог не принимаем, ждём продолжения (попытка {Attempt})",
                            task.Id, stopReason, _truncations.GetValueOrDefault(session.Id));
                        break;
                    }
                    var ok = IsSuccess(result) && stopReason is null;
                    var updated = _tasks.MarkClaudeResult(task.Id, ok ? "success" : "error");
                    if (updated is null) return;
                    await _hub.BroadcastTaskChangedAsync(updated.OwnerId!, "updated", updated);
                    if (stopReason is not null)
                    {
                        // Своё уведомление вместо обычного «Не смог выполнить задачу»: у человека
                        // должно быть ровно одно сообщение о судьбе задачи, и оно про причину
                        await HandleExecutorStoppedAsync(updated, stopReason);
                        break;
                    }
                    if (!ok)
                    {
                        // Провал хода — L0 «не выполнена» требует только сигнал R (этот ход),
                        // join с сигналом D не нужен — задача обычно даже не Done.
                        // Уведомление о судьбе задачи ровно одно (см. NotifyDelegatorAsync):
                        // делегированная — от лица постановщика, обычная — от лица исполнителя
                        var persona = updated.PersonaId is not null ? _personas.Get(updated.PersonaId, updated.OwnerId!) : null;
                        if (!await NotifyDelegatorAsync(updated, ok))
                            await NotifyAsync(updated, BuildResultNotification(updated, ok, persona));
                        // Режим «Командная реализация» (Э4): провал под-задачи — одна перевыдача,
                        // второй провал той же под-задачи — эскалация. Решает TeamWaveService
                        // (он знает план и бюджет), поэтому здесь только хук — цикл зависимостей
                        // TeamWaveService → TaskExecutionService иначе замкнулся бы.
                        if (TeamTaskFailed is { } onFailed)
                        {
                            try { await onFailed(updated); }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "Обработка провала под-задачи {TaskId} режимом «Командная реализация» не удалась", updated.Id);
                            }
                        }
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

    // Судьба сабагентов только что закончившегося хода. Отметку об обрыве ставит приёмник
    // паспортов и снимаем её здесь одноразово; серию считаем сами: пока добивания не исчерпаны
    // (потолок SessionManager.MaxSubagentNudges) — обрыв восстановим, после — работа встала
    // и это уже случай «зовите человека». Ход без обрыва обрывает серию.
    internal ExecutorStopClassifier.SubagentTurnState TakeSubagentState(string sessionId)
    {
        if (_subagentRuns?.TakeTruncated(sessionId) is null)
        {
            _truncations.TryRemove(sessionId, out _);
            return SubagentStateFor(0);
        }
        return SubagentStateFor(_truncations.AddOrUpdate(sessionId, 1, (_, n) => n + 1));
    }

    // Правило эскалации отдельной функцией: пока добивания не исчерпаны — обрыв восстановим,
    // после потолка — работа встала (терминальная причина, зовём человека).
    internal static ExecutorStopClassifier.SubagentTurnState SubagentStateFor(int consecutiveTruncations) =>
        consecutiveTruncations <= 0 ? ExecutorStopClassifier.SubagentTurnState.None
        : consecutiveTruncations <= SessionManager.MaxSubagentNudges
            ? ExecutorStopClassifier.SubagentTurnState.Truncated
            : ExecutorStopClassifier.SubagentTurnState.Stuck;

    // Копим текст ошибок хода до его result (их может быть несколько — напр. ошибка API
    // и следом обрыв потока)
    private void NoteTurnError(string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _turnErrors.AddOrUpdate(sessionId, text,
            (_, prev) => prev.Length >= TurnErrorTextLimit ? prev : prev + "\n" + text);
    }

    // Забрать и очистить: текст относится к ЗАВЕРШИВШЕМУСЯ ходу, следующий копится с нуля
    private string? TakeTurnError(string sessionId) =>
        _turnErrors.TryRemove(sessionId, out var text) ? text : null;

    // Исполнитель встал насовсем: пометка на задаче + уведомление владельцу + (если задачу
    // ставила персона) доклад ей с пробуждением. Перезапуск НЕ делаем: причина терминальная,
    // повтор упёрся бы в неё же и дал серию одинаковых уведомлений.
    private async Task HandleExecutorStoppedAsync(TaskItem task, string reason)
    {
        var stopped = _tasks.MarkExecutorStopped(task.Id, DateTime.UtcNow, reason) ?? task;
        await _hub.BroadcastTaskChangedAsync(stopped.OwnerId!, "updated", stopped);

        var persona = stopped.PersonaId is not null ? _personas.Get(stopped.PersonaId, stopped.OwnerId!) : null;
        await NotifyAsync(stopped, BuildExecutorStoppedNotification(stopped, persona));
        _log.LogWarning("Исполнитель задачи {TaskId} «{Title}» остановлен: {Reason} (сессия {SessionId})",
            stopped.Id, stopped.Title, reason, stopped.LinkedSessionId);

        // Постановщика-персону будим ходом: делегированная работа встала, и её чат — тот же
        // канал, по которому исполнитель докладывает о блокере (chats_report_up). Задачу ставил
        // человек (CreatedByPersonaId == null) — будить некого, хватает уведомления выше.
        if (stopped.CreatedByPersonaId is null || stopped.LinkedSessionId is null) return;
        try
        {
            var result = await _sessions.ReportUpAsync(stopped.LinkedSessionId,
                BuildExecutorStoppedReport(stopped, reason), stopped.OwnerId!, withTurn: true);
            _log.LogInformation("Остановка исполнителя задачи {TaskId}: доклад постановщику — {Outcome}",
                stopped.Id, result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось доложить постановщику об остановке исполнителя задачи {TaskId}", stopped.Id);
        }
    }

    // Уведомление владельцу: почему работа встала и что задача ждёт его. Лицо — персона-исполнитель
    internal static NotificationMessage BuildExecutorStoppedNotification(TaskItem task, Persona? persona = null) => new(
        Title: "Исполнитель остановился",
        Body: $"{task.Title}: {ExecutorStopText(task.ExecutorStopReason)}. Работа не идёт, задача ждёт вас",
        Url: TaskSchedulerService.TaskUrl(task),
        Kind: "claude",
        PersonaId: persona?.Id,
        ProjectId: task.ProjectId,
        TaskId: task.Id,
        Tag: "Исполнитель");

    // Причина человеческим языком. Неизвестная (пометку поставил более новый код) — общая
    // формулировка вместо служебного ключа в глазах пользователя.
    internal static string ExecutorStopText(string? reason) => reason switch
    {
        ExecutorStopClassifier.AuthFailedReason => "не удалось авторизоваться у провайдера модели",
        ExecutorStopClassifier.SubagentStuckReason =>
            "сабагент раз за разом обрывается посреди работы, добить его не удалось",
        _ => "исполнение прервано",
    };

    // Текст доклада постановщику-персоне: факт, причина и что делать (перезапуск — его решение)
    internal static string BuildExecutorStoppedReport(TaskItem task, string reason) =>
        $"⛔ Исполнитель остановился по задаче «{task.Title}» (id: {task.Id}): " +
        $"{ExecutorStopText(reason)}. Работа не идёт, автоперезапуска не будет — " +
        "нужно решение человека (починить доступ и перезапустить исполнителя).";

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
        // L0: ровно ОДИН тост о факте завершения. Делегированная задача → уведомление
        // постановщика (ведёт в исходный чат, где лежит доклад ниже); обычная либо
        // нерезолвимый постановщик → обычное «Завершил работу над задачей».
        if (!await NotifyDelegatorAsync(task, ok: true))
            await NotifyAsync(task, BuildResultNotification(task, ok: true, persona));
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

    // L0-доставка постановщику: задача делегирована персоной из чата → уведомление от её лица
    // со ссылкой на исходный чат (без агентского хода — бесплатный дефолт). Оно ЗАМЕНЯЕТ собой
    // обычное «Завершил работу над задачей», а не дополняет его: постановщик-персона и владелец
    // задачи — всегда один и тот же человек (персоны per-owner, обе отправки идут в task.OwnerId),
    // поэтому два тоста об одном факте были дублем. Возвращает true — уведомление ушло, обычное
    // слать не надо; false — не делегирование, и вызывающий шлёт обычное.
    // Скип: постановщик не задан, совпадает с исполнителем или удалён.
    private async Task<bool> NotifyDelegatorAsync(TaskItem task, bool ok)
    {
        if (task.CreatedByPersonaId is null || task.CreatedByPersonaId == task.PersonaId)
        {
            _log.LogInformation("L0 задачи {TaskId}: пропуск — не делегирование (постановщик не задан или совпадает с исполнителем)", task.Id);
            return false;
        }
        var delegator = _personas.Get(task.CreatedByPersonaId, task.OwnerId!);
        if (delegator is null)
        {
            _log.LogInformation("L0 задачи {TaskId}: пропуск — постановщик {PersonaId} не найден", task.Id, task.CreatedByPersonaId);
            return false;
        }
        // SourceSessionId приходит из тела POST и мог указать на чужой чат — ссылку строим
        // только по сессии владельца задачи, иначе fallback на TaskUrl
        var sourceSession = task.SourceSessionId is not null ? _sessions.GetById(task.SourceSessionId) : null;
        if (sourceSession is not null && _sessions.ResolveOwnerId(sourceSession) != task.OwnerId)
            sourceSession = null;
        await NotifyAsync(task, BuildDelegatorNotification(task, ok, delegator, sourceSession));
        _log.LogInformation("L0 задачи {TaskId}: отправлено постановщику {PersonaId} (обычное уведомление подавлено)", task.Id, delegator.Id);
        return true;
    }

    // Уведомление постановщику о судьбе делегированной задачи: при успехе Url — исходный чат
    // (SourceSessionId), куда следом ложится доклад-реплика; чат удалён/неизвестен — ссылка на
    // задачу. При провале ссылка ВСЕГДА на задачу: доклада в исходном чате не будет, а разбираться
    // надо в карточке (оттуда виден чат исполнителя) — это уведомление теперь единственное.
    internal static NotificationMessage BuildDelegatorNotification(
        TaskItem task, bool ok, Persona delegator, Session? sourceSession) => new(
        Title: ok ? "Делегированная задача выполнена" : "Делегированная задача не выполнена",
        Body: task.Title,
        Url: sourceSession is null || !ok
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
        // Флаг владельца задачи: включён — карточка доклада (тексты для человека, id задачи
        // структурным полем) и реакция-решение; выключен — прежние тексты и прежний промпт
        var reportCard = IsReportCardEnabled(task);
        var reportText = BuildDelegationReportText(task, reportCard);
        var executorChatName = executorSession?.Name;
        // Время доклада ставим один раз и кладём в оба слоя (история + живая лента) —
        // тот же приём, что у reportTs в ReportUpAsync: фронт дедупит призрачный повтор
        // (BroadcastSessionMessageAsync шлёт событие и в session-, и в project_/user_-группу)
        // по совпадению timestamp+текста, без общего timestamp дедуп ключа не было бы.
        var reportTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // B1: id задачи едет структурным полем в ОБОИХ вариантах записи (исполнитель-персона →
        // guest_text, исполнитель без персоны → user_message) и в обоих слоях (история + живая
        // лента) — по нему карточка открывает задачу, не выковыривая id из текста маркера.
        // Пишем его независимо от флага: поле невидимо, пока карточка выключена, зато включение
        // флага поднимает карточку и на докладах, доставленных до него.
        await _sessions.AppendStoredAsync(targetSessionId,
            executor is not null
                ? new StoredTextMessage(reportText, personaId: executor.Id, timestamp: reportTs,
                    delegationTaskId: task.Id)
                : new StoredUserMessage(reportText, viaAgent: true, senderChatName: executorChatName,
                    timestamp: reportTs, delegationTaskId: task.Id),
            executor is not null
                ? new GuestTextMessage(reportText, executor.Id, reportTs, DelegationTaskId: task.Id)
                : new UserMessageMessage(reportText, null, null, true, null, executorChatName,
                    Timestamp: reportTs, DelegationTaskId: task.Id));

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
        //
        // B4, вход хода: silent гасит только призрак в очереди, а сам ход дальше идёт как
        // auto — и его текст лёг бы в ленту и в историю пузырём от лица постановщика с СЫРЫМ
        // служебным промптом («…ответь ровно <no-reply/>»). То есть при пустой реакции об
        // одном факте снова было бы два сообщения. staffNote переводит запись в плашку-
        // разделитель (как у ходов штаба — «Ответ на карточку передан координатору»): она
        // едет и в live-бродкаст, и в history.json, и переживает доставку из очереди.
        // Под флагом: без карточки доклада поведение прежнее (критерий приёмки 5).
        var deferred = await _sessions.SendOrEnqueueAsync(targetSessionId,
            BuildDelegatorReactionPrompt(task, executor, reportCard),
            senderPersonaId: delegator?.Id, silent: true, suppressTasksExecute: true,
            staffNote: reportCard ? DelegatorReactionStaffNote : null);
        _log.LogInformation("Доклад Z задачи {TaskId}: отправлен (гостевая реплика + реакция{Deferred}) в чат {SessionId}",
            task.Id, deferred ? " отложена — чат занят" : "", targetSessionId);
    }

    // Маркер гостевой реплики-доклада: контракт для фронта — по нему отличают доклад
    // делегированной задачи от обычной реплики персоны в ленте
    internal const string DelegationReportMarker = "↩ Отчёт по делегированной задаче: ";

    // Потолок итога в гостевой реплике. Отчёт постановщику нужен как СИГНАЛ «готово, вот
    // куда смотреть»: полный текст уже лежит в задаче и читается через tasks_get, а копия
    // в ленте оплачивается на каждом последующем ходу чата постановщика.
    // Короткий итог проходит целиком — резать его в «факт» смысла нет.
    internal const int DelegationReportBodyLimit = 400;

    // B2: хвост обрезанного итога адресован ЧЕЛОВЕКУ — «открыть задачу» есть действием прямо
    // на карточке. Прежний хвост звал модель к `tasks_get` по id: инструкция для LLM в тексте,
    // который читают глазами, и мусор в контексте чата постановщика.
    internal const string DelegationReportTruncatedTail = "Итог показан не полностью — открыть задачу";

    // B3: исполнитель закрыл задачу без итога (напр. вручную через UI/PUT, а не tasks_complete)
    internal const string DelegationReportNoResult = "Исполнитель не оставил описания результата";

    // Подпись плашки хода-реакции постановщика: единственное, что человек видит от служебного
    // промпта. Говорит о факте («доклад дошёл»), а не о протоколе — сам промпт остаётся под
    // капотом. Молчаливый ход после неё не оставляет реплики вовсе.
    internal const string DelegatorReactionStaffNote = "Доклад по задаче передан постановщику";

    // Тело resultMarkdown задачи для доклада — фолбэк на случай, если исполнитель завершил
    // задачу (done) не через tasks_complete с итогом (напр. вручную через UI/PUT).
    // Длинный итог обрезается по границе строки с явной пометкой, где читать целиком.
    // reportCard — флаг `task-report-card` владельца: тексты для человека вместо прежних.
    private static string DelegationReportBody(TaskItem task, bool reportCard)
    {
        var body = task.ResultMarkdown;
        if (string.IsNullOrWhiteSpace(body))
            return reportCard ? DelegationReportNoResult : "(итог не указан)";
        body = body.Trim();
        if (body.Length <= DelegationReportBodyLimit) return body;

        // Режем по последнему переводу строки в пределах лимита, иначе — жёстко по лимиту:
        // обрыв на середине markdown-конструкции ломает разметку карточки в ленте
        var head = body[..DelegationReportBodyLimit];
        var cut = head.LastIndexOf('\n');
        if (cut > DelegationReportBodyLimit / 2) head = head[..cut];
        var tail = reportCard
            ? DelegationReportTruncatedTail
            : $"… итог целиком — в задаче (`tasks_get` по id {task.Id}).";
        return head.TrimEnd() + "\n\n" + tail;
    }

    // Текст гостевой реплики B: маркер + пустая строка + итог задачи
    internal static string BuildDelegationReportText(TaskItem task, bool reportCard = false) =>
        $"{DelegationReportMarker}{task.Title}\n\n{DelegationReportBody(task, reportCard)}";

    // Промпт авто-хода постановщика A: выжимка (id/название) + просьба отреагировать.
    // MINOR 1: полное тело resultMarkdown сюда НЕ дублируем — оно уже перед этим ходом
    // легло в ленту гостевой репликой B (ШАГ 1), A видит его при resume без пересказа.
    //
    // B4 (reportCard=true): просим не «отреагировать», а ПРИНЯТЬ РЕШЕНИЕ — карточка доклада уже
    // отвечает на вопрос «что сделано», и пересказ итога был вторым сообщением об одном факте.
    // Решать нечего — ход отвечает ровно маркером молчания, и реплики в ленте не появляется
    // (гасит TurnAccumulator: после стрижки маркеров текст пуст → записи нет).
    internal static string BuildDelegatorReactionPrompt(TaskItem task, Persona? executor,
        bool reportCard = false)
    {
        // Исполнитель может быть Claude без персоны (assignee=Claude, PersonaId=null) — тогда
        // PersonaLabel падал бы на null. Подпись nullable: персона → «Роль (Имя)», без персоны —
        // нейтральная (ход идёт от постановщика, так что безличный исполнитель в промпте —
        // только субъект события, а не спикер).
        var who = executor is not null
            ? $"Персона-исполнитель {PersonaLabel(executor)} завершила"
            : "Исполнитель-Claude (без персоны) завершил";
        var head = $"{who} делегированную тобой задачу «{task.Title}» (id: {task.Id}). ";
        if (!reportCard)
            return head +
                "Отчёт исполнителя только что появился выше в ленте.\n\n" +
                "Отреагируй и продолжи работу при необходимости.";

        return head +
            "Отчёт исполнителя уже показан выше в ленте — пересказывать его не нужно.\n\n" +
            "Реши, что дальше: продолжаем работу (скажи, что делаешь), ставим новую задачу " +
            "или работа по этой линии закрыта. Пиши только решение и следующий шаг.\n\n" +
            // Маркер даём БЕЗ обратных кавычек: в код-блоке (в т.ч. инлайн-`…`) он не считается
            // активным ни стрижкой, ни HasNoReplyMarker — модель, скопировав оформление из
            // промпта, оставила бы его в ленте буквальным текстом
            $"Решать нечего — ответь ровно {SessionManager.NoReplyMarker} и больше ничем: " +
            "тогда реплика в ленте не появится.";
    }

    // Флаг `task-report-card` владельца задачи. Владельца нет (данные до мультипользователя)
    // либо сервис флагов не подключён — считаем выключенным: доклад работает как раньше.
    private bool IsReportCardEnabled(TaskItem task) =>
        task.OwnerId is { } owner && (_flags?.IsEnabled(owner, FeatureFlagKeys.TaskReportCard) ?? false);

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
