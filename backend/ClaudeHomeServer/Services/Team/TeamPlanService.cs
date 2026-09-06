using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Team;

// Планирование штаба «Командная реализация» (этап 4, шаг 2г-3д, волна В плана выноса
// штаба, docs/research/session-core-split-2026-09.md). Сюда переехали блоки тела,
// отвечающие за построение плана, его публикацию карточкой и гашение устаревших карточек:
//
//   • RunTeamPlanningAsync — координатор планирования: вводная/правка сохраняются на
//     состоянии до планировщика, флаг живого планирования взводится, вызов CreateTeamPlanAsync,
//     при отказе — карточка с причиной. Гардов по стадии нет: состояние готовит вызывающий
//     (StartTeamWorkAsync для вводной, RespondTeamPlanAsync для правки «Изменить план»,
//     retryPlan для повтора после сбоя).
//   • CreateTeamPlanAsync — резолв координатора/кандидатов/планировщика, broadcast
//     «планировщик запущен», вызов промпта и сбор карточки при успехе.
//   • PublishTeamPlanAsync — публикация карточки плана: история (через шов) + WS +
//     стадия «ждёт подтверждения» (или Wave, если план добавочный при авто-волнах).
//   • SupersedeCurrentPlanCardAsync — гашение ТЕКУЩЕЙ карточки плана как заменённой версией
//     nextVersion. Идемпотентна (уже разрешённую карточку не трогает).
//   • ResolveStalePlanCardAsync — гашение клика по устаревшей карточке (M8) с разъяснением.
//   • SaveTeamPlanCardAsync — простая правка карточки после простановки под-задачам TaskId
//     (Э3). Карточка уже Resolved, поэтому обновляем её напрямую.
//
// Все пять методов из задачи плюс SaveTeamPlanCardAsync — содержат размазанную ранее
// развилку «активный аккумулятор против диска» (шестая точка — это общая AppendStoredAsync
// через шов AppendAsync, она в задачу не входит). Эта развилка спрятана внутри нового метода
// ITeamHistoryStore.SavePlanCardAsync (волна В, единственный новый метод шва, не пятый).
//
// Owning-паттерн (как TeamCoordinator/TeamStateService): экземпляр создаётся в конструкторе
// SessionManager, а не через DI. Так разорван цикл «SessionManager хочет TeamPlanService,
// TeamPlanService хочет SessionManager» без Lazy<T> и без новых Func-каналов. Когда придёт
// шаг 2г-4, регистрация переедет в Program.cs, а owning-обёртки в SessionManager будут сняты.
//
// На этом шаге новых швов нет: вертикаль получает всё через заведённые интерфейсы
// ITeamSessionDirectory/ITeamHistoryStore/ITeamRunState, доступ к Session — через публичный
// API ядра (GetById/BroadcastAsync/ResolveOwnerId/TeamEscalationRaiser/TeamWaveStarter).
internal sealed class TeamPlanService
{
    private readonly SessionManager _sessions;
    private readonly ITeamHistoryStore _history;
    private readonly ITeamRunState _run;
    private readonly TeamPlanningService? _planning;
    private readonly TeamCoordinator _coordinator;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly ILogger<TeamPlanService> _log;

    internal TeamPlanService(SessionManager sessions, ITeamHistoryStore history, ITeamRunState run,
        TeamPlanningService? planning, TeamCoordinator coordinator, PersonaManager personas,
        ProjectManager projects, ILogger<TeamPlanService> log)
    {
        _sessions = sessions;
        _history = history;
        _run = run;
        _planning = planning;
        _coordinator = coordinator;
        _personas = personas;
        _projects = projects;
        _log = log;
    }

