using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Team;

// Хранитель состояния режима «Командная реализация» (этап 4, шаг 2г-3в, волна А
// плана выноса штаба, docs/research/session-core-split-2026-09.md, §5(4)).
//
// Сюда переехало минимальное ядро штабного состояния: транзакция над SessionTeamImplement,
// персист+бродкаст, бюджет итерации, проверка готовности к включению, корень файла плана
// и чтение плана из истории на диске. Блоки лежали в SessionManager вперемешку с ядром —
// ядру они не нужны (их звали только штабные хелперы), и единственный мотив держать их
// здесь был «вызывающий код пишет Session прямо». Теперь вызывающий код пишет Session
// через SessionManager (обёртки), а эти блоки сами работают с ITeamSessionDirectory и
// публичным API ядра (BroadcastAsync/ResolveOwnerId).
//
// Шов ITeamSessionDirectory.Persist() — единственный новый метод контракта, и он же
// закрывает потребность «персист каталога» из отчёта разведки: пять блоков штаба
// (включая будущие волны), которым нужно SaveSessions, идут через этот метод.
//
// Owning-паттерн (как TeamCoordinator/TeamWaveService): экземпляр создаётся в
// конструкторе SessionManager, а не через DI. Так разорван цикл «SessionManager хочет
// TeamStateService, TeamStateService хочет SessionManager» — вертикаль видит ядро по
// прямой ссылке, а ядро знает о вертикали через поле _teamState. Когда придёт шаг 2г-4,
// регистрация переедет в Program.cs, а owning-обёртки в SessionManager будут сняты.
//
// Лок штаба — на стороне вертикали (ConcurrentDictionary по sessionId), а не на entry.
// Так разорван цикл «нужен entry для lock → нужен шов → нельзя»: вертикаль держит
// собственный словарь блокировок, а само TeamImplement достаётся через публичный
// SessionManager.GetById(...).TeamImplement. Прежнее поле SessionEntry.TeamLock было
// удалено (этап 4, приём хода, 2026-09-07): за ненадобностью — до этой волны других
// держателей лока не было, и весь мутирующий код штаба проходит через WithTeamState.
internal sealed class TeamStateService
{
    private readonly SessionManager _sessions;
    private readonly ITeamSessionDirectory _dir;
    private readonly ITeamRunState _run;
    private readonly TeamPlanningService? _teamPlanning;
    private readonly IProjectManager _projects;
    private readonly IConfiguration? _config;
    private readonly LlmProviderRegistry _llmProviders;
    // Лок-словарь штаба по sessionId: единственный держатель блокировки для мутаций
    // SessionTeamImplement. Инициализируется по требованию, GC подберёт объект, когда
    // режим выключат и ссылок не останется.
    private readonly ConcurrentDictionary<string, object> _teamLocks = new();

    internal TeamStateService(SessionManager sessions, TeamPlanningService? teamPlanning,
        IProjectManager projects, IConfiguration? config, LlmProviderRegistry llmProviders)
    {
        _sessions = sessions;
        _dir = sessions;
        _run = sessions;
        _teamPlanning = teamPlanning;
        _projects = projects;
        _config = config;
        _llmProviders = llmProviders;
    }

