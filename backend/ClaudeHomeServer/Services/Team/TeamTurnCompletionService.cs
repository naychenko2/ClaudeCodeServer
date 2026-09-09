using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Turn;
// ReportUpResult — публичный enum, объявленный внутри класса SessionManager (Services).
// Через алиас using получаем короткое имя без префикса SessionManager в сигнатуре.
using ReportUpResult = ClaudeHomeServer.Services.SessionManager.ReportUpResult;

namespace ClaudeHomeServer.Services.Team;

// Шапка разбора хода штаба и доклад о блокере (этап 4, шаг 2г-3и, волна Ж плана выноса
// штаба, docs/research/session-core-split-2026-09.md, §5(4)). Сюда переехало тело шести
// блоков SessionManager, замыкающих штабной цикл «на терминале хода»:
//
//   • HandleTeamTurnCompletedAsync — подписчик шины turn/completed (этап 4 / шаг 1в):
//     по Outcome исхода и наличию плана в LastTeamTurnEnds решает, звать ли штабной
//     разбор хода, и в фоне (Task.Run) запускает HandleTeamTurnEndAsync. interrupted
//     и crashed без плана идут по отдельной ветке — там восстанавливаются отсечки
//     сторожа волн и выходят без штабного разбора.
//
//   • HandleTeamTurnEndAsync — тело штабного разбора хода (Э4 + Э5): восстановление
//     отсечек сторожа, авто-резолв карточки блокера (P23), разбор маркера эскалации/
//     работы/разговора, гард молчаливого тупика (Э7-фикс + Э8 + P16), обработка
//     упавшей и успешной проверки. Самый крупный блок штаба — 132 строки тела, и
//     он держит все развилки исходов хода координатора.
//
//   • RestoreWaveWatchdogIfPausedAsync — отсечки сторожа, погашенные вопросом ASK
//     в волне, возвращаются по завершении хода (ответ получен / прерывание без
//     result). Без восстановления волна осталась бы без надзора, и настоящий stall
//     никто бы не поймал. Статические хелперы (BuildSilentStallEscalation/
//     AllPlannedWavesClosed) живут рядом — они узкие и другому сервису вертикали
//     не нужны.
//
//   • ReportBlockerAsync — публичный метод ядра (ChatsController и фичи вызова
//     из чата исполнителя): пробуждает штаб через `TryConsumeTeamWakeup` и шлёт
//     координатору ход с BlockerReactionTurn. При исчерпанной квоте — карточка
//     «Исполнитель застрял, а бюджет/практика исчерпаны» с правильным шаблоном
//     (Stopped vs BudgetExhausted). Несостоявшееся пробуждение компенсируется
//     `RefundTeamWakeup` (m3, второй проход Глеба).
//
// Все шесть блоков работают через швы данных «штаб → ядро» (ITeamSessionDirectory/
// ITeamHistoryStore/ITeamRunState) и публичный API ядра: GetById/GetOwned/ResolveOwnerId/
// ReportUpAsync/BroadcastAsync/BroadcastTeamImplementAsync/EnterInterviewAsync/
// StartTeamWorkAsync/CloseTeamTalkAsync/
// TryConsumeTeamWakeup/RefundTeamWakeup/RaiseCoordinatorEscalationAsync/TryAutoResolveTeamBlockerAsync/
// RestoreWaveWatchdogIfPaused. Доступ к LastTeamTurnEnds идёт через достройку
// ITeamRunState (RecordTeamTurnEnd/TryTakeTeamTurnEnd, волна Ж) — вертикаль не получает
// SessionEntry, а ядро владеет единственным местом, где LastTeamTurnEnds живёт. Func-свойство
// EscalationRaiser с шага 2г-4 живёт в TeamCoordinator и читается через _sessions.TeamHandlers
// (прежде — Func-свойство ядра; разбор — docs/research/team-di-migration-2026-09.md §3).
//
// Owning-паттерн (как TeamDecisionService/TeamBudgetService/TeamEnableService): экземпляр
// создаётся в конструкторе SessionManager, а не через DI. Так разорван цикл
// «SessionManager хочет TeamTurnCompletionService, TeamTurnCompletionService хочет SessionManager»
// без Lazy<T> и без новых Func-каналов. Когда придёт шаг 2г-4, регистрация переедет в
// Program.cs, а owning-обёртки HandleTeamTurnEndAsync/HandleTeamTurnCompletedShim/
// ReportBlockerAsync в SessionManager будут сняты.
internal sealed class TeamTurnCompletionService
{
    private readonly SessionManager _sessions;
    private readonly ITeamSessionDirectory _dir;
    private readonly ITeamHistoryStore _history;
    private readonly ITeamRunState _run;
    private readonly ILogger<TeamTurnCompletionService> _log;