    // Построить план по вводной и опубликовать карточкой в ленту штаба.
    // Возвращает план либо null с причиной отказа в reason (нет координатора, пустой состав,
    // планировщик не ответил) — вызывающая сторона показывает её человеку.
    // fromHuman (M7) — вводная пришла от человека: только тогда добавочный план может
    // авто-подтвердиться. Прямые вызовы (кнопки человека) не передают параметр — true.
    // feedback — правка человека к текущему плану («Изменить план»): уходит планировщику
    // вместе с предыдущей версией (см. TeamPlanningService.BuildPlannerPrompt).
    public async Task<(TeamImplementPlan? Plan, string? Reason)> CreateTeamPlanAsync(
        string sessionId, string request, string? userId = null, CancellationToken ct = default,
        bool fromHuman = true, string? feedback = null)
    {
        if (_sessions.GetById(sessionId) is not { } session) return (null, "Чат не найден");
        var ownerId = _sessions.ResolveOwnerId(session);
        if (ownerId is null || (userId is not null && ownerId != userId)) return (null, "Чат не найден");
        if (session.TeamImplement is null) return (null, "Режим «Командная реализация» не включён");
        if (_planning is null) return (null, "Планирование недоступно");

        // Координатор = собеседник чата. Без персоны режим планировать не может —
        // фронт показывает пикер координатора при включении режима.
        if (_planning.ResolveCoordinator(session, ownerId) is null)
            return (null, "Выберите координатора — чат без персоны штабом быть не может");

        var candidates = _planning.ResolveCandidates(session, ownerId);
        if (candidates.Count == 0)
            return (null, session.ProjectId is null
                ? "Выберите исполнителей — вне проекта команды нет, и подбирать не из кого"
                : "В команде проекта нет персон — выберите исполнителей явно");

        var projectHint = session.ProjectId is { } pid ? _projects.GetById(pid)?.Name : null;
        // Перепланирование после интервью (Э8): планировщик получает предыдущую версию плана —
        // из неё он и выводит блок «Что изменилось». Не нашли карточку (чат чистили) — строим
        // с нуля: план без «что изменилось» лучше, чем отсутствие плана.
        var previous = session.TeamImplement is { Replanning: true, PlanCardId: { } prevId }
            ? await _sessions.GetTeamPlanAsync(sessionId, prevId)
            : null;
        // Планировщика резолвим тут же: фронту нужна его персона для карточки «Готовит план…»
        // в ленте. ResolvePlanner без побочных эффектов (тот же пул кандидатов, что уйдёт
        // в CreatePlanAsync ниже), так что лишнего запроса не добавляем
        var plannerPersonaId = _planning.ResolvePlanner(session, ownerId, candidates)?.Id;
        // Событие «планировщик запущен» сразу после резолва кандидатов: фронт рисует
        // «Штаб планирует…», а сам факт не путается с долгим молчанием (контракт для Киры).
        var empty = new TeamPlanningService.Result(null, TeamPlanningService.Failure.Failed, null, 0, 0, TimeSpan.Zero);
        await _coordinator.BroadcastTeamPlanningStartedAsync(sessionId, empty, plannerPersonaId);

        var planning = await _planning.CreatePlanAsync(session, ownerId, request, projectHint, ct, previous, feedback);
        if (planning.Plan is null)
        {
            // Событие «планировщик закончил» с отказом — фронт снимет спиннер и покажет
            // причину в плашке рядом с карточкой отказа (контракт для Киры, см. docs).
            await _coordinator.BroadcastTeamPlanningFinishedAsync(sessionId, planning, plannerPersonaId);
            return (null, TeamCoordinator.PlannerFailureReason(planning.Failure));
        }

        // План построен — сохранённая вводная и правка отказа отработаны (повтор по кнопке
        // «Повторить планирование» после успеха не нужен)
        _sessions.WithTeamState(sessionId, t => { t.LastPlanRequest = null; t.LastPlanFeedback = null; return true; });
        await _coordinator.BroadcastTeamPlanningFinishedAsync(sessionId, planning, plannerPersonaId);
        await PublishTeamPlanAsync(sessionId, session, planning.Plan, fromHuman);
        return (planning.Plan, null);
    }

