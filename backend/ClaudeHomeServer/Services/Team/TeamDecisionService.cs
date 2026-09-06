using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Team;

// Запуск работы, закрытие интервью, реакция человека на карточку плана, эскалации
// и остановка практики (этап 4, шаги 2г-3е и 2г-3ж, волны Г и Д плана выноса штаба,
// docs/research/session-core-split-2026-09.md). Сюда переехало тело восьми блоков
// SessionManager, которые раньше жили вперемешку с ядром:
//
// Волна Г:
//   • StartTeamWorkAsync — маркер работы `<team:work/>` от координатора: готовит состояние
//     режима (стадия Interview/Planning/Idle → Planning, новый бюджет на итерации Idle,
//     погашение устаревшей карточки на Confirming) и зовёт RunTeamPlanningAsync. Точка
//     входа для классификации вводной как работы — спека «Бюджет» сбрасывает потолки
//     ИМЕННО здесь, а не на приёме сообщения (разговорный ход в Idle потолки не трогает).
//   • CloseTeamTalkAsync — маркер разговора `<team:talk/>` от координатора: выход из
//     интервью без работы, режим человека возвращается, бюджет не трогается. Третья
//     дверь в мёртвую зону конвейера закрыта тем же предикатом и вызовом раздачи, что
//     у двух других точек выхода из ожидания.
//   • RespondTeamPlanAsync — реакция человека на карточку плана (Run/Reassign/Cancel/Edit):
//     подготовка к запуску волны, смена исполнителя, отмена, серверное перепланирование.
//     Содержит развилку «активный аккумулятор против диска» для неактивного чата и
//     гард устаревшей карточки (M8) с гашением через ResolveStalePlanCardAsync.
//
// Волна Д:
//   • PublishTeamEscalationAsync — публикация карточки остановки (запись в ленту +
//     стадия «ждёт решения», переживает рестарт). Единая точка публикации для штабных
//     триггеров (блокер, таймаут, отклонение плана) и кнопки «Остановить».
//   • GetOpenTeamEscalationsAsync / MarkTeamEscalationRemindedAsync — повторные напоминания
//     сторожа (TeamWaveService.CheckAwaitingEscalationsAsync). Оба прячут развилку
//     Accumulator/диск за публичными методами ядра (ListOpenEscalationsAsync и
//     MarkEscalationRemindedAsync) — вертикаль не получает ни SessionEntry, ни Accumulator.
//   • RespondTeamEscalationAsync — реакция человека по карточке остановки (addBudget /
//     runNext / resume / retryPlan / finish / stop / keepFixing / skip / drop / editRest /
//     answer). Самый крупный блок эскалаций: карточка гаснет, состояние правится
//     транзакцией, координатору уходит ход с текстом решения.
//   • StopTeamImplementAsync — «Остановить» (кнопка человека): едет вместе с
//     RespondTeamEscalationAsync, потому что это вторая половина той же кнопки: карточку
//     возврата поднимает RespondTeamEscalationAsync, а состояние ставит Stop. Комментарий
//     в коде прямо называет их одной точкой (сбой 28.08.2026).
//
// Все восемь блоков работают с состоянием SessionTeamImplement (через шов WithTeamState) и
// публичным API ядра (BroadcastAsync/ResolveOwnerId/GetById/FindActivePlanAsync/
// ListOpenEscalationsAsync/MarkEscalationRemindedAsync/ResolveEscalationAsync/RunTeamPlanningAsync/
// EnterInterviewAsync/BroadcastTeamImplementAsync/SaveSessions). Развилки «Accumulator vs диск»
// спрятаны внутри ядра — это не пятый шов, а новые методы в существующий контракт публичного API.
//
// Собственные помощники (IsStalePlanCard/WaveStartPendingAfterDecision/AllPlannedWavesClosed/
// FireAndForget) живут private static внутри класса: они узкие и другому сервису вертикали
// не нужны. Обработчики WaveStarter/EscalationRaiser/SubtaskDropHandler с шага 2г-4 живут
// в TeamCoordinator и читаются через _sessions.TeamHandlers (прежде — Func-свойства ядра;
// разбор, почему это был круговой маршрут, а не разрыв цикла, —
// docs/research/team-di-migration-2026-09.md §3).
//
// Owning-паттерн (как TeamCoordinator/TeamStateService/TeamPlanService): экземпляр создаётся
// в конструкторе SessionManager, а не через DI. Так разорван цикл «SessionManager хочет
// TeamDecisionService, TeamDecisionService хочет SessionManager» без Lazy<T> и без новых
// Func-каналов. Когда придёт шаг 2г-4, регистрация переедет в Program.cs, а owning-обёртки
// в SessionManager будут сняты.
internal sealed class TeamDecisionService
{
    private readonly SessionManager _sessions;
    // Швы данных «штаб → ядро» (этап 4, шаг 2г-3б; объявления — TeamCoreSeams.cs). Пока
    // приходят upcast'ом из того же SessionManager: конструктор не меняется, чтобы шов не
    // потянул за собой правку тестов (их 116, и переезд их arrange — работа шага 2г).
    private readonly ITeamSessionDirectory _dir;
    private readonly ITeamHistoryStore _history;
    private readonly ITeamRunState _run;
    private readonly ITeamTurnIntake _intake;
    private readonly PersonaManager _personas;
    private readonly ILogger<TeamDecisionService> _log;

