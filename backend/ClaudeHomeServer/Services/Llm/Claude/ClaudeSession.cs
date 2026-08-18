using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Telemetry;

namespace ClaudeHomeServer.Services.Llm.Claude;

public class ClaudeSession : ILlmSessionAdapter
{
    public Session Info { get; }

    // По модели: сторонний CLI-провайдер отдаёт свои возможности (SupportsImages и т.п.)
    public LlmCapabilities Capabilities =>
        _providers?.CapabilitiesFor(EffectiveModel) ?? LlmCapabilitiesCatalog.Claude;

    // Модель, которой реально пойдёт ход. Пустая Info.Model означает «по назначению места»
    // (слоты тиров + таблица назначений, ModelAssignmentResolver), а не дефолт claude CLI.
    // Резолвим на каждом обращении, а не при создании адаптера: смена настройки применяется
    // со следующего хода, чат её не «замораживает». Итоговый null = слот пуст, решает CLI.
    private string? EffectiveModel => _assignments?.Resolve(UsageKey, Info.Model, Info.OwnerId) ?? Info.Model;

    // Эффективная модель для слоя фолбэка (FallbackLlmSessionAdapter): пара «модель × подписка»
    // учитывается по модели, которой реально идёт ход. Сам резолв остаётся приватным.
    internal string? EffectiveTurnModel => EffectiveModel;

    // Модель для --model / set_model: суффикс [1m] тир-алиаса остаётся, пока в пуле есть живой
    // кандидат с поддержкой 1M-окна; иначе срезается в базовый алиас (деградация в 200K вместо
    // падения хода на аккаунте без доступа — см. ClaudeSubscriptionPool.ResolveWindowAlias).
    // Пул не задан (нет подписок) — срезаем безусловно: локальный вход не описан в конфиге, и
    // безопаснее идти в надёжном 200K, чем рисковать падением на неизвестном аккаунте.
    private string? ResolveModelForCli(string? model) =>
        _subscriptionPool?.ResolveWindowAlias(model) ?? LlmProviderRegistry.StripClaudeWindowAlias(model);

    // Цепочка хода для фолбэка (ADR-007 §4): упорядоченные конкретные модели пресета (первая =
    // основная, остальные = план подмен). Пустая Info.Model → резолв по месту мог дать пресет;
    // цепочка нужна оркестратору, чтобы при сбое шагать по ней, а не автоподбирать. Без резолвера
    // (тесты) — один элемент (эффективная модель), т.е. цепочки нет.
    internal IReadOnlyList<string> EffectiveTurnChain =>
        _assignments?.ResolveChain(UsageKey, Info.Model, Info.OwnerId)
        ?? (EffectiveModel is { } m ? new[] { m } : Array.Empty<string>());

    // Размер контекста последнего запроса для слоя фолбэка (оценка заполнения окна): при
    // ContextOverflow оркестратор по нему решает, вместит ли кандидат из цепочки текущий
    // разговор. Для нового хода — последнее известное значение чата (контекст растёт плавно,
    // прошлый ход — хорошая оценка). Сам резолв остаётся приватным, наружу — только чтение.
    internal int LastContextTokens => _lastContextTokens;

    // Место применения сессии — порядок как в SessionManager.UsageKeyFor
    private string UsageKey =>
        Info.TaskExecution || Info.TaskId is not null ? LocalActionCatalog.TasksExecutor
        : !string.IsNullOrWhiteSpace(Info.PersonaId) ? LocalActionCatalog.ChatPersona
        : LocalActionCatalog.ChatNew;

    // Состояние делегирования идущего хода — по нему бэкенд гейтит действия MCP-серверов
    // (DenyOnDelegatedTurnAttribute), не трогая СОСТАВ их инструментов
    public int CurrentTurnAgentDepth => _currentTurnAgentDepth;
    public bool CurrentTurnSuppressTasksExecute => _currentTurnSuppressTasksExecute;

    private readonly string _rootPath;
    private readonly Func<ServerMessage, Task> _onMessage;
    // Словари ниже — Concurrent: их мутируют и памп stdout, и SignalR-вызовы
    // (RespondPermission/AnswerQuestion/RespondPlan/Interrupt) параллельно
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _permissionWaiters = new();
    // Инструменты, для которых пользователь выбрал «всегда разрешать» в этой сессии (значение не используется)
    private readonly ConcurrentDictionary<string, byte> _autoAllowTools = new();
    // tool_use_id → request_id вопросов AskUserQuestion (приходят как control_request can_use_tool, ждут control_response)
    private readonly ConcurrentDictionary<string, string> _pendingQuestions = new();
    // request_id → исходный input ожидающего согласования ExitPlanMode (режим «План»)
    private readonly ConcurrentDictionary<string, object> _pendingPlans = new();

    // Прогон ждёт control_response от пользователя (permission / AskUserQuestion / план).
    // В этом окне смерть CLI — реальный обрыв живого хода, а не same-process-гонка: ретраить
    // ту же пару бессмысленно (ответа нет и не будет), карточка в ленте зависнет до таймаута.
    // На этот признак опираются HandleProcessExitedAsync/FinalizeRunAsync (P30).
    private bool HasPendingControlResponse()
        => !_permissionWaiters.IsEmpty || !_pendingQuestions.IsEmpty || !_pendingPlans.IsEmpty;

    // Признак ожидания control_response на момент смерти прогона — с фиксацией на прогоне (P31).
    // Смерть детектируется двумя обработчиками: HandleProcessExitedAsync по событию ОС (обычно
    // первым) и FinalizeRunAsync по закрытию stdout. Оба решают по pending, ретраить ли ход и
    // слать ли ошибку. Без фиксации они читали бы живые словари — а первый же обработчик, пославший
    // ошибку, зовёт CancelPendingControlResponses и очищает их. Итог зависел бы от порядка вызовов:
    //   • Exited первым: FinalizeRunAsync видел бы пустые словари → ложный ретрай (дубль: ошибка
    //     пользователю + переигранка новым процессом — двойная оплата);
    //   • EOF первым: HandleProcessExitedAsync выходит по DeathDiagnosed до CancelPending →
    //     pending залипал бы в сессии навсегда (каждая следующая смерть рапортовала «во время
    //     ожидания разрешения»).
    // Фикс: первый заметивший pending выставляет run.PendingControlAtDeath, оба читают его.
    private bool ResolvePendingControlAtDeath(CliRun run)
    {
        if (run.PendingControlAtDeath) return true;
        if (HasPendingControlResponse())
        {
            run.PendingControlAtDeath = true;
            return true;
        }
        return false;
    }

    // Отменить ожидающие control_response — вызывается при смерти процесса (P30) и в Interrupt().
    // TrySetCanceled выводит DecidePermissionAsync из WaitAsync → возвращает "cancelled" → control_response
    // не пошлётся, поток reader'а доедет до EOF штатно.
    private void CancelPendingControlResponses()
    {
        foreach (var tcs in _permissionWaiters.Values)
            tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        _pendingQuestions.Clear();
        _pendingPlans.Clear();
    }
    // Гарантированное исполнение одобренного плана:
    // после approve ждём реализацию; если ход завершится без правок — дошлём команду.
    private volatile bool _awaitPlanExecution;
    private volatile bool _sawToolSinceApprove;
    // Следующий ход запустить без --permission-mode plan (исполнение одобренного плана)
    private volatile bool _forceNonPlanNextTurn;
    // Текущий (последний) ход убит по воле пользователя: кнопка «Стоп» или прерывание ради
    // очереди (SessionManager.Interrupt / PreemptTurnForQueue). Смерть процесса в этом случае
    // ОЖИДАЕМАЯ — HandleProcessExitedAsync не должен слать ErrorMessage «Процесс модели
    // завершился во время хода»: маркер «Ход остановлен пользователем» уже стоит в ленте,
    // и красная плашка рядом с ним — ложная (два элемента вместо одного).
    // Сброс — ТОЛЬКО в начале следующего хода (RunTurnAsync), не в Interrupt и не в
    // финализации: событие Exited приходит асинхронно ПОСЛЕ Interrupt, и ранний сброс вернул бы
    // гонку с ложной ошибкой. Обратная сторона — залипший флаг заглушил бы настоящую смерть
    // процесса (P27: чат висит в «ожидании» до watchdog), поэтому точка сброса ровно одна.
    private volatile bool _interruptedByUser;
    // Глубина делегирования текущего хода: > 0 — ход инициирован агентом из другой сессии
    // (chats_send). Выставляется в начале RunTurnAsync и сбрасывается после хода;
    // при глубине >= 1 BuildTurnMcpConfig урезает инструменты делегирования (анти-рекурсия)
    private volatile int _currentTurnAgentDepth;
    // Реакционный авто-ход постановщика на доклад делегированной задачи (TaskExecutionService.
    // ReportToDelegatorAsync) — отдельный от agentDepth флаг: ход обычного пользовательского
    // чата (agentDepth=0), но tasks_run_executor всё равно должен быть недоступен, иначе A может
    // сам себе запустить только что созданную задачу → новый доклад → новая реакция →
    // бесконечный платный цикл A↔B. Выставляется/сбрасывается вместе с _currentTurnAgentDepth.
    private volatile bool _currentTurnSuppressTasksExecute;
    // Стриминг tool_use: индекс content-блока → (id инструмента, накопленный partial_json).
    // Concurrent — для видимости между потоками пампа разных ходов
    private readonly ConcurrentDictionary<int, (string Id, System.Text.StringBuilder Sb)> _toolStream = new();
    // Контекст последнего запроса к API (input + cache_read + cache_creation из usage
    // последнего assistant-сообщения ОСНОВНОГО агента) — оценка заполнения окна для клиента.
    // Обновляется на каждом шаге tool-лупа, уезжает в ResultMessage.ContextTokens.
    private volatile int _lastContextTokens;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    // Диагностика повторных доставок (инцидент 2026-08-10): счётчик ходов, прошедших через
    // постановку в QueueTurnAsync и не отдавших _turnLock. На момент постановки новой задачи
    // показывает, сколько ходов УЖЕ запарковано/идёт. >0 для серверского авто-хода — симптом
    // дублирующей доставки (должна идти через видимую серверную очередь, а не этот невидимый лок).
    private int _queuedTurns;
    // Ре-аттемпт хода фолбэком не должен перепосылать текст в stdin: первый submit уже durable
    // в .jsonl транскрипта (CLI пишет синхронно с приёмом из stdin, на репро enqueue→dequeue
    // мгновенны), и повторный submit на новом процессе --resume создал бы второй user-turn
    // того же текста — дубль, видимый модели (инцидент 2026-08-10). Запоминаем текст последнего
    // submit'а, снят ли он result'ом и был ли он durable-записан новым процессом; незавершённый
    // ре-аттемпт тем же текстом идёт без submit — CLI доиграет висящее через --resume.
    private string? _lastSubmittedTurnText;
    private volatile bool _lastTurnResolved = true;
    private bool _lastSubmitWasNewProcess;
    // Сериализует записи в stdin процесса: control_response шлются из SignalR-потоков
    // параллельно с пампом — без лока JSON-строки могут перемешаться
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private Process? _currentProcess;

    // Ватчеры фоновых Workflow (по одному на каждый запущенный workflow в сессии)
    private readonly List<WorkflowWatcher> _workflowWatchers = [];
    // Полный поток inline-сабагентов из их транскриптов (CLI шлёт в stdout только tool_use);
    // создаётся на system/init каждого хода, диспозится по завершении процесса
    private SubagentStreamWatcher? _subagentWatcher;

    // Максимальная тишина stdout активного хода: при живой работе (генерация, инструмент,
    // субагент, компакция, ожидание пользователя) CLI шлёт события регулярно; полное молчание
    // 60 мин — крайняя защита от вечно висящего процесса (напр. провайдер оборвал стрим, а CLI
    // не завершился). Реальный обрыв, при котором CLI сам падает/выходит, ловится раньше в цикле
    // чтения (result/EOF/исключение). Не занижаем: короткий порог ложно рубил бы долгие
    // инструменты, субагентов OMO/workflow, компакцию и медленные ответы провайдера.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

    // Грейс после result: штатно CLI выходит сам, но плагинные хуки/MCP-мосты (наблюдалось
    // с oh-my-claudecode) могут держать процесс живым бесконечно — тогда завершаем ход сами,
    // не дожидаясь часового watchdog.
    private static readonly TimeSpan ResultExitGrace = TimeSpan.FromSeconds(15);

    // Расширенный грейс для прогонов с --prompt-suggestions: CLI генерит подсказку ПОСЛЕ
    // result (замер: ~9с на лёгком ходе, на тяжёлых дольше) — 15с обрывали её на середине.
    // Цена расширения — лишнее ожидание только в аварийном случае (CLI сам не вышел).
    private static readonly TimeSpan PromptSuggestionExitGrace = TimeSpan.FromSeconds(45);

    // Грейс на старт хода-продолжения после завершения последней фоновой задачи. Между
    // task_notification и первым stream_event продолжения ContinuationActive ещё не взведён,
    // а ResultExitGrace (15с) мог бы рубить разгон продолжения под нагрузкой/ретраях провайдера.
    // Состояние «result отдан, фоновых задач нет, stdin ещё открыт» бывает ТОЛЬКО в этом окне:
    // при результате хода без фоновых задач stdin закрывается сразу (case "result"), а здесь он
    // открыт именно потому, что доживали bg-агенты — их task_notification запускает продолжение.
    private static readonly TimeSpan ContinuationStartGrace = TimeSpan.FromMinutes(2);

    // Потолок доживания процесса с работающими фоновыми агентами после конца хода.
    // Агенты (Agent run_in_background, Workflow) живут ВНУТРИ процесса CLI: убить его
    // по грейсу — значит убить их на середине (наблюдалось на проде: task-notification
    // «status=stopped» у всех агентов длиннее 15 секунд). Значение — из Claude:BgLingerMinutes.
    // Потолок доживания процесса с фоновыми агентами после конца хода. Инстансное поле
    // (из конфига через фабрику) — раньше был public static settable, мутируемый как скрытый
    // сайд-эффект конструктора фабрики (глобальное общее состояние на весь процесс).
    private readonly TimeSpan _bgLingerTimeout;

    // Процессный прогон: один запуск claude CLI. Может пережить ход — пока в нём доживают
    // фоновые агенты, процесс не убиваем, а следующий совместимый ход отдаём ему же в stdin
    // (stream-json это штатно поддерживает). Поля мутирует поток чтения stdout (reader);
    // исключения помечены у полей.
    private sealed class CliRun
    {
        public required Process Process { get; init; }
        // Сигнатура окружения запуска (модель/режим/env/набор MCP/слой персоны) — следующий
        // ход можно отдать живому процессу только при полном совпадении (см. BuildLaunchSignature)
        public required string Signature { get; init; }
        public string? TurnMcpPath { get; init; }
        // turnId запуска — по нему pid-файл прогона в песочнице (Kill контейнерного pgid)
        public string? LaunchTurnId { get; init; }
        // Снимок промпта, с которым прогон СТАРТОВАЛ. Ходы, доигрывающиеся в этом же процессе,
        // ссылаются на него (inheritedFromId): их собственный промпт модели не уходил.
        public string? PromptSnapshotId { get; init; }
        public Task? ReaderTask { get; set; }
        // Смерть прогона зафиксирована диагностирована (маркер в лог + ErrorMessage клиенту).
        // Взводит HandleProcessExitedAsync по событию Exited (опережает финализацию), либо — для гонки
        // EOF-раньше-callback — ветка activeTurnDied в FinalizeRunAsync. Гарантирует ровно один
        // маркер смерти и одно сообщение об ошибке на прогон (см. P27).
        public volatile bool DeathDiagnosed;
        // P31: прогон на момент смерти ждал control_response (permission / AskUserQuestion / план).
        // Фиксируется на прогоне при первом обнаружении (см. ResolvePendingControlAtDeath), чтобы
        // оба обработчика смерти (HandleProcessExitedAsync по событию ОС, FinalizeRunAsync по EOF)
        // читали стабильное значение, а не живые мутируемые словари: первый же обработчик, шлёший
        // ошибку, зовёт CancelPendingControlResponses и опустошает словари — без фиксации второй
        // обработчик видел бы false и ретраил ход (дубль: ошибка + переигранка), либо pending
        // залипал до конца сессии (блокер ревью P30).
        public volatile bool PendingControlAtDeath;
        // Ход завершён (result без parent_tool_use_id получен); между ходами true.
        // Сбрасывает поток нового хода в TrySubmitTurn (под _stdinLock)
        public volatile bool TurnDone;
        // Резолвится reader'ом на result текущего хода (или финализацией — процесс умер)
        public TaskCompletionSource TurnTcs { get; set; } = NewTcs();
        // Живые фоновые задачи прогона: agentId/runId из tool_result запуска → toolUseId
        // его карточки (для события bg_agent_done при завершении);
        // читается и потоками ходов — доступ под lock (PendingBg)
        public readonly Dictionary<string, string> PendingBg = [];
        // Фоновый запуск замечен, но id не распарсился — точный учёт невозможен,
        // доживание ограничено только потолком BgLingerTimeout
        public volatile bool PendingBgUnknown;
        // toolUseId фоновых запусков без распарсенного id — их карточки закрываем
        // только при финализации прогона (доступ под lock (PendingBg))
        public readonly HashSet<string> UnknownBgToolUses = [];
        // toolUseId вызовов Agent/Task c run_in_background и Workflow — ждём их tool_result,
        // чтобы достать id фоновой задачи
        public readonly HashSet<string> BgLaunchCandidates = [];
        // Между ходами CLI ведёт собственные ходы-продолжения (ответы на task-notification) —
        // контент после TurnDone означает, что продолжение началось. Его result не должен
        // завершать пользовательский ход (см. SkipResults)
        public volatile bool ContinuationActive;
        // Сколько ближайших result'ов принадлежит продолжениям, начатым ДО отправки
        // текущего пользовательского хода (инкремент в TrySubmitTurn под _stdinLock,
        // декремент — поток reader'а)
        public int SkipResults;
        public volatile bool StdinClosed;
        // Прогон убит ради несовместимого нового хода: ExitedMessage не слать —
        // статусом сессии владеет уже новый ход
        public volatile bool SuppressExited;
        // Ход получил хотя бы одно событие после submit (взводит ридер в ProcessLineAsync,
        // сбрасывает TrySubmitTurn). false на смерти прогона ДО первого события = гонка
        // TOCTOU (запись в stdin прошла, но CLI уже завершается); true = легитимный обрыв
        // посреди хода. Консервативно взводится на ЛЮБОЙ строке пока ход активен: любое
        // событие блокирует ретрай, иначе пересылка дублировала бы частичный вывод.
        public volatile bool TurnGotEvent;
        // Same-process ход может быть перезапущен при пустой смерти процесса (гонка TOCTOU):
        // выставляет TrySubmitTurn (только он уязвим — новый процесс, умерший пустым, = реальный
        // сбой старта). Читает FinalizeRunAsync вместе с TurnGotEvent — см. ShouldRetryEmptyExit.
        public volatile bool RetryOnEmptyExit;
        // Same-process ход отправлен в доживающий прогон БЕЗ активного continuation и БЕЗ фоновых
        // задач (фильтр SkipResults из Д1 неприменим — он защищает только режим continuation).
        // В таком окне процесс после result предыдущего хода уже завершается, и хвостовые события
        // этого обречённого процесса ненадёжны: они ложно взводят TurnGotEvent и маскируют гонку
        // TOCTOU под легитимный обрыв → ложный Unreachable → подмена модели (инцидент 2026-08-10,
        // П2). Смерть same-process хода без result в этом окне трактуется как гонка (тихий ретрай
        // той же парой) независимо от TurnGotEvent. Выставляет TrySubmitTurn в ветке без
        // ContinuationActive && !HasPendingBg; читает ShouldRetryEmptyExit.
        public volatile bool ReuseSubmit;
        // Финализация обнаружила пустую смерть same-process хода: RunTurnAsync по этому
        // признаку проваливается к запуску нового процесса на той же паре (один ретрай),
        // не отдавая смерть наружу. volatile — пишется потоком ридера, читается потоком хода.
        public volatile bool DiedEmpty;
        // Прогон запущен с --prompt-suggestions: после result ждём выхода CLI дольше
        // (PromptSuggestionExitGrace) — подсказка генерится и приходит после result
        public bool PromptSuggestionsActive { get; init; }
        // Номер ПОСЛЕДНЕГО хода, отданного этому прогону (ClaudeSession._turnSeq). Прогон живёт
        // дольше хода (same-process submit), поэтому метка обновляется на каждой подаче. Уезжает
        // наружу в ExitedMessage: по ней фолбэк отличает смерть своего прогона от чужой.
        // Пишет поток хода, читает reader — доступ через Interlocked.
        public long LastTurnSeq;

        public bool HasPendingBg
        {
            get { lock (PendingBg) return PendingBg.Count > 0 || PendingBgUnknown; }
        }

        public static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Текущий прогон; присваивает поток хода (под _turnLock), обнуляет финализация reader'а
    private CliRun? _run;

    // Сквозной номер ходов, отданных прогонам этой сессии: инкремент в RunTurnAsync на каждый
    // ход, значение садится в CliRun.LastTurnSeq того прогона, который ход принял (живой через
    // stdin или новый процесс). Монотонность — единственное требование: сравниваются только
    // метки одной сессии (см. ILlmSessionAdapter.SubmittedTurnSeq и ExitedMessage.TurnSeq).
    private long _turnSeq;

    public long SubmittedTurnSeq => Interlocked.Read(ref _turnSeq);

    // Прогон существует — чат занят по-настоящему. Доживание фоновых агентов сюда попадает,
    // но чат при нём уже Active, так что реанимации мёртвого Working это не мешает.
    public bool HasLiveTurn => _run is not null;

    // Ход принят, но прогона ещё нет: стоит на _turnLock либо поднимает процесс CLI.
    // Счётчик держит QueueTurnAsync — от постановки до выхода из RunTurnAsync.
    public bool HasQueuedTurn => Volatile.Read(ref _queuedTurns) > 0;

    // ClaudeSession сам по себе не ведёт отдельной «оркестрации поверх хода» — это фолбэк-адаптер
    // (FallbackLlmSessionAdapter) делает. Здесь всегда false: форсированный drain серверной
    // очереди не гейтится. См. ILlmSessionAdapter.OrchestrationActive (инцидент 2026-08-10 П3).
    public bool OrchestrationActive => false;

    // Доживающий прогон держит незавершённые фоновые задачи (run_in_background агенты, Workflow).
    // SessionManager гейтит этим terminus Active→Finished после result хода: пока фоновая работа
    // жива, Finished преждевременен (синтез панели экспертов может прилететь в ленту спустя минуты
    // — P12/P15). Тем же признаком пользуется CloseStdinIfIdle, чтобы не закрыть stdin у прогона с
    // живыми агентами. Чтение _run — как в HasLiveTurn: ссылка атомарна, устаревший в момент чтения
    // run даёт лишь консервативный «фоновые есть» (sweep пропустит), что безопаснее ложного terminus.
    public bool HasPendingBg
    {
        get { var run = _run; return run is not null && run.HasPendingBg; }
    }

    // Процесс завершил ход (result), но намеренно держат живым с открытым stdin: CLI ведёт
    // (ContinuationActive) или вот-вот начнёт (окно ContinuationStartGrace, см. ResolveWatchdog)
    // ход-продолжение — ответ на task_notification завершившегося фонового агента. Sweep-terminus в
    // SessionManager гейтит этим ложный Finished посреди разгоняющегося продолжения. Формула — калька
    // состояния «ContinuationStartGrace» из ResolveWatchdog (turnDone && !stdinClosed && !hasPendingBg)
    // ПЛЮС активное продолжение (ContinuationActive — тоже turnDone && !stdinClosed, но может быть и
    // с hasPendingBg, если параллельно работает ещё один bg-агент). Чтение _run — как в HasLiveTurn/
    // HasPendingBg: ссылка атомарна, устаревший в момент чтения run даёт лишь консервативное
    // «продолжение идёт» (sweep пропустит), что безопаснее ложного terminus.
    public bool IsContinuationInFlight
    {
        get
        {
            var run = _run;
            return run is not null
                && run.TurnDone && !run.StdinClosed
                && (run.ContinuationActive || !run.HasPendingBg);
        }
    }

    // Хвостовой ридер главного транскрипта: завершения фоновых задач (<task-notification>)
    // CLI пишет в транскрипт, в stdout завершённого хода их может не быть (проверено live) —
    // без ридера pending прогона не опустел бы и процесс висел бы до потолка BgLingerTimeout
    private MainTranscriptTailer? _transcriptTailer;

    // Коннекторы аккаунта claude.ai (Calendar, Drive, Gamma, Miro и др.) вливаются в каждую
    // сессию автоматически помимо --mcp-config — их нельзя убрать через конфиг. Блокируем
    // через --disallowedTools; список задаётся из конфига (Claude:DisallowedTools).
    private readonly string[] _disallowedTools;

    // Встроенные Task-инструменты Claude Code (Tasks-фича, синхронизация с claude.ai) —
    // дублируют наш MCP tasks-server. Пока tasks-server подключён, блокируем их через
    // --disallowedTools (см. сборку _disallowedTools в конструкторе), чтобы модель звала
    // mcp__tasks__*, а не пустой встроенный трекер. ВНИМАНИЕ: «Task» (без суффикса) —
    // это тул ЗАПУСКА СУБАГЕНТА (делегирование), его НЕ трогаем; только трекерные
    // TaskGet/TaskList/TaskCreate/TaskUpdate.
    //
    // ВНИМАНИЕ: раньше тут стояло «несуществующие claude молча проигнорирует» и список
    // содержал TaskComplete/TaskDelete/TaskSearch. Это допущение сломалось: CLI 2.1.x
    // ВАЛИДИРУЕТ имена в deny-правилах. В интерактивном режиме он ругается в stderr на
    // каждый ход, а в `--print` (one-shot) вообще падает с кодом 1 — так у нас разом легли
    // все ИИ-фичи из-за мёртвого MultiEdit. Мёртвые имена сюда не добавлять: список сверять
    // с реальным набором инструментов CLI при его обновлении.
    private static readonly string[] BuiltInTaskTools =
        ["TaskGet", "TaskList", "TaskCreate", "TaskUpdate"];

    // Браузерные инструменты, которые приходят в сессию ПОМИМО нашего MCP-конфига, — два
    // независимых канала: MCP плагина playwright (профили CLI) и коннектор аккаунта claude.ai
    // «microsoft/playwright-mcp». Гасить достаточно обоих сразу: выключение одного плагина
    // ничего не даёт — модель просто идёт браузером из коннектора (проверено живым прогоном).
    // Маски (а не enabledPlugins) закрывают инструменты и в песочнице, где --settings не
    // передаётся. Маска `mcp__server__*` для CLI законна даже если сервера в сессии нет —
    // так же блокируются коннекторы из Claude:DisallowedTools.
    private static readonly string[] BrowserTools =
        ["mcp__plugin_playwright_playwright__*", "mcp__microsoft_playwright-mcp__*"];

    // Инструменты правки файлов — используются для атрибуции file_changed
    // (FileChangeAttributor.Claim), чтобы TurnFileWatcher чужого чата того же rootPath
    // не показал карточку правки, сделанной этой сессией. NotebookEdit — единственный
    // с другим именем аргумента (notebook_path, не file_path — сверено с фронтом,
    // frontend/src/components/chat/ToolUseView.tsx: inp.file_path ?? inp.path ?? inp.notebook_path).
    private static readonly Dictionary<string, string> FileWriteToolPathKey =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Edit"] = "file_path",
            ["Write"] = "file_path",
            ["MultiEdit"] = "file_path",
            ["NotebookEdit"] = "notebook_path",
        };

    // Путь файла из аргументов tool_use для инструментов правки; null — не инструмент
    // правки или в аргументах нет непустой строки по ожидаемому ключу
    private static string? ExtractFileWritePath(string toolName, JsonElement input)
    {
        if (!FileWriteToolPathKey.TryGetValue(toolName, out var key)) return null;
        if (input.ValueKind != JsonValueKind.Object) return null;
        return input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() is { Length: > 0 } s ? s : null
            : null;
    }

    // Свои MCP-серверы (mcp/*-server, mcp-dify — код этого репозитория, собираются в
    // BuildTurnMcpConfig): работа с данными самого пользователя внутри системы, не внешнее
    // действие с побочным эффектом наружу (в отличие от Google Drive/Gamma/Miro/figma и
    // т.п. сторонних коннекторов). Разрешаем их автоматически, без карточки пользователю —
    // иначе персоны и автоматизации вязнут в перманентных permission-запросах на каждый
    // созданный чат/процесс claude, хотя доступ уже ограничен на уровне Persona.Tools/
    // ExtraDisallowedTools и project deny-правил (проверяются раньше, см. DecidePermissionAsync).
    // mcp__pmem_ — выделенные memory-серверы персон-консультантов (pmem_<handle>, файловые
    // сабагенты): их permission-запросы падают в фоновом контексте сабагента, где отвечать
    // некому — авторазрешаем, как и остальные свои серверы (доступ ограничен allow-list агента).
    private static readonly string[] BuiltInMcpServerPrefixes =
        ["mcp__tasks__", "mcp__notes__", "mcp__memory__", "mcp__personas__", "mcp__wsp__", "mcp__notifications__", "mcp__widgets__", "mcp__dify__", "mcp__pmem_"];

