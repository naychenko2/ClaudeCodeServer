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
// StartTeamWorkAsync/CloseTeamTalkAsync/PublishTeamEscalationAsync/SaveTeamImplementStateAsync/
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
        var stalledStage = team.Stage == TeamImplementStage.Interview
            || (team.Stage == TeamImplementStage.Planning && team.WaveNumber == 0);
        if (stalledStage && !asked && !_run.IsPlanningInFlight(sessionId)
            && !_run.HasAsyncAgent(sessionId))
        {
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
                    Wave = team.WaveNumber,
                    Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
                }
                : BuildSilentStallEscalation(team, turnText);
            if (_sessions.TeamHandlers.EscalationRaiser is { } raise) await raise(session, stalled);
            else await _sessions.PublishTeamEscalationAsync(sessionId, stalled);
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
            await _sessions.SaveTeamImplementStateAsync(sessionId);
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
                else await _sessions.PublishTeamEscalationAsync(parentId!, card);
            }
            _log.LogWarning("Доклад-блокер из чата {SessionId}: ход штаба не запущен ({Reason})", sessionId, wake.Reason);
            return quiet;
        }

        var result = await _sessions.ReportUpAsync(sessionId, TeamImplementPrompts.BlockerReportText(text), ownerId,
            withTurn: true, reactionPrompt: TeamImplementPrompts.BlockerReactionTurn(chat?.Name));
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
            else await _sessions.PublishTeamEscalationAsync(parentId, escalation);
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
}
