using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Team;

// === Четыре шва данных «штаб → ядро сессии» ===
// Этап 4, шаг 2г-3б плана выноса штаба (docs/research/session-core-split-2026-09.md, §5(4)).
// Заводятся ОТДЕЛЬНЫМ шагом, без переноса блоков тела: спор о форме шва отделён от риска
// переезда — тот же приём, что в шаге 2б с ITeamNotifier.
//
// Направление здесь обратное ITeamNotifier: там ядро уведомляет вертикаль, здесь вертикаль
// спрашивает ядро. Общее у них одно — контракт объявлен в вертикали, реализация в ядре, и это
// не одиннадцатый делегат: реализация интерфейса видна сторожу границ Implements-ребром, а
// метод в поименованном контракте — review-абельная единица (присвоение Func-поля не видно ни
// рефлексии, ни IL-скану — так их и стало десять в LlmSessionContext).
//
// Реализация ПОКА внутри SessionManager обёрткой над прежними приватными методами (явные
// реализации в конце штабного блока, поведение один в один). В шаге 2г тело штаба переедет в
// вертикаль, и штаб будет получать эти четыре интерфейса через DI, а не upcast'ом.
//
// Почему ровно четыре и почему пятого нет: каталог персон швом не оформляется — вертикаль
// берёт PersonaManager напрямую через DI, а он уже отдаёт объект Persona (Get(id, userId)) и
// умеет перечислять по проекту (GetForContext(userId, projectId)). Требование ревью швов
// («каталог персон обязан отдавать Persona и перечислять по проекту, а не string GetName(id)»)
// выполнено без нового интерфейса.

/// <summary>
/// Что штабу нужно знать о чате. Отдельный снимок, а не сама <see cref="Session"/>: 128
/// обращений вертикали через <c>entry.Info</c> — не повод протаскивать наружу 40-польную
/// персистентную модель (тот же довод, что у <c>DesktopChatInfo</c>). Состав — строго по
/// фактической потребности переведённых точек: тишина задачи по чату-исполнителю, признак
/// занятости исполнителя, якоря активности дочерних чатов волны.
/// </summary>
internal sealed record TeamSessionInfo(
    string Id,
    string? ParentSessionId,
    string? ProjectId,
    string? OwnerId,
    SessionStatus Status,
    DateTime UpdatedAt);

/// <summary>
/// Шов 1 — каталог сессий снимком. Реализация ядра остаётся единственным местом, которое
/// знает <c>SessionEntry</c>: вертикаль не получает ни его, ни сам словарь сессий (это была бы
/// общая память двух подсистем — доступ к 40 полям, включая пять примитивов синхронизации).
/// </summary>
internal interface ITeamSessionDirectory
{
    /// <summary>Снимок чата, либо null — чата больше нет.</summary>
    TeamSessionInfo? Get(string sessionId);

    /// <summary>
    /// Дочерние чаты-исполнители одного штаба. <c>ParentSessionId</c> вычисляемый (из
    /// <c>Task.SourceSessionId</c>), поэтому фильтр живёт на стороне ядра.
    /// </summary>
    IReadOnlyList<TeamSessionInfo> ListChildren(string parentSessionId);

    /// <summary>
    /// Дети ВСЕХ штабов одним проходом — для тика пульса волн: собирать их внутри каждого
    /// снапшота значило бы полный перебор чатов на каждый штаб (O(N×M)).
    /// </summary>
    ILookup<string, TeamSessionInfo> ChildrenByParent();

    /// <summary>
    /// Сбросить каталог сессий на диск. Вызывающий код уже обновил нужные поля на
    /// снимке/через публичный API; метод только персистит (SaveSessions). Идемпотентен
    /// и безопасен под concurrent: реализация сама держит свой лок. Достройка шага
    /// 2г-3в (волна А): пять блоков штаба из отчёта разведки, включая SaveTeamImplementState
    /// и будущие волны с правкой каталога, идут через этот метод. Шов не пятый — это
    /// метод в уже заведённый контракт, потребность «сохранить каталог» закрыта один раз.
    /// </summary>
    void Persist();
}

/// <summary>
/// Шов 2 — история чата. Транзакционный, а НЕ набор геттеров, и причина механическая: правка
/// карточки у неактивного чата (аккумулятора ещё нет — обычное дело сразу после рестарта) есть
/// read-modify-write файла истории, и идёт она под общим <c>_falPersistLock</c> вместе со всеми
/// внеходовыми записями. Без общего лока двойной клик по карточке применился бы дважды:
/// удвоенная прибавка бюджета и двойная раздача волны. Отдать вертикали сам <c>SemaphoreSlim</c>
/// нельзя — это утечка примитива синхронизации через границу, худший вид шва; значит лок
/// остаётся внутри реализации, а наружу выходит операция целиком.
/// </summary>
internal interface ITeamHistoryStore
{
    /// <summary>
    /// Найти последнюю карточку типа <typeparamref name="T"/>, удовлетворяющую
    /// <paramref name="match"/>, и изменить её ПОД ЛОКОМ внеходовых записей. false — карточки
    /// нет, чат без транскрипта либо запись не удалась (причина уходит в лог реализации).
    /// Мутатор обязан быть синхронным и коротким: он выполняется под локом.
    /// </summary>
    Task<bool> MutateCardAsync<T>(string sessionId, Func<T, bool> match, Action<T> mutate)
        where T : StoredMessage;