    // Игнор служебной папки вложений в git ставится лениво один раз за жизнь сессии:
    // модель кладёт туда картинки для показа в ленте (см. подсказку про картинки в промпте),
    // а у проекта со своим .gitignore правила может не быть — при аплоаде его пишет
    // ChatsController, но модель загружает файлы мимо него.
    private bool _attachmentsExcludeEnsured;

    // Отслеживание изменений файлов на время хода
    private readonly TurnFileWatcher _fileWatcher;
    // Атрибуция file_changed чату-источнику при параллельных ходах одного проекта
    // (см. FileChangeAttributor); null — фильтрация выключена (тесты)
    private readonly FileChangeAttributor? _fileChangeAttributor;
    // tool_use_id → путь ОЖИДАЮЩЕЙ заявки на правку (см. ExtractFileWritePath): подтверждается
    // в Claim по успешному tool_result, снимается без Claim при ошибке/отказе permission
    private readonly ConcurrentDictionary<string, string> _pendingFileClaims = new();

    private readonly string? _rawSystemPrompt;
    private readonly string? _mcpConfigPath;
    // Ключ HTTP MCP-сервера fal-ai (Fal:McpApiKey) — сервер инжектится в конфиг хода
    // из appsettings, а не хардкодится в .mcp.json (секрет вне git); пусто — без fal-ai
    private readonly string? _falMcpApiKey;
    // Токен HTTP MCP-сервера glif (Glif:McpToken) — второй генератор медиа рядом с fal-ai;
    // инжектится тем же путём из appsettings; пусто — без glif
    private readonly string? _glifMcpToken;
    private readonly SkillsService? _skills;
    private readonly WorkspaceKnowledgeStore? _wkStore;
    // Провайдер правил разрешений проекта — резолвим каждый запрос (правила могут меняться)
    private readonly Func<IReadOnlyList<PermissionRule>>? _permissionRules;
    private readonly TasksMcpContext? _tasksMcp;
    private readonly NotesMcpContext? _notesMcp;
    // Auto-recall заметок: по тексту хода возвращает блок для системного промпта + манифест (F3)
    private readonly Func<string, Task<RecallBlock?>>? _recallProvider;
    // Провайдер системного промпта персоны — вызывается на каждый ход
    // (свежие контракт/модель/PersonaSwitched без пересоздания адаптера)
    private readonly Func<string?>? _personaPromptProvider;
    // MCP-сервер долгой памяти персоны + auto-recall её памяти (текст промпта + манифест F3)
    private readonly MemoryMcpContext? _memoryMcp;
    private readonly Func<string, Task<RecallBlock?>>? _personaRecallProvider;
    // Блок «Привязанные знания и правила» персоны (флаг persona-bindings)
    private readonly Func<string, Task<string?>>? _bindingsProvider;
    // Per-ход slice top-10 god-nodes Code Graph (ADR вариант A): null — без rootPath/фичи
    private readonly Func<string?, Task<string?>>? _codeGraphProvider;
    // Снимок промпта хода: черновик → id записанного снимка. null — снимки не ведутся.
    private readonly Func<PromptSnapshotDraft, string?>? _promptSnapshotSink;
    // Дозапись в снимок состава инструментов из system/init (он приходит после старта)
    private readonly Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerInfo>>? _promptSnapshotToolsSink;
    // Паспорт прогона сабагента на его завершении (диагностика обрывов). null — не ведутся.
    private readonly Action<SubagentRunPassport>? _subagentRunSink;
    // Корень профиля CLI этого хода (CLAUDE_CONFIG_DIR) — для блока «слой CLI»
    private readonly string? _cliConfigRoot;
    // Id снимка текущего хода: пишет поток хода (RunTurnAsync), читает поток stdout-ридера,
    // дописывающий в снимок состав инструментов из system/init. Отсюда volatile.
    private volatile string? _currentSnapshotId;
    // MCP-сервер персон: CRUD из любого чата + @упоминания/persona_ask
    private readonly PersonasMcpContext? _personasMcp;
    // MCP-сервер рабочего пространства: проекты/файлы/знания/поиск владельца
    private readonly WorkspaceMcpContext? _workspaceMcp;
    // MCP-сервер уведомлений: создание уведомлений из Claude/агентов
    private readonly NotificationsMcpContext? _notificationsMcp;
    // MCP-серверы внешних модулей из реестра (контракт §6): аддитивно к встроенным
    private readonly ModulesMcpContext? _modulesMcp;
    // MCP-сервер виджетов чата (widget_show): null — сессия без владельца
    private readonly WidgetsMcpContext? _widgetsMcp;
    // MCP-сервер графа кода (codegraph_find/neighbors/hubs): null — чат вне проекта
    private readonly CodeGraphMcpContext? _codeGraphMcp;
    // Подсказка про трейлер CCS-Session/CCS-Task (ADR-004): null — флаг выключен/вне проекта
    private readonly string? _dossierTrailerHint;
    // Файловые сабагенты-персоны: план хода — папки --add-dir
    // + pmem-серверы памяти консультантов; вычисляется на каждый ход
    private readonly Func<PersonaAgentsContext?>? _personaAgentsProvider;
    // MCP-серверы личного реестра владельца: вычисляется на каждый ход, чтобы правка
    // реестра применялась без пересоздания адаптера
    private readonly Func<ExternalMcpContext?>? _externalMcpProvider;
    // Браузер (плагин playwright) в этой сессии: false — гасим плагин на запуске CLI
    private readonly bool _browserEnabled;
    // Реестр CLI-провайдеров: env-оверрайды процесса (ANTHROPIC_BASE_URL и др.)
    // для сторонних моделей; null — всегда родной Claude
    private readonly LlmProviderRegistry? _providers;
    // Резолвер назначений моделей — нужен ради «модели по назначению места» (см. EffectiveModel);
    // null — адаптер собран без него (тесты), тогда пустая модель означает дефолт CLI, как раньше
    private readonly ModelAssignmentResolver? _assignments;
    private readonly ClaudeSubscriptionPool? _subscriptionPool;
    // Драйвер среды исполнения владельца (local / docker-песочница)
    private readonly Execution.IProcessLauncher _launcher;
    // Метка текущего хода — по ней драйвер песочницы добивает процесс внутри контейнера
    private string? _currentTurnId;

    // Модель, которой РЕАЛЬНО идёт ход: CLI называет её в system/init и в message.model
    // каждого ответа (TurnTelemetry.ModelFromEvent). EffectiveModel — лишь намерение, и при
    // пустом слоте он null, из-за чего в телеметрию уходил литерал unknown.
    private string? _turnCliModel;

    // Спан идущего хода — чтобы дописать в него фактическую модель, когда CLI её назовёт.
    // Тег ставится в двух местах, но никогда одновременно: при старте хода (до запуска
    // процесса, поток RunTurnAsync) и потом из ридера stdout, пока RunTurnAsync ждёт
    // завершения. Ссылка на уже закрытый спан безвредна: SetTag после Stop ничего не
    // меняет — спан к тому моменту экспортирован.
    private Activity? _turnActivity;

    public ClaudeSession(Session info, LlmSessionContext context,
        string? mcpConfigPath = null, SkillsService? skills = null,
        WorkspaceKnowledgeStore? workspaceStore = null, string[]? disallowedTools = null,
        LlmProviderRegistry? providers = null,
        ClaudeSubscriptionPool? subscriptionPool = null,
        FileWatcherOptions? fileWatcherOptions = null,
        TimeSpan? bgLingerTimeout = null,
        string? falMcpApiKey = null,
        string? glifMcpToken = null,
        ModelAssignmentResolver? assignments = null,
        FileChangeAttributor? fileChangeAttributor = null)
    {
        _providers = providers;
        _assignments = assignments;
        _subscriptionPool = subscriptionPool;
        _bgLingerTimeout = bgLingerTimeout ?? TimeSpan.FromMinutes(30);
        Info = info;
        _rootPath = context.RootPath;
        _onMessage = context.OnMessage;
        _mcpConfigPath = mcpConfigPath;
        _falMcpApiKey = falMcpApiKey;
        _glifMcpToken = glifMcpToken;
        _rawSystemPrompt = context.RawSystemPrompt;
        _skills = skills;
        _wkStore = workspaceStore;
        _permissionRules = context.PermissionRules;
        _tasksMcp = context.TasksMcp;
        _notesMcp = context.NotesMcp;
        _recallProvider = context.RecallProvider;
        _personaPromptProvider = context.PersonaPromptProvider;
        _memoryMcp = context.MemoryMcp;
        _personaRecallProvider = context.PersonaRecallProvider;
        _bindingsProvider = context.BindingsProvider;
        _codeGraphProvider = context.CodeGraphProvider;
        _promptSnapshotSink = context.PromptSnapshotSink;
        _promptSnapshotToolsSink = context.PromptSnapshotToolsSink;
        _subagentRunSink = context.SubagentRunSink;
        _cliConfigRoot = context.CliConfigRoot;
        _personasMcp = context.PersonasMcp;
        _workspaceMcp = context.WorkspaceMcp;
        _notificationsMcp = context.NotificationsMcp;
        _modulesMcp = context.ModulesMcp;
        _widgetsMcp = context.WidgetsMcp;
        _codeGraphMcp = context.CodeGraphMcp;
        _dossierTrailerHint = context.DossierTrailerHint;
        _personaAgentsProvider = context.PersonaAgentsProvider;
        _externalMcpProvider = context.ExternalMcpProvider;
        _browserEnabled = context.BrowserEnabled;
        _launcher = context.Launcher ?? Execution.LocalProcessRunner.Instance;
        // Запреты конфига + ограничения возможностей персоны (ExtraDisallowedTools)
        _disallowedTools = context.ExtraDisallowedTools is { Count: > 0 } extra
            ? [.. (disallowedTools ?? []), .. extra]
            : disallowedTools ?? [];
        // Пока подключён наш MCP tasks-server, запрещаем встроенные Task-инструменты
        // Claude Code (синхронизация с claude.ai — там пусто): они дублируют mcp__tasks__*
        // и путают модель (особенно haiku зовёт TaskGet/TaskList вместо tasks_get/tasks_list,
        // получает «No tasks» и бросает задачу). Без задач в сессии — не трогаем.
        if (context.TasksMcp is not null)
            _disallowedTools = [.. _disallowedTools, .. BuiltInTaskTools];
        // Браузер не положен персоне по роли — закрываем оба канала (плагин + коннектор)
        if (!_browserEnabled)
            _disallowedTools = [.. _disallowedTools, .. BrowserTools];
        _fileChangeAttributor = fileChangeAttributor;
        _fileWatcher = new TurnFileWatcher(_rootPath, _onMessage, fileWatcherOptions,
            fileChangeAttributor, info.Id);
    }

    // Объединённый MCP-конфиг хода: серверы из базового конфига (Dify с инжекцией
    // dataset id) + tasks-server с контекстом сессии; для сессий сторонних провайдеров —
    // ещё и user-scope серверы из ~/.claude.json (fal-ai и др.: изолированный
    // CLAUDE_CONFIG_DIR их не видит). null → базовый конфиг как есть.
    // Возвращает путь temp-конфига и отсортированный набор ключей серверов — ключи входят
    // в сигнатуру прогона (сам путь и содержимое меняются каждый ход: новый файл, свежий JWT)
    // turnText в параметрах больше нет: текст хода не имеет права влиять на состав инструментов
    // (гейт WriteIntentGate менял его между ходами → перезапуск процесса со всеми MCP)
    // ServerKeys — строка сигнатуры («ключ:отпечаток-состава»), НЕ список серверов: отпечаток
    // сам содержит запятые, парсить его нельзя. ServerNames — плоский список ключей для
    // показа человеку (снимок промпта хода).
    private (string? Path, string ServerKeys, IReadOnlyList<string> ServerNames) BuildTurnMcpConfig(
        string? datasetId, PersonaAgentsContext? personaAgents = null)
    {
        var tasksServerPath = _tasksMcp is not null ? MapMcpPath(TasksServerLocator.FindTasksServerPath()) : null;
        var hasTasks = tasksServerPath is not null;
        var notesServerPath = _notesMcp is not null ? MapMcpPath(NotesServerLocator.FindNotesServerPath()) : null;
        var hasNotes = notesServerPath is not null;
        var hasConsultants = personaAgents is { MemoryServers.Count: > 0 };
        var memoryServerPath = _memoryMcp is not null || hasConsultants
            ? MapMcpPath(MemoryServerLocator.FindMemoryServerPath()) : null;
        var hasMemory = _memoryMcp is not null && memoryServerPath is not null;
        var personasServerPath = _personasMcp is not null ? MapMcpPath(PersonasServerLocator.FindPersonasServerPath()) : null;
        var hasPersonas = personasServerPath is not null;
        var workspaceServerPath = _workspaceMcp is not null ? MapMcpPath(WorkspaceServerLocator.FindWorkspaceServerPath()) : null;
        var hasWorkspace = workspaceServerPath is not null;
        var notificationsServerPath = _notificationsMcp is not null ? MapMcpPath(NotificationsServerLocator.FindNotificationsServerPath()) : null;
        var hasNotifications = notificationsServerPath is not null;
        var widgetsServerPath = _widgetsMcp is not null ? MapMcpPath(WidgetsServerLocator.FindWidgetsServerPath()) : null;
        var hasWidgets = widgetsServerPath is not null;
        var codeGraphServerPath = _codeGraphMcp is not null ? MapMcpPath(CodeGraphServerLocator.FindCodeGraphServerPath()) : null;
        var hasCodeGraph = codeGraphServerPath is not null;
        var hasDataset = !string.IsNullOrEmpty(datasetId);
        var hasModules = _modulesMcp is { Servers.Count: > 0 };
        var hasFalAi = !string.IsNullOrEmpty(_falMcpApiKey);
        var hasGlif = !string.IsNullOrEmpty(_glifMcpToken);
        var userServers = LoadUserScopeMcpServers();
        // Личный реестр владельца: состав решается по owner/project/persona в SessionManager,
        // от свойств хода не зависит (иначе сигнатура запуска «мерцала» бы между ходами)
        var externalMcp = _externalMcpProvider?.Invoke();
        var hasExternal = externalMcp is { Servers.Count: > 0 };
        if (!hasTasks && !hasNotes && !hasMemory && !hasPersonas && !hasWorkspace && !hasNotifications
            && !hasWidgets && !hasCodeGraph && !hasDataset && !hasModules && !hasFalAi && !hasGlif && userServers is null
            && !hasExternal
            && !(hasConsultants && memoryServerPath is not null)) return (null, "", []);

        try
        {
            var servers = new System.Text.Json.Nodes.JsonObject();
            // Отпечаток СОСТАВА инструментов сервера (ключ сервера → суффикс сигнатуры).
            // Per-turn env, меняющие набор tools (PERSONAS_WRITE/MENTIONS, WORKSPACE_WRITE,
            // TASKS_EXECUTE, …), не попадают в токены/URL (те исключены из сигнатуры как
            // изменчивые). Без этого отпечатка смена, напр., PERSONAS_WRITE 0→1 не меняла
            // сигнатуру запуска — ход уходил в живой процесс доживания, personas_create там
            // так и не поднимался («No such tool available»). Копится параллельно servers,
            // вклеивается в ServerKeys ниже.
            var shapes = new Dictionary<string, string>(StringComparer.Ordinal);

            // User-scope серверы (только у сторонних провайдеров) — первыми:
            // одноимённые из базового конфига ниже их перекроют
            if (userServers is not null)
                foreach (var (key, val) in userServers)
                    if (val?.DeepClone() is { } clone && AdaptServerForRuntime(key, clone))
                        servers[key] = clone;

            // Серверы из базового конфига (+ dataset id в env Dify)
            if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
            {
                var baseDoc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_mcpConfigPath));
                if (baseDoc?["mcpServers"] is System.Text.Json.Nodes.JsonObject baseServers)
                {
                    foreach (var (key, val) in baseServers)
                    {
                        var clone = val?.DeepClone();
                        if (clone is null || !AdaptServerForRuntime(key, clone)) continue;
                        if (key == "dify" && hasDataset && clone["env"] is { } env)
                        {
                            env["DIFY_DEFAULT_DATASET_ID"] = datasetId;
                            env["DIFY_SEARCH_ONLY"] = "true";
                        }
                        servers[key] = clone;
                    }
                }
            }

            // Серверы личного реестра владельца — сразу после базового конфига: одноимённая
            // запись реестра перекрывает наследство из .mcp.json (человек правил реестр
            // осознанно, а глобальный файл — на весь инстанс), а встроенные серверы продукта
            // ставятся ниже и всё равно выигрывают (их ключи в реестре зарезервированы).
            if (hasExternal)
            {
                foreach (var srv in externalMcp!.Servers)
                {
                    var node = new System.Text.Json.Nodes.JsonObject();
                    if (string.Equals(srv.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                    {
                        node["command"] = srv.Command ?? "";
                        var argsArr = new System.Text.Json.Nodes.JsonArray();
                        foreach (var arg in srv.Args) argsArr.Add(arg);
                        node["args"] = argsArr;
                        if (srv.Env.Count > 0)
                        {
                            var envObj = new System.Text.Json.Nodes.JsonObject();
                            foreach (var (name, value) in srv.Env) envObj[name] = value;
                            node["env"] = envObj;
                        }
                    }
                    else
                    {
                        node["type"] = srv.Transport.ToLowerInvariant();
                        node["url"] = srv.Url ?? "";
                        if (srv.Headers.Count > 0)
                        {
                            var headersObj = new System.Text.Json.Nodes.JsonObject();
                            foreach (var (name, value) in srv.Headers) headersObj[name] = value;
                            node["headers"] = headersObj;
                        }
                    }
                    if (srv.AlwaysLoad) node["alwaysLoad"] = true;
                    // Та же адаптация к среде, что у наследства: в песочнице переписываются
                    // loopback-адреса и хостовые пути; непереводимый путь → сервер пропускается
                    if (!AdaptServerForRuntime(srv.Key, node)) continue;
                    servers[srv.Key] = node;
                    // Отпечаток: alwaysLoad меняет момент подключения, AuthVersion — заголовки,
                    // запечённые в файл конфига на старте процесса. Сам секрет в сигнатуру не идёт.
                    shapes[srv.Key] = $"a{(srv.AlwaysLoad ? 1 : 0)}:v{srv.AuthVersion}";
                }
            }

            // Продуктовый HTTP-сервер fal-ai (генерация изображений/видео): инжектится из
            // Fal:McpApiKey одинаково для хоста и песочницы (паритет сред). Ставим ПОСЛЕ
            // user-scope и базового конфига — одноимённый сервер оттуда перекрывается,
            // ключ не задваивается.
            if (hasFalAi)
            {
                servers["fal-ai"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = "https://mcp.fal.ai/mcp",
                    ["headers"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Authorization"] = $"Bearer {_falMcpApiKey}",
                    },
                };
            }

            // Продуктовый HTTP-сервер glif (агентская генерация медиа, glif.app): инжектится из
            // Glif:McpToken тем же путём, что fal-ai — паритет хост/песочница, ПОСЛЕ user-scope
            // и базового конфига (одноимённый сервер оттуда перекрывается)
            if (hasGlif)
            {
                servers["glif"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = "https://glif.app/api/mcp",
                    ["headers"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Authorization"] = $"Bearer {_glifMcpToken}",
                    },
                };
            }

            if (hasTasks)
            {
                // ИНВАРИАНТ: состав инструментов сервера не зависит от хода. tasks_run_executor
                // подключён ВСЕГДА; анти-рекурсия (запуск исполнителя с делегированного хода и
                // с реакционного авто-хода постановщика) проверяется бэкендом по актуальному
                // состоянию сессии — [DenyOnDelegatedTurn] на TasksController.Execute.
                // Было наоборот: env TASKS_EXECUTE входил в сигнатуру запуска, поэтому
                // чередование обычного и делегированного хода убивало процесс CLI со всеми
                // MCP-серверами («Stream closed»), а инструмент то появлялся, то исчезал.
                const string tasksExecute = "1";
                // Кросс-проектные ProjectTasks-привязки текущей персоны: доступ к задачам
                // ДРУГИХ проектов владельца (extraProjectIdsCsv), подмножество только для
                // чтения — extraReadOnlyCsv (create/update/delete там запрещены)
                // hasTasks ⇒ _tasksMcp не null (путь сервера резолвится только при заданном
                // контексте), но связь через промежуточный флаг компилятор не видит — идём
                // через ?., чтобы инвариант не держался на подавлении nullable-анализа
                var extraProjectIdsCsv = _tasksMcp?.ExtraProjectIds is { Count: > 0 } extraIds
                    ? string.Join(",", extraIds) : "";
                var extraReadOnlyCsv = _tasksMcp?.ExtraProjectIdsReadOnly is { Count: > 0 } extraRo
                    ? string.Join(",", extraRo) : "";
                servers["tasks"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { tasksServerPath! },
                    // alwaysLoad как у memory/personas/wsp: при ленивом подключении первый вызов
                    // в ходе падает «No such tool available» (claude-code#19282), а аккаунт-
                    // коннекторы claude.ai переводят CLI в режим deferred-tools, где ленивый
                    // сервер и вовсе прячет инструменты от модели. В истории прода этим
                    // объясняются карточки «No such tool available: mcp__tasks__*» при живом
                    // сервере. Цена — node-процесс на каждый ход, стартует параллельно.
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["TASKS_API_URL"] = _tasksMcp!.ApiUrl,
                        ["TASKS_API_TOKEN"] = _tasksMcp.Token,
                        ["TASKS_PROJECT_ID"] = _tasksMcp.ProjectId ?? "",
                        // Происхождение создаваемых задач: чат-источник и персона-постановщик.
                        // Берём из Info на каждый ход (как NOTES_SESSION_ID) — PersonaId сессии
                        // меняется по ходу разговора (SetPersona, смена спикера в группе)
                        ["TASKS_SESSION_ID"] = Info.Id,
                        ["TASKS_SELF_PERSONA_ID"] = Info.PersonaId ?? "",
                        ["TASKS_EXECUTE"] = tasksExecute,
                        ["TASKS_EXTRA_PROJECT_IDS"] = extraProjectIdsCsv,
                        ["TASKS_EXTRA_PROJECT_IDS_READONLY"] = extraReadOnlyCsv,
                    },
                };
                // Кросс-проектные скоупы влияют на видимость/доступность задач других проектов —
                // смена привязок должна пробить доживание живого процесса. Меняются они при смене
                // персоны/привязок, а не от хода к ходу, поэтому процесс от них не «мерцает».
                shapes["tasks"] = $"{extraProjectIdsCsv}:{extraReadOnlyCsv}";
            }

            if (hasNotes)
            {
                // Модуль комментариев к документам и редких операций (дневник, граф, backlinks,
                // удаление, промоут чекбокса, подсказка заголовка) — решение ПО ПЕРСОНЕ
                // (ключ notes-annotations, PersonaBindingsService.SectionEnabled), от хода
                // не зависит. Ядро заметок (создать/прочитать/найти/изменить/переместить)
                // остаётся у всех, у кого сервер включён.
                var notesAnnotations = _notesMcp!.AnnotationsEnabled ? "1" : "0";
                servers["notes"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { notesServerPath! },
                    // alwaysLoad — по той же причине, что у tasks (см. выше): ленивый сервер
                    // прячет инструменты в режиме deferred-tools
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["NOTES_API_URL"] = _notesMcp.ApiUrl,
                        ["NOTES_API_TOKEN"] = _notesMcp.Token,
                        ["NOTES_PROJECT_ID"] = _notesMcp.ProjectId ?? "",
                        ["NOTES_SESSION_ID"] = Info.Id,
                        ["NOTES_ANNOTATIONS"] = notesAnnotations,
                    },
                };
                // Состав инструментов заметок зависит от модуля — в сигнатуру запуска
                shapes["notes"] = $"a{notesAnnotations}";
            }

            if (hasWidgets)
            {
                // Сервер виджетов: без env (API ему не нужен). alwaysLoad — единственный
                // крохотный инструмент; без него первый вызов в ходе падает «No such tool
                // available» (claude-code#19282), а ретраить показ виджета модели не свойственно.
                servers["widgets"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { widgetsServerPath! },
                    ["alwaysLoad"] = true,
                };
            }

