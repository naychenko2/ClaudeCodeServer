using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Team;

// Квоты и бюджет практики (этап 4, шаг 2г-3з, волна Е плана выноса штаба,
// docs/research/session-core-split-2026-09.md). Сюда переехал блок SessionManager
// целиком — квоты запуска исполнителей и пробуждения штаба агентом, подъём от чата
// исполнения к штабу и карточка «бюджет исрасходован»:
//
//   • TryConsumeTeamImplementRun — гейт лавины запусков исполнителей с реакционного
//     хода координатора: списывает единицу бюджета RunsUsed/TasksUsed авансом, иначе
//     цикл «доклад → запуск → доклад» уходил бы в бесконечный платный круг (Э4).
//   • RaiseTeamBudgetExhaustedAsync — карточка «Бюджет итерации израсходован» с
//     кнопкой «Добавить бюджет» (путь через EscalationRaiser — push и уведомление,
//     иначе публикация без хука — молчаливая остановка). Блок отложен до волны Е
//     сознательно: зовёт публикацию эскалации, а та уехала в волне Д — без обратного
//     ребра в ядро.
//   • RefundTeamImplementRun — компенсация квоты запуска (m3, второй проход Глеба):
//     реальный запуск может не состояться (404/400), и платить команде не за что.
//   • ResolveTeamStabId — чат-штаб для запроса: сам чат, если режим включён у него,
//     иначе ближайший предок в режиме; null — запрос отношения к режиму не имеет.
//   • TryConsumeTeamWakeup — квота пробуждения штаба агентом (Э4): любой платный ход
//     чата-штаба, поднятый НЕ человеком, считается против отдельного потолка, иначе
//     бюджет обходится соседним инструментом (доклад-блокер / chats_send).
//   • RefundTeamWakeup — симметрия: ReportBlockerAsync может упереться в TooDeep,
//     а chats_send — в дубль/переполнение/занятость; единица возвращается.
//
// Все шесть блоков работают через шов _teamState.WithTeamState (транзакция над
// SessionTeamImplement) и публичный API ядра: GetById, GetOwned, BroadcastTeamImplementAsync,
// SaveSessions и обработчик EscalationRaiser из TeamCoordinator. Owning-паттерн (экземпляр создаётся
// в конструкторе SessionManager) разрывает цикл DI, как у TeamDecisionService — регистрация
// переедет в Program.cs на шаге 2г-4, owning-обёртки в SessionManager будут сняты.
//
// Лок штаба — на стороне TeamStateService: блокировка командой ThroughWithTeamState лежит
// в её словаре, и любой обход (прямая правка team.Budget) обнулил бы счётчик ровно там,
// ради чего Э4 и затевался. Имена швов и инварианты — TeamStateService.WithTeamState.
internal sealed class TeamBudgetService
{
    private readonly SessionManager _sessions;
    private readonly ITeamSessionDirectory _dir;
    private readonly ITeamRunState _run;
    private readonly ITeamHistoryStore _history;
    private readonly ILogger<TeamBudgetService> _log;

    internal TeamBudgetService(SessionManager sessions, ITeamSessionDirectory dir,
        ITeamRunState run, ILogger<TeamBudgetService> log)
    {
        _sessions = sessions;
        _dir = dir;
        _run = run;
        _history = sessions;
        _log = log;
    }

    // Вердикт квоты запуска исполнителя со штабного хода-реакции (Э4). Enum живёт в Models
    // (TeamRunQuota): вертикаль принимает и возвращает его, ядро только прокидывает через
    // обёртку-делегат.

