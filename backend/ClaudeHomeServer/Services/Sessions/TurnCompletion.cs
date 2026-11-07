using System.Diagnostics;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Team;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Конец хода (S3-6, docs/research/session-manager-split-2026-11.md §1 «ход»): разбор сообщений
// адаптера (OnMessageQueuedAsync) — аккумулятор, статусы, гейты прогона, запуск очереди и цикла —
// перенесён из SessionManager дословно, с комментариями. Рассылка в SignalR и обход наблюдателей
// остаются в очереди сессии (SessionMessagePipeline), и её владелец теперь этот класс.
// Шов приёма сообщений меняет владельца на этот класс: хост процесса CLI и фабрика контекста
// отдают сообщения хода сюда — смена владельца правкой одной строки регистрации
// в AddSessionCore и одной строки запасной сборки в конструкторе SessionManager (S3-5).
// Фолбэк хода — маппинг ErrorMessage без следующего result к SessionStatus.Error — переехал
// вместе с телом; на нём характеризационный тест SessionManagerTurnErrorStatusTests.
// Зависимости десять (план §2, редакция 2247620de; S3-7 — редакция аддендума): store, history
// (снимки аккумулятора пишет через ChatHistoryService), route, worktree, providerMigration,
// ledger (цены fal/glif, учёт лимитов, расход хода — RecordTurnSpend переехал в журнал цен
// вместе с этим вызовом), ленивые швы ITurnStarter (разбор очереди — старт хода принадлежит
// приёму), IWorkLoopContinuation (продолжение цикла «до готово» и добивание — S3-7: они не
// стартуют ход сами и не должны видеть весь шов приёма; тело цикла на фасаде до шага 2) и
// ITeamNotifier (штаб: bg-agent, ASK-карточка, возврат сторожа волн). processHost здесь нет:
// тело конца хода не зовёт ни одного метода хоста процесса, entry.Process — поле записи,
// то есть стор. Экземпляра PendingQueueService тоже нет: тело пользуется только его
// статикой HasPending/HasContinuingPending.
// Флаг «штатная остановка» (Stopping) — в ядре приёма TurnIntake (S3-7, аддендум §2): писатель
// один — BeginShutdown (его зовёт SessionManager.KillAllProcesses ДО гашения адаптеров); здесь
// его читают через IsStopping шва ITurnStarter — только как раннюю отсечку разбора (ADR-033:
// обрывы умирающего процесса статус не меняют).
// Событие OnSessionMessage (подписка наблюдателей ленты) — здесь: его единственный потребитель —
// ProcessPipelineItemAsync; фасад держит аксессоры подписки — внешняя сигнатура прежняя.
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// конструктор internal (как у ProviderMigration): параметры держат internal-типы швов
// (ITeamNotifier), DI — factory-лямбда в AddSessionCore.
public sealed class TurnCompletion : ITurnMessageSink
{
    private readonly SessionStore _store;
    private readonly ChatHistoryService _history;
    private readonly CliRoute _route;
    private readonly ChatWorktree _worktree;
    private readonly ProviderMigration _providerMigration;
    private readonly TurnCostLedger _costs;
    private readonly Lazy<ITurnStarter> _turnStarter;
    private readonly Lazy<IWorkLoopContinuation> _workLoop;
    private readonly Lazy<ITeamNotifier> _teamNotifier;
    private readonly ILogger<TurnCompletion> _log;

    // Наблюдатели сообщения сессии (автоматизация персон, git-автокоммит, журнал событий):
    // подписка идёт через фасадный аксессор на этот экземляр; потребитель — ProcessPipelineItemAsync.
    internal event Func<Session, ServerMessage, Task>? OnSessionMessage;

    internal TurnCompletion(SessionStore store, ChatHistoryService history, CliRoute route,
        ChatWorktree worktree, ProviderMigration providerMigration, TurnCostLedger costs,
        Lazy<ITurnStarter> turnStarter, Lazy<IWorkLoopContinuation> workLoop,
        Lazy<ITeamNotifier> teamNotifier, ILogger<TurnCompletion> log)
    {
        _store = store;
        _history = history;
        _route = route;
        _worktree = worktree;
        _providerMigration = providerMigration;
        _costs = costs;
        _turnStarter = turnStarter;
        _workLoop = workLoop;
        _teamNotifier = teamNotifier;
        _log = log;
    }

    internal async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
    {
        await OnMessageQueuedAsync(sessionId, acc, msg, runId);
        // Тестовый шов сохраняет прежнюю семантику «вернулся — рассылка и наблюдатели отработали»;
        // боевые вызывающие идут в OnMessageQueuedAsync и очередь не ждут
        await DrainPipelineAsync(sessionId);
    }

    internal SessionMessagePipeline? PipelineForTest(string sessionId) =>
        _store.Sessions.TryGetValue(sessionId, out var entry) ? entry.Pipeline : null;

    // Дожидается разбора всего, что легло в очередь сессии (тесты и точки, которым нужны
    // отработавшие наблюдатели)
    internal Task DrainPipelineAsync(string sessionId) =>
        _store.Sessions.TryGetValue(sessionId, out var entry) && entry.Pipeline is { } pipeline
            ? pipeline.DrainAsync() : Task.CompletedTask;

    // Шов ITurnMessageSink (S3-5 → S3-6): процесс CLI отдаёт сообщения хода в ленту через него
    // (TurnLaunchContextFactory держит ленивый шов). С этого шага владелец разбора — этот класс:
    // явная реализация на SessionManager снята, регистрация в AddSessionCore резолвит его.
    Task ITurnMessageSink.OnMessageQueuedAsync(string sessionId, TurnAccumulator accumulator,
        ServerMessage message, long runId) => OnMessageQueuedAsync(sessionId, accumulator, message, runId);