            if (hasMemory)
            {
                // Секция dossier_lookup/dossier_get (этап 2, ADR-004 §5): гейт по флагу
                // ВЛАДЕЛЬЦА change-dossiers-recall — стабилен в рамках сессии (меняется
                // человеком из меню редко), от свойств хода не зависит. Флаг входит в
                // отпечаток состава: переключение корректно перезапустит процесс CLI.
                var memoryDossierTools = _memoryMcp!.DossierToolsEnabled ? "1" : "0";
                servers["memory"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { memoryServerPath! },
                    // MCP подключается лениво (claude-code#19282): без alwaysLoad первый вызов
                    // инструмента в ходе падает «No such tool available». Память/персон модель
                    // зовёт первым же действием — ждём подключения до старта хода.
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["MEMORY_API_URL"] = _memoryMcp.ApiUrl,
                        ["MEMORY_API_TOKEN"] = _memoryMcp.Token,
                        ["MEMORY_PERSONA_ID"] = _memoryMcp.PersonaId,
                        // ③-3.4: проектная персона получает team_memory_* — общая память команды
                        ["MEMORY_PROJECT_ID"] = _memoryMcp.ProjectId ?? "",
                        ["MEMORY_DOSSIER_TOOLS"] = memoryDossierTools,
                    },
                };
                // Состав инструментов памяти зависит от секции паспортов — в сигнатуру запуска
                shapes["memory"] = $"d{memoryDossierTools}";
            }

            // Проверка _personasMcp избыточна по смыслу (hasPersonas истинен только когда контекст
            // есть — см. резолв пути выше), но без неё компилятор теряет null-состояние поля через
            // промежуточную bool и требует ! на каждом обращении внутри блока.
            if (hasPersonas && _personasMcp is not null)
            {
                // persona_ask выключен когда есть файловые сабагенты-персоны: модель должна
                // использовать Task(agentType=...) в Workflow, а не путаться. Состав персон
                // владельца в рамках сессии не меняется, так что от хода это не зависит.
                // Гейт по agentDepth убран (был анти-рекурсией): он менял состав между ходами →
                // процесс CLI перезапускался. Анти-рекурсия persona_ask — на бэкенде,
                // [DenyOnDelegatedTurn] на PersonasController.Ask.
                var personaMentions = _personasMcp.MentionsHint is not null
                    && personaAgents is not { AgentHandles.Count: > 0 }
                    ? "1" : "0";
                // Общий рубильник write-инструментов персон — ВСЕГДА "1". Гейт по agentDepth
                // и по тексту хода снят: он давал самый частый «No such tool available» прода
                // (personas_create), а реального ограничения не нёс — права персоны и так режутся
                // Persona.Tools / ExtraDisallowedTools, а у файловых сабагентов — их allow-list.
                // Состав write-инструментов режут модули manage/automation ниже — но по ПЕРСОНЕ.
                const string personaWrite = "1";
                // Модули сервера персон: manage (CRUD персон) и automation (правила
                // проактивности) — решение принято ПО ПЕРСОНЕ в SessionManager
                // (PersonaBindingsService.SectionEnabled), от хода не зависит.
                var personaManage = _personasMcp.ManageEnabled ? "1" : "0";
                var personaAutomation = _personasMcp.AutomationEnabled ? "1" : "0";
                // Кросс-проектные ProjectPersonas-привязки: доступ к команде/точечным персонам
                // ДРУГОГО проекта — расширяют personas_list(scope=context) и резолв handle в persona_ask
                var extraProjectIdsCsv = _personasMcp.ExtraProjectIds is { Count: > 0 } extraProjects
                    ? string.Join(",", extraProjects) : "";
                var extraPersonaIdsCsv = _personasMcp.ExtraPersonaIds is { Count: > 0 } extraPersonas
                    ? string.Join(",", extraPersonas) : "";
                servers["personas"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { personasServerPath! },
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["PERSONAS_API_URL"] = _personasMcp.ApiUrl,
                        ["PERSONAS_API_TOKEN"] = _personasMcp.Token,
                        ["PERSONAS_PROJECT_ID"] = _personasMcp.ProjectId ?? "",
                        ["PERSONAS_SELF_ID"] = _personasMcp.SelfPersonaId ?? "",
                        // Сессия-вызыватель: по ней бэкенд гейтит persona_ask на делегированном
                        // ходу (заголовок X-Caller-Session-Id → DenyOnDelegatedTurn)
                        ["PERSONAS_SESSION_ID"] = Info.Id,
                        ["PERSONAS_MENTIONS"] = personaMentions,
                        ["PERSONAS_BINDINGS"] = _personasMcp.BindingsEnabled ? "1" : "0",
                        ["PERSONAS_WRITE"] = personaWrite,
                        ["PERSONAS_MANAGE"] = personaManage,
                        ["PERSONAS_AUTOMATION"] = personaAutomation,
                        ["PERSONAS_EXTRA_PROJECT_IDS"] = extraProjectIdsCsv,
                        ["PERSONAS_EXTRA_PERSONA_IDS"] = extraPersonaIdsCsv,
                    },
                };
                // Область персон зависит от mentions/bindings/модулей/extra-скоупов — в сигнатуру.
                // Все они постоянны в рамках сессии (состав персон, привязки, роль), поэтому
                // процесс от них не «мерцает»; write в сигнатуре больше нет — он всегда включён.
                shapes["personas"] = $"m{personaMentions}b{(_personasMcp.BindingsEnabled ? "1" : "0")}"
                    + $"g{personaManage}a{personaAutomation}:{extraProjectIdsCsv}:{extraPersonaIdsCsv}";
            }

            if (hasWorkspace)
            {
                // Секции — только от привязок персоны, НЕ от хода. Анти-рекурсия делегирования
                // (chats_send и удаление на агентном ходу) переехала на бэкенд —
                // [DenyOnDelegatedTurn] на SessionMessagesController.PostMessage, FilesController.Delete,
                // ChatsController/SessionsController.Delete. Пока срез секций жил здесь, состав
                // wsp менялся между обычным и делегированным ходом → процесс CLI перезапускался
                // со всеми MCP-серверами, и chats_send «пропадал» посреди работы.
                var sectionsJoined = string.Join(",", _workspaceMcp!.Sections);
                // WORKSPACE_WRITE всегда "1": гейт по тексту хода (WriteIntentGate) менял env
                // между ходами → MCP-сервер перезапускался («Stream closed»). Write-инструменты
                // (files_write, projects_create, git_commit, knowledge_index) доступны всегда,
                // safety-уровень — право доступа персоны (Persona.Tools / ExtraDisallowedTools).
                var workspaceWrite = "1";
                // Ключ сервера — "wsp", НЕ "workspace": claude CLI молча отбрасывает
                // MCP-сервер с зарезервированным именем "workspace" из --mcp-config
                // (сервер не стартует, инструменты не появляются). Отсюда же префикс
                // инструментов mcp__wsp__* в подсказках ниже и в PersonaBindingsService.
                servers["wsp"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { workspaceServerPath! },
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["WORKSPACE_API_URL"] = _workspaceMcp!.ApiUrl,
                        ["WORKSPACE_API_TOKEN"] = _workspaceMcp.Token,
                        ["WORKSPACE_PROJECT_ID"] = _workspaceMcp.ProjectId ?? "",
                        ["WORKSPACE_SECTIONS"] = sectionsJoined,
                        ["WORKSPACE_PROJECT_IDS"] = _workspaceMcp.AllowedProjectIds is { Count: > 0 } allowed
                            ? string.Join(",", allowed) : "",
                        ["WORKSPACE_SELF_SESSION_ID"] = _workspaceMcp.SelfSessionId ?? "",
                        // WORKSPACE_AGENT_DEPTH убран: глубину для целевого чата бэкенд считает
                        // сам по сессии-отправителю (заголовок X-Caller-Session-Id). Из env она
                        // протухала при переиспользовании живого прогона — делегированный ход
                        // мог отправить сообщение с глубиной прошлого хода.
                        // Тяжёлые write-схемы (files_write с content, projects_create/update,
                        // knowledge_index) грузим в контекст только когда ход про запись в рабочее
                        // пространство. Read (list/tree/read/search/status/history) — всегда.
                        // chats_create/send/update под гейт НЕ попадают (см. WRITE_TOOLS в
                        // workspace-server): их состав должен быть одинаков на всех ходах, иначе
                        // инструменты «мерцают» между ходами вместе с сигнатурой MCP.
                        ["WORKSPACE_WRITE"] = workspaceWrite,
                    },
                    // alwaysLoad как у memory/personas: аккаунт-коннекторы claude.ai переводят
                    // CLI в режим deferred-tools, где ленивые серверы прячут инструменты от модели.
                    // Персона-секретарь опирается на workspace-инструменты — держим их всегда видимыми.
                    ["alwaysLoad"] = true,
                };
                // Состав wsp-инструментов зависит от write-режима и набора секций — в сигнатуру
                shapes["wsp"] = $"w{workspaceWrite}:{sectionsJoined}";
            }

            if (hasNotifications)
            {
                servers["notifications"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { notificationsServerPath! },
                    // alwaysLoad — по той же причине, что у tasks (см. выше)
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["NOTIFICATIONS_API_URL"] = _notificationsMcp!.ApiUrl,
                        ["NOTIFICATIONS_API_TOKEN"] = _notificationsMcp.Token,
                        ["NOTIFICATIONS_SELF_PERSONA_ID"] = _notificationsMcp.SelfPersonaId ?? "",
                    },
                };
            }

            if (hasCodeGraph && _codeGraphMcp is not null)
            {
                // Граф кода проекта: поиск типа, связи узла, хабы по связности. Состав
                // инструментов постоянный (три чтения), per-owner изоляция — токеном
                // владельца и проверкой владельца проекта в CodeGraphController.
                servers["codegraph"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { codeGraphServerPath! },
                    // alwaysLoad — по той же причине, что у tasks (см. выше)
                    ["alwaysLoad"] = true,
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["CODEGRAPH_API_URL"] = _codeGraphMcp.ApiUrl,
                        ["CODEGRAPH_API_TOKEN"] = _codeGraphMcp.Token,
                        ["CODEGRAPH_PROJECT_ID"] = _codeGraphMcp.ProjectId,
                        ["CODEGRAPH_SESSION_ID"] = _codeGraphMcp.SessionId ?? "",
                        // Рабочее дерево хода: отдельное worktree чата имеет свой граф
                        ["CODEGRAPH_ROOT_PATH"] = _codeGraphMcp.RootPath ?? "",
                    },
                };
            }

            // pmem-серверы персон-консультантов (файловые сабагенты):
            // тот же memory-server под уникальным ключом pmem_<handle> с env КОНСУЛЬТАНТА —
            // файл агента ссылается на него по имени (mcpServers: [pmem_<handle>]), токен
            // живёт только в этом временном конфиге. Ретрай «No such tool available» вшит
            // в тело файла агента.
            //
            // alwaysLoad здесь НЕ ставится, но экономии процессов это не даёт: CLI поднимает
            // ВСЕ stdio-серверы конфига на старте, а флаг управляет лишь видимостью их
            // инструментов в tools/list (проверено 15.08.2026 на CLI 2.1.229 отдельным стендом,
            // см. docs/architecture/mcp-servers.md). Поэтому сколько персон объявлено ходу,
            // столько и процессов node: на боевом 14 персон ≈ 610 МБ на каждый ход. Цена
            // осознанно принята; уменьшить её можно только числом объявленных серверов.
            if (hasConsultants && memoryServerPath is not null)
            {
                foreach (var c in personaAgents!.MemoryServers)
                {
                    servers[c.ServerKey] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["command"] = "node",
                        ["args"] = new System.Text.Json.Nodes.JsonArray { memoryServerPath },
                        ["env"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["MEMORY_API_URL"] = c.ApiUrl,
                            ["MEMORY_API_TOKEN"] = c.Token,
                            ["MEMORY_PERSONA_ID"] = c.PersonaId,
                            ["MEMORY_PROJECT_ID"] = c.ProjectId ?? "",
                        },
                    };
                }
            }

            // MCP-серверы внешних модулей (контракт §6, ТЗ R7) — строго аддитивно:
            // коллизия ключа со встроенным/пользовательским сервером → пропуск с логом
            // (модуль не может перекрыть tasks/notes/memory/…). Трафик инструментов идёт
            // через gateway ядра (MODULE_API_URL), токен chan=mcp свежий на каждый ход.
            if (hasModules)
            {
                foreach (var mod in _modulesMcp!.Servers)
                {
                    if (servers.ContainsKey(mod.Key))
                    {
                        Console.Error.WriteLine(
                            $"[ClaudeSession] MCP модуля «{mod.ModuleId}» пропущен: ключ «{mod.Key}» уже занят");
                        continue;
                    }
                    var argsArr = new System.Text.Json.Nodes.JsonArray();
                    var skip = false;
                    foreach (var arg in mod.Args)
                    {
                        // В песочнице абсолютные хост-пути args переводим в контейнерные
                        // (как AdaptServerForRuntime); непереводимый путь → сервер пропускается
                        if (_launcher.IsSandboxed && arg is { Length: > 2 } && char.IsLetter(arg[0]) && arg[1] == ':')
                        {
                            try { argsArr.Add(_launcher.Paths.ToRuntime(arg)); }
                            catch (InvalidOperationException)
                            {
                                Console.Error.WriteLine(
                                    $"[ClaudeSession] MCP модуля «{mod.ModuleId}» пропущен: путь {arg} недоступен в песочнице");
                                skip = true;
                                break;
                            }
                        }
                        else argsArr.Add(arg);
                    }
                    if (skip) continue;
                    servers[mod.Key] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["command"] = mod.Command,
                        ["args"] = argsArr,
                        ["env"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["MODULE_API_URL"] = mod.ApiUrl,
                            ["MODULE_API_TOKEN"] = mod.TokenFactory(),
                            ["MODULE_ID"] = mod.ModuleId,
                        },
                    };
                }
            }

            if (servers.Count == 0) return (null, "", []);
            var combined = new System.Text.Json.Nodes.JsonObject { ["mcpServers"] = servers };
            // HostTempDir среды: для песочницы это bind-mount — процесс claude увидит файл
            var tmpPath = Path.Combine(_launcher.HostTempDir, $"claude-mcp-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmpPath, combined.ToJsonString());
            // Ключ + отпечаток состава инструментов (если есть): смена per-turn флагов
            // (PERSONAS_WRITE/MENTIONS, WORKSPACE_WRITE/секции, TASKS_EXECUTE) меняет сигнатуру
            // запуска → живой процесс доживания не переиспользуется, инструменты поднимаются
            return (tmpPath, string.Join(",", servers
                    .Select(kv => shapes.TryGetValue(kv.Key, out var shp) ? $"{kv.Key}:{shp}" : kv.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)),
                servers.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList());
        }
        catch (Exception ex)
        {
            // Без лога сессия молча пойдёт без MCP-серверов (tasks/dify) — обязательно сообщаем
            Console.Error.WriteLine($"[ClaudeSession] Не удалось собрать MCP-конфиг хода, используется базовый конфиг: {ex.Message}");
            return (null, "", []);
        }
    }

    // Путь MCP-сервера в среде исполнения: локально — как есть, в песочнице — /app/mcp/...
    // (образ несёт то же дерево). null — сервера нет в целевой среде (ход без него).
    private string? MapMcpPath(string? hostPath)
    {
        if (hostPath is null) return null;
        try { return _launcher.Paths.ToRuntime(hostPath); }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"[ClaudeSession] MCP-сервер недоступен в песочнице: {hostPath}");
            return null;
        }
    }

    // Адаптация стороннего описания MCP-сервера (базовый конфиг / user-scope / личный реестр)
    // к среде: локально — без изменений; в песочнице переписываем абсолютные Windows-пути
    // (args и command) на контейнерные, а loopback-адреса (env и url) — на host.docker.internal.
    // Непереводимый путь → сервер пропускается (false). POSIX-пути оставляем как есть:
    // конфиги, писанные для контейнера (/app/...), в образе валидны.
    private bool AdaptServerForRuntime(string key, System.Text.Json.Nodes.JsonNode node)
    {
        if (!_launcher.IsSandboxed) return true;
        // localhost/127.0.0.1 в env-URL — это loopback ХОСТА (напр. DIFY_API_URL у dify):
        // из контейнера недостижим, переписываем на host.docker.internal (он в no_proxy песочницы)
        if (node["env"] is System.Text.Json.Nodes.JsonObject envObj)
        {
            foreach (var name in envObj.Select(kv => kv.Key).ToArray())
            {
                if (envObj[name] is not System.Text.Json.Nodes.JsonValue jv
                    || !jv.TryGetValue<string>(out var envVal)) continue;
                var rewritten = RewriteLoopbackUrl(envVal);
                if (!ReferenceEquals(rewritten, envVal)) envObj[name] = rewritten;
            }
        }
        // Адрес http/sse-сервера: тот же loopback хоста. Без этого локальный сервер
        // из песочницы молча мёртв — CLI стучится в 127.0.0.1 внутри контейнера
        if (node["url"] is System.Text.Json.Nodes.JsonValue urlVal
            && urlVal.TryGetValue<string>(out var url))
        {
            var rewrittenUrl = RewriteLoopbackUrl(url);
            if (!ReferenceEquals(rewrittenUrl, url)) node["url"] = rewrittenUrl;
        }
        // Команда запуска stdio-сервера: голое имя («node», «npx») оставляем среде,
        // абсолютный хост-путь переводим — иначе процесс в контейнере не стартует
        if (node["command"] is System.Text.Json.Nodes.JsonValue cmdVal
            && cmdVal.TryGetValue<string>(out var command)
            && command is { Length: > 2 } && char.IsLetter(command[0]) && command[1] == ':')
        {
            try { node["command"] = _launcher.Paths.ToRuntime(command); }
            catch (InvalidOperationException)
            {
                Console.Error.WriteLine($"[ClaudeSession] MCP-сервер «{key}» пропущен: путь {command} недоступен в песочнице");
                return false;
            }
        }
        if (node["args"] is not System.Text.Json.Nodes.JsonArray argsArr) return true;
        for (var i = 0; i < argsArr.Count; i++)
        {
            var val = argsArr[i]?.GetValue<string>();
            // Только абсолютные хост-пути вида X:\... / X:/...
            if (val is not { Length: > 2 } || !char.IsLetter(val[0]) || val[1] != ':') continue;
            try { argsArr[i] = _launcher.Paths.ToRuntime(val); }
            catch (InvalidOperationException)
            {
                Console.Error.WriteLine($"[ClaudeSession] MCP-сервер «{key}» пропущен: путь {val} недоступен в песочнице");
                return false;
            }
        }
        return true;
    }

    // http://localhost:…/http://127.0.0.1:… → http://host.docker.internal:… (для env песочницы).
    // Возвращает исходную строку (тот же экземпляр), если переписывать нечего.
    private static string RewriteLoopbackUrl(string value)
    {
        foreach (var prefix in (string[])["http://localhost", "http://127.0.0.1"])
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = value[prefix.Length..];
            // Граница хоста: конец строки, порт или путь — «localhost-foo» не трогаем
            if (rest.Length == 0 || rest[0] == ':' || rest[0] == '/')
                return "http://host.docker.internal" + rest;
        }
        return value;
    }

    // User-scope MCP-серверы (~/.claude.json, mcpServers: fal-ai и др.) — прокидываем в
    // --mcp-config только когда ход пойдёт с ИЗОЛИРОВАННЫМ CLAUDE_CONFIG_DIR, где CLI не
    // прочитает ~/.claude.json сам:
    //  - сторонний провайдер (DeepSeek/GLM) — свой профиль claude-profiles/{key};
    //  - подписка пула Claude (sub-*) — свой профиль claude-profiles/sub-{key}.
    // Для основной подписки (CONFIG_DIR = ~/.claude) НЕ дублируем — CLI читает сам (задвоение).
    // null — основной Claude, файла нет или mcpServers пуст.
    private System.Text.Json.Nodes.JsonObject? LoadUserScopeMcpServers()
    {
        if (_providers is null) return null;
        var isThirdParty = _providers.ResolveByModel(EffectiveModel) is not null;
        // Подписка пула = провайдер сессии не "claude", не сторонний ключ, а активная доп.
        // подписка (условие 1:1 с применением BuildOAuthCliEnv при выборе env хода)
        var isPoolSubscription = _subscriptionPool?.HasExtra == true
            && Info.Provider is not null && Info.Provider != "claude"
            && _providers.GetByKey(Info.Provider) is null
            && _subscriptionPool.All.FirstOrDefault(s => s.Key == Info.Provider)?.Enabled == true;
        if (!isThirdParty && !isPoolSubscription) return null;
        var path = _providers.UserClaudeJsonPath;
        try
        {
            if (!File.Exists(path)) return null;
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            return doc?["mcpServers"] is System.Text.Json.Nodes.JsonObject o && o.Count > 0 ? o : null;
        }
        catch (Exception ex)
        {
            // Без user-scope серверов ход пойдёт, но fal-ai и др. пропадут — сообщаем
            Console.Error.WriteLine($"[ClaudeSession] Не удалось прочитать user-scope MCP из {path}: {ex.Message}");
            return null;
        }
    }

    // Ничего не делаем при старте — процесс запускается при первом сообщении
    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null, int agentDepth = 0,
        bool suppressTasksExecute = false)
    {
        Info.MessageCount++;
        Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
        Info.UpdatedAt = DateTime.UtcNow;

        // Если сообщение — вызов скилла (/skill-name [args]), разворачиваем его содержимое
        var effectiveText = _skills?.TryExpandSkill(text) ?? text;
        // Картинки отправляем как image-блоки (base64), остальные файлы — инлайним в текст
        var (imagePaths, otherPaths) = AttachmentInliner.SplitImagePaths(attachedPaths);
        var fullText = AttachmentInliner.BuildMessageText(_rootPath, effectiveText, otherPaths);

        // Модель без зрения (напр. GLM): base64-блок эндпоинт принимает молча, но модель
        // домысливает содержимое (проверено — на красный PNG отвечает «Colorful»). Картинку
        // не шлём, а честно помечаем в тексте — иначе тихая галлюцинация вместо явного отказа.
        // Защита в глубину: фронт при !supportsImages вложения и не даёт прикрепить.
        if (imagePaths.Count > 0 && !Capabilities.SupportsImages)
        {
            var names = string.Join(", ", imagePaths.Select(Path.GetFileName));
            fullText += $"\n\n[Вложены изображения ({names}), но выбранная модель не поддерживает "
                      + "зрение и не может их рассмотреть — их содержимое недоступно.]";
            imagePaths = [];
        }

        return QueueTurnAsync(fullText, imagePaths, agentDepth, suppressTasksExecute);
    }

    // Ручное сворачивание контекста: /compact как обычный ход,
    // минуя счётчики сообщений, авто-имя чата и разворачивание скиллов
    public Task CompactAsync() => QueueTurnAsync("/compact", [], 0, false);

    // Ставит ход в очередь в фоне, чтобы не блокировать SignalR-соединение
    private Task QueueTurnAsync(string fullText, List<string> imagePaths, int agentDepth, bool suppressTasksExecute)
    {
        _ = Task.Run(async () =>
        {
            // ДИАГНОСТИКА повторных доставок: сколько ходов УЖЕ запарковано/идёт на _turnLock
            // в момент этой постановки. Логируем только нетривиальный случай (parked>0) —
            // обычный одиночный ход ничего не пишет. Для серверских авто-ходов parked>0 =
            // подозрение на дубль (источник — байпас адаптера или ре-энтри).
            var parked = Interlocked.Increment(ref _queuedTurns) - 1;
            if (parked > 0)
            {
                var snippet = (fullText.Length > 50 ? fullText[..50] : fullText).Replace('\n', ' ');
                Console.Error.WriteLine($"[ClaudeSession] Постановка хода в очередь _turnLock: уже запарковано {parked} (session {Info.Id}, «{snippet}»)");
            }
            try
            {
                if (_cts.IsCancellationRequested) return;
                await _turnLock.WaitAsync(_cts.Token);
                try { await RunTurnAsync(fullText, imagePaths, agentDepth, suppressTasksExecute, _cts.Token); }
                catch (OperationCanceledException) { /* остановка сессии — штатно */ }
                // Адаптер снесли из-под хода (закрытие сессии, реанимация чата) — это остановка,
                // а не сбой модели: показывать человеку «Cannot access a disposed object» нельзя
                catch (ObjectDisposedException) { /* штатная остановка */ }
                catch (Exception ex)
                {
                    // Ошибка хода обязана попадать и в лог сервера с идентификатором сессии
                    // и стектрейсом (инцидент 16.08.2026: ObjectDisposedException в ленте,
                    // в логе пусто; наружу уходит только ex.Message — системные падения
                    // по нему неотличимы друг от друга)
                    Console.Error.WriteLine($"[ClaudeSession] Ход упал с исключением (session {Info.Id}): {ex}");
                    // Статус Error выставит SessionManager по ErrorMessage
                    await _onMessage(new ErrorMessage(ex.Message));
                }
                finally
                {
                    // Ход закончился — следующий (если его инициирует человек) идёт с полным
                    // набором инструментов; действует ровно на ход внутри _turnLock
                    _currentTurnAgentDepth = 0;
                    _currentTurnSuppressTasksExecute = false;
                    // Семафор мог уйти вместе с адаптером — Release тогда некому и незачем
                    try { _turnLock.Release(); } catch (ObjectDisposedException) { /* адаптер уже снесён */ }
                }
            }
            catch (Exception ex)
            {
                // Гонка с DisposeAsync (реанимация зависшего чата убивает адаптер под
                // запаркованным ходом): ход тихо отменяем, но фиксируем в логе —
                // необработанное исключение fire-and-forget невидимо нигде
                Console.Error.WriteLine($"[ClaudeSession] Ход не стартовал (гонка с dispose сессии {Info.Id}): {ex.Message}");
            }
            finally { Interlocked.Decrement(ref _queuedTurns); }
        });

        return Task.CompletedTask;
    }

    private static string MediaTypeForExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // Блоки изображений для content стартового сообщения. Пустые/слишком большие (>8 МБ) пропускаем.
    private List<object> BuildImageBlocks(IReadOnlyList<string> imagePaths)
    {
        var blocks = new List<object>();
        foreach (var rel in imagePaths)
        {
            try
            {
                var full = FileService.SafeJoin(_rootPath, rel);
                if (!File.Exists(full)) continue;
                var bytes = File.ReadAllBytes(full);
                if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) continue;
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = MediaTypeForExt(Path.GetExtension(rel)), data = Convert.ToBase64String(bytes) }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] Не удалось прочитать вложение-изображение «{rel}»: {ex.Message}");
            }
        }
        return blocks;
    }

    public void RespondPermission(string requestId, string behavior)
    {
        if (_permissionWaiters.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult(behavior);
    }

    // Ответ пользователя на AskUserQuestion — control_response на исходный can_use_tool запрос
    public void AnswerQuestion(string toolUseId, string updatedInputJson)
    {
        if (!_pendingQuestions.TryRemove(toolUseId, out var requestId)) return;
        object updatedInput;
        try { updatedInput = JsonSerializer.Deserialize<object>(updatedInputJson)!; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ClaudeSession] Ответ на вопрос не распарсился, отправляем пустой input: {ex.Message}");
            updatedInput = new { };
        }
        SendControlResponse(requestId, new { behavior = "allow", updatedInput });
    }

    // Решение пользователя по плану (ExitPlanMode): approve → allow и Claude продолжает выполнение;
    // reject → deny с комментарием, Claude остаётся в режиме планирования
    public void RespondPlan(string requestId, bool approve, string? feedback)
    {
        if (!_pendingPlans.TryRemove(requestId, out _)) return;
        if (approve)
        {
            // Ждём, что Claude реализует план в этом ходу; если завершит без правок — дошлём команду.
            // allow без updatedInput — CLI продолжит с исходным планом (см. HandleControlRequestAsync)
            _awaitPlanExecution = true;
            _sawToolSinceApprove = false;
            SendControlResponse(requestId, new { behavior = "allow" });
        }
        else
        {
            var message = string.IsNullOrWhiteSpace(feedback)
                ? "Пользователь отклонил план. Уточни план с учётом контекста и предложи заново."
                : $"Пользователь отклонил план с комментарием: {feedback}";
            SendControlResponse(requestId, new { behavior = "deny", message });
        }
    }

    // Обработка control_request(can_use_tool): AskUserQuestion → интерактивная карточка,
    // ExitPlanMode → согласование плана, прочие инструменты → permission-пайплайн.
    // Актуальные CLI шлют permission-запросы именно этим каналом (не sdk_control_request) —
    // авто-allow здесь означал бы исполнение любых команд без карточек.
    private async Task HandleControlRequestAsync(CliRun run, JsonElement root)
    {
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
        if (!root.TryGetProperty("request", out var req)) return;
        var subtype = req.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "can_use_tool") return;

        var toolName = req.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var toolUseId = req.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? "" : "";
        var inputEl = req.TryGetProperty("input", out var ti) ? ti : default;
        var input = inputEl.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.Deserialize<object>(inputEl.GetRawText())! : new object();

        if (toolName == "AskUserQuestion")
        {
            // Лимит раундов интервью (Minor, волна 3): раньше InterviewProtocol только ПРОСИЛ
            // модель остановиться словами («лимит исчерпан, больше не спрашивай») — факт бэкенд
            // не проверял, и 3-й раунд так же уходил в карточку. Раунд сверх MaxInterviewRounds
            // отклоняем прямо на permission-канале — тем же приёмом, что CoordinatorWriteGuard
            // (см. DecidePermissionAsync), только для другого инструмента.
            if (Info.TeamImplement is { Stage: TeamImplementStage.Interview } team
                && TeamImplementPrompts.InterviewRoundsExhausted(team))
            {
                SendControlResponse(requestId, new
                {
                    behavior = "deny",
                    message = $"Лимит раундов интервью ({TeamImplementPrompts.MaxInterviewRounds}) уже " +
                              "исчерпан — больше не спрашивай, оформи остаток неясностей допущениями " +
                              "и заверши интервью маркером работы."
                }, run.Process);
                return;
            }
            // Ждём выбор пользователя — control_response отправит AnswerQuestion.
            // Статус Waiting выставит SessionManager по AskQuestionMessage
            _pendingQuestions[toolUseId] = requestId;
            await _onMessage(new AskQuestionMessage(toolUseId, input));
            return;
        }

        if (toolName == "ExitPlanMode")
        {
            // Режим «План»: Claude представил план — ждём решения пользователя (RespondPlan),
            // НЕ авто-одобряем, иначе план не выносится на согласование.
            // Статус Waiting выставит SessionManager по PlanReviewMessage
            _pendingPlans[requestId] = input;
            var plan = inputEl.ValueKind == JsonValueKind.Object && inputEl.TryGetProperty("plan", out var pl)
                ? pl.GetString() ?? "" : "";
            await _onMessage(new PlanReviewMessage(requestId, plan));
            return;
        }

        var behavior = await DecidePermissionAsync(requestId, toolName, inputEl, input);
        // Отказ в разрешении ИЛИ Interrupt: инструмент не выполнится, tool_result может не
        // прийти вовсе — снимаем ожидающую заявку атрибуции (см. _pendingFileClaims) ДО раннего
        // return по cancelled, иначе она провисит в словаре до конца жизни ClaudeSession
        // (своего TTL, в отличие от FileChangeAttributor, у _pendingFileClaims нет)
        if (behavior != "allow") _pendingFileClaims.TryRemove(toolUseId, out _);
        if (behavior == "cancelled") return; // Interrupt — процесс убит, отвечать некому
        // allow БЕЗ updatedInput: CLI продолжает с исходным вводом модели. Эхо updatedInput
        // ломало Workflow — возвращённый хэндлером ввод CLI прогоняет через доп. проверку
        // «управляющие символы, скрытые в диалоге одобрения» (исходный ввод модели ей не
        // подвергается), и резолвнутый script именованного workflow её не проходил.
        SendControlResponse(requestId, behavior == "allow"
            ? new { behavior = "allow" }
            : (object)new { behavior = "deny", message = "Пользователь отклонил действие" }, run.Process);
    }

    // Решение по инструменту: правила проекта → «всегда разрешать» этой сессии → карточка
    // пользователю. Возвращает "allow" | "deny" | "cancelled" (Interrupt во время ожидания).
    private async Task<string> DecidePermissionAsync(string requestId, string toolName, JsonElement inputEl, object toolInput)
    {
        // Гард «координатор не пишет код сам» (Э7-фикс): жёстче project-правил и «всегда
        // разрешать» — иначе достаточно один раз кликнуть «Разрешить всегда» на Bash, и
        // настройка перестаёт что-либо значить. См. CoordinatorWriteGuard про эвристику
        // и про то, почему это работает не в любом --permission-mode.
        if (Info.TeamImplement is { CoordinatorNoCode: true }
            && CoordinatorWriteGuard.IsShellTool(toolName)
            && inputEl.ValueKind == JsonValueKind.Object
            && inputEl.TryGetProperty("command", out var cmdEl)
            && cmdEl.ValueKind == JsonValueKind.String
            && CoordinatorWriteGuard.LooksLikeFileWrite(cmdEl.GetString()))
            return "deny";

        // Правила проекта: deny приоритетнее; allow — авто-разрешить; null — спросить пользователя
        var ruleDecision = PermissionRuleEvaluator.Evaluate(_permissionRules?.Invoke(), toolName, inputEl);
        if (ruleDecision == "deny") return "deny";
        // Сессия-исполнитель задачи или ход правила автоматизации персоны работают автономно —
        // отвечать на карточку разрешения некому (чат никто не открывал), и без этого исполнитель
        // вязнет в первом же permission-запросе (status=Waiting до таймаута в 60 мин) и не может
        // работать. Разрешаем ВСЕ инструменты автоматически: deny-правило проекта выше учтено,
        // а права персоны уже ограничены Persona.Tools и ExtraDisallowedTools.
        if (ruleDecision == null && (Info.TaskExecution || Info.AutomationRuleId is not null)) return "allow";
        // Свои MCP-серверы — без карточки, см. комментарий у BuiltInMcpServerPrefixes.
        if (ruleDecision == null && Array.Exists(BuiltInMcpServerPrefixes, p => toolName.StartsWith(p, StringComparison.Ordinal)))
            return "allow";
        if (ruleDecision == "allow" || _autoAllowTools.ContainsKey(toolName)) return "allow";

        var tcs = new TaskCompletionSource<string>();
        _permissionWaiters[requestId] = tcs;

        // Статус Waiting выставит SessionManager по PermissionRequestMessage,
        // Working вернёт SessionManager.RespondPermission по ответу пользователя
        await _onMessage(new PermissionRequestMessage(requestId, toolName, toolInput));

        string behavior;
        try
        {
            // Ждём ответа пользователя или таймаута 60 минут
            behavior = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(60));
        }
        catch (TaskCanceledException)
        {
            // Interrupt() отменил TCS через TrySetCanceled() — процесс уже убит
            _permissionWaiters.TryRemove(requestId, out _);
            return "cancelled";
        }
        catch (TimeoutException)
        {
            // Пользователь не ответил — deny и продолжаем
            _permissionWaiters.TryRemove(requestId, out _);
            return "deny";
        }
        _permissionWaiters.TryRemove(requestId, out _);

        // «Всегда разрешать»: запоминаем инструмент и отвечаем обычным allow
        if (behavior == "allow_always")
        {
            _autoAllowTools.TryAdd(toolName, 0);
            behavior = "allow";
        }
        return behavior;
    }

    private void SendControlResponse(string requestId, object responsePayload, Process? target = null)
    {
        var msg = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = responsePayload }
        });
        WriteLineToStdin(msg, target);
    }

    // Смена режима прав на лету: пишем control_request set_permission_mode в stdin живого
    // процесса. CLI применяет его к идущему ходу (дальнейшие tool-вызовы уже по новому режиму)
    // и отвечает control_response success (reader его игнорирует как неизвестный тип).
    // Нет процесса — false: SessionManager уже обновил Info.Mode, следующий ход пересоздастся с флагом.
    public bool TrySetPermissionModeLive(ClaudeMode mode)
    {
        var proc = _currentProcess;
        if (proc is null || proc.HasExited) return false;
        var req = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = "setmode_" + Guid.NewGuid().ToString("N")[..12],
            request = new { subtype = "set_permission_mode", mode = mode.ToCliFlag() }
        });
        WriteLineToStdin(req);
        return true;
    }

    // Смена модели на лету: control_request set_model в stdin живого процесса. CLI применяет
    // её к последующим round-trip'ам идущего хода и отвечает control_response success (reader
    // игнорирует). Модель нормализуем как для --model (резолв [1m] по способности пула). Нет
    // процесса — false: SessionManager уже обновил Info.Model, следующий ход пересоздастся с ней.
    public bool TrySetModelLive(string model)
    {
        var proc = _currentProcess;
        if (proc is null || proc.HasExited) return false;
        var req = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = "setmodel_" + Guid.NewGuid().ToString("N")[..12],
            request = new { subtype = "set_model", model = ResolveModelForCli(model) ?? model }
        });
        WriteLineToStdin(req);
        return true;
    }

    // Единая точка записи в stdin процесса — под _stdinLock, чтобы параллельные
    // control_response (SignalR-потоки + памп) не перемешали JSON-строки
    private void WriteLineToStdin(string line, Process? target = null)
    {
        // target — процесс прогона, приславшего запрос: control_request/can_use_tool приходит
        // из stdout конкретного прогона, и ответ должен вернуться в НЕГО, а не в _currentProcess,
        // которым к моменту ответа мог стать уже новый прогон. public-точки входа без контекста
        // прогона (AnswerQuestion/RespondPlan из SignalR-потока) передают null → пишем в текущий.
        var proc = target ?? _currentProcess;
        if (proc is null || proc.HasExited) return;
        _stdinLock.Wait();
        try
        {
            proc.StandardInput.WriteLine(line);
            proc.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            // Процесс мог завершиться между проверкой и записью
            Console.Error.WriteLine($"[ClaudeSession] Запись в stdin не удалась: {ex.Message}");
        }
        finally { _stdinLock.Release(); }
    }

    // Закрытие stdin под тем же локом — не обрываем чужую запись на середине строки.
    // StdinClosed помечаем до Close: прогон с закрытым stdin ходы больше не принимает
    private void CloseStdin(CliRun run)
    {
        _stdinLock.Wait();
        try
        {
            run.StdinClosed = true;
            run.Process.StandardInput.Close();
        }
        catch { /* поток уже закрыт или процесс мёртв — не критично */ }
        finally { _stdinLock.Release(); }
    }

    // Закрыть stdin, только если прогон реально простаивает. Проверка условия — ПОД
    // _stdinLock: между внешней проверкой и закрытием мог проскочить TrySubmitTurn
    // (TurnDone=false), и безусловное закрытие оставило бы свежий ход с мёртвым stdin
    // (permission-ответы не записать, ход умер бы по часовому IdleTimeout)
    private void CloseStdinIfIdle(CliRun run)
    {
        _stdinLock.Wait();
        try
        {
            if (!run.TurnDone || run.HasPendingBg || run.StdinClosed) return;
            run.StdinClosed = true;
            run.Process.StandardInput.Close();
        }
        catch { /* поток уже закрыт или процесс мёртв — не критично */ }
        finally { _stdinLock.Release(); }
    }

    // Отдать ход живому процессу доживающего прогона (same-process ход). false — прогон
    // непригоден (умер/stdin закрыт/запись сорвалась): ход пойдёт новым процессом
    private bool TrySubmitTurn(CliRun run, string userMessageJson, long turnSeq)
    {
        _stdinLock.Wait();
        try
        {
            if (run.StdinClosed || run.Process.HasExited) return false;
            // Идёт ход-продолжение CLI (ответ на task-notification) — его result придёт
            // раньше нашего и не должен завершить этот ход
            if (run.ContinuationActive)
            {
                Interlocked.Increment(ref run.SkipResults);
                run.ContinuationActive = false;
                // Continuation — отдельный режим: фильтр SkipResults из Д1 защищает его событийный
                // хвост. ReuseSubmit неприменим (окно продолжения, а не завершающийся прогон).
                run.ReuseSubmit = false;
                CorrTrace("submit-turn(skip++)", Info.Id, run);
            }
            else
            {
                // Без continuation и фоновых задач прогон после result уже завершается —
                // same-process submit в такое окно это гонка с умирающим процессом. Хвостовые
                // события обречённого процесса ложно взвели бы TurnGotEvent (фильтра SkipResults
                // тут нет) → смерть ушла бы как легитимный Unreachable. Флаг заставляет
                // ShouldRetryEmptyExit трактовать её как TOCTOU (тихий ретрай той же парой).
                // Доживающие агенты (HasPendingBg) держат процесс живым — там ход легитимен,
                // смерть посреди хода реальна, ретрай не нужен.
                run.ReuseSubmit = !run.HasPendingBg;
                CorrTrace("submit-turn(no-skip)", Info.Id, run);
            }
            // Ход принят живым прогоном — его exited теперь наш (метка обновляется до записи
            // в stdin: reader увидит уже новое значение, если процесс умрёт сразу после submit)
            Interlocked.Exchange(ref run.LastTurnSeq, turnSeq);
            run.TurnTcs = CliRun.NewTcs();
            run.TurnDone = false;
            run.TurnGotEvent = false;       // новый ход — событий прогона ещё не было
            run.RetryOnEmptyExit = true;    // same-process: смерть до первого события = гонка TOCTOU
            run.Process.StandardInput.WriteLine(userMessageJson);
            run.Process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex)
        {
            run.TurnDone = true; // прогон между ходами; финализация резолвит его TurnTcs
            Console.Error.WriteLine($"[ClaudeSession] Ход в живой процесс не записался, стартуем новый: {ex.Message}");
            return false;
        }
        finally { _stdinLock.Release(); }
    }

    public void Interrupt()
    {
        // Смерть процесса ниже — ожидаемая: помечаем ход прерванным ДО Kill, чтобы обработчик
        // Exited (он прилетает асинхронно, иногда мгновенно) уже видел флаг и не слал ошибку.
        // Флаг снимается в начале следующего хода (RunTurnAsync).
        _interruptedByUser = true;
        if (_currentProcess is { } proc) _launcher.Kill(proc, _currentTurnId);
        // Ход убит, но Workflow-агенты — независимые процессы и не обязаны погибнуть вместе
        // с ним (см. NoteOwnerProcessGone): даём им короткое окно доказать, что работа жива,
        // вместо немедленного «прерван»
        List<WorkflowWatcher> workflowWatchers;
        lock (_workflowWatchers) workflowWatchers = _workflowWatchers.Where(w => !w.IsDisposed).ToList();
        foreach (var w in workflowWatchers) w.NoteOwnerProcessGone();
        // Отменяем все ожидающие permission-диалоги: процесс убит, ответа не будет
        CancelPendingControlResponses();
        _awaitPlanExecution = false;
        _forceNonPlanNextTurn = false;
    }

    private async Task RunTurnAsync(string text, IReadOnlyList<string> imagePaths, int agentDepth,
        bool suppressTasksExecute, CancellationToken ct)
    {
        // Глубина делегирования действует ровно на этот ход (внутри _turnLock):
        // MCP-конфиг ниже собирается уже с учётом анти-рекурсии, сброс — в finally
        _currentTurnAgentDepth = agentDepth;
        _currentTurnSuppressTasksExecute = suppressTasksExecute;

        // P31: подстраховка от залипания pending control-состояния. К началу нового хода
        // (предыдущий завершился result'ом) _pendingQuestions/_pendingPlans/_permissionWaiters
        // обязаны быть пусты — ответы на control присланы. Если не пусты, это зависший мусор
        // после смерти процесса в порядке «EOF первым» (AskUserQuestion/ExitPlanMode): тогда
        // HandleProcessExitedAsync выходит по DeathDiagnosed, не доходя до CancelPending, и без
        // этой чистки HasPendingControlResponse() остался бы true навсегда — каждая следующая
        // смерть рапортовала бы «во время ожидания разрешения». TrySetCanceled на уже закрытых
        // waiter'ах — no-op, легитимного ожидания в начале хода не бывает.
        CancelPendingControlResponses();

        // Единственная точка сброса признака пользовательского прерывания: прошлый ход отжит,
        // его отложенное Exited уже никого не касается, а смерть процесса в ЭТОМ ходе снова
        // обязана доехать до клиента ошибкой (P27).
        _interruptedByUser = false;

        // Номер этого хода: садится в прогон, который его примет (CliRun.LastTurnSeq), и уходит
        // наружу в ExitedMessage. Выделяем ДО подачи — снимок SubmittedTurnSeq, снятый вызывающим
        // раньше (фолбэк на старте попытки), гарантированно меньше нашего номера.
        var turnSeq = Interlocked.Increment(ref _turnSeq);

        // Картинки, которые модель показывает в ленте, живут в служебной папке вложений —
        // в git-статусе проекта они светиться не должны. Лениво (один раз за жизнь сессии)
        // и best-effort: нет репозитория или прав на запись — ход это не касается.
        if (!_attachmentsExcludeEnsured && Info.ProjectId is not null)
        {
            _attachmentsExcludeEnsured = true;
            try { GitService.EnsureAttachmentsExcluded(_rootPath); }
            catch { /* игнор вложений — не критично для хода */ }
        }

        // OTel: корневой спан хода. using гарантирует Dispose в конце метода
        // (все return-пути и исключения). turn_id генерируем здесь — он нужен
        // спану для всех путей (включая same-process, где _launcher.Start не идёт).
        _currentTurnId = Guid.NewGuid().ToString("N")[..12];
        using var turnActivity = TurnTelemetry.StartTurnSpan(
            sessionId: Info.ClaudeSessionId ?? Info.Id.ToString(),
            turnId: _currentTurnId,
            model: EffectiveModel,
            provider: Info.Provider);

        // Модель в спане выше — намерение (что просили). Факт назовёт сам CLI по ходу
        // прогона, тогда тег перезапишется на реальную модель; см. HandleStreamJson.
        _turnActivity = turnActivity;
        _turnCliModel = null;

        // --print обязателен: без него --output-format/--input-format/--include-partial-messages/--permission-prompt-tool не работают
        // --input-format stream-json нужен: мы посылаем JSON-объекты в stdin, а не plain text
        var args = new List<string>
        {
            "--print",
            "--verbose",
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            "--include-partial-messages",
            "--permission-prompt-tool", "stdio"
        };

        // Отключаем хуки плагинов на хосте (окна консоли на каждый ход); скиллы остаются.
        // Тем же файлом гасится плагин браузера, если он персоне не положен по роли —
        // путь файла входит в сигнатуру прогона, но решение постоянно в рамках сессии.
        args.AddRange(ClaudeRuntimeSettings.HooksOffArgs(_launcher, _browserEnabled));

        if (Info.ClaudeSessionId is not null)
            args.AddRange(["--resume", Info.ClaudeSessionId]);

        // Режим прав у claude CLI задаётся флагом --permission-mode (значения: default,
        // acceptEdits, plan, auto, dontAsk, bypassPermissions), а НЕ --mode (такого флага нет).
        // После одобрения плана один ход выполняем без plan, чтобы Claude реализовал, а не планировал заново.
        if (_forceNonPlanNextTurn)
            _forceNonPlanNextTurn = false;
        else
            args.AddRange(["--permission-mode", Info.Mode.ToCliFlag()]);

        // Пустая модель сессии = «по умолчанию»: подставляем глобальную настройку, а не
        // отдаём выбор CLI (иначе «по умолчанию» значило бы в разных местах разные модели)
        if (EffectiveModel is { } turnModel && !string.IsNullOrWhiteSpace(turnModel))
            args.AddRange(["--model", ResolveModelForCli(turnModel)!]);

        if (!string.IsNullOrWhiteSpace(Info.Effort))
            args.AddRange(["--effort", Info.Effort]);

        // Подсказка следующего сообщения: CLI после result испускает prompt_suggestion
        // (генерация фоном с переиспользованием prompt cache хода; при холодном кэше CLI
        // сам пропускает). Только родной Claude — сторонним провайдерам фоновые запросы
        // не включаем (кэш-экономика чужая).
        var promptSuggestionsActive = _providers is null || _providers.ResolveByModel(EffectiveModel) is null;
        if (promptSuggestionsActive)
            args.AddRange(["--prompt-suggestions", "true"]);

        // Файловые сабагенты-персоны. На агентном ходу (agentDepth >= 1) план урезается
        // до pmem-серверов: подсказки и --add-dir не даём (анти-рекурсия, как
        // TASKS_EXECUTE/PERSONAS_MENTIONS), но .md-файлы в cwd проекта/Chats CLI видит
        // и без add-dir — если модель всё же позовёт Task(handle), память сабагента
        // должна быть достижима, иначе frontmatter (mcpServers: [pmem_…]) укажет в пустоту.
        // Ошибки провайдера — ход без консультантов.
        PersonaAgentsContext? personaAgents = null;
        if (_personaAgentsProvider is not null
            && !_disallowedTools.Contains("Task", StringComparer.Ordinal))
        {
            try { personaAgents = _personaAgentsProvider(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] План сабагентов-персон не собрался: {ex.Message}");
            }
            if (agentDepth >= 1 && personaAgents is not null)
                personaAgents = personaAgents with { AddDirs = [], AgentHandles = [] };
        }

        // MCP-конфиг: создаём каждый ход с актуальным dataset id (мог появиться после создания сессии)
        var currentWk = _wkStore?.GetByPath(_rootPath);
        var currentDatasetId = currentWk?.DifyDatasetId;
        var (turnMcpPath, mcpServerKeys, mcpServerNames) = BuildTurnMcpConfig(currentDatasetId, personaAgents);
        var effectiveMcpConfig = turnMcpPath ?? _mcpConfigPath;
        if (!string.IsNullOrWhiteSpace(effectiveMcpConfig) && File.Exists(effectiveMcpConfig))
        {
            // Аргумент — путь В СРЕДЕ исполнения (temp хода лежит на bind-mount песочницы);
            // файл базового конфига может оказаться вне неё — тогда ход идёт без MCP
            try { args.AddRange(["--mcp-config", _launcher.Paths.ToRuntime(effectiveMcpConfig)]); }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] MCP-конфиг недоступен в песочнице, ход без него: {ex.Message}");
            }
        }

        // Папки файловых сабагентов: CLI сканирует {dir}/.claude/agents при старте процесса —
        // правки персон применяются со следующего хода
        if (personaAgents is not null)
            foreach (var dir in personaAgents.AddDirs)
            {
                // Резервные папки агентов не смонтированы в песочницу — пропускаем:
                // агенты чата и так лежат в {chatRoot}/.claude/agents, консультанты доступны через persona_ask
                try { args.AddRange(["--add-dir", _launcher.Paths.ToRuntime(dir)]); }
                catch (InvalidOperationException) { }
            }

        // pmem-серверы консультантов: сессионный allow, БЕЗ него вызов из фонового сабагента
        // упирается в permission-запрос, на который некому ответить (проверено вживую).
        // Закрыть их от ГЛАВНОЙ сессии технически нельзя — permission-правила общие на процесс,
        // а disallow имени сервера проникает и в сабагента, глуша его allow-list (проверено
        // вживую: сабагент получил только Read/Grep). Осознанный компромисс: главную сессию
        // ограничиваем инструкцией в hint (BuildMentionsHint) — «не трогай mcp__pmem_*».
        if (personaAgents is { MemoryServers.Count: > 0 })
            args.AddRange(["--allowedTools",
                string.Join(",", personaAgents.MemoryServers.Select(s => "mcp__" + s.ServerKey))]);

        // Блокируем коннекторы аккаунта claude.ai — они вливаются помимо --mcp-config.
        if (_disallowedTools.Length > 0)
            args.AddRange(["--disallowedTools", string.Join(",", _disallowedTools)]);

        // Слой персоны из системного промпта — жёсткая часть сигнатуры прогона
        // (смена собеседника посреди доживания = несовместимый ход → новый процесс)
        string? personaLayerPrompt = null;

        // Секции промпта хода — нужны ниже, при записи снимка «что ушло модели»
        // (снимок пишется уже после развилки same-process, когда известно, применён ли он)
        List<PromptSectionDto> sections = [];

        // Системный промпт: пересчитываем и передаём КАЖДЫЙ ход. Ход в новом процессе
        // (claude --print --resume) получает его через --append-system-prompt — тот не
        // сохраняется в транскрипте сессии: не передать → инструкции (fal-ai/запрет ASCII,
        // Dify, теги) пропадут. Same-process ход промпт живого процесса НЕ обновляет —
        // recall/подсказки на нём остаются со старта прогона (мягкая деградация).
        {
            // Секции — единственный источник правды: из этого списка собирается и текст для
            // --append-system-prompt (TurnPromptAssembler.Combine), и снимок «что ушло модели»
            // (кнопка под постом). Пустые куски список игнорирует — как игнорировала их
            // прежняя склейка «пусто ? кусок : накопленное + \n\n + кусок».
            // stable=false — кусок пересчитывается под текст хода (recall, привязки, граф):
            // такие ломают переиспользование кэша префикса, и в UI это видно.
            // group — чья это часть: по ней UI считает, во сколько обходится персона.
            void Add(string key, string title, string? text, bool stable = true, string group = "misc")
            {
                if (!string.IsNullOrWhiteSpace(text))
                    sections.Add(new PromptSectionDto(key, title, text, Stable: stable, Group: group));
            }

            // Системный промпт проекта — теми же частями, что показывает карточка проекта
            // (/effective-prompt): встроенная константа, промпт проекта, автодополнения Dify.
            // Индекс в ключе разводит две auto-части (блок Dify и инструкция по тегам).
            var partIndex = 0;
            foreach (var part in ProjectManager.GetSystemPromptParts(
                         _rawSystemPrompt, currentDatasetId != null, currentWk?.DocumentTags))
            {
                var partTitle = part.Kind switch
                {
                    "builtin" => "Общие правила приложения",
                    "user" => "Что вы написали в карточке проекта",
                    _ => "Про базу знаний проекта",
                };
                Add($"project-{part.Kind}-{partIndex++}", partTitle, part.Content, group: "project");
            }

            // Подсказка про AskUserQuestion — только на интерактивном ходу, где на карточку
            // есть кому ответить: не исполнитель задачи, не ход правила автоматизации персоны
            // и не делегированный ход (вызван Task() из другого чата) — тот же признак «нет
            // живого пользователя», что и в гейте авто-allow permission (Info.TaskExecution/
            // AutomationRuleId) выше по файлу.
            if (!Info.TaskExecution && Info.AutomationRuleId is null && _currentTurnAgentDepth < 1)
            {
                var askHint =
                    "Если нужно уточнить что-то у пользователя и у вопроса есть 2–4 осмысленных варианта ответа — " +
                    "задай его инструментом AskUserQuestion (рекомендуемый вариант первым) вместо вопроса текстом: " +
                    "он покажет пользователю кнопки для выбора. Открытый вопрос без осмысленных вариантов — как обычно, текстом.";
                Add("ask-question", "Как задавать вопросы кнопками", askHint);
            }

            // Голосовой режим чата: ответ слушают, а не читают — коротко и без таблиц/кода/схем.
            // Вторая половина правила — оговорка в конце слоя персоны (PersonaPromptBuilder):
            // слой персоны клеится ПОСЛЕ секций и без неё перебил бы этот формат своим.
            if (Info.VoiceMode)
                Add("voice-mode", "Формат для голосового режима", Prompts.VoicePrompts.SectionText);

            // Подсказка про систему задач — только когда tasks-server подключён
            if (_tasksMcp is not null)
            {
                var scope = _tasksMcp.ProjectId is not null
                    ? "Текущий контекст — задачи этого проекта."
                    : "Текущий контекст — личные задачи пользователя (вне проектов).";
                var columnsHint = _tasksMcp.ProjectId is not null
                    ? " У проекта может быть Kanban-доска с кастомными колонками: получи их через tasks_board_columns и клади задачу в нужную колонку, передавая columnId в tasks_create/tasks_update (статус выставится по категории колонки)."
                    : "";
                // tasks_run_executor подключён ВСЕГДА (состав инструментов не зависит от хода);
                // на делегированном ходу бэкенд ответит отказом — предупреждаем заранее,
                // чтобы модель не тратила ход на заведомо запрещённый вызов
                var executeHint = _currentTurnAgentDepth < 1
                    ? " tasks_run_executor запускает Claude-исполнителя задачи (отдельная сессия, работает в фоне)."
                    : " tasks_run_executor на этом ходу вернёт отказ: ход инициирован другим чатом, "
                      + "и цепочка делегирования дальше не идёт — верни результат тому, кто тебя позвал.";
                // Поручение задачи персоне — только когда доступен и personas-server (есть personas_list)
                var personaExecHint = _personasMcp is not null
                    ? " Чтобы поручить задачу персоне-исполнителю, передай её personaId в tasks_create/tasks_update — " +
                      "задачу выполнит Claude от её лица; список персон и их id — personas_list."
                    : "";
                // Прикрепление итога выполнения — задачи с проектом имеют файлы; результат полезен всегда
                var resultHint = _tasksMcp.ProjectId is not null
                    ? " Завершая задачу через tasks_complete, прикрепляй итог: resultMarkdown — короткое описание сделанного, linkedFiles — пути затронутых файлов проекта (от корня, через /)."
                    : " Завершая задачу через tasks_complete, прикрепляй итог: resultMarkdown — короткое описание сделанного.";
                // Кросс-проектные ProjectTasks-привязки: тебе доступны задачи ещё каких-то проектов
                var crossProjectHint = _tasksMcp.ExtraProjectIds is { Count: > 0 }
                    ? " Тебе также доступны задачи ДРУГИХ проектов владельца (кросс-проектная привязка) — " +
                      "список и доступность (полный/только чтение) — tasks_list_projects; в tasks_create/tasks_list " +
                      "передай их projectId явно, чтобы адресовать задачу туда."
                    : "";
                var tasksHint =
                    "У пользователя есть встроенная система задач (вкладка «Задачи» в проекте и раздел «Календарь»). " +
                    "Управляй ею через MCP-инструменты mcp__tasks__* (tasks_list, tasks_search, tasks_get, tasks_create, " +
                    "tasks_update, tasks_complete, tasks_delete, tasks_add_subtask, tasks_toggle_subtask, tasks_board_columns). " + scope + " " +
                    "Когда пользователь просит создать/найти/изменить задачу, напоминание или список дел — используй эти инструменты, " +
                    "а не файлы или собственный список. Даты — в формате YYYY-MM-DD, время HH:MM." + columnsHint + executeHint + personaExecHint + resultHint + crossProjectHint;
                Add("mcp-tasks", "Как работать с задачами", tasksHint, group: "mcp");
            }

            // Трейлер истории решений (ADR-004) — рядом с конвенцией Co-Authored-By: одной
            // строкой, только для проектных чатов владельца
            Add("dossier-trailer", "Трейлер истории решений", _dossierTrailerHint, group: "project");

            // Подсказка про базу заметок — только когда notes-server подключён
            if (_notesMcp is not null)
            {
                var scope = _notesMcp.ProjectId is not null
                    ? "По умолчанию создавай заметки в notes/ текущего проекта; source=\"personal\" — в личный vault."
                    : "По умолчанию создавай заметки в личный vault пользователя; source=<projectId> — в notes/ проекта.";
                // Модуль комментариев и редких операций — только когда он смонтирован этой
                // сессии: подсказывать инструменты, которых нет в tools/list, значит гнать
                // модель на «No such tool available»
                var annotationsHint = _notesMcp.AnnotationsEnabled
                    ? " Плюс редкие операции: notes_backlinks, notes_graph, notes_delete, notes_daily (дневник), " +
                      "notes_promote_task (чекбокс → задача), notes_resolve ([[ссылка]] → заметка). " +
                      "Комментарии к markdown-документам: notes_annotate (оставить комментарий к дословному фрагменту " +
                      "документа — anchorText копируй точно из файла), notes_annotations (комментарии документа с их " +
                      "статусами), notes_reply/notes_thread (ответы в треде комментария), " +
                      "notes_set_status (resolved = обработан), notes_search со status:open — найти необработанные."
                    : "";
                var notesHint =
                    "У пользователя есть база знаний «Заметки» (Obsidian-совместимая: markdown-файлы со связями [[Заголовок]], " +
                    "обратными ссылками и графом). Веди её через MCP-инструменты mcp__notes__* (notes_list, notes_search, " +
                    "notes_semantic_search, notes_read, notes_create, notes_update, notes_move). " + scope + " " +
                    "Связывай заметки друг с другом через [[Заголовок другой заметки]] — по этим ссылкам строится граф знаний. " +
                    "Когда пользователь просит записать/законспектировать/связать мысль или найти по заметкам — используй эти инструменты." +
                    annotationsHint;
                Add("mcp-notes", "Как работать с заметками", notesHint, group: "mcp");
            }

            // Подсказка про виджеты — только когда widgets-server подключён
            if (_widgetsMcp is not null)
            {
                var widgetsHint =
                    "Тебе доступен инструмент mcp__widgets__widget_show — интерактивный HTML-виджет прямо в ленте чата. " +
                    "Используй его, когда наглядность лучше текста: дашборды и сводки с метриками, графики и диаграммы " +
                    "(рисуй сам через inline SVG/canvas), таблицы с сортировкой, калькуляторы, мини-игры, интерактивные " +
                    "демонстрации. Требования к html: self-contained фрагмент БЕЗ <html>/<head>/<body>, все стили и " +
                    "скрипты — inline; внешние ресурсы (CDN-скрипты, картинки по URL, шрифты, fetch) заблокированы " +
                    "песочницей — не используй их вовсе. Лимит 64 КБ. Для попадания в тему приложения используй " +
                    "CSS-переменные var(--cc-bg), var(--cc-text), var(--cc-accent), var(--cc-border), var(--cc-muted). " +
                    "Верстай адаптивно: лента бывает узкой (320px). Виджет уже показан пользователю — не пересказывай " +
                    "его содержимое текстом, достаточно короткого комментария.";
                Add("mcp-widgets", "Как показывать виджеты в чате", widgetsHint, group: "mcp");
            }

            // Подсказка про показ картинок — только у чата с проектом: локальный путь фронт
            // резолвит относительно RootPath проекта (ChatImage), вне проекта показать нечем.
            // Без этой подсказки модель знает единственный способ «показать» — виджет, и уходит
            // в тупик: в песочнице виджета внешние ресурсы запрещены, остаётся только base64.
            if (Info.ProjectId is not null)
            {
                var imagesHint =
                    "Чтобы ПОКАЗАТЬ пользователю картинку в ленте чата (скриншот, сгенерированное или скачанное " +
                    "изображение, готовый график-файл), виджет не нужен: сохрани файл внутри рабочей папки проекта " +
                    "и вставь в ответ markdown-картинку ![подпись](относительный/путь.png) — приложение подгрузит " +
                    "её из проекта и покажет прямо в сообщении. Путь пиши от корня проекта через /, абсолютный путь " +
                    "внутри проекта тоже понимается. Файл ВНЕ папки проекта (например во временной папке системы) " +
                    "показать нельзя — сначала перенеси его в проект. Служебные картинки, которым не место в " +
                    "репозитории (скриншоты, промежуточные кадры), клади в подпапку " + FileService.AttachmentsDir +
                    "/ — она скрыта из дерева файлов, из синка базы знаний и исключена из git. Не встраивай картинку " +
                    "в виджет и не печатай её base64 в чат: в песочнице виджета внешние ресурсы заблокированы, " +
                    "а base64 в ленте бесполезен и съедает контекст. Учти: прочитать картинку инструментом (Read) — " +
                    "это показать её СЕБЕ, в ленте пользователя она так не появится; единственный способ показать " +
                    "ему — markdown-картинка на файл внутри проекта. Читай изображение в свой контекст, только если " +
                    "тебе самому нужно его рассмотреть: крупный файл дорого стоит и раздувает контекст.";
                Add("images", "Как показывать картинки", imagesHint, group: "project");
            }

            // Манифест recall (F3): что персона подтянула в этот ход — заметки + память.
            List<RecallItem>? manifestItems = null;

            // Auto-recall: релевантные заметки по тексту хода. Имеет смысл только когда
            // notes-server подключён (в блоке фигурирует notes_read по id). Провайдер сам
            // гейтит по флагу и failsafe-таймауту; исключения не должны ронять ход.
            if (_recallProvider is not null && _notesMcp is not null)
            {
                RecallBlock? recallBlock = null;
                try { recallBlock = await _recallProvider(text); }
                catch { /* recall не должен ронять ход */ }
                Add("recall-notes", "Заметки, подходящие к вопросу", recallBlock?.Text, stable: false, group: "recall");
                if (recallBlock?.Items.Count > 0)
                {
                    manifestItems ??= new List<RecallItem>();
                    manifestItems.AddRange(recallBlock.Items);
                }
            }

            // Подсказка про раздел «Персоны» — только когда personas-server подключён
            if (_personasMcp is not null)
            {
                var scope = _personasMcp.ProjectId is not null
                    ? "Текущий контекст — проект: создавая проектную персону (scope \"project\"), projectId можно не указывать."
                    : "Текущий чат вне проекта: по умолчанию создаются глобальные персоны, для проектной укажи projectId.";
                // Write-инструменты персон подключены ВСЕГДА: гейт по глубине делегирования снят
                // (давал самый частый «No such tool available» прода, а ограничения не нёс —
                // права режут Persona.Tools/ExtraDisallowedTools и allow-list файловых агентов)
                var personasHint =
                    "У пользователя есть раздел «Персоны» — AI-собеседники с именем, ролью, характером и аватаром, " +
                    "глобальные или привязанные к проекту. Смотри их через mcp__personas__* (personas_list, personas_get). " +
                    scope +
                    " Управляй ими: personas_create, personas_update, personas_delete, personas_generate_avatar — " +
                    "когда пользователь просит создать/изменить/удалить персону или сгенерировать ей аватар. " +
                    "Создавая персону, заполняй ВСЕ слоты характера: character (на «ты», «Ты — …»), tone, mustDo, " +
                    "mustNot, outputFormat, speechExamples; приветствие — в greeting от её лица.";
                // Привязки персон (флаг persona-bindings) — кратко про инструменты работы с ними
                if (_personasMcp.BindingsEnabled)
                {
                    personasHint +=
                        " У персон есть «привязки» — источники знаний и правила с условиями применения: " +
                        "personas_bindings_list — посмотреть, personas_suggest_bindings — предложить (не сохраняет)";
                    personasHint +=
                        ", personas_bindings_set — заменить набор; в personas_create — параметры bindings/autoBindings. " +
                        "Свои собственные привязки персона менять не может.";
                }
                Add("mcp-personas", "Как работать с персонами", personasHint, group: "persona");
            }

            // Подсказка про рабочее пространство — только когда workspace-server подключён
            if (_workspaceMcp is not null)
            {
                var wsScope = _workspaceMcp.ProjectId is not null
                    ? "Текущая сессия идёт в проекте — его файлы правь встроенными Read/Edit/Write, а не через mcp__wsp__files_*."
                    : "Текущая сессия — чат вне проекта.";
                // WORKSPACE_WRITE теперь всегда "1" (стабильный набор инструментов, без гейта по тексту хода)
                // Подсказка про чаты — только когда секция chats реально подключена этим ходом.
                // chats-инструменты вне write-гейта (детерминированный состав с 920defd1)
                // На делегированном ходу chats_send и удаление отклонит бэкенд — предупреждаем,
                // чтобы модель не тратила ход на заведомо запрещённый вызов (сами инструменты
                // на месте: состав от хода не зависит)
                var delegatedTurn = _currentTurnAgentDepth >= 1;
                var chatsHint = !_workspaceMcp.Sections.Contains("chats") ? ""
                    : delegatedTurn
                        ? " chats_send на этом ходу вернёт отказ: ход инициирован другим чатом, " +
                          "и цепочка делегирования дальше не идёт."
                        : " Плюс чаты пользователя: chats_list, chats_history, chats_create, " +
                          "chats_update (переименование) и chats_send — полноценный ход в другом чате " +
                          "от имени пользователя (результат виден ему в ленте).";
                // Предупреждение про разрушающие операции — только когда секция destructive смонтирована
                var destructiveHint = !_workspaceMcp.Sections.Contains("destructive") ? ""
                    : delegatedTurn
                        ? " Удаление (files_delete, chats_delete) на делегированном ходу запрещено."
                        : " Разрушающие операции files_delete и chats_delete НЕВОССТАНОВИМЫ: применяй их ТОЛЬКО " +
                          "по явной просьбе пользователя удалить конкретный файл или чат, никогда по своей инициативе.";
                // Git — только когда секция git смонтирована (идёт с files). Запись истории
                // (stage/commit) — отдельная секция git_write: её даёт лишь явно включённый
                // ключ git, пресет по роли оставляет чтение.
                var gitHint = !_workspaceMcp.Sections.Contains("git") ? ""
                    : _workspaceMcp.Sections.Contains("git_write")
                        ? " Git любого проекта: git_status, git_diff, git_log, а по явной просьбе — " +
                          "git_stage и git_commit."
                        : " Git любого проекта (только чтение): git_status, git_diff, git_log.";
                // Базы знаний Dify пользователя (личные и публичные, не проектные) — секция knowledge_bases.
                var kbHint = _workspaceMcp.Sections.Contains("knowledge_bases")
                    ? " Базы знаний пользователя: kb_list, kb_get, kb_search (семантика/полнотекст), kb_add_document."
                    : "";
                var workspaceHint =
                    "Тебе доступно всё рабочее пространство пользователя через MCP-инструменты mcp__wsp__*: " +
                    "список проектов и их карточки (projects_list → projects_get), файлы любого проекта " +
                    "(files_tree, files_read, files_search), базы знаний проектов (knowledge_search, knowledge_status) " +
                    "и единый поиск по заметкам и задачам (search_unified). " +
                    "Запись: projects_create/projects_update, files_write/files_mkdir/files_rename, " +
                    "knowledge_index (добавить файл в базу). files_write используй только для ДРУГИХ проектов." +
                    gitHint + kbHint + chatsHint + destructiveHint + " " + wsScope + " " +
                    "Когда пользователь спрашивает «где-то у меня было…» — начинай с search_unified." +
                    " Если вызов вернул «No such tool available» — сервер ещё подключается: " +
                    "подожди мгновение и повтори тот же вызов.";
                Add("mcp-workspace", "Как искать по проектам и файлам", workspaceHint, group: "mcp");
            }

            // Подсказка про долгую память. Персонная сессия — личная (memory_*) + командная (team_*);
            // обычный проектный чат без персоны — только память КОМАНДЫ проекта (team_memory_*).
            if (_memoryMcp is not null)
            {
                var hasPersonal = !string.IsNullOrEmpty(_memoryMcp.PersonaId);
                var hasTeam = !string.IsNullOrEmpty(_memoryMcp.ProjectId);
                string? memoryHint = hasPersonal
                    ? "У тебя есть долгая память между разговорами — управляй ей через MCP-инструменты mcp__memory__* " +
                      "(memory_remember, memory_search, memory_list, memory_rethink, memory_forget). Типы: semantic — устойчивые факты и " +
                      "предпочтения пользователя; episodic — что было/обсуждалось в прошлых разговорах; procedural — выученные " +
                      "приёмы и правила. Когда узнаёшь что-то важное о пользователе или договариваешься о чём-то на будущее — " +
                      "запоминай это (memory_remember). Когда нужно вспомнить контекст — ищи в памяти (memory_search). Записи можно " +
                      "не только добавлять и забывать: если факт изменился — не плоди дубль, а УТОЧНИ существующую запись по id " +
                      "через memory_rethink (перезапись текста)."
                    : null;
                if (hasTeam)
                {
                    var teamHint = hasPersonal
                        ? " Кроме личной памяти у тебя есть память КОМАНДЫ проекта — общие факты и договорённости, " +
                          "которые видят и могут править ВСЕ персоны команды (не только ты): "
                        : "У тебя есть общая память КОМАНДЫ проекта — факты, решения и договорённости, которые видят и " +
                          "используют все, кто работает в этом проекте: ";
                    teamHint +=
                        "mcp__memory__team_memory_remember (добавить общий факт/решение проекта), team_memory_list " +
                        "(посмотреть, что уже знает команда), team_memory_update (уточнить/переписать запись по id, " +
                        "когда общий факт изменился — вместо дубля), team_memory_forget (удалить устаревшее). Пиши туда то, что " +
                        "относится к проекту в целом" +
                        (hasPersonal
                            ? " и полезно другим персонам команды — а не то, что касается лично тебя (это остаётся в memory_remember)."
                            : " и полезно в дальнейшей работе над ним. Если пользователь просит «запомнить для команды/проекта» — используй team_memory_remember.");
                    memoryHint = memoryHint is null ? teamHint : memoryHint + teamHint;
                }
                Add("mcp-memory", "Как пользоваться долгой памятью", memoryHint, group: "persona");
            }

            // Подсказка про @упоминания (список «@handle — Роль (Имя)» + persona_ask) —
            // только при включённом флаге persona-mentions и наличии других персон
            if (_personasMcp?.MentionsHint is { } mentionsHint)
                Add("persona-mentions", "Кого можно позвать через @", mentionsHint, group: "persona");

            // Подсказка про субагентов-персон в Workflow: перечисляем handle'ы доступных
            // .md-агентов (из --add-dir) — модель должна знать, что их можно вызывать
            // через agentType в Task(agentType="handle", "prompt": "...") внутри workflow-скрипта.
            // ВАЖНО: добавляем ВСЕГДА. persona_ask — это одноразовый вопрос в чат, НЕ для Workflow.
            if (personaAgents is { AgentHandles.Count: > 0 })
            {
                var workflowHint =
                    "## Персоны-субагенты в Workflow\n" +
                    "У пользователя есть персоны-субагенты (файловые .md-агенты). " +
                    "Их можно вызывать в Workflow через Task(agentType=\"<handle>\", prompt=\"...\"). " +
                    "НЕ используй persona_ask (MCP-инструмент) для вызова внутри Workflow — " +
                    "persona_ask задаёт одноразовый вопрос в отдельный чат, а не запускает субагента. " +
                    "Для Workflow всегда используй Task(agentType=\"handle\").\n" +
                    "Доступные agentType: " + string.Join(", ", personaAgents.AgentHandles) + ".";
                Add("workflow-subagents", "Кого можно подключить к работе", workflowHint, group: "persona");
            }

            // Auto-recall долгой памяти персоны: релевантные записи по тексту хода.
            // Независим от заметок; провайдер сам гейтит по MemoryEnabled/флагу, ошибки не роняют ход.
            // Заодно собираем манифест (что подтянулось) для «использовано сейчас» (F3).
            if (_personaRecallProvider is not null && _memoryMcp is not null)
            {
                RecallBlock? memRecall = null;
                try { memRecall = await _personaRecallProvider(text); }
                catch { /* recall памяти не должен ронять ход */ }
                Add("recall-memory", "Что персона помнит по теме", memRecall?.Text, stable: false, group: "persona");
                if (memRecall?.Items.Count > 0)
                {
                    manifestItems ??= new List<RecallItem>();
                    manifestItems.AddRange(memRecall.Items);
                }
            }

            // Привязанные знания и правила персоны (флаг persona-bindings): индекс источников
            // «когда → откуда» + выжимки режима «всегда». Только у персонных сессий;
            // провайдер сам гейтит по флагу, ошибки не роняют ход.
            if (_bindingsProvider is not null && _personaPromptProvider is not null)
            {
                string? bindingsBlock = null;
                try { bindingsBlock = await _bindingsProvider(text); }
                catch { /* блок привязок не должен ронять ход */ }
                Add("persona-bindings", "Знания и правила, привязанные к персоне", bindingsBlock, stable: false, group: "persona");
            }

            // Slice top-10 god-nodes Code Graph: хабы по связности для холодного старта
            // понимания кода (граф иначе невидим Claude CLI). Текст хода игнорируется —
            // god-узлы структурны; провайдер кэширует slice по builtAt, ошибки → null.
            if (_codeGraphProvider is not null)
            {
                string? codeGraphBlock = null;
                try { codeGraphBlock = await _codeGraphProvider(text); }
                catch { /* блок графа не должен ронять ход */ }
                // Граф меняется при пересборке, а не под текст хода, но и стабильным
                // его не назовёшь: правки кода прилетают в промпт следующего же хода
                Add("code-graph", "Главные узлы кода проекта", codeGraphBlock, stable: false, group: "project");
            }

            // Персональный слой: промпт персоны имеет приоритет
            // над .md-агентом — чат ведётся от её лица, характер задаёт именно персона.
            string? agentPrompt = _personaPromptProvider?.Invoke();
            if (agentPrompt is null && !string.IsNullOrEmpty(Info.AgentName) && _skills is not null)
                agentPrompt = _skills.GetAgentSystemPrompt(_rootPath, Info.AgentName);
            personaLayerPrompt = agentPrompt;

            var combinedPrompt = TurnPromptAssembler.Combine(sections, agentPrompt);

            if (!string.IsNullOrWhiteSpace(combinedPrompt))
                args.AddRange(["--append-system-prompt", combinedPrompt]);

            // Слой персоны — тоже часть того, что ушло модели: кладём его секцией уже ПОСЛЕ
            // склейки (Combine принимает его отдельным аргументом, чтобы не спутать порядок).
            if (!string.IsNullOrWhiteSpace(agentPrompt))
                sections.Add(new PromptSectionDto("persona-layer", "Кто она: роль и характер",
                    agentPrompt, Group: "persona"));

            // Текст хода — не системный промпт, но модель видит именно его: сюда уже вклеены
            // обвязки OmO (SessionManager.BuildCliTurnText), разворот скилла и имена вложений,
            // а в ленте и истории лежит исходное сообщение человека. Kind = turn: в склейку
            // не идёт, в шторке — отдельным блоком.
            sections.Add(new PromptSectionDto("turn-text", "Ваше сообщение с добавками", text, "turn",
                Stable: false, Group: "turn"));

            // Манифест recall (F3): что персона подтянула из памяти в этот ход — клиенту,
            // для «опирается на…» / «использовано сейчас» во вкладке контекста персоны.
            if (manifestItems is { Count: > 0 })
                _ = _onMessage(new RecallManifestMessage(
                    manifestItems.Select(i => new RecallItemDto(i.Kind, i.Ref, i.Title, i.Snippet)).ToList()));
        }

        // Env-оверрайды процесса собираем заранее (не сразу в psi): пары входят в сигнатуру прогона
        var envOverrides = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            // claude --print по умолчанию ждёт фоновые задачи (субагентов workflow) не дольше 600с,
            // затем принудительно завершается: «Background tasks still running after 600s; terminating».
            // Из-за этого длинные workflow обрывались на 10-й минуте, не доходя до конца. 0 = ждать без
            // ограничения по времени; нас страхует watchdog (см. WatchdogFor).
            ["CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS"] = "0",

            // Даём MCP-серверам больше времени на подключение при старте хода: с дефолтным
            // таймаутом медленно стартующий node-сервер (personas и др.) не успевал
            // зарегистрировать тулы, и первый же вызов падал «No such tool available»
            // (модель ретраила, но карточка ошибки засоряла ленту).
            ["MCP_TIMEOUT"] = "30000",
        };

        // Сторонний провайдер (DeepSeek/GLM): перенаправляем CLI на его Anthropic-совместимый
        // эндпоинт. Env считаются каждый ход — модель сессии могла смениться между ходами.
        var cliEnv = _providers?.BuildCliEnv(EffectiveModel);
        if (cliEnv is not null)
        {
            foreach (var (k, v) in cliEnv)
                envOverrides[k] = v;
        }
        else if (_subscriptionPool?.HasExtra == true
            && _providers?.GetByKey(Info.Provider) is null)
        {
            // Подписка пула (включая "claude", если задана с токеном) — свой OAuth-профиль и
            // токен. Если ключ не найден в пуле (локальный режим — пул пуст, HasExtra=false, сюда
            // не входим) — оверрайдов нет, ход идёт по ~/.claude/.credentials.json (вход без ключа).
            var sub = _subscriptionPool.All.FirstOrDefault(s => s.Key == Info.Provider);
            if (sub?.Enabled == true)
            {
                var oauthEnv = _providers?.BuildOAuthCliEnv(sub.Key, sub.OAuthToken, sub.ApiKey, EffectiveModel);
                if (oauthEnv is not null)
                    foreach (var (k, v) in oauthEnv)
                        envOverrides[k] = v;
            }
        }

        // Сообщение хода: с картинками content — массив блоков (text + image base64), иначе строка
        var imageBlocks = BuildImageBlocks(imagePaths);
        object content;
        if (imageBlocks.Count == 0)
        {
            content = text;
        }
        else
        {
            var blocks = new List<object> { new { type = "text", text } };
            blocks.AddRange(imageBlocks);
            content = blocks;
        }
        var userMessageJson = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content }
        });

        var signature = BuildLaunchSignature(args, mcpServerKeys, envOverrides, personaLayerPrompt);

        // Same-process ход: прогон дожил с прошлого хода (фоновые агенты ещё работают),
        // окружение не изменилось — отдаём сообщение живому процессу в stdin, агенты
        // переживают смену хода. Собранный temp MCP-конфиг не пригодился — убираем.
        var existing = _run;
        if (existing is not null && existing.TurnDone && existing.Signature == signature
            && TrySubmitTurn(existing, userMessageJson, turnSeq))
        {
            Console.WriteLine("[ClaudeSession] Ход отдан живому процессу прогона (фоновые агенты доживают)");
            // Same-process submit НЕ гарантирован durable (процесс доживает, запись в .jsonl
            // может не дойти при последующей смерти) — поэтому DiedEmpty-ретрай новым процессом
            // не должен skip'ать submit. Флаг WasNewProcess=false именно это гарантирует.
            _lastSubmittedTurnText = text;
            _lastTurnResolved = false;
            _lastSubmitWasNewProcess = false;
            // Снимок ДО ожидания конца хода: событие обязано опередить result, иначе
            // TurnAccumulator сбросит текущий ход, и id уже некуда будет прицепить.
            // applied=false — промпт пересобран, но модели не ушёл: работает промпт старта.
            PublishPromptSnapshot(sections, args, mcpServerNames,
                applied: false, inheritedFromId: existing.PromptSnapshotId);
            await existing.TurnTcs.Task.WaitAsync(ct);
            // Прогон умер, не выдав ни одного события хода (TOCTOU: фоновые агенты кончились,
            // CLI завершается сразу после успешной записи в stdin) — гонка same-process, а не
            // ошибка доставки. Молча перезапускаем ход новым процессом на ТОЙ ЖЕ паре, не отдавая
            // смерть наружу (иначе фолбэк сочтёт её Unreachable и навсегда сменит провайдера).
            // Обрыв посреди хода (TurnGotEvent=true) сюда не попадает — там DiedEmpty=false.
            if (!existing.DiedEmpty)
            {
                // Штатное завершение same-process хода: temp MCP-конфиг ЭТОГО хода больше не
                // нужен (финализация прогона удалит только конфиг ПЕРВОГО хода — этот файл с
                // сервисным токеном больше никто не приберёт, важно хотя бы знать о неудаче
                // удаления). В DiedEmpty-ветке ниже файл НЕ трогаем: ретрай стартует новый
                // процесс с этими же args (--mcp-config указывает на него), удалит его
                // финализация нового прогона (run.TurnMcpPath). Удали файл до ретрая —
                // CLI умирает мгновенно с «Invalid MCP configuration: config file not found»,
                // ход гибнет молча (инцидент 16.08.2026).
                if (turnMcpPath != null)
                    try { File.Delete(turnMcpPath); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ClaudeSession] Не удалось удалить temp MCP-конфиг same-process хода {turnMcpPath}: {ex.Message}");
                    }
                return;
            }
            existing.DiedEmpty = false;
            // existing уже финализирован (процесс мёртв, _run обнулён, ресурсы прогона сняты):
            // блок убийства несовместимого прогона ниже пропускаем, проваливаемся к запуску
            // нового процесса. Ретрай единственный — у нового прогона RetryOnEmptyExit=false,
            // повторная пустая смерть уходит наружу как Unreachable (фолбэк работает штатно).
            existing = null;
        }

        // Живой, но несовместимый прогон (сменились модель/режим/персона/env — или запись
        // в stdin сорвалась): убиваем и дожидаемся финализации. Его фоновые агенты гибнут —
        // осознанная плата за смену окружения; CLI сообщит о них notification'ом на resume.
        // ExitedMessage прогона подавляем: статусом сессии владеет новый ход.
        if (existing is not null)
        {
            existing.SuppressExited = true;
            _launcher.Kill(existing.Process, existing.LaunchTurnId);
            if (existing.ReaderTask is { } prevReader)
                try { await prevReader.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { /* финализация зависла — не блокируем новый ход */ }
        }

        // Ход идёт новым процессом — этот промпт модель и увидит. Пишем снимок ДО старта:
        // id нужен прогону (CliRun.PromptSnapshotId), чтобы следующие same-process ходы
        // могли на него сослаться. Процесс может не стартовать — тогда снимок останется
        // с applied=true при неушедшем промпте, но ход тут же закончится ошибкой рядом.
        var turnSnapshotId = PublishPromptSnapshot(sections, args, mcpServerNames,
            applied: true, inheritedFromId: null);

        // claude.exe пишет/читает UTF-8. Без явной кодировки .NET берёт системную
        // OEM code page (напр. CP866 на русской Windows) → кракозябры в ответах.
        // Задаём UTF-8 без BOM (BOM сломал бы первое сообщение в stdin).
        var utf8NoBom = new System.Text.UTF8Encoding(false);

        // ArgumentList/Args экранирует каждый аргумент корректно (важно для многострочного
        // системного промпта); env-оверрайды собраны выше — они входят в сигнатуру прогона.
        // OTel: дочерний спан запуска процесса (родитель — активный chat.turn).
        Process process;
        using (var procActivity = TurnTelemetry.StartProcessSpan(
                   kind: TurnTelemetry.ExecutionKind(_launcher.IsSandboxed),
                   command: _launcher.ClaudeCliCommand,
                   sessionId: Info.ClaudeSessionId ?? Info.Id.ToString(),
                   mcpConfigHash: TurnTelemetry.McpConfigHash(effectiveMcpConfig)))
        {
            process = _launcher.Start(new Execution.ProcessSpec
            {
                FileName = _launcher.ClaudeCliCommand,
                Args = args,
                WorkingDirectory = _rootPath,
                Env = envOverrides,
                // Маршрут хода определяем только мы: системные ANTHROPIC_*/CLAUDE_CONFIG_DIR
                // машины в ход не пускаем, иначе чат «на Claude» молча уедет на чужой эндпоинт
                ClearEnv = _providers?.EnvKeysToClear ?? LlmProviderRegistry.ProviderEnvKeys,
                StdioEncoding = utf8NoBom,
                TurnId = _currentTurnId,
                // Событие Exited — единственный надёжный сигнал смерти процесса: закрытие stdout
                // может не наступить (дочерние node-процессы MCP наследуют и держат pipe). Без него
                // обрыв хода зависает в «ожидании» без диагностики (инцидент P27).
                EnableRaisingEvents = true,
            });
        }
        _currentProcess = process;

        CliRun run;
        try
        {
            if (process.HasExited)
                throw new InvalidOperationException("claude мгновенно завершился при старте");

            _fileWatcher.Start();

            run = new CliRun { Process = process, Signature = signature, TurnMcpPath = turnMcpPath, LaunchTurnId = _currentTurnId, PromptSuggestionsActive = promptSuggestionsActive, PromptSnapshotId = turnSnapshotId, LastTurnSeq = turnSeq };
            // Смерть процесса — по событию ОС (см. HandleProcessExitedAsync): EnableRaisingEvents=true
            // выставлен в spec. Подписка обязательна: закрытие stdout ненадёжно как сигнал гибели
            // (дочерние node-процессы MCP держат pipe — P27), и без Exited обрыв хода зависает
            // в «ожидании» без единого маркера в логе.
            process.Exited += (_, _) => _ = HandleProcessExitedAsync(run);
            // stderr CLI перехватываем построчно и немедленно (до P27 не перехватывался вовсе,
            // 0 строк в логе): при крахе это единственный источник причины, ждать финализации
            // нельзя — она может не стартовать часами. BeginErrorReadLine исключает ручное чтение
            // StandardError, поэтому StderrTask убран.
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Console.Error.WriteLine($"[ClaudeSession stderr] {e.Data.Trim()}");
            };
            process.BeginErrorReadLine();
            // Между Start и подпиской процесс мог умереть — обработаем сразу, иначе Exited уже
            // не сработает (событие стреляет только при переходе жив→мёртв, а мы его проспали).
            if (process.HasExited) _ = HandleProcessExitedAsync(run);

            // stdin оставляем открытым — claude пишет control_response в него при permission-запросах
            var skipResubmit = ShouldSkipResubmit(text, _lastSubmittedTurnText, _lastTurnResolved, _lastSubmitWasNewProcess);
            if (skipResubmit)
            {
                // Доп-проверка durability: submit прошлого процесса мог НЕ долететь до транскрипта
                // (процесс убит до чтения stdin — CLI пишет user-сообщение в .jsonl только при
                // чтении). Инцидент 16.08.2026: такой skip запускал CLI с --resume без submit,
                // тот отдавал пустой result (numTurns=0) — ход «завершался успехом» без ответа.
                // Текста в транскрипте нет → submit обязателен; есть → skip как раньше (без дубля).
                var lastUserText = ReadLastTranscriptUserText();
                if (!string.Equals(lastUserText, text, StringComparison.Ordinal))
                {
                    skipResubmit = false;
                    Console.Error.WriteLine(
                        "[ClaudeSession] Ре-аттемпт: прошлый submit не durable в транскрипте (текста нет) — " +
                        $"пойдём обычным submit (session {Info.Id})");
                }
            }
            await _stdinLock.WaitAsync(ct);
            try
            {
                if (skipResubmit)
                {
                    // Ре-аттемпт хода фолбэком: прошлый submit новым процессом уже durable в .jsonl,
                    // но ход не дошёл до result (процесс умер). Повторная запись в stdin создала бы
                    // второй user-turn = дубль. На --resume CLI сам доиграет висящий user-turn.
                    Console.Error.WriteLine($"[ClaudeSession] Ре-аттемпт хода без submit — доиграется через --resume (session {Info.Id})");
                }
                else
                {
                    await process.StandardInput.WriteLineAsync(userMessageJson);
                    await process.StandardInput.FlushAsync();
                }
            }
            finally { _stdinLock.Release(); }
            _lastSubmittedTurnText = text;
            _lastTurnResolved = false;
            _lastSubmitWasNewProcess = true;

            _run = run;
        }
        catch
        {
            // Прогон не собрался: reader не стартовал, финализировать некому — прибираем сами
            // и завершаем ход ExitedMessage, иначе статус сессии застрянет в Working
            _fileWatcher.Stop();
            _launcher.Kill(process, _currentTurnId);
            process.Dispose();
            _currentProcess = null;
            if (turnMcpPath != null)
                try { File.Delete(turnMcpPath); }
                catch { /* temp-каталог приберёт ОС */ }
            await _onMessage(new ExitedMessage(turnSeq));
            throw;
        }

        // Reader живёт дольше хода — до смерти процесса (доживание фоновых агентов);
        // финализация прогона (ватчеры, temp-конфиг, ExitedMessage) — на нём
        run.ReaderTask = Task.Run(() => ReadLoopAsync(run, _cts.Token), CancellationToken.None);

        await run.TurnTcs.Task.WaitAsync(ct);

        // Без живых фоновых задач сохраняем прежнюю семантику хода: возвращаемся, когда процесс
        // умер и финализирован (ExitedMessage послан до release _turnLock). С ними — сразу:
        // прогон доживает, чат остаётся Active, ExitedMessage пошлёт финализация.
        if (!run.HasPendingBg && run.ReaderTask is { } reader)
            await reader.WaitAsync(ct);
    }

    // Цикл чтения stdout прогона. Принадлежит прогону, не ходу: переживает result и
    // продолжает транслировать события доживающих фоновых агентов и ходов-продолжений CLI
    private async Task ReadLoopAsync(CliRun run, CancellationToken ct)
    {
        // Ридер stdout переиспользуется между итерациями: нельзя запустить второе чтение
        // того же потока, пока предыдущее не завершилось. При срабатывании watchdog чтение
        // остаётся висеть — убийство процесса закроет stdout и разблокирует его.
        Task<string?>? pendingRead = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var armedTurnDone = run.TurnDone;
                pendingRead ??= run.Process.StandardOutput.ReadLineAsync(ct).AsTask();

                // Watchdog через гонку «строка против таймера», а НЕ через отмену ReadLineAsync:
                // на Windows-пайпе токен НЕ прерывает уже начатое чтение молчащего stdout, поэтому
                // старый watchdogCts.CancelAfter не срабатывал и зависший процесс жил часами.
                string? line;
                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var timeout = WatchdogFor(run);
                    var completed = await Task.WhenAny(pendingRead, Task.Delay(timeout, delayCts.Token));
                    if (completed != pendingRead)
                    {
                        // Тишина дольше таймаута. Пока ждали, мог начаться same-process ход —
                        // тогда переармируем (чтение держим, оно ещё валидно)
                        if (armedTurnDone && !run.TurnDone) continue;
                        if (!run.TurnDone && !run.DeathDiagnosed)
                        {
                            // Активный ход молчит дольше допустимого (генерация оборвана,
                            // инструмент завис ИЛИ result хода проглочен корреляцией — тогда
                            // индикатор залипал до этого момента) — прерываем с ошибкой, спиннер снимется.
                            // Взводим DeathDiagnosed ДО Kill: Kill стрельнёт событием Exited →
                            // HandleProcessExitedAsync, и по флагу не пришлёт второй ErrorMessage/маркер.
                            // Гейт !DeathDiagnosed — обратная гонка: процесс УЖЕ умер, Exited опередил
                            // таймаут и HandleProcessExitedAsync выставил маркер + свою ошибку; без гейта
                            // здесь прилетело бы второе «Модель не отвечает» (3.2).
                            CorrTrace($"watchdog-timeout({timeout.TotalMinutes:0}min)", Info.Id, run);
                            run.DeathDiagnosed = true;
                            await _onMessage(new ErrorMessage(
                                $"Модель не отвечает более {timeout.TotalMinutes:0} мин — ход прерван"));
                        }
                        else if (run.HasPendingBg)
                            Console.Error.WriteLine(
                                $"[ClaudeSession] Фоновые агенты не завершились за {_bgLingerTimeout.TotalMinutes:0} мин тишины — завершаем процесс");
                        // Иначе: result отдан, фоновых задач нет — процесс держат плагинные
                        // хуки/мосты (наблюдалось с oh-my-claudecode), гасим молча
                        _launcher.Kill(run.Process, run.LaunchTurnId);
                        // Добираем висящее чтение: kill закрыл stdout → оно завершится (null/ошибка),
                        // без await остался бы unobserved-таск
                        try { await pendingRead; } catch { /* пайп закрыт убийством — ожидаемо */ }
                        break;
                    }
                    delayCts.Cancel(); // чтение выиграло — гасим таймер, иначе на активном стриме
                                       // копились бы тысячи висящих Task.Delay
                    line = await pendingRead;
                    pendingRead = null; // следующей итерации нужно новое чтение
                }

                if (line is null) break; // stdout закрыт — процесс завершился
                if (string.IsNullOrWhiteSpace(line)) continue;
                await ProcessLineAsync(run, line);
            }
        }
        catch (OperationCanceledException) { /* отмена сессии — штатно */ }
        catch (Exception ex)
        {
            // Полный стек — иначе не видно, ГДЕ упал разбор (напр. небезопасное чтение числа
            // из stream-json стороннего провайдера). Message в одиночку тут бесполезен.
            Console.Error.WriteLine($"[ClaudeSession] Цикл чтения прогона упал (session={Info.Id} cli={Info.ClaudeSessionId ?? "-"}): {ex}");
            // Активный ход из-за краха цикла не дождётся result — без явной ошибки клиенту
            // UI навсегда завис бы на «Размышление…» (ExitedMessage из finally не гасит плашку
            // размышления). Шлём ошибку, чтобы ход честно завершился. DeathDiagnosed — ДО сообщения:
            // finally убьёт процесс → Exited → HandleProcessExitedAsync, и по флагу не пришлёт дубль
            // ErrorMessage (текст краха цикла здесь точнее, чем «процесс завершился»).
            if (!run.TurnDone)
            {
                run.DeathDiagnosed = true;
                TurnTelemetry.RecordError(Info.Provider, "process_exit");
                try { await _onMessage(new ErrorMessage("Ход прерван из-за ошибки обработки ответа модели")); }
                catch { /* сообщение клиенту best-effort */ }
            }
        }
        finally { await FinalizeRunAsync(run); }
    }

    // Допустимая тишина stdout по состоянию прогона. Активный ход — щедрый IdleTimeout (60 мин):
    // при активном ходе молчание stdout почти всегда легитимно (CLI выполняет инструмент/субагента,
    // ждёт ответа пользователя, сжимает контекст, медленно генерирует или ретраит провайдера) —
    // короткий таймаут по любому из этих состояний ложно рубил бы ход, а надёжно отличить их от
    // обрыва по одному таймауту нельзя. Реальный обрыв провайдера отлавливается иначе: если CLI
    // сам завершится/упадёт — это увидит цикл чтения (result/EOF/исключение → ErrorMessage
    // клиенту), а IdleTimeout — лишь крайняя защита от вечно висящего процесса.
    // Доживание с фоновыми агентами — потолок BgLingerTimeout; иначе — короткий грейс выхода CLI.
    private TimeSpan WatchdogFor(CliRun run) =>
        ResolveWatchdog(run.TurnDone, run.ContinuationActive, run.HasPendingBg, run.StdinClosed,
            run.PromptSuggestionsActive, _bgLingerTimeout);

    // Чистое отображение состояния прогона → допустимой тишины stdout. Вынесено из WatchdogFor
    // ради прямого тестирования веток. Активный ход ИЛИ ход-продолжение CLI (ответ на
    // task_notification) — щедрый IdleTimeout: продолжение это полноценный агентный ход, и его
    // долгие инструменты (npx tsc, dotnet build) не должны гибнуть по короткому грейсу молчания.
    // result без фоновых задач, но stdin ещё открыт — окно старта продолжения (ContinuationStartGrace).
    internal static TimeSpan ResolveWatchdog(
        bool turnDone, bool continuationActive, bool hasPendingBg, bool stdinClosed,
        bool promptSuggestions, TimeSpan bgLinger) =>
        !turnDone || continuationActive ? IdleTimeout
        : hasPendingBg ? bgLinger
        : !stdinClosed ? ContinuationStartGrace
        : promptSuggestions ? PromptSuggestionExitGrace
        : ResultExitGrace;

    // Финализация прогона: единственная точка уборки после смерти процесса
    private async Task FinalizeRunAsync(CliRun run)
    {
        CloseStdin(run);
        // Всегда убиваем процесс. На Windows дочерние node-процессы MCP-серверов
        // НЕ завершаются автоматически при выходе родителя — без явного Kill с
        // entireProcessTree они остаются сиротами и копятся сутками, съедая память.
        // На POSIX Kill уже мёртвого процесса — no-op (ловим внутри метода).
        _launcher.Kill(run.Process, run.LaunchTurnId);
        if (!run.Process.HasExited)
        {
            // Ограниченное ожидание завершения — Kill() асинхронен на некоторых ОС
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await run.Process.WaitForExitAsync(exitCts.Token); }
            catch (OperationCanceledException) { } // 10 с истекло — идём дальше
        }
        // Смерть процесса была безмолвной — exit-code недоставало для диагностики причины
        // (штатный выход CLI по result vs крах). Сохраняем до Dispose: при активной смерти
        // хода ниже он попадёт в лог, чтобы различить «процесс сам вышел» и «упал по ошибке».
        // stderr к этому моменту уже в логе — он перехватывается построчно через
        // ErrorDataReceived (см. RunTurnAsync), а не копится здесь.
        int? exitCode = null;
        try { if (run.Process.HasExited) exitCode = run.Process.ExitCode; }
        catch (Exception) { /* ExitCode бросает, если процесс ещё не вышел или не задан код */ }
        run.Process.Dispose();
        if (ReferenceEquals(_currentProcess, run.Process)) _currentProcess = null;
        // Прогон всё ещё «текущий»? Если его уже заместил новый (несовместимый ход убил
        // старый, финализация опоздала) — общие пер-сессионные ресурсы (file watcher,
        // сабагент-ватчер, tailer) принадлежат новому ходу, их не трогаем
        var wasCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _run, null, run), run);

        if (run.TurnMcpPath != null)
            try { File.Delete(run.TurnMcpPath); }
            catch (Exception ex)
            {
                // В temp-конфиге сервисный токен — важно знать, если он не удалился
                Console.Error.WriteLine($"[ClaudeSession] Не удалось удалить temp MCP-конфиг {run.TurnMcpPath}: {ex.Message}");
            }

        // Процесс мёртв, но Workflow-агенты — независимые процессы и не обязаны погибнуть
        // вместе с ним (см. NoteOwnerProcessGone) — недобитые ватчеры ЭТОГО прогона получают
        // короткое окно доказать, что работа жива, вместо немедленного «прерван» (Interrupt
        // мог успеть раньше — повторный вызов idempotent). Чужие (нового прогона при
        // опоздавшей финализации замещённого) не трогаем — их workflow живы.
        List<WorkflowWatcher> lingeringWatchers;
        lock (_workflowWatchers)
            lingeringWatchers = _workflowWatchers
                .Where(w => !w.IsDisposed && (w.Owner is null || ReferenceEquals(w.Owner, run)))
                .ToList();
        foreach (var w in lingeringWatchers) w.NoteOwnerProcessGone();
        lock (_workflowWatchers) _workflowWatchers.RemoveAll(w => w.IsDisposed);

        if (wasCurrent)
        {
            _fileWatcher.Stop();
            // Дочитываем хвосты транскриптов сабагентов и останавливаем ватчеры прогона
            if (_subagentWatcher is not null)
            {
                await _subagentWatcher.DrainAsync();
                _subagentWatcher.Dispose();
                _subagentWatcher = null;
            }
            _transcriptTailer?.Dispose();
            _transcriptTailer = null;
        }

        // Фоновые задачи, не успевшие завершиться, умерли вместе с процессом —
        // закрываем их карточки, иначе UI ждал бы «ответ готовится» вечно
        List<string> orphanedTools;
        lock (run.PendingBg)
        {
            orphanedTools = run.PendingBg.Values.Where(v => !string.IsNullOrEmpty(v))
                .Concat(run.UnknownBgToolUses).Distinct().ToList();
            run.PendingBg.Clear();
            run.UnknownBgToolUses.Clear();
        }
        run.PendingBgUnknown = false;
        // drainSubagent: false — _subagentWatcher либо уже продренирован и обнулён выше
        // (wasCurrent), либо принадлежит НОВОМУ прогону, заместившему этот (!wasCurrent):
        // дренировать чужой поток здесь было бы порчей его состояния.
        if (orphanedTools.Count > 0)
            await CompleteBgTasksAsync(orphanedTools, aborted: true, drainSubagent: false);

        // Ход, ждущий result, его уже не дождётся — процесс умер. Активен ли был ход (ждуresult)
        // в момент смерти — фиксируем ДО смены TurnDone: по этому признаку отличаем смерть
        // same-process хода до первого события (гонка TOCTOU) от штатного выхода между ходами/после result.
        var activeTurnDied = !run.TurnDone;
        run.TurnDone = true;

        // Same-process ход умер, не получив ни одного события после submit: успешная запись в
        // stdin успела, но CLI уже завершался (фоновые агенты кончились) — внутренняя гонка
        // процесса, а не ошибка доставки провайдера. Наружу ExitedMessage не идёт, RunTurnAsync
        // перезапустит ход новым процессом на той же паре (DiedEmpty). Этим ОТЛИЧАЕТСЯ от обрыва
        // посреди хода (TurnGotEvent=true) — тот легитимный Unreachable уходит наружу как раньше.
        //
        // ПОРЯДОК КРИТИЧЕН: выставление DiedEmpty/SuppressExited — СТРОГО ДО TurnTcs.TrySetResult.
        // TrySetResult будит ждущий same-process путь (NewTcs = RunContinuationsAsynchronously),
        // и в продолжение по happens-before видно ТОЛЬКО то, что сделано до TrySetResult. Поставь
        // флаги после — ждущий прочтёт DiedEmpty=false (хода как будто нет), а SuppressExited уже
        // подавил ExitedMessage → ни result, ни exited, ни ретрая: сессия навсегда залипает в
        // Working (тот же класс бага, что и реанимация зависших чатов). Решение — чистая ф-я (тест).

        // P30: прогон в этом окне ждёт control_response (permission / AskUserQuestion / план).
        // Даже same-process-ретрай тут ошибочен — ответа нет и не будет, карточка в ленте зависнет
        // до таймаута. Передаём гард в HandleProcessExitedAsync: он шлёт ErrorMessage и снимает
        // ожидание, оставляя нас с честным обрывом вместо ложного ретрая.
        // P31: читаем фиксацию на прогоне (ResolvePendingControlAtDeath), а не живые словари —
        // при порядке «Exited первым» HandleProcessExitedAsync уже очистил их, и без фиксации
        // мы бы увидели false и молча ретраили ход (дубль ошибки).
        var pendingControlResponse = ResolvePendingControlAtDeath(run);
        if (ShouldRetryEmptyExit(activeTurnDied, run.RetryOnEmptyExit, run.TurnGotEvent, run.ReuseSubmit)
            && !pendingControlResponse)
        {
            run.DiedEmpty = true;
            run.SuppressExited = true;
            // Диагностика причины смерти (раньше была безмолвной): что убило процесс — штатный
            // выход по result (exit=0), крах CLI (ненулевой код) или обречённость reuse-окна.
            // reuse=true — гонка same-process (фильтр SkipResults неприменим), это ожидаемо.
            Console.WriteLine(
                $"[ClaudeSession] Same-process ход умер без result — перезапуск новым процессом на той же паре " +
                $"(session={Info.Id} cli={Info.ClaudeSessionId ?? "-"} reuse={run.ReuseSubmit} gotEvent={run.TurnGotEvent} exit={exitCode?.ToString() ?? "?"})");
        }
        else if (activeTurnDied)
        {
            // Активный ход умер, но DiedEmpty-ретрай не сработал (обрыв посреди хода с реальной
            // выдачей — TurnGotEvent=true, либо новый процесс — сбой старта): смерть уйдёт наружу
            // как ProcessGone/Unreachable. Фиксируем причину для разбора ложных подмен — но только
            // если это не сделал HandleProcessExitedAsync (он реагирует на событие Exited и обычно
            // опережает финализацию; fallback здесь — для гонки, когда EOF пришёл раньше callback'а).
            if (!run.DeathDiagnosed)
            {
                run.DeathDiagnosed = true;
                Console.Error.WriteLine(
                    $"[ClaudeSession] Прогон умер при активном ходе (session={Info.Id} cli={Info.ClaudeSessionId ?? "-"} gotEvent={run.TurnGotEvent} reuse={run.ReuseSubmit} retryOnEmpty={run.RetryOnEmptyExit} exit={exitCode?.ToString() ?? "?"})");
                // EOF-путь (stdout закрылся раньше события ОС Exited) обязан сам донести ошибку
                // до клиента: после DeathDiagnosed HandleProcessExitedAsync молча выйдет, и без
                // ErrorMessage здесь ход закончился бы «никак» — тишина в ленте при живом статусе
                // Working (инцидент 15.08.2026). Тот же текст и гейты, что там: ход убит
                // пользователем — не дублируем его маркер остановки.
                if (!_interruptedByUser)
                {
                    // P30: pending control_response завис бы карточкой до 60-минутного таймаута
                    if (pendingControlResponse) CancelPendingControlResponses();
                    var message = pendingControlResponse
                        ? "Процесс модели завершился во время ожидания разрешения — ход прерван"
                        : "Процесс модели завершился во время хода — ответ не был получен";
                    try { await _onMessage(new ErrorMessage(message)); }
                    catch (Exception ex) { Console.Error.WriteLine($"[ClaudeSession] ErrorMessage о смерти хода не отправлен: {ex.Message}"); }
                }
            }
            // P31: при порядке «EOF первым» (AskUserQuestion/ExitPlanMode — ридер не блокируется
            // permission-waiter'ом) этот путь отрабатывает раньше HandleProcessExitedAsync, который
            // затем выходит по DeathDiagnosed, не доходя до CancelPendingControlResponses. Чистим
            // pending здесь же — иначе _pendingQuestions/_pendingPlans (без таймаута, в отличие от
            // permission-waiter'ов) залипали бы в сессии навсегда. При порядке «Exited первым»
            // словари уже пусты — повторная очистка no-op.
            if (pendingControlResponse) CancelPendingControlResponses();
        }

        // Резолвим TurnTcs ПОСЛЕ выставления флагов ретрая: будим ждущий same-process путь,
        // к этому моменту DiedEmpty/SuppressExited уже стоят и гарантированно видны продолжению.
        run.TurnTcs.TrySetResult();

        // Статусом владеет SessionManager: Finished/Active он выставит по ExitedMessage.
        // Пустая смерть same-process хода наружу не идёт (SuppressExited выше) — её поглотит ретрай.
        if (!run.SuppressExited)
            await _onMessage(new ExitedMessage(Interlocked.Read(ref run.LastTurnSeq)));
    }

    // Смерть процесса CLI по событию ОС (Exited). До фикса P27 смерть детектировалась только по
    // закрытию stdout, но дочерние node-процессы MCP-серверов наследуют хэндл pipe и могут держать
    // его часами: ReadLineAsync не возвращал EOF, FinalizeRunAsync не стартовала — маркера гибели
    // в логе не было, ErrorMessage клиенту не шёл, и чат зависал в «Claude ждёт ответа» (ожидание)
    // до watchdog-таймаута (десятки минут). Exited срабатывает по факту ОС — независимо от pipe.
    //
    // Что делает: залогирует маркер с причиной (exit-code + контекст), Kill'ом дерева закрывает
    // унаследованные хэндлы (это разблокирует ReadLineAsync → FinalizeRunAsync выставит флаги
    // ретрая/ExitedMessage штатно) и при обрыве активного хода шлёт ErrorMessage (UI видит ошибку,
    // не вечное ожидание). Гонка same-process без события (DiedEmpty-ретрай) наружу не вылазит —
    // её поглотит перезапуск новым процессом, ErrorMessage не нужен.
    private async Task HandleProcessExitedAsync(CliRun run)
    {
        // Прогон уже заместительён новым (несовместимый ход убил старый — SuppressExited):
        // смерть Expected, её не эскалируем.
        if (!ReferenceEquals(_run, run)) return;
        // Только один путь (Exited / EOF-финализация / watchdog) отмечает гибель и шлёт ошибку.
        if (run.DeathDiagnosed) return;
        run.DeathDiagnosed = true;

        int? exitCode = null;
        try { if (run.Process.HasExited) exitCode = run.Process.ExitCode; }
        catch (Exception) { /* процесс уже Dispose'нут в гонке с финализацией — код недоступен */ }

        var activeTurn = !run.TurnDone;
        // P30: прогон ждёт control_response — shouldRetryEmptyExit даст true (gotEvent=true,
        // reuse=true), но ретрай тут нелеп: control_response не пошлётся, карточка в ленте зависнет.
        // Форсируем willRetry=false и шлём понятную ошибку ниже.
        // P31: фиксируем pending на прогоне (ResolvePendingControlAtDeath) — тогда FinalizeRunAsync,
        // вызванный после CancelPendingControlResponses ниже, прочитает true и не ретраил ход (дубль).
        var pendingControlResponse = ResolvePendingControlAtDeath(run);
        var willRetry = activeTurn
            && !pendingControlResponse
            && ShouldRetryEmptyExit(activeTurn, run.RetryOnEmptyExit, run.TurnGotEvent, run.ReuseSubmit);

        Console.Error.WriteLine(
            $"[ClaudeSession] Процесс CLI завершился (session={Info.Id} cli={Info.ClaudeSessionId ?? "-"} exit={exitCode?.ToString() ?? "?"} " +
            $"activeTurn={activeTurn} gotEvent={run.TurnGotEvent} reuse={run.ReuseSubmit} " +
            $"retryOnEmpty={run.RetryOnEmptyExit} retry={willRetry} " +
            $"pendingControlResponse={pendingControlResponse} interruptedByUser={_interruptedByUser} " +
            $"turnSeq={Interlocked.Read(ref run.LastTurnSeq)})");

        if (!activeTurn) return;

        // Обрыв активного хода без ретрая: клиент обязан видеть ошибку, иначе гибель процесса
        // выглядит как штатное ожидание ввода (P27). Ретраемая same-process смерть сюда не идёт.
        // Шлём ДО Kill дерева и с await: Kill закрывает дочерние stdout-pipe → разблокируется ридер
        // → FinalizeRunAsync пошлёт ExitedMessage. Без await (или после Kill) гонка ставила бы ошибку
        // в ленту ПОСЛЕ терминала хода (3.4). Шаблон await _onMessage(ErrorMessage) — как в watchdog.
        // Исключение — ход убит пользователем (кнопка «Стоп» / прерывание ради очереди): смерть
        // ожидаемая, фронт уже поставил маркер «Ход остановлен пользователем», и красная плашка
        // рядом с ним лжёт. Pending control при этом чистит сам Interrupt.
        if (!willRetry && !_interruptedByUser)
        {
            var message = pendingControlResponse
                ? "Процесс модели завершился во время ожидания разрешения — ход прерван"
                : "Процесс модели завершился во время хода — ответ не был получен";
            // Отменяем ожидающие control_response: иначе DecidePermissionAsync держит граф до
            // 60-минутного таймаута, карточка в ленте «Claude ждёт ответа» зависает навсегда (P30).
            if (pendingControlResponse) CancelPendingControlResponses();
            try { await _onMessage(new ErrorMessage(message)); }
            catch (Exception ex) { Console.Error.WriteLine($"[ClaudeSession] ErrorMessage о смерти хода не отправлен: {ex.Message}"); }
        }

        // Kill дерева: даже мёртвый родитель может оставить потомков-node, держащих stdout-pipe;
        // без этого висящее ReadLineAsync не вернётся и финализация не стартует.
        try { _launcher.Kill(run.Process, run.LaunchTurnId); }
        catch (Exception ex) { Console.Error.WriteLine($"[ClaudeSession] Kill прогона при смерти не удался: {ex.Message}"); }
    }

    /// <summary>
    /// Записать снимок промпта хода и сообщить клиенту его id (кнопка «какой промпт ушёл»).
    /// applied=false — ход доигрывается в живом процессе: собранный сейчас промпт модели НЕ
    /// уходил, она работает с промптом старта прогона (inheritedFromId).
    /// Возвращает id снимка либо null (снимки не ведутся / запись не удалась).
    /// </summary>
    private string? PublishPromptSnapshot(IReadOnlyList<PromptSectionDto> sections,
        IReadOnlyList<string> args, IReadOnlyList<string> mcpServerNames,
        bool applied, string? inheritedFromId)
    {
        if (_promptSnapshotSink is null) return null;

        string? id = null;
        try
        {
            id = _promptSnapshotSink(new PromptSnapshotDraft(
                applied, inheritedFromId, sections, MaskArgs(args), mcpServerNames,
                EffectiveModel, Info.Mode.ToWireToken(), BuildCliLayerFiles()));
        }
        catch (Exception ex)
        {
            // Снимок диагностический — его сбой не имеет права ронять ход
            Console.Error.WriteLine($"[ClaudeSession] Снимок промпта не записан: {ex.Message}");
        }

        _currentSnapshotId = id;
        if (id is not null)
            _ = _onMessage(new PromptSnapshotMessage(id, applied, inheritedFromId));
        return id;
    }

    /// <summary>
    /// Файловая часть слоя CLI: оба CLAUDE.md с раскрытыми импортами, каталог скиллов и вес
    /// истории, которую подтянет --resume. Всё резолвится по окружению ЭТОГО хода (рабочая
    /// папка чата, профиль CLI), а не по «вообще машине» — иначе показали бы то, чего CLI
    /// не видел. Состав инструментов сюда не входит: он приходит позже, из system/init.
    /// </summary>
    private CliLayerDto BuildCliLayerFiles()
    {
        var files = new List<PromptSectionDto>();
        AddClaudeMd(files, Path.Combine(_rootPath, "CLAUDE.md"), "CLAUDE.md проекта");
        AddClaudeMd(files, Path.Combine(_rootPath, ".claude", "CLAUDE.md"), "CLAUDE.md проекта (.claude)");
        if (_cliConfigRoot is { Length: > 0 } configRoot)
            AddClaudeMd(files, Path.Combine(configRoot, "CLAUDE.md"), "Ваш общий CLAUDE.md (для всех проектов)");

        var skills = new List<CliSkillDto>();
        if (_skills is not null)
        {
            try
            {
                if (_cliConfigRoot is { Length: > 0 } root)
                    skills.AddRange(_skills.GetSkillsInConfigRoot(root)
                        .Select(s => new CliSkillDto(s.Name, s.Description, "profile")));
                skills.AddRange(_skills.GetProjectSkills(_rootPath)
                    .Select(s => new CliSkillDto(s.Name, s.Description, "project")));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] Каталог скиллов для снимка не прочитан: {ex.Message}");
            }
        }

        var (bytes, messages) = TranscriptStats();
        return new CliLayerDto(Files: files, Skills: skills,
            TranscriptBytes: bytes, TranscriptMessages: messages);
    }

    private static void AddClaudeMd(List<PromptSectionDto> files, string path, string title)
    {
        if (ClaudeMdExpander.Read(path) is { } text)
            files.Add(new PromptSectionDto(path, title, text, "cli-file"));
    }

    // Вес истории разговора, которую CLI подтягивает по --resume. Содержимое транскрипта
    // в снимок не тащим (это вся переписка) — только размер и число сообщений.
    private (long? Bytes, int? Messages) TranscriptStats()
    {
        if (_cliConfigRoot is not { Length: > 0 } root || Info.ClaudeSessionId is not { } csid)
            return (null, null);
        try
        {
            var path = TranscriptMigrator.FindTranscript(root, _rootPath, csid);
            if (path is null) return (null, null);
            return (new FileInfo(path).Length, File.ReadLines(path).Count());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    // Аргументы запуска для показа человеку. Системный промпт не дублируем (он и есть
    // секции), а путь temp-конфига MCP заменяем именем файла: внутри сервисный JWT владельца.
    private static IReadOnlyList<string> MaskArgs(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            result.Add(args[i]);
            if (args[i] == "--append-system-prompt" && i + 1 < args.Count)
            {
                result.Add("<секции промпта выше>");
                i++;
            }
            else if (args[i] == "--mcp-config" && i + 1 < args.Count)
            {
                result.Add(Path.GetFileName(args[i + 1]));
                i++;
            }
        }
        return result;
    }

    // Сигнатура окружения прогона — жёсткая часть запуска процесса. Совпала у следующего
    // хода → его можно отдать живому процессу в stdin; отличие → новый процесс.
    // Исключены изменчивые на каждый ход части: --resume (не меняется в рамках сессии),
    // путь temp MCP-конфига (вместо него набор ключей серверов + отпечаток СОСТАВА их
    // инструментов — write/mentions/секции; токены/URL из содержимого намеренно опущены как
    // изменчивые) и системный промпт целиком — из него в сигнатуре только слой персоны,
    // recall/подсказки деградируют мягко.
    // Решение о ретрае same-process хода при пустой смерти процесса (гонка TOCTOU). Чистая
    // функция: FinalizeRunAsync применяет её исход, тест вызывает напрямую. activeTurnDied —
    // ход был активен (ждуresult) в момент смерти (не между ходами и не после result);
    // retryOnEmptyExit — ход отправлен через same-process submit (TrySubmitTurn, только он
    // уязвим к гонке — новый процесс, умерший пустым, это реальный сбой старта); turnGotEvent —
    // пришло хотя бы одно событие хода после submit (обрыв посреди хода — НЕ ретрай);
    // reuseSubmit — same-process submit ушёл в доживающий прогон без continuation и фоновых
    // задач (см. CliRun.ReuseSubmit): процесс обречён, его хвостовые события ненадёжны и ложно
    // взводят turnGotEvent, поэтому в этом окне смерть без result трактуем как TOCTOU-гонку
    // (ретрай) даже при turnGotEvent=true (инцидент 2026-08-10 П2 — ложный Unreachable).
    internal static bool ShouldRetryEmptyExit(bool activeTurnDied, bool retryOnEmptyExit,
        bool turnGotEvent, bool reuseSubmit)
        => activeTurnDied && retryOnEmptyExit && (!turnGotEvent || reuseSubmit);

    // Ре-аттемпт хода фолбэком пропускает повторный submit текста. Условие: прошлый submit был
    // тем же текстом, через НОВЫЙ процесс (durable в .jsonl — CLI пишет синхронно с приёмом) И
    // ход не завершён result'ом (процесс умер без result — висящий user-turn доиграется на
    // --resume). WasNewProcess отсекает same-process submit (TrySubmitTurn) и его DiedEmpty-ретрай:
    // их запись в .jsonl не гарантирована, skip привёл бы к зависанию. Чистая функция для теста.
    internal static bool ShouldSkipResubmit(string text, string? lastSubmittedText,
        bool lastTurnResolved, bool lastSubmitWasNewProcess)
        => !lastTurnResolved && lastSubmitWasNewProcess
           && lastSubmittedText is not null && text == lastSubmittedText;

    // Последний user-текст главного транскрипта этой сессии (null — файла нет/ошибка/вложение)
    private string? ReadLastTranscriptUserText()
        => TranscriptProbe.LastUserText(
            Info.ClaudeSessionId is { } csid
                ? TranscriptProbe.FindMainTranscript(_rootPath, csid)
                : null);

    // Пустой result CLI: «success» без единого хода модели и без токенов — служебный маркер
    // «модель не вызывалась» (микро-ход task-notification на --resume; запуск без submit при
    // ре-аттемпте фолбэком), а не ответ пользовательскому ходу. Любой настоящий ответ модели
    // даёт numTurns>=1; отказы в правах и api-ошибки приходят с непустым subtype/статусом/отказами
    // и пустым не считаются. Чистая функция для теста (инцидент 16.08.2026).
    internal static bool IsEmptyNoopResult(int numTurns, string subtype, string? apiErrorStatus,
        IReadOnlyList<string>? permissionDenials, UsageInfo? usage)
        => numTurns == 0 && subtype == "success" && apiErrorStatus is null
           && (permissionDenials is null || permissionDenials.Count == 0)
           && (usage is null || (usage.InputTokens == 0 && usage.OutputTokens == 0
                                  && usage.CacheReadTokens == 0 && usage.CacheCreationTokens == 0));

    private static string BuildLaunchSignature(
        IReadOnlyList<string> args, string mcpServerKeys,
        IReadOnlyDictionary<string, string> envOverrides, string? personaLayerPrompt)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--resume" or "--mcp-config" or "--append-system-prompt") { i++; continue; }
            sb.Append(args[i]).Append('\u0001');
        }
        sb.Append("mcp=").Append(mcpServerKeys).Append('\u0001');
        foreach (var (k, v) in envOverrides) sb.Append(k).Append('=').Append(v).Append('\u0001');
        if (!string.IsNullOrEmpty(personaLayerPrompt))
            sb.Append("persona=").Append(personaLayerPrompt);
        return sb.ToString();
    }

    // Трассировка корреляции result↔ход (диагностика залипающего индикатора «дымящийся домик»):
    // по этим строкам на реальном залипшем кейсе восстанавливается ФАКТИЧЕСКИЙ порядок событий
    // CLI (сливает ли он ход-продолжение с пользовательским ходом или всегда выдаёт отдельный
    // result) — без этого нельзя отличить отказной сценарий проглатывания от штатного пропуска.
    private static void CorrTrace(string ev, string sid, CliRun? run, JsonElement? root = null)
    {
        var nt = root is { } r && r.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number
            ? n.GetInt32() : -1;
        Console.WriteLine(
            $"[ClaudeSession][corr] {ev} sid={sid} turnDone={run?.TurnDone} "
            + $"skip={(run is null ? -1 : Volatile.Read(ref run.SkipResults))} "
            + $"cont={run?.ContinuationActive} bg={run?.HasPendingBg} numTurns={nt}");
    }

    // run — прогон-владелец read-loop'а, из которого пришла строка. Корреляцию ведём по нему,
    // а НЕ по полю _run: после механики доживания _run мог быть заменён новым прогоном, пока
    // старый reader дочитывает хвост — тогда поздние строки замещённого прогона (в т.ч. его
    // result) попадали бы в чужой CliRun.
    private async Task ProcessLineAsync(CliRun run, string line)
    {
        // Невалидный JSON от CLI не должен убивать весь turn
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp)) return;

        // Ход активен и получил событие от прогона — процесс жив и обрабатывает ход. Без
        // этого флага смерть same-process прогона до ЛЮБОГО события неотличима от гонки TOCTOU
        // (запись в stdin прошла, но CLI уже завершается), и ретрай работал бы даже на обрыв
        // посреди хода. Сбрасывается в TrySubmitTurn — поэтому между ходами не срабатывает.
        //
        // НО событие должно принадлежать САМОМУ ходу. Пока SkipResults>0, поток выдаёт хвост
        // ход-продолжения CLI — его ответ на task-notification, начатый ДО нашего submit'а
        // (TrySubmitTurn при ContinuationActive делает SkipResults++). Эти строки чужие нашему
        // ходу: они доказывают, что процесс жив, но НЕ что он взялся за наше сообщение в stdin.
        // Их учёт взводил TurnGotEvent ложно — продолжение заканчивалось, процесс умирал, не
        // тронув нашу очередь, и смерть уходила наружу как легитимный Unreachable вместо тихого
        // ретрая той же парой (инцидент 2026-08-10). result продолжения снимает SkipResults→0,
        // после чего события нашего хода взводят флаг как обычно.
        if (!run.TurnDone && Volatile.Read(ref run.SkipResults) == 0) run.TurnGotEvent = true;

        // Фактическая модель хода. Одной строкой на все виды событий: CLI называет её и в
        // system/init, и в message.model каждого ответа — разбор в TurnTelemetry.ModelFromEvent.
        // До этого в телеметрию шло намерение (EffectiveModel), а при пустом слоте — unknown.
        if (TurnTelemetry.ModelFromEvent(root) is { Length: > 0 } cliModel && cliModel != _turnCliModel)
        {
            _turnCliModel = cliModel;
            _turnActivity?.SetTag("model", cliModel);
        }

        switch (typeProp.GetString())
        {
            case "system":
                var sysSubtype = root.TryGetProperty("subtype", out var sst) ? sst.GetString() : null;
                if (sysSubtype == "init" && root.TryGetProperty("session_id", out var sid))
                {
                    var isResume = Info.ClaudeSessionId is not null;
                    Info.ClaudeSessionId = sid.GetString();
                    var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    var cwd = root.TryGetProperty("cwd", out var cw) && cw.ValueKind == JsonValueKind.String ? cw.GetString() : null;
                    var hasTools = root.TryGetProperty("tools", out var tl) && tl.ValueKind == JsonValueKind.Array;
                    var toolCount = hasTools ? tl.GetArrayLength() : 0;
                    // Имена инструментов — единственная часть «невидимого» слоя CLI, которую он
                    // сам про себя рассказывает: складываем их в снимок промпта хода
                    var toolNames = hasTools
                        ? tl.EnumerateArray().Select(t => t.GetString() ?? "").Where(n => n.Length > 0).ToList()
                        : [];
                    List<McpServerInfo>? mcp = null;
                    if (root.TryGetProperty("mcp_servers", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        mcp = [];
                        foreach (var s in ms.EnumerateArray())
                        {
                            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var status = s.TryGetProperty("status", out var st2) ? st2.GetString() ?? "" : "";
                            if (name.Length > 0) mcp.Add(new McpServerInfo(name, status));
                        }
                    }
                    // Дописываем состав инструментов в снимок текущего хода. init повторяется
                    // и на same-process ходах, и на ходах-продолжениях CLI — перезапись тем же
                    // составом безвредна. Снимок мог уехать по ретеншну: стор молча выйдет.
                    if (_currentSnapshotId is { } snapshotId && _promptSnapshotToolsSink is not null)
                    {
                        try { _promptSnapshotToolsSink(snapshotId, toolNames, mcp ?? []); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[ClaudeSession] Состав инструментов в снимок не дописан: {ex.Message}");
                        }
                    }

                    var worktree = ResolveTurnWorktree(cwd, _rootPath, _launcher);
                    await _onMessage(new SessionStartedMessage(
                        Info.ClaudeSessionId!, isResume, model, Info.Mode.ToWireToken(), cwd, toolCount, mcp,
                        Capabilities.Provider, Capabilities, worktree));

                    // Поток inline-сабагентов этого хода — из их транскриптов на диске.
                    // Same-process ход (init повторяется в том же процессе, контекст тот же) —
                    // ватчер переиспользуем: пересоздание помечало бы файлы «прочитанными
                    // целиком» и теряло хвост текста доживающих агентов. Иной контекст —
                    // дочитываем хвост (Drain) и только потом пересоздаём.
                    if (_subagentWatcher is null || !_subagentWatcher.Matches(cwd ?? _rootPath, Info.ClaudeSessionId!))
                    {
                        if (_subagentWatcher is not null)
                        {
                            await _subagentWatcher.DrainAsync();
                            _subagentWatcher.Dispose();
                        }
                        _subagentWatcher = new SubagentStreamWatcher(cwd ?? _rootPath, Info.ClaudeSessionId!, _onMessage,
                            Info.Id, _subagentRunSink);
                        _subagentWatcher.Start();
                    }

                    // Ридер notification'ов — один на прогон (init повторяется на каждом ходе
                    // нового CLI; пересоздание сбросило бы офсет и пропустило завершения)
                    if (_transcriptTailer is null)
                    {
                        _transcriptTailer = new MainTranscriptTailer(
                            cwd ?? _rootPath, Info.ClaudeSessionId!, HandleTaskNotification);
                        _transcriptTailer.Start();
                    }
                }
                else if (sysSubtype == "compact_boundary")
                {
                    // Claude свернул контекст — показываем разделитель
                    var meta = root.TryGetProperty("compact_metadata", out var cm) ? cm : default;
                    var trigger = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("trigger", out var tr)
                        ? tr.GetString() ?? "auto" : "auto";
                    int? preTokens = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("pre_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
                    int? postTokens = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("post_tokens", out var pst) && pst.TryGetInt32(out var pstv) ? pstv : null;
                    await _onMessage(new CompactBoundaryMessage(trigger, preTokens, postTokens));
                    // После компакции оценки контекста НЕТ. post_tokens — размер свёрнутой ИСТОРИИ,
                    // а не контекст следующего хода: в него не входят системный промпт, определения
                    // инструментов и CLAUDE.md, которые в окно возвращаются. Замер на живом чате дал
                    // post=3.7k при 52k реального контекста следующего хода (frontend/src/lib/context.ts).
                    // Класть post_tokens в оценку — отравлять реестр ёмкости: ход сразу после компакции
                    // часто падает с overflow у потолка (summary + большой tool_result), и тогда
                    // RecordOverflow запомнил бы МИНИМУМ 3.7k на час, выпав модель из цепочек по всему
                    // процессу. Обнуляем безусловно: 0 = «оценки нет» — WouldFit уходит в fail-open,
                    // RecordOverflow отсекается guard'ом contextTokens<=0, ContextTokens в result
                    // остаётся пустым (фронтовый fresh: «оценка появится после следующего хода»), а
                    // реальное значение вернёт TrackContextTokens на первом же assistant-сообщении.
                    _lastContextTokens = 0;
                }
                else if (sysSubtype == "status")
                {
                    // Ход компакции: status=="compacting" — началась; compact_result — завершилась
                    var status = root.TryGetProperty("status", out var stv) && stv.ValueKind == JsonValueKind.String
                        ? stv.GetString() : null;
                    var compactResult = root.TryGetProperty("compact_result", out var crv) && crv.ValueKind == JsonValueKind.String
                        ? crv.GetString() : null;
                    var compactError = root.TryGetProperty("compact_error", out var cev) && cev.ValueKind == JsonValueKind.String
                        ? cev.GetString() : null;
                    if (status == "compacting" || compactResult is not null)
                        await _onMessage(new CompactStatusMessage(status, compactResult, compactError));
                }
                // Структурные события жизненного цикла фоновых агентов (CLI 2.1.220+) —
                // ПЕРВИЧНЫЙ источник учёта наравне с текстовыми путями (TrackBgLaunch,
                // <task-notification>, TaskOutput): несут готовую пару task_id↔tool_use_id
                // без регекс-разбора текста. Текстовые пути не удаляем — фолбэк для старых CLI.
                else if (sysSubtype == "task_started") HandleTaskStarted(run, root);
                else if (sysSubtype == "task_notification") HandleStructuredTaskNotification(run, root);
                else if (sysSubtype == "background_tasks_changed") HandleBackgroundTasksChanged(run, root);
                break;

            case "stream_event":
                // Контент ОСНОВНОГО агента после конца хода — CLI начал ход-продолжение
                // (ответ на task-notification); его result не должен завершить будущий ход
                // (см. case "result"). Сообщения сабагентов (parent_tool_use_id) — это стрим
                // доживающих фоновых агентов, а не продолжение.
                if (run is { TurnDone: true, ContinuationActive: false } && !HasParentToolUseId(root))
                {
                    CorrTrace("continuation-start(stream_event)", Info.Id, run, root);
                    run.ContinuationActive = true;
                }
                await HandleStreamEventAsync(root);
                break;

            case "assistant":
                if (run is { TurnDone: true, ContinuationActive: false } && !HasParentToolUseId(root))
                {
                    CorrTrace("continuation-start(assistant)", Info.Id, run, root);
                    run.ContinuationActive = true;
                }
                TrackContextTokens(root);
                await HandleAssistantToolsAsync(run, root);
                break;

            case "result":
                // Результаты субагентов имеют parent_tool_use_id — не завершаем сессию по ним
                if (root.TryGetProperty("parent_tool_use_id", out var rPid) && rPid.ValueKind == JsonValueKind.String)
                    break;
                // Корреляция result ↔ ход: между ходами CLI ведёт собственные ходы-продолжения
                // (ответы на task-notification) со своими result'ами — их нельзя засчитывать
                // пользовательскому ходу. TurnDone=true → продолжение между ходами (ход никто
                // не ждёт); SkipResults>0 → продолжение шло в момент отправки текущего хода,
                // его result приходит первым (stdout последовательный) — пропускаем, result
                // самого хода будет следующим.
                {
                    var contRun = run;
                    if (contRun.TurnDone)
                    {
                        CorrTrace("result-skip(turnDone)", Info.Id, contRun, root);
                        contRun.ContinuationActive = false;
                        Console.WriteLine("[ClaudeSession] Result хода-продолжения CLI между ходами — пропущен");
                        CloseStdinIfIdle(contRun);
                        break;
                    }
                    if (Volatile.Read(ref contRun.SkipResults) > 0)
                    {
                        CorrTrace("result-skip(skipResults)", Info.Id, contRun, root);
                        Interlocked.Decrement(ref contRun.SkipResults);
                        Console.WriteLine("[ClaudeSession] Result хода-продолжения CLI при ожидающем ходе — пропущен");
                        break;
                    }
                    CorrTrace("result-emit", Info.Id, contRun, root);
                }
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "success" : "success";
                // Числа читаем через безопасные хелперы: openrouter-совместимый поток шлёт
                // эти поля как JSON null (Anthropic — всегда число), а прямой GetInt64/GetDouble
                // на null кидает и роняет весь цикл чтения прогона (ход виснет без ответа).
                var durationMs = LongProp(root, "duration_ms");
                var numTurns = IntProp(root, "num_turns");
                var totalCost = DoubleProp(root, "total_cost_usd");
                var apiErr = StatusProp(root, "api_error_status");
                List<string>? denials = null;
                if (root.TryGetProperty("permission_denials", out var pd) && pd.ValueKind == JsonValueKind.Array && pd.GetArrayLength() > 0)
                {
                    denials = [];
                    foreach (var x in pd.EnumerateArray())
                        denials.Add(x.TryGetProperty("tool_name", out var tnm) ? tnm.GetString() ?? "?" : "?");
                }
                var usage = ParseUsage(root);
                if (IsEmptyNoopResult(numTurns, subtype, apiErr, denials, usage))
                {
                    // Пустой result CLI (ноль ходов модели, ноль токенов, success без api-ошибки
                    // и отказов) — служебный маркер «модель не вызывалась», а не ответ
                    // пользовательскому ходу. Так CLI завершает микро-ходы task-notification'ов
                    // на --resume и запуск без submit (ре-аттемпт фолбэком). До фикса такой
                    // result резолвил ход «успехом»: пользователь видел завершённый ход без
                    // ответа, а настоящий result хода затем скипался фильтром корреляции
                    // (инцидент 16.08.2026: ходы «Проверь»/«Ну как» по 280 мс с нулём токенов).
                    // Проглатываем: ход ждёт настоящего result; не придёт — процесс завершится
                    // и его смерть уйдёт наружу честной ошибкой/фолбэком.
                    Console.Error.WriteLine(
                        "[ClaudeSession] Пустой result CLI (numTurns=0, без вызова модели) не засчитан ответом ходу " +
                        $"(session {Info.Id}) — ждём настоящий result");
                    break;
                }
                // Ход получил свой result (success или error) — завершён, ре-аттемпт этого
                // текста уже не висящий: повторный ход с тем же текстом пойдёт обычным submit.
                _lastTurnResolved = true;
                // На стороннем эндпоинте CLI считает total_cost_usd по ценам Anthropic —
                // пересчитываем по ценам конфига модели (нет цен → стоимость не показываем)
                if (_providers is not null && _providers.ResolveByModel(EffectiveModel) is not null)
                    totalCost = _providers.ComputeCost(EffectiveModel, usage);
                // API-ошибка (напр. 429 у провайдера): CLI отдаёт subtype=success, но is_error=true
                // и текст в result; синтетический assistant-текст не стримится дельтами —
                // без этого пользователь увидел бы пустой «успешный» ход
                var isErrorFlag = root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True;
                if (isErrorFlag
                    && root.TryGetProperty("result", out var resText) && resText.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(resText.GetString()))
                    await _onMessage(new ErrorMessage(resText.GetString()!, ExpectResultFollows: true));
                // Статус Error/Active выставит SessionManager по ResultMessage
                var ctxTokens = _lastContextTokens > 0 ? _lastContextTokens : (int?)null;
                await _onMessage(new ResultMessage(subtype, durationMs, numTurns, usage, totalCost, apiErr, denials, ctxTokens, ParseUsageModel(root)));
                // OTel: метрика длительности хода (duration_ms из самого CLI — не пересчитываем)
                // и счётчик ошибок. Оба признака отказа сводит IsTurnFailure: без is_error
                // отказы провайдера (429) уходили в метрику как outcome=success — счётчик
                // ccs.llm.errors пустовал, а мгновенные отказные ходы занижали p95 duration.
                // Модель — фактическая (её назвал CLI), а не та, что просили: при пустом слоте
                // EffectiveModel равен null и метрика получала unknown вместо ответа на вопрос
                // «чем считали». Намерение остаётся фолбэком, если CLI модель не назвал.
                var turnFailed = TurnTelemetry.IsTurnFailure(subtype, isErrorFlag);
                TurnTelemetry.RecordTurnResult(durationMs, Info.Provider, _turnCliModel ?? EffectiveModel,
                    isError: turnFailed, apiErrorStatus: apiErr,
                    isSandboxed: _launcher.IsSandboxed);
                // Тот же исход — на спан: иначе в трейсах отказной ход неотличим от успешного
                TurnTelemetry.MarkTurnOutcome(_turnActivity, turnFailed, apiErr);
                // Ход завершён. Без живых фоновых задач закрываем stdin — CLI выйдет сам,
                // дальше ждём его не дольше ResultExitGrace. С ними stdin держим открытым:
                // прогон доживает (агенты работают внутри процесса) и готов принять
                // следующий совместимый ход. Result'ы ходов-продолжений CLI сюда не доходят —
                // отфильтрованы корреляцией выше.
                {
                    var doneRun = run;
                    doneRun.TurnDone = true;
                    if (!doneRun.HasPendingBg) CloseStdin(doneRun);
                    else
                    {
                        int pendingCount;
                        lock (doneRun.PendingBg) pendingCount = doneRun.PendingBg.Count;
                        Console.WriteLine(
                            $"[ClaudeSession] Ход завершён, прогон доживает: фоновых задач {pendingCount}"
                            + (doneRun.PendingBgUnknown ? " (+неучтённые)" : ""));
                    }
                    doneRun.TurnTcs.TrySetResult();
                }
                // Гарантия исполнения одобренного плана: если ход завершился, а Claude так и не
                // приступил к правкам — дошлём команду на реализацию (следующий ход — без plan-режима)
                if (_awaitPlanExecution)
                {
                    var needFollowUp = !_sawToolSinceApprove && subtype != "error";
                    _awaitPlanExecution = false;
                    if (needFollowUp)
                    {
                        _forceNonPlanNextTurn = true;
                        _ = SendMessageAsync("Одобренный план согласован. Реализуй его полностью сейчас — без повторного планирования.");
                    }
                }
                break;

            case "user":
                await HandleUserMessageAsync(run, root);
                break;

            case "sdk_control_request":
                await HandlePermissionAsync(run, root);
                break;

            case "control_request":
                await HandleControlRequestAsync(run, root);
                break;

            case "rate_limit_event":
                await HandleRateLimitAsync(root);
                break;

            case "prompt_suggestion":
                await HandlePromptSuggestionAsync(root);
                break;
        }
        } // using (doc)
    }

    // Подсказка следующего сообщения (--prompt-suggestions): формат поля с текстом
    // официально не документирован — парсим снисходительно (suggestion/prompt/text,
    // строка или объект с теми же полями), непонятный payload пропускаем молча.
    private async Task HandlePromptSuggestionAsync(JsonElement root)
    {
        var text = ExtractSuggestionText(root);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"[ClaudeSession] prompt_suggestion: {Truncate(text.Trim(), 120)}");
            await _onMessage(new PromptSuggestionMessage(text.Trim()));
        }
        else
            // Формат события не документирован: если CLI сменил имя поля — увидим в логе
            Console.WriteLine($"[ClaudeSession] prompt_suggestion без распознанного текста: {Truncate(root.GetRawText(), 300)}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? ExtractSuggestionText(JsonElement root)
    {
        foreach (var key in (string[])["suggestion", "prompt", "text"])
        {
            if (!root.TryGetProperty(key, out var prop)) continue;
            switch (prop.ValueKind)
            {
                case JsonValueKind.String:
                    return prop.GetString();
                case JsonValueKind.Object:
                    var nested = ExtractSuggestionText(prop);
                    if (nested is not null) return nested;
                    break;
            }
        }
        return null;
    }

    // Сообщение принадлежит сабагенту (у CLI помечено parent_tool_use_id)
    private static bool HasParentToolUseId(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var pid) && pid.ValueKind == JsonValueKind.String;

    // Мягкий лимит API: claude шлёт rate_limit_event и приостанавливается до сброса окна.
    // Разбор вынесен в ClaudeRateLimitParser (общий со стартовым прогревом подписок).
    private async Task HandleRateLimitAsync(JsonElement root)
    {
        TurnTelemetry.RecordRateLimit(Info.Provider);
        if (ClaudeRateLimitParser.TryParse(root, out var msg))
            await _onMessage(msg);
    }

    private async Task HandleUserMessageAsync(CliRun run, JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;
        // Строковый content — служебные user-сообщения CLI (summary после компакта,
        // <local-command-stdout>, task-notification): не tool_result, в ленту не транслируем.
        // task-notification — завершение фоновой задачи, вычёркиваем её из pending прогона
        if (content.ValueKind == JsonValueKind.String)
        {
            HandleTaskNotification(content.GetString());
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            if (bt.GetString() != "tool_result") continue;

            var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() ?? "" : "";
            var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();

            // Атрибуция file_changed: заявка на путь из tool_use подтверждается ТОЛЬКО здесь,
            // по успешному результату — упавшая правка (old_string не найден и т.п.) не должна
            // глушить настоящую параллельную правку того же файла в другой сессии
            if (_pendingFileClaims.TryRemove(toolUseId, out var claimedPath) && !isError)
                _fileChangeAttributor?.Claim(Info.Id, claimedPath);

            var resultContent = "";
            if (block.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String)
                    resultContent = c.GetString() ?? "";
                else if (c.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var cb in c.EnumerateArray())
                        if (cb.TryGetProperty("text", out var t))
                            sb.AppendLine(t.GetString());
                    resultContent = sb.ToString().TrimEnd();
                }
            }

            // Дочитываем транскрипты сабагентов ДО трансляции результата: весь текст сабагента
            // должен лечь в ленту раньше tool_result (и продолжения текста основного агента)
            if (_subagentWatcher is not null) await _subagentWatcher.DrainAsync();

            await _onMessage(new ToolResultMessage(toolUseId, resultContent, isError));

            // Запуск фоновой задачи (async-агент, resume через SendMessage, workflow) —
            // берём её id на учёт прогона: пока pending не пуст, процесс переживает ход
            TrackBgLaunch(run, toolUseId, resultContent, isError);

            // Обратная сторона: модель сама опросила результат фоновой задачи через TaskOutput —
            // это сигнал её завершения (Kimi и др. не ждут task-notification)
            if (!isError) HandleTaskOutputCompletion(run, resultContent);

            // Синхронный Task: его tool_result и есть конец агента — снимаем паспорт прогона.
            // Фоновый запуск сюда не попадает (TrackBgLaunch выше уже взял его на учёт) —
            // у него конец объявляет task-notification, а паспорт на старте был бы пустым.
            if (_subagentWatcher is { IsDisposed: false } doneWatcher && !IsBgPending(run, toolUseId))
                await doneWatcher.FinalizeAsync([toolUseId], "tool_result");

            // Если это результат Workflow с транскриптом — запускаем watcher
            if (!isError && resultContent.Contains("Transcript dir:"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(resultContent, @"Transcript dir:\s*(.+)");
                if (m.Success)
                {
                    var transcriptDir = m.Groups[1].Value.Trim();
                    Console.WriteLine($"[WorkflowWatcher] старт: dir={transcriptDir} allowed={WorkflowAgentParser.IsPathAllowed(transcriptDir)}");
                    var watcher = new WorkflowWatcher(transcriptDir, toolUseId, _onMessage) { Owner = run };
                    lock (_workflowWatchers)
                    {
                        // Завершившиеся ватчеры диспозятся сами — чистим список, чтобы не рос
                        _workflowWatchers.RemoveAll(w => w.IsDisposed);
                        _workflowWatchers.Add(watcher);
                    }
                    watcher.Start();
                }
            }
        }
    }

    // Паттерны учёта фоновых задач. Id агентов у CLI бывают и hex (a4faf5af…), и base36
    // (br4ihb0jl) — берём общий алфавит [0-9a-zA-Z_-] (проверено live на CLI 2.1.212+)
    private static readonly System.Text.RegularExpressions.Regex BgAgentIdRe =
        new(@"agentId:\s*([0-9a-zA-Z_-]{6,})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BgResumedRe =
        new(@"Agent ""([0-9a-zA-Z_-]{6,})""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BgWorkflowRe =
        new(@"runId:\s*(wf_[0-9a-zA-Z_-]{4,})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TaskIdRe =
        new(@"<task-id>([^<]+)</task-id>", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Опрос результата фоновой задачи инструментом TaskOutput: его tool_result несёт
    // <task_id>X</task_id> (подчёркивание, не дефис как у task-notification) и
    // <status>completed|failed…</status>. Некоторые модели (Kimi/Moonshot) не ждут
    // task-notification, а сами тянут результат через TaskOutput — это тоже сигнал завершения.
    private static readonly System.Text.RegularExpressions.Regex TaskOutputIdRe =
        new(@"<task_id>\s*([0-9a-zA-Z_-]{6,})\s*</task_id>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TaskOutputStatusRe =
        new(@"<status>\s*([a-zA-Z_]+)\s*</status>", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Разбор tool_result инструмента TaskOutput → (agentId, aborted) при ТЕРМИНАЛЬНОМ статусе;
    // null — это не TaskOutput-результат либо агент ещё работает (running/pending/queued).
    // Чистая функция (вынесена ради юнит-тестов): без побочных эффектов и состояния прогона.
    internal static (string AgentId, bool Aborted)? ParseTaskOutputCompletion(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var idm = TaskOutputIdRe.Match(content);
        if (!idm.Success) return null;
        var statusM = TaskOutputStatusRe.Match(content);
        if (!statusM.Success) return null;
        return statusM.Groups[1].Value.Trim().ToLowerInvariant() switch
        {
            "completed" => (idm.Groups[1].Value.Trim(), false),
            "failed" or "error" or "cancelled" or "canceled" => (idm.Groups[1].Value.Trim(), true),
            _ => null, // running / pending / queued — ещё не готов, ждём дальше
        };
    }

    // Разбор структурных сабтайпов system-события CLI 2.1.220+. Чистые функции (вынесены
    // ради юнит-тестов на реальных JSON-образцах CLI) — без побочных эффектов и состояния прогона.

    // task_started: (TaskId, ToolUseId) — null, если task_id или tool_use_id пустые/отсутствуют
    // (без tool_use_id привязать задачу к карточке в ленте нечем).
    internal static (string TaskId, string ToolUseId)? ParseTaskStarted(JsonElement root)
    {
        var taskId = StringProp(root, "task_id");
        var toolUseId = StringProp(root, "tool_use_id");
        return string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(toolUseId) ? null : (taskId, toolUseId);
    }

    // task_notification (структурный): (TaskId, ToolUseId?, Aborted) — null, если task_id
    // отсутствует. Aborted = статус не "completed" (failed/stopped и любой нераспознанный
    // считаем обрывом — тот же принцип, что и у ParseTaskOutputCompletion).
    internal static (string TaskId, string? ToolUseId, bool Aborted)? ParseTaskNotification(JsonElement root)
    {
        var taskId = StringProp(root, "task_id");
        return string.IsNullOrEmpty(taskId) ? null : (taskId, StringProp(root, "tool_use_id"), StringProp(root, "status") != "completed");
    }

    // background_tasks_changed: true — массив tasks присутствует и пуст. Единственное
    // безопасное применение этого события (см. HandleBackgroundTasksChanged) — остальные
    // его формы (непустой список) намеренно не разбираем.
    internal static bool IsBackgroundTasksEmptySnapshot(JsonElement root) =>
        root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array && tasks.GetArrayLength() == 0;

    // Учёт запуска фоновой задачи по tool_result: async-агент — «Async agent launched …
    // agentId: X», возобновление — «Agent "X" … resumed from transcript in the background»,
    // workflow — «runId: wf_…». Структурный кандидат (run_in_background/Workflow из
    // HandleAssistantToolsAsync) без распарсенного id → PendingBgUnknown: точный учёт
    // потерян, доживание прогона ограничится только потолком BgLingerTimeout.
    private void TrackBgLaunch(CliRun run, string toolUseId, string content, bool isError)
    {
        bool candidate;
        lock (run.PendingBg) candidate = run.BgLaunchCandidates.Remove(toolUseId);
        if (isError) return;

        // Гейт по маркерам запуска: без него агентский id пришлось бы искать в любом
        // tool_result (Bash с логами нашего же продукта дал бы ложный pending навсегда)
        var launchLike = candidate
            || content.Contains("Async agent launched", StringComparison.Ordinal)
            || content.Contains("resumed from transcript in the background", StringComparison.Ordinal)
            || content.Contains("Transcript dir:", StringComparison.Ordinal);
        if (!launchLike) return;

        var m = BgAgentIdRe.Match(content);
        if (!m.Success) m = BgWorkflowRe.Match(content);
        if (!m.Success && content.Contains("in the background", StringComparison.Ordinal))
            m = BgResumedRe.Match(content);
        if (m.Success)
            lock (run.PendingBg) run.PendingBg[m.Groups[1].Value] = toolUseId;
        else if (candidate)
        {
            run.PendingBgUnknown = true;
            lock (run.PendingBg) run.UnknownBgToolUses.Add(toolUseId);
        }
    }

    // Этот tool_use — фоновый агент, который только запустился и ещё работает? Учёт ведут
    // и структурное событие task_started, и текстовый TrackBgLaunch, и кандидаты запуска.
    private static bool IsBgPending(CliRun run, string toolUseId)
    {
        lock (run.PendingBg)
            return run.PendingBg.ContainsValue(toolUseId)
                || run.UnknownBgToolUses.Contains(toolUseId)
                || run.BgLaunchCandidates.Contains(toolUseId);
    }

    // Общая точка завершения фоновой(ых) задачи(задач): дочитывает хвост сабагента (финальный
    // текст должен лечь в ленту РАНЬШЕ индикатора завершения) и шлёт клиентам bg_agent_done.
    // Переиспользуется всеми путями завершения: task-notification (текстовый и структурный),
    // TaskOutput-опрос, смерть процесса (FinalizeRunAsync — drainSubagent: false, там
    // _subagentWatcher либо уже продренирован и обнулён, либо принадлежит НОВОМУ прогону,
    // и трогать его нельзя). Снятие задачи из PendingBg/Unknown — забота вызывающего кода:
    // у путей разный способ поиска (регекс по тексту, словарный Remove по task_id,
    // bulk-очистка при обрыве прогона).
    private async Task CompleteBgTasksAsync(IReadOnlyList<string> toolUseIds, bool aborted, bool drainSubagent = true)
    {
        if (toolUseIds.Count == 0) return;
        // Паспорт прогона снимаем ровно здесь: это и есть момент, когда продукт объявляет
        // результат агента готовым (диагностика обрывов — см. SubagentRunLog). FinalizeAsync
        // дренирует ватчер сам, поэтому отдельный DrainAsync ему не нужен.
        if (drainSubagent && _subagentWatcher is { IsDisposed: false } watcher)
            await watcher.FinalizeAsync(toolUseIds, aborted ? "bg_aborted" : "bg_done");
        await _onMessage(new BgAgentDoneMessage(toolUseIds, Aborted: aborted));
    }

    // Уведомление CLI о завершении фоновых задач: user-ход со строковым content
    // <task-notification>…<task-id>X</task-id>… Вычёркиваем задачи из pending и шлём клиентам
    // bg_agent_done (карточки агентов переключаются из «работает» в «ответ готов» только
    // по этому событию). Stdin НЕ закрываем: task_notification запускает ход-продолжение CLI,
    // которому нужен живой stdin для permission-канала (can_use_tool → control_response) —
    // преждевременное закрытие роняло все tool-запросы продолжения с «Stream closed». Закрытие
    // выполнит result самого продолжения (case "result" → CloseStdinIfIdle); не начнётся —
    // процесс умрёт по ватчдогу (ContinuationStartGrace тишины).
    // Текстовый путь статус не несёт — завершение всегда считается успешным (Aborted: false);
    // структурный task_notification (HandleStructuredTaskNotification) точнее.
    private void HandleTaskNotification(string? text)
    {
        var run = _run;
        if (run is null || text is null
            || !text.Contains("<task-notification>", StringComparison.Ordinal)) return;
        List<string> doneTools = [];
        int removed, left;
        lock (run.PendingBg)
        {
            var before = run.PendingBg.Count;
            foreach (System.Text.RegularExpressions.Match m in TaskIdRe.Matches(text))
                if (run.PendingBg.Remove(m.Groups[1].Value.Trim(), out var toolUseId)
                    && !string.IsNullOrEmpty(toolUseId))
                    doneTools.Add(toolUseId);
            removed = before - run.PendingBg.Count;
            left = run.PendingBg.Count;
        }
        if (removed > 0)
            Console.WriteLine($"[ClaudeSession] Фоновая задача завершилась ({removed} шт.), осталось {left}");
        if (doneTools.Count > 0)
            _ = Task.Run(async () =>
            {
                try { await CompleteBgTasksAsync(doneTools, aborted: false); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ClaudeSession] bg_agent_done не разослан: {ex.Message}");
                }
            });
    }

    // Завершение фоновой задачи, замеченное по tool_result инструмента TaskOutput (модели,
    // которые сами опрашивают результат, а не ждут task-notification). Вычёркиваем агента
    // из pending и шлём bg_agent_done — иначе карточка консультации крутила бы спиннер вечно.
    // Идемпотентно: повторный опрос того же агента уже не найдёт его в pending и no-op.
    private void HandleTaskOutputCompletion(CliRun run, string content)
    {
        if (ParseTaskOutputCompletion(content) is not { } completion) return;
        var (agentId, aborted) = completion;

        string? doneTool;
        int left;
        lock (run.PendingBg)
        {
            run.PendingBg.Remove(agentId, out doneTool);
            left = run.PendingBg.Count;
        }
        if (string.IsNullOrEmpty(doneTool)) return;

        Console.WriteLine($"[ClaudeSession] Фоновая задача завершилась через TaskOutput (aborted={aborted}), осталось {left}");
        var tool = doneTool;
        _ = Task.Run(async () =>
        {
            try { await CompleteBgTasksAsync([tool], aborted); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] bg_agent_done (TaskOutput) не разослан: {ex.Message}");
            }
        });
    }

    // Структурное событие CLI: старт фоновой задачи. Первичный (точный) источник учёта —
    // несёт готовую пару task_id↔tool_use_id, в отличие от TrackBgLaunch (регекс по тексту
    // tool_result). Идемпотентно: повторное/дублирующее событие того же task_id просто
    // перезапишет тем же значением; если tool_use_id уже был учтён как неизвестный (текстовый
    // путь не смог распарсить id) — снимаем его оттуда, чтобы карточка не осталась висеть
    // в UnknownBgToolUses и не закрылась дважды при финализации прогона.
    private void HandleTaskStarted(CliRun run, JsonElement root)
    {
        if (ParseTaskStarted(root) is not { } started) return;
        var (taskId, toolUseId) = started;
        lock (run.PendingBg)
        {
            run.PendingBg[taskId] = toolUseId;
            run.BgLaunchCandidates.Remove(toolUseId);
            if (run.UnknownBgToolUses.Remove(toolUseId) && run.UnknownBgToolUses.Count == 0)
                run.PendingBgUnknown = false;
        }
    }

    // Структурное событие CLI: завершение фоновой задачи (completed/failed/stopped) — точный
    // аналог текстового <task-notification>, но с готовым статусом (текстовый путь статус не
    // несёт и всегда считает завершение успешным). Fallback: если task_id не учтён в PendingBg
    // (запуск проехал мимо и структурного, и текстового пути), но tool_use_id ещё числится
    // кандидатом/неучтённым — закрываем карточку по нему всё равно, иначе она крутилась бы
    // вечно. Гейт по факту снятия: повторное событие уже закрытой задачи ничего не находит
    // ни в PendingBg, ни в BgLaunchCandidates/UnknownBgToolUses — done не шлём, чтобы не
    // задваивать карточку в UI.
    private void HandleStructuredTaskNotification(CliRun run, JsonElement root)
    {
        if (ParseTaskNotification(root) is not { } n) return;
        var (taskId, toolUseId, aborted) = n;

        string? doneTool;
        lock (run.PendingBg) run.PendingBg.Remove(taskId, out doneTool);

        if (string.IsNullOrEmpty(doneTool) && !string.IsNullOrEmpty(toolUseId))
        {
            bool wasTracked;
            lock (run.PendingBg)
            {
                wasTracked = run.BgLaunchCandidates.Remove(toolUseId);
                if (run.UnknownBgToolUses.Remove(toolUseId))
                {
                    wasTracked = true;
                    if (run.UnknownBgToolUses.Count == 0)
                        run.PendingBgUnknown = false;
                }
            }
            if (wasTracked) doneTool = toolUseId;
        }
        if (string.IsNullOrEmpty(doneTool)) return;

        Console.WriteLine($"[ClaudeSession] Фоновая задача завершилась (структурно, aborted={aborted})");
        var tool = doneTool;
        _ = Task.Run(async () =>
        {
            try { await CompleteBgTasksAsync([tool], aborted); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ClaudeSession] bg_agent_done (structured) не разослан: {ex.Message}");
            }
        });
    }

    // Структурное событие CLI: снэпшот живых фоновых задач. Единственное безопасное
    // применение — пустой tasks сбрасывает PendingBgUnknown (CLI сам подтвердил: неучтённых
    // задач больше нет). Карточки НЕ закрываем и PendingBg/UnknownBgToolUses не чистим здесь:
    // пустой снэпшот наблюдался живьём и ДО task_notification — закрытие карточек остаётся
    // за ним (и за финализацией прогона), не за этим событием.
    private void HandleBackgroundTasksChanged(CliRun run, JsonElement root)
    {
        if (IsBackgroundTasksEmptySnapshot(root)) run.PendingBgUnknown = false;
    }

    private async Task HandleStreamEventAsync(JsonElement root)
    {
        // Стрим-события сабагента (если CLI вдруг начнёт их слать) не подмешиваем в текст
        // основного агента — его контент придёт целыми блоками в HandleAssistantToolsAsync
        if (root.TryGetProperty("parent_tool_use_id", out var sePid) && sePid.ValueKind == JsonValueKind.String)
            return;

        if (!root.TryGetProperty("event", out var evt)) return;
        if (!evt.TryGetProperty("type", out var et)) return;
        var eventType = et.GetString();
        var index = evt.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : -1;

        // Начало блока tool_use — показываем карточку сразу (до прихода полного assistant-сообщения)
        if (eventType == "content_block_start")
        {
            if (!evt.TryGetProperty("content_block", out var cb)) return;
            if (!cb.TryGetProperty("type", out var cbt) || cbt.GetString() != "tool_use") return;
            var id = cb.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
            var name = cb.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
            // Служебные инструменты не показываем: AskUserQuestion/ExitPlanMode идут отдельными
            // карточками (вопрос/план), ToolSearch — внутренний механизм загрузки схем инструментов
            if (id.Length == 0 || name is "AskUserQuestion" or "ExitPlanMode" or "ToolSearch") return;
            _toolStream[index] = (id, new System.Text.StringBuilder());
            await _onMessage(new ToolUseMessage(id, name, new { }));
            return;
        }

        if (eventType == "content_block_stop") { _toolStream.TryRemove(index, out _); return; }

        if (eventType != "content_block_delta") return;
        if (!evt.TryGetProperty("delta", out var delta)) return;
        if (!delta.TryGetProperty("type", out var dt)) return;

        switch (dt.GetString())
        {
            case "text_delta":
                if (delta.TryGetProperty("text", out var text))
                    await _onMessage(new TextDeltaMessage(text.GetString() ?? ""));
                break;

            case "thinking_delta":
                if (delta.TryGetProperty("thinking", out var thinking))
                    await _onMessage(new ThinkingDeltaMessage(thinking.GetString() ?? ""));
                break;

            case "input_json_delta":
                if (_toolStream.TryGetValue(index, out var ts) && delta.TryGetProperty("partial_json", out var pj))
                {
                    ts.Sb.Append(pj.GetString());
                    await _onMessage(new ToolInputDeltaMessage(ts.Id, ts.Sb.ToString()));
                }
                break;
        }
    }

    private async Task HandleAssistantToolsAsync(CliRun run, JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;

        // Сообщения субагента (Task) несут parent_tool_use_id на уровне строки — для вложенности
        var parentId = root.TryGetProperty("parent_tool_use_id", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString() : null;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            var blockType = bt.GetString();

            // Скрытое размышление — показываем плашку-плейсхолдер (только у основного агента:
            // от сабагента плейсхолдер попал бы в основную ленту)
            if (blockType == "redacted_thinking")
            {
                if (parentId is null) await _onMessage(new RedactedThinkingMessage());
                continue;
            }

            // Текст/thinking сабагента CLI в stdout НЕ транслирует (сюда приходят только его
            // tool_use) — полный поток эмитит SubagentStreamWatcher из транскрипта на диске.
            // Текстовые блоки основного агента пропускаем: они уже пришли дельтами stream_event.
            if (blockType != "tool_use") continue;

            var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
            var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
            var toolInput = block.TryGetProperty("input", out var ti)
                ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();

            // Атрибуция file_changed (см. FileChangeAttributor): путь запоминаем как ОЖИДАЮЩУЮ
            // заявку — подтвердим (Claim) только по успешному tool_result (HandleUserMessageAsync)
            // или снимем при отказе permission (HandleControlRequestAsync). Заявлять сразу здесь
            // нельзя: правка может быть отклонена пользователем или упасть с ошибкой (old_string
            // не найден и т.п.) — тогда чужая сессия, реально правящая тот же файл в те же 15с,
            // молча потеряла бы свою карточку из-за протухшей заявки несостоявшейся правки.
            if (_fileChangeAttributor is not null && toolId.Length > 0
                && ExtractFileWritePath(toolName, ti) is { Length: > 0 } fp)
                _pendingFileClaims[toolId] = Path.IsPathRooted(fp) ? fp : Path.Combine(_rootPath, fp);

            // Служебные инструменты не дублируем в ленте: AskUserQuestion/ExitPlanMode показываем
            // отдельными карточками (вопрос/план), ToolSearch — внутренняя загрузка схем инструментов
            if (toolName is "AskUserQuestion" or "ExitPlanMode" or "ToolSearch") continue;
            // После одобрения плана любой реальный инструмент означает, что Claude приступил к реализации
            if (_awaitPlanExecution) _sawToolSinceApprove = true;
            // Workflow по имени (без inline-script) → дописываем meta-блок скрипта, чтобы фронт
            // показал этапы (дотики фаз + N/M в тулбаре и карточке)
            if (toolName == "Workflow") toolInput = EnrichWorkflowInput(toolInput);

            // Кандидат в фоновые задачи прогона: Agent/Task с run_in_background или Workflow —
            // подтверждение запуска и id задачи придут в tool_result (TrackBgLaunch)
            if (parentId is null && toolId.Length > 0
                && (toolName == "Workflow"
                    || (toolName is "Agent" or "Task" && block.TryGetProperty("input", out var inputEl)
                        && inputEl.TryGetProperty("run_in_background", out var bgEl)
                        && bgEl.ValueKind == JsonValueKind.True)))
                lock (run.PendingBg) run.BgLaunchCandidates.Add(toolId);

            await _onMessage(new ToolUseMessage(toolId, toolName, toolInput, parentId));
        }

        // Ответ оборван по лимиту токенов (у сабагента — не показываем плашку в основной ленте)
        if (parentId is null && msg.TryGetProperty("stop_reason", out var stopReason)
            && stopReason.GetString() == "max_tokens")
            await _onMessage(new TruncatedMessage());
    }

    // Обогащение input вызова Workflow: сохранённый workflow запускается по имени
    // (Workflow({ name, args }) без script), а фронт достаёт meta.phases только из
    // input.script — этапы пропадали. Дописываем вырезанный блок `export const meta {…}`
    // того же скрипта, что исполнил CLI (workflows-каталог профиля этой сессии).
    private object EnrichWorkflowInput(object input)
    {
        if (input is not JsonElement el || el.ValueKind != JsonValueKind.Object) return input;
        // Inline-script уже есть (модель передала скрипт целиком) — не трогаем
        if (el.TryGetProperty("script", out _)) return input;
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return input;
        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name)) return input;

        var metaBlock = WorkflowMetaResolver.TryGetMetaBlock(WorkflowScriptDirs(), name);
        if (metaBlock is null) return input;

        // Пересобираем input словарём: исходные поля (JsonElement) + script (строка meta-блока).
        // System.Text.Json сериализует смешанные значения штатно.
        var dict = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject()) dict[p.Name] = p.Value;
        dict["script"] = metaBlock;
        return dict;
    }

    // Каталог workflow-скриптов профиля ЭТОГО хода (ровно тот файл, что исполняет CLI):
    // _cliConfigRoot приходит из SessionManager.ConfigRootFor — единственной точки, знающей
    // раскладку профилей (сторонний провайдер, подписка пула sub-*, песочница). Своя усечённая
    // копия этой логики знала только про провайдеров реестра и уводила ходы на подписке пула
    // в хостовый ~/.claude/workflows, куда однажды попали копии механик, перекодированные мимо
    // UTF-8: CLI исполнял правильный скрипт из профиля, а в карточку ехали кракозябры.
    private IReadOnlyList<string> WorkflowScriptDirs() =>
        _cliConfigRoot is { Length: > 0 } root
            ? [Path.Combine(root, "workflows")]
            : [WorkflowMetaResolver.GlobalWorkflowsDir];

    // Permission-запрос старого канала (sdk_control_request) — общий пайплайн DecidePermissionAsync
    private async Task HandlePermissionAsync(CliRun run, JsonElement root)
    {
        // Используем request_id из CLI — именно его ждёт claude в control_response
        var requestId = root.TryGetProperty("request_id", out var rid)
            ? rid.GetString() ?? Guid.NewGuid().ToString()
            : Guid.NewGuid().ToString();

        var toolName = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var inputEl = root.TryGetProperty("tool_input", out var ti) ? ti : default;
        var toolInput = inputEl.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.Deserialize<object>(inputEl.GetRawText())! : new object();

        var behavior = await DecidePermissionAsync(requestId, toolName, inputEl, toolInput);
        if (behavior == "cancelled") return; // Interrupt — процесс убит, отвечать некому

        // Без updated_input — CLI продолжает с исходным вводом (см. HandleControlRequestAsync)
        var response = JsonSerializer.Serialize(new
        {
            type = "control_response",
            behavior
        });
        WriteLineToStdin(response, run.Process);
    }

    // Размер контекста последнего запроса к API. usage у assistant-сообщения относится к ОДНОМУ
    // запросу (в отличие от result, где всё сложено за ход), поэтому сумма входных токенов здесь
    // и есть текущее заполнение окна. Сабагентов пропускаем: у них свой контекст, к окну
    // основной сессии отношения не имеющий.
    private void TrackContextTokens(JsonElement root)
    {
        if (HasParentToolUseId(root)) return;
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("usage", out var u)) return;

        var tokens = IntProp(u, "input_tokens")
            + IntProp(u, "cache_read_input_tokens")
            + IntProp(u, "cache_creation_input_tokens");
        if (tokens > 0) _lastContextTokens = tokens;
    }

    // Ход мог уйти в собственный git worktree через встроенный инструмент EnterWorktree —
    // это происходит мимо тумблера чата (Session.WorktreePath/SetWorktreeAsync), поэтому
    // фактический cwd из system/init сверяем с тем, что сервер сам передал в WorkingDirectory
    // при запуске процесса. rootPath уже учитывает штатное дерево чата (SessionManager.EffectiveRoot
    // подставляет Session.WorktreePath ДО старта процесса) — совпадение с ним тоже даёт null.
    internal static TurnWorktreeInfo? ResolveTurnWorktree(string? cwd, string rootPath, Execution.IProcessLauncher launcher)
    {
        if (string.IsNullOrEmpty(cwd)) return null;

        // Признак косметический (подпись в UI), а вызывается на каждом system/init у всех
        // пользователей — сбой нормализации пути (Path.GetFullPath внутри NormalizePath
        // кидает ArgumentException/NotSupportedException на не вполне обычных путях) не должен
        // ронять ход целиком: необработанное исключение здесь ушло бы в общий catch цикла чтения
        // прогона ДО отправки SessionStartedMessage — не проставился бы ClaudeSessionId и не
        // поднялись бы _subagentWatcher/_transcriptTailer. Деградация — как в BuildTurnMcpConfig.
        try
        {
            // В песочнице WorkingDirectory переводится в контейнерный путь при старте процесса
            // (DockerProcessRunner) — сверяем с тем же переводом, иначе КАЖДЫЙ ход в контейнере
            // ложно считался бы «чужим деревом» (cwd там всегда в другом пространстве путей)
            var expected = rootPath;
            if (launcher.IsSandboxed)
            {
                try { expected = launcher.Paths.ToRuntime(rootPath); }
                catch (InvalidOperationException) { /* непереводимый корень — сравниваем как есть */ }
            }

            if (WorkspaceKnowledgeStore.NormalizePath(cwd) == WorkspaceKnowledgeStore.NormalizePath(expected))
                return null;

            var trimmed = cwd.TrimEnd('/', '\\');
            var name = trimmed.Length > 0 ? Path.GetFileName(trimmed) : cwd;
            return new TurnWorktreeInfo(cwd, string.IsNullOrEmpty(name) ? cwd : name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ClaudeSession] Не удалось определить дерево хода для cwd={cwd}: {ex.Message}");
            return null;
        }
    }

    // Токены хода из result. Основной источник — modelUsage: агрегат по ВСЕМ итерациям хода
    // (ключи — модели, поля camelCase), тогда как usage описывает только последнюю итерацию —
    // на многоитерационных ходах расходятся в разы, и стоимость у сторонних провайдеров
    // занижалась. Фолбэк на usage — если modelUsage отсутствует или пуст.
    internal static UsageInfo? ParseUsage(JsonElement root)
    {
        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
        {
            int input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
            var any = false;
            foreach (var m in mu.EnumerateObject())
            {
                if (m.Value.ValueKind != JsonValueKind.Object) continue;
                any = true;
                input += IntProp(m.Value, "inputTokens");
                output += IntProp(m.Value, "outputTokens");
                cacheRead += IntProp(m.Value, "cacheReadInputTokens");
                cacheCreate += IntProp(m.Value, "cacheCreationInputTokens");
            }
            if (any) return new UsageInfo(input, output, cacheRead, cacheCreate);
        }
        if (!root.TryGetProperty("usage", out var u)) return null;
        return new UsageInfo(
            IntProp(u, "input_tokens"),
            IntProp(u, "output_tokens"),
            IntProp(u, "cache_read_input_tokens"),
            IntProp(u, "cache_creation_input_tokens")
        );
    }

    // Доминирующая модель хода — ключ modelUsage с наибольшей суммой токенов (субагенты могли
    // считать другой моделью; аналитике расхода нужна главная). null — modelUsage отсутствует,
    // потребитель откатывается на модель сессии.
    internal static string? ParseUsageModel(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var mu) || mu.ValueKind != JsonValueKind.Object)
            return null;
        string? best = null;
        long bestSum = -1;
        foreach (var m in mu.EnumerateObject())
        {
            if (m.Value.ValueKind != JsonValueKind.Object) continue;
            long sum = IntProp(m.Value, "inputTokens") + IntProp(m.Value, "outputTokens")
                + IntProp(m.Value, "cacheReadInputTokens") + IntProp(m.Value, "cacheCreationInputTokens");
            if (sum > bestSum)
            {
                bestSum = sum;
                best = m.Name;
            }
        }
        return best;
    }

    // Безопасное чтение числовых полей stream-json. TryGetProperty возвращает true и для JSON
    // null, а Get*/TryGet* на НЕ-числовом элементе (Null/String) КИДАЮТ InvalidOperationException
    // (TryGetInt32 отдаёт false лишь при переполнении Number, но не при Null!) — на openrouter-
    // совместимом потоке (usage/стоимость приходят null) это роняло цикл чтения прогона.
    // Поэтому обязательна явная проверка ValueKind == Number перед чтением.
    internal static int IntProp(JsonElement o, string name, int def = 0) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : def;
    internal static long LongProp(JsonElement o, string name, long def = 0) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : def;
    internal static double? DoubleProp(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : (double?)null;
    internal static string? StringProp(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    // api_error_status приходит и строкой (ярлык CLI: "rate_limit", "authentication_error"),
    // и ЧИСЛОМ (HTTP-код: 401, 429, 500) — CLI не приводит его к одному типу. Числовой код
    // приводим к строке прямо на границе парсинга, чтобы весь конвейер ниже
    // (ResultMessage.ApiErrorStatus, TurnErrorClassifier, ExecutorStopClassifier) работал
    // с одним строковым представлением. Без этого числовые коды молча терялись.
    internal static string? StatusProp(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt32(out var code) ? code.ToString(CultureInfo.InvariantCulture) : null,
            _ => null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Ход в полёте на момент уборки адаптера — ненормально: он упадёт на диспознутых
        // семафорах (гасится в QueueTurnAsync). Строка в логе — след для разбора
        if (_run is not null || Volatile.Read(ref _queuedTurns) > 0)
            Console.Error.WriteLine($"[ClaudeSession] DisposeAsync под живым ходом (session {Info.Id}, run={_run is not null}, queued={Volatile.Read(ref _queuedTurns)})");
        _fileWatcher.Dispose();
        _subagentWatcher?.Dispose();
        _subagentWatcher = null;
        _transcriptTailer?.Dispose();
        _transcriptTailer = null;
        lock (_workflowWatchers)
        {
            foreach (var w in _workflowWatchers) w.Dispose();
            _workflowWatchers.Clear();
        }
        // Ожидающие permission-диалоги: ответа не будет — отменяем, иначе
        // DecidePermissionAsync держит граф адаптера до часового таймаута
        CancelPendingControlResponses();
        _cts.Cancel();
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            // Убиваем всё дерево: claude порождает node-процессы MCP-серверов
            _launcher.Kill(_currentProcess, _currentTurnId);
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _currentProcess.WaitForExitAsync(exitCts.Token); }
            catch (OperationCanceledException) { } // 10 с истекло — идём дальше
        }
        _currentProcess?.Dispose();
        // _cts/_turnLock/_stdinLock НЕ диспозим (инцидент 16.08.2026: «Cannot access a
        // disposed object: SemaphoreSlim» в ленте). Реанимация зависшего чата
        // (ReviveStuckSession) зовёт DisposeAsync в фоне, пока ход запаркован на
        // _turnLock или идёт внутри него — dispose примитивов под живыми ожидателями
        // это гонка by design: WaitAsync/Wait/Release бросают ObjectDisposedException
        // в чат пользователя или в необработанное исключение. SemaphoreSlim без
        // AvailableWaitHandle не держит неуправляемых ресурсов — мусорщик соберёт их
        // вместе с отвязанным адаптером (entry.Process = null в ReviveStuckSession).
    }
}