    internal TeamTurnCompletionService(SessionManager sessions, ITeamSessionDirectory dir,
        ITeamHistoryStore history, ITeamRunState run, ILogger<TeamTurnCompletionService> log)
    {
        _sessions = sessions;
        _dir = dir;
        _history = history;
        _run = run;
        _log = log;
    }

    // Подписчик шины turn/completed (этап 4 / шаг 1в): план вызова HandleTeamTurnEndAsync
    // лежит в LastTeamTurnEnds, ключ TurnSeq. Шина публикует turn/completed строго ОДИН раз
    // на ход, и подписчик забирает план ровно один раз. Дедуп SkipNextTeamTurnEnd в
    // OnMessageAsync ушёл вместе с переключением (двойной терминал ErrorMessage +
    // ResultMessage одного хода даёт один план по первой записи).
    //
    // Контракт фильтра по Outcome:
    //   - success | failed | egress_down | local_down — изымаем план и зовём штаб;
    //   - crashed — решает НЕ исход, а факт «терминал хода дошёл downstream»: план может
    //     отсутствовать штатно (SettleAsync без downstream), и это не сбой проводки;
    //   - interrupted — штаб НЕ зовём, но восстанавливаем отсечки сторожа
    //     (RestoreWaveWatchdogIfPausedAsync) — здесь, иначе прерванный ход result не
    //     пришлёт, и штатная ветка не восстановит;
    //   - cancelled — downstream не получает ничего (return до SettleAsync), сторож не трогаем.
    public Task HandleTeamTurnCompletedAsync(TurnCompleted e)
    {
        var outcome = e.Outcome;
        var turnSeq = e.Turn.TurnSeq;
        var sessionId = e.Turn.SessionId;

        // cancelled — downstream не получает ничего (return до SettleAsync): ни плана, ни повода
        // трогать сторож.
        if (outcome == "cancelled")
            return Task.CompletedTask;

        var session = _sessions.GetById(sessionId);
        if (session is null)
            return Task.CompletedTask;

        // interrupted — НЕ конец хода штаба, но это исход, где сторож волн после конца хода
        // **больше никто** не восстановит (HandleTeamTurnEndAsync возвращает отсечки штатно
        // на success | failed | egress_down | local_down). Ход оборвался без result, отсечки
        // сторожа, погашенные вопросом ASK, возвращаем здесь.
        if (outcome == "interrupted")
        {
            _sessions.RestoreWaveWatchdogIfPaused(sessionId);
            return Task.CompletedTask;
        }

        // Без живого TeamImplement штаб не работает вововсе — гард нужен на случай терминала
        // хода внешней персоны. Сторож волн у такого чата тоже не заведён.
        if (session.TeamImplement is null)
            return Task.CompletedTask;

        // Изъять план из LastTeamTurnEnds через шов ITeamRunState (волна Ж): отсутствие —
        // WARN, не бросаем: запись могла быть вытеснена потолком (8 записей, шторм), либо
        // OnMessageAsync не успел положить (невозможно по ордерингу), либо TurnSeq чужой.
        // Без плана штаб звать нечем.
        if (!_run.TryTakeTeamTurnEnd(sessionId, turnSeq, out var turnText, out var failed, out var asked))
        {
            // На crashed плана может не быть штатно (финал через SettleAsync — терминал downstream
            // не дошёл): это не сбой проводки, а второй законный путь исхода. Сторож волн при этом
            // восстановить всё равно надо — HandleTeamTurnEndAsync не позовут.
            if (outcome == "crashed")
            {
                _sessions.RestoreWaveWatchdogIfPaused(sessionId);
                return Task.CompletedTask;
            }
            _log?.LogWarning(
                "Подписчик turn/completed: sessionId={SessionId}, turnSeq={TurnSeq}, outcome={Outcome} — в LastTeamTurnEnds нет плана вызова HandleTeamTurnEndAsync",
                sessionId, turnSeq, outcome);
            return Task.CompletedTask;
        }

        // Task.Run — как и в прежнем OnMessageAsync: разбор маркеров и публикация карточек/WS —
        // синхронные блокирующие вызовы, в read-loop шины им не место. Гонок нет: публикация
        // turn/completed одна на ход, TryTakeTeamTurnEnd удаляет запись атомарно.
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleTeamTurnEndAsync(sessionId, turnText ?? string.Empty, failed, asked);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TeamTurnCompletionService] Конец хода штаба ({sessionId}): {ex.Message}");
            }
        });

        return Task.CompletedTask;
    }

    // Конец хода штаба (Э4 + Э5): маркеры координатора и переход в ожидание вводной.
    // Приоритет — эскалация: она останавливает практику, и разворачивать волну поверх
    // остановки незачем. Стадию «ожидание» ставим только на успешном ходу: упавший ход
    // итог не подвёл, и «итерация завершена» было бы враньём.
    // asked — в этом ходу координатор задал вопрос ASK-карточкой: тогда интервью работает,
    // и гард молчаливого тупика молчит (иначе карточка «вопросов не будет» приходила бы
    // ровно поверх пришедших вопросов).
    public async Task HandleTeamTurnEndAsync(string sessionId, string turnText, bool failed,
        bool asked = false)
    {
        var session = _sessions.GetById(sessionId);
        if (session is null) return;
        if (session.TeamImplement is not { } team) return;

        // Вопрос ASK в волне гасил отсечки сторожа (OnAskQuestionStabAsync): ход завершился —
        // ответ получен или ход прерван, волна снова под надзором. Стадию не трогаем: если
        // дальше по ходу маркер эскалации, публикация карточки сама переведёт практику в
        // ожидание и снова обнулит отсечки.
        _sessions.RestoreWaveWatchdogIfPaused(sessionId);

        // P23 (прод 2026-08-12): практика в «ждёт решения» по карточке блокера, но координатор
        // в этом ходе снял предмет блокера сам — продолжил работу маркером team:work или подвёл
        // итог при закрытых волнах плана. Карточку не держать: иначе она висит по решённому
        // вопросу, стадия стоит в AwaitingDecision, а человек отвечает кнопкой на уже ненужную
        // эскалацию (прогон Веры P23). Если погасили — перечитываем состояние команды.
        if (team.Stage == TeamImplementStage.AwaitingDecision
            && await _sessions.TryAutoResolveTeamBlockerAsync(sessionId, turnText)
            && session.TeamImplement is { } teamAfterResolve)
        {
            team = teamAfterResolve;
        }

        // Волна 1 team-blocker-honest: явный маркер снятия блокера `<team:resolved>суть</team>`
        // — координатор говорит, что снял блокер действием, без всякого team:work и финала
        // итерации. На каждое TaskId из `TaskId` открытых блокеров зовём TryResolveBlockerByFactAsync,
        // затем НЕ возвращаемся — идём дальше по остальным маркерам хода (M4, фикс-волна): раньше
        // немедленный return глотал `<escalate:*>`/`<team:work>`/`<team:talk/>` того же хода.
        // Координатор в одном ходу снял блокер и попросил решение — карточка развилки не
        // публиковалась; снял блокер и дал team:work — волна не стартовала.
        if (TeamProtocolMarkers.ParseResolvedMarker(turnText) is { } resolvedNote
            && team.Stage == TeamImplementStage.AwaitingDecision)
        {
            var openBlockers = (await _history.GetOpenTeamEscalationsAsync(sessionId))
                .Where(c => c.Kind == TeamEscalationKind.Blocker && c.TaskId is not null)
                .Select(c => c.TaskId!)
                .Distinct()
                .ToList();
            foreach (var taskId in openBlockers)
                await _sessions.TryResolveBlockerByFactAsync(sessionId, taskId, resolvedNote);
            // намеренно не return — разбор продолжается ниже
        }

        if (TeamProtocolMarkers.ParseEscalationMarker(turnText) is { } marker)
        {
            // Тупик в волне (Э8) — не «жду решения», а возврат в интервью: волны на паузе,
            // карточка с push, следом ход с просьбой задать вопросы ASK-карточками.
            if (marker.Kind == TeamEscalationKind.NeedsClarification)
                await _sessions.EnterInterviewAsync(sessionId, marker.Text, withTurn: true);
            else
                await _sessions.RaiseCoordinatorEscalationAsync(sessionId, marker.Kind, marker.Text);
            return;
        }

        if (TeamProtocolMarkers.ParseWorkMarker(turnText) is { } request)
        {
            await _sessions.StartTeamWorkAsync(sessionId, request);
            return;
        }

        // Разговорный ответ в интервью (M6): работы нет — закрываем интервью без плана
        // и без ложной эскалации, практика возвращается в прежнее состояние.
        if (TeamProtocolMarkers.HasTalkMarker(turnText))
        {
            await _sessions.CloseTeamTalkAsync(sessionId);
            return;
        }

        // Молчаливый тупик (Э7-фикс, находка Веры Major №3; Э8 расширил на Interview —
        // ревью Глеба): координатор ни разу не довёл дело до волны (WaveNumber == 0) и
        // закончил ход в planning или interview без маркера работы/эскалации — вводная, с
        // которой начинается практика, повисла бы без следа: ход завершился, плана нет,
        // карточки нет, бейдж «интервью»/«планирование» никогда не сдвинется. После первой
        // волны (WaveNumber > 0) такой же ответ без маркера — легитимный разговор по
        // WorkClassificationProtocol («что сейчас в работе?» и т.п.), эскалацию не поднимаем.
        // M9: интервью, вызванное тупиком в волне, приходит с WaveNumber > 0 — на него гард
        // «только до первой волны» не распространялся, и клятва карточки «сейчас придут
        // вопросы» нарушалась молча: вопросов нет, маркера нет, сторож волн в Interview
        // не тикает. Теперь стадия интервью под гардом при любом номере волны.
        // Прод 2026-08-04: гард обязан молчать, пока живо планирование по этой вводной
        // (TeamPlanningInFlight). Планировщик работает ДОЛЬШЕ хода (потолок 300 с), и за
        // это время в чате спокойно заканчиваются другие ходы — их конец без маркера при
        // Planning && WaveNumber == 0 не тупик координатора: план уже строится и придёт
        // карточкой (а не построится — карточку даст сбой/таймаут планировщика). Без флага
        // тревога «Координатор не понял вводную» поднималась на живой работе и висела
        // красной рядом с пришедшим планом.
        // Прод 2026-08-12 (P16): тот же аргумент — для async-субагента координатора. Ход,
        // что закончился текстом «запустил разведку», но в фоновом Tool-вызове оставил живого
        // агента (entry.Process.HasPendingBg), — НЕ тупик: координатор ждёт собственного
        // результата, и следующий ход (пробуждение по task-notification) почти наверняка
        // принесёт маркер team:work. Без исключения гард поднимал «Координатор не понял
        // вводную» на живой работе и уводил стадию в AwaitingDecision — тогда штатный
        // team:work уже не потреблялся (StartTeamWorkAsync её не принимает), и человеку
        // приходилось отвечать на ложную карточку (прогон Веры P16).
        // Задача b63fd8ea: голое HasAsyncAgent держало подавление бессрочно — фоновый агент
        // с heartbeat'ами мог не доводить координатора до маркера часами, и BgLingerTimeout
        // грейс тишины тут не лечит (это не потолок длительности). ShouldSuppressAsyncAgentStallGuard
        // подавляет гард ТОЛЬКО пока длительность подавления в окне порога (10 мин, см.
        // TeamAsyncAgentStallGuard); дольше — гард поднимает карточку молчаливого тупика.
        var stalledStage = team.Stage == TeamImplementStage.Interview
            || (team.Stage == TeamImplementStage.Planning && team.WaveNumber == 0);
        if (stalledStage && !asked && !_run.IsPlanningInFlight(sessionId)
            && !_run.ShouldSuppressAsyncAgentStallGuard(sessionId))
        {
            // Атомарный pre-claim под WithTeamState (Minor 1, идемпотентность против
            // гонки с HandleBgAgentDoneAsync): без него оба пути прочитали бы
            // team.Stage == Planning, дошли до publish и опубликовали карточку дважды —
            // shared snapshot всё ещё показывал Planning, пока второй поток не мутировал
            // объект. Pre-claim атомарно резервирует AwaitingDecision; параллельный
            // путь увидит её в собственном TryClaimSilentStall и выйдет. Снимок для
            // построения карточки (Stage/WaveNumber) и отката при сбое публикации берётся ДО мутации.
            if (!_run.TryClaimSilentStall(sessionId, out var claim))
                return;

            // Снимок для BuildSilentStallEscalation: оригинальный объект уже
            // AwaitingDecision внутри pre-claim, читать из него напрямую нельзя.
            var snapshot = new SessionTeamImplement
            {
                Stage = claim.Stage,
                WaveNumber = claim.WaveNumber,
            };
            // Волна 6 (живая приёмка волны 5): ход мог не завершиться маркером по ДВУМ разным
            // причинам, и текст карточки должен их различать. «Координатор не понял вводную»/
            // «Уточнения так и не пришли» — координатор ОТВЕТИЛ, но без маркера: это честная
            // реакция на его текст (SilentPlanningStallDetails/ClarifyStallDetails цитируют
            // turnText). А `failed` — ход оборван технически (рестарт сервера, упавший процесс,
            // таймаут провайдера) ДО того, как координатор вообще успел ответить по существу:
            // turnText в этом случае пуст или обрублен, и цитировать в карточке нечего, а текст
            // «не понял вводную» отправляет человека переформулировать задачу, хотя проблема не
            // в ней. Формулировка карточки-инфраструктурного обрыва согласована с владельцем.
            var stalled = failed
                ? new TeamEscalation
                {
                    Kind = TeamEscalationKind.ProductDecision,
                    Title = "Ход прервался",
                    Details = TeamImplementPrompts.TurnInterruptedDetails(),
                    Wave = snapshot.WaveNumber,
                    Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
                }
                : BuildSilentStallEscalation(snapshot, turnText);
            // Публикация карточки ПОСЛЕ успешного claim: если она упадёт (AppendAsync
            // бросил исключение), клеймо откатывается через RollbackSilentStallClaim под
            // тем же WithTeamState. Без отката чат зависнет в AwaitingDecision БЕЗ карточки:
            // stalledStage для этой стадии больше не true, гард больше никогда не сработает,
            // а исключение раньше молча уходило в Console.Error (см. ниже, обёртка
            // HandleTeamTurnCompletedAsync). Случай «чат удалён» этим catch НЕ покрыт —
            // PublishTeamEscalationAsync там делает ранний return БЕЗ исключения, и клеймо
            // остаётся до следующего гарда (отдельный нечастый кейс, отдельная диагностика).
            try
            {
                if (_sessions.TeamHandlers.EscalationRaiser is { } raise)
                    await raise(session, stalled);
                else
                    await _history.PublishTeamEscalationAsync(sessionId, stalled);
            }
            catch
            {
                if (!_run.RollbackSilentStallClaim(sessionId, claim))
                    _log.LogInformation("Молчаливый тупик {SessionId}: хвост публикации карточки «{Title}» упал после того, как она уже опубликована — клеймо не откатываем",
                        sessionId, stalled.Title);
                throw;
            }
            return;
        }

        // Ход проверки упал (процесс умер, лимит провайдера, ошибка): итог не подведён, но и
        // висеть в «проверке» вечно нельзя — сторож волн сюда не смотрит, а новая вводная
        // из этой стадии не разворачивается. Зовём человека карточкой «проверка не прошла».
        if (failed && team.Stage == TeamImplementStage.Checking)
        {
            await _sessions.RaiseCoordinatorEscalationAsync(sessionId, TeamEscalationKind.CheckFailed,
                "Ход проверки завершился ошибкой — итог итерации не подведён. "
                + "Продолжить починку или закрыть итерацию с замечаниями?");
            return;
        }

        // Проверка завершилась без эскалации — итерация закрыта, режим ждёт следующую вводную
        // (сам режим при этом НЕ выключается: выключает его только человек из бейджа).
        if (!failed && team.Stage == TeamImplementStage.Checking)
        {
            _run.WithTeamState(sessionId, t =>
            {
                t.Stage = TeamImplementStage.Idle;
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            await _dir.PersistAndBroadcastAsync(sessionId);
            _log.LogInformation("Итерация чата-штаба {SessionId} завершена — режим ждёт следующей вводной", sessionId);
        }
    }

    // Доклад о блокере (Э4): в отличие от промежуточного отчёта БУДИТ постановщика — ход
    // запускается сразу. Иначе «я застрял» лежит в ленте штаба до конца волны, а координатор
    // всё это время ждёт докладов о завершении, которых не будет.
    // Родитель — чат-штаб «Командной реализации» → человек дополнительно получает карточку
    // остановки с кнопками (молчаливых остановок в режиме не бывает).
    public async Task<ReportUpResult> ReportBlockerAsync(string sessionId, string text, string ownerId)
    {
        var chat = _sessions.GetOwned(sessionId, ownerId);
        var parentId = chat?.ParentSessionId;

        // Пробуждение штаба — платный ход, инициированный агентом, поэтому оно под квотой:
        // иначе исполнитель поднимал бы координатора докладом-блокером в бесконечном цикле,
        // не расходуя ни одной другой единицы бюджета (та же лавина, только с другого входа).
        var wake = parentId is null ? (true, true, null) : _sessions.TryConsumeTeamWakeup(parentId);

        if (!wake.Allowed)
        {
            // Квота выбрана либо практика остановлена — ход не поднимаем, НО молча не
            // отходим: застрявший исполнитель без карточки означал бы ровно то зависание,
            // которого в режиме быть не должно. Доклад ложится в ленту, человек — видит
            // карточку и push, а координатор проснётся уже по его решению.
            var quiet = await _sessions.ReportUpAsync(sessionId, TeamImplementPrompts.BlockerReportText(text),
                ownerId, withTurn: false);
            // Карточку и push шлём ОДИН раз на остановку: практика уже ждёт решения человека
            // (стадия awaitingDecision), и каждый следующий блокер волны добавлял бы к той же
            // причине ещё одну карточку и ещё один push — спам вместо сигнала.
            if (quiet is (ReportUpResult.Delivered or ReportUpResult.Queued)
                && _sessions.GetById(parentId!) is { TeamImplement: { } blockedTeam } blockedStab
                && blockedTeam.Stage != TeamImplementStage.AwaitingDecision)
            {
                var card = new TeamEscalation
                {
                    Kind = blockedTeam.Stopped ? TeamEscalationKind.Stopped : TeamEscalationKind.BudgetExhausted,
                    Title = blockedTeam.Stopped
                        ? "Исполнитель застрял, а практика остановлена"
                        : "Исполнитель застрял, а бюджет итерации израсходован",
                    Details = $"{text.Trim()}\n\nКоординатор не разбужен: {wake.Reason}.\n\n"
                              + TeamImplementPrompts.BudgetLine(blockedTeam.Budget),
                    TaskId = chat?.TaskId,
                    Wave = blockedTeam.WaveNumber,
                    Actions = TeamEscalationActions.For(blockedTeam.Stopped
                        ? TeamEscalationKind.Stopped
                        : TeamEscalationKind.BudgetExhausted),
                };
                if (_sessions.TeamHandlers.EscalationRaiser is { } raiseBlocked) await raiseBlocked(blockedStab, card);
                else await _history.PublishTeamEscalationAsync(parentId!, card);
            }
            _log.LogWarning("Доклад-блокер из чата {SessionId}: ход штаба не запущен ({Reason})", sessionId, wake.Reason);
            return quiet;
        }

        var result = await _sessions.ReportUpAsync(sessionId, TeamImplementPrompts.BlockerReportText(text), ownerId,
            withTurn: true, reactionPrompt: TeamImplementPrompts.BlockerReactionTurn(chat?.Name, chat?.Id));
        if (result is not (ReportUpResult.Delivered or ReportUpResult.Queued))
        {
            // Пробуждение списано выше (wake.Allowed), а доклад не дошёл (TooDeep/NoParent/
            // NotFound) — координатор фактически не разбужен, платить команде не за что (m3)
            if (parentId is not null) _sessions.RefundTeamWakeup(parentId);
            return result;
        }

        if (parentId is not null && _sessions.GetById(parentId) is { TeamImplement: { } team } stab)
        {
            var escalation = new TeamEscalation
            {
                Kind = TeamEscalationKind.Blocker,
                Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.Blocker, text),
                Details = text,
                TaskId = chat?.TaskId,
                Wave = team.WaveNumber,
                Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
            };
            if (_sessions.TeamHandlers.EscalationRaiser is { } raise) await raise(stab, escalation);
            else await _history.PublishTeamEscalationAsync(parentId, escalation);
        }
        return result;
    }

    // Карточка молчаливого тупика (ход завершился штатно, но без маркера) — вынесено из
    // HandleTeamTurnEndAsync, чтобы её не спутать с веткой инфраструктурного обрыва (см. там).
    private static TeamEscalation BuildSilentStallEscalation(SessionTeamImplement team, string turnText)
    {
        var clarifyStall = team.Stage == TeamImplementStage.Interview && team.WaveNumber > 0;
        return new TeamEscalation
        {
            Kind = TeamEscalationKind.ProductDecision,
            Title = clarifyStall ? "Уточнения так и не пришли" : "Координатор не понял вводную",
            Details = clarifyStall
                ? TeamImplementPrompts.ClarifyStallDetails(turnText, team.WaveNumber)
                : TeamImplementPrompts.SilentPlanningStallDetails(turnText),
            Wave = team.WaveNumber,
            Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
        };
    }

    // Хук на завершение фонового async-агента (волна 3 задачи b63fd8ea).
    //
    // Решает три задачи; первые две завязаны на ОДНУ проверку HasAsyncAgent ПОСЛЕ учёта
    // текущего сообщения (снимок читает OnMessageAsync через entry.Process?.HasPendingBg; к
    // этому моменту все четыре источника BgAgentDoneMessage — HandleStructuredTaskNotification,
    // HandleTaskNotification, HandleTaskOutputCompletion, FinalizeRunAsync — уже удалили
    // задачу из run.PendingBg синхронно, см. комментарий в OnMessageAsync на эту тему).
    // Третья задача — про pre-claim, не про HasAsyncAgent, общего у них только лок
    // WithTeamState:
    //
    // 1. Major (регрессия P16): карточка «Координатор не понял вводную» поднимается
    //    ТОЛЬКО когда (а) bg-агент абортировался (Aborted=true) и (б) других живых
    //    async-агентов больше нет (HasAsyncAgent == false). Иначе структурный
    //    task_notification ставит Aborted = status != "completed" ДЛЯ ОДНОГО конкретного
    //    агента, даже если параллельно работает ДРУГОЙ — карточка сразу на первом аборте
    //    уводила стадию в AwaitingDecision, хотя координатор ЖИВ и второй агент ещё
    //    работает: дословный регресс P16 (Major, найден ревью Глеба — тест Глеб уронил
    //    репро мутацией).
    //
    // 2. Minor 2 (сброс метки AsyncAgentStallSince): метка на entry сбрасывается только
    //    когда после текущего done агентов больше нет вообще (HasAsyncAgent == false).
    //    Прежний безусловный сброс на ЛЮБОМ BgAgentDoneMessage перезапускал 10-минутный
    //    потолок подавления на КАЖДОМ агенте цепочки — суппрессия тянулась неограниченно,
    //    ровно против того, что баг b63fd8ea чинил. Этот сброс делает вызывающий код в
    //    OnMessageAsync (см. комментарий там) под TeamTurnLock, отдельным решением от
    //    публикации карточки — но на ТОЙ ЖЕ проверке HasAsyncAgent.
    //
    // 3. Minor 1 (идемпотентность против гонки с HandleTeamTurnEndAsync — НЕ про
    //    HasAsyncAgent): оба пути могут прийти к публикации почти одновременно
    //    (Task.Run штабного разбора идёт параллельно с read-loop OnMessageAsync);
    //    без атомарного pre-claim оба читали team.Stage == Planning (объект общий,
    //    между read и publish параллельный поток успевает мутировать) и публиковали
    //    карточку дважды. Закрывается через ITeamRunState.TryClaimSilentStall — общий
    //    лок TeamStateService.WithTeamState сериализует оба пути: первый переводит
    //    стадию в AwaitingDecision под локом, второй видит её в собственном
    //    TryClaimSilentStall и выходит. Снимок Stage/WaveNumber/StageBeforeDecision/
    //    WaveStartedAt/WaveActivityAt берётся ДО мутации (объект уже AwaitingDecision
    //    внутри лока) — BuildSilentStallEscalation читает первые два для заголовка,
    //    а паре TryClaimSilentStall/RollbackSilentStallClaim нужен полный набор
    //    для отката при сбое публикации карточки.
    //
    // Метод, а не шов: вертикаль TeamTurnCompletionService уже владеет гардом и его
    // эскалацией (BuildSilentStallEscalation + EscalationRaiser/PublishTeamEscalationAsync),
    // добавить сюда соседний путь публикации — естественное расширение, а не новая ось.
    public async Task HandleBgAgentDoneAsync(string sessionId, bool aborted, bool hasAsync, bool asked)
    {
        var session = _sessions.GetById(sessionId);
        if (session is null) return;
        if (session.TeamImplement is not { } team) return;

        // 1. Major: есть ещё живой async-агент — карточка не нужна (P16-регресс).
        //    Гард дождётся финального BgAgentDoneMessage от последнего агента или
        //    следующего хода + 10-минутного порога волны 1.
        if (hasAsync) return;

        // Агент завершился штатно — карточка не нужна. Метка подавления
        // AsyncAgentStallSince уже сброшена (или не тронута) в OnMessageAsync по той же
        // проверке HasAsyncAgent.
        if (!aborted) return;

        // Зеркало гарда из HandleTeamTurnEndAsync (строки 244–247), но без 10-минутного
        // суппрессирования (агент уже мёртв, ждать дальше нечего).
        var stalledStage = team.Stage == TeamImplementStage.Interview
            || (team.Stage == TeamImplementStage.Planning && team.WaveNumber == 0);
        if (!stalledStage) return;
        if (asked) return;
        if (_run.IsPlanningInFlight(sessionId)) return;

        // 3. Minor 1: атомарный pre-claim. Снимок для заголовка карточки и для отката при сбое
        // публикации берётся ДО мутации — полный набор, см. xml-doc TryClaimSilentStall.
        if (!_run.TryClaimSilentStall(sessionId, out var claim))
            return;

        var snapshot = new SessionTeamImplement
        {
            Stage = claim.Stage,
            WaveNumber = claim.WaveNumber,
        };

        // turnText намеренно пуст: BgAgentDoneMessage не несёт координаторского текста,
        // и цитировать в карточке нечего. Формулировка «Координатор не понял вводную» и
        // так говорит, что текста-ответа не было.
        var escalation = BuildSilentStallEscalation(snapshot, turnText: string.Empty);
        // Публикация карточки ПОСЛЕ успешного claim: если она упадёт (AppendAsync
        // бросил исключение), клеймо откатывается через RollbackSilentStallClaim под тем же
        // WithTeamState. Без отката чат зависнет в AwaitingDecision БЕЗ карточки: stalledStage
        // для этой стадии больше не true, гард больше никогда не сработает. Случай «чат удалён»
        // этим catch НЕ покрыт — PublishTeamEscalationAsync там делает ранний return БЕЗ
        // исключения, и клеймо остаётся до следующего гарда (отдельный нечастый кейс).
        try
        {
            if (_sessions.TeamHandlers.EscalationRaiser is { } raise)
                await raise(session, escalation);
            else
                await _history.PublishTeamEscalationAsync(sessionId, escalation);
        }
        catch
        {
            if (!_run.RollbackSilentStallClaim(sessionId, claim))
                _log.LogInformation("Молчаливый тупик {SessionId} через хук: хвост публикации карточки «{Title}» упал после того, как она уже опубликована — клеймо не откатываем",
                    sessionId, escalation.Title);
            throw;
        }
    }
}