    /// <summary>
    /// Дописать сообщение в историю и разослать его в ленту. Вторая половина того же лока:
    /// внеходовая запись сериализуется с правкой карточек, иначе снимок хода и файл истории
    /// разъезжаются.
    /// </summary>
    Task AppendAsync(string sessionId, StoredMessage stored, ServerMessage broadcast);

    /// <summary>
    /// Сохранить или обновить карточку плана с правильным персистом в обеих ветках
    /// «активный аккумулятор» против «диск». Это второй метод шва (волна В плана выноса
    /// штаба): размазанная развилка из шести точек ушла внутрь реализации.
    ///
    /// Семантика <paramref name="request"/>:
    /// <list type="bullet">
    ///   <item><c>Resolved=false, SupersededBy=null</c> — append (публикация новой карточки).</item>
    ///   <item><c>SupersededBy=X</c> — правка с <c>Resolved=true, Approved=false, SupersededBy=X</c>.</item>
    ///   <item><c>Resolved=true, Approved=…</c> — правка с <c>Resolved=true, Approved=…</c>.</item>
    ///   <item><c>Resolved=false</c> — простая правка <c>Plan</c>, флаг <c>Resolved</c> остаётся прежним.</item>
    /// </list>
    /// Активный чат → <c>Accumulator.OnTeamPlan/OnTeamPlanUpdated/OnTeamPlanSuperseded</c> +
    /// FireAndForget <c>SaveSnapshotAsync</c>; неактивный → <c>LoadAsync</c> + append/mutate +
    /// <c>SaveAsync</c> под <c>_falPersistLock</c>. true — карточка добавлена/найдена и обновлена,
    /// false — чата нет, нет транскрипта или карточка не найдена (причина уходит в лог реализации).
    /// </summary>
    Task<bool> SavePlanCardAsync(string sessionId, PlanCardWriteRequest request);
}

/// <summary>
/// Параметры правки карточки плана для <see cref="ITeamHistoryStore.SavePlanCardAsync"/>.
/// Подробнее о комбинациях — в xml-комментарии к методу.
/// </summary>
internal sealed record PlanCardWriteRequest(
    TeamImplementPlan Plan,
    bool Resolved,
    bool? Approved,
    int? SupersededBy);

/// <summary>
/// Шов 3 — рантайм-состояние прогона и собственные рантайм-поля штаба. Живут они в приватном
/// <c>SessionEntry</c> (в памяти, не в файле): <c>TeamPlanningInFlight</c>,
/// <c>TeamTurnFromHuman</c> и признаки живости прогона. Часть из них штабу нужна на запись —
/// поэтому шов не read-only. Обратное направление (ядро спрашивает штаб «занят ли он»)
/// закрыто отдельно: <see cref="ITeamNotifier.IsSessionBusy"/>.
/// </summary>
internal interface ITeamRunState
{
    /// <summary>
    /// Живой прогон CLI: ход идёт либо принят адаптером и вот-вот начнётся. Для пульса волны
    /// это «активность сейчас» — UpdatedAt-якоря на длинном ходу статичны, и честная
    /// 35-минутная сборка исполнителя без этого признака читалась бы как зависание.
    /// </summary>
    bool HasLiveTurn(string sessionId);

    /// <summary>
    /// У прогона остались фоновые агенты (ход отдал текст, но Tool-вызов ещё жив). Гард
    /// молчаливого тупика такой ход тупиком не считает: координатор ждёт своего результата.
    /// </summary>
    bool HasAsyncAgent(string sessionId);

    /// <summary>
    /// Есть ли зритель у чата. Человек, который прямо сейчас в чате, и так видит карточку —
    /// повторные напоминания и push ему не нужны.
    /// </summary>
    bool HasViewers(string sessionId);

    /// <summary>
    /// Текущий ход поднял человек, а не агент. Признак однонаправленный («на будущее внутри
    /// того же хода»): пишет его ядро на приёме хода, читает штаб при планировании.
    /// </summary>
    bool TurnStartedByHuman(string sessionId);

    /// <summary>Идёт ли планирование штаба прямо сейчас (потолок 300 с без прогона CLI).</summary>
    bool IsPlanningInFlight(string sessionId);

    /// <summary>
    /// Отметить начало и конец планирования. По этому признаку молчат сразу двое: гард
    /// молчаливого тупика по концу хода и sweep зависших сессий — второй через
    /// <see cref="ITeamNotifier.IsSessionBusy"/>.
    /// </summary>
    void SetPlanningInFlight(string sessionId, bool inFlight);