    internal TeamDecisionService(SessionManager sessions, ITeamSessionDirectory dir,
        ITeamHistoryStore history, ITeamRunState run, ITeamTurnIntake intake,
        PersonaManager personas, ILogger<TeamDecisionService> log)
    {
        _sessions = sessions;
        _dir = dir;
        _history = history;
        _run = run;
        _intake = intake;
        _personas = personas;
        _log = log;
    }

    // Новая вводная разложена планировщиком и уходит в волну (Э5). Гард по стадии: работу
    // разворачиваем только когда итерация не идёт — иначе маркер посреди волны запустил бы
    // вторую поверх первой. «Остановить» удерживает маркеры в стадиях идущей итерации, но
    // не новую вводную в ожидании: классифицированная как работа — она и есть решение
    // человека продолжить (спека «Бюджет»: «Остановить» относится к прошлой итерации).
    // feedback — правка человека к текущему плану («Изменить план»): планировщик
    // пересобирает план под неё (см. TeamPlanningService.BuildPlannerPrompt).
    public async Task StartTeamWorkAsync(string sessionId, string request, string? feedback = null)
    {
        if (_sessions.GetById(sessionId) is not { } session) return;
        if (session.TeamImplement is not { } team) return;
        // Э8: интервью — легальная точка выхода в план (маркером его и закрывает координатор).
        // Confirming (правка плана текстом, прод 2026-08-04): координатор получил правку и
        // обязан пересобрать план маркером работы (PlanEditProtocol) — до фикса маркер здесь
        // молча проглатывался, и человек оставался со старой карточкой.
        if ((team.Stopped && team.Stage != TeamImplementStage.Idle)
            || team.Stage is not (TeamImplementStage.Interview
                or TeamImplementStage.Planning or TeamImplementStage.Idle
                or TeamImplementStage.Confirming))
        {
            _log.LogInformation("Маркер работы в чате-штабе {SessionId} пропущен: стадия {Stage}, остановка {Stopped}",
                sessionId, team.Stage, team.Stopped);
            return;
        }

        // Интервью закончено — идёт планирование. Стадию двигаем ДО вызова планировщика:
        // он работает секунды, и всё это время бейдж обязан показывать «планирование», а не
        // «интервью», в котором человек ждал бы новых вопросов. План-режим остаётся: обе
        // стадии живут в одном непрерывном план-режиме.
        if (team.Stage == TeamImplementStage.Interview)
        {
            _sessions.WithTeamState(sessionId, t => { t.Stage = TeamImplementStage.Planning; return true; });
            session.UpdatedAt = DateTime.UtcNow;
            _dir.Persist();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        }

        // Правка плана на подтверждении: тот же контур, что у clarify (Э8) — старая карточка
        // гаснет как заменённая, новый план публикуется версией vN+1 с обязательным
        // подтверждением. План-режим уже навязан (Confirming живёт в нём со стадии интервью).
        if (team.Stage == TeamImplementStage.Confirming)
        {
            var nextVersion = team.PlanVersion + 1;
            _sessions.WithTeamState(sessionId, t =>
            {
                t.Stage = TeamImplementStage.Planning;
                t.Replanning = true;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            await _sessions.SupersedeCurrentPlanCardAsync(sessionId, session, nextVersion);
            session.UpdatedAt = DateTime.UtcNow;
            _dir.Persist();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        }

        // M6: новая итерация в ожидании открывается ЗДЕСЬ — классификацией вводной как работы,
        // а не приёмом сообщения (спека «Бюджет»: сброс — по вводной, которую координатор
        // классифицировал как работу). Разговорный вопрос в Idle потолки не обнуляет,
        // план-режим не навязывает и ложной эскалации не даёт.
        if (team.Stage == TeamImplementStage.Idle)
        {
            _sessions.WithTeamState(sessionId, t =>
            {
                t.Budget = _sessions.NewTeamImplementBudget();
                t.WaveNumber = 0;
                t.ClosedWave = 0;
                t.PlannedWaves = 0;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                // «Остановить» относилось к прошлой итерации — новая вводная её снимает
                t.Stopped = false;
                t.Stage = TeamImplementStage.Planning;
                t.InterviewRounds = 0;
                t.Replanning = false;
                // Новая вводная после Idle (М6) — тоже НОВАЯ вводная в счёте IterationNumber,
                // отдельно от ResetTeamIterationOnUserInput (та ловит только самую первую):
                // без этого файл плана снова писался бы в тот же путь, что и у прошлой (прод
                // 2026-08-03, находка Веры).
                t.IterationNumber++;
                return true;
            });
            // План-режим — с классификации, а не с приёма сообщения: разговорный ход в
            // ожидании идёт в режиме человека, селектор не лочится.
            _sessions.EnterPlanPhaseMode(sessionId);
            session.UpdatedAt = DateTime.UtcNow;
            _dir.Persist();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        }

        await _sessions.RunTeamPlanningAsync(sessionId, request, feedback, _run.TurnStartedByHuman(sessionId));
    }

    // Выход из интервью без работы (M6, маркер `<team:talk/>`): координатор честно разобрал
    // сообщение — это разговор, практику на пустом месте не разворачиваем. Свежая «итерация»
    // возвращается в ожидание первой вводной (Planning), прерванное clarify-интервью — обратно
    // в волну со свежими отсечками сторожа (как выход из «ждёт решения»). План-режим был
    // навязан на время интервью — возвращаем режим человека. Бюджет не трогаем: разговор
    // ничего не стоит (WorkClassificationProtocol).
    public async Task CloseTeamTalkAsync(string sessionId)
    {
        if (_sessions.GetById(sessionId) is not { } session) return;
        if (session.TeamImplement is not { } team) return;
        if (team.Stage != TeamImplementStage.Interview) return;

        _sessions.WithTeamState(sessionId, t =>
        {
            if (t.WaveNumber > 0)
            {
                t.Stage = TeamImplementStage.Wave;
                // Волна продолжается — страховка таймаута заводится заново
                if (t.ClosedWave < t.WaveNumber)
                {
                    t.WaveStartedAt = DateTime.UtcNow;
                    t.WaveActivityAt = DateTime.UtcNow;
                }
                // Интервью закончилось без плана — следующий план снова обычный, а не «новая
                // версия с обязательным подтверждением» (признак ставил вход в clarify)
                t.Replanning = false;
            }
            else
            {
                t.Stage = TeamImplementStage.Planning;
                t.InterviewRounds = 0;
            }
            return true;
        });
        _sessions.RestoreUserMode(sessionId);
        session.UpdatedAt = DateTime.UtcNow;
        _dir.Persist();
        await _sessions.BroadcastTeamImplementAsync(sessionId, session);

        // Третья дверь в мёртвую зону (Major, ревью 2026-08-17): интервью могло закончиться
        // ПОСЛЕ закрытия волны (clarify посреди волны → CloseWaveIfDoneAsync стоит в
        // waitsHuman, ставит ClosedWave и конвейер не двигает). Тогда выше стадия вернулась
        // в Wave, но отсечки не заводятся (ClosedWave == WaveNumber) и раздачу следующей
        // никто не позвал — тот же стоящий конвейер, который сторож ловил бы только через
        // таймаут простоя. Тот же предикат и тот же вызов раздачи, что у двух других точек
        // выхода из ожидания; повод StateCatchUp — гейт при снятых авто-волнах решает
        // TeamWaveService, как и везде.
        if (session.TeamImplement is { } teamNow
            && teamNow.PlanCardId is { } planId
            && _sessions.TeamHandlers.WaveStarter is { } starter)
        {
            var plan = await _sessions.GetTeamPlanAsync(sessionId, planId);
            if (plan is not null && WaveStartPendingAfterDecision(teamNow, plan))
            {
                try { await starter(session, plan, TeamWaveTrigger.StateCatchUp); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Раздача волны после выхода из интервью (чат {SessionId}) не удалась", sessionId);
                }
            }
        }
    }

    // Ответ человека по карточке плана (SessionHub.RespondTeamPlan).
    // Run — согласование получено, стадия уходит в «волна» (раздача — Э3);
    // Reassign — сменить исполнителя под-задачи, карточка остаётся открытой;
    // Cancel — план отклонён, режим возвращается к планированию;
    // Edit — правка плана текстом feedback: сервер сам пересобирает план (см. ветку ниже).
    public async Task<TeamImplementPlan?> RespondTeamPlanAsync(string sessionId, string planId,
        TeamPlanDecision decision, string? subtaskId = null, string? executorPersonaId = null,
        string? userId = null, string? feedback = null)
    {
        if (_sessions.GetById(sessionId) is not { } session) return null;
        var ownerId = _sessions.ResolveOwnerId(session);
        if (ownerId is null || (userId is not null && ownerId != userId)) return null;

        // Карточка живёт в аккумуляторе идущего хода, а после рестарта его ещё нет — тогда
        // читаем её с диска, как это давно делают карточки остановок. Без fallback кнопка
        // «Запустить» после перезапуска сервера молча не работала.
        var plan = await _sessions.FindActivePlanAsync(sessionId, planId);
        if (plan is null) return null;

        // M8: клик по УСТАРЕВШЕЙ карточке — в ленте висит v1, а опубликован уже v2 (либо
        // текущая карточка вообще другая). Пропускать такое решение нельзя: стадия ушла бы
        // в Wave при WaveNumber=0 («волна-призрак» — сторож не тикает), ApprovedPlanVersion
        // откатился бы на старую версию, а RestoreUserMode снял бы план-режим посреди
        // перепланирования. Волна всё равно не стартовала (гард версий в TeamWaveService),
        // то есть отказ был молчаливым, а состояние — враньём.
        if (session.TeamImplement is { } current && IsStalePlanCard(current, planId, plan))
        {
            await _sessions.ResolveStalePlanCardAsync(sessionId, session, current, planId, plan);
            return null;
        }

        // Edit («Изменить план», прод 2026-08-04): серверное перепланирование. Правка —
        // решение по карточке, а не сообщение в чат: ход координатору не выдаётся, сервер
        // сам гасит текущую карточку как заменённую и запускает планировщик с правкой.
        // Итог детерминирован: либо карточка версии vN+1 на подтверждении, либо карточка
        // с причиной сбоя и кнопкой повтора — молчаливого тупика нет ни в каком исходе.
        if (decision == TeamPlanDecision.Edit)
        {
            var team = session.TeamImplement;
            if (team is null || string.IsNullOrWhiteSpace(feedback)) return null;
            // Правка жива только для плана на подтверждении: запущенный план уже раздаёт
            // волны (остаток меняется карточкой «Изменить остаток плана»), а отменённый
            // нечему править. Отклоняем тихо: кнопка в этих стадиях не рендерится.
            if (team.Stage is not (TeamImplementStage.Confirming or TeamImplementStage.Planning))
            {
                _log.LogInformation("Правка плана {PlanId} в чате {SessionId} пропущена: стадия {Stage}",
                    planId, sessionId, team.Stage);
                return null;
            }

            // Правка видна в ленте и остаётся в истории: при серверном перехвате хода
            // координатору не выдаётся, и без записи текст человека исчез бы из чата
            // (раньше кнопка слала его обычным сообщением).
            var editTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _history.AppendAsync(sessionId,
                new StoredUserMessage(feedback.Trim(), timestamp: editTs),
                new UserMessageMessage(feedback.Trim(), null, null, false, Timestamp: editTs));

            var nextVersion = team.PlanVersion + 1;
            _sessions.WithTeamState(sessionId, t =>
            {
                t.Stage = TeamImplementStage.Planning;
                // Тот же контур, что у clarify (Э8): следующий план — версия vN+1,
                // подтверждение обязательно даже при включённых авто-волнах.
                t.Replanning = true;
                return true;
            });
            await _sessions.SupersedeCurrentPlanCardAsync(sessionId, session, nextVersion);
            session.UpdatedAt = DateTime.UtcNow;
            _dir.Persist();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);

            // Планировщик зовётся напрямую: вводная — Request самой карточки (последняя
            // накопленная постановка итерации), правка уходит отдельным блоком промпта.
            await _sessions.RunTeamPlanningAsync(sessionId, plan.Request, feedback, fromHuman: true);
            return plan;
        }

        if (decision == TeamPlanDecision.Reassign)
        {
            if (subtaskId is null || executorPersonaId is null) return null;
            var subtask = plan.Subtasks.FirstOrDefault(s => s.Id == subtaskId);
            if (subtask is null) return null;
            // Новый исполнитель — только своя персона: чужая утекла бы в задачу Э3
            var persona = _personas.Get(executorPersonaId, ownerId);
            if (persona is null) return null;
            subtask.ExecutorPersonaId = persona.Id;
            subtask.ExecutorRationale = $"Выбран вручную: {PersonaManager.PersonaLabel(persona)}";
        }
        else
            plan.Approved = decision == TeamPlanDecision.Run;

        var resolved = decision != TeamPlanDecision.Reassign;
        // Развилка «активный аккумулятор против диска» спрятана внутри ядра
        // (ApplyPlanDecisionAsync в SessionManager): вертикаль не получает ни SessionEntry,
        // ни TurnAccumulator, ни лок истории, ни SemaphoreSlim — только команду и план.
        // Метод идемпотентен: фильтр по !Resolved на диске — та же защита от повторного клика,
        // что и у FindTeamPlan у активного чата.
        if (!await _sessions.ApplyPlanDecisionAsync(sessionId, planId, plan, resolved))
            return null;

        if (resolved && session.TeamImplement is not null)
        {
            _sessions.WithTeamState(sessionId, t =>
            {
                t.Stage = decision == TeamPlanDecision.Run
                    ? TeamImplementStage.Wave
                    : TeamImplementStage.Planning;
                // Плановое число волн итерации — из самого плана, а не из потолка бюджета:
                // при плане в 2 волны бейдж обязан показать «волна 1 из 2»
                if (decision == TeamPlanDecision.Run)
                {
                    t.PlannedWaves = plan.WaveCount;
                    // Утверждённый план — новый счёт волн: иначе волна 1 нового плана попадала бы
                    // под защиту «эта волна уже закрыта» от предыдущего (Э5, повторные итерации)
                    t.WaveNumber = 0;
                    t.ClosedWave = 0;
                    // Э8: работа разрешена именно этой версии плана — по ней и только по ней
                    // стартуют волны (гард в TeamWaveService).
                    t.ApprovedPlanVersion = plan.Version;
                }
                if (decision == TeamPlanDecision.Cancel) { t.PlanCardId = null; t.PlannedWaves = 0; }
                return true;
            });
            // Э8: «Запустить» закрывает стадии интервью и планирования — человеку возвращается
            // его режим прав (селектор снова разблокирован). «Отменить» возвращает штаб в
            // планирование, поэтому план-режим там остаётся.
            if (decision == TeamPlanDecision.Run) _sessions.RestoreUserMode(sessionId);
            session.UpdatedAt = DateTime.UtcNow;
            _dir.Persist();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        }

        await _sessions.BroadcastAsync(sessionId, new TeamPlanMessage(planId, plan, resolved,
            resolved ? plan.Approved : null));

        // Раздача под-задач и пакетный запуск волны (Э3) — в TeamWaveService: он знает про
        // задачи и исполнителей, которых SessionManager по построению не знает (цикл DI
        // разорван хуком, как OnSessionMessage у TaskExecutionService). Повод UserCommand:
        // «Запустить» — явное решение человека, гейт авто-волн не нужен.
        if (decision == TeamPlanDecision.Run && _sessions.TeamHandlers.WaveStarter is { } starter)
        {
            try { await starter(session, plan, TeamWaveTrigger.UserCommand); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Раздача волны по плану {PlanId} (чат {SessionId}) не удалась", planId, sessionId);
            }
        }
        return plan;
    }

    // Публикация карточки остановки: запись в ленту (переживает рестарт) + WS + стадия
    // «ждёт решения». Молчаливых остановок в режиме быть не должно, поэтому карточку
    // публикуем всегда, даже если человека сейчас нет в чате — уведомление и push шлёт
    // вызывающая сторона (TeamWaveService), она же знает про NotificationService.
    public async Task PublishTeamEscalationAsync(string sessionId, TeamEscalation escalation)
    {
        if (_sessions.GetById(sessionId) is not { } session)
        {
            // Чат удалён вместе с режимом — показывать карточку некуда, но след нужен:
            // «остановка без следа» и есть тот самый молчаливый провал, которого не должно быть
            _log.LogWarning("Карточка остановки «{Title}» не опубликована: чата {SessionId} больше нет",
                escalation.Title, sessionId);
            return;
        }

        // Автор карточки (Э8) — координатор НА МОМЕНТ публикации: карточка идёт от его лица,
        // а смена координатора позже историю не переписывает. Уже проставленного автора не
        // трогаем: карточку мог составить другой участник штаба (например планировщик).
        escalation.PersonaId ??= session.TeamImplement?.CoordinatorPersonaId ?? session.PersonaId;

        await _history.AppendAsync(sessionId,
            new StoredTeamEscalationMessage { EscalationId = escalation.Id, Escalation = escalation },
            new TeamEscalationMessage(escalation.Id, escalation.Kind.ToWireToken(), escalation.Title,
                escalation.Details, escalation.Actions, escalation.TaskId, escalation.Wave,
                false, null, escalation.PersonaId));

        if (session.TeamImplement is null) return;
        // Информационная карточка (добавочная волна) практику не останавливает: стадию и
        // отсечку таймаута не трогаем — работа по ней идёт прямо сейчас
        if (escalation.Kind.IsInformational()) return;
        // Тупик в волне (Э8) ведёт не в «ждёт решения», а в интервью: стадию ставит
        // EnterInterviewAsync — вместе с план-режимом и признаком перепланирования.
        if (escalation.Kind == TeamEscalationKind.NeedsClarification) return;
        _sessions.WithTeamState(sessionId, t =>
        {
            // Запоминаем, откуда практика пришла в ожидание: ответ человека до первой волны
            // вернёт её в эту стадию, а не в Wave. Повторная карточка поверх ожидания исходную
            // стадию не затирает — иначе возврат шёл бы в «ждёт решения» самого себя.
            if (t.Stage != TeamImplementStage.AwaitingDecision)
                t.StageBeforeDecision = t.Stage;
            t.Stage = TeamImplementStage.AwaitingDecision;
            // Волна больше не считается идущей: сторож зависших волн не должен второй раз
            // эскалировать то, что уже ждёт человека
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        session.UpdatedAt = DateTime.UtcNow;
        _sessions.SaveSessions();
        await _sessions.BroadcastTeamImplementAsync(sessionId, session);
    }

    // Открытые (не resolved) карточки остановки чата — сторожу повторных напоминаний
    // (TeamWaveService.CheckAwaitingEscalationsAsync). Развилка Accumulator/диск
    // спрятана в ListOpenEscalationsAsync ядра — вертикаль не получает ни SessionEntry,
    // ни Accumulator, ни лок истории.
    public Task<IReadOnlyList<TeamEscalation>> GetOpenTeamEscalationsAsync(string sessionId) =>
        _sessions.ListOpenEscalationsAsync(sessionId);

    // Отметить отправленное повторное напоминание по карточке остановки: счётчик и момент
    // последнего оклика пишутся на карточку в истории — переживают рестарт сервера, чтобы
    // после перезапуска не начать оклик заново. Развилка Accumulator/диск спрятана в
    // MarkEscalationRemindedAsync ядра.
    public Task<bool> MarkTeamEscalationRemindedAsync(string sessionId, string escalationId) =>
        _sessions.MarkEscalationRemindedAsync(sessionId, escalationId);

    // Решение человека по карточке остановки (SessionHub.RespondTeamEscalation).
    // Кнопка — это ярлык: карточка гаснет, а координатору уходит ход с текстом решения,
    // как если бы человек написал его сам. Часть действий дополнительно двигает бэкенд:
    // addBudget расширяет потолки, runNext раздаёт следующую волну, resume снимает «Стоп»,
    // retryPlan повторяет планирование по сохранённой вводной (без хода координатору).
    public async Task<bool> RespondTeamEscalationAsync(string sessionId, string escalationId,
        string? actionId, string? comment = null, string? userId = null)
    {
        if (_sessions.GetById(sessionId) is not { } session) return false;
        var ownerId = _sessions.ResolveOwnerId(session);
        if (ownerId is null || (userId is not null && ownerId != userId)) return false;

        // Развилка «активный аккумулятор против диска» спрятана внутри ядра
        // (ResolveEscalationAsync): вертикаль не получает ни SessionEntry, ни Accumulator,
        // ни лок истории — только объект карточки и признак успеха. На неактивном чате
        // карточка уже поднята с диска до правки (та же защита от двойного клика, что
        // и у FindActivePlanAsync у активного чата).
        var escalation = await _sessions.ResolveEscalationAsync(sessionId, escalationId, actionId);
        if (escalation is null) return false;

        var label = escalation.Actions.FirstOrDefault(a => a.Id == actionId)?.Label ?? actionId;
        var kind = escalation.Kind;

        if (session.TeamImplement is not null)
        {
            // Всё состояние решения — одной транзакцией: потолки, «Стоп», стадия и отсечки
            // сторожа правятся из разных потоков (квота хода, раздача волны, колбэки задач).
            _sessions.WithTeamState(sessionId, team =>
            {
                switch (actionId)
                {
                    // Добавить бюджет может ТОЛЬКО человек — этот путь идёт из хаба, у агента
                    // такого инструмента нет. Иначе потолок обходился бы действием координатора.
                    case "addBudget":
                        var fresh = _sessions.NewTeamImplementBudget();
                        team.Budget.MaxTasks += fresh.MaxTasks;
                        team.Budget.MaxWaves += fresh.MaxWaves;
                        team.Budget.MaxRuns += fresh.MaxRuns;
                        team.Budget.MaxRetries += fresh.MaxRetries;
                        team.Budget.MaxWakeups += fresh.MaxWakeups;
                        break;
                    case "resume":
                        team.Stopped = false;
                        break;
                    // «Остановить» с информационной карточки добавочной волны (Э5) — то же, что
                    // кнопка режима: запущенные исполнители дорабатывают, новые волны не идут
                    case "stop":
                        team.Stopped = true;
                        team.WaveStartedAt = null;
                        team.WaveActivityAt = null;
                        break;
                }
                // Практика возвращается в работу: волны идут дальше, стадию вернём в «волна»
                // (для «завершить» координатор сам подведёт итог — стадию двигать не станем;
                // для «остановить» стадия остаётся прежней — работа не возобновляется).
                // P23 (прод 2026-08-12): но если все плановые волны уже закрыты — возвращать в Wave
                // некуда, это вечная «волна N из N» без работы. В Idle: итерация завершена, режим
                // ждёт новой вводной. Хода координатору здесь нет, поэтому не Checking (оно зависло
                // бы без хода проверки), а сразу Idle — итог уже подведён в ходе работы волн.
                // «Чинить дальше» (m4, второй проход Глеба) — координатор чинит и перепроверяет
                // САМ, раздачи волны здесь нет (starter ниже зовётся только для
                // runNext/addBudget/resume): уводить стадию в Wave означало бы, что упавший
                // следующий ход не даст checkFailed (HandleTeamTurnEndAsync требует
                // Stage == Checking), а сторож волн в Checking не смотрит — молчаливый тупик.
                team.Stage = actionId switch
                {
                    "finish" or "finishWithIssues" => TeamImplementStage.Checking,
                    "stop" => team.Stage,
                    "keepFixing" => TeamImplementStage.Checking,
                    // Повтор планирования по сохранённой вводной: интервью уже пройдено,
                    // сразу в планирование — даже когда волна уже была (сбой перепланирования)
                    "retryPlan" => TeamImplementStage.Planning,
                    // editRest (Minor, волна 3): «Изменить остаток плана» — не «продолжай как
                    // есть» (Wave), а перепланирование. EnterInterviewAsync ниже переставит
                    // стадию и корректно обнулит отсечки сторожа сам — здесь стадию не трогаем,
                    // чтобы не мелькала «волна» без отсечек (сторож её не увидел бы: волна уже
                    // закрыта, ClosedWave == WaveNumber, ветка обновления отсечек ниже не сработает).
                    "editRest" => team.Stage,
                    // До первой волны «вернуть в работу» некуда: волны ещё не стартовали,
                    // и Wave здесь — «волна-призрак» (WaveNumber=0, PlanCardId=null, сторож
                    // не тикает, статус врёт про доклады — прод 2026-07-31). Возвращаем
                    // стадию, из которой пришла карточка (интервью/планирование). Если волна
                    // реально стартует по этому решению (runNext/addBudget/resume с планом),
                    // стадию Wave выставит сама раздача (TeamWaveService.StartWaveCore).
                    _ => team.WaveNumber == 0
                        ? team.StageBeforeDecision ?? team.Stage
                        : AllPlannedWavesClosed(team)
                            ? TeamImplementStage.Idle
                            : TeamImplementStage.Wave,
                };
                // Решение принято, карточка гаснет — сохранённая стадия отработана
                team.StageBeforeDecision = null;
                // Вернулись в волну — заводим страховку таймаута заново: без отсечки сторож
                // молчал бы, и повторное зависание той же волны снова осталось бы незамеченным
                if (team.Stage == TeamImplementStage.Wave && team.WaveNumber > 0
                    && team.ClosedWave < team.WaveNumber)
                {
                    team.WaveStartedAt = DateTime.UtcNow;
                    team.WaveActivityAt = DateTime.UtcNow;
                }
                return true;
            });
            session.UpdatedAt = DateTime.UtcNow;
            _sessions.SaveSessions();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        }

        await _sessions.BroadcastAsync(sessionId, new TeamEscalationMessage(escalationId,
            (kind).ToWireToken(),
            escalation.Title, escalation.Details,
            escalation.Actions, escalation.TaskId, escalation.Wave, true, actionId,
            escalation.PersonaId));

        // «Остановить» с информационной карточки добавочной волны (Э5) — той же точкой, что
        // кнопка режима (ChatsController): состояние уже поставлено транзакцией выше, а повторный
        // вызов идемпотентен — зато карточку возврата («Продолжить»/«Завершить итерацию»)
        // публикует ОДНО место, и продолжить практику всегда есть чем (сбой 28.08.2026).
        if (actionId == "stop" && session.TeamImplement is not null)
            await StopTeamImplementAsync(sessionId, userId);

        // Раздача волны по решению человека — — тем же путём, что автоволна: план лежит в
        // карточке, раздаёт TeamWaveService (хук разрывает цикл DI). Явных кнопок четыре:
        // «Запустить», «Добавить бюджет», «Продолжить» и «Перезапустить» — после них практика
        // обязана поехать сама. Без раздачи волна не стартовала, WaveStartedAt оставался пустым
        // и сторож молчал: человек нажал кнопку, а работа встала навсегда без единого сигнала.
        // Мёртвая зона (прод 2026-08-17): тот же вызов ещё и по СОСТОЯНИЮ, а не только по
        // кнопке из белого списка — карточка могла висеть ПОСЛЕ закрытия волны (allow/
        // keepPlan/answer…), авто-раздача следующей была уже подавлена, и кроме этого
        // вызова позвать её было некому. Действия с иной стадией (finish/stop/editRest/
        // retryPlan) сюда не попадают: их стадия не Wave.
        // D1 (ревью 2026-08-17): повод вызова различает два случая — кнопки «Запустить»/
        // «Добавить бюджет»/«Продолжить»/«Перезапустить» это явное решение запускать (гейт
        // не нужен), а докрут по состоянию при снятых авто-волнах обязан показать гейт-карточку
        // вместо молчаливой раздачи. Что именно делать, решает TeamWaveService по поводу
        // вызова — второй точки истины здесь не заводим.
        // «Перезапустить» в списке с круга 3 (приёмка круга 2): до него клик по карточке
        // мёртвой зоны при снятых авто-волнах поднимал гейт, и работа ехала со второго
        // клика — подпись обещала больше, чем делала. Для обычной зависшей волны добавление
        // ничего не меняет: там ClosedWave < WaveNumber, предикат раздачи ложен.
        // Раздавать нечего (волна уже идёт) — StartWave вернёт пустой список и не навредит.
        if (session.TeamImplement is { } teamNow
            && teamNow.PlanCardId is { } planId
            && _sessions.TeamHandlers.WaveStarter is { } starter)
        {
            var plan = await _sessions.GetTeamPlanAsync(sessionId, planId);
            var trigger = actionId is "runNext" or "addBudget" or "resume" or "restart"
                ? TeamWaveTrigger.UserCommand
                : TeamWaveTrigger.StateCatchUp;
            if (plan is not null && (trigger == TeamWaveTrigger.UserCommand
                    || WaveStartPendingAfterDecision(teamNow, plan)))
            {
                try { await starter(session, plan, trigger); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Раздача волны по решению человека (чат {SessionId}) не удалась", sessionId);
                }
            }
        }

        // skip (TaskFailed) / drop (Blocker) — Minor, волна 3: под-задача помечается Done
        // (хук SubtaskDropHandler), тем же путём закрывая волну, что и обычный доклад —
        // раньше кнопки ничего не делали, и волна не могла закрыться до ручного tasks_complete.
        if (actionId is "skip" or "drop" && escalation.TaskId is { } droppedTaskId
            && _sessions.TeamHandlers.SubtaskDropHandler is { } dropHandler)
        {
            try
            {
                await dropHandler(droppedTaskId,
                    $"Снято решением человека по карточке остановки ({label}).");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Снятие под-задачи {TaskId} по решению человека (чат {SessionId}) не удалось",
                    droppedTaskId, sessionId);
            }
        }

        // editRest (WaveGate) — Minor, волна 3: «Изменить остаток плана» это перепланирование,
        // а не «продолжай как есть» — заводим его тем же путём, что тупик в волне (clarify).
        if (actionId == "editRest")
        {
            await _sessions.EnterInterviewAsync(sessionId, "человек попросил изменить остаток плана",
                withTurn: true);
            return true;
        }

        // retryPlan (сбой планирования): повтор идёт НАПРЯМУЮ по сохранённой вводной —
        // без хода координатору (интервью уже пройдено, текст маркера сохранён дословно).
        // Правка плана («Изменить план») повторяется С НЕЙ ЖЕ — иначе повтор вернул бы
        // прежний план, и правка человека потерялась бы. Стадию уже поставили Planning выше,
        // гарды StartTeamWorkAsync повтору не нужны. Не получится снова — RunTeamPlanningAsync
        // опубликует новую карточку с той же кнопкой.
        if (actionId == "retryPlan")
        {
            var teamState = session.TeamImplement;
            if (!string.IsNullOrWhiteSpace(teamState?.LastPlanRequest))
                await _sessions.RunTeamPlanningAsync(sessionId, teamState.LastPlanRequest,
                    teamState.LastPlanFeedback, _run.TurnStartedByHuman(sessionId));
            else
                _log.LogWarning("Повтор планирования в чате {SessionId}: сохранённая вводная пуста", sessionId);
            return true;
        }

        // Координатор узнаёт решение обычным ходом — как если бы человек написал его текстом.
        // В ленте — плашка механики, а не пузырь «Автоматически» с сырым текстом директивы.
        await _intake.SendOrEnqueueAsync(sessionId,
            TeamImplementPrompts.EscalationResolvedTurn(escalation, actionId, label, comment),
            senderPersonaId: null, silent: true, suppressTasksExecute: true,
            staffNote: TeamStaffNotes.EscalationResolved);
        return true;
    }

    // «Остановить» (кнопка человека): текущие исполнители дорабатывают, новые волны не
    // стартуют. Единая точка остановки для кнопки режима (ChatsController) и кнопки
    // «Остановить» информационной карточки волны (RespondTeamEscalationAsync): состояние И
    // карточка возврата живут здесь — без карточки продолжать остановленную практику
    // было бы нечем (сбой 28.08.2026).
    public async Task<Session?> StopTeamImplementAsync(string sessionId, string? userId = null)
    {
        if (_sessions.GetById(sessionId) is not { } session) return null;
        if (userId is not null && _sessions.ResolveOwnerId(session) != userId) return null;
        if (session.TeamImplement is null) return session;

        var wave = _sessions.WithTeamState(sessionId, t =>
        {
            t.Stopped = true;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return t.WaveNumber;
        });
        session.UpdatedAt = DateTime.UtcNow;
        _sessions.SaveSessions();
        await _sessions.BroadcastTeamImplementAsync(sessionId, session);

        // Карточка возврата — один раз на остановку: повторное «Остановить» при уже открытой
        // карточке Stopped второй не плодит, человек решает по той, что висит
        if ((await GetOpenTeamEscalationsAsync(sessionId)).Any(e => e.Kind == TeamEscalationKind.Stopped))
            return session;
        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.Stopped,
            Title = "Практика остановлена",
            Details = "Новые волны не стартуют. Запущенные исполнители доработают начатое — " +
                      "нажмите «Продолжить», когда команде можно идти дальше.",
            Wave = wave,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Stopped),
        };
        // Через раизер — с уведомлением и push (TeamWaveService); без него карточка всё равно
        // публикуется: молчаливых остановок в режиме не бывает
        if (_sessions.TeamHandlers.EscalationRaiser is { } raise) await raise(session, card);
        else await PublishTeamEscalationAsync(sessionId, card);
        return session;
    }