    // Публикация карточки плана: история (переживает рестарт) + WS + стадия «ждёт подтверждения».
    // Добавочный план (Э5) при включённых авто-волнах подтверждения не ждёт: первоначальный
    // план итерации человек утверждает всегда, а для добавочного точкой контроля была сама
    // его вводная — карточка публикуется уже решённой, работа стартует сразу.
    // fromHuman (M7): «вводная человека» — буквально. Агентская вводная (chats_send в штаб),
    // классифицированная координатором как работа, авто-подтверждения НЕ получает: план
    // ждёт клика человека, как первоначальный — иначе единственное согласование обходится.
    public async Task PublishTeamPlanAsync(string sessionId, Session session, TeamImplementPlan plan,
        bool fromHuman)
    {
        // Версия плана (Э8): перепланирование после интервью даёт vN+1, обычная публикация —
        // v1 новой итерации. Автор карточки — планировщик НА МОМЕНТ публикации: карточка
        // рисуется как его речь и переживает смену координатора.
        var replanning = session.TeamImplement is { Replanning: true };
        plan.Version = replanning ? (session.TeamImplement?.PlanVersion ?? 0) + 1 : 1;
        plan.PlannerPersonaId ??= session.TeamImplement?.CoordinatorPersonaId ?? session.PersonaId;

        // Полный план файлом (решение владельца 2026-08-02): сервер рендерит markdown из
        // структуры плана и кладёт рядом с проектом — координатору писать файлы запрещено
        // (CoordinatorWriteGuard). Версия — отдельный файл: plan.Version уже проставлен выше,
        // поэтому перепланирование ложится рядом с предыдущим, не поверх него. Подпапка на
        // IterationNumber той же логикой разводит разные вводные одного чата (прод 2026-08-03).
        // Глобальный чат без проекта — писать некуда (null), карточка покажет только «Замысел»;
        // ошибка записи не должна ронять публикацию карточки — TryWrite её не бросает.
        if (_sessions.ResolveTeamPlanRoot(session) is { } planRoot)
        {
            var ownerIdForLabels = _sessions.ResolveOwnerId(session);
            plan.PlanFilePath = TeamPlanFileRenderer.TryWrite(planRoot, session.Name, sessionId,
                session.TeamImplement?.IterationNumber ?? 0, plan,
                personaId => personaId is not null && _personas.Get(personaId, ownerIdForLabels ?? "") is { } p
                    ? PersonaManager.PersonaLabel(p) : personaId ?? "не назначен", _log);
        }

        // Добавочный = в режиме уже был план (первый ставит PlanCardId). Отменённый план
        // обнуляет PlanCardId, поэтому после «Отменить» следующий снова требует подтверждения.
        // Перепланирование (Э8) добавочным НЕ считается: новую версию плана человек утверждает
        // всегда — авто-волны покрывают волны по неизменному плану, но не смену самого плана.
        // И M7: авто-подтверждение — только за вводной человека, агентская идёт через карточку.
        var additional = fromHuman && !replanning
            && session.TeamImplement is { PlanCardId: not null, AutoWaves: true, Stopped: false };
        if (additional) plan.Approved = true;

        // Развилка «активный vs диск» спрятана внутри шва SavePlanCardAsync.
        await _history.SavePlanCardAsync(sessionId, new PlanCardWriteRequest(plan,
            Resolved: additional, Approved: additional ? true : null, SupersededBy: null));

        // Страховка инварианта «перепланирование ⇒ старая карточка погашена»: обычно её
        // гасит вход в перепланирование (правка человека, clarify), но легаси-состояние могло
        // дойти до публикации и без него — у устаревшей версии не должно оставаться кнопок.
        if (replanning)
            await SupersedeCurrentPlanCardAsync(sessionId, session, plan.Version);

        if (session.TeamImplement is not null)
        {
            _sessions.WithTeamState(sessionId, t =>
            {
                t.PlanCardId = plan.Id;
                t.PlanVersion = plan.Version;
                // Перепланирование закончилось публикацией: дальше по этому плану идёт обычный
                // цикл, а признак снимаем — иначе следующая версия считалась бы от него же.
                t.Replanning = false;
                // Новый план — новый счёт волн итерации: без обнуления ClosedWave волна 1
                // добавочного плана считалась бы уже закрытой и никогда не закрылась бы снова.
                if (additional)
                {
                    t.Stage = TeamImplementStage.Wave;
                    t.WaveNumber = 0;
                    t.ClosedWave = 0;
                    t.PlannedWaves += plan.WaveCount;
                    // Добавочная волна при авто клика не ждёт: точкой контроля была сама
                    // вводная человека — значит эта версия плана и есть подтверждённая.
                    t.ApprovedPlanVersion = plan.Version;
                }
                else
                    t.Stage = TeamImplementStage.Confirming;
                return true;
            });
            // B1: добавочный план при авто-волнах согласования не ждёт — работа уже пошла,
            // а значит и режим прав человеку возвращается ЗДЕСЬ. Иначе SavedMode, поставленный
            // входом в интервью по этой же вводной, снять было бы негде (RestoreUserMode звался
            // только по клику «Запустить» и при выключении режима), и селектор оставался бы
            // залоченным «Штаб планирует…» до конца жизни чата.
            if (additional) _sessions.RestoreUserMode(sessionId);
            session.UpdatedAt = DateTime.UtcNow;
            _sessions.SaveSessions();
            await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        }
        await _sessions.BroadcastAsync(sessionId, new TeamPlanMessage(plan.Id, plan, additional,
            additional ? true : null));

        if (!additional) return;

        // Информационная карточка состава (Э5): работа уже пошла, поэтому у карточки одна
        // кнопка — «Остановить», и стадию режима она не двигает.
        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.WaveAdded,
            Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.WaveAdded,
                string.IsNullOrWhiteSpace(plan.Summary) ? plan.Request : plan.Summary),
            Details = TeamImplementPrompts.WaveAddedDetails(plan),
            Wave = session.TeamImplement?.WaveNumber ?? 0,
            Actions = TeamEscalationActions.For(TeamEscalationKind.WaveAdded),
        };
        if (_sessions.TeamEscalationRaiser is { } raise) await raise(session, card);
        else await _sessions.PublishTeamEscalationAsync(sessionId, card);

        // Раздача — тем же путём, что «Запустить» и авто-волна: план у TeamWaveService.
        // Повод UserCommand: добавочная волна разворачивается вводной человека — точки
        // контроля уже пройдены, гейт авто-волн ей не нужен.
        if (_sessions.TeamWaveStarter is { } starter)
        {
            try { await starter(session, plan, TeamWaveTrigger.UserCommand); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Раздача добавочной волны по плану {PlanId} (чат {SessionId}) не удалась",
                    plan.Id, sessionId);
            }
        }
    }

    // Погасить ТЕКУЩУЮ карточку плана как заменённую версией nextVersion. Зовётся при
    // входе в перепланирование (правка человека кнопкой или маркер работы в подтверждении)
    // и страховочно при публикации новой версии: у устаревшей карточки не должно оставаться
    // живых кнопок вовсе — гард M8 ловит клик, но человек не должен его делать.
    // Идемпотентна: уже разрешённую карточку (запуск/отмена/повторный вход) не трогает.
    public async Task SupersedeCurrentPlanCardAsync(string sessionId, Session session, int nextVersion)
    {
        if (session.TeamImplement is not { PlanCardId: { } oldId }) return;

        var plan = await _sessions.GetTeamPlanAsync(sessionId, oldId);
        if (plan is null) return;

        // Сохранить новые поля карточки + SupersededBy внутри шова (развилка спрятана).
        var changed = await _history.SavePlanCardAsync(sessionId, new PlanCardWriteRequest(plan,
            Resolved: true, Approved: false, SupersededBy: nextVersion));
        if (!changed) return;

        await _sessions.BroadcastAsync(sessionId, new TeamPlanMessage(oldId, plan, true, false, nextVersion));
    }

    // Гасим устаревшую карточку и объясняем человеку, почему решение по ней не сработало.
    // Стадию, версии и режим прав НЕ трогаем: практика живёт по актуальному плану, а этот
    // клик — по карточке из прошлого. Молчать нельзя (правило «молчаливых пауз не бывает»):
    // человек нажал кнопку и обязан узнать, что она больше ни к чему не ведёт.
    public async Task ResolveStalePlanCardAsync(string sessionId, Session session,
        SessionTeamImplement team, string planId, TeamImplementPlan plan)
    {
        plan.Approved = false;
        var changed = await _history.SavePlanCardAsync(sessionId, new PlanCardWriteRequest(plan,
            Resolved: true, Approved: false, SupersededBy: null));
        if (!changed) return;

        await _sessions.BroadcastAsync(sessionId, new TeamPlanMessage(planId, plan, true, false));

        var text = TeamImplementPrompts.StalePlanCardNotice(plan.Version, team.PlanVersion);
        // Пояснение идёт репликой планировщика — карточка плана рисуется его речью, и ответ
        // про её устаревание логично слышать от него же. Персоны нет (режим включён у чата
        // без собеседника — возможно у состояний до гарда B2): канала для реплики нет,
        // ограничиваемся гашением карточки и логом.
        var personaId = team.PlannerPersonaId ?? team.CoordinatorPersonaId ?? session.PersonaId;
        _log.LogInformation("Решение по устаревшей карточке плана {PlanId} (v{Version} при актуальной v{Current}) " +
            "в чате {SessionId} отклонено", planId, plan.Version, team.PlanVersion, sessionId);
        if (personaId is null) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _history.AppendAsync(sessionId, new StoredTextMessage(text, personaId: personaId, timestamp: ts),
            new GuestTextMessage(text, personaId, ts));
    }

    // Сохранить карточку плана в историю чата после правки бэкендом (Э3 проставляет
    // TeamImplementSubtask.TaskId). Карточка уже Resolved, поэтому обновляем её напрямую:
    // шов SavePlanCardAsync сам находит карточку по PlanId и применяет мутатор под локом.
    public async Task SaveTeamPlanCardAsync(string sessionId, TeamImplementPlan plan)
    {
        // Молча выйти нельзя: раздача волны проставляет под-задачам TaskId, и без записи
        // следующее чтение плана с диска увидело бы их нерозданными и создало дубли задач.
        await _history.SavePlanCardAsync(sessionId, new PlanCardWriteRequest(plan,
            Resolved: false, Approved: null, SupersededBy: null));
    }

    // Собственно планирование: вводная (и правка к плану) сохраняются для повтора, зовётся
    // планировщик, а при отказе публикуется карточка с причиной и кнопкой повтора. Гардов
    // по стадии нет — состояние готовит вызывающий (StartTeamWorkAsync для вводной,
    // RespondTeamPlanAsync для правки «Изменить план», retryPlan для повтора после сбоя).
    // Молчаливых тупиков не бывает ни в одном исходе: успех даёт карточку плана, сбой и
    // таймаут — карточку отказа.
    public async Task RunTeamPlanningAsync(string sessionId, string request, string? feedback,
        bool fromHuman)
    {
        if (_sessions.GetById(sessionId) is not { } session) return;
        if (session.TeamImplement is not { } team) return;

        // Вводная и правка сохраняются на состоянии ДО планировщика: при его отказе человек
        // сможет повторить планирование кнопкой карточки, не проходя интервью заново и не
        // теряя правку (повтор обязан пересобирать план по той же правке).
        _sessions.WithTeamState(sessionId, t =>
        {
            t.LastPlanRequest = request;
            t.LastPlanFeedback = feedback;
            return true;
        });

        var isEdit = !string.IsNullOrWhiteSpace(feedback);
        // Флаг живого планирования: гард молчаливого тупика по концу хода (см.
        // HandleTeamTurnEndAsync) не поднимает тревогу, пока планировщик реально строит план.
        _run.SetPlanningInFlight(sessionId, true);
        try
        {
            var (plan, reason) = await CreateTeamPlanAsync(sessionId, request,
                fromHuman: fromHuman, feedback: feedback);
            if (plan is not null) return;

            // Молчаливых тупиков в режиме не бывает: человек ждёт план — значит про
            // несостоявшийся план он должен узнать карточкой, а не по тишине. Таймаут
            // планировщика — отдельный случай: причина не в постановке человека, и текст
            // карточки называет её как есть. У правки текст свой: старая карточка уже
            // погашена как заменённая, и без карточки отказа человек остался бы вообще без плана.
            // Обрыв по токенам и невалидный JSON — третья и четвёртая ветки (прод 2026-08-05):
            // совет другой, текст другой, без подмены «таймаут».
            var (title, details) = isEdit
                ? TeamCoordinator.EditFailureText(feedback!, reason)
                : TeamCoordinator.FreshFailureText(request, reason);
            var failed = new TeamEscalation
            {
                Kind = TeamEscalationKind.ProductDecision,
                Title = title,
                Details = details,
                Wave = team.WaveNumber,
                // Кнопка повторяет планирование по сохранённой вводной и правке (retryPlan в
                // RespondTeamEscalationAsync) — без хода координатору и без повторного интервью.
                Actions = [new TeamEscalationAction("retryPlan", "Повторить планирование")],
            };
            if (_sessions.TeamEscalationRaiser is { } raise) await raise(session, failed);
            else await _sessions.PublishTeamEscalationAsync(sessionId, failed);
        }
        finally
        {
            // Сброс идёт по sessionId, а не по захваченному session: если чат удалили за время
            // планирования (потолок 300 с), сбрасывать нечего и не нужно — удалённый session
            // в словарь уже не вернётся (создание всегда новый), а флаг читают ТОЛЬКО через
            // него (гард тупика, sweep через IsSessionBusy).
            _run.SetPlanningInFlight(sessionId, false);
        }
    }
}