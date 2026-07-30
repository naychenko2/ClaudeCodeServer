using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Prompts;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Раздача под-задач и запуск волн режима «Командная реализация» (Э3): по подтверждённому
// плану бэкенд создаёт задачи (personaId = исполнитель, SourceSessionId = чат-штаб,
// CreatedByPersonaId = координатор) и ПАКЕТНО стартует исполнителей волны. Дочерние чаты
// исполнения прикрепляются к штабу сами — Session.ParentSessionId вычисляется из
// Task.SourceSessionId, новой иерархии не заводим.
// Волны считает бэкенд, а не модель: волна = под-задачи, у которых нет невыполненных
// зависимостей (все под-задачи предыдущих волн закрыты).
// См. docs/architecture/team-implement-mode.md, раздел «Этапы → Э3».
public class TeamWaveService
{
    private readonly SessionManager _sessions;
    private readonly TaskManager _tasks;
    private readonly ProjectManager _projects;
    private readonly IHubContext<SessionHub> _hub;
    private readonly TaskExecutionService? _exec;
    private readonly NotificationService? _notif;
    private readonly ILogger<TeamWaveService> _log;
    // Таймаут волны (Э4): волна молчит дольше — сторож поднимает эскалацию.
    private readonly TimeSpan _waveTimeout;
    // Переходы волны идут из двух независимых потоков (колбэк завершения задачи и сторож),
    // поэтому «кто закрывает волну» решается под этим локом, а не проверкой состояния на глаз.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _waveLocks = new();

    public TeamWaveService(SessionManager sessions, TaskManager tasks, ProjectManager projects,
        IHubContext<SessionHub> hub, ILogger<TeamWaveService> log,
        // Опционально (в тестах не передаётся): без исполнителя задачи создаются, но не стартуют
        TaskExecutionService? exec = null,
        // Опционально: уведомление + web push на каждую остановку (Э4)
        NotificationService? notif = null,
        IConfiguration? config = null)
    {
        _sessions = sessions;
        _tasks = tasks;
        _projects = projects;
        _hub = hub;
        _log = log;
        _exec = exec;
        _notif = notif;
        // Дефолт великоват намеренно: таймаут считается от ПОСЛЕДНЕЙ активности волны, и
        // срабатывать он должен на настоящем зависании, а не на длинной честной работе
        // (сборка + тесты у исполнителя легко занимают полчаса).
        _waveTimeout = TimeSpan.FromMinutes(
            int.TryParse(config?["TeamImplement:WaveTimeoutMinutes"], out var m) && m > 0 ? m : 90);
        // Хук раздачи в SessionManager: цикл зависимостей (TaskExecutionService → SessionManager)
        // разорван так же, как у подписки TaskExecutionService на OnSessionMessage
        _sessions.TeamWaveStarter = (session, plan) => StartWaveAsync(session, plan);
        // Э4: карточка остановки + уведомление с push — единая точка на все триггеры
        _sessions.TeamEscalationRaiser = RaiseEscalationAsync;
        // Закрытие волны ловим на переходе задачи в Done — единственном пути в Done (Update)
        _tasks.TaskCompleted += OnTaskDone;
        // Провал хода исполнителя: одна перевыдача, второй провал — эскалация
        if (_exec is not null) _exec.TeamTaskFailed = OnTaskFailedAsync;
    }

    // Под-задачи очередной волны: минимальный номер волны среди нерозданных, и только если
    // все под-задачи предыдущих волн уже закрыты (это и есть «нет невыполненных зависимостей»).
    // Пустой результат — раздавать нечего либо предыдущая волна ещё в работе.
    internal static (int Wave, List<TeamImplementSubtask> Subtasks) SelectWave(
        TeamImplementPlan plan, Func<string, bool> isTaskDone)
    {
        var pending = plan.Subtasks.Where(s => s.TaskId is null).ToList();
        if (pending.Count == 0) return (0, []);

        var wave = pending.Min(s => s.Wave);
        // Зависимость волны — все под-задачи с меньшим номером: не роздана либо не закрыта =
        // ждём. Иначе исполнители следующей волны сели бы на неготовый результат предыдущей.
        var blocked = plan.Subtasks.Any(s => s.Wave < wave && (s.TaskId is null || !isTaskDone(s.TaskId)));
        if (blocked) return (wave, []);

        return (wave, pending.Where(s => s.Wave == wave).ToList());
    }

