using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Team;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Стор истории чата (data/sessions/{csid}/history.json): ключ истории, общий лок внеходовых
// записей (_falPersistLock), дедуп-запись, чтение ленты и карточек, правка карточек на диске.
// Вынесен из SessionManager (S3-2, docs/research/session-manager-split-2026-11.md §1): тела
// перенесены дословно, SessionManager держит делегирующий фасад. GetHistoryAsync здесь — БЕЗ
// досчёта цен fal/glif: склейка истории с ценами остаётся в фасаде (иначе история зависела бы
// от журнала цен, а журнал пишет в историю — цикл, §1 «цены fal/glif»).
// Зависимостей две (ChatHistoryService, SessionStore). Класс public ТОЛЬКО потому, что он
// параметр публичного конструктора SessionManager (CS0051) и DI нужен публичный конструктор;
// обход фасада закрывает сторож формы ядра (SessionCoreShapeGuardTests, правило 4).
public sealed class SessionHistory
{
    private readonly ChatHistoryService _history;
    private readonly SessionStore _store;
    private readonly ILogger<SessionHistory> _log;

    public SessionHistory(ChatHistoryService history, SessionStore store, ILogger<SessionHistory> log)
    {
        _history = history;
        _store = store;
        _log = log;
    }

    // Сериализует внеходовые операции над историей сессии (lazy-init аккумулятора,
    // публикация fal/glif, правка карточек, эскалации штаба). Раньше семь мест брали
    // этот лок напрямую через Wait/Release — консолидировано через WithFalPersistLockAsync.
    private readonly SemaphoreSlim _falPersistLock = new(1, 1);

    // ЕДИНСТВЕННАЯ точка входа к _falPersistLock: WaitAsync + try/finally Release() под
    // одну обёртку. Все семь операций, которые раньше брали лок напрямую, идут через
    // неё — это держит инвариант «общий лок для внеходовых записей истории» по
    // построению (новая операция не сможет взять лок в обход, не дописав вызов).
    // Тот же лок обязана брать каждая роль, пишущая в историю вне хода (TurnCostLedger):
    // отдельный лок в каждой роли вернул бы гонку lost update, от которой этот лок и защищён.
    internal async Task<T> WithFalPersistLockAsync<T>(Func<Task<T>> work)
    {
        await _falPersistLock.WaitAsync();
        try { return await work(); }
        finally { _falPersistLock.Release(); }
    }

    internal async Task WithFalPersistLockAsync(Func<Task> work)
    {
        await _falPersistLock.WaitAsync();
        try { await work(); }
        finally { _falPersistLock.Release(); }
    }

    // Результат AppendIfNotDuplicateStoredNoLockAsync для дисковой ветки публикаций:
    // Added — запись добавлена; Duplicate — предикат уже видел такую запись.
    internal enum AppendResult { Added, Duplicate }

    // Ключ history.json чата: транскрипт CLI, а до первого ответа агента — id самого чата.
    // Тот же ключ берут EnsureAccumulatorAsync и локальный голосовой ход, поэтому записи,
    // сделанные до первого ответа (карточки нитей картинок, ADR-019), аккумулятор первого хода
    // поднимает с диска и дальше сохраняет уже под ClaudeSessionId — ничего не теряется.
    internal static string HistoryKeyOf(Session info) => info.ClaudeSessionId ?? info.Id;

    // Дедуп-then-append: общий шов публикаций fal/glif и AppendStoredAsync на
    // дисковой ветке. КОНТРАКТ: вызывающий ОБЯЗАН держать _falPersistLock — иначе
    // Load+SaveAsync терял бы параллельные записи соседа, а главное — публикация
    // могла бы пройти в обход оживления аккумулятора (EnsureAccumulatorAsync) и
    // дописать запись в историю, которую тут же затрёт свежий SaveSnapshotAsync.
    // Проверка entry.Accumulator в публикациях тоже идёт под этим локом, чтобы
    // EnsureAccumulatorAsync не мог вклиниться между выбором ветки и самой записью.
    internal async Task<AppendResult> AppendIfNotDuplicateStoredNoLockAsync(
        SessionEntry entry,
        Func<StoredMessage, bool> isDuplicate,
        Func<StoredMessage> factory)
    {
        var key = HistoryKeyOf(entry.Info);
        var stored = await _history.LoadAsync(key);
        if (stored.Any(isDuplicate)) return AppendResult.Duplicate;
        stored.Add(factory());
        await _history.SaveAsync(key, stored);
        return AppendResult.Added;
    }