    // Боевой вход обработчика сообщений адаптера. Всё, что меняет состояние сессии (снимок в
    // аккумулятор, статус, гейты прогона, запуск очереди и цикла), выполняется здесь же и по
    // порядку; рассылка в SignalR и обход наблюдателей уходят в очередь сессии
    // (SessionMessagePipeline), чтобы медленный клиент или наблюдатель не держал read-loop CLI.
    internal async Task OnMessageQueuedAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
    {
        _store.Sessions.TryGetValue(sessionId, out var entry);
        // Волна 6: живая трансляция хода штаба фильтрует маркеры протокола (см. кейс ниже) —
        // иногда дельте нечего транслировать (весь её текст — часть маркера/незавершённый
        // хвост), тогда исходное сообщение до низового BroadcastAsync не доходит вовсе.
        var sendBroadcast = true;

        // Аккумулятор — отдельный try, чтобы ошибка сохранения истории
        // не заблокировала обновление статуса и широковещания.
        try
        {
            switch (msg)
            {
                case SessionStartedMessage m:
                    // Страховка ветвления: CLI 2.1.276 сессию по --resume не форкает (разведка
                    // шага 0), но это поведение версионно-зависимое. Ветке мы подложили
                    // транскрипт и историю под СВОИМ csid — если CLI выдал другой session_id,
                    // подготовленная пара осиротела и её надо убрать, иначе она пролежит на
                    // диске до плановой уборки CLI, а в data/sessions останется навсегда.
                    // Гейт BranchedFromSessionId — у обычных чатов проверка бесплатна.
                    if (entry is { Info.BranchedFromSessionId: not null }
                        && acc.SaveKey is { Length: > 0 } preparedCsid
                        && !string.Equals(preparedCsid, m.ClaudeSessionId, StringComparison.Ordinal))
                        CleanupOrphanBranchTranscript(entry.Info, preparedCsid);
                    acc.SetSaveKey(m.ClaudeSessionId);
                    acc.OnSessionStarted(m.Model, m.Mode, m.TurnWorktree);
                    if (entry is not null)
                    {
                        entry.TurnInWorktree = m.TurnWorktree != null;
                        // Этап 4 / шаг 1а: фиксируем TurnSeq текущего хода для слота осушенного
                        // буфера. SubmittedTurnSeq инкрементируется в SubmitTurn
                        // (ClaudeSession.cs:2351), а SessionStartedMessage шлётся после неё
                        // (ClaudeSession.cs:4099) — к моменту нашего чтения номер уже актуален.
                        // 0 — локальный голосовой ход (адаптер не Claude, SubmittedTurnSeq = 0),
                        // turn/completed по нему не публикуется, слот не пополняется.
                        entry.LastTurnSeq = entry.Process is { } adapter
                            ? (int)adapter.SubmittedTurnSeq
                            : 0;
                    }
                    _store.SaveSessions();
                    break;
                case TextDeltaMessage m:
                    acc.OnTextDelta(m.Text);
                    // Цикл «до готово»: копим текст хода для поиска маркера завершения
                    if (entry?.Info.WorkLoop is not null)
                        lock (entry.LoopTurnLock) entry.LoopTurnText.Append(m.Text);
                    // Маркеры протокола в живой ленте: копим текст хода и решаем, что из
                    // накопленного уже безопасно показать человеку (волна 6). Целиком
                    // завершённый маркер вырезаем, незавершённый хвост придерживаем до
                    // следующей дельты — иначе полтега мелькнёт на экране раньше, чем мы
                    // поймём, что это протокол, а не текст персоны. Режим штаба ещё и ловит
                    // отсюда маркер эскалации координатора (разбор — на конце хода).
                    // Стрижка идёт в ЛЮБОМ чате, не только в штабе: сохранённая история
                    // чистится безусловно (TurnAccumulator.FlushBuffers), и без этого маркер
                    // (в т.ч. `<no-reply/>` постановщика) мелькал бы в стриме и пропадал
                    // после перезагрузки страницы.
                    if (entry is not null)
                    {
                        string? displayDelta;
                        lock (entry.TeamTurnLock)
                        {
                            entry.TeamTurnText.Append(m.Text);
                            if (!entry.TurnSawAngleBracket && m.Text.Contains('<'))
                                entry.TurnSawAngleBracket = true;
                            if (!entry.TurnSawAngleBracket)
                            {
                                // Быстрый путь обычного чата: без '<' маркеру взяться неоткуда,
                                // стрижка вернула бы тот же текст — не платим за ToString()
                                // всего хода на каждой дельте.
                                entry.TeamTurnShownLength += m.Text.Length;
                                displayDelta = m.Text;
                            }
                            else
                            {
                                var safe = TeamProtocolMarkers.TrimAmbiguousMarkerTail(
                                    TeamProtocolMarkers.TrimUnresolvedMarkerOpen(TeamProtocolMarkers.StripTeamProtocolMarkers(entry.TeamTurnText.ToString())));
                                // Пока в очищенном тексте нет ни одного непробельного символа,
                                // показывать нечего: ход, ответивший ровно маркером, не должен
                                // родить в ленте пустой пузырь из «\n» вокруг маркера. Длину
                                // показанного не двигаем — придержанные пробелы уйдут вместе с
                                // первым настоящим текстом, если он появится.
                                if (safe.Length > entry.TeamTurnShownLength && safe.Trim().Length > 0)
                                {
                                    displayDelta = safe[entry.TeamTurnShownLength..];
                                    entry.TeamTurnShownLength = safe.Length;
                                }
                                else displayDelta = null;
                            }
                            // Всё накопленное показано и ничего не придержано — маркеру в
                            // буфере взяться неоткуда. В обычном чате буфер нужен ровно для
                            // показа, поэтому сжимаем его: иначе каждая следующая дельта
                            // хода с кодом (там '<' на каждом шагу) пересканировала бы весь
                            // ход целиком — квадрат по длине ответа. В штабе так нельзя:
                            // там весь текст хода разбирается на маркеры в его конце.
                            if (entry.Info.TeamImplement is null
                                && entry.TeamTurnShownLength == entry.TeamTurnText.Length)
                            {
                                entry.TeamTurnText.Clear();
                                entry.TeamTurnShownLength = 0;
                                entry.TurnSawAngleBracket = false;
                            }
                        }
                        if (string.IsNullOrEmpty(displayDelta)) sendBroadcast = false;
                        else msg = m with { Text = displayDelta };
                    }
                    break;
                case ThinkingDeltaMessage m: acc.OnThinkingDelta(m.Text); break;
                case AgentTextMessage m:
                    acc.OnAgentText(m.ParentToolUseId, m.Text);
                    await acc.SaveSnapshotAsync(_history); // reload посреди долгого сабагента видит уже написанное
                    break;
                case AgentThinkingMessage m: acc.OnAgentThinking(m.ParentToolUseId, m.Text); break;
                case ToolUseMessage m:
                    // Время старта — по часам сервера и одним числом в ленту и в историю:
                    // иначе таймер карточки после F5 начинал бы отсчёт заново
                    m = m with { StartedAt = m.StartedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    msg = m;
                    acc.OnToolUse(m.Id, m.Name, m.Input, m.ParentToolUseId, m.StartedAt);
                    _worktree.TryUnmarkCommittedOnToolUse(sessionId, entry, m.Name, m.Input);
                    if (entry is not null && SpendMapping.TryExtractHiggsfieldGeneration(m.Name))
                        _costs.RecordHiggsfieldGeneration(entry.Info);
                    break;
                case ToolStartedMessage m:
                    m = m with { StartedAt = m.StartedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    msg = m;
                    acc.OnToolStarted(m.ToolUseId, m.StartedAt.Value);
                    break;
                case ToolResultMessage m:
                    m = m with { FinishedAt = m.FinishedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    msg = m;
                    acc.OnToolResult(m.ToolUseId, m.Content, m.IsError, m.FinishedAt);
                    await acc.SaveSnapshotAsync(_history); // промежуточное сохранение после каждого tool call
                    // Продолженный после рестарта ход дошёл до результата инструмента — это уже не
                    // crash-loop, а живая работа: счётчик auto-resume обнуляем, чтобы СЛЕДУЮЩИЙ
                    // рестарт (вечер 22.10: девять выкаток подряд, долгие ходы исполнителей не
                    // успевали дойти до result) снова мог продолжить этот ход. До первого
                    // tool_result счётчик стоит: ход, падающий сразу после --resume, не зациклится.
                    if (entry is not null && entry.Info.RestartResumeAttempts > 0)
                    {
                        entry.Info.RestartResumeAttempts = 0;
                        _store.SaveSessions();
                    }
                    _costs.TryTrackFalCost(sessionId, m.Content); // fire-and-forget: стоимость придёт позже
                    _costs.TryTrackGlifCost(sessionId, m.Content); // синхронно: кредиты уже в tool_result
                    break;
                case WorkflowProgressMessage m:
                    if (entry is not null)
                    {
                        // Кэш мутируют таймер-потоки ватчеров (несколько workflow = несколько
                        // потоков), читает JoinSession — только под локом
                        lock (entry.WorkflowProgress)
                        {
                            if (m.IsDone) entry.WorkflowProgress.Remove(m.ToolUseId);
                            else entry.WorkflowProgress[m.ToolUseId] = m;
                        }
                    }
                    // Последний снапшот — в историю: карточка workflow и вкладка «Агенты»
                    // должны переживать перезагрузку страницы и рестарт сервера
                    acc.OnWorkflowProgress(m.ToolUseId, m.IsDone, m.Agents);
                    if (m.IsDone) await acc.SaveSnapshotAsync(_history);
                    break;
                case BgAgentDoneMessage m:
                    acc.OnBgAgentsDone(m.ToolUseIds);
                    await acc.SaveSnapshotAsync(_history);
                    // Волна 3 задачи b63fd8ea: хук на завершение фонового async-агента.
                    //
                    // Снимок «есть ли ещё живой async-агент» берётся ПОСЛЕ учёта текущего
                    // сообщения — гонки «читаем старое состояние» нет ни на одном из четырёх
                    // источников BgAgentDoneMessage. Все четыре ДО отправки сообщения уже
                    // убрали завершившуюся задачу из run.PendingBg (а для FinalizeRunAsync —
                    // из всего словаря):
                    //   • HandleStructuredTaskNotification (ClaudeSession.cs:5055) — снимает
                    //     задачу через `lock (run.PendingBg) run.PendingBg.Remove(taskId, ...)`
                    //     синхронно и публикует BgAgentDoneMessage через Task.Run из того же
                    //     пути, что HandleStructuredTaskNotification читает;
                    //   • HandleTaskNotification (ClaudeSession.cs:4954) — то же самое, по
                    //     текстовому <task-notification> вместо структурного события;
                    //   • HandleTaskOutputCompletion (ClaudeSession.cs:4989) — снимает через
                    //     `lock (run.PendingBg) run.PendingBg.Remove(agentId, ...)` и
                    //     публикует через Task.Run;
                    //   • FinalizeRunAsync (ClaudeSession.cs:3706) — единственный путь,
                    //     где публикация идёт inline await (не Task.Run): прогон уже умер,
                    //     `lock (run.PendingBg) { orphanedTools = run.PendingBg.Values...;
                    //     run.PendingBg.Clear(); }` происходит ДО вызова CompleteBgTasksAsync
                    //     и до возврата await, поэтому HasPendingBg к моменту чтения ниже
                    //     уже отражает очищенный словарь.
                    //
                    // Решения (Major + Minor 2, оба на одной проверке HasAsyncAgent):
                    //   • Метка AsyncAgentStallSince сбрасывается ТОЛЬКО когда после
                    //     текущего done агентов больше нет вообще (HasAsyncAgent == false).
                    //     Безусловный сброс (как в волне 2) перезапускал 10-минутный
                    //     потолок подавления на каждом агенте цепочки — суппрессия
                    //     тянулась неограниченно, ровно против того, что баг b63fd8ea
                    //     чинил.
                    //   • Карточка молчаливого тупика поднимается ТОЛЬКО когда (а) агент
                    //     умер абортивно (Aborted=true) и (б) других живых async-агентов
                    //     больше нет (HasAsyncAgent == false). Иначе — структурный
                    //     task_notification ставит Aborted для ОДНОГО агента, пока
                    //     параллельно работает ДРУГОЙ: карточка «Координатор не понял
                    //     вводную» сразу на первом аборте уводила стадию в AwaitingDecision,
                    //     хотя координатор ЖИВ — дословный регресс P16 (Major, найден
                    //     ревью Глеба).
                    //
                    // Карточка публикуется через TeamTurnCompletionService.HandleBgAgentDoneAsync
                    // — та же точка, что HandleTeamTurnEndAsync, и обе сериализуются на
                    // общем локе TeamStateService.WithTeamState через TryClaimSilentStall
                    // (Minor 1, идемпотентность): гонка «оба пути публикуют одновременно»
                    // закрывается атомарным pre-claim — первый путь переводит стадию в
                    // AwaitingDecision под локом, второй видит её и выходит.
                    if (entry is not null)
                    {
                        // Снимок HasAsyncAgent — ПОСЛЕ учёта текущего сообщения (см. шапку):
                        // HandleStructuredTaskNotification (ClaudeSession.cs:5055) уже удалил
                        // задачу из run.PendingBg синхронно до публикации BgAgentDoneMessage,
                        // entry.Process?.HasPendingBg отражает обновлённое состояние.
                        var hasAsync = SessionStore.AsyncAgentInFlight(entry);
                        bool asked;
                        lock (entry.TeamTurnLock)
                        {
                            // Minor 2: метка сбрасывается ТОЛЬКО когда после текущего done
                            // других async-агентов больше нет. Безусловный сброс перезапускал
                            // бы 10-минутный потолок подавления на КАЖДОМ агенте цепочки —
                            // суппрессия тянулась бы неограниченно (ровно то, против чего
                            // баг b63fd8ea был написан).
                            if (!hasAsync && entry.AsyncAgentStallSince is not null)
                                entry.AsyncAgentStallSince = null;
                            asked = entry.TeamTurnAsked;
                        }
                        await _teamNotifier.Value.HandleBgAgentDoneAsync(sessionId, m.Aborted, hasAsync, asked);
                    }
                    break;
                // Присутствие фона — сигнал для СПИСКА чатов, а не для ленты: в историю не
                // пишем (состояние живёт ровно столько, сколько процесс) и статус сессии не
                // трогаем — ApplyStatusAsync двигал бы UpdatedAt, а по нему идут сортировка,
                // секции дерева и непрочитанность. Рассылка — в session + project/user-группы
                case BgAgentsPresenceMessage m:
                    if (entry is not null)
                        await EnqueueWorkAsync(entry, () => _store.BroadcastSessionMessageAsync(sessionId, m));
                    else
                        await _store.BroadcastSessionMessageAsync(sessionId, m);
                    break;
                case FileChangedMessage m:
                    acc.OnFileChanged(m.Path, m.Added, m.Removed, m.External);
                    // External не гейтим: сырое множество индекса содержит и внешние правки
                    // (фильтр «только файлы чата») — пометка снимается любой из них
                    if (entry is { TurnInWorktree: false })
                        _worktree.UnmarkFileCommitted(sessionId, SessionChangedPaths.Normalize(m.Path));
                    break;
                case CompactBoundaryMessage m:
                    acc.OnCompactBoundary(m.Trigger, m.PreTokens, m.PostTokens);
                    await acc.SaveSnapshotAsync(_history); // авто-компакт бывает посреди хода — фиксируем сразу
                    break;
                case AskQuestionMessage m:
                    acc.OnAskQuestion(m.ToolUseId, m.Input);
                    await acc.SaveSnapshotAsync(_history);
                    // Э8: вопрос координатора штаба — раунд интервью. Из волны или ожидания
                    // он же возвращает практику в интервью (волны на паузе, карточка + push).
                    if (entry?.Info.TeamImplement is not null)
                    {
                        // Ход задал вопросы — гард молчаливого тупика по его концу молчит (M9)
                        lock (entry.TeamTurnLock) entry.TeamTurnAsked = true;
                        await _teamNotifier.Value.OnAskQuestionStabAsync(sessionId);
                    }
                    break;
                case PlanReviewMessage m:
                    acc.OnPlanReview(m.RequestId, m.Plan);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case RecallManifestMessage m:
                    if (entry is not null) entry.LastRecallManifest = m;
                    break;
                case PromptSnapshotMessage m:
                    // Привязываем снимок к сообщению, которым начался ход: под ним живёт
                    // кнопка «какой промпт ушёл». Событие приходит ДО result — иначе
                    // FlushAsync уже унёс бы текущий ход из _currentTurn.
                    acc.SetPromptSnapshot(m.SnapshotId);
                    break;
                case ResultMessage m:
                    // Точный якорь границы хода для ветвления (фича chat-branch, §4): uuid
                    // последней записи транскрипта на КОНЕЦ хода. Снимается здесь, потому что
                    // result — последнее событие хода, и дальше файл уже не растёт до
                    // следующего. Не прочиталось — null, чат остаётся на текстовом пути.
                    await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history, m.ContextTokens, m.UsageModel, m.DurationApiMs,
                        entry is not null ? Llm.Claude.TranscriptProbe.LastRecordUuid(_route.FindResumeTranscript(entry)) : null,
                        m.PromptCache);
                    if (entry is not null) entry.LoopTurnFailed = m.Subtype == "error";
                    // Актор читается здесь, пока ход жив: переход статуса по result и снятие
                    // актора идут ниже по методу (ApplyStatusQueuedAsync) — потребитель после
                    // конца хода получил бы null (урок R-S2a-6c).
                    _costs.RecordTurnSpend(entry?.Info, m);
                    break;
                case ProviderSwitchedMessage m:
                    // Пометка автоподмены модели в историю — после F5/рестарта человек видит,
                    // что отвечала не та модель, что был выбран. Уровень 1 (ротация подписок)
                    // модель не трогает — пилюля там не нужна; адаптер шлёт Auto=true без
                    // Model, OnModelSwitched в таком случае no-op.
                    // ErrorDetails — сырой текст погашенной подменой ошибки провайдера: красной
                    // карточки в ленте нет, но текст доступен «Подробностями» внутри пометки,
                    // поэтому кладём его в историю вместе с ней.
                    if (m.Auto && !string.IsNullOrEmpty(m.Model))
                        acc.OnModelSwitched(m.Model, acc.LastStartedModel(), m.Reason, m.ErrorDetails);
                    break;
                case RateLimitMessage m:
                    {
                        // Учёт лимита — единая точка SubscriptionLimitRecorder (её же зовёт шлюз
                        // LLM по заголовкам anthropic-ratelimit-unified-*). Здесь — только реакция
                        // на свой ход. Подавленное событие выходит из обработчика целиком, как и
                        // до выноса: дальше для rate_limit делать нечего.
                        var limit = _costs.LimitRecorder.Record(entry?.Info.Provider, m, "turn",
                            () => entry?.Process is FallbackLlmSessionAdapter fb && fb.FallbackTurnActive,
                            $"ход {sessionId}");
                        if (limit == LimitRecordOutcome.Suppressed)
                            return;
                        if (limit == LimitRecordOutcome.Exhausted && entry is not null)
                        {
                            // Сразу перевозим чат на здоровый аккаунт пула — кнопка «Повторить»
                            // упавшего хода пойдёт уже через него. Если переключиться некуда,
                            // а ход реально отбит — предлагаем сторонний провайдер карточкой.
                            _route.TryPoolFailover(sessionId, entry);
                            if (m.Status == "rejected")
                                await _providerMigration.OfferProviderFallbackAsync(sessionId, m.ResetsAt);
                        }
                    }
                    break;
                case ErrorMessage m:
                    // Сюда доезжают только настоящие провалы: промежуточную ошибку попытки,
                    // за которой пошла подмена, адаптер наружу не выпускает (её текст едет
                    // в ErrorDetails маркера) — значит и LoopTurnFailed на ней не взводится.
                    // Details — сырой техтекст под «Подробностями» карточки.
                    await acc.OnErrorAsync(m.Text, _history, m.Details, m.Action);
                    // Ошибка хода (в т.ч. упавший старт процесса) — цикл «до готово»
                    // не продолжаем; иначе ретрай-шторм до лимита итераций
                    if (entry is not null) entry.LoopTurnFailed = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ошибка аккумулятора ({sessionId}): {ex.Message}");
        }

        // Резолв ожидателя синхронного хода (SendMessageAndWaitAsync): result — штатное
        // завершение, error/exited — обрыв (резолвим тоже, чтобы вызывающий не завис).
        // Обнуляем безусловно — ожидатель не должен утечь в следующий ход.
        if (entry is not null && msg is ResultMessage or ErrorMessage or ExitedMessage
            && Interlocked.Exchange(ref entry.TurnWaiter, null) is { } waiter)
        {
            switch (msg)
            {
                case ResultMessage rm:
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), rm.DurationMs, rm.TotalCostUsd,
                        Succeeded: true));
                    break;
                case ErrorMessage em:
                    // Ошибка хода (протухший логин CLI и пр.) — не штатная реплика
                    waiter.TrySetResult(new TurnResult(em.Text, 0, null, Succeeded: false));
                    break;
                case ExitedMessage:
                    // Прерван без result — отдаём то, что ассистент успел написать (обрубок,
                    // не готовая реплика)
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), 0, null, Succeeded: false));
                    break;
            }
        }

        // Этап 4 / шаг 2в (MAJOR 1 этап 4, доработка швов): бэкстоп
        // `RestoreWaveWatchdogIfPaused` на любом `ExitedMessage` возвращён. Шим
        // `HandleTeamTurnCompletedShim` тоже зовёт его на `interrupted | crashed`,
        // а `HandleTeamTurnEndAsync:7593` — на `success | failed | egress_down |
        // local_down`: всего бэкстоп здесь выглядит избыточным. НО он покрывает случай,
        // когда план в `LastTeamTurnEnds` не нашёлся (вытеснение потолком 8, чужой
        // `TurnSeq`): шим уходит с WARN и без восстановления, и единственный путь
        // сохранить отсечки сторожа — вызвать здесь, не дожидаясь подписчика.
        // Двойной вызов с шимом/HandleTeamTurnEnd идемпотентен
        // (`WaveStartedAt = DateTime.UtcNow` поверх себя), контракт «один исход —
        // одна публикация» шины не нарушается: бэкстоп жёстко локальный и срабатывает
        // только после того, как downstream получил `ExitedMessage`.
        if (msg is ExitedMessage && entry is not null)
            _teamNotifier.Value.RestoreWaveWatchdogIfPaused(sessionId);

        // Обновление статуса — всегда, независимо от аккумулятора; SessionManager —
        // ЕДИНСТВЕННЫЙ владелец переходов Session.Status (ClaudeSession статус не пишет).
        // Если OnResultAsync выбросит, статус всё равно обновится.
        if (entry is not null)
        {
            SessionStatus? newStatus = null;

            if (msg is PermissionRequestMessage or AskQuestionMessage or PlanReviewMessage)
            {
                // Вторая карточка поверх висящей наследует признак первой; иначе свободный
                // (не Working) чат = карточка пришла между ходами
                if (entry.Info.Status is not SessionStatus.Waiting)
                    entry.InteractionOutsideTurn = entry.Info.Status is not SessionStatus.Working;
                newStatus = SessionStatus.Waiting;
                // Кэш для replay при JoinSession: после F5 клиент должен снова увидеть
                // карточку, которую ждёт CLI
                entry.PendingInteraction = msg;
            }
            else if (msg is ResultMessage rm)
            {
                // Active (не Finished): клиент по active перезагружает историю хода;
                // финальный Finished выставится по ExitedMessage ниже
                newStatus = rm.Subtype == "error" ? SessionStatus.Error : SessionStatus.Active;
                // Result хода: сбрасываем счётчик auto-resume (B2). Поле служебное — UpdatedAt
                // не двигаем сами (сдвинет ApplyStatusAsync ниже, как при любой смене статуса).
                // Персистится тем же SaveSessions внутри ApplyStatusAsync.
                entry.Info.RestartResumeAttempts = 0;
            }
            else if (msg is ErrorMessage)
                newStatus = SessionStatus.Error;
            else if (msg is ExitedMessage)
                newStatus = entry.Info.Status switch
                {
                    // прерван без result — возвращаем в рабочее состояние
                    SessionStatus.Working or SessionStatus.Waiting => SessionStatus.Active,
                    // ход завершился штатно (result уже перевёл в Active) — фиксируем Finished
                    SessionStatus.Active => SessionStatus.Finished,
                    _ => null,
                };

            // Штатная остановка (ADR-033): обрывы (error/exited умирающего процесса) статус не
            // меняют — sessions.json сохранит Working, и LoadSessions отберёт сессию в кандидаты
            // на auto-resume. Working живёт на диске только до следующего старта — там LoadSessions
            // сразу переводит его в Orphaned. result хода пишется как обычно: ход честно
            // завершился, и «продолжать» его после рестарта нельзя.
            if (_turnStarter.Value.IsStopping && msg is ErrorMessage or ExitedMessage)
                newStatus = null;

            // Конец хода/обрыв — ожидающей карточки больше нет
            if (msg is ResultMessage or ErrorMessage or ExitedMessage)
                entry.PendingInteraction = null;

            if (newStatus.HasValue)
            {
                // P12/P15: отметим момент, когда статус стал Active — sweep в SaveSessions по этой
                // метке доведёт до Finished, если exited прогона не придёт (доживающий прогон при
                // живых фоновых агентах до BgLingerTimeout, подавленный SuppressExited, висящий без
                // работы процесс). Любой иной статус (Working/Waiting — идёт ход/карточка; Finished/
                // Error — терминальный) — «завершённого Active» нет, метку гасим. Под PendingLock:
                // struct DateTimeOffset? читает sweep под тем же локом; lock короткий, без await.
                lock (entry.PendingLock)
                    entry.LastTurnEndedAt = newStatus == SessionStatus.Active ? DateTimeOffset.UtcNow : null;
                // exited — та же доводка, что у sweep: содержимое хода пришло раньше
                // (result/сообщения), а обрыв без result его не добавляет
                // Состояние и SaveSessions — здесь же; рассылка смены статуса уходит в очередь
                // (раньше await на SignalR держал read-loop), порядок «статус → сообщение» сохранён
                await ApplyStatusQueuedAsync(sessionId, entry, newStatus.Value,
                    touchUpdatedAt: msg is not ExitedMessage);
            }

            // Цикл «до готово»: решение о продолжении — по result/error хода, нёсшего
            // протокол цикла (LoopTurnInFlight). Раньше триггером был exited, но после
            // механики доживания exited приходит лишь со смертью прогона — цикл замирал
            // на десятки минут при живых фоновых агентах.
            // В фоне, чтобы не блокировать read-loop адаптера пересозданием процесса.
            // Режим «Командная реализация»: ход штаба закончился — разбираем его маркеры
            // (эскалация, новая работа) и переводим завершённую проверку в ожидание вводной.
            // В фоне по той же причине, что и цикл «до готово»: публикация карточки,
            // уведомление и планирование не должны держать read-loop адаптера.
            // Буфер стрижки маркеров потребляем в ЛЮБОМ чате (он и копится везде): ход
            // закончился — ждать больше нечего, и то, что живая трансляция придерживала как
            // «вдруг это начало маркера» (TrimAmbiguousMarkerTail), дальше не дорастёт ни во
            // что — довешиваем как обычный текст и обнуляем буфер под следующий ход.
            var turnText = "";
            var turnAsked = false;
            if (msg is ResultMessage or ErrorMessage)
            {
                string? catchUpDelta;
                lock (entry.TeamTurnLock)
                {
                    turnText = entry.TeamTurnText.ToString();
                    var finalSafe = TeamProtocolMarkers.StripTeamProtocolMarkers(turnText);
                    // Тот же гард, что в живой трансляции: ход, ответивший ровно маркером,
                    // не должен догнать ленту пробелами вокруг вырезанного маркера.
                    catchUpDelta = finalSafe.Length > entry.TeamTurnShownLength && finalSafe.Trim().Length > 0
                        ? finalSafe[entry.TeamTurnShownLength..] : null;
                    entry.TeamTurnShownLength = 0;
                    entry.TeamTurnText.Clear();
                    entry.TurnSawAngleBracket = false;
                    turnAsked = entry.TeamTurnAsked;
                    entry.TeamTurnAsked = false;
                    // Этап 4 / шаг 1в: текст и план вызова HandleTeamTurnEndAsync кладутся в LastTeamTurnEnds
                    // ниже отдельным вызовом RecordTeamTurnEnd (эта функция сама берёт
                    // TeamTurnLock через реентрантность lock()). Здесь — только осушение буфера.
                }
                if (!string.IsNullOrEmpty(catchUpDelta))
                {
                    // Догоняющая дельта — единственная порция придержанного текста: слить её нельзя
                    var catchUp = new TextDeltaMessage(catchUpDelta);
                    await EnqueueWorkAsync(entry, () => _store.BroadcastAsync(sessionId, catchUp));
                }
            }

            // При поднятом Stopping план штабу не кладём: разбор хода (HandleTeamTurnEndAsync)
            // планирует новую работу, а процессы уже гасятся. Подписчик turn/completed без плана
            // только пишет предупреждение; состояние штаба остаётся на диске как есть.
            if (!_turnStarter.Value.IsStopping && msg is ResultMessage or ErrorMessage && entry.Info.TeamImplement is not null)
            {
                // Этап 4 / шаг 1в: кладём план вызова HandleTeamTurnEndAsync в LastTeamTurnEnds.
                // Подписчик turn/completed изымает план и асинхронно зовёт HandleTeamTurnEndAsync
                // ровно один раз на ход. Дедуп SkipNextTeamTurnEnd, стоявший здесь прежде, ушёл
                // вместе с переключением: первая запись по ключу TurnSeq выигрывает (двойной
                // терминал ErrorMessage{ExpectResultFollows=true} + ResultMessage того же хода
                // даёт один план, второй no-op), а шина гарантирует единственность публикации
                // turn/completed на ход — на этом держится защита от потери маркера.
                var teamTurnFailed = msg is ErrorMessage or ResultMessage { Subtype: "error" };
                entry.RecordTeamTurnEnd(entry.LastTurnSeq, turnText, teamTurnFailed, turnAsked);
            }

            // Сабагент этого хода оборвался на середине — добиваем его продолжением. Строго по
            // result (ход в процесс уже не идёт): systemDirective-отправка очередь не проходит
            // и запустила бы второй ход параллельно живому.
            //
            // ПОРЯДОК ВАЖЕН: этот блок стоит ПЕРЕД разбором цикла «до готово» ниже. Цикл поднимает
            // следующий ход из Task.Run, и пометка об обрыве (TruncatedBgNote) обязана лечь раньше —
            // иначе она опаздывает ровно на один ход, а на последней итерации цикла теряется совсем.
            // Перенос сделан поведенчески нейтральным: раньше блок стоял ПОСЛЕ сброса
            // LoopTurnInFlight и потому всегда видел флаг ложным — здесь он ещё взведён, поэтому
            // в ShouldNudgeSubagent идёт «ход цикла реально продолжится» (флаг И живой WorkLoop),
            // а не сырой флаг. Иначе снятый посреди хода цикл (WorkLoop=null при взведённом флаге)
            // потерял бы добивание, которое получал до переноса.
            // Ошибочный ход добивать нечем —
            // сначала разбирается ошибка, поэтому ErrorMessage только гасит отметку.
            if (msg is ResultMessage or ErrorMessage && entry.TruncatedSubagent is { } cutAgent)
            {
                entry.TruncatedSubagent = null;
                // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
                if (SubagentNudgePolicy.StartsNudgeSeries(entry.NudgeAgentId, cutAgent.AgentId)) entry.SubagentNudges = 0;
                if (msg is ResultMessage && SubagentNudgePolicy.ShouldNudgeSubagent(entry.SubagentNudges,
                        entry.Info.WorkLoop is not null, entry.Info.TeamImplement is not null,
                        PendingQueueService.HasPending(entry), entry.LoopTurnInFlight && entry.Info.WorkLoop is not null,
                        isInterruptedRun: cutAgent.FinishedBy == "interrupted"))
                {
                    entry.NudgeAgentId = cutAgent.AgentId;
                    var attempt = ++entry.SubagentNudges;
                    _ = Task.Run(async () =>
                    {
                        try { await _workLoop.Value.NudgeTruncatedSubagentAsync(sessionId, cutAgent, attempt); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Добивание сабагента ({sessionId}): {ex.Message}");
                        }
                    });
                }
                else
                {
                    // Добивать не стали (уступили циклу «до готово»/штабу, очередь непуста, потолок
                    // исчерпан, ход ошибочный) — но МОЛЧАТЬ об обрыве нельзя: координатор уже принял
                    // последнюю реплику агента за его итог. Раньше отметка здесь просто гасилась, и в
                    // чате с активным циклом обрыв не оставлял следа вообще — ни добивания, ни
                    // предупреждения (инцидент 25.08.2026, чат «Зависание чатов практики»: обрыв
                    // заметил сам координатор, потому что обрывок случайно не был похож на отчёт).
                    // Пометка уедет префиксом ближайшего хода — а его цикл и штаб поднимут сами,
                    // это и есть их «свой протокол продолжения», ради которого им уступает добивание.
                    entry.TruncatedBgNote = cutAgent;
                }
            }

            if (msg is ResultMessage or ErrorMessage && entry.LoopTurnInFlight)
            {
                entry.LoopTurnInFlight = false;
                if (entry.Info.WorkLoop is not null)
                    _ = Task.Run(async () =>
                    {
                        try { await _workLoop.Value.ContinueWorkLoopAsync(sessionId); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Цикл «до готово» ({sessionId}): {ex.Message}");
                        }
                    });
            }

            // Ход закончился — выпускаем следующее сообщение из очереди (и агентские chats_send,
            // и пользовательские «честной очереди»). По одному за раз: следующее уйдёт по result
            // уже этого хода. Цикл «до готово» приоритетнее агентских — он продолжает работу,
            // начатую человеком, и его директива уже поставлена выше; замороженная «Стоп»
            // очередь Drain'ом не трогается (проверка внутри). Ход, прерванный ради очереди
            // (enqueue + interrupt в SendMessageAsync/SendMessageAndWaitAsync), result не шлёт —
            // процесс убит: его очередь разбирает exited ТОГО ЖЕ прогона (DrainOnExitedRun).
            // Привязка к прогону обязательна: exited доживающего чужого прогона приходит с
            // опозданием до ~30 мин и увёл бы сообщение в умирающий от interrupt адаптер.
            // На штатном конце хода метка гасится — иначе поздний exited этого же прогона
            // доставил бы второе сообщение параллельно уже запущенному ходу. В фоне — как и
            // work-loop, чтобы не держать read-loop адаптера.
            // Компакт-ход кончился (штатно или обрывом) — метка своё отработала. Гейт по
            // прогону обязателен, как у DrainOnExitedRun ниже: exited чужого доживающего
            // прогона (опаздывает до ~30 мин) иначе снял бы защиту с ЖИВОЙ компакции, и
            // PreemptForPending убил бы её — ровно тот вечный Working, ради которого метка и есть.
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && runId != 0 && entry.CompactRun == runId)
                entry.CompactRun = 0;

            var drainOnExited = msg is ExitedMessage && runId != 0 && entry.DrainOnExitedRun == runId;
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && entry.DrainOnExitedRun == runId)
                entry.DrainOnExitedRun = 0;
            // Прогон умер без result (кнопка «Стоп», смерть процесса, ватчдог), а очередь
            // непуста. Раньше эту дыру закрывала метка: её ставила КАЖДАЯ постановка
            // пользовательского сообщения, потому что она же и убивала ход. Теперь обычная
            // отправка ход не трогает, и без разбора здесь сообщение висело бы призраком до
            // следующей отправки (ходовой случай: «Стоп» → сразу отправить исправленный текст,
            // пока exited убитого хода ещё в пути). Гейты: свой прогон (поздний exited чужого
            // доживающего очередь не трогает), у адаптера нет живого прогона CLI и очередь не
            // заморожена — заморозку «Стоп» снимает только возобновление пользователем.
            //
            // Смотрим ИМЕННО HasLiveTurn, не статус и не Busy — оба здесь ложны:
            //  • статус exited уже сбросил Working→Active выше по методу (ApplyStatusAsync);
            //  • Busy включает OrchestrationActive, а боевой адаптер — всегда обёртка
            //    FallbackLlmSessionAdapter, которая отдаёт придержанный exited вниз из
            //    SettleAsync и обнуляет _turn лишь в finally ПОСЛЕ нас: гейт по Busy не
            //    срабатывал бы никогда (найдено на ревью).
            // ClaudeSession обнуляет _run ДО отправки exited, поэтому у мёртвого прогона
            // HasLiveTurn=false, а при уже запущенном следующем ходе — true (второй ход
            // параллельно не уйдёт). Если разбор всё же попадёт в окно живой оркестрации,
            // доставка не потеряется: SendMessageAsync обёртки вернут ход в Pending через
            // EnqueueBypass, а её finally добьёт разбор сигналом _orchestrationDone. Известное
            // ограничение этой страховки: EnqueueBypassTurn кладёт ход обратно как агентский и
            // теряет Mode — то есть пользовательское сообщение, попавшее в это редкое окно,
            // доедет пузырём «Автоматически» и без своего режима.
            // Дубль с разбором по метке безопасен: DrainInFlight пропустит второй заход.
            var drainOnDeadRun = msg is ExitedMessage && runId != 0 && runId == entry.RunId
                && !entry.QueueFrozen && entry.Process is not { HasLiveTurn: true } && PendingQueueService.HasPending(entry);
            if (drainOnExited || drainOnDeadRun
                || (msg is ResultMessage or ErrorMessage && !entry.LoopTurnInFlight
                    && (entry.Info.WorkLoop is null || PendingQueueService.HasContinuingPending(entry))))
            {
                _ = Task.Run(async () =>
                {
                    try { await _turnStarter.Value.DrainNextPendingAsync(sessionId); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Разбор очереди сообщений ({sessionId}): {ex.Message}");
                    }
                });
            }
        }

        // Карточка в ленте — старт MCP-вызова, обогнавший её рассылку, уходит следом.
        // Вычисляется здесь, синхронно: аккумулятор принадлежит порядку чтения stdout
        ToolStartedMessage? earlyStarted = msg is ToolUseMessage announced
            && acc.OnToolAnnounced(announced.Id) is { } earlyStart
            ? new ToolStartedMessage(announced.Id, earlyStart) : null;

        if (entry is null)
        {
            // Записи сессии нет (удалена) — наблюдателей у кадра не было и раньше
            if (sendBroadcast) await _store.BroadcastAsync(sessionId, msg);
            if (earlyStarted is not null) await _store.BroadcastAsync(sessionId, earlyStarted);
            return;
        }

        // Дальше рассылку и наблюдателей ведёт потребитель очереди сессии, read-loop свободен
        var broadcastMsg = sendBroadcast;
        await GetPipeline(entry).EnqueueAsync(new PipelineItem(
            async () =>
            {
                if (broadcastMsg) await _store.BroadcastAsync(sessionId, msg);
                if (earlyStarted is not null) await _store.BroadcastAsync(sessionId, earlyStarted);
            },
            msg, entry.Info,
            Droppable: SessionMessagePipeline.IsDroppable(msg),
            EnqueuedAt: Stopwatch.GetTimestamp(),
            // Слитой дельте очередь пересобирает рассылку по новому тексту (кадр без показа не рассылается)
            WorkFor: broadcastMsg ? merged => () => _store.BroadcastAsync(sessionId, merged) : null));
    }

    private SessionMessagePipeline GetPipeline(SessionEntry entry) =>
        LazyInitializer.EnsureInitialized(ref entry.Pipeline,
            () => new SessionMessagePipeline(item => ProcessPipelineItemAsync(entry, item),
                sessionId: entry.Info.Id, sessionKind: () => entry.Info.TaskId is null ? "human" : "executor",
                log: _log));

    // Работа без сообщения для наблюдателей (догоняющая дельта, presence, смена статуса)
    private ValueTask EnqueueWorkAsync(SessionEntry entry, Func<Task> work) =>
        GetPipeline(entry).EnqueueAsync(new PipelineItem(work, null, null, false, Stopwatch.GetTimestamp()));

    // Единственный потребитель очереди сессии: рассылка, затем наблюдатели по порядку
    private async Task ProcessPipelineItemAsync(SessionEntry entry, PipelineItem item)
    {
        var sessionId = entry.Info.Id;
        var kind = entry.Info.TaskId is null ? "human" : "executor";
        ServerMetrics.RecordSessionQueueWait(Stopwatch.GetElapsedTime(item.EnqueuedAt).TotalMilliseconds, kind);

        if (item.Work is { } work)
        {
            try { await work(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Ошибка рассылки сообщения сессии ({sessionId}): {ex.Message}");
            }
        }

        if (item.Msg is null || item.Info is null || OnSessionMessage is not { } observers) return;
        // Multicast Func<> await-ит только Task ПОСЛЕДНЕГО подписчика — ждём всех явно,
        // иначе исключения и незавершённая работа не-последних наблюдателей теряются
        foreach (var observer in observers.GetInvocationList().Cast<Func<Session, ServerMessage, Task>>())
        {
            var started = Stopwatch.GetTimestamp();
            try { await observer(item.Info, item.Msg); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Ошибка наблюдателя сессии ({sessionId}): {ex.Message}");
            }
            ServerMetrics.RecordSessionObserverDuration(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                observer.Method.DeclaringType?.Name ?? observer.Target?.GetType().Name ?? "unknown", kind);
        }
    }

    // То же, что ApplyStatusAsync (её обёртка остаётся в фасаде — её зовут тела приёма хода),
    // но рассылка смены статуса идёт через очередь сессии: состояние и запись sessions.json
    // остаются синхронными, read-loop не ждёт SignalR
    private async Task ApplyStatusQueuedAsync(string sessionId, SessionEntry entry, SessionStatus status,
        bool touchUpdatedAt = true)
    {
        entry.Info.Status = status;
        if (touchUpdatedAt && !entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        _store.SaveSessions();
        var lastMessage = entry.Info.LastMessage;
        var messageCount = entry.Info.MessageCount;
        await EnqueueWorkAsync(entry,
            () => _store.BroadcastStatusChangeAsync(sessionId, entry.Info, status, lastMessage, messageCount));
    }

    // Текст последней реплики ассистента текущего хода (сообщения после baseline) —
    // ответ для синхронного ожидателя SendMessageAndWaitAsync
    private static string LastAssistantText(TurnAccumulator acc, int baseline) =>
        acc.GetAll().Skip(Math.Max(0, baseline)).OfType<StoredTextMessage>()
            .LastOrDefault(t => t.ParentToolUseId is null)?.Text ?? "";

    // Подготовленная ветвлением пара «{csid}.jsonl + data/sessions/{csid}» осиротела: CLI на
    // первом ходе ветки выдал другой session_id, и подложенный транскрипт уже никто не
    // прочитает (история с этого момента пишется под новым ключом). Гейт общего разговора —
    // тот же, что в DeleteAsync: пока на csid ссылается другой чат (двойник, созданный с тем
    // же resumeSessionId), не трогаем ни историю, ни транскрипт. Сам чат-ветка под этот гейт
    // уже не попадает — его ClaudeSessionId к этому моменту переписан на пришедший от CLI.
    // Best-effort: уборка не должна ронять ход, все сбои — в лог (внутри DeleteTranscript).
    // Единственный вызов — case SessionStartedMessage выше; в плане (§1) метод относился к
    // CliRoute, но там он добавил бы одиннадцатую зависимость (ChatHistoryService), а здесь
    // пользуется только существующими (стор, история, маршрут).
    private void CleanupOrphanBranchTranscript(Session info, string orphanCsid)
    {
        if (_store.Sessions.Values.Any(e => e.Info.ClaudeSessionId == orphanCsid))
        {
            _log.LogInformation(
                "Ветка {SessionId}: CLI сменил сессию на другую, но подготовленный {Csid} оставлен — на него ссылается другой чат",
                info.Id, orphanCsid);
            return;
        }
        _log.LogInformation(
            "Ветка {SessionId}: CLI выдал свой session_id — убираем осиротевшую подготовленную сессию {Csid}",
            info.Id, orphanCsid);
        try { _history.Delete(orphanCsid); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Историю осиротевшей сессии {Csid} убрать не удалось", orphanCsid);
        }
        _route.DeleteTranscript(info, orphanCsid);
    }
}