    // Раздача очередной волны плана: создание задач + пакетный запуск исполнителей.
    // Возвращает созданные задачи (пустой список — раздавать нечего или волна ждёт предыдущую).
    // Точек вызова три (подтверждение плана, авто-волна после закрытия предыдущей, кнопка
    // «Запустить» на карточке), и прийти они могут одновременно — поэтому раздача идёт под
    // per-session локом: две параллельные раздачи одной волны создали бы дубли задач.
    public async Task<IReadOnlyList<TaskItem>> StartWaveAsync(Session session, TeamImplementPlan plan)
    {
        var gate = _waveLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await StartWaveCoreAsync(session, plan); }
        finally { gate.Release(); }
    }

    // Тело раздачи БЕЗ лока: зовётся из StartWaveAsync (взял лок) и из закрытия волны
    // (оно уже держит тот же лок — SemaphoreSlim не реентрантен, повторный захват = дедлок).
    private async Task<IReadOnlyList<TaskItem>> StartWaveCoreAsync(Session session, TeamImplementPlan plan)
    {
        if (session.TeamImplement is not { } team) return [];
        var ownerId = session.OwnerId
            ?? (session.ProjectId is { } pid ? _projects.GetById(pid)?.OwnerId : null);
        if (ownerId is null) return [];

        // m1/m2 (второй проход Глеба): у чата БЕЗ аккумулятора GetTeamPlanAsync каждый раз
        // десериализует НОВЫЙ объект плана — аргумент мог быть прочитан ДО того, как сюда же
        // (под тем же per-session семафором волны) записался снимок соседнего вызова. Раз мы
        // уже держим семафор (StartWaveAsync/CloseWaveIfDoneAsync), перечитываем канонический
        // план прямо тут: любой предыдущий держатель лока успел дописать его на диск, прежде
        // чем освободить семафор (SaveTeamPlanCardAsync ниже — синхронно до return/release).
        plan = await _sessions.GetTeamPlanAsync(session.Id, plan.Id) ?? plan;

        var (wave, subtasks) = SelectWave(plan, id => _tasks.GetById(id)?.Status == TaskItemStatus.Done);
        if (subtasks.Count == 0)
        {
            _log.LogInformation("Волна {Wave} плана {PlanId} не стартовала: нечего раздавать или предыдущая волна не закрыта",
                wave, plan.Id);
            return [];
        }

        // Гейт бюджета (Э4) и резерв под волну — ОДНОЙ транзакцией, потому что раздельные
        // «проверить остаток» и «списать по факту» давали перерасход: между ними успевали
        // пройти квота хода координатора и соседняя раздача. Резервируем всю волну сразу:
        // она либо помещается в остаток целиком, либо не стартует и даёт карточку с расходом.
        // Здесь же — стадия, номер волны и отсечки сторожа: одно состояние, одна запись.
        // Reserved=false у сессии без режима: WithTeamState отдаёт default, и молчаливое
        // «резерв прошёл» на выключенном режиме было бы худшим исходом.
        var gate = _sessions.WithTeamState(session.Id, t =>
        {
            // Человек нажал «Остановить»: текущие исполнители дорабатывают, новые не стартуют
            if (t.Stopped) return (Reserved: false, Stopped: true, Exceeded: (string?)null);
            if (t.Budget.ExceededReasonForWave(subtasks.Count) is { } reason)
                return (Reserved: false, Stopped: false, Exceeded: reason);

            t.Budget.TasksUsed += subtasks.Count;
            t.Budget.RunsUsed += subtasks.Count;
            t.Budget.WavesUsed++;
            t.WaveNumber = wave;
            if (t.PlannedWaves < plan.WaveCount) t.PlannedWaves = plan.WaveCount;
            t.Stage = TeamImplementStage.Wave;
            // Отсечки для сторожа зависших волн (Э4): волна идёт, пока поля не обнулены
            t.WaveStartedAt = DateTime.UtcNow;
            t.WaveActivityAt = DateTime.UtcNow;
            return (Reserved: true, Stopped: false, Exceeded: null);
        });
        if (gate.Stopped)
        {
            _log.LogInformation("Волна {Wave} плана {PlanId} не стартовала: практика остановлена человеком", wave, plan.Id);
            return [];
        }
        if (gate.Exceeded is { } exceeded)
        {
            await RaiseEscalationAsync(session, new TeamEscalation
            {
                Kind = TeamEscalationKind.BudgetExhausted,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.BudgetExhausted, exceeded),
                Details = $"Волна {wave} не стартовала целиком ({subtasks.Count} под-задач): {exceeded}.\n\n"
                          + TeamImplementPrompts.BudgetLine(team.Budget),
                Wave = wave,
                Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
            });
            return [];
        }
        // Режим успели выключить между проверкой в начале и транзакцией — раздавать некуда
        if (!gate.Reserved) return [];

        var created = new List<TaskItem>();
        foreach (var subtask in subtasks)
        {
            var task = _tasks.Create(session.ProjectId, ownerId, new CreateTaskRequest(
                Title: subtask.Title,
                Description: TeamImplementPrompts.SubtaskDescription(plan, subtask,
                    session.WorktreePath, session.WorktreeBranch),
                // Исполнитель — персона плана; PersonaId подразумевает Assignee=Claude
                PersonaId: subtask.ExecutorPersonaId,
                // Происхождение: чат-штаб как источник (из него же вычисляется
                // Session.ParentSessionId дочернего чата) и координатор как постановщик
                SourceSessionId: session.Id,
                CreatedByPersonaId: team.CoordinatorPersonaId ?? session.PersonaId,
                Labels: ["Командная реализация", $"волна {subtask.Wave}"]));
            // Поля под-задачи правит и перевыдача (OnTaskFailedAsync) — держим их под тем же
            // локом состояния, иначе на одном объекте плана было бы два разных лока
            _sessions.WithTeamState(session.Id, _ =>
            {
                subtask.TaskId = task.Id;
                // Первая попытка по под-задаче: провал даст ровно одну перевыдачу (Э4)
                subtask.Attempts = 1;
                return true;
            });
            created.Add(task);
            await _hub.BroadcastTaskChangedAsync(ownerId, "created", task);
        }

        // Состояние и бюджет уже записаны транзакцией-резервом выше (счёт ведёт бэкенд
        // в точке запуска, а не модель) — здесь остаётся сохранить и разослать.
        await _sessions.SaveTeamPlanCardAsync(session.Id, plan);
        await _sessions.SaveTeamImplementStateAsync(session.Id);

        _log.LogInformation("Волна {Wave} плана {PlanId} роздана: {Count} задач (чат-штаб {SessionId})",
            wave, plan.Id, created.Count, session.Id);

        // Пакетный запуск исполнителей — фоном: старт CLI-процессов долгий, а карточки задач
        // и состояние волны человек должен увидеть сразу после клика «Запустить».
        if (_exec is not null) _ = Task.Run(() => LaunchAllAsync(created));
        return created;
    }

    // Пакетный старт исполнителей волны: провал одного не отменяет остальных —
    // такая задача остаётся в Todo и подхватывается перевыдачей координатора (Э4).
    private async Task LaunchAllAsync(IReadOnlyList<TaskItem> tasks)
    {
        foreach (var task in tasks)
        {
            try { await _exec!.ExecuteAsync(task, auto: true); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Запуск исполнителя по задаче {TaskId} «{Title}» не удался", task.Id, task.Title);
            }
        }
    }

    // --- Э4: автономный цикл волн ---

    // Задача закрыта (единственный путь в Done — TaskManager.Update): возможно, это была
    // последняя задача волны. Событие синхронное, поэтому работу уводим в фон, как это
    // делает TaskExecutionService.OnTaskCompleted.
    private void OnTaskDone(TaskItem task) => _ = Task.Run(async () =>
    {
        try { await OnTeamTaskDoneAsync(task); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Закрытие волны по задаче {TaskId} не удалось", task.Id);
        }
    });

    internal async Task OnTeamTaskDoneAsync(TaskItem task)
    {
        var (session, team, plan) = await ResolveContextAsync(task);
        if (session is null || team is null || plan is null) return;
        // Закрытая задача — активность волны: пока они закрываются, сторож молчит,
        // сколько бы волна ни шла (таймаут считается от последней активности).
        _sessions.WithTeamState(session.Id, t =>
        {
            if (t.WaveStartedAt is not null) t.WaveActivityAt = DateTime.UtcNow;
            return true;
        });
        await CloseWaveIfDoneAsync(session, team, plan);
    }

    // Штаб, состояние режима и план по задаче волны. null-кортеж — задача не из режима
    // (обычная задача пользователя), дальше идти незачем.
    private async Task<(Session? Session, SessionTeamImplement? Team, TeamImplementPlan? Plan)> ResolveContextAsync(TaskItem task)
    {
        if (task.SourceSessionId is not { } stabId) return (null, null, null);
        if (_sessions.GetById(stabId) is not { } session) return (null, null, null);
        if (session.TeamImplement is not { } team) return (null, null, null);
        if (team.PlanCardId is not { } planId) return (null, null, null);
        var plan = await _sessions.GetTeamPlanAsync(stabId, planId);
        // Задача могла прийти из этого же чата, но мимо плана (координатор завёл её руками)
        if (plan is null || !plan.Subtasks.Any(s => s.TaskId == task.Id)) return (null, null, null);
        return (session, team, plan);
    }

    // Волна закрывается, когда все её розданные под-задачи в Done. Дальше — по флагу авто:
    // либо следующая волна стартует сама (карточек согласования между волнами нет), либо
    // человек получает карточку «Волна N закрыта. Запустить следующую?».
    private async Task CloseWaveIfDoneAsync(Session session, SessionTeamImplement team, TeamImplementPlan plan)
    {
        // Ход-сводка координатору уходит ПОСЛЕ освобождения лока: запуск хода поднимает
        // процесс CLI (секунды), и держать на это время закрытие волны незачем.
        string? summaryTurn = null;
        var gate = _waveLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var wave = team.WaveNumber;
            // Волну закрываем ровно один раз: колбэки завершения задач приходят из разных
            // потоков, а между ними стадия могла уйти в «ждёт решения» (блокер исполнителя) —
            // это закрытию не мешает, работа-то доделана.
            if (wave == 0 || team.ClosedWave >= wave) return;

            var current = plan.Subtasks.Where(s => s.Wave == wave && s.TaskId is not null).ToList();
            if (current.Count == 0) return;
            if (!current.All(s => IsDone(s.TaskId!))) return;

            _sessions.WithTeamState(session.Id, t =>
            {
                t.ClosedWave = wave;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            await _sessions.SaveTeamImplementStateAsync(session.Id);
            var hasNext = plan.Subtasks.Any(s => s.TaskId is null);
            var nextWave = hasNext ? plan.Subtasks.Where(s => s.TaskId is null).Min(s => s.Wave) : (int?)null;
            _log.LogInformation("Волна {Wave} чата-штаба {SessionId} закрыта: {Count} задач, следующая — {Next}",
                wave, session.Id, current.Count, nextWave?.ToString() ?? "нет");

            var started = false;
            if (hasNext && !team.Stopped && team.AutoWaves)
            {
                // Авто: следующая волна идёт сама. Бюджет проверяет раздача — она же
                // поднимет карточку исчерпания, если квота кончилась. Зовём Core: лок волны
                // уже наш, повторный захват семафора здесь означал бы дедлок.
                started = (await StartWaveCoreAsync(session, plan)).Count > 0;
            }
            else if (hasNext && !team.Stopped)
            {
                await RaiseEscalationAsync(session, new TeamEscalation
                {
                    Kind = TeamEscalationKind.WaveGate,
                    Title = $"Волна {wave} закрыта. Запустить волну {nextWave}?",
                    Details = $"Закрыто под-задач: {current.Count}. Осталось волн по плану: " +
                              $"{plan.Subtasks.Where(s => s.TaskId is null).Select(s => s.Wave).Distinct().Count()}. " +
                              "Авто-волны сняты, поэтому следующая ждёт вашего решения.",
                    Wave = wave,
                    Actions = TeamEscalationActions.For(TeamEscalationKind.WaveGate),
                });
            }
            else if (!hasNext)
            {
                // Волны кончились — стадия проверки: сборка/тесты и итоговый отчёт
                _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Checking; return true; });
                await _sessions.SaveTeamImplementStateAsync(session.Id);
            }

            // Сводку волны публикует координатор — ему и уходит ход с фактами закрытия
            summaryTurn = TeamImplementPrompts.WaveClosedTurn(wave, current.Count, current.Count, started, nextWave);
        }
        finally { gate.Release(); }

        if (summaryTurn is not null)
            await _sessions.SendOrEnqueueAsync(session.Id, summaryTurn,
                senderPersonaId: null, silent: true, suppressTasksExecute: true);
    }

    // Провал хода исполнителя (хук TaskExecutionService.TeamTaskFailed): одна перевыдача
    // с учётом причины, второй провал той же под-задачи — эскалация человеку.
    internal async Task OnTaskFailedAsync(TaskItem task)
    {
        var (session, team, plan) = await ResolveContextAsync(task);
        if (session is null || team is null || plan is null) return;

        // m2 (второй проход Глеба): без лока и перечитывания два параллельных провала волны
        // брали каждый свой (возможно устаревший) снимок плана и потом целиком перезаписывали
        // его на диск (SaveTeamPlanCardAsync ниже) — последняя запись затирала Attempts++
        // соседа, и вместо одной перевыдачи выходило две. Держим тот же per-session семафор,
        // что раздача (StartWaveAsync/CloseWaveIfDoneAsync), и перечитываем план под ним.
        var gate = _waveLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        TeamImplementSubtask subtask;
        bool retry;
        try
        {
            plan = await _sessions.GetTeamPlanAsync(session.Id, plan.Id) ?? plan;
            subtask = plan.Subtasks.First(s => s.TaskId == task.Id);

            // Решение «перевыдать или звать человека» и расход перевыдачи — одной транзакцией:
            // иначе два параллельных провала волны прошли бы проверку потолка вдвоём и оба
            // перевыдали работу сверх квоты.
            retry = _sessions.WithTeamState(session.Id, t =>
            {
                var blocked = t.Stopped
                    || t.Budget.RetriesUsed >= t.Budget.MaxRetries
                    || t.Budget.RunsUsed >= t.Budget.MaxRuns;
                if (subtask.Attempts >= 2 || blocked) return false;
                subtask.Attempts++;
                t.Budget.RetriesUsed++;
                t.Budget.RunsUsed++;
                // Перевыдача — тоже движение волны: сторож зависаний отсчитывает срок заново
                if (t.WaveStartedAt is not null) t.WaveActivityAt = DateTime.UtcNow;
                return true;
            });

            if (retry)
            {
                // Перевыдача: тому же исполнителю, но с причиной прошлого провала в описании —
                // «повтори то же самое» без контекста провала обычно даёт тот же результат.
                // Счётчики уже израсходованы транзакцией выше. Сохраняем ПЕРЕЧИТАННЫЙ план —
                // запись отражает актуальное состояние соседних под-задач, а не устаревший снимок.
                await _sessions.SaveTeamPlanCardAsync(session.Id, plan);
                await _sessions.SaveTeamImplementStateAsync(session.Id);
            }
        }
        finally { gate.Release(); }

        if (!retry)
        {
            await RaiseEscalationAsync(session, new TeamEscalation
            {
                Kind = TeamEscalationKind.TaskFailed,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.TaskFailed, task.Title),
                Details = subtask.Attempts >= 2
                    ? $"Под-задача «{task.Title}» провалилась дважды — перевыдача не помогла.\n\n"
                      + TeamImplementPrompts.BudgetLine(team.Budget)
                    : $"Под-задача «{task.Title}» провалилась, а перевыдать её нельзя: "
                      + (team.Stopped ? "практика остановлена человеком." : team.Budget.ExceededReason() ?? "исчерпан потолок перевыдач."),
                TaskId = task.Id,
                Wave = subtask.Wave,
                Actions = TeamEscalationActions.For(TeamEscalationKind.TaskFailed),
            });
            return;
        }

        var reason = string.IsNullOrWhiteSpace(task.ClaudeResult) ? "ход завершился ошибкой" : task.ClaudeResult;
        var updated = _tasks.Update(task.Id, new UpdateTaskRequest(
            Description: task.Description + "\n\n## Повторная попытка\n" +
                $"Прошлый запуск не довёл задачу до конца ({reason}). Разберись, что пошло не так, " +
                "и доведи работу до фактической проверки — «почти готово» не считается."));
        if (updated is null) return;
        await _hub.BroadcastTaskChangedAsync(updated.OwnerId!, "updated", updated);

        if (_exec is null) return;
        try
        {
            await _exec.ExecuteAsync(updated, auto: true);
            _log.LogInformation("Под-задача {TaskId} «{Title}» перевыдана (попытка {Attempt})",
                updated.Id, updated.Title, subtask.Attempts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Перевыдача под-задачи {TaskId} не удалась", updated.Id);
        }
    }

    // Сторож зависших волн (TeamWaveWatchdog, тик раз в минуту): волна молчит дольше
    // таймаута — человек получает карточку, а не бесконечное «идёт волна 2».
    public async Task CheckStalledWavesAsync()
    {
        foreach (var session in _sessions.GetTeamImplementSessions())
        {
            if (session.TeamImplement is not { } team) continue;
            if (team.Stage != TeamImplementStage.Wave || team.WaveStartedAt is not { } startedAt) continue;
            // Отсчёт — от последней активности волны (закрытая задача, перевыдача), а не от
            // её старта: волна из пяти долгих задач живая, пока задачи закрываются одна
            // за другой, и эскалировать её только за длительность — ложная тревога.
            var lastActivity = team.WaveActivityAt ?? startedAt;
            if (DateTime.UtcNow - lastActivity < _waveTimeout) continue;

            var stalled = StalledTaskTitles(session, team.WaveNumber);
            await RaiseEscalationAsync(session, new TeamEscalation
            {
                Kind = TeamEscalationKind.WaveStalled,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.WaveStalled,
                    stalled.FirstOrDefault() ?? $"волна {team.WaveNumber}"),
                Details = $"Волна {team.WaveNumber} идёт дольше {_waveTimeout.TotalMinutes:0} минут и не закрывается. " +
                          (stalled.Count > 0
                              ? "Молчат задачи: " + string.Join(", ", stalled.Select(t => $"«{t}»")) + "."
                              : "Незакрытых задач волны не видно — возможно, они удалены."),
                Wave = team.WaveNumber,
                Actions = TeamEscalationActions.For(TeamEscalationKind.WaveStalled),
            });
            _log.LogWarning("Волна {Wave} чата-штаба {SessionId} не двигается дольше таймаута",
                team.WaveNumber, session.Id);
        }
    }

    // Названия незакрытых задач текущей волны — для текста карточки зависания
    private List<string> StalledTaskTitles(Session session, int wave)
    {
        var ownerId = session.OwnerId
            ?? (session.ProjectId is { } pid ? _projects.GetById(pid)?.OwnerId : null);
        IReadOnlyCollection<TaskItem> pool = session.ProjectId is { } projectId
            ? _tasks.GetByProject(projectId)
            : ownerId is not null ? _tasks.GetByOwner(ownerId) : [];
        return [.. pool
            .Where(t => t.SourceSessionId == session.Id
                && t.Labels.Contains($"волна {wave}")
                && t.Status != TaskItemStatus.Done)
            .Select(t => t.Title)];
    }

    // Единая точка остановки: карточка в ленте + уведомление, а если человека нет в чате —
    // ещё и web push. Молчаливых остановок в режиме быть не должно.
    public async Task RaiseEscalationAsync(Session session, TeamEscalation escalation)
    {
        await _sessions.PublishTeamEscalationAsync(session.Id, escalation);

        var ownerId = session.OwnerId
            ?? (session.ProjectId is { } pid ? _projects.GetById(pid)?.OwnerId : null);
        if (ownerId is null || _notif is null) return;

        // Push — только когда человека нет в этом чате: он и так видит карточку в ленте
        var away = !_sessions.HasViewers(session.Id);
        await _notif.SendNotificationMessageAsync(ownerId, new Protocol.NotificationMessage(
            Title: "Команда ждёт вашего решения",
            Body: escalation.Title,
            Url: string.IsNullOrEmpty(session.ProjectId)
                ? $"/chats/{session.Id}"
                : $"/project/{session.ProjectId}/chat/{session.Id}",
            Kind: "claude",
            TaskId: escalation.TaskId,
            ProjectId: session.ProjectId,
            PersonaId: session.TeamImplement?.CoordinatorPersonaId ?? session.PersonaId,
            // SessionId — унаследованное init-свойство базы ServerMessage, не параметр
            // конструктора: задаётся инициализатором (см. NotificationService и др.)
            Tag: "Командная реализация") { SessionId = session.Id }, sendPush: away);
    }

    private bool IsDone(string taskId) => _tasks.GetById(taskId)?.Status == TaskItemStatus.Done;
}