    /// <summary>
    /// Сменить permission-mode живому CLI-прогону (control-протокол set_permission_mode).
    /// Зовётся из вертикали штаба при входе в план-фазу (Э5) и при возврате режима
    /// человека после согласования плана — без него режим применился бы только к
    /// следующему ходу, а идущий остался бы в прежнем. Реализация в ядре: оно знает про
    /// <c>entry.Process</c> (живой <c>ILlmSessionAdapter</c>).
    /// </summary>
    void TrySetPermissionModeLive(string sessionId, ClaudeMode mode);

    /// <summary>
    /// Установить <c>entry.Info.Mode = mode</c>, отправить <c>set_permission_mode</c>
    /// живому процессу и взвести <c>entry.AdapterStale</c> при его наличии — тройная
    /// синхронизация для включения режима штаба (волна Ж, достройка ITeamRunState).
    /// Гард «координатор не пишет код» (CoordinatorWriteGuard) пересекается с
    /// <c>--disallowedTools</c>: первый режет на приёме permission, второй — на
    /// создании адаптера. Без AdapterStale правка доехала бы только до следующего
    /// пересоздания процесса, а живой ход остался бы с прежним набором инструментов.
    /// Реализация — обёртка над entry, потому что три поля лежат в SessionEntry.
    /// </summary>
    void TrySetEntryModeLiveAndStaleAdapter(string sessionId, ClaudeMode mode);

    /// <summary>
    /// Транзакция над SessionTeamImplement (штабные рантайм-поля): единственный способ
    /// править счётчики бюджета и попытки под-задач. Точки записи разнесены по потокам
    /// (раздача волны из колбэка завершения задачи, перевыдача из колбэка провала хода,
    /// квота из HTTP-фильтра), а <c>int++</c> не атомарен — частичный лок означал бы
    /// потерянные инкременты и нечестный счёт ровно там, ради чего Э4 и делался.
    /// Реализация хранит собственный лок-словарь и доступ к SessionTeamImplement через
    /// снимок ядра — вертикаль не видит ни SessionEntry, ни ConcurrentDictionary, ни
    /// сам объект блокировки. Достройка шага 2г-3з (волна Е): квоты бюджета практики
    /// переехали в вертикаль, и шов нужен TeamBudgetService (TryConsume/Refund).
    /// </summary>
    T? WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate);

    /// <summary>
    /// Положить план вызова штабного разбора по ключу turnSeq (text/failed/asked).
    /// Достройка шага 2г-3и (волна Ж): подписчик turn/completed переехал в вертикаль
    /// TeamTurnCompletionService, и ему нужен доступ к LastTeamTurnEnds без прямого
    /// входа в SessionEntry (это утечка 40-полейной персистентной модели). Ядро
    /// пишет план на терминале хода (ResultMessage/ErrorMessage), подписчик забирает
    /// — оба идут через этот шов, чтобы вертикаль не видела SessionEntry.
    /// Потолок 8 записей, ключ TurnSeq, симметричные LastTurnTexts правила (первая
    /// запись выигрывает, повторная не затирает непустую). turnSeq &lt;= 0 — невалидный
    /// ключ, метод no-op.
    /// </summary>
    void RecordTeamTurnEnd(string sessionId, int turnSeq, string? text, bool failed, bool asked);

    /// <summary>
    /// Изъять план вызова штабного разбора по ключу turnSeq. true и заполненные out'ы —
    /// ключ найден (запись при этом удаляется атомарно, под TeamTurnLock). false — записи
    /// нет (потолок вытеснил, либо OnMessageAsync ещё не положил). Достройка шага 2г-3и
    /// (волна Ж) — парный к RecordTeamTurnEnd.
    /// </summary>
    bool TryTakeTeamTurnEnd(string sessionId, int turnSeq,
        out string? text, out bool failed, out bool asked);
}

/// <summary>
/// Шов 4 — приём хода. Штаб не поднимает процесс сам: он отдаёт текст ядру, а тот либо
/// стартует ход, либо ставит его в очередь идущего. Прерывание здесь же — это та же ось
/// управления ходом, только с другого конца.
/// </summary>
internal interface ITeamTurnIntake
{
    /// <summary>
    /// Отправить ход в чат либо поставить в очередь, если чат занят. true — ход поднят,
    /// false — встал в очередь. Штабные ходы всегда silent + suppressTasksExecute: в ленте
    /// человек видит плашку механики, а не пузырь с сырой директивой.
    /// </summary>
    Task<bool> SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId = null, bool silent = false,
        bool suppressTasksExecute = false, string? staffNote = null);

    /// <summary>
    /// Прервать ход чата: зависший статус реанимируется, живой зависший прогон убивается.
    /// Нужно перед перевыдачей под-задачи — иначе она упрётся в гейт «по задаче уже работает
    /// сессия».
    /// </summary>
    void InterruptTurn(string sessionId);
}
