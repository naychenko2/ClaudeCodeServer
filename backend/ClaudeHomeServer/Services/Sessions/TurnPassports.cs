using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Sessions;

// Подписчики шины событий хода (ADR-013) и паспорта: снимок промпта → PromptSnapshotStore,
// паспорт сабагента → SubagentRunLog + side-effects на сессии, паспорт хода → TurnRunLog
// (ровно один источник записи). Вынесено из SessionManager (S3-4,
// docs/research/session-manager-split-2026-11.md): тела перенесены дословно, подписки на
// шину ставит этот конструктор — тот же экземпляр шины, что у фасада (синглтон DI; в тестах
// без DI фасад лениво создаёт свой и передаёт его сюда, как раньше).
//
// Контракт тот же (запись в стор + side-effects на сессии), но заводится ОДИН раз на инстанс
// — без привязки к конкретной сессии, потому что id сессии едет В СОБЫТИИ (TurnContext).
//
// Состав:
//
//   PromptAssembled      → PromptSnapshotStore (запись черновика / дозапись tools/mcp)
//                          + McpStatusStore (статусы MCP из system/init, попутно с AttachCliLayer).
//   SubagentRunCompleted → SubagentRunLog (запись паспорта) + side-effects на сессии
//                          (TruncatedSubagent/TruncatedBgNote, сброс счётчика добиваний).
//   TurnCompleted        → TurnRunLog (запись паспорта хода; в finally фолбэк-цикла больше
//                          НЕТ прямого вызова Record — это и есть тот самый «ровно один
//                          источник», который задача фиксирует как инвариант).
//
// Подписчик turn/completed для штаба (HandleTeamTurnCompletedShim) остаётся на фасаде: он
// относится к разбору хода штаба, а не к паспортам (S3-5/S3-6).
//
// Зависимостей восемь: bus, promptSnapshots, mcpStatus, subagentRuns, turnRuns, store,
// Lazy<IWorkLoopContinuation> (шов «паспорт → продолжение цикла»: добивание сабагента просит
// продолжение через фасад, а прямая зависимость замкнула бы цикл «паспорт → приём → паспорт»;
// S3-7: с общего шва ITurnStarter точка снята — добивание ход не стартует само), реестр
// контрибьюторов секций промпта.
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// конструктор internal (как у TurnCompletion): параметр держит internal-шов IWorkLoopContinuation
// (CS0051), DI — factory-лямбда в AddSessionCore, вне сборки этот конструктор не зовёт никто;
// обход фасада закрывает сторож формы ядра (правило 4: на типы Services.Sessions ссылаются лишь
// SessionManager, сами Services.Sessions и тесты).
public sealed class TurnPassports
{
    private readonly PromptSnapshotStore? _promptSnapshots;
    private readonly Mcp.McpStatusStore? _mcpStatus;
    private readonly Llm.Claude.SubagentRunLog? _subagentRuns;
    private readonly Llm.TurnRunLog? _turnRuns;
    private readonly SessionStore _store;
    private readonly Lazy<IWorkLoopContinuation> _workLoop;
    private readonly ITurnEventBus _bus;

    internal TurnPassports(ITurnEventBus bus, PromptSnapshotStore? promptSnapshots,
        Mcp.McpStatusStore? mcpStatus, Llm.Claude.SubagentRunLog? subagentRuns, Llm.TurnRunLog? turnRuns,
        SessionStore store, Lazy<IWorkLoopContinuation> workLoop,
        IEnumerable<IPromptSectionContributor>? promptSectionContributors)
    {
        _promptSnapshots = promptSnapshots;
        _mcpStatus = mcpStatus;
        _subagentRuns = subagentRuns;
        _turnRuns = turnRuns;
        _store = store;
        _workLoop = workLoop;
        _bus = bus;

        // Подписки на шину событий хода (ADR-013, этап 1): снимок промпта → PromptSnapshotStore,
        // паспорт сабагента → SubagentRunLog + side-effects, паспорт хода → TurnRunLog.
        // Шина регистрируется через DI как singleton; в тестах без DI лениво создаётся
        // свой экземпляр на SessionManager (TurnEvents), чтобы подписчики могли работать.
        _bus.OnNotification<PromptAssembled>(HandlePromptAssembled, "TurnPassports.PromptSnapshotStore");
        _bus.OnNotification<SubagentRunCompleted>(HandleSubagentRunCompleted, "TurnPassports.SubagentRunLog");
        _bus.OnNotification<TurnCompleted>(HandleTurnCompleted, "TurnPassports.TurnRunLog");

        // Этап 2: реестр IPromptSectionContributor (7 контрибьюторов)
        // подключается к шине Filter-событием prompt/assembling. В тестах без DI бак
        // пуст — сборка секций остаётся на инлайне (он закрыт гейтами IsEnabled).
        if (promptSectionContributors is not null)
            PromptSectionContributorsRegistration.RegisterAll(_bus, promptSectionContributors);
    }