    // Гейт лавины запусков: на реакционном ходу координатора (ответ на доклад исполнителя)
    // запуск задачи разрешён, ПОКА цел бюджет итерации — запрет заменён квотой, а не снят.
    // Разрешение сразу же расходует единицу: счёт ведёт бэкенд в точке запуска, иначе
    // координатор в цикле «доклад → запуск → доклад» уходит в бесконечный платный круг.
    public (TeamRunQuota Verdict, string? Reason) TryConsumeTeamImplementRun(string sessionId, string ownerId)
    {
        // Запуск приходит не только из самого штаба, но и со второго уровня — из чата
        // исполнения под ним: расход ложится на бюджет ЕГО штаба, иначе исполнитель заводит
        // и запускает задачи мимо квоты (тот же обход, только этажом ниже).
        if (ResolveTeamStabId(sessionId, ownerId) is not { } stabId) return (TeamRunQuota.NotTeamMode, null);
        if (_sessions.GetById(stabId) is not { } stab) return (TeamRunQuota.NotTeamMode, null);
        if (stab.TeamImplement is not { } team) return (TeamRunQuota.NotTeamMode, null);

        string? reason = null;
        // Причина именно из бюджета (а не «остановлено»/«ждёт решения»/«план не подтверждён») —
        // по ней ниже поднимается карточка с кнопкой «Добавить бюджет»
        string? budgetReason = null;
        // WithTeamState — единственная транзакция над SessionTeamImplement (TeamStateService):
        // иначе «int++» не атомарен и счётчик теряет инкременты между потоками (раздача волны,
        // колбэки задач, HTTP-фильтр).
        _run.WithTeamState(stabId, t =>
        {
            // Стадия волны — вторая проверка после «Остановлено»: без неё квота честно
            // считала расход, но разрешала запуск ДО публикации и подтверждения плана —
            // единственное согласование (карточка плана) обходилось целиком (Э7-фикс).
            if (t.Stopped)
                reason = "практика остановлена человеком — новые запуски не идут, пока он не продолжит";
            // M3: причина отказа обязана быть честной. Из «ждёт решения» ссылаться на
            // неподтверждённый план — враньё: план как раз подтверждён, а ждём мы ответа
            // человека по карточке остановки (кнопкой или обычным сообщением в чат).
            else if (t.Stage == TeamImplementStage.AwaitingDecision)
                reason = "практика ждёт решения человека по карточке остановки — запуск исполнителей " +
                         "возобновится, когда он ответит (кнопкой карточки или сообщением в чат)";
            else if (t.Stage != TeamImplementStage.Wave)
                reason = "план ещё не подтверждён человеком — запуск исполнителей доступен только " +
                         "в стадии волны, единственное согласование — карточка плана";
            else
                reason = budgetReason = t.Budget.ExceededReason();
            if (reason is null)
            {
                t.Budget.RunsUsed++;
                // Задача, запущенная руками координатора, — такая же задача итерации, как
                // розданная волной: без этого счётчика потолок задач обходился ручной раздачей
                t.Budget.TasksUsed++;
            }
            return true;
        });
        if (reason is not null)
        {
            // Исчерпанный бюджет — единственный отказ, о котором человек ещё НЕ знает:
            // «остановлено» и «ждёт решения» уже висят карточкой, неподтверждённый план —
            // карточкой плана. Без этой публикации выхода из тупика не было вовсе: потолки
            // поднимает только кнопка «Добавить бюджет» карточки BudgetExhausted, а её
            // публиковала раздача волны — не гейт ручного запуска; попросить карточку
            // координатор тоже не мог (в протоколе лишь deviation/check/clarify), и штаб
            // бесконечно упирался в отказ, пока человек жал «Разрешить» на чужой карточке
            // расхождения с планом — та бюджет не трогает (прод 2026-08-08).
            if (budgetReason is not null)
                FireAndForget(RaiseTeamBudgetExhaustedAsync(stabId, budgetReason),
                    $"карточка исчерпанного бюджета итерации ({stabId})");
            return (TeamRunQuota.Exhausted, reason);
        }

        stab.UpdatedAt = DateTime.UtcNow;
        _dir.Persist();
        FireAndForget(_sessions.BroadcastTeamImplementAsync(stabId, stab),
            $"рассылка состояния режима после расхода квоты ({stabId})");
        return (TeamRunQuota.Allowed, null);
    }