    // Карточка плана уже не актуальна (Э8, M8): либо текущая карточка режима другая, либо
    // её версия старше опубликованной. Нули — состояние до Э8 (версий не было): гард выключен,
    // прежнее поведение цело. PlanCardId=null — план ещё не публиковали, сравнивать не с чем.
    private static bool IsStalePlanCard(SessionTeamImplement team, string planId, TeamImplementPlan plan) =>
        (team.PlanCardId is { } currentId && currentId != planId)
        || (team.PlanVersion > 0 && plan.Version > 0 && plan.Version < team.PlanVersion);

    // Та же логика предиката, что у двух других точек выхода из ожидания (ResumeTeamFromDecision
    // OnUserInput и ответ по карточке остановки) — она собственная для вертикали Team, и
    // внешнему коду знать её незачем. Старая логика (SessionManager.WaveStartPendingAfterDecision
    // до волны Г): волна закрыта (ClosedWave == WaveNumber), ещё в стадии Wave, есть
    // неразданные под-задачи — тогда пора звать TeamWaveService.
    private static bool WaveStartPendingAfterDecision(SessionTeamImplement team, TeamImplementPlan plan) =>
        team.Stage == TeamImplementStage.Wave
        && team.WaveNumber > 0
        && team.ClosedWave == team.WaveNumber
        && plan.Subtasks.Any(s => s.TaskId is null);

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча. Копия приватного
    // хелпера SessionManager — у вертикали свой логгер, и тащить ради одной строчки
    // отдельный шов нерационально.
    private static void FireAndForget(Task task, string context) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[TeamDecisionService] {context}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    // Все плановые волны закрыты — единственное условие перевода итерации в Idle
    // (RespondTeamEscalationAsync, P23 прод 2026-08-12). Узкий предикат, чужому сервису
    // вертикали знать незачем.
    private static bool AllPlannedWavesClosed(SessionTeamImplement team) =>
        team.PlannedWaves > 0 && team.ClosedWave >= team.PlannedWaves;
}