    private System.Collections.Concurrent.ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;
    private string? ResolveOwnerId(Session s) => _store.ResolveOwnerId(s);
    private static bool HasPending(SessionEntry entry) => PendingQueueService.HasPending(entry);

    // Приёмник паспортов сабагента через шину: сюда стекается тестовый код через рефлексию
    // (раньше этим путём ходил настоящий sink для ватчера сабагентов; сейчас ватчер публикует
    // subagent/completed сам, а этот метод — синхронный мост через шину для unit-тестов,
    // которые проверяют side-effects на сессии непосредственно после emit). Синхронный мост
    // здесь ОК: подписчик шины пишет в стор и взводит флаги, оба эти действия идемпотентны
    // и не могут зациклиться. null — шины нет (тесты без SessionManager), ватчер сам бы
    // отказался публиковать.
    internal Action<Llm.SubagentRunPassport>? SubagentRunSinkFor(string sessionId)
    {
        return passport =>
        {
            // Синхронный мост: тесты берут делегат через рефлексию и зовут его из тестового
            // метода, который потом немедленно проверяет side-effects на entry. В проде никто
            // этим методом не пользуется — ватчер публикует subagent/completed в свой ITurnEventBus.
            _bus.PublishAsync(new SubagentRunCompleted(
                Turn: new TurnContext(SessionId: sessionId, OwnerId: null, TurnSeq: 0,
                    AgentDepth: 0, ProjectId: null),
                Passport: passport)).GetAwaiter().GetResult();
        };
    }

    private Task HandlePromptAssembled(PromptAssembled e)
    {
        var snap = e.Snapshot;
        if (snap is null) return Task.CompletedTask; // событие не наш — большинство подписчиков его не носят
        switch (snap.Phase)
        {
            case PromptSnapshotPhase.Draft:
                if (_promptSnapshots is null || snap.Draft is null) return Task.CompletedTask;
                // ЧЕРНОВИК идёт в стор с id, который ClaudeSession уже сгенерировал и положил
                // в payload — тот же id едет в UI-кнопку «какой промпт ушёл» через _onMessage.
                // Счётчик общий с PromptSnapshotStore.NewPublicId, и без записи в стор id
                // всё равно бесполезен.
                SafePromptSnapshotDraft(e.Turn.SessionId, snap.SnapshotId, snap.Draft);
                break;
            case PromptSnapshotPhase.Tools:
                if ((_promptSnapshots is null && _mcpStatus is null) || snap.SnapshotId is null) return Task.CompletedTask;
                SafePromptSnapshotAttach(e.Turn.SessionId, snap.SnapshotId,
                    snap.ToolNames ?? [], snap.McpServers ?? []);
                break;
        }
        return Task.CompletedTask;
    }

    // Все ошибочные ветки возвращают Task.CompletedTask: подписчик Notification не должен
    // бросать наружу — шина гасит исключения, но мы и сами не плодим трейс ради диагностики.
    private void SafePromptSnapshotDraft(string sessionId, string? snapshotId, PromptSnapshotDraft draft)
    {
        if (snapshotId is null) { _promptSnapshots?.Save(sessionId, draft); return; }
        try { _promptSnapshots?.Save(sessionId, snapshotId, draft); }
        catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Снимок промпта не записан: {ex.Message}"); }
    }

    private void SafePromptSnapshotAttach(string sessionId, string snapshotId,
        IReadOnlyList<string> tools, IReadOnlyList<McpServerInfo> servers)
    {
        try
        {
            _promptSnapshots?.AttachCliLayer(sessionId, snapshotId, tools, servers);
            if (_mcpStatus is null || servers.Count == 0) return;
            if (_sessions.TryGetValue(sessionId, out var entry)
                && ResolveOwnerId(entry.Info) is { } ownerId)
                _mcpStatus.RecordFromInit(ownerId, sessionId, servers);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Дозапись снимка промпта не удалась: {ex.Message}");
        }
    }