    // Карточка «Бюджет итерации израсходован» из точки отказа квоты: у человека появляется
    // кнопка «Добавить бюджет» — единственный способ поднять потолки (агенту он недоступен).
    // Публикация переводит практику в «ждёт решения», поэтому следующий отказ придёт уже с
    // другой причиной и второй карточки не даст. Через EscalationRaiser, когда он есть:
    // хук вдобавок шлёт уведомление и push, иначе остановка осталась бы только в ленте.
    private async Task RaiseTeamBudgetExhaustedAsync(string stabId, string reason)
    {
        if (_sessions.GetById(stabId) is not { TeamImplement: { } team } stab) return;
        if (team.Stage == TeamImplementStage.AwaitingDecision) return;

        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.BudgetExhausted, reason),
            Details = $"Запуск исполнителя отклонён: {reason}.\n\n"
                      + TeamImplementPrompts.BudgetLine(team.Budget),
            Wave = team.WaveNumber,
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        if (_sessions.TeamHandlers.EscalationRaiser is { } raise) await raise(stab, card);
        else await _history.PublishTeamEscalationAsync(stabId, card);
    }

    // Компенсация квоты запуска (m3, второй проход Глеба): TryConsumeTeamImplementRun списывает
    // единицу авансом, в точке РЕШЕНИЯ (до попытки запуска) — иначе гейт нечестно разрешал бы
    // потратить лишнее между «проверить» и «списать». Но реальный запуск может не состояться
    // (задача не найдена, неверное состояние) — тогда платить команде не с чего, и вызывающая
    // сторона (фильтр DenyOnDelegatedTurn.OnActionExecuted) возвращает единицу сюда.
    public void RefundTeamImplementRun(string sessionId, string ownerId)
    {
        if (ResolveTeamStabId(sessionId, ownerId) is not { } stabId) return;
        if (_sessions.GetById(stabId) is not { TeamImplement: { } team } stab) return;

        _run.WithTeamState(stabId, t =>
        {
            if (t.Budget.RunsUsed > 0) t.Budget.RunsUsed--;
            if (t.Budget.TasksUsed > 0) t.Budget.TasksUsed--;
            return true;
        });
        stab.UpdatedAt = DateTime.UtcNow;
        _dir.Persist();
        FireAndForget(_sessions.BroadcastTeamImplementAsync(stabId, stab),
            $"рассылка состояния режима после возврата квоты ({stabId})");
    }

    // Чат-штаб для запроса: сам чат, если режим включён у него, иначе ближайший предок
    // в режиме (чат исполнения висит под штабом через вычисляемый ParentSessionId).
    // null — к режиму запрос отношения не имеет. Шагов немного: иерархия исполнения мелкая,
    // а счётчик — страховка от кольца в данных (как в IsDescendantOf).
    private string? ResolveTeamStabId(string sessionId, string ownerId)
    {
        var cur = _sessions.GetOwned(sessionId, ownerId);
        for (var steps = 0; cur is not null && steps < 8; steps++)
        {
            if (cur.TeamImplement is not null) return cur.Id;
            if (cur.ParentSessionId is not { } parentId) return null;
            cur = _sessions.GetOwned(parentId, ownerId);
        }
        return null;
    }

    // Квота пробуждения штаба агентом (Э4): любой платный ход чата-штаба, поднятый НЕ
    // человеком, а другим агентом (доклад-блокер, chats_send из чата исполнителя), считается
    // против отдельного потолка. Без этого бюджет обходится соседним инструментом: запуск
    // задач гейтит квота `TryConsumeTeamImplementRun`, а разбудить координатора можно было
    // бесплатно и бесконечно.
    // TeamMode=false — чат не штаб: ограничение не наше дело, пропускаем как раньше.
    public (bool TeamMode, bool Allowed, string? Reason) TryConsumeTeamWakeup(string sessionId)
    {
        if (_sessions.GetById(sessionId) is not { TeamImplement: { } team } stab)
            return (false, true, null);

        string? reason = null;
        var allowed = _run.WithTeamState(sessionId, t =>
        {
            reason = t.Stopped
                ? "практика остановлена человеком — команда не будит координатора, пока он не продолжит"
                : t.Budget.ExceededReason();
            if (reason is not null) return false;
            t.Budget.WakeupsUsed++;
            return true;
        }) is true;

        if (allowed)
        {
            stab.UpdatedAt = DateTime.UtcNow;
            _dir.Persist();
            FireAndForget(_sessions.BroadcastTeamImplementAsync(sessionId, stab),
                $"рассылка состояния режима после расхода пробуждения ({sessionId})");
        }
        return (true, allowed, reason);
    }

    // Компенсация квоты пробуждения (m3, второй проход Глеба): TryConsumeTeamWakeup списывает
    // единицу авансом, ДО того как сообщение реально дойдёт — ReportBlockerAsync может после
    // этого упереться в TooDeep, а chats_send — в дубль/переполнение очереди/занятость
    // (SessionMessagesController). Платить за несостоявшееся пробуждение команде не с чего —
    // возвращаем единицу.
    public void RefundTeamWakeup(string sessionId)
    {
        if (_sessions.GetById(sessionId) is not { TeamImplement: { } team } stab) return;

        _run.WithTeamState(sessionId, t =>
        {
            if (t.Budget.WakeupsUsed > 0) t.Budget.WakeupsUsed--;
            return true;
        });
        stab.UpdatedAt = DateTime.UtcNow;
        _dir.Persist();
        FireAndForget(_sessions.BroadcastTeamImplementAsync(sessionId, stab),
            $"рассылка состояния режима после возврата пробуждения ({sessionId})");
    }

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча. Копия приватного
    // хелпера SessionManager — у вертикали свой логгер, и тащить ради одной строчки
    // отдельный шов нерационально.
    private static void FireAndForget(Task task, string context) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[TeamBudgetService] {context}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
}