    // Создание/переиспользование аккумулятора истории (без CLI-процесса). Вырезано из
    // EnsureProcessCoreAsync: локальному голосовому ходу (chat-voice) процесс не нужен,
    // а аккумулятор — да (реплики разговора пишутся в ту же историю). Ключ: ClaudeSessionId
    // чата, а при его отсутствии (ходов CLI ещё не было) — id чата: локальные ходы пишут
    // историю в data/sessions/{id чата}/history.json, и после рестарта сервера, и при
    // возврате на CLI она подхватывается отсюда же. (S3-5: переехал в роль истории — процессу
    // и восстановлению нужен тот же ключ и тот же лок, а они здесь.)
    internal async Task EnsureAccumulatorAsync(SessionEntry entry)
    {
        if (entry.Accumulator is not null) return;
        // Оживление под _falPersistLock — сериализуем с прямой записью fal-стоимости в
        // историю неактивной сессии (PublishFalCostAsync): иначе LoadAsync тут и запись там
        // теряли бы друг друга (lost update). Повторная проверка под локом.
        await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null) return;
            // Ключ сохранения — тот же, что у чтения: до первого ответа агента это id чата, иначе
            // внеходовая запись в свежий чат (AppendStoredAsync) не легла бы на диск вовсе.
            // Ответ CLI переключит ключ на ClaudeSessionId (SessionStartedMessage → SetSaveKey)
            var key = HistoryKeyOf(entry.Info);
            var existingHistory = await _history.LoadAsync(key);
            entry.Accumulator = new TurnAccumulator(existingHistory, key);
        });
    }

    // Снимок аккумулятора на диск, fire-and-forget (тонкая запись для процесса: отметка
    // прерывания хода, S3-5). Тот же путь, что был у RecordUserInterrupt в фасаде.
    internal void SaveAccumulatorSnapshot(SessionEntry entry, string what)
    {
        if (entry.Accumulator is not { } acc) return;
        DeferredRun.FireAndForget(acc.SaveSnapshotAsync(_history), what);
    }

    // Карточка «Цикл работы остановлён» (причина и текст) — в ленту и в рассылку. Молча
    // выходит при null-аккумуляторе (в отличие от AppendServerRestartCardAsync ниже, которая
    // создаёт его явно). S3-5: тело переехало из фасада; S3-7: карточки больше не проходят
    // через шов ITurnStarter — пишущий их вызывает эту роль напрямую (TurnRecovery,
    // TurnProcessHost), фасадная реализация шва снята вместе с самими точками шва.
    internal async Task AddWorkLoopStoppedNoticeAsync(string sessionId, string reason, string text)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry) || entry.Accumulator is not { } acc) return;
        acc.Append(new StoredWorkLoopStoppedMessage(reason, text));
        await acc.SaveSnapshotAsync(_history);
        await _store.BroadcastAsync(sessionId, new WorkLoopStoppedMessage(reason, text));
    }

    // Явная гидратация аккумулятора + идемпотентная карточка server_restart (B-cards).
    // В отличие от AddWorkLoopStoppedNoticeAsync (молча выходит при null), здесь аккумулятор
    // создаётся явно: в сценарии 1 (чат уже имел ClaudeSessionId, ход был Error) аккумулятор
    // после LoadSessions может быть null, и вызов просто вышел бы. Идемпотентность: если
    // карточка с reason=server_restart уже в истории (повторный вызов после рестарта),
    // не дублируем. S3-7: тело перенесено из фасада дословно; entry берётся из стора,
    // как в AddWorkLoopStoppedNoticeAsync — точка входа одна для обеих карточек.
    internal async Task AppendServerRestartCardAsync(string sessionId)
    {
        const string reason = "server_restart";
        const string text = "Сервер был перезапущен — продолжаю ход с того же места";

        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;

        // Явная гидратация: если аккумулятор null — создаём из существующей истории.
        if (entry.Accumulator is null)
            await EnsureAccumulatorAsync(entry);

        if (entry.Accumulator is not { } acc) return;

        // Идемпотентность: карточка с таким reason уже есть — не дублируем.
        if (acc.GetAll().Any(m => m is StoredWorkLoopStoppedMessage w && w.Reason == reason))
            return;

        acc.Append(new StoredWorkLoopStoppedMessage(reason, text));
        await acc.SaveSnapshotAsync(_history);
        await _store.BroadcastAsync(sessionId, new WorkLoopStoppedMessage(reason, text));
    }

    // Запись StoredMessage в историю сессии ВНЕ хода + broadcast (обобщение паттерна
    // PublishFalCostAsync): активная сессия → через Accumulator + SaveSnapshot;
    // неактивная → LoadAsync + append + SaveAsync под локом. Используется совещаниями.
    public async Task AppendStoredAsync(string sessionId, StoredMessage stored, ServerMessage broadcast, bool inTurn = false)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
        // Запись в ленту = «чат занят» (S2d-7a): карточка уходит в группу проекта, и приватным такой чат уже не сделать
        _store.ClaimFirstMessage(entry);

        // Как в PublishFalCostAsync: лок держим РОВНО один раз на «проверить entry.Accumulator
        // → выбрать ветку → записать», иначе EnsureAccumulatorAsync вклинится между
        // выбором дисковой ветки и записью и следующий SaveSnapshotAsync затрёт нашу запись.
        await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is { } acc)
            {
                if (inTurn) acc.AppendInTurn(stored);
                else acc.Append(stored);
                try { await acc.SaveSnapshotAsync(_history); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Сохранение истории ({sessionId}) после внеходовой записи: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    // Дедуп предикат «никогда» — совещания/конвейеры сами следят за
                    // уникальностью по своим ключам; AppendResult тут не интересует,
                    // broadcast всё равно отправим.
                    await AppendIfNotDuplicateStoredNoLockAsync(entry, _ => false, () => stored);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Прямая внеходовая запись истории ({sessionId}): {ex.Message}");
                }
            }
        });

        await _store.BroadcastSessionMessageAsync(sessionId, broadcast);
    }

    // Чтение ленты без досчёта цен: досчёт fal/glif — забота фасада (склейка с TurnCostLedger),
    // здесь её нет намеренно — иначе история зависела бы от журнала цен, а журнал пишет в историю.
    public async Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry))
            return [];

        IReadOnlyList<StoredMessage> list;
        if (entry.Accumulator != null)
            list = entry.Accumulator.GetAll();
        else
            list = await _history.LoadAsync(HistoryKeyOf(entry.Info));

        return list;
    }

    public IReadOnlyList<WorkflowProgressMessage> GetWorkflowProgress(string sessionId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return [];
        // Снимок под локом: словарь конкурентно мутируют таймер-потоки ватчеров
        lock (entry.WorkflowProgress) return entry.WorkflowProgress.Values.ToList();
    }

    // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при JoinSession
    public ServerMessage? GetPendingInteraction(string sessionId) =>
        _store.Sessions.TryGetValue(sessionId, out var entry) ? entry.PendingInteraction : null;

    // Последний манифест recall (F3) сессии — replay при подключении клиента (JoinSession),
    // как и у workflow_progress: без этого «использовано сейчас» видно только тем, кто был
    // на связи в момент самого хода.
    public RecallManifestMessage? GetLastRecallManifest(string sessionId) =>
        _store.Sessions.TryGetValue(sessionId, out var entry) ? entry.LastRecallManifest : null;

    // Доступ к ChatHistoryService для вертикали Services.Team через шов: TeamStateService
    // читает историю неактивного чата в GetTeamPlanFromHistoryAsync, но идёт через этот
    // метод, а не через прямую ссылку на ChatHistoryService — иначе сторож границ краснеет
    // (ChatHistoryService живёт в корне Services, это «спинка», а не Team-вертикаль).
    // Тот же шаблон, что и у прочих internal-обёрток: минимум публичной поверхности при
    // максимуме гибкости реализации.
    internal async Task<TeamImplementPlan?> ReadStoredTeamPlanAsync(string claudeSessionId, string planId,
        bool onlyUnresolved = false)
    {
        if (claudeSessionId is null) return null;
        try
        {
            var stored = await _history.LoadAsync(claudeSessionId);
            return stored.OfType<StoredTeamPlanMessage>()
                .LastOrDefault(m => m.PlanId == planId && (!onlyUnresolved || !m.Resolved))?.Plan;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточки плана {PlanId} с диска ({SessionId}) не удалось",
                planId, claudeSessionId);
            return null;
        }
    }

    // Правка карточки истории у НЕактивного чата (аккумулятора нет): загрузить, изменить,
    // сохранить. У активного чата тем же занимается аккумулятор — там снимок хода в памяти
    // и правка под его локом. Здесь read-modify-write файла, поэтому идём под _falPersistLock —
    // тем же, что сериализует остальные внеходовые записи истории. Без него двойной клик по
    // карточке (обычное дело сразу после рестарта, когда аккумулятор ещё не оживлён) проходил
    // бы дважды: удвоенная прибавка бюджета и двойная раздача волны.
    internal async Task<bool> MutateStoredAsync<T>(SessionEntry entry, string sessionId,
        Func<T, bool> match, Action<T> mutate) where T : StoredMessage
    {
        if (entry.Info.ClaudeSessionId is not string key) return false;
        try
        {
            return await WithFalPersistLockAsync(async () =>
            {
                var stored = await _history.LoadAsync(key);
                var card = stored.OfType<T>().LastOrDefault(match);
                if (card is null) return false;
                mutate(card);
                await _history.SaveAsync(key, stored);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Правка карточки в истории на диске ({SessionId}) не удалась", sessionId);
            return false;
        }
    }

    // Синхронный собрат ListOpenEscalationsAsync (S6, фикс-волна). Нужен там, где
    // async на запросном потоке недопустим (MVC-фильтр DenyOnDelegatedTurn → TeamBudgetService)
    // или где мы хотим объединить чтение с мутацией в одной WithTeamState-транзакции
    // (S4 — гонка возврата стадии). Реализация безопасна для синхронного вызова: у
    // активного чата читает Accumulator напрямую (исторически тот же путь без явного
    // lock), у неактивного — LoadAsync(...).GetAwaiter().GetResult() (метод фактически
    // синхронен: Task.FromResult, см. ChatHistoryService.LoadAsync).
    internal IReadOnlyList<TeamEscalation> GetOpenTeamEscalationsSync(string sessionId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return [];
        if (entry.Accumulator is { } acc)
            return acc.GetAll().OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        if (entry.Info.ClaudeSessionId is not string key) return [];
        try
        {
            var stored = _history.LoadAsync(key).GetAwaiter().GetResult();
            return stored.OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточек остановки с диска ({SessionId}) не удалось", sessionId);
            return [];
        }
    }

    // Единая точка правки/добавления карточки плана (волна В). Сюда ушла размазанная
    // развилка из шести мест — теперь развилка спрятана внутри шва. Семантика request
    // описана в xml-комментарии ITeamHistoryStore.SavePlanCardAsync.
    internal async Task<bool> SavePlanCardAsync(string sessionId, PlanCardWriteRequest req)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;

        // Ветка аккумулятора: правка через Accumulator (живой ход) + SaveSnapshot.
        // Один путь и для append (OnTeamPlan), и для mutate (OnTeamPlanUpdated /
        // OnTeamPlanSuperseded) — Accumulator разруливает по состоянию карточки сам.
        if (entry.Accumulator is { } acc)
        {
            if (req.SupersededBy is { } sb)
                acc.OnTeamPlanSuperseded(req.Plan.Id, sb);
            else if (req.Resolved)
                acc.OnTeamPlanUpdated(req.Plan.Id, req.Plan, req.Approved);
            else
                acc.OnTeamPlan(req.Plan);
            DeferredRun.FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после правки карточки плана ({sessionId})");
            return true;
        }

        // Чат неактивен — пишем карточку прямо в историю на диске под _falPersistLock:
        // та же сериализация, что у внеходовых записей и правок карточек (см. ITeamHistoryStore).
        if (entry.Info.ClaudeSessionId is not string key) return false;
        try
        {
            return await WithFalPersistLockAsync(async () =>
            {
                var stored = await _history.LoadAsync(key);
                // Append — публикация карточки плана.
                if (!req.Resolved && req.SupersededBy is null)
                {
                    stored.Add(new StoredTeamPlanMessage
                    {
                        PlanId = req.Plan.Id,
                        Plan = req.Plan,
                        Resolved = false,
                        Approved = req.Approved,
                        PersonaId = req.Plan.PlannerPersonaId,
                    });
                }
                else
                {
                    // Mutate существующей неразрешённой карточки — иначе двойной клик по
                    // карточке применился бы дважды (та же защита, что у Filter by Resolved
                    // у Accumulator.FindTeamPlan).
                    var card = stored.OfType<StoredTeamPlanMessage>().LastOrDefault(
                        m => m.PlanId == req.Plan.Id && !m.Resolved);
                    if (card is null) return false;
                    if (req.SupersededBy is { } sb)
                    {
                        card.Resolved = true;
                        card.Approved = false;
                        card.SupersededBy = sb;
                    }
                    else
                    {
                        card.Plan = req.Plan;
                        if (req.Resolved)
                        {
                            card.Resolved = true;
                            card.Approved = req.Approved;
                        }
                    }
                }
                await _history.SaveAsync(key, stored);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Прямая запись карточки плана ({SessionId}) не удалась", sessionId);
            return false;
        }
    }
}