    // Транзакция над состоянием режима: ЕДИНСТВЕННЫЙ способ править счётчики бюджета и
    // попытки под-задач. Точки записи разнесены по потокам (раздача волны из колбэка
    // завершения задачи, перевыдача из колбэка провала хода, квота из HTTP-фильтра), а
    // `int++` не атомарен — частичный лок означал бы потерянные инкременты и нечестный счёт
    // ровно там, ради чего Э4 и делался. Внутри — только синхронная работа с моделью.
    public T? WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate)
    {
        var info = _dir.Get(sessionId);
        if (info is null) return default;
        var lockObj = _teamLocks.GetOrAdd(sessionId, _ => new object());
        lock (lockObj)
        {
            var session = _sessions.GetById(sessionId);
            if (session?.TeamImplement is not { } team) return default;
            return mutate(team);
        }
    }

    // Рассылка TeamImplementMessage по группе чата: стадия, номер волны, состав команды,
    // бюджет и флаги. Тело взято один в один из прежней
    // SessionManager.BroadcastTeamImplementAsync.
    public async Task BroadcastTeamImplementAsync(string sessionId, Session session)
    {
        var ti = session.TeamImplement;
        // Потолки после расширения (волна 1 team-blocker-honest): плашка бюджета должна
        // показывать ЧЕСТНЫЕ новые лимиты, а не размер плана — текст обещал «потолок
        // поднимется до N», но старый N был размером плана (находка ревью c156193b).
        // Дельта считается из остатка и плана: Max + max(0, plan - left). План подтягиваем
        // здесь, а не в момент записи в карточку, чтобы число ехало в каждом WS-снимке —
        // иначе фронт показывал бы устаревшее значение до следующей правки бюджета.
        if (ti?.Budget is not null) await FillAfterBudgetAsync(sessionId, ti);
        await _sessions.BroadcastAsync(sessionId, new TeamImplementMessage(
            ti is not null,
            ti?.Stage.ToWireToken(),
            ti?.WaveNumber ?? 0,
            ti?.AutoWaves ?? true,
            ti?.CoordinatorPersonaId,
            ti?.PlannerPersonaId,
            ti?.ExecutorPersonaIds,
            ti?.Budget,
            ti?.PlanCardId,
            ti?.PlannedWaves ?? 0,
            ti?.CoordinatorNoCode ?? true,
            ti?.Stopped ?? false,
            ti?.SavedMode is not null,
            ti?.PlanVersion ?? 0));
    }

    // Считает MaxWavesAfter/MaxTasksAfter в Budget: 0 — плана нет (плашка скрывается).
    // Вызывается на каждом broadcast'е, чтобы дельта всегда была свежей — потолок могли
    // расширить через addBudget, а план с тех пор не менялся. Формула — PlanShortfall
    // (M1 фикс-волны 4 team-blocker-honest): единая точка с автоматическим расширением
    // в TeamDecisionService, без неё две ветки расходились (дефект c156193b).
    private async Task FillAfterBudgetAsync(string sessionId, SessionTeamImplement ti)
    {
        ti.Budget.MaxWavesAfter = 0;
        ti.Budget.MaxTasksAfter = 0;
        if (ti.PlanCardId is not { } planId) return;
        var plan = await GetTeamPlanFromHistoryAsync(sessionId, planId);
        if (plan is null) return;
        var plannedWaves = plan.Subtasks.Count == 0 ? 0 : plan.Subtasks.Max(s => s.Wave);
        var plannedTasks = plan.Subtasks.Count;
        ti.Budget.MaxWavesAfter = ti.Budget.MaxWaves
            + TeamImplementBudget.PlanShortfall(ti.Budget.MaxWaves, ti.Budget.WavesUsed, plannedWaves);
        ti.Budget.MaxTasksAfter = ti.Budget.MaxTasks
            + TeamImplementBudget.PlanShortfall(ti.Budget.MaxTasks, ti.Budget.TasksUsed, plannedTasks);
    }

    // Бюджет итерации из дефолтов плана с optional override из конфига TeamImplement:Max*
    public TeamImplementBudget NewTeamImplementBudget() => new()
    {
        MaxTasks = int.TryParse(_config?["TeamImplement:MaxTasks"], out var t) ? t : 12,
        MaxWaves = int.TryParse(_config?["TeamImplement:MaxWaves"], out var w) ? w : 4,
        MaxRuns = int.TryParse(_config?["TeamImplement:MaxRuns"], out var r) ? r : 20,
        MaxRetries = int.TryParse(_config?["TeamImplement:MaxRetries"], out var rt) ? rt : 3,
        MaxWakeups = int.TryParse(_config?["TeamImplement:MaxWakeups"], out var wu) ? wu : 10,
    };

    // Причина, по которой режим включать нельзя — до единого хода интервью (B2 приёмки).
    // Порядок проверок совпадает с CreateTeamPlanAsync: сначала координатор, затем состав.
    // null — включать можно. Состав проверяем по БУДУЩЕМУ состоянию (пробный объект), чтобы
    // не дублировать логику подбора — она живёт в TeamPlanningService.
    internal (string Code, string Message)? TeamImplementSetupError(Session session,
        string? coordinatorPersonaId, IReadOnlyCollection<string>? executorPersonaIds)
    {
        // Координатор = собеседник чата, если явно не выбран другой (см. ResolveCoordinator)
        var coordinatorId = coordinatorPersonaId ?? session.PersonaId;
        if (string.IsNullOrWhiteSpace(coordinatorId))
            return (TeamImplementSetupException.NoCoordinator,
                "Выберите координатора — чат без персоны штабом быть не может. "
                + "Назначьте собеседника чата или укажите координатора при включении режима.");

        var ownerId = _sessions.ResolveOwnerId(session);
        if (_teamPlanning is null || ownerId is null) return null;

        var probe = new Session
        {
            Id = session.Id,
            ProjectId = session.ProjectId,
            OwnerId = session.OwnerId,
            PersonaId = session.PersonaId,
            TeamImplement = new SessionTeamImplement
            {
                CoordinatorPersonaId = coordinatorPersonaId,
                ExecutorPersonaIds = executorPersonaIds?.ToList() ?? [],
            },
        };

        if (_teamPlanning.ResolveCoordinator(probe, ownerId) is null)
            return (TeamImplementSetupException.NoCoordinator,
                "Координатор не найден — выберите персону-собеседника чата, которая будет штабом.");

        if (_teamPlanning.ResolveCandidates(probe, ownerId).Count == 0)
            return (TeamImplementSetupException.NoExecutors, session.ProjectId is null
                ? "Выберите исполнителей — вне проекта команды нет, и подбирать не из кого"
                : "В команде проекта нет персон — выберите исполнителей явно");

        return null;
    }

    // Корень, куда «Командная реализация» пишет файл полного плана (Э8-доп., 2026-08-02):
    // worktree штаба, если он в нём работает, иначе корень проекта. null — чат вне проекта,
    // писать план некуда (глобальный чат — раздел «Состав команды» продуктового плана).
    internal string? ResolveTeamPlanRoot(Session session) =>
        session.ProjectId is { } pid && _projects.GetById(pid) is { } project
            ? SessionManager.EffectiveRoot(session, project.RootPath)
            : null;

    // План итерации по id карточки — источник правды автономного цикла: раздача остатка
    // волн и счётчик попыток под-задач живут в нём. В отличие от FindTeamPlan карточка
    // уже разрешена («Запустить» нажали), поэтому ищем без фильтра по Resolved.
    // Read с диска истории для неактивного чата идёт через SessionManager.ReadStoredTeamPlanAsync,
    // чтобы вертикаль не тянула прямую ссылку на ChatHistoryService (общий ресурс ядра).
    public Task<TeamImplementPlan?> GetTeamPlanFromHistoryAsync(string claudeSessionId, string planId) =>
        _sessions.ReadStoredTeamPlanAsync(claudeSessionId, planId);

    // План-режим при входе в план-фазу штаба (Э5). Тело переехало из SessionManager
    // (волна Б плана выноса штаба): управление режимом хода — собственное дело вертикали,
    // а ядро держит только рантайм-поля и доступ к Process. Поведение один в один:
    // деградация провайдера без «План» — молчаливая (план-режим не навязывается),
    // SavedMode сохраняется через WithTeamState (НЕ перезаписывается — цикл «интервью
    // → волна → снова интервью» обязан вернуть исходный выбор человека, а не Plan,
    // поставленный прошлым заходом), живой прогон CLI получает set_permission_mode
    // на лету через шов ITeamRunState.TrySetPermissionModeLive.
    public void EnterPlanPhaseMode(string sessionId)
    {
        var session = _sessions.GetById(sessionId);
        if (session?.TeamImplement is null) return;
        if (session.Mode == ClaudeMode.Plan) return;
        if (!_llmProviders.CapabilitiesFor(session.Model).SupportsPlanMode) return;
        WithTeamState(sessionId, t => { t.SavedMode ??= session.Mode; return true; });
        session.Mode = ClaudeMode.Plan;
        _run.TrySetPermissionModeLive(sessionId, ClaudeMode.Plan);
    }

    // Возврат режима человека после согласования плана (Confirming → Wave), при
    // выключении режима, после миграции на провайдера без поддержки «План» и при
    // добавочном плане авто-волн (B1). Тело переехало из SessionManager (волна Б).
    // Гард «координатор не пишет код» сохраняется: восстановленный режим прогоняется
    // через PermissionModeGuard.GuardCompatibleMode. Выбор пользователя не затирается:
    // после планирования чат работает в том режиме, в котором был, с поправкой на гард.
    public void RestoreUserMode(string sessionId)
    {
        var session = _sessions.GetById(sessionId);
        if (session?.TeamImplement is not { SavedMode: { } saved } team) return;
        var restored = PermissionModeGuard.GuardCompatibleMode(saved, team.CoordinatorNoCode);
        WithTeamState(sessionId, t => { t.SavedMode = null; return true; });
        if (session.Mode == restored) return;
        session.Mode = restored;
        _run.TrySetPermissionModeLive(sessionId, restored);
    }
}
