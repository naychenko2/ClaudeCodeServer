using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Фолбэк выбора модели при рантайм-ошибках доставки (ADR «Порядок резолва модели,
// классы ошибок фолбэка, защита от зацикливания» §2–4). Декоратор адаптера сессии:
// видит ВСЕ события хода через перехват OnMessage и при ошибке фолбэк-класса
// перезапускает ход на другой паре «модель × подписка»:
//
//   Уровень 1 — та же модель, другая подписка ТОГО ЖЕ пула: существующие механизмы
//               ротации (MarkExhausted → Pick → перенос транскрипта), новых не заводится;
//   Уровень 2 — следующий шаг цепочки хода (пресет слота тира, ADR-007 §4).
//               Алфавитного автоподбора провайдеров больше нет: ход без хвоста цепочки
//               после пула завершается честной ошибкой.
//
// Защита от зацикливания: каждая пара пробуется не более одного раза за ход
// (HashSet попыток), потолок подмен задаётся через FallbackSettingsStore
// (per-owner → global → дефолт), значение клампится в 1..HardMaxSubstitutions.
//
// SessionManager о подменах не знает: терминальные сообщения неудачных попыток
// задерживаются здесь, наружу уходит только финальный исход — статус, аккумулятор
// истории и наблюдатели задач видят ход одним целым. При исчерпании цепочки финал
// всегда ошибочный result: у задачи исполнителя существующий путь TaskExecutionService
// помечает сбой и уведомляет постановщика, а след перепробованных пар остаётся
// в ленте чата (финальный ErrorMessage + маркеры provider_switched).
//
// Промежуточные ошибки провайдера (529 и т.п.) задерживаются вместе с result попытки:
// подмена состоялась — красной карточки в ленте нет вовсе, сырой текст переезжает в
// ProviderSwitchedMessage.ErrorDetails; подмены не было — ошибка уходит downstream перед
// финальным result. Красная карточка остаётся только там, где ход реально не состоялся.
public sealed class FallbackLlmSessionAdapter : ILlmSessionAdapter
{
    private readonly ILlmSessionAdapter _inner;
    private readonly Func<string?> _effectiveModel;
    private readonly Func<ServerMessage, Task> _downstream;
    private readonly ClaudeSubscriptionPool _pool;
    private readonly LlmProviderRegistry? _providers;
    private readonly string _rootPath;
    private readonly Execution.IProcessLauncher? _launcher;
    // Стор настроек фолбэка. Жёсткий потолок (FallbackSettingsStore.HardMaxSubstitutions)
    // сохраняется; конкретное значение читается per-owner → global → дефолт каждый ход
    // заново (админская правка в UI применяется без рестарта). null — в тестах без DI,
    // читаем дефолт напрямую.
    private readonly FallbackSettingsStore? _fallbackSettings;
    // Цепочка хода (ADR-007 §4): упорядоченные конкретные модели пресета (первая = основная,
    // остальные = план фолбэка). null/один элемент — цепочки нет (существующий автоподбор).
    // Вычисляется вызывающим (фабрикой через ClaudeSession.EffectiveTurnChain) на каждом ходу.
    private readonly Func<IReadOnlyList<string>>? _effectiveChain;
    // Кулдаун недоступности провайдера (волна 2): провайдер, вернувший Unreachable/ProviderError,
    // помечается недоступным на TTL; фолбэк пропускает его шаги цепочки (fail-open). null (тесты).
    private readonly ProviderHealthRegistry? _health;
    // Наблюдаемая ёмкость окна модели (ContextOverflow): модель, не принявшая контекст размера N,
    // не получает последующие ходы с контекстом ≥ N. Заявленный ContextWindow — первичная оценка,
    // наблюдение — уточнение (конфиг расходится с фактическим лимитом тарифа). null (тесты) — fail-open.
    private readonly ContextCapacityRegistry? _capacity;
    // Оценка размера контекста текущего хода (ClaudeSession.LastContextTokens): по ней оркестратор
    // отсеивает кандидатов цепочки с заведомо меньшим окном. Передаётся в ResolveNextTarget ВСЕГДА,
    // а WouldFit различает источники ёмкости по достоверности: наблюдённый потолок (точный факт
    // отказа) отсеивает при ЛЮБОМ классе ошибки, заявленное окно каталога — только при
    // ContextOverflow (Major 3 ревью). null (тесты) — оценка 0, фильтр по ёмкости выключен.
    private readonly Func<int>? _lastContextTokens;
    // Источник оценки контекста для диагностики («живая» / «из истории» / «нет»): параллельный
    // Func<string>, собираемый фабрикой из того же замыкания, что и _lastContextTokens. Без него
    // очередной провал оценки пришлось бы снова ловить по «~0 токенов» в проде. null (тесты без
    // фабрики) — «нет». Контракт Func<int> оценки не меняется — это отдельный диагностический сигнал.
    private readonly Func<string>? _contextSource;
    // Лог подмен: без него оркестрацию нечем отлаживать на стенде (что классифицировали,
    // куда переключились, почему кандидат отвергнут). null (тесты без DI) — пишем в
    // Console.Error, как делал код до появления логгера.
    private readonly ILogger? _log;
    // Колбэк персиста сессий (SessionManager.SaveSessions через фабрику/контекст): нужен,
    // чтобы переписать sessions.json ВОССТАНОВЛЕННЫМИ значениями после restore в finally —
    // иначе финальный result успевает сохранить подменённую модель (ApplyStatusAsync →
    // SaveSessions ДО restore), и рестарт сервера в этом окне залипил бы чат на подмене
    // (возврат инцидента 2026-08-07). null (тесты без DI) — персист не вызывается.
    private readonly Action? _persist;
    // Requeue хода в серверную Pending взамен байпаса в _inner (null — тесты: откат к байпасу).
    private readonly Func<string, string, IReadOnlyList<string>?, int, bool, Task>? _enqueueBypass;
    // Сигнал «оркестрация завершена» в finally → SessionManager запускает разбор Pending (null — тесты).
    private readonly Action<string>? _orchestrationDone;
    // Корень профиля CLI на момент старта сессии (хостовый путь): источник для
    // переноса транскрипта и, у container-пользователя, способ вывести раскладку
    // песочных профилей (родитель = data/sandbox-profiles/{ownerId}).
    private readonly string? _initialProfileRoot;
    // Корень профиля ТЕКУЩЕЙ подписки/провайдера — обновляется каждой подменой
    private string? _profileRoot;
    private readonly CancellationTokenSource _cts = new();

    // Активная оркестрация фолбэка (null — сообщения проходят насквозь)
    private FallbackTurn? _turn;
    private readonly object _gate = new();
    // Был ли requeue обхода в серверную Pending за текущую оркестрацию (finally триггерит drain
    // только когда есть что разбирать — иначе лишний drain на каждом ходе поверх штатного по result).
    private int _bypassRequeued;
    private volatile bool _userInterrupted;
    // Снимок MessageCount перед ходом: оркестратор восстанавливает счётчик в конце,
    // чтобы повторы не раздували «сообщения пользователя» (одно user-сообщение = +1)
    private int _snapshotMessageCount;

    public FallbackLlmSessionAdapter(
        ILlmSessionAdapter inner,
        Func<string?> effectiveModel,
        Func<ServerMessage, Task> downstream,
        ClaudeSubscriptionPool pool,
        LlmProviderRegistry? providers,
        string rootPath,
        Execution.IProcessLauncher? launcher,
        string? initialProfileRoot,
        FallbackSettingsStore? fallbackSettings = null,
        Func<IReadOnlyList<string>>? effectiveChain = null,
        ProviderHealthRegistry? health = null,
        ILogger? log = null,
        Action? persist = null,
        ContextCapacityRegistry? capacity = null,
        Func<int>? lastContextTokens = null,
        Func<string, string, IReadOnlyList<string>?, int, bool, Task>? enqueueBypass = null,
        Action<string>? orchestrationDone = null,
        Func<string>? contextSource = null)
    {
        _inner = inner;
        _effectiveModel = effectiveModel;
        _downstream = downstream;
        _pool = pool;
        _providers = providers;
        _rootPath = rootPath;
        _launcher = launcher;
        _initialProfileRoot = initialProfileRoot;
        _fallbackSettings = fallbackSettings;
        _effectiveChain = effectiveChain;
        _health = health;
        _capacity = capacity;
        _lastContextTokens = lastContextTokens;
        _contextSource = contextSource;
        _log = log;
        _persist = persist;
        _enqueueBypass = enqueueBypass;
        _orchestrationDone = orchestrationDone;
        _profileRoot = initialProfileRoot ?? ResolveRootFor(CurrentProviderKey());
    }

    // Ход сейчас под фолбэк-оркестрацией. По этому признаку SessionManager отходит в
    // сторону на rate_limit_event: ротацией подписок владеет оркестратор (M1 — на одно
    // событие реагирует ровно один механизм, см. комментарий у обработчика RateLimitMessage).
    public bool FallbackTurnActive => _turn is not null;

    // Оркестрация активна (ход под фолбэком) — SessionManager гейтит этим форсированный drain
    // серверной очереди: ходы, вернувшиеся из-под активной оркестрации через EnqueueBypass,
    // должны ждать её завершения, а не крутить цикл drain↔requeue (инцидент 2026-08-10 П3).
    public bool OrchestrationActive => _turn is not null;

    // Единая точка логирования: с DI — обычный ILogger, без него (тесты, ручная сборка
    // адаптера) — Console.Error, чтобы диагностика не пропадала совсем
    private void LogWarn(string message)
    {
        if (_log is not null) _log.LogWarning("[ModelFallback] {FallbackStep}", message);
        else Console.Error.WriteLine($"[ModelFallback] {message}");
    }

    // sid сессии — при двух параллельных фолбэк-сагах (два чата одновременно) лог без него
    // нечитаем: строки Подмена/Переполнение/Исчерпание перемешиваются без признака владельца.
    private void LogInfo(string message) => _log?.LogInformation("[ModelFallback] sid={Sid} {FallbackStep}", Info.Id, message);

    private void LogDebug(string message) => _log?.LogDebug("[ModelFallback] {FallbackStep}", message);

    // Эффективный потолок подмен для владельца сессии: per-owner → global → дефолт 3
    // (FallbackSettingsStore.ClampMaxSubstitutions). Info.OwnerId может быть пуст у
    // проектных сессий без владельца — тогда читаем global-слой.
    private int EffectiveMaxSubstitutions() =>
        _fallbackSettings?.ResolveMaxSubstitutions(Info.OwnerId)
        ?? FallbackSettingsStore.DefaultMaxSubstitutions;

    public Session Info => _inner.Info;
    public LlmCapabilities Capabilities => _inner.Capabilities;
    public int CurrentTurnAgentDepth => _inner.CurrentTurnAgentDepth;
    public bool CurrentTurnSuppressTasksExecute => _inner.CurrentTurnSuppressTasksExecute;
    public bool HasLiveTurn => _inner.HasLiveTurn;

    public bool HasQueuedTurn => _inner.HasQueuedTurn;
    // Фоновые задачи живут в обёрнутом ClaudeSession-прогоне — делегируем (P12/P15: фолбэк
    // запускает ход через inner CLI, pending bg у inner).
    public bool HasPendingBg => _inner.HasPendingBg;
    // Продолжение живёт в обёрнутом ClaudeSession-прогоне — делегируем (как HasPendingBg).
    public bool IsContinuationInFlight => _inner.IsContinuationInFlight;
    public long SubmittedTurnSeq => _inner.SubmittedTurnSeq;

    public Task StartAsync() => _inner.StartAsync();
    public Task CompactAsync() => _inner.CompactAsync();
    public void RespondPermission(string requestId, string behavior) => _inner.RespondPermission(requestId, behavior);
    public void AnswerQuestion(string toolUseId, string updatedInputJson) => _inner.AnswerQuestion(toolUseId, updatedInputJson);
    public void RespondPlan(string requestId, bool approve, string? feedback) => _inner.RespondPlan(requestId, approve, feedback);
    public bool TrySetPermissionModeLive(ClaudeMode mode) => _inner.TrySetPermissionModeLive(mode);
    public bool TrySetModelLive(string model) => _inner.TrySetModelLive(model);

    public void Interrupt()
    {
        // Остановка пользователем — не ошибка доставки: фолбэк на этом ходу запрещён
        _userInterrupted = true;
        _inner.Interrupt();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _inner.DisposeAsync();
    }

    // Запуск хода с фолбэк-оркестрацией. Возвращаемся сразу (как базовый адаптер):
    // цикл живёт в фоне и держит ход в своих руках до финального исхода.
    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
        int agentDepth = 0, bool suppressTasksExecute = false)
    {
        lock (_gate)
        {
            // Оркестрация уже идёт (нештатно: SessionManager считает чат свободным — результат
            // хода ушёл downstream, статус Active, — а адаптер ещё держит _turn в окне между
            // SettleAsync и finally). Раньше тут был байпас — ход уходил в _inner (невидимая
            // очередь _turnLock ClaudeSession) мимо серверной Pending, без дедупа и фолбэк-защиты:
            // гонка same-process давала ложный Unreachable (П1), а повторные доставки — дубли
            // (П3). Теперь возвращаем ход в серверную очередь Pending: он дождётся конца
            // оркестрации (finally триггерит drain) и пойдёт под штатным фолбэком как новый _turn.
            // EnqueuePendingAsync дедупит Agent-сообщения по text+persona — повторный requeue
            // того же хода безопасен (Duplicate). Колбэк не настроен (тесты без SessionManager) —
            // откат к прежнему байпасу, чтобы не терять ходы.
            if (_turn is not null)
            {
                if (_enqueueBypass is not null)
                {
                    Interlocked.Exchange(ref _bypassRequeued, 1);
                    LogWarn($"Доставка при активной оркестрации — возврат в серверную очередь Pending (session {Info.Id}, «{Truncate(text, 60)}»). " +
                            "Ход дождётся конца оркестрации и пойдёт под штатным фолбэком.");
                    return _enqueueBypass(Info.Id, text, attachedPaths, agentDepth, suppressTasksExecute);
                }
                LogWarn($"Доставка при активной оркестрации — байпас в _inner (session {Info.Id}, «{Truncate(text, 60)}»): колбэк requeue не настроен (тесты).");
                return _inner.SendMessageAsync(text, attachedPaths, agentDepth, suppressTasksExecute);
            }
            _turn = new FallbackTurn();
        }
        _userInterrupted = false;
        _snapshotMessageCount = Info.MessageCount;
        var turn = _turn;
        _ = Task.Run(() => RunFallbackLoopAsync(turn, text, attachedPaths ?? [], agentDepth, suppressTasksExecute));
        return Task.CompletedTask;
    }

    // Перехват событий хода: терминальные сообщения попытки задерживаются до решения
    // оркестратора (провал+повтор / финал), остальные идут downstream сразу.
    internal Task HandleMessageAsync(ServerMessage msg)
    {
        var turn = _turn;
        if (turn is null)
        {
            // Осиротевший терминал завершённого прогона. _turn=null значит, что оркестрация
            // фолбэка снята — SettleAsync/FailExhausted отработали и финал хода (Result/Exited)
            // УЖЕ ушёл downstream, а SessionManager получил его и разобрал очередь по первому
            // терминалу. Поздний ExitedMessage доживающего/завершённого процесса (приходит с
            // опозданием до ~30 мин, либо как «хвост» после штатного result) тут — избыточный
            // повторный триггер разбора очереди (симптом 2: дублирующиеся авто-ходы
            // «Персона-исполнитель завершила»). В SessionManager эту же природу лечит
            // DrainOnExitedRun (привязка к RunId: после первого терминала сбрасывается, и поздний
            // exited чужого прогона не триггерит), но здесь — второй рубеж: глотаем ExitedMessage,
            // чтобы он вообще не ушёл вниз как лишний терминал. ResultMessage при turn=null тоже
            // аномален, но несёт данные хода; фильтровать только его рискованнее возможного дубля,
            // поэтому вниз пропускаем — а Exited это лишь факт смерти процесса, его потеря безопасна.
            if (msg is ExitedMessage) return Task.CompletedTask;
            return _downstream(msg);
        }

        switch (msg)
        {
            case ResultMessage res:
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    // Result попытки задерживаем: при повторе он не должен завершить ход
                    // в SessionManager (статус/аккумулятор/наблюдатели задач), а при
                    // финале уйдёт наружу решением оркестратора
                    turn.NoteResult(res);
                    return Task.CompletedTask;
                }

            case ErrorMessage { ExpectResultFollows: true } em:
                // Текст API-ошибки провайдера ПЕРЕД result. Раньше он уходил в ленту сразу, ДО
                // классификации: при успешной подмене человек видел красную карточку сбоя, за
                // которой шёл нормальный ответ (замер по проду: 9 таких «ложных тревог» из 16
                // карточек в одном чате). Теперь ошибка ЗАДЕРЖИВАЕТСЯ так же, как result попытки,
                // а показывать её решает оркестратор:
                //   • подмена состоялась → карточки в ленте нет вовсе, сырой текст едет в
                //     ProviderSwitchedMessage.ErrorDetails («Подробности» внутри маркера);
                //   • подмены не было (FailClosedAsync/FailExhaustedAsync) → ошибка уходит
                //     downstream как есть, строго ПЕРЕД финальным result.
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    turn.NoteErrorText(RawErrorText(em));
                    // Уборка прерванной ротацией попытки — глотаем, как и прочие её терминалы
                    if (turn.SwallowCleanup) return Task.CompletedTask;
                    turn.Hold(msg);
                    return Task.CompletedTask;
                }

            case ErrorMessage em:
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    turn.NoteErrorText(RawErrorText(em));
                    if (turn.SwallowCleanup) return Task.CompletedTask; // уборка прерванной ротацией попытки
                    turn.Hold(msg);
                    turn.ResolveAttempt(AttemptEndKind.FatalError);
                    return Task.CompletedTask;
                }

            case ExitedMessage exited:
                lock (turn.Sync)
                {
                    if (turn.Settled) return _downstream(msg);
                    if (turn.SwallowCleanup) return Task.CompletedTask; // процесс убит ротацией — глотаем
                    // Терминал ЧУЖОГО прогона: ход этой попытки в нём не подавался (штатная смерть
                    // прогона предыдущего хода, доживавшего после своего result). Попытку не трогаем —
                    // иначе её исход становится ProcessGone → Unreachable → подмена модели на живой
                    // подписке (инцидент 2026-08-11: opus → glm-5.2 без единой ошибки доставки).
                    // Глотаем ДО turn.Hold: иначе чужой Exited осядет в held и при SettleAsync
                    // (foreach var m in held await _downstream(m)) уйдёт downstream лишним терминалом —
                    // та же природа, что в ветке _turn is null выше.
                    if (!IsOwnExited(exited.TurnSeq, turn.AttemptTurnSeq)) return Task.CompletedTask;
                    turn.Hold(msg);
                    // Процесс умер без result — обрыв потока. Если попытка уже разрешена
                    // result'ом — это штатный выход после хода, не новый исход
                    if (!turn.AttemptResolved) turn.ResolveAttempt(AttemptEndKind.ProcessGone);
                    return Task.CompletedTask;
                }

            case RateLimitMessage rl:
                // Сначала downstream: SessionManager пишет usage-снимок и помечает пул
                // (существующий механизм), реакция фолбэка — после, по помеченному пулу
                return ForwardThenAsync(msg, () =>
                {
                    lock (turn.Sync)
                    {
                        if (turn.Settled || turn.AttemptResolved) return;
                        if (rl.Status != "rejected" || !ClaudeSubscriptionPool.IsExhaustionWindow(rl.LimitType)) return;
                        // CLI приостановил ход до сброса окна — снимем его с паузы и уйдём
                        // на другую подписку (ADR §2: rejected по окну исчерпания = фолбэк)
                        turn.RateLimitResetsAt = rl.ResetsAt;
                        turn.ResolveAttempt(AttemptEndKind.RateLimited);
                    }
                });

            default:
                return _downstream(msg);
        }
    }

    // Сырой технический текст ошибки. С 18.08.2026 ClaudeSession кладёт в Text человеческую
    // формулировку («Модель перегружена…»), а ответ CLI — в Details. Классификатор
    // (TurnErrorClassifier) и «Подробности» маркера работают ТОЛЬКО с сырым текстом: по
    // русской формулировке ни «overloaded», ни «429» не распознаются, и ход, который должен
    // был уйти на подмену, завершился бы честной ошибкой (класс None).
    private static string RawErrorText(ErrorMessage em) =>
        string.IsNullOrWhiteSpace(em.Details) ? em.Text : em.Details!;

    // Сырые тексты задержанных ошибок попытки — в «Подробности» маркера подмены.
    // null — гасить было нечего (ошибка не приходила).
    private static string? HeldErrorDetails(IEnumerable<ServerMessage> held)
    {
        var parts = held.OfType<ErrorMessage>()
            .Select(RawErrorText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n", parts);
    }

    // Основной цикл: попытка → исход → классификация → ротация → повтор.
    private async Task RunFallbackLoopAsync(FallbackTurn turn, string text,
        IReadOnlyList<string> attachedPaths, int agentDepth, bool suppressTasksExecute)
    {
        // Учёт попыток «модель × подписка»: пара не пробуется дважды за ход (ADR §4)
        var attempted = new HashSet<(string Model, string Key)>();
        // След перепробованных пар — в финальную ошибку (виден в ленте/карточке задачи).
        // Хранится структурой: для ленты берём поставщика и причину, для лога — модель и ключ.
        var trace = new List<AttemptTrace>();
        var substitutions = 0;
        AttemptEnd? lastEnd = null;

        // Цепочка хода (ADR-007 §4): конкретные модели пресета, первый = основной, остальные
        // = план фолбэка. Есть цепочка (Count > 1) — фолбэк идёт по её шагам; нет (одноэлементная
        // или пустая) — после пула честная ошибка, автоподбора больше нет. Вычисляем один раз за ход.
        var chain = _effectiveChain?.Invoke() ?? Array.Empty<string>();
        var chainIndex = 0;
        // Отладка «почему выбралась эта модель»: построенная цепочка хода одной строкой.
        LogDebug($"Цепочка хода ({Info.Id}): [{string.Join(", ", chain)}]");

        // Фактическая пара текущей попытки «модель × ключ». Берётся из шага цепочки или
        // эффективной модели на СТАРТЕ и далее обновляется применённой подменой (next), а не
        // пересчитывается из _effectiveModel() каждую итерацию. _effectiveModel() — НАМЕРЕНИЕ
        // хода (слот/назначение места); оно не отражает подмену на эквивалент стороннего
        // провайдера. Считай от него — и в attempted ляжет намерение (напр. «opus»), а
        // проверка зацикливания пойдёт по эквиваленту — пары разойдутся, и одна мёртвая пара
        // повторится до потолка (инцидент 2026-08-07).
        var currentModel = chain.Count > 0 ? chain[chainIndex] : (_effectiveModel() ?? "");
        var currentKey = ProviderKeyFor(currentModel);

        // Снимок «модели × провайдера» на старте хода. Подмены внутри хода пишутся в
        // Info.Model/Provider (процесс попытки пересобирается из них), но в finally исходные
        // значения восстанавливаются — иначе ход фолбэка переписывал бы модель чата навсегда
        // (инцидент 2026-08-07: чат залип на qwen3.7-plus после одной подмены). appliedModel/
        // appliedProvider — последнее значение, записанное ApplyTarget; по нему CAS в finally.
        var origModel = Info.Model;
        var origProvider = Info.Provider;
        var appliedModel = origModel;
        var appliedProvider = origProvider;
        // Была ли за ход хоть одна смена ТИПА поставщика (шаг цепочки/сторонний провайдер,
        // IsProviderSwitch:true). Накапливается по «или»: тихая ротация подписки того же пула
        // Claude флаг не сбрасывает (сценарий «шаг цепочки → 429 → тихая ротация → успех» —
        // restore обязан сработать по накопленному флагу). Решает в finally, что восстанавливать
        // и переносить ли транскрипт обратно — регрессия волны 2 (см. блок restore ниже).
        var anyProviderSwitch = false;

        try
        {
            // (б) Стартовая подмена при кулдауне (волна 2): если стартовый провайдер помечен
            // недоступным (возвращал Unreachable/ProviderError в прошлых ходах либо исчерпал
            // квоту — UsageLimit), не тратим попытку на мёртвый/исчерпанный эндпоинт — стартуем
            // сразу с первого живого шага цепочки. Маркер provider_switched с Reason из реестра
            // (фронт покажет «Исчерпан лимит»/«Эндпоинт недоступен» вместо универсальной фразы).
            // fail-open: если ВСЕ шаги цепочки в кулдауне, остаёмся на исходной паре (ход должен
            // идти). substitutions не тратим — это выбор стартовой точки, а не подмена по ошибке.
            //
            // Асимметрия с WouldFit (Minor 4 ревью): стартовый шаг намеренно НЕ сверяется с
            // фильтром ёмкости. Кулдаун — про доступность эндпоинта, а не про размер окна, а
            // стартовая модель — НАМЕРЕНИЕ пользователя (шаг цепочки пресета, выбранный им же).
            // Отсечь её здесь значило бы молча подменить выбор пользователя до любой ошибки.
            // Если стартовый шаг мал и контекст велик — первый ход упадёт с ContextOverflow, и
            // тогда оркестратор пойдёт по цепочке уже с фильтром WouldFit (см. ResolveNextTarget).
            if (_health is not null && chain.Count > 1 && _health.IsUnavailable(currentKey))
            {
                var stepIdx = -1;
                for (var i = 1; i < chain.Count; i++)
                {
                    var sm = chain[i];
                    if (!_health.IsUnavailable(ProviderKeyFor(sm))) { stepIdx = i; break; }
                }
                if (stepIdx > 0)
                {
                    var stepModel = chain[stepIdx];
                    var stepKey = ProviderKeyFor(stepModel);
                    var stepRoot = ResolveRootFor(stepKey);
                    // Перенос транскрипта ДО подмены (Major, инвариант ADR-007 §4.1: Provider и свежий
                    // транскрипт — одно место). Профиль исходного провайдера (_profileRoot) → профиль
                    // шага цепочки. Без этого --resume в профиле шага не найдёт разговор — ход падает
                    // (класс None, fail-closed), фолбэк не спасает: дальнейшие миграции идут из уже
                    // пустого _profileRoot, кандидаты отсеиваются → «Ни одна из доступных моделей не
                    // ответила». Чат стоит колом до истечения TTL кулдауна, хотя оба провайдера живы.
                    // Провал переноса — fail-open: НЕ применяем подмену, остаёмся на исходной паре
                    // (ход должен идти; эндпоинт мог уже подняться, а кулдаун — лишь наблюдение).
                    // TryMigrateTranscript=true при отсутствии ClaudeSessionId — переносить нечего,
                    // подмена разрешена (как и везде в адаптере).
                    if (!TryMigrateTranscript(stepRoot))
                    {
                        LogWarn($"Стартовая подмена (кулдаун) {Info.Id}: транскрипт не перенесён в профиль «{stepKey}» — остаёмся на исходной паре «{origProvider}» (ход продолжится, кулдаун — наблюдение, а не запрет)");
                    }
                    else
                    {
                        chainIndex = stepIdx;
                        currentModel = stepModel;
                        currentKey = stepKey;
                        Info.Model = currentModel;
                        Info.Provider = currentKey;
                        _profileRoot = stepRoot;
                        appliedModel = currentModel;
                        appliedProvider = currentKey;
                        anyProviderSwitch = true;   // старт на шаге цепочки = смена поставщика
                        var slabel = string.IsNullOrWhiteSpace(currentModel) ? KeyLabel(currentKey) : currentModel;
                        // Причину берём из реестра: исчерпанная квота и мёртвый эндпоинт для
                        // пользователя — разные ситуации (одна лечится пополнением баланса, другая
                        // — сетью). Раньше здесь стоял хардкод «unreachable»/«недоступен» со времён,
                        // когда кулдаун был только про обрыв связи, и при исчерпании квоты подсказка
                        // врала («Сервис не отвечает»). Label — нейтрален: точную формулировку фронт
                        // берёт из Reason (providerSwitchReasonLabel предпочитает его перед Label).
                        var switchReason = _health?.UnavailableReason(origProvider) ?? "unreachable";
                        await _downstream(new ProviderSwitchedMessage(currentKey, currentModel,
                            $"Старт на «{slabel}»: исходный провайдер временно исключён",
                            Auto: true, Reason: switchReason));
                        LogInfo($"Стартовая подмена (кулдаун, {switchReason}): «{origModel}» × «{origProvider}» исключён → старт на «{currentKey}» / «{currentModel}»");
                    }
                }
            }

            while (!_cts.IsCancellationRequested)
            {
                if (_userInterrupted) { await SettleAsync(turn); return; }

                attempted.Add((currentModel, currentKey));

                TaskCompletionSource<AttemptEnd> attemptTcs;
                lock (turn.Sync)
                {
                    // Снимок номера ходов ДО подачи: ход попытки получит номер больше него,
                    // а всё, что подавалось раньше (в т.ч. ход, чей прогон ещё доживает), —
                    // меньше или равно. По этой границе отсеиваем чужие exited (см. IsOwnExited)
                    turn.BeginAttempt(_inner.SubmittedTurnSeq);
                    attemptTcs = turn.AttemptTcs;
                }

                await _inner.SendMessageAsync(text, attachedPaths, agentDepth, suppressTasksExecute);

                AttemptEnd end;
                try { end = await attemptTcs.Task.WaitAsync(_cts.Token); }
                catch (OperationCanceledException) { return; } // сессию закрыли — оркестрация снята
                lastEnd = end;

                if (_userInterrupted) { await SettleAsync(turn); return; }

                // Успех (нет ошибки доставки) — задержанный result уходит наружу, ход окончен.
                // Снимаем кулдаун с провайдера ФАКТИЧЕСКОЙ попытки (currentKey): успешный ход —
                // прямой сигнал «уже жив», сильнее верхней границы TTL. Без этого пополенный
                // тариф или поднявшийся эндпоинт простаивал бы в кулдауне весь TTL (час для
                // исчерпанной квоты), уводя стартовые подмены на чужие модели (Major ревью).
                // Подписок пула здесь нет (мы их не помечаем), так что Clear для них — no-op.
                if (!IsDeliveryFailure(end))
                {
                    _health?.Clear(currentKey);
                    await SettleAsync(turn);
                    return;
                }

                var outcome = new TurnAttemptOutcome
                {
                    HasResult = end.Kind == AttemptEndKind.Result,
                    Subtype = end.Result?.Subtype,
                    ApiErrorStatus = end.Result?.ApiErrorStatus,
                    ErrorText = end.ErrorText,
                    RateLimitRejected = end.Kind == AttemptEndKind.RateLimited,
                    // Намеренное прерывание (Interrupt ради очереди / «Стоп») — НЕ ошибка доставки.
                    // Это второй эшелон: первым стоит if (_userInterrupted) выше (SettleAsync без
                    // классификации), но гонка «interrupt пришёл, когда цикл УЖЕ ушёл в
                    // классификацию по ProcessGone» (поздний Kill относительно обрыва, либо
                    // proc убит до result) оставляет end.Kind=ProcessGone при активном turn —
                    // тогда Classify без этого флага отдал бы Unreachable и менял провайдера
                    // (симптом 1: 33 ложных подмены). Classify по InterruptedByUser=true → None.
                    InterruptedByUser = _userInterrupted,
                };
                var cls = TurnErrorClassifier.Classify(outcome);
                // Прерывание пользователем — нейтральный финал: без ошибки, без ротации (даже если
                // обрыв успел классифицироваться как None по InterruptedByUser).
                if (outcome.InterruptedByUser) { await SettleAsync(turn); return; }
                // None — неопознанная/содержательная ошибка: фолбэк НЕ запускаем (ADR fail-closed —
                // не жечь лимиты других аккаунтов о неопознанную проблему). Но «не запускаем» ≠
                // «всё в порядке»: ход обязан завершиться error, а не штатным finished (исходная
                // половина P29). CLI при API-ошибке отдаёт subtype=success + is_error — без замены
                // result на error статус становился Active, и провал маскировался. AuthFailure сюда
                // не попадает — это отдельный класс, он эскалирует по пулу и цепочке ниже.
                if (cls == FallbackErrorClass.None) { await FailClosedAsync(turn, end); return; }

                // Кулдаун недоступности (волна 2): провайдер, вернувший Unreachable/ProviderError,
                // помечаем недоступным на TTL — следующие ходы и шаги цепочки пропустят его сразу.
                // RateLimit сюда не попадает: окно запросов у аккаунта отпускает быстро, и это
                // квота, а не мёртвый эндпоинт.
                // ТОЛЬКО для сторонних провайдеров: ключи подписок пула Claude НЕ помечаем — их
                // здоровье уже ведёт ClaudeSubscriptionPool (исчерпание/ротация). Иначе один
                // транзитный 529 на «claude» уводил бы все новые ходы с цепочкой на сторонний шаг 2
                // мимо живой «claude-2» (Major 2 ревью). Стартовая подмена для нативного ключа тогда
                // не срабатывает (IsUnavailable всегда false) — пул отработает уровень 1 штатно.
                if (IsExternalProvider(currentKey))
                {
                    if (cls is FallbackErrorClass.Unreachable or FallbackErrorClass.ProviderError)
                        _health?.MarkUnavailable(currentKey, cls);
                    // Исчерпанная квота стороннего провайдера (инцидент 2026-08-11, kimi): без отметки
                    // каждый следующий ход снова стартовал бы на нём, тратил попытку и падал до конца
                    // биллингового цикла. MarkExhausted — механизм пула Claude, стороннего он не знает,
                    // поэтому память об исчерпании ведём здесь, длинным TTL (QuotaCooldownMinutes).
                    else if (cls == FallbackErrorClass.UsageLimit)
                        _health?.MarkQuotaExhausted(currentKey);
                    // Негодный auth стороннего провайдера (401/протухший ключ, P29): помечаем ключ
                    // длинным кулдауном и уходим на следующий шаг цепочки ниже. Смена модели auth не
                    // лечит, но у следующего шага свой ключ — это и есть лечение.
                    else if (cls == FallbackErrorClass.AuthFailure)
                        _health?.MarkAuthDead(currentKey);
                }

                // ContextOverflow: модель не приняла контекст — запоминаем наблюдённый потолок
                // ёмкости (модель не вместила ~N токенов), чтобы в этом и следующих ходах не
                // отправлять на неё контекст ≥ N. Оценка N — последний известный размер контекста
                // чата (для нового хода — значение прошлого хода, контекст растёт плавно).
                // Не помечаем подписку исчерпанной (это не квота) и эндпоинт недоступным (он жив).
                if (cls == FallbackErrorClass.ContextOverflow)
                {
                    var ctxN = ContextEstimate();
                    _capacity?.RecordOverflow(currentModel, ctxN);
                    LogInfo($"Переполнение контекста: «{currentModel}» × «{currentKey}» не принял ~{ctxN} токенов (оценка: {ContextSource()})");
                }

                trace.Add(new AttemptTrace(currentModel, currentKey, cls));
                if (substitutions >= EffectiveMaxSubstitutions())
                {
                    await FailExhaustedAsync(turn, trace, substitutions, end);
                    return;
                }

                // Оценку контекста передаём ВСЕГДА (реальный размер) — какие источники ёмкости
                // сверять, ResolveNextTarget/WouldFit решают по достоверности: наблюдённый потолок
                // (ObservedCeiling, точный факт отказа) отсеивает кандидата при ЛЮБОМ классе ошибки;
                // заявленное окно каталога (неточное, расхождение токенизаторов 10–30%) — только при
                // ContextOverflow (Major 3 ревью). Так шаг цепочки, уже падавший на этом контексте,
                // не получит его повторно ни по rate-limit, ни по unreachable (инцидент 2026-08-10:
                // kimi-k3). Запись наблюдения выше использует тот же реальный размер.
                var ctx = ContextEstimate();
                var next = ResolveNextTarget(cls, end, attempted, currentModel, currentKey, chain, ref chainIndex, ctx);
                if (next is null)
                {
                    // Всё исчерпано (или запасных вариантов не было) — честная ошибка. P29: финал
                    // обязан быть error, не finished. ContextOverflow всегда идёт в FailExhausted —
                    // у него свой текст про контекст/compact, осмысленный даже на одной попытке.
                    // Прочее по числу попыток: перепробовано несколько пар — FailExhausted (список
                    // + «ни одна не ответила»); тупик сразу (нет пула и цепочки) — FailClosed:
                    // одиночный error, причина уже в ленте от ErrorMessage(ExpectResultFollows).
                    if (cls == FallbackErrorClass.ContextOverflow || attempted.Count > 1)
                        await FailExhaustedAsync(turn, trace, substitutions, end);
                    else
                        await FailClosedAsync(turn, end);
                    return;
                }

                // Терминальные сообщения провальной попытки наружу не идут — ход продолжается.
                // Сырой текст задержанной ошибки забираем ДО очистки: красной карточки в ленте
                // не будет (ход состоится на другой паре), но текст не теряется — он поедет в
                // ErrorDetails маркера подмены и в лог ниже.
                string? swallowedError;
                lock (turn.Sync)
                {
                    swallowedError = HeldErrorDetails(turn.Held);
                    turn.Held.Clear();
                    // Attempt прерван ротацией: последующие Exited/ErrorMessage — уборка, глотать
                    // (выставляем ВСЕГДА, не только на RateLimited — уборка нужна и после
                    // ProcessGone/FatalError; иначе Exited/ErrorMessage пойдут downstream
                    // и осядут в ленте «ответом» уже выбывшей попытки)
                    turn.SwallowCleanup = true;
                }
                // Паузу CLI на исчерпанном окне снимаем убийством процесса — следующий
                // процесс запустится уже на новой паре
                if (end.Kind == AttemptEndKind.RateLimited) _inner.Interrupt();

                // Подмена помечается в ленте существующим provider_switched (Auto=true). Маркер —
                // ТОЛЬКО при смене ТИПА поставщика (сторонний провайдер / шаг цепочки): переход
                // между аккаунтами ОДНОГО пула Claude проходит тихо (Label=null), пользователь
                // видит ровно один ход без служебных переключений. Reason — wire-имя класса
                // ошибки, чтобы фронт показал каноническую формулировку подсказки. ErrorDetails —
                // погашенная ошибка попытки: в маркере она живёт под «Подробностями».
                if (next.Label is not null)
                {
                    var reason = TurnErrorClassifier.WireName(cls);
                    await _downstream(new ProviderSwitchedMessage(next.Key, next.Model, next.Label,
                        Auto: true, Reason: reason, ErrorDetails: swallowedError));
                    LogInfo($"Подмена: {TraceLine(currentModel, currentKey, cls)} → «{KeyLabel(next.Key)}» / «{next.Model ?? currentModel}» (причина {reason ?? "неизвестно"}). "
                        + $"Погашенная ошибка: {Truncate(swallowedError ?? "нет", 300)}");
                }
                else
                {
                    // Тихая подмена (смена подписки того же пула): в ленту не идёт — ни маркера,
                    // ни карточки ошибки, показывать человеку нечего. Для разбора пишем причину
                    // (LogDetail ротации, иначе общий текст) и погашенный текст ошибки — иначе
                    // после тихой ротации от него не осталось бы следа нигде.
                    LogInfo((next.LogDetail
                        ?? $"Подмена без маркера: {TraceLine(currentModel, currentKey, cls)} → «{KeyLabel(next.Key)}» (маркер уже был от SessionManager)")
                        + $". Погашенная ошибка: {Truncate(swallowedError ?? "нет", 300)}");
                }

                ApplyTarget(next);
                // Фактическая пара следующей попытки: смена провайдера несёт эквивалент
                // (next.Model), смена подписки того же пула — модель не меняется; ключ — всегда
                // next.Key. Согласовано с ApplyTarget (Info.Model/Provider), но не зависит от
                // _effectiveModel() — поэтому именно эта пара попадает в attempted и в след.
                currentModel = next.Model ?? currentModel;
                currentKey = next.Key;
                // Запоминаем последнее применённое значение Info — для CAS-восстановления в finally.
                // anyProviderSwitch накапливаем: за ход могла быть смена поставщика, а затем тихая
                // ротация подписки (IsProviderSwitch:false) — по накопленному флагу finally решает,
                // восстанавливать ли Provider и переносить ли транскрипт обратно из актуального
                // _profileRoot (профиль последней пары — там лежит свежий ответ).
                appliedModel = Info.Model;
                appliedProvider = Info.Provider;
                anyProviderSwitch |= next.IsProviderSwitch;
                // Потолок подмен тратится только на смену ТИПА поставщика (шаг цепочки /
                // переход к стороннему провайдеру). Тихие ротации подписок того же пула
                // Claude бесплатны — это та же модель на другом аккаунте, а не подмена.
                if (next.IsProviderSwitch) substitutions++;
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Сбой оркестрации ({Info.Id}): {ex.Message}");
            try
            {
                // P31: если сбой случился после провальной попытки доставки (lastEnd есть и это
                // отказ), завершаем ход честной ошибкой, а не SettleAsync — иначе задержанный result
                // провальной попытки (subtype=success + is_error) уходит наружу как есть, и сессия
                // становится Active (исходный симптом P29, редкая ветка: исключение в _downstream
                // маркера смены провайдера / TryMigrateTranscript / ResolveRootFor). До первой
                // попытки lastEnd=null — тогда как раньше SettleAsync (в hold'е ещё ничего).
                if (lastEnd is { } failedEnd && IsDeliveryFailure(failedEnd))
                    await FailClosedAsync(turn, failedEnd);
                else
                    await SettleAsync(turn);
            }
            catch { /* сессия уже закрыта */ }
        }
        finally
        {
            // Восстановить модель/провайдер, сохранённые на старте хода, — подмена не должна
            // переписывать модель чата навсегда (инцидент 2026-08-07). Восстановление ПОКОМПОНЕНТНОЕ
            // (CAS по каждому полю отдельно): так ручная смена одного поля во время хода не блокирует
            // восстановление другого. ABA (Info совпала с applied случайно) принят осознанно: редкий
            // случай, а различить «подмена» от «совпадение» без версионирования нельзя. Без подмен
            // applied == orig и восстановление — no-op.
            //
            // Флаг anyProviderSwitch решает, что делать с Provider (регрессия волны 2: restore
            // провайдера терял свежий транскрипт хода). TranscriptMigrator.TryMigrate — это Copy,
            // а не move: ход на профиле A поймал ошибку → транскрипт скопирован A→B → финальный
            // ответ CLI записан ТОЛЬКО в профиль B. Вернуть Provider в A — и следующий ход на
            // --resume с A не вспомнит собственный ответ (в ленте CCS он есть, в контексте CLI нет),
            // а TryPoolFailover ещё и затрёт свежий B устаревшей копией A→B.
            //   • За ход НЕ было смены типа поставщика (только тихие ротации подписок того же пула):
            //     Provider НЕ восстанавливаем — оставляем аккаунт, где реально прошёл ход и лежит
            //     свежий транскрипт. Качество не страдает (та же модель, другой аккаунт), а транскрипт
            //     остаётся консистентным. Модель восстанавливаем как обычно (тихая ротация её и не
            //     трогала — restore здесь no-op).
            //   • За ход БЫЛА смена типа поставщика (IsProviderSwitch — шаг цепочки/сторонний
            //     провайдер, возможно с последующей тихой ротацией внутри нового пула): восстанавливаем
            //     И модель, И Provider (иначе пара вроде opus × kimi несовместима), но ПЕРЕД restore
            //     копируем транскрипт обратно в профиль исходного провайдера. Источник копии —
            //     _profileRoot (профиль ПОСЛЕДНЕЙ пары: шаг цепочки прошёл, а за ним тихая ротация
            //     сменила подписку — именно там лежит свежий ответ).
            var modelRestored = false;
            var providerRestored = false;
            if (Info.Model == appliedModel) { Info.Model = origModel; modelRestored = true; }
            if (anyProviderSwitch)
            {
                if (Info.Provider == appliedProvider)
                {
                    // Обратный перенос строго ДО перезаписи Provider и ДО персиста: копируем из
                    // профиля последней пары (_profileRoot — там лежит свежий ответ) в профиль
                    // исходного провайдера, куда вернётся --resume. Провал не роняет ход (TryCopy).
                    if (appliedProvider != origProvider)
                        // Ход прошёл (lastEnd=Result) → копия профиля подмены несёт свежий ответ:
                        // перезаписываем приёмник, НЕ preserve (иначе «приёмник длиннее» оставит
                        // висящий turn без ответа → реюрект дубля на следующем ходе, доп. №5).
                        TryCopyTranscriptBack(ResolveRootFor(origProvider),
                            preserveLongerDestination: lastEnd is not { Kind: AttemptEndKind.Result });
                    Info.Provider = origProvider;
                    providerRestored = true;
                }
            }
            // Без смены типа поставщика (!anyProviderSwitch): Provider оставляем — аккаунт, на
            // котором прошёл ход, хранит свежий транскрипт. providerRestored остаётся false.
            //
            // (Minor 4) Заметка о видимости для клиента: новое значение Info.Provider персистится
            // (ниже), но в ленте событие об этом НЕ идёт — тихая ротация по решению владельца
            // остаётся тихой. У открытого клиента session.provider поэтому устаревает до перезагрузки/
            // следующего REST-рефетча списка чатов. Существующего WS-события, обновляющего именно
            // session.provider БЕЗ элемента ленты, НЕТ: ProviderSwitchedMessage(Auto:true) (которое
            // шлёт TryPoolFailover в той же ситуации) фронт использует только чтобы гасить карточки
            // provider_limit и рисовать model_switched — поле session.provider из WS он не пишет
            // (useSession/chatReducer этого не делают; session_started несёт Provider, но редьюсер
            // его в объект сессии тоже не кладёт). Слать такой маркер отсюда не чинит устаревание и
            // может невпопад погасить чужую provider_limit-карточку — поэтому поведение оставлено как
            // есть. Полный фикс — фронтенд-задача: провести WS-сигнал провайдера (provider_switched
            // или session_started.Provider) в session.provider единообразно для TryPoolFailover и адаптера.
            Info.MessageCount = _snapshotMessageCount + 1;
            // ПЕРСИСТ ПОСЛЕ RESTORE (и обратного переноса): финальный result уже ушёл downstream
            // и успел сохранить sessions.json ПОДМЕНЁННЫМ (ApplyStatusAsync → SaveSessions ДО finally).
            // При смене поставщика — переписываем персист восстановленными значениями, иначе рестарт
            // в этом окне залипил бы чат на подмене. При тихой ротации persist фиксирует оставленный
            // новый аккаунт (тот же, что сохранил result, но гарантированно — на случай хода без
            // result, когда ApplyStatus не успел). Без подмены persist не дёргаем — ApplyStatus уже
            // сохранил корректное состояние.
            if ((modelRestored || providerRestored)
                && (appliedModel != origModel || appliedProvider != origProvider))
                _persist?.Invoke();
            lock (_gate) if (ReferenceEquals(_turn, turn)) _turn = null;
            // Оркестрация завершена (_turn сброшен → OrchestrationActive=false). Если за время хода
            // были обходы в серверную Pending (EnqueueBypass), штатный drain по result уже отработал
            // по ещё пустой очереди — без этой сигнализации requeued-ходы ждали бы следующего хода.
            // Запускаем разбор: теперь адаптер свободен, и Pending-сообщения пойдут под новым _turn
            // со штатным фолбэком. Drain идемпотентен (DrainInFlight гейт), пустая очередь — no-op.
            if (Interlocked.Exchange(ref _bypassRequeued, 0) == 1)
                _orchestrationDone?.Invoke(Info.Id);
        }
    }

    // Принадлежит ли пришедший exited прогону, которому подан ход ТЕКУЩЕЙ попытки. Чистая
    // функция (по образцу ClaudeSession.ShouldRetryEmptyExit): HandleMessageAsync применяет
    // её исход, тест вызывает напрямую.
    //   exitedTurnSeq — номер последнего хода умершего прогона (ExitedMessage.TurnSeq);
    //   attemptTurnSeq — снимок номера ходов на начало попытки (BeginAttempt).
    // Ход попытки подаётся ПОСЛЕ снимка, поэтому его прогон всегда несёт метку строго больше.
    // Метка не больше снимка = прогон обслуживал ход, поданный до попытки: его смерть — штатный
    // конец предыдущего хода, а не обрыв нашего (инцидент 2026-08-11). 0 — метки нет (адаптер
    // без прогонов, тесты): fail-open, считаем своим — прежнее поведение, иначе попытка,
    // резолвившаяся только по exited, зависла бы навсегда.
    internal static bool IsOwnExited(long exitedTurnSeq, long attemptTurnSeq) =>
        exitedTurnSeq <= 0 || exitedTurnSeq > attemptTurnSeq;

    // Ошибка доставки: всё, кроме чистого успеха. API-ошибка провайдера приходит как
    // subtype=success + is_error (в протоколе остаётся следом api_error_status и текстом
    // ошибки ErrorMessage(ExpectResultFollows=true)) — оба признака учтены.
    private static bool IsDeliveryFailure(AttemptEnd end) => end.Kind switch
    {
        AttemptEndKind.Result => end.Result!.Subtype == "error"
            || !string.IsNullOrEmpty(end.Result.ApiErrorStatus)
            || !string.IsNullOrEmpty(end.ErrorText),
        _ => true,
    };

    // Уровень 1: другая подписка того же пула (та же модель). Далее — следующий шаг цепочки
    // (ADR-007 §4). null — кандидатов не осталось (цепочки нет либо она исчерпана). Без цепочки
    // (одноэлементная/пустая) после пула автоподбора нет — финальный сбой.
    private FallbackTarget? ResolveNextTarget(FallbackErrorClass cls, AttemptEnd end,
        HashSet<(string Model, string Key)> attempted, string model, string currentKey,
        IReadOnlyList<string> chain, ref int chainIndex, int contextEstimate)
    {
        var modelForPool = string.IsNullOrWhiteSpace(model) ? null : model;
        var isNativeClaude = _providers?.ResolveByModel(modelForPool) is null;

        // Лимитные классы помечают подписку исчерпанной в пуле (существующий механизм):
        // 5xx/обрыв — НЕ помечают, это не квота аккаунта (инцидент 2026-08-02: ложные баны)
        if (cls is FallbackErrorClass.RateLimit or FallbackErrorClass.UsageLimit)
        {
            DateTime? resetsAt = null;
            if (end.RateLimitResetsAt is { } s && DateTime.TryParse(s, out var dt))
                resetsAt = dt.ToUniversalTime();
            _pool.MarkExhausted(currentKey, resetsAt);
        }
        // AuthFailure подписки пула: протухший OAuth/ключ — пометить аккаунт негодным (MarkAuthDead,
        // без TTL — токен не воскреснет сам). Сторонний провайдер (isNativeClaude=false) здесь не
        // помечается: его auth-кулдаун ведёт ProviderHealthRegistry (выше в health-блоке), а этот
        // метод затем уводит ход на следующий шаг цепочки.
        else if (cls == FallbackErrorClass.AuthFailure && isNativeClaude)
        {
            _pool.MarkAuthDead(currentKey);
        }

        // Уровень 1: ротация подписок пула (только нативные claude-модели; у сторонних пула нет —
        // шаг считается исчерпанным сразу, переходим к следующему шагу цепочки/автоподбору).
        // ContextOverflow обходим уровень 1: окно — свойство МОДЕЛИ, не подписки. Другая подписка
        // того же аккаунта-пула несёт ту же модель с тем же окном и упадёт так же — гонять её
        // бессмысленно. Сразу к шагу цепочки, где может быть модель с бóльшим окном.
        if (isNativeClaude && _pool.HasExtra && cls != FallbackErrorClass.ContextOverflow)
        {
            // SessionManager.TryPoolFailover по rate_limit_event мог уже переключить чат
            // (транскрипт перенесён им) — тогда не мигрируем повторно и не шлём второй маркер
            var switched = Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
            if (switched != currentKey
                && !attempted.Contains((model, switched))
                && !_pool.IsExhausted(switched)
                && !_pool.IsAuthDead(switched)
                && _pool.SupportsModel(switched, modelForPool))
                return new FallbackTarget(switched, null, Label: null,
                    ProfileRoot: ResolveRootFor(switched), IsProviderSwitch: false);

            // Иначе крутим сами: Pick — штатный механизм ротации пула. Если он вернул
            // уже пробованную пару (напр. пометку исчерпания посреди хода снял warmup-пинг)
            // — перебираем остальных последовательно; новой логики выбора не заводим (ADR §3)
            foreach (var candidate in SubscriptionCandidates(modelForPool, currentKey))
            {
                if (attempted.Contains((model, candidate))) continue;
                var dstRoot = ResolveRootFor(candidate);
                if (!TryMigrateTranscript(dstRoot)) continue;
                // Смена подписки того же пула Claude — тихо: маркер в ленте только при
                // переходе на ДРУГОЙ тип поставщика. Причина сохраняется в LogDetail для
                // разбора (какая подписка на какую сменилась), наружу не идёт.
                return new FallbackTarget(candidate, null,
                    Label: null,
                    ProfileRoot: dstRoot, IsProviderSwitch: false,
                    // Причина ротации — в LogInfo для разбора (какой класс ошибки погнал
                    // смену аккаунта: 429, 5xx, обрыв): это единственный след тихой подмены.
                    LogDetail: $"Автофолбэк: {TraceLine(model, currentKey, cls)} → подписка «{KeyLabel(candidate)}»");
            }
        }

        // Цепочка пресета: следующий шаг (модель могла быть сторонней — у неё нет ротации,
        // поэтому мы здесь). Каждый шаг цепочки, дойдя до своих подписок (у нативных),
        // отработает уровень 1 на следующей итерации. Кулдаун (волна 2): шаги с провайдером
        // в кулдауне пропускаем, но fail-open — если ВСЕ оставшиеся непробованные в кулдауне,
        // берём первого остывшего (кулдаун — наблюдение, а не запрет). Кулдаун проверяем ДО
        // переноса транскрипта: иначе копии плодятся по профилям пропущенных остывших кандидатов.
        // Цепочка пресета (уровень 2): следующий шаг. AuthFailure сюда ДОЛЖЕН дойти — у следующего
        // шага свой ключ/токен, и смена модели авторизацию как раз лечит (P29: «не смог продолжить»
        // быть не должно, пока жив хоть один шаг). ContextOverflow сюда тоже доходит (ему нужна
        // модель с большим окном). Каждый шаг, дойдя до своих подписок (у нативных), отработает
        // уровень 1 на следующей итерации. Кулдаун (волна 2): шаги с провайдером в кулдауне
        // пропускаем, но fail-open — если ВСЕ оставшиеся непробованные в кулдауне, берём первого
        // остывшего (кулдаун — наблюдение, а не запрет). Кулдаун проверяем ДО переноса транскрипта:
        // иначе копии плодятся по профилям пропущенных остывших кандидатов.
        if (chain.Count > 1)
        {
            var cooledIdx = -1;
            var scan = chainIndex;
            while (++scan < chain.Count)
            {
                var stepModel = chain[scan];
                var stepKey = ProviderKeyFor(stepModel);
                if (attempted.Contains((stepModel, stepKey))) continue;
                // Окно кандидата не вместит текущий контекст — пропускаем ЖЁСТКО, без fail-open:
                // это уверенный отказ (модель точно упадёт с тем же «Prompt is too long»), а не
                // наблюдение о живости (кулдаун). Какие источники сверять, WouldFit решает по
                // checkDeclared: наблюдённый потолок (точный факт отказа) — при ЛЮБОМ классе ошибки,
                // заявленное окно каталога (неточное) — только при ContextOverflow (Major 3).
                var checkDeclared = cls == FallbackErrorClass.ContextOverflow;
                if (!WouldFit(stepModel, contextEstimate, checkDeclared)) continue;
                // Остывший: запоминаем индекс первого и ищем живого дальше, НЕ мигрируя —
                // перенос выполнится единожды, только если этот кандидат будет выбран (fail-open)
                if (_health?.IsUnavailable(stepKey) is true)
                {
                    if (cooledIdx < 0) cooledIdx = scan;
                    continue;
                }
                var dstRoot = ResolveRootFor(stepKey);
                if (!TryMigrateTranscript(dstRoot)) continue;
                // Живой — берём сразу
                chainIndex = scan;
                return ChainStepTarget(scan, stepKey, stepModel, dstRoot);
            }
            // Живых не осталось — fail-open: берём первого остывшего (мигрируем здесь), иначе цепочка исчерпана
            if (cooledIdx > 0)
            {
                var cm = chain[cooledIdx];
                var ck = ProviderKeyFor(cm);
                var dstRoot = ResolveRootFor(ck);
                if (TryMigrateTranscript(dstRoot))
                {
                    chainIndex = cooledIdx;
                    return ChainStepTarget(cooledIdx, ck, cm, dstRoot);
                }
            }
            return null;
        }

        // Без цепочки (одноэлементная/пустая) автоподбора нет — честная ошибка (ADR-007 §4).
        return null;
    }

    // Целевой шаг цепочки с маркером для ленты («Цепочка пресета: шаг N → …»)
    private FallbackTarget ChainStepTarget(int scan, string stepKey, string stepModel, string? dstRoot)
    {
        var label = string.IsNullOrWhiteSpace(stepModel) ? KeyLabel(stepKey) : stepModel;
        return new FallbackTarget(stepKey, stepModel,
            Label: $"Цепочка пресета: шаг {scan + 1} → «{label}»",
            ProfileRoot: dstRoot, IsProviderSwitch: true);
    }

    // Ключ пары для модели шага: провайдер по модели (приоритет), затем Provider сессии —
    // тот же порядок, что у CurrentProviderKey, но от ЗАДАННОЙ модели шага цепочки, а не Info.Model.
    private string ProviderKeyFor(string? model)
    {
        var byModel = _providers?.ResolveByModel(
            string.IsNullOrWhiteSpace(model) ? null : model)?.Key;
        return byModel ?? (string.IsNullOrEmpty(Info.Provider)
            ? ClaudeSubscriptionPool.PrimaryKey : Info.Provider);
    }

    // Сторонний ли провайдер (есть в реестре LlmProviderRegistry), а не подписка пула Claude.
    // Кулдаун недоступности помечает только сторонние эндпоинты — здоровье подписок пула ведёт
    // ClaudeSubscriptionPool (Major 2).
    private bool IsExternalProvider(string? key) =>
        _providers is not null && _providers.GetByKey(key ?? "") is not null;

    // Оценка размера контекста текущего хода. Составная (собирается фабрикой): живое значение
    // (ClaudeSession.LastContextTokens — usage последнего assistant-сообщения), а при его
    // отсутствии — фолбэк на историю чата (LastContextFromHistory, переживает рестарт). 0 —
    // значения нет совсем: фильтр по ёмкости выключается (WouldFit возвращает true), поведение
    // как до защиты от overflow. Источник оценки (живая/из истории/нет) — ContextSource().
    private int ContextEstimate() => _lastContextTokens?.Invoke() ?? 0;

    // Источник оценки контекста для диагностики. Имя источника собирается фабрикой из того же
    // замыкания, что и ContextEstimate, поэтому согласовано с реальной цифрой: «живая» —
    // ContextEstimate > 0 от usage; «из истории» — фолбэк к StoredResultMessage.ContextTokens;
    // «нет» — оценка 0 (фильтр fail-open, наблюдение не записывается).
    private string ContextSource() => _contextSource?.Invoke() ?? "нет";

    // Запас на расхождение токенизаторов: оценка контекста измерена токенизатором ДРУГОЙ модели
    // (расхождение 10–30%), поэтому заявленное окно должно вмещать контекст с 10%-м запасом — иначе
    // модель «впритык» по конфигу падает на том же overflow. Применяется в WouldFit условием
    // declared < contextTokens * ContextCapacityMargin (контекст «перешагнул» окно с запасом).
    private const double ContextCapacityMargin = 1.1;

    // Вместила бы модель текущий контекст? Два источника ёмкости проверяются РАЗДЕЛЬНО и с разной
    // применимостью по классу ошибки, который привёл к этому шагу фолбэка:
    //   • наблюдение (ObservedCeiling) — РАЗМЕР, на котором модель ОТКАЗАЛА: точный факт, поэтому
    //     проверяется при ЛЮБОМ классе ошибки (модель, падавшая на контексте N, не получит его ни
    //     по rate-limit, ни по unreachable — инцидент 2026-08-10). Контекст должен быть СТРОГО меньше
    //     (при равенстве модель упадёт снова — Major 1 ревью: именно на границе старый код, сводивший
    //     всё к min(declared, observed) >= context, пропускал отказавшую модель);
    //   • заявленное окно (ContextWindow из каталога) — ЁМКОСТЬ, неточная: проверяется ТОЛЬКО при
    //     ContextOverflow (checkDeclared). Для прочих классов конфиг не должен молча выкидывать
    //     живых кандидатов (Major 3 ревью: оценка измерена токенизатором другой модели, расхождение
    //     10–30%), вмещает с запасом ContextCapacityMargin.
    // Нет ни того, ни другого (модель не описана, наблюдений нет) — fail-open: лучше попробовать,
    // чем молча сдаться. Контекст <= 0 — тоже fail-open (оценки нет, фильтровать нечем).
    private bool WouldFit(string? model, int contextTokens, bool checkDeclared)
    {
        if (contextTokens <= 0) return true;
        // Наблюдённый размер отказа: контекст СТРОГО меньше — иначе модель точно упадёт. Всегда.
        if (_capacity?.ObservedCeiling(model) is { } observed && contextTokens >= observed) return false;
        // Заявленное окно: вмещает с запасом на расхождение токенизаторов. Только при ContextOverflow.
        if (checkDeclared
            && _providers?.ResolveByModel(model)?.FindModel(model)?.ContextWindow is { } declared
            && declared < contextTokens * ContextCapacityMargin) return false;
        // Нет ни заявленного, ни наблюдённого — fail-open.
        return true;
    }

    // Кандидаты уровня 1: сначала штатный Pick (с учётом исчерпания, SupportsModel,
    // тарифа и утилизации), затем последовательно остальные подписки пула. P31: auth-dead
    // отсеивается явно — Pick их исключает, но перебор остальных без этого фильтра гонял бы
    // ход на заведомо мёртвые аккаунты (запуск CLI + перенос транскрипта + отказ впустую).
    private IEnumerable<string> SubscriptionCandidates(string? model, string currentKey)
    {
        var pick = _pool.Pick(model);
        if (pick != currentKey && !_pool.IsExhausted(pick) && !_pool.IsAuthDead(pick) && _pool.SupportsModel(pick, model))
            yield return pick;
        foreach (var sub in _pool.All)
        {
            if (sub.Key == currentKey || sub.Key == pick) continue;
            if (_pool.IsExhausted(sub.Key) || _pool.IsAuthDead(sub.Key) || !_pool.SupportsModel(sub.Key, model)) continue;
            yield return sub.Key;
        }
    }

    // Перенос транскрипта в профиль целевой пары (тот же механизм, что у ручной миграции
    // MigrateProviderAsync и автофейловера TryPoolFailover). Ход ещё не начинался —
    // переносить нечего. Ошибка копирования — кандидат пропускается (fail-closed)
    private bool TryMigrateTranscript(string? dstRoot)
    {
        if (Info.ClaudeSessionId is null) return true;
        if (_profileRoot is null || dstRoot is null) return false;
        if (ResolveCwd() is not { } cwd) return false; // папка вне монтирований песочницы
        // Транскрипта нет в источнике ВООБЩЕ: CLI умер до первой записи в .jsonl (мгновенная
        // AuthFailure — основной сценарий фолбэка: 401 приходит за ~100-400мс, CLI не успевает
        // создать файл). Переносить нечего — кандидат допустим: --resume на нём начнёт разговор
        // так же, как начал бы его и текущая пара. Fail-closed здесь отсекал ВСЕХ кандидатов и
        // делал AuthFailure-фолбэк структурно невозможным (инцидент 14.08.2026: ход с
        // «OAuth session expired» не ушёл по цепочке при живых шагах). Сам
        // TranscriptMigrator.TryMigrate остаётся fail-closed — для явной миграции (SwitchProvider)
        // «файла нет» должно быть ошибкой; здесь меняется только интерпретация кандидата.
        if (TranscriptMigrator.FindTranscript(_profileRoot, cwd, Info.ClaudeSessionId) is null)
        {
            LogInfo($"Транскрипт {Info.ClaudeSessionId} не найден в {_profileRoot} — CLI умер до первой записи, переносить нечего");
            return true;
        }
        if (!TranscriptMigrator.TryMigrate(_profileRoot, dstRoot, cwd, Info.ClaudeSessionId, out var error))
        {
            LogWarn($"Транскрипт {Info.Id} не перенесён {dstRoot}: {error}");
            return false;
        }
        return true;
    }

    // Обратный перенос транскрипта в профиль исходного провайдера при restore после смены типа
    // поставщика (IsProviderSwitch). Финальный ответ хода CLI записал только в профиль ПОДМЕНЫ
    // (_profileRoot); restore вернёт Info.Provider в исходный — следующий ход на --resume из
    // исходного профиля не нашёл бы ответ модели. Копируем туда, где его ждёт resume. Провал
    // не роняет ход, но пишем Warning с обоими путями (иначе потеря памяти диалога останется
    // незамеченной). cwd — тот же, что у прямого переноса (TryMigrateTranscript).
    private bool TryCopyTranscriptBack(string? dstRoot, bool preserveLongerDestination = true)
    {
        if (Info.ClaudeSessionId is null) return true;
        if (_profileRoot is null || dstRoot is null) return false;
        if (ResolveCwd() is not { } cwd)
        {
            LogWarn($"Обратный перенос транскрипта {Info.Id} не удался: папка вне монтирований песочницы (src={_profileRoot}, dst={dstRoot})");
            return false;
        }
        // preserveLongerDestination: не затираем более полную историю приёмника усечённым
        // источником (Minor 1). Skip (приёмник длиннее) возвращается как true, но с причиной в
        // error — логируем её как Warning, иначе потеря полной истории осталась бы незамеченной.
        // НО когда ход ПРОШЁЛ под подменой (caller передаёт preserve=false), источник = профиль
        // последней пары — там лежит СВЕЖИЙ ответ, которого у приёмника нет. «Приёмник длиннее»
        // здесь ложный сигнал (старая история + висящий turn без ответа), и preserve привело бы к
        // расхождению копий: на следующем ходе --resume из приёмника реплеит висящий turn = дубль
        // реакции (инцидент 2026-08-10, доп. №5). Поэтому успешный ход → перезапись (preserve=false).
        if (!TranscriptMigrator.TryMigrate(_profileRoot, dstRoot, cwd, Info.ClaudeSessionId, out var error,
                preserveLongerDestination: preserveLongerDestination))
        {
            LogWarn($"Обратный перенос транскрипта {Info.Id} не удался: src={_profileRoot}, dst={dstRoot}: {error}");
            return false;
        }
        if (error is not null)
            LogWarn($"Обратный перенос транскрипта {Info.Id}: src={_profileRoot}, dst={dstRoot}: {error}");
        return true;
    }

    // cwd хода для переноса транскрипта: хостовый для local, контейнерный — для песочницы
    // (CLI уплощает КОНТЕЙНЕРНЫЙ путь). null — папка вне монтирований песочницы.
    private string? ResolveCwd()
    {
        try { return _launcher is { IsSandboxed: true } ? _launcher.Paths.ToRuntime(_rootPath) : _rootPath; }
        catch (InvalidOperationException) { return null; }
    }

    // Применение выбранной пары: смена подписки двигает только Provider, смена провайдера —
    // и модель (эквивалент по слоту). Следующий ход пересоберёт env и процесс сам:
    // оверрайды подписки читаются из Info.Provider, модель — из Info.Model на каждый ход
    private void ApplyTarget(FallbackTarget next)
    {
        if (next.IsProviderSwitch) Info.Model = next.Model;
        Info.Provider = next.Key;
        Info.UpdatedAt = DateTime.UtcNow;
        _profileRoot = next.ProfileRoot;
        // Персистентность подхватится ближайшим SaveSessions (статус хода меняется на
        // финале) — своего сохранения у адаптера нет, им владеет SessionManager
    }

    // Текущий ключ пары: провайдер по модели (приоритет), затем Provider сессии —
    // тот же порядок, что у MigrateProviderAsync при вычислении currentKey
    private string CurrentProviderKey()
    {
        var byModel = _providers?.ResolveByModel(
            string.IsNullOrWhiteSpace(Info.Model) ? null : Info.Model)?.Key;
        return byModel ?? (string.IsNullOrEmpty(Info.Provider)
            ? ClaudeSubscriptionPool.PrimaryKey : Info.Provider);
    }

    // Хостовый физический корень профиля CLI для ключа пары (источник/приёмник переноса).
    // Раскладка зеркалит сборку env хода (ClaudeSession + BuildCliEnv/BuildOAuthCliEnv)
    // и её перепись для песочницы (DockerProcessRunner.RewriteProfileEnv)
    private string? ResolveRootFor(string key)
    {
        if (_providers?.GetByKey(key) is not null)
            return SandboxAwareRoot(key, _providers.GetProfileDir(key));
        if (_pool.All.Any(s => s.Key == key && s.Enabled))
            return SandboxAwareRoot("sub-" + key, _providers?.GetProfileDir("sub-" + key));
        // Первичный аккаунт без записи пула (или запись без креденшалов) — ~/.claude
        return SandboxAwareRoot("default", _providers?.UserProfileDir);
    }

    // Local — как есть; container-пользователь — data/sandbox-profiles/{ownerId}/{папка}:
    // родитель выводится из корневого пути старта сессии (SessionManager.ConfigRootFor
    // уже разрешил раскладку песочницы для текущей подписки)
    private string? SandboxAwareRoot(string folder, string? localRoot)
    {
        if (_launcher is not { IsSandboxed: true }) return localRoot;
        if (_initialProfileRoot is null) return null;
        var parent = Path.GetDirectoryName(_initialProfileRoot.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, folder);
    }

    // Финал ошибки, когда запасных вариантов не нашлось с первой попытки (нет пула и цепочки —
    // одиночная подписка/провайдер): задержанный result уходит как error, а не как есть. Иначе
    // провал маскируется finished: CLI при API-ошибке отдаёт subtype=success + is_error, и
    // SessionManager по нему ставил Active (исходная половина P29). Причину ход не состоялся —
    // выпускаем сами: задержанный ErrorMessage(ExpectResultFollows) идёт ПЕРЕД result.
    // Используется только когда attempted.Count == 1: перепробованы несколько пар → FailExhausted
    // (там список попыток и общий текст «ни одна не ответила»). Usage/cost сохраняем из реального
    // result — отказный запрос мог потратить токены (биллинг).
    private async Task FailClosedAsync(FallbackTurn turn, AttemptEnd end)
    {
        List<ServerMessage> held;
        lock (turn.Sync)
        {
            held = [.. turn.Held];
            turn.Held.Clear();
            turn.Settled = true;
        }
        var orig = held.OfType<ResultMessage>().FirstOrDefault();
        var result = orig is { Subtype: "error" }
            ? orig
            : new ResultMessage("error", orig?.DurationMs ?? 0, orig?.NumTurns ?? 0,
                orig?.Usage, orig?.TotalCostUsd,
                ApiErrorStatus: orig?.ApiErrorStatus ?? end.Result?.ApiErrorStatus);
        // Ход реально не состоялся — задержанную ошибку показываем, и строго ПЕРЕД финальным
        // result. Порядок инвариантен: SessionManager по паре ErrorMessage{ExpectResultFollows}
        // → ResultMessage разбирает конец хода ровно один раз (SessionEntry.SkipNextTeamTurnEnd
        // выставляется ошибкой и гасится спаренным result). Тот же порядок был и раньше, когда
        // ошибка уходила downstream в момент получения.
        // ТРЕБОВАНИЕ К СЛЕДУЮЩЕЙ ПОД-ЗАДАЧЕ (SessionManager, его тут не правим): парная логика
        // остаётся корректной — при подмене НИ ошибка, НИ result попытки downstream не идут,
        // так что флаг не выставляется и не остаётся висеть.
        foreach (var m in held.OfType<ErrorMessage>().Where(e => e.ExpectResultFollows))
            await _downstream(m);
        await _downstream(result);
        foreach (var m in held.Where(m => m is not ResultMessage and not ErrorMessage { ExpectResultFollows: true }))
            await _downstream(m);
    }

    // Финал при исчерпании цепочки: человекочитаемый текст в ленту, финальный result —
    // ошибочный (у задачи исполнителя существующий путь пометит сбой и уведомит
    // постановщика; в исходный статус задача не возвращается). Счётчик подмен, потолок,
    // модель/ключ и текст последней ошибки — в LogInfo для разбора, человеку они не нужны.
    private async Task FailExhaustedAsync(FallbackTurn turn, IReadOnlyList<AttemptTrace> trace,
        int substitutions, AttemptEnd lastEnd)
    {
        // Переполнение контекста: перепробованы все шаги цепочки, и ни одна модель не вместила
        // текущий разговор. Текст — про окно и действия (/compact / смена модели), а не общая
        // «модели не ответили»: причина иная (контекст слишком велик, а не эндпоинты лежат).
        // Формулировка согласована с продуктовым аналитиком (Софья): честнее «ни одна из
        // доступных моделей не смогла», т.к. фолбэк мог пройти через 2–3 модели до отказа.
        // Задержанные терминалы забираем СРАЗУ: подмены за последней попыткой не последовало,
        // исход хода решён — и её ошибка (если была) уходит в ленту перед итоговым текстом,
        // сохраняя хронологию «причина → вердикт → result».
        List<ServerMessage> held;
        lock (turn.Sync)
        {
            held = [.. turn.Held];
            turn.Held.Clear();
            turn.Settled = true;
        }
        foreach (var m in held.OfType<ErrorMessage>().Where(e => e.ExpectResultFollows))
            await _downstream(m);

        var overflow = trace.Count > 0 && trace[^1].Class == FallbackErrorClass.ContextOverflow;
        if (overflow)
        {
            await _downstream(new ErrorMessage(
                "Разговор слишком велик — ни одна из доступных моделей не смогла его обработать.\n\n"
                + "Сожмите контекст командой /compact или выберите модель с бóльшим окном в настройках чата."));
        }
        else
        {
            // Три блока: заголовок, по строке на попытку (поставщик и причина), подсказка.
            // Блоки разделены пустой строкой, строки попыток — \n: ChatItemView рендерит
            // переносы в error-сообщениях (white-space: pre-wrap), так что обходной
            // разделитель « · » больше не нужен. Служебные термины и ключи сюда не попадают.
            var attempts = string.Join("\n", trace.Select(t => $"{ProviderLabel(t.Key)} — {UserClassLabel(t.Class)}"));
            await _downstream(new ErrorMessage(
                "Ни одна из доступных моделей не ответила.\n\n"
                + attempts + "\n\n"
                + "Попробуйте позже или выберите другую модель в настройках чата."));
        }

        var reason = string.IsNullOrEmpty(lastEnd.Result?.ApiErrorStatus)
            ? lastEnd.ErrorText ?? "поток прерван"
            : lastEnd.Result!.ApiErrorStatus!;
        LogInfo($"Исчерпание фолбэка: подмен {substitutions}, потолок {EffectiveMaxSubstitutions()}. "
            + $"Пары: {string.Join("; ", trace.Select(t => TraceLine(t.Model, t.Key, t.Class)))}. "
            + $"Последняя ошибка: {Truncate(reason, 300)}");

        // Реальный result последней попытки со subtype=error выпускаем как есть;
        // success-с-API-ошибкой заменяем ошибочным — иначе сбой выглядел бы успехом
        var result = held.OfType<ResultMessage>().FirstOrDefault(r => r.Subtype == "error")
            ?? new ResultMessage("error", 0, 0, null, null, ApiErrorStatus: lastEnd.Result?.ApiErrorStatus);
        await _downstream(result);
        // Ошибки попытки уже ушли выше — здесь только остальные терминалы (exited и т.п.)
        foreach (var m in held.Where(m => m is not ResultMessage and not ErrorMessage { ExpectResultFollows: true }))
            await _downstream(m);
    }

    // Финал без подмены (успех / неизвестная ошибка / interrupt): задержанные
    // терминальные сообщения попытки уходят наружу как есть
    private async Task SettleAsync(FallbackTurn turn)
    {
        List<ServerMessage> held;
        lock (turn.Sync)
        {
            held = [.. turn.Held];
            turn.Held.Clear();
            turn.Settled = true;
        }
        foreach (var m in held) await _downstream(m);
    }

    private async Task ForwardThenAsync(ServerMessage msg, Action after)
    {
        await _downstream(msg);
        after();
    }

    private static string TraceLine(string model, string key, FallbackErrorClass cls) =>
        $"{(string.IsNullOrEmpty(model) ? "модель по умолчанию" : $"модель «{model}»")} × «{KeyLabel(key)}» — {ClassLabel(cls)}";

    // Технические метки классов — в лог (для разбора), не в ленту. С человеческими
    // формулировками для пользователя см. UserClassLabel.
    private static string ClassLabel(FallbackErrorClass cls) => cls switch
    {
        FallbackErrorClass.RateLimit => "лимит запросов",
        FallbackErrorClass.UsageLimit => "лимит использования",
        FallbackErrorClass.ProviderError => "ошибка провайдера",
        FallbackErrorClass.Unreachable => "эндпоинт недоступен",
        FallbackErrorClass.AuthFailure => "ошибка авторизации",
        _ => "ошибка",
    };

    // Человекочитаемые причины для ленты (постановка задачи). Wire-имена классов и
    // технические метки (ClassLabel/WireName) не трогаем — они для разбора и фронта.
    private static string UserClassLabel(FallbackErrorClass cls) => cls switch
    {
        FallbackErrorClass.RateLimit => "слишком много запросов",
        FallbackErrorClass.UsageLimit => "закончился лимит",
        FallbackErrorClass.ProviderError => "поставщик вернул ошибку",
        FallbackErrorClass.Unreachable => "сервис не отвечает",
        FallbackErrorClass.AuthFailure => "не удалось авторизоваться",
        _ => "не удалось выполнить",
    };

    private static string KeyLabel(string key) =>
        key == ClaudeSubscriptionPool.PrimaryKey ? "claude" : key;

    // Имя поставщика для пользовательского текста: DisplayName подписки пула, затем
    // DisplayName провайдера, иначе — ключ (через KeyLabel). Подписка пула без DisplayName —
    // фолбэк «Аккаунт Claude»: имя подписки задаётся только локально (appsettings.Local.json,
    // у большинства машин пусто), сырой ключ («acc-2») в текст пользователю не показываем.
    // У сторонних провайдеров DisplayName есть всегда (appsettings.json) — они сюда не доходят.
    private string ProviderLabel(string key)
    {
        var sub = _pool.All.FirstOrDefault(s => s.Key == key);
        if (sub is not null)
            return !string.IsNullOrWhiteSpace(sub.DisplayName) ? sub.DisplayName : "Аккаунт Claude";
        var provider = _providers?.GetByKey(key);
        if (provider is not null && !string.IsNullOrWhiteSpace(provider.DisplayName))
            return provider.DisplayName;
        return KeyLabel(key);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // Исход одной попытки
    private enum AttemptEndKind { Result, ProcessGone, FatalError, RateLimited }

    private sealed record AttemptEnd(
        AttemptEndKind Kind,
        ResultMessage? Result,
        string? ErrorText,
        string? RateLimitResetsAt);

    // След одной попытки для финального сообщения: модель и ключ — в лог (через
    // TraceLine), поставщик (ProviderLabel) и причина (UserClassLabel) — в ленту.
    private sealed record AttemptTrace(string Model, string Key, FallbackErrorClass Class);

    private sealed record FallbackTarget(
        string Key, string? Model, string? Label, string? ProfileRoot, bool IsProviderSwitch,
        // Текст тихой подмены (смена подписки того же пула, Label=null) для LogInfo: в ленту
        // не идёт, но нужен для разбора — какая подписка на какую сменилась.
        string? LogDetail = null);

    // Состояние хода под фолбэком: задержанные терминальные сообщения, исход текущей
    // попытки и флаг «оркестрация завершена» (после него события идут насквозь)
    private sealed class FallbackTurn
    {
        public readonly object Sync = new();
        public TaskCompletionSource<AttemptEnd> AttemptTcs = NewTcs();
        public bool Settled;
        // Процесс попытки прерван ротацией (rate-limit пауза): следующие Exited/ErrorMessage
        // этой попытки — уборка, их глотаем до начала следующей попытки
        public bool SwallowCleanup;
        public bool AttemptResolved;
        // Снимок ClaudeSession.SubmittedTurnSeq на начало попытки: exited прогона с меткой не
        // больше этой — терминал чужого прогона (см. IsOwnExited). 0 — метки недоступны.
        public long AttemptTurnSeq;
        public List<ServerMessage> Held = [];
        public string? ErrorText;
        public string? RateLimitResetsAt;

        public static TaskCompletionSource<AttemptEnd> NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Новая попытка: чистый TCS и сброшенные признаки (задержанное предыдущей
        // попыткой уже решено оркестратором)
        public void BeginAttempt(long submittedTurnSeq = 0)
        {
            AttemptTcs = NewTcs();
            AttemptTurnSeq = submittedTurnSeq;
            AttemptResolved = false;
            SwallowCleanup = false;
            Held.Clear();
            ErrorText = null;
            RateLimitResetsAt = null;
        }

        public void NoteResult(ResultMessage res)
        {
            Held.Add(res);
            ResolveAttempt(AttemptEndKind.Result, res);
        }

        public void NoteErrorText(string text)
            => ErrorText = string.IsNullOrEmpty(ErrorText) ? text : ErrorText + "\n" + text;

        public void Hold(ServerMessage msg) => Held.Add(msg);

        public void ResolveAttempt(AttemptEndKind kind, ResultMessage? result = null)
        {
            if (AttemptResolved) return;
            AttemptResolved = true;
            AttemptTcs.TrySetResult(new AttemptEnd(kind, result, ErrorText, RateLimitResetsAt));
        }
    }
}
