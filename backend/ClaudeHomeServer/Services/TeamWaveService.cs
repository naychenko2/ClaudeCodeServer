using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Prompts;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Живость волны «Командной реализации» для человека: бейдж «КР · волна N» различает
// работу и обвал (КР-наблюдаемость, этап 1). Wire-токен — TeamWaveService.LivenessToken.
internal enum WaveLiveness
{
    // Задачи двигаются (или молчат меньше QuietMinutes) — всё в порядке
    Alive,
    // Тишина дольше QuietMinutes — работы не видно, но порог зависания ещё не вышел
    Quiet,
    // Штаб заявляет работу (Working/Waiting), а живого прогона CLI нет — ход убит
    // сбоем, статус не разобран. Это обвал, а не тишина.
    Dead,
    // Тишина дольше StalledMinutes — до карточки сторожа остались минуты (или она
    // уже на подходе)
    Stalled,
}

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
    private readonly PersonaManager _personas;
    private readonly ILogger<TeamWaveService> _log;
    // Таймаут волны (Э4): волна молчит дольше — сторож поднимает эскалацию.
    private readonly TimeSpan _waveTimeout;
    // Страховка мёртвой зоны конвейера (прод 2026-08-17): волна закрыта, следующая не
    // роздана и раздачу никто не позвал. Работа не идёт вовсе, поэтому порог короче
    // общего таймаута волны — висеть так долго конвейер не должен ни при каком раскладе.
    private readonly TimeSpan _deadZoneTimeout;
    // Пороги liveness пульса волны (КР-наблюдаемость, этап 1): quiet — тишина дольше
    // QuietMinutes (работа вроде жива, но давно не двигалась), stalled — дольше
    // StalledMinutes (похоже на зависание до срабатывания сторожа). stalled заведомо
    // тише таймаута волны: пульс должен предупредить РАНЬШЕ карточки, а не дублировать её.
    private readonly TimeSpan _quietThreshold;
    private readonly TimeSpan _stalledThreshold;
    // Переходы волны идут из двух независимых потоков (колбэк завершения задачи и сторож),
    // поэтому «кто закрывает волну» решается под этим локом, а не проверкой состояния на глаз.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _waveLocks = new();

    public TeamWaveService(SessionManager sessions, TaskManager tasks, ProjectManager projects,
        IHubContext<SessionHub> hub, ILogger<TeamWaveService> log,
        // Имя персоны-автора для текста уведомлений и push (Э8) — зависимость обязательная:
        // «карточки и уведомления от лица персоны» это требование фичи, а не украшение,
        // и молча деградировать до обезличенного текста из-за DI мы не хотим.
        PersonaManager personas,
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
        _personas = personas;
        // Дефолт великоват намеренно: таймаут считается от ПОСЛЕДНЕЙ активности волны, и
        // срабатывать он должен на настоящем зависании, а не на длинной честной работе
        // (сборка + тесты у исполнителя легко занимают полчаса).
        _waveTimeout = TimeSpan.FromMinutes(
            int.TryParse(config?["TeamImplement:WaveTimeoutMinutes"], out var m) && m > 0 ? m : 90);
        _deadZoneTimeout = TimeSpan.FromMinutes(
            int.TryParse(config?["TeamImplement:DeadZoneTimeoutMinutes"], out var dz) && dz > 0 ? dz : 15);
        _quietThreshold = TimeSpan.FromMinutes(
            int.TryParse(config?["TeamImplement:QuietMinutes"], out var q) && q > 0 ? q : 15);
        _stalledThreshold = TimeSpan.FromMinutes(
            int.TryParse(config?["TeamImplement:StalledMinutes"], out var st) && st > 0 ? st : 30);
        // Хук раздачи в SessionManager: цикл зависимостей (TaskExecutionService → SessionManager)
        // разорван так же, как у подписки TaskExecutionService на OnSessionMessage
        _sessions.TeamWaveStarter = (session, plan, trigger) => StartWaveAsync(session, plan, trigger);
        // Э4: карточка остановки + уведомление с push — единая точка на все триггеры
        _sessions.TeamEscalationRaiser = RaiseEscalationAsync;
        // Э8: ASK-вопрос интервью тоже будит человека уведомлением и push
        _sessions.TeamQuestionNotifier = OnStabQuestionAsync;
        // Minor (волна 3): кнопки skip/drop карточки эскалации закрывают под-задачу тем же
        // путём, что доклад исполнителя — иначе волна не закрывалась до ручного tasks_complete
        _sessions.TeamSubtaskDropHandler = DropSubtaskAsync;
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
    // trigger — повод вызова (D1, ревью 2026-08-17): явная кнопка человека раздаёт как есть,
    // докрут по состоянию при снятых авто-волнах заменяется гейт-карточкой. Дефолта нет
    // намеренно (круг 3): «забыли передать повод» деградировало бы в молчаливую раздачу
    // мимо гейта — ровно ту дверь, которую закрывали, поэтому каждый вызов называет повод явно.
    public async Task<IReadOnlyList<TaskItem>> StartWaveAsync(Session session, TeamImplementPlan plan,
        TeamWaveTrigger trigger)
    {
        var gate = _waveLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await StartWaveCoreAsync(session, plan, trigger); }
        finally { gate.Release(); }
    }

    // Тело раздачи БЕЗ лока: зовётся из StartWaveAsync (взял лок) и из закрытия волны
    // (оно уже держит тот же лок — SemaphoreSlim не реентрантен, повторный захват = дедлок).
    private async Task<IReadOnlyList<TaskItem>> StartWaveCoreAsync(Session session, TeamImplementPlan plan,
        TeamWaveTrigger trigger)
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
            if (t.Stopped) return (Reserved: false, Stopped: true, Exceeded: (string?)null, Held: (string?)null, Gate: false, ClosedWave: 0);
            // M2: волна не стартует поверх стадий, которые ждут человека. Окно clarify→vN+1:
            // координатор объявил тупик (версия плана ещё та же, гард версий пропускает), а
            // доехавшие задачи волны запускали следующую — стадия затиралась в Wave, интервью
            // сбито. То же с «ждёт решения»: конвейер, идущий поверх нерешённой карточки,
            // и есть «практика ждёт решения», которая на самом деле не ждёт.
            if (t.Stage is TeamImplementStage.Interview or TeamImplementStage.AwaitingDecision)
                return (false, false, null, $"практика в стадии «{t.Stage.ToWireToken()}» и ждёт человека", false, 0);
            // Э8: волна идёт ТОЛЬКО по подтверждённой последней версии плана. Два случая:
            // план устарел (после интервью опубликован vN+1 — старый доигрывать нельзя) либо
            // новая версия ещё не подтверждена человеком (авто-волны смену плана не покрывают).
            // Нули — состояние или карточка из до-Э8: гард выключен, прежнее поведение цело.
            if (t.PlanVersion > 0 && plan.Version < t.PlanVersion)
                return (false, false, null,
                    $"план версии {plan.Version} устарел: актуальна версия {t.PlanVersion}", false, 0);
            if (t.ApprovedPlanVersion > 0 && plan.Version > t.ApprovedPlanVersion)
                return (false, false, null,
                    $"версия плана {plan.Version} ещё не подтверждена человеком", false, 0);
            if (t.Budget.ExceededReasonForWave(subtasks.Count) is { } reason)
                return (Reserved: false, Stopped: false, Exceeded: reason, Held: (string?)null, Gate: false, ClosedWave: 0);
            // D1 (ревью 2026-08-17): докрут конвейера по состоянию при снятых авто-волнах —
            // не раздача, а гейт-карточка: следующая волна идёт только по явной кнопке
            // человека. Явная кнопка (runNext/addBudget/resume, подтверждение плана) сюда
            // не попадает: у неё trigger=UserCommand. Бюджет проверен выше — его карточка
            // информативнее, чем гейт, за которым сразу последовал бы отказ по квоте.
            if (trigger == TeamWaveTrigger.StateCatchUp && !t.AutoWaves)
                return (Reserved: false, Stopped: false, Exceeded: null, Held: null, Gate: true, t.ClosedWave);

            t.Budget.TasksUsed += subtasks.Count;
            t.Budget.RunsUsed += subtasks.Count;
            t.Budget.WavesUsed++;
            t.WaveNumber = wave;
            if (t.PlannedWaves < plan.WaveCount) t.PlannedWaves = plan.WaveCount;
            t.Stage = TeamImplementStage.Wave;
            // Отсечки для сторожа зависших волн (Э4): волна идёт, пока поля не обнулены
            t.WaveStartedAt = DateTime.UtcNow;
            t.WaveActivityAt = DateTime.UtcNow;
            return (Reserved: true, Stopped: false, Exceeded: null, Held: (string?)null, Gate: false, ClosedWave: 0);
        });
        if (gate.Stopped)
        {
            _log.LogInformation("Волна {Wave} плана {PlanId} не стартовала: практика остановлена человеком", wave, plan.Id);
            return [];
        }
        // Волна придержана: устаревшая/неподтверждённая версия плана (Э8) либо стадия, ждущая
        // человека (M2). Молчим намеренно — карточка (плана, уточнений или остановки) уже висит
        // в ленте и ждёт его ответа, вторая карточка про то же только мешала бы.
        if (gate.Held is { } held)
        {
            _log.LogInformation("Волна {Wave} плана {PlanId} не стартовала: {Reason}", wave, plan.Id, held);
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
        // Гейт авто-волн (D1): снятые авто-волны + докрут по состоянию — вместо раздачи
        // карточка «Волна N закрыта. Запустить следующую?», той же сборкой, что у закрытия
        // волны. Публикация уводит практику в «ждёт решения» — работу вернёт кнопка
        // «Запустить» на самой карточке (trigger=UserCommand, повторного гейта не будет).
        if (gate.Gate)
        {
            await RaiseWaveGateAsync(session, plan, gate.ClosedWave, wave);
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
                // Дерево штаба — источником правды полем: чат-исполнитель стартует прямо в нём
                // (TaskExecutionService), подсказка в описании остаётся человеку и модели
                WorktreePath: session.WorktreePath,
                WorktreeBranch: session.WorktreeBranch,
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
        if (_exec is not null) _ = Task.Run(() => LaunchAllAsync(session, created));
        return created;
    }

    // Пакетный старт исполнителей волны: провал одного не отменяет остальных —
    // такая задача остаётся в Todo и подхватывается перевыдачей координатора (Э4).
    // Молчаливым провал быть не должен (правило спеки «молчаливых пауз не бывает»): раньше
    // непойманный старт уходил только в лог, а человек видел лишь исчезнувшую задачу — так и
    // выглядел B3 приёмки (исполнитель упал на лимите провайдера ещё до первого хода).
    private async Task LaunchAllAsync(Session session, IReadOnlyList<TaskItem> tasks)
    {
        foreach (var task in tasks)
        {
            try { await _exec!.ExecuteAsync(task, auto: true); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Запуск исполнителя по задаче {TaskId} «{Title}» не удался", task.Id, task.Title);
                await RaiseLaunchFailedAsync(session, task, ex);
            }
        }
    }

    // Карточка в ленту штаба: исполнитель не стартовал вовсе (модель недоступна, лимит
    // провайдера, задача удалена). Kind — TaskFailed: для человека это ровно тот же случай
    // «работа по под-задаче не идёт», и кнопки карточки те же.
    internal async Task RaiseLaunchFailedAsync(Session session, TaskItem task, Exception ex)
    {
        var wave = session.TeamImplement?.WaveNumber ?? 0;
        try
        {
            await RaiseEscalationAsync(session, new TeamEscalation
            {
                Kind = TeamEscalationKind.TaskFailed,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.TaskFailed, task.Title),
                Details = $"Исполнитель по под-задаче «{task.Title}» не запустился: {ex.Message}.\n\n"
                          + "Работа по ней не идёт. Проверьте модель исполнителя и доступность провайдера, "
                          + "затем перевыдайте задачу.",
                TaskId = task.Id,
                Wave = wave,
                Actions = TeamEscalationActions.For(TeamEscalationKind.TaskFailed),
            });
        }
        catch (Exception publishEx)
        {
            // Карточка — страховка, а не критичный путь: её провал не должен ронять раздачу
            _log.LogError(publishEx, "Карточка о несостоявшемся запуске задачи {TaskId} не опубликована", task.Id);
        }
    }

    // Кнопки skip (TaskFailed)/drop (Blocker) карточки эскалации (Minor, волна 3): под-задача
    // помечается Done с пояснением — тем же путём, что и обычный доклад исполнителя
    // (TaskManager.TaskCompleted → OnTaskDone → CloseWaveIfDoneAsync), иначе волна не могла
    // закрыться до ручного tasks_complete, а «Пропустить»/«Снять» на карточке ничего не делали.
    internal async Task DropSubtaskAsync(string taskId, string reason)
    {
        var task = _tasks.GetById(taskId);
        if (task is null) return;
        var updated = _tasks.Update(taskId, new UpdateTaskRequest(
            Status: TaskItemStatus.Done,
            ResultMarkdown: reason));
        if (updated is null) return;
        if (updated.OwnerId is { } ownerId) await _hub.BroadcastTaskChangedAsync(ownerId, "updated", updated);
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
        string? summaryNote = null;
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

            // M2: закрытие волны — факт (работа доделана), а вот двигать конвейер дальше
            // поверх стадий, ждущих человека, нельзя: следующая волна затирала бы интервью,
            // гейт-карточка и «проверка» подменяли бы стадию, в которой человек как раз
            // отвечает. Дождёмся его — он вернёт практику в работу (кнопкой или сообщением).
            var waitsHuman = _sessions.WithTeamState(session.Id,
                t => t.Stage is TeamImplementStage.Interview or TeamImplementStage.AwaitingDecision) is true;
            if (waitsHuman)
                _log.LogInformation("Волна {Wave} чата-штаба {SessionId} закрыта, но конвейер стоит: " +
                    "практика ждёт человека", wave, session.Id);

            var started = false;
            if (waitsHuman)
            {
                // Ничего не двигаем: ни следующей волны, ни гейт-карточки, ни «проверки»
            }
            else if (hasNext && !team.Stopped && team.AutoWaves)
            {
                // Авто: следующая волна идёт сама. Бюджет проверяет раздача — она же
                // поднимет карточку исчерпания, если квота кончилась. Зовём Core: лок волны
                // уже наш, повторный захват семафора здесь означал бы дедлок. Повод
                // UserCommand: авто-волны уже проверены здесь, гейта в раздаче не нужно.
                started = (await StartWaveCoreAsync(session, plan, TeamWaveTrigger.UserCommand)).Count > 0;
            }
            else if (hasNext && !team.Stopped)
            {
                await RaiseWaveGateAsync(session, plan, wave, nextWave!.Value);
            }
            else if (!hasNext)
            {
                // Волны кончились — стадия проверки: сборка/тесты и итоговый отчёт
                _sessions.WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Checking; return true; });
                await _sessions.SaveTeamImplementStateAsync(session.Id);
            }

            // Сводку волны публикует координатор — ему и уходит ход с фактами закрытия
            summaryTurn = TeamImplementPrompts.WaveClosedTurn(wave, current.Count, current.Count, started, nextWave);
            summaryNote = $"Волна {wave} закрыта — сводка передана координатору";
        }
        finally { gate.Release(); }

        if (summaryTurn is not null)
            await _sessions.SendOrEnqueueAsync(session.Id, summaryTurn,
                senderPersonaId: null, silent: true, suppressTasksExecute: true,
                staffNote: summaryNote);
    }

    // Гейт-карточка следующей волны при снятых авто-волнах: единая сборка для закрытия
    // волны (CloseWaveIfDoneAsync) и докрута по состоянию (StartWaveCoreAsync с trigger=
    // StateCatchUp, D1 ревью 2026-08-17) — текст и кнопки одни, где бы волна ни ждала
    // решения человека. «Запустить» на карточке идёт trigger=UserCommand и гейт не повторяет.
    // Дедуп (D4, приёмка круга 2): каждое сообщение человека при висящем гейте снова зовёт
    // докрут по состоянию — без проверки в ленте росли одинаковые открытые гейты (каждый со
    // своим уведомлением, push и счётчиком напоминаний). Открытый гейт той же закрытой волны
    // уже ждёт ответа: карточку не дублируем, но стадию, как и публикация, возвращаем
    // в «ждёт решения» — иначе докрут оставил бы практику «в волне» без идущей волны.
    private async Task RaiseWaveGateAsync(Session session, TeamImplementPlan plan, int closedWave, int nextWave)
    {
        if ((await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            .Any(c => c.Kind == TeamEscalationKind.WaveGate && c.Wave == closedWave))
        {
            _sessions.WithTeamState(session.Id, t =>
            {
                if (t.Stage != TeamImplementStage.AwaitingDecision)
                    t.StageBeforeDecision = t.Stage;
                t.Stage = TeamImplementStage.AwaitingDecision;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            await _sessions.SaveTeamImplementStateAsync(session.Id);
            return;
        }
        await RaiseEscalationAsync(session, new TeamEscalation
        {
            Kind = TeamEscalationKind.WaveGate,
            Title = $"Волна {closedWave} закрыта. Запустить волну {nextWave}?",
            Details = $"Закрыто под-задач: {plan.Subtasks.Count(s => s.Wave == closedWave && s.TaskId is not null)}. " +
                      $"Осталось волн по плану: {plan.Subtasks.Where(s => s.TaskId is null).Select(s => s.Wave).Distinct().Count()}. " +
                      "Авто-волны сняты, поэтому следующая ждёт вашего решения.",
            Wave = closedWave,
            Actions = TeamEscalationActions.For(TeamEscalationKind.WaveGate),
        });
    }

    // Провал хода исполнителя (хук TaskExecutionService.TeamTaskFailed): одна перевыдача
    // с учётом причины, второй провал той же под-задачи — эскалация человеку.
    internal async Task OnTaskFailedAsync(TaskItem task)
    {
        var (session, team, plan) = await ResolveContextAsync(task);
        if (session is null || team is null || plan is null) return;
        var decision = await DecideReissueAsync(session, task);
        if (decision is not { Allowed: true, Subtask: { } subtask }) return;
        await LaunchReissueAsync(task, subtask);
    }

    // Решение «перевыдача или человек» с расходом бюджета — ОДНА точка для провала хода
    // (OnTaskFailedAsync) и кнопки человека (RestartWaveTaskAsync/RestartWaveAsync, этап 3):
    // потолок Attempts под-задачи и RetriesUsed/RunsUsed бюджета общие, отдельных счётчиков
    // для человека не заводим. Отказ поднимает карточку эскалации — как при провале хода:
    // для человека это один и тот же случай «работа по под-задаче не идёт», и кнопки те же.
    internal sealed record ReissueDecision(bool Allowed, string? Refusal, TeamImplementSubtask? Subtask);

    private async Task<ReissueDecision> DecideReissueAsync(Session session, TaskItem task)
    {
        if (session.TeamImplement is not { PlanCardId: { } planId } team)
            return new ReissueDecision(false, "под-задача не принадлежит плану практики", null);

        // Гонка ревью этапа 3: задача закрылась (или её удалили), пока вызов добирался
        // сюда — остановка исполнителя ждёт до 10 с, и закрывшаяся за это время задача
        // успевала получить «Повторная попытку» в описание и впустую списать Attempts
        // и бюджет. Отказ без карточки эскалации: закрытая задача — не проблема.
        var current = _tasks.GetById(task.Id);
        if (current is null || current.Status == TaskItemStatus.Done)
            return new ReissueDecision(false, "задача уже закрыта", null);

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
            var plan = await _sessions.GetTeamPlanAsync(session.Id, planId)
                       ?? throw new InvalidOperationException("План практики не найден");
            subtask = plan.Subtasks.FirstOrDefault(s => s.TaskId == task.Id)
                      ?? throw new InvalidOperationException("Под-задача не найдена в плане практики");

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

        if (retry) return new ReissueDecision(true, null, subtask);

        var refusal = subtask.Attempts >= 2
            ? "под-задача провалилась дважды — перевыдача не помогла"
            : team.Stopped ? "практика остановлена человеком" : team.Budget.ExceededReason() ?? "исчерпан потолок перевыдач";
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
        return new ReissueDecision(false, refusal, subtask);
    }

    // Запуск перевыдачи: причина прошлого запуска дописывается в описание, исполнитель
    // стартует заново. humanNote — пояснение вместо причины провала, когда человек
    // перезапускает ЗАВИСШУЮ задачу (провала хода у неё не было). Ошибка запуска
    // возвращается текстом: фоновый хук её только логирует, а человеку она нужна видимой.
    private async Task<(bool Ok, string? Error)> LaunchReissueAsync(
        TaskItem task, TeamImplementSubtask subtask, string? humanNote = null)
    {
        // Гонка ревью этапа 3: задача закрылась, пока запуск добирался сюда (решение о
        // перевыдаче и запись плана — свои await'ы). Готовую работу не трогаем: ни
        // «Повторная попытка» в описании, ни запуск исполнителя поверх закрытого.
        var live = _tasks.GetById(task.Id);
        if (live is null || live.Status == TaskItemStatus.Done)
            return (false, "задача уже закрыта");
        task = live;
        var reason = humanNote ?? (string.IsNullOrWhiteSpace(task.ClaudeResult)
            ? "ход завершился ошибкой" : task.ClaudeResult);
        var updated = _tasks.Update(task.Id, new UpdateTaskRequest(
            Description: task.Description + "\n\n## Повторная попытка\n" +
                $"Прошлый запуск не довёл задачу до конца ({reason}). Разберись, что пошло не так, " +
                "и доведи работу до фактической проверки — «почти готово» не считается."));
        if (updated is null) return (false, "задача удалена");
        await _hub.BroadcastTaskChangedAsync(updated.OwnerId!, "updated", updated);

        if (_exec is null) return (true, null);
        try
        {
            await _exec.ExecuteAsync(updated, auto: true);
            _log.LogInformation("Под-задача {TaskId} «{Title}» перевыдана (попытка {Attempt})",
                updated.Id, updated.Title, subtask.Attempts);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Перевыдача под-задачи {TaskId} не удалась", updated.Id);
            return (false, ex.Message);
        }
    }

    // --- КР-наблюдаемость, этап 3: перезапуск задачи/волны человеком ---

    // Гейт повторных вызовов: перезапуск многошаговый (стоп исполнения → решение о
    // перевыдаче → запуск), второй параллельный вызов плодил бы дубли. Ключи —
    // "task:{taskId}" и "wave:{sessionId}".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _restarts = new();

    // Тестовый стоп-кран: если установлен, перезапуск паркуется сразу после захвата гейта —
    // детерминированная проверка отказа повторного клика. В проде всегда null.
    internal TaskCompletionSource? TestHoldRestart;

    private async Task<bool> BeginRestartAsync(string key)
    {
        if (!_restarts.TryAdd(key, 0)) return false;
        if (TestHoldRestart is { } hold) await hold.Task;
        return true;
    }

    // Виден ли идущий перезапуск с этим ключом (тесты ждут true перед вторым вызовом)
    internal bool RestartInFlight(string key) => _restarts.ContainsKey(key);

    private void EndRestart(string key) => _restarts.TryRemove(key, out _);

    // Тишина задачи: время с последней активности — обновления задачи либо хода её чата
    // (живой прогон сюда не попадает: пока он идёт, гейт перезапуска отказывает раньше).
    private TimeSpan TaskQuiet(TaskItem task)
    {
        var last = task.UpdatedAt;
        if (task.LinkedSessionId is { } sid && _sessions.GetById(sid) is { } s && s.UpdatedAt > last)
            last = s.UpdatedAt;
        return DateTime.UtcNow - last;
    }

    // Занят ли чат-исполнитель задачи (статус хода/карточки; «занят» шире «живого прогона»:
    // зависший Working без процесса тоже занят — и его надо реанимировать перед перевыдачей)
    private bool IsExecutorBusy(TaskItem task) =>
        task.LinkedSessionId is { } sid
        && _sessions.GetById(sid) is { Status: SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting };

    // Занят ли исполнитель задачи по id: задачу могли удалить за окно ожидания останова
    // (NRE ревью этапа 3) — удалённая задача занятым исполнителем не считается
    internal bool ExecutorBusyById(string taskId) =>
        _tasks.GetById(taskId) is { } t && IsExecutorBusy(t);

    // Дождаться, пока чат-исполнитель перестанет числиться занятым после Interrupt:
    // статус убирается асинхронно (реанимация зависшего / финализация убитого прогона),
    // а перевыдача сразу после стопа упиралась бы в гейт «по задаче уже работает сессия».
    // Не дождались — не страшно: ExecuteAsync честно откажет своим текстом, он уйдёт человеку.
    private static async Task WaitExecutorIdleAsync(Func<bool> busy, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (busy())
        {
            if (DateTime.UtcNow >= deadline) return;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    // Перезапуск одной под-задачи — строка задачи в поповере (этап 3). Тот же путь перевыдачи,
    // что у провала хода. Гейт: задача завершена → отказ; живое исполнение с недавней
    // активностью → отказ с объяснением; провалена, не стартовала или молчит дольше
    // StalledMinutes → перевыдача (зависшее исполнение перед этим останавливаем).
    public sealed record TaskRestartResult(string Outcome, string Message);

    public async Task<TaskRestartResult> RestartWaveTaskAsync(Session session, string taskId)
    {
        if (session.TeamImplement is null)
            throw new InvalidOperationException("Режим «Командная реализация» выключен");
        var task = _tasks.GetById(taskId)
                   ?? throw new InvalidOperationException("Задача не найдена");
        if (task.SourceSessionId != session.Id)
            throw new InvalidOperationException("Задача не относится к этой практике");
        if (task.Status == TaskItemStatus.Done)
            throw new InvalidOperationException("Задача уже завершена — перезапуск не нужен");
        if (!await BeginRestartAsync("task:" + taskId))
            throw new InvalidOperationException("Задача уже перезапускается — дождитесь результата");
        try
        {
            // Живой прогон CLI (или ход, вот-вот стартующий) с недавней активностью — это
            // работа, а не зависание: тот же порог тишины, что у пульса волны
            if (task.LinkedSessionId is { } sid && _sessions.HasLiveTurnProcess(sid))
            {
                var quiet = TaskQuiet(task);
                if (quiet < _stalledThreshold)
                    throw new InvalidOperationException(
                        $"Задачу выполняет исполнитель — {(int)quiet.TotalMinutes} мин назад была активность. " +
                        "Дождитесь результата или остановите его чат кнопкой «Стоп»");
            }

            // Исполнение, числящееся занятым, прибираем: зависший статус реанимируется,
            // живой зависший прогон убивается — иначе перевыдача упрётся в гейт задачи
            if (IsExecutorBusy(task) && task.LinkedSessionId is { } linkedId)
            {
                _sessions.Interrupt(linkedId);
                await WaitExecutorIdleAsync(() => IsExecutorBusy(task), TimeSpan.FromSeconds(10));
            }

            // Перепроверка перед перевыдачей (гонка ревью этапа 3): исполнитель мог
            // закрыть задачу, пока мы его останавливали (окно до 10 с). Отказ тем же
            // текстом, что и входной гейт, — без расхода Attempts и бюджета
            var recheck = _tasks.GetById(taskId);
            if (recheck is null || recheck.Status == TaskItemStatus.Done)
                throw new InvalidOperationException("Задача уже завершена — перезапуск не нужен");

            var decision = await DecideReissueAsync(session, recheck);
            if (decision is not { Allowed: true, Subtask: { } subtask })
                return new TaskRestartResult("escalated",
                    $"Перевыдача не разрешена: {decision.Refusal}. Карточка с решением — в ленте штаба");
            var launch = await LaunchReissueAsync(task, subtask,
                humanNote: task.ClaudeResult == "error"
                    ? null : "исполнение не подавало признаков жизни и было остановлено");
            return launch.Ok
                ? new TaskRestartResult("reissued",
                    $"Задача перевыдана (попытка {subtask.Attempts}) — исполнитель стартует заново")
                : new TaskRestartResult("failed", $"Перевыдача не запустилась: {launch.Error}");
        }
        finally { EndRestart("task:" + taskId); }
    }

    // Перезапуск волны при зависании (этап 3): пере-раздача НЕсделанного — под-задачи в Done
    // не трогаем ни при каких условиях. Живые исполнения не перезаписываем молча: предупреждаем
    // списком и ждём подтверждения (confirm). Решение и расход — тем же путём, что перевыдача
    // после провала: потолок Attempts и бюджета общий.
    public sealed record WaveRestartResult(
        bool RequiresConfirm, IReadOnlyList<string> LiveTasks,
        int Reissued, int Escalated, int Failed, string Message);

    public async Task<WaveRestartResult> RestartWaveAsync(Session session, bool confirm)
    {
        if (session.TeamImplement is not { WaveNumber: > 0, PlanCardId: { } planId } team)
            throw new InvalidOperationException("Волна не идёт — перезапускать нечего");
        // Снапшот-гейт как у поповера: стадия обязана быть Wave/Checking, иначе кнопки нет
        if (BuildWaveSnapshot(session) is not { } snap)
            throw new InvalidOperationException("Волна не идёт — перезапускать нечего");
        if (!await BeginRestartAsync("wave:" + session.Id))
            throw new InvalidOperationException("Волна уже перезапускается — дождитесь результата");
        try
        {
            var plan = await _sessions.GetTeamPlanAsync(session.Id, planId)
                       ?? throw new InvalidOperationException("План практики не найден");

            // Несделанное текущей волны: розданные под-задачи не в Done
            var undone = plan.Subtasks
                .Where(s => s.Wave == team.WaveNumber && s.TaskId is not null && !IsDone(s.TaskId!))
                .ToList();
            if (undone.Count == 0)
                return new WaveRestartResult(false, [], 0, 0, 0,
                    "Незакрытых задач в волне нет — перезапускать нечего");

            // Живые исполнения: чат-исполнитель числится занятым. Перезапуск остановит их —
            // предупреждаем и ждём подтверждения. Живая на вид волна (пульс alive/quiet)
            // без подтверждения тоже не перезапускается: состояние могло измениться после
            // открытия поповера
            var live = undone
                .Where(s => _tasks.GetById(s.TaskId!) is { } t && IsExecutorBusy(t))
                .Select(s => s.Title).ToList();
            if (!confirm && (live.Count > 0 || snap.Liveness is WaveLiveness.Alive or WaveLiveness.Quiet))
            {
                var who = live.Count > 0
                    ? "Ещё работают исполнения: " + string.Join(", ", live.Select(t => $"«{t}»")) + ". "
                    : "Волна выглядит живой — возможно, работа идёт. ";
                return new WaveRestartResult(true, live, 0, 0, 0,
                    who + "Перезапуск остановит их и выдаст незакрытые задачи заново. Закрытые задачи не пострадают");
            }

            // Подтверждено (или зависло без живых): занятые исполнения прибираем, затем
            // перевыдаём каждую незакрытую под-задачу
            foreach (var s in undone)
            {
                if (_tasks.GetById(s.TaskId!) is not { } t || !IsExecutorBusy(t)) continue;
                _sessions.Interrupt(t.LinkedSessionId!);
                await WaitExecutorIdleAsync(() => ExecutorBusyById(t.Id), TimeSpan.FromSeconds(10));
            }

            var reissued = 0;
            var escalated = 0;
            var failed = 0;
            foreach (var s in undone)
            {
                // Перечитываем и пропуск закрывшегося (гонка ревью этапа 3): остановка
                // соседних исполнителей ждёт до 10 с на каждого, и закрывшаяся за это
                // время задача не должна получать ни «Повторную попытку», ни расход
                // Attempts/бюджета, ни счётчик эскалаций
                var task = _tasks.GetById(s.TaskId!);
                if (task is null || task.Status == TaskItemStatus.Done) continue;
                var decision = await DecideReissueAsync(session, task);
                if (decision is not { Allowed: true, Subtask: { } subtask }) { escalated++; continue; }
                var launch = await LaunchReissueAsync(task, subtask,
                    humanNote: task.ClaudeResult == "error"
                        ? null : "исполнение не подавало признаков жизни и было остановлено");
                if (launch.Ok) reissued++; else failed++;
            }

            // Перезапуск — движение волны: сторож зависаний отсчитывает срок заново
            _sessions.WithTeamState(session.Id, t =>
            {
                if (t.WaveStartedAt is not null) t.WaveActivityAt = DateTime.UtcNow;
                return true;
            });

            var parts = new List<string> { $"перевыдано задач: {reissued}" };
            if (escalated > 0) parts.Add($"перевыдача не разрешена: {escalated} (карточки — в ленте штаба)");
            if (failed > 0) parts.Add($"не запустилось: {failed}");
            return new WaveRestartResult(false, [], reissued, escalated, failed,
                "Волна перезапущена — " + string.Join(", ", parts) + ". Закрытые задачи не тронуты");
        }
        finally { EndRestart("wave:" + session.Id); }
    }

    // Сторож зависших волн (TeamWaveWatchdog, тик раз в минуту): волна молчит дольше
    // таймаута — человек получает карточку, а не бесконечное «идёт волна 2».
    public async Task CheckStalledWavesAsync()
    {
        foreach (var session in _sessions.GetTeamImplementSessions())
        {
            if (session.TeamImplement is not { } team) continue;
            if (team.Stage != TeamImplementStage.Wave) continue;
            // Мёртвая зона конвейера (прод 2026-08-17): Stage=Wave, но волна закрыта и
            // следующая не роздана (WaveStartedAt пуст) — прежний гард пропускал такое
            // молча, и конвейер стоял часами, пока человек не трогал его руками.
            if (team.WaveStartedAt is not { } startedAt)
            {
                await CheckDeadZoneStallAsync(session, team);
                continue;
            }
            // Отсчёт — от последней активности волны (закрытая задача, перевыдача), а не от
            // её старта: волна из пяти долгих задач живая, пока задачи закрываются одна
            // за другой, и эскалировать её только за длительность — ложная тревога.
            var lastActivity = team.WaveActivityAt ?? startedAt;
            if (DateTime.UtcNow - lastActivity < _waveTimeout) continue;

            var stalled = StalledWaveTasks(session, team.WaveNumber);
            await RaiseEscalationAsync(session, new TeamEscalation
            {
                Kind = TeamEscalationKind.WaveStalled,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.WaveStalled,
                    stalled.FirstOrDefault()?.Title ?? $"волна {team.WaveNumber}"),
                Details = $"Волна {team.WaveNumber} идёт дольше {_waveTimeout.TotalMinutes:0} минут и не закрывается. " +
                          (stalled.Count > 0
                              ? "Молчат задачи: " + string.Join(", ", stalled.Select(t => $"«{t.Title}»")) + "."
                              : "Незакрытых задач волны не видно — возможно, они удалены."),
                // D3 (приёмка круга 2): «Снять» работает только с TaskId, а он честен лишь когда
                // молчит РОВНО ОДНА под-задача — иначе кнопка сняла бы первую попавшую, а не ту,
                // что выбрал человек. При нескольких (или ни одной) молчащих берём набор без «Снять».
                TaskId = stalled.Count == 1 ? stalled[0].Id : null,
                Wave = team.WaveNumber,
                Actions = stalled.Count == 1
                    ? TeamEscalationActions.For(TeamEscalationKind.WaveStalled)
                    : TeamEscalationActions.WithoutDrop(),
            });
            _log.LogWarning("Волна {Wave} чата-штаба {SessionId} не двигается дольше таймаута",
                team.WaveNumber, session.Id);
        }
    }

    // Страховка мёртвой зоны конвейера: волна закрыта (ClosedWave == WaveNumber), в плане
    // есть нерозданные под-задачи, а WaveStartedAt пуст — раздачу следующей никто не позвал
    // (любой БУДУЩИЙ путь в такое состояние, не только прод-инцидент 17.08). Отсчёт — от
    // UpdatedAt чата: в мёртвой зоне это последний факт в нём (решение по карточке, конец
    // хода координатора). Карточка WaveStalled переводит практику в «ждёт решения», поэтому
    // повторных карточек не будет — ровно одна. Остановлена человеком практика — не мёртвая
    // зона, стойкость там выбрана осознанно.
    // Состояние перечитывается под локом волны непосредственно перед публикацией (Minor,
    // ревью 2026-08-17): между дешёвой проверкой выше и карточкой параллельная раздача
    // успевает выставить Stage=Wave с отсечками и стартовать исполнителей — публикация
    // перетёрла бы это на «ждёт решения», и волна шла бы, чего бэкенд уже не знает.
    private async Task CheckDeadZoneStallAsync(Session session, SessionTeamImplement team)
    {
        if (team.WaveNumber == 0 || team.ClosedWave != team.WaveNumber || team.Stopped) return;
        if (team.PlanCardId is not { } planId) return;
        if (DateTime.UtcNow - session.UpdatedAt < _deadZoneTimeout) return;

        var gate = _waveLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var fresh = _sessions.WithTeamState(session.Id,
                t => (Stage: t.Stage, StartedAt: t.WaveStartedAt, ClosedWave: t.ClosedWave, Stopped: t.Stopped));
            if (fresh.Stage != TeamImplementStage.Wave || fresh.StartedAt is not null
                || fresh.ClosedWave != team.WaveNumber || fresh.Stopped)
                return;
            var plan = await _sessions.GetTeamPlanAsync(session.Id, planId);
            if (plan is null || !plan.Subtasks.Any(s => s.TaskId is null)) return;

            await RaiseEscalationAsync(session, new TeamEscalation
            {
                Kind = TeamEscalationKind.WaveStalled,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.WaveStalled,
                    $"волна {team.WaveNumber} закрыта, следующая не роздана"),
                Details = $"Волна {team.WaveNumber} закрыта, но следующая не была роздана дольше " +
                          $"{_deadZoneTimeout.TotalMinutes:0} минут — конвейер стоит без работы. " +
                          "Проверьте состояние практики: возможно, раздача не стартовала.",
                Wave = team.WaveNumber,
                // D2 (ревью 2026-08-17): у карточки мёртвой зоны свой набор кнопок, БЕЗ «Снять»:
                // TaskId у неё нет, а блок раздачи по состоянию ниже ветки drop в SessionManager
                // запускал бы следующую волну — подпись кнопки врала о последствии.
                Actions = TeamEscalationActions.WithoutDrop(),
            });
            _log.LogWarning("Мёртвая зона конвейера чата-штаба {SessionId}: волна {Wave} закрыта, " +
                "следующая не роздана дольше таймаута", session.Id, team.WaveNumber);
        }
        finally { gate.Release(); }
    }

    // Незакрытые задачи текущей волны — для карточки зависания: заголовки в текст и
    // TaskId кнопке «Снять» (когда молчит ровно одна, D3)
    private List<TaskItem> StalledWaveTasks(Session session, int wave) =>
        [.. WaveTasks(session, wave).Where(t => t.Status != TaskItemStatus.Done)];

    // --- Пульс волны (КР-наблюдаемость, этап 1) ---

    // Снимок состояния волны: поля пульса (TeamWavePulseMessage) + задачи и пороги
    // для REST-снапшота поповера. Собирается ОДНИМ методом для обоих потребителей —
    // живой пульс и REST обязаны показывать одно и то же.
    internal sealed record WaveSnapshot(
        TeamImplementStage Stage,
        int WaveNumber,
        int PlannedWaves,
        int TasksActive,
        int TasksTotal,
        DateTime LastActivityAt,
        long QuietSeconds,
        WaveLiveness Liveness,
        IReadOnlyList<TaskItem> WaveTasks,
        int QuietMinutes,
        int StalledMinutes);

    // Составить снапшот текущей волны. null — пульса нет: режим выключен либо стадия
    // не в работе (Wave/Checking) — интервью и «ждёт решения» человек видит карточками,
    // пульс там добавил бы шум. Дочерние чаты-исполнители собирает вызывающий: тику
    // дешевле один проход по всем чатам на весь тик (SendWavePulsesAsync), одиночному
    // вызову (REST-снапшот) дешевле достать их самому.
    internal WaveSnapshot? BuildWaveSnapshot(Session session) =>
        BuildWaveSnapshot(session, _sessions.GetAll().Where(s => s.ParentSessionId == session.Id));

    internal WaveSnapshot? BuildWaveSnapshot(Session session, IEnumerable<Session> childSessions)
    {
        if (session.TeamImplement is not { } team) return null;
        if (team.Stage is not (TeamImplementStage.Wave or TeamImplementStage.Checking)) return null;

        var children = childSessions as IReadOnlyList<Session> ?? childSessions.ToList();
        var waveTasks = WaveTasks(session, team.WaveNumber);
        var now = DateTime.UtcNow;
        // Последняя активность волны: старт/закрытие/перевыдача её задач (их UpdatedAt —
        // TaskManager.Update двигает) плюс ходы дочерних чатов-исполнителей (ParentSessionId
        // вычисляется из Task.SourceSessionId). Max по всем точкам: двигалась ЛЮБАЯ часть
        // волны — волна жива. Старт волны — нижняя граница: сразу после раздачи это самая
        // свежая точка (задачи создаются тем же моментом, но и без них якорь нужен).
        // Живой прогон CLI — активность «сейчас»: все UpdatedAt-якоря двигаются только на
        // границах ходов (ApplyStatusAsync), и пока ход идёт, они статичны — честная
        // 35-минутная сборка исполнителя или длинный ход финальной проверки (Checking,
        // WaveStartedAt уже обнулён) считались бы «зависло». Прогон может идти и в самом
        // штабе, и у ребёнка-исполнителя — смотрим обоих.
        var anchors = waveTasks.Select(t => t.UpdatedAt)
            .Concat(children.Select(s => s.UpdatedAt))
            .Append(team.WaveStartedAt ?? session.UpdatedAt);
        if (_sessions.HasLiveTurnProcess(session.Id) || children.Any(c => _sessions.HasLiveTurnProcess(c.Id)))
            anchors = anchors.Append(now);
        var lastActivityAt = anchors.Max();
        var quiet = now - lastActivityAt;

        var liveness = ClassifyLiveness(session, quiet);
        return new WaveSnapshot(
            Stage: team.Stage,
            WaveNumber: team.WaveNumber,
            PlannedWaves: team.PlannedWaves,
            TasksActive: waveTasks.Count(t => t.Status != TaskItemStatus.Done),
            TasksTotal: waveTasks.Count,
            LastActivityAt: lastActivityAt,
            QuietSeconds: Math.Max(0, (long)quiet.TotalSeconds),
            Liveness: liveness,
            WaveTasks: waveTasks.OrderBy(t => t.CreatedAt).ToList(),
            QuietMinutes: (int)_quietThreshold.TotalMinutes,
            StalledMinutes: (int)_stalledThreshold.TotalMinutes);
    }

    // Задачи волны (включая закрытые): по ним REST-поповер показывает прогресс
    // «2 из 5 закрыто», а пульс считает активные.
    private List<TaskItem> WaveTasks(Session session, int wave)
    {
        if (wave == 0) return [];
        var ownerId = session.OwnerId
            ?? (session.ProjectId is { } pid ? _projects.GetById(pid)?.OwnerId : null);
        IReadOnlyCollection<TaskItem> pool = session.ProjectId is { } projectId
            ? _tasks.GetByProject(projectId)
            : ownerId is not null ? _tasks.GetByOwner(ownerId) : [];
        return [.. pool.Where(t => t.SourceSessionId == session.Id && t.Labels.Contains($"волна {wave}"))];
    }

    // Классификация живости. dead приоритетнее тишины: «штаб Working без живого прогона»
    // не зависит от quietSeconds и значит большее — обвал, а не пауза.
    private WaveLiveness ClassifyLiveness(Session session, TimeSpan quiet) =>
        session.Status is SessionStatus.Working or SessionStatus.Waiting
            && !_sessions.HasLiveTurnProcess(session.Id)
            ? WaveLiveness.Dead
        : quiet > _stalledThreshold ? WaveLiveness.Stalled
        : quiet > _quietThreshold ? WaveLiveness.Quiet
        : WaveLiveness.Alive;

    internal static string LivenessToken(WaveLiveness liveness) => liveness switch
    {
        WaveLiveness.Quiet => "quiet",
        WaveLiveness.Dead => "dead",
        WaveLiveness.Stalled => "stalled",
        _ => "alive",
    };

    // Пульс всех живых волн (TeamWaveWatchdog, тот же тик раз в минуту): снапшот уходит
    // ТОЛЬКО в session-группу штаба — прямо в хаб, минуя историю и Session.UpdatedAt
    // (эфемерное событие, см. TeamWavePulseMessage). Порядок в тике: после проверки
    // зависаний — эскалация могла сменить стадию, и пульс должен честно замолчать.
    // Шлём каждый тик без дедупа: quietSeconds растёт ежеминутно, «изменения» есть
    // всегда, а дедуп — лишнее состояние; рассылка в одну пустую группу без зрителей
    // стоит копейки.
    public async Task SendWavePulsesAsync()
    {
        // Дети всех штабов — одним проходом на тик: собирать их внутри каждого снапшота
        // значило бы полный GetAll() на каждый штаб (O(N×M), растёт с числом чатов).
        // ParentSessionId вычисляемый, поэтому фильтруем по нему всех сразу.
        var childrenByParent = _sessions.GetAll()
            .Where(s => s.ParentSessionId is not null)
            .ToLookup(s => s.ParentSessionId!);
        foreach (var session in _sessions.GetTeamImplementSessions())
        {
            try
            {
                if (BuildWaveSnapshot(session, childrenByParent[session.Id]) is not { } snap) continue;
                var msg = new Protocol.TeamWavePulseMessage(
                    Stage: snap.Stage.ToWireToken(),
                    WaveNumber: snap.WaveNumber,
                    PlannedWaves: snap.PlannedWaves,
                    TasksActive: snap.TasksActive,
                    TasksTotal: snap.TasksTotal,
                    LastActivityAt: snap.LastActivityAt,
                    QuietSeconds: snap.QuietSeconds,
                    Liveness: LivenessToken(snap.Liveness))
                    with { SessionId = session.Id };
                await _hub.Clients.Group(session.Id).SendAsync("message", msg);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Пульс волны чата-штаба {SessionId} не отправлен", session.Id);
            }
        }
    }

    // --- Повторные напоминания о висящей карточке ---

    // Первое напоминание — через час после публикации карточки, второе — через четыре часа
    // после первого. Максимум два на карточку: навязчивость хуже пропущенного сигнала.
    private static readonly TimeSpan FirstReminderAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan NextReminderAfter = TimeSpan.FromHours(4);
    private const int MaxReminders = 2;

    // Сторож ожидающих карточек (TeamWaveWatchdog, тот же тик, что и у зависших волн):
    // открытая карточка без ответа дольше порога — повторить уведомление. Первый оклик
    // уходил один раз, и пропущенная ночью карточка оставляла практику стоять до утра —
    // «молчаливых пауз не бывает» и здесь. Счётчик напоминаний живёт на карточке в истории
    // и переживает рестарт сервера.
    public async Task CheckAwaitingEscalationsAsync()
    {
        foreach (var session in _sessions.GetTeamImplementSessions())
        {
            if (session.TeamImplement is not { } team) continue;
            // Человек прямо сейчас в этом чате — он и так видит карточку в ленте
            if (_sessions.HasViewers(session.Id)) continue;

            var ownerId = session.OwnerId
                ?? (session.ProjectId is { } pid ? _projects.GetById(pid)?.OwnerId : null);
            if (ownerId is null || _notif is null) continue;

            foreach (var card in await _sessions.GetOpenTeamEscalationsAsync(session.Id))
            {
                if (!AwaitsHuman(card, team)) continue;
                if (card.RemindersSent >= MaxReminders) continue;
                // Порог первого напоминания — от публикации карточки, второго — от первого
                var anchor = card.RemindersSent == 0 ? card.CreatedAt : card.LastReminderAt ?? card.CreatedAt;
                var threshold = card.RemindersSent == 0 ? FirstReminderAfter : NextReminderAfter;
                if (DateTime.UtcNow - anchor < threshold) continue;

                var hours = (int)Math.Floor((DateTime.UtcNow - card.CreatedAt).TotalHours);
                var authorId = card.PersonaId ?? team.CoordinatorPersonaId ?? session.PersonaId;
                // Push — по тем же правилам, что у первого уведомления (только когда человека
                // нет в чате); зрителей мы выше отсекли, поэтому здесь он всегда уходит
                await _notif.SendNotificationMessageAsync(ownerId, new Protocol.NotificationMessage(
                    Title: TeamImplementPrompts.ReminderTitle(PersonaName(authorId, ownerId)),
                    Body: TeamImplementPrompts.ReminderBody(card.Title, hours),
                    Url: ChatUrl(session),
                    Kind: "claude",
                    TaskId: card.TaskId,
                    ProjectId: session.ProjectId,
                    PersonaId: authorId,
                    Tag: "Командная реализация") { SessionId = session.Id }, sendPush: true);
                await _sessions.MarkTeamEscalationRemindedAsync(session.Id, card.Id);
                _log.LogInformation("Напоминание {Number} о карточке «{Title}» чата-штаба {SessionId} отправлено",
                    card.RemindersSent + 1, card.Title, session.Id);
            }
        }
    }

    // Карточка ждёт действия человека: не информационная (добавочная волна не ждёт клика)
    // и стадия режима всё ещё стоит в ожидании её ответа (тупик с уточнениями держит
    // интервью, остальные карточки — «ждёт решения»). Ответ сообщением возвращает стадию
    // в работу, НЕ гася карточку (ResumeTeamFromDecisionOnUserInput), и по такой
    // «карточке-призраку» будить нельзя.
    private static bool AwaitsHuman(TeamEscalation card, SessionTeamImplement team) =>
        !card.Kind.IsInformational()
        && (card.Kind != TeamEscalationKind.NeedsClarification
            ? team.Stage == TeamImplementStage.AwaitingDecision
            : team.Stage == TeamImplementStage.Interview);

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
        var authorId = escalation.PersonaId
            ?? session.TeamImplement?.CoordinatorPersonaId ?? session.PersonaId;
        // Заголовок от имени персоны (Э8) и по виду карточки: остановка, гейт волны и
        // вопросы различаются уже в списке уведомлений. Обезличенное «Команда ждёт…»
        // остаётся фолбэком, когда персоны у штаба нет.
        await _notif.SendNotificationMessageAsync(ownerId, new Protocol.NotificationMessage(
            Title: TeamImplementPrompts.WaitingTitle(PersonaName(authorId, ownerId), escalation.Kind),
            Body: escalation.Title,
            Url: ChatUrl(session),
            Kind: "claude",
            TaskId: escalation.TaskId,
            ProjectId: session.ProjectId,
            PersonaId: authorId,
            // SessionId — унаследованное init-свойство базы ServerMessage, не параметр
            // конструктора: задаётся инициализатором (см. NotificationService и др.)
            Tag: "Командная реализация") { SessionId = session.Id }, sendPush: away);
    }

    // Вопрос интервью ждёт человека (Э8): ASK-карточка в ленте штаба — та же ситуация, что
    // permission-запрос у исполнителя задачи, поэтому и обвязка та же (ср.
    // TaskExecutionService.BuildWaitingNotification): уведомление всегда, push — когда
    // человека нет в чате. Иначе интервью молча ждало бы ответа, а «молчаливых пауз не бывает».
    internal async Task OnStabQuestionAsync(Session session)
    {
        if (_notif is null) return;
        var ownerId = session.OwnerId
            ?? (session.ProjectId is { } pid ? _projects.GetById(pid)?.OwnerId : null);
        if (ownerId is null) return;

        var authorId = session.TeamImplement?.CoordinatorPersonaId ?? session.PersonaId;
        await _notif.SendNotificationMessageAsync(ownerId, new Protocol.NotificationMessage(
            // Вопрос интервью — тот же текст, что у карточки тупика с уточнениями
            Title: TeamImplementPrompts.WaitingTitle(PersonaName(authorId, ownerId),
                TeamEscalationKind.NeedsClarification),
            Body: TeamImplementPrompts.QuestionNotificationBody,
            Url: ChatUrl(session),
            Kind: "claude",
            ProjectId: session.ProjectId,
            PersonaId: authorId,
            Tag: "Командная реализация") { SessionId = session.Id }, sendPush: !_sessions.HasViewers(session.Id));
    }

    private static string ChatUrl(Session session) => string.IsNullOrEmpty(session.ProjectId)
        ? $"/chats/{session.Id}"
        : $"/project/{session.ProjectId}/chat/{session.Id}";

    // Имя персоны-автора для текста уведомления. null — персоны нет, реестр не передан или
    // персона удалена: текст деградирует до обезличенного.
    private string? PersonaName(string? personaId, string ownerId) =>
        personaId is not null && _personas.Get(personaId, ownerId) is { } p ? p.Name : null;

    private bool IsDone(string taskId) => _tasks.GetById(taskId)?.Status == TaskItemStatus.Done;
}