    private Task HandleSubagentRunCompleted(SubagentRunCompleted e)
    {
        var sessionId = e.Turn.SessionId;
        var passport = e.Passport;
        if (_subagentRuns is not null) _subagentRuns.Record(passport);
        if (!_sessions.TryGetValue(sessionId, out var entry)) return Task.CompletedTask;
        if (passport.Truncated && passport.FinishedBy != "interrupted")
        {
            entry.TruncatedSubagent = passport;
            // Фоновый агент: продукт ТОЛЬКО ЧТО объявил его результат готовым посреди хода
            // координатора (bg_agent_done), и координатор принял обрывок последней реплики
            // за итог. Ждать result здесь нельзя: ход координатора не заканчивается, а сам
            // фоновый агент часто дозавершается уже ПОСЛЕ конца хода — тогда отметку не
            // разбирает никто и чат стоит до сообщения человека (ровно то, что видно в логе:
            // у исполнителей задач добивание срабатывало, в обычном чате — ни разу).
            if (passport.FinishedInBackground) NoteTruncatedBgAgent(sessionId, entry, passport);
        }
        else
        {
            // Опровержение обрыва: сигнал bg_agent_done обгоняет дозапись финального отчёта
            // в транскрипт, и пометка могла взвеститься по хвосту tool_use агента, который
            // на деле дописал end_turn. Штатный отчёт гасит ТОЛЬКО СВОЮ пометку — иначе
            // в чат уходит ложная директива добивания давно завершившегося агента, а чужая
            // пометка (другой AgentId) ждёт отчёта своего агента.
            if (SubagentNudgePolicy.RefutesTruncation(entry.TruncatedSubagent?.AgentId, passport.AgentId))
                entry.TruncatedSubagent = null;
            if (SubagentNudgePolicy.RefutesTruncation(entry.TruncatedBgNote?.AgentId, passport.AgentId))
                entry.TruncatedBgNote = null;
            // Агент, доложившийся штатно, снимает счётчик добиваний: потолок в две попытки —
            // на серию подряд, а не на всю жизнь чата. Но снимает ТОЛЬКО СВОЙ счётчик: в ходе
            // работают несколько агентов, и штатный отчёт соседа не значит, что оборвавшегося
            // добили — иначе потолок не достигается никогда (добивание уходит с attempt=1 по кругу).
            if (SubagentNudgePolicy.ResetsNudgeSeries(entry.NudgeAgentId, passport.AgentId))
            {
                entry.SubagentNudges = 0;
                entry.NudgeAgentId = null;
            }
        }
        return Task.CompletedTask;
    }

    private Task HandleTurnCompleted(TurnCompleted e)
    {
        // Запись TurnRunLog — РОВНО ОДИН источник (CLAUDE.md, раздел LLM-провайдеры). До
        // переезда на шину источником был finally-блок FallbackLlmSessionAdapter; теперь
        // подписчик здесь, а finally публикует событие. Подписчик шины не должен бросать,
        // но try всё равно — запись НЕ бросает даже при сбое файла (см. TurnRunLog), try тут
        // для понятного журнала, если в сторе что-то поломается.
        if (e.Passport is null) return Task.CompletedTask;
        try { _turnRuns?.Record(e.Passport); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Паспорт хода не записан ({e.Turn.SessionId}): {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Обрыв ФОНОВОГО агента: пометка координатору обязательна всегда, добивание — только
    /// если ход координатора не идёт (иначе ждём result, как раньше).
    /// </summary>
    private void NoteTruncatedBgAgent(string sessionId, SessionEntry entry,
        Llm.SubagentRunPassport run)
    {
        // Пометка уедет префиксом ближайшего хода — чем бы он ни был поднят (человеком,
        // очередью, добиванием): координатор обязан узнать, что обрывок не итог, даже когда
        // добивать мы не стали.
        entry.TruncatedBgNote = run;

        if (entry.Process is null || SubagentNudgePolicy.TurnInFlight(entry.Info.Status)) return;
        // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
        if (SubagentNudgePolicy.StartsNudgeSeries(entry.NudgeAgentId, run.AgentId)) entry.SubagentNudges = 0;
        if (!SubagentNudgePolicy.ShouldNudgeSubagent(entry.SubagentNudges, entry.Info.WorkLoop is not null,
                entry.Info.TeamImplement is not null, HasPending(entry), entry.LoopTurnInFlight,
                isInterruptedRun: run.FinishedBy == "interrupted")) return;

        // Разбираем отметку здесь — иначе по ближайшему result добивание ушло бы вторым разом
        entry.TruncatedSubagent = null;
        entry.NudgeAgentId = run.AgentId;
        var attempt = ++entry.SubagentNudges;
        _ = Task.Run(async () =>
        {
            try
            {
                // Ход мог стартовать, пока планировалась отправка (очередь, автоматизация,
                // цикл): откатываем попытку и возвращаем отметку — добивание уедет по его
                // result, штатным путём. Второй ход в живой процесс не отправляем никогда.
                if (SubagentNudgePolicy.TurnInFlight(entry.Info.Status))
                {
                    entry.SubagentNudges = Math.Max(0, entry.SubagentNudges - 1);
                    entry.TruncatedSubagent = run;
                    return;
                }
                await _workLoop.Value.NudgeTruncatedSubagentAsync(sessionId, run, attempt);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Добивание фонового сабагента ({sessionId}): {ex.Message}");
            }
        });
    }
}
