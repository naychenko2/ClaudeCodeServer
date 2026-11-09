using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Sessions;

// Состояние очереди занятой сессии и его файл (pending.json): постановка с дедупом,
// восстановление при загрузке с отсевом просроченного, снимки очереди клиенту, персист
// заморозки. Вынесен из SessionManager (S3-2, docs/research/session-manager-split-2026-11.md §1).
// Лок файла очереди — entry.PendingLock (на записи SessionEntry), ключ тот же: по сессии,
// не по экземпляру этой роли; перенос кода его не меняет.
// Разрыв цикла «очередь → приём хода»: при переходе очереди 0→1 под свободным чатом
// постановка форсирует разбор (DrainNextPendingAsync) — это рекурсия, сейчас живущая внутри
// SessionManager; здесь она названа швом Lazy<ITurnStarter> (S3-0 §3, единственный
// допустимый способ такой связи).
// Зависимостей четыре (store, chatHistory, history, lazyTurnStarter): в таблице S3-0 было
// три (store, ChatHistoryService, config) — config этим методам не нужен (в телах не
// встречается), зато нужны SessionHistory (плашка просроченного пишет карточку в историю)
// и шов ITurnStarter. Класс public ТОЛЬКО потому, что он параметр публичного конструктора
// SessionManager (CS0051) и DI нужен публичный конструктор; обход фасада закрывает сторож
// формы ядра (правило 4).
// Имя — PendingQueueService, а не PendingQueue из плана S3-0: в этом же namespace живёт
// коллекция состояния очереди (PendingQueue на SessionEntry.Pending) — роль и её данные
// названы по-разному, чтобы вызывающий не путал хранение с обслуживанием. Суть роли — §1 плана.
public sealed class PendingQueueService
{
    private readonly SessionStore _store;
    private readonly ChatHistoryService _history;
    private readonly SessionHistory _sessionHistory;
    private readonly Lazy<ITurnStarter> _turnStarter;
    private readonly ILogger<PendingQueueService> _log;

    public PendingQueueService(
        SessionStore store,
        ChatHistoryService chatHistory,
        SessionHistory history,
        Lazy<ITurnStarter> turnStarter,
        ILogger<PendingQueueService> log)
    {
        _store = store;
        _history = chatHistory;
        _sessionHistory = history;
        _turnStarter = turnStarter;
        _log = log;
    }

    // Потолок очереди на сессию: агент может ретраить, а занятый чат — стоять долго.
    // Переполнение — честный отказ вызывающему, а не молчаливая потеря.
    internal const int MaxPendingPerSession = 10;

    // --- Персистентность очереди отложенных сообщений (pending.json) ---

    // Срок годности восстановленного сообщения: старше — не оживляем, чтобы не будить чаты
    // недельной давности (в историю ложится плашка «не доставлено»).
    internal static readonly TimeSpan PendingRestoreMaxAge = TimeSpan.FromHours(24);

    internal void AttachPendingPersistence(SessionEntry entry) =>
        entry.Pending.Changed = () => PersistPending(entry);

    // Запись очереди и заморозки. Берёт PendingLock сам (замок реентерабелен): QueueFrozen
    // пишется и вне замка, а две параллельные записи одного файла не должны разъезжаться.
    internal void PersistPending(SessionEntry entry)
    {
        try
        {
            lock (entry.PendingLock)
            {
                var key = SessionHistory.HistoryKeyOf(entry.Info);
                _history.SavePending(key, new PendingQueueFile(entry.QueueFrozen, entry.Pending.ToList()));
                // Чат получил ClaudeSessionId уже после первой записи — файл под id чата устарел
                if (key != entry.Info.Id) _history.SavePending(entry.Info.Id, new PendingQueueFile(false, []));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PendingQueueService] Очередь чата {entry.Info.Id} не записана: {ex.Message}");
        }
    }

    // Загрузка очереди на старте: свежие сообщения — в Pending (в том же порядке), старше срока —
    // в ExpiredOnRestore (плашку пишет DrainRestoredPendingAsync). Файл читаем ДО подключения
    // Changed: восстановление не должно писать диск, пока не отсеяли просроченное.
    // Колбэк SessionStore.Load(onLoaded) при сборке указывает именно на этот метод.
    internal void Restore(SessionEntry entry)
    {
        try
        {
            var state = _history.LoadPending(SessionHistory.HistoryKeyOf(entry.Info))
                ?? (entry.Info.ClaudeSessionId is not null ? _history.LoadPending(entry.Info.Id) : null);
            if (state is { Items.Count: > 0 })
            {
                var now = DateTime.UtcNow;
                var fresh = state.Items.Where(m => now - m.EnqueuedAt <= PendingRestoreMaxAge).ToList();
                var expired = state.Items.Where(m => now - m.EnqueuedAt > PendingRestoreMaxAge).ToList();
                entry.Pending.Restore(fresh);
                entry.QueueFrozen = state.Frozen;
                entry.PendingRestored = fresh.Count > 0;
                if (expired.Count > 0) entry.ExpiredOnRestore = expired;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PendingQueueService] Очередь чата {entry.Info.Id} не прочитана: {ex.Message}");
        }
        AttachPendingPersistence(entry);
        if (entry.ExpiredOnRestore is not null) PersistPending(entry);
    }

    internal async Task AppendPendingExpiredCardAsync(string sessionId, SessionEntry entry, List<QueuedMessage> expired)
    {
        const string reason = "pending_expired";
        var text = expired.Count == 1
            ? "Не доставлено: сервер перезапускался, а сообщение в очереди устарело (старше 24 ч)"
            : $"Не доставлено: сервер перезапускался, а {expired.Count} сообщений в очереди устарели (старше 24 ч)";
        await _sessionHistory.AppendStoredAsync(sessionId, new StoredWorkLoopStoppedMessage(reason, text),
            new WorkLoopStoppedMessage(reason, text));
    }

    // === Очередь сообщений занятой сессии ===

    // Поставить сообщение в очередь занятой сессии. Дедуп по (текст, отправитель) — ТОЛЬКО
    // для агентских: прежний контракт chats_send советовал ретраить при отказе, и наивный
    // агент насыпал бы дублей одного и того же текста. Человек может осознанно слать
    // повторы («ещё раз», «продолжи») — его сообщения не дедупятся.
    internal async Task<SendAndWaitResult> EnqueuePendingAsync(string sessionId, SessionEntry entry,
        string text, string? senderPersonaId, string? senderOrigin, int agentDepth,
        bool silent = false, bool suppressTasksExecute = false, string? senderChatName = null,
        PendingKind kind = PendingKind.Agent, IReadOnlyList<string>? attachedPaths = null,
        string? mode = null, string? staffNote = null, string? senderUserId = null)
    {
        _store.ClaimFirstMessage(entry);
        bool dispatchNow;
        int position;
        lock (entry.PendingLock)
        {
            if (kind == PendingKind.Agent
                && entry.Pending.Any(p => p.Kind == PendingKind.Agent && p.Text == text && p.SenderPersonaId == senderPersonaId))
                return new SendAndWaitResult.Queued(entry.Pending.Count, Duplicate: true);
            if (entry.Pending.Count >= MaxPendingPerSession)
                return new SendAndWaitResult.QueueFull(MaxPendingPerSession);

            entry.Pending.Add(new QueuedMessage(Guid.NewGuid().ToString("N"), text, senderPersonaId,
                senderOrigin, agentDepth, DateTime.UtcNow, silent, suppressTasksExecute, senderChatName,
                kind, attachedPaths, mode, staffNote, senderUserId));
            position = entry.Pending.Count;

            // Защита от гонки TOCTOU: статус занятости читается БЕЗ лока выше (в SendMessageAsync/
            // SendMessageAndWaitAsync), а Add — здесь, под PendingLock. Если между чтением и Add ход
            // успел завершиться (статус упал из Working, а ResultMessage уже прошёл и его DrainNextPendingAsync
            // отработал по ЕЩЁ ПУСТОЙ очереди) — сообщение зависнет при свободном чате: триггер автодоставки
            // (по result) уже стрелял, а нового не будет. Форсируем разбор при переходе очереди 0→1: drain
            // идемпотентен (RemoveAt атомарен), повторный безопасен. Запускаем ровно один раз на переход,
            // чтобы конкурентные постановки не стимулировали несколько drain'ов. Условия НЕ срабатывания:
            // замороженная «Стоп» очередь (возобновляет только новое пользовательское сообщение) и активный
            // цикл «до готово» — между итерациями чат на мгновение свободен, но агентское сообщение должно
            // ждать конца ВСЕГО цикла (посторонний агент не сбивает координатора, решение владельца
            // 2026-09-01). User и Report — наоборот, продолжают цикл как следующая итерация / ход-реакция
            // постановщика, поэтому при живом цикле dispatchNow форсируется.
            dispatchNow = position == 1
                && !entry.QueueFrozen
                && (entry.Info.WorkLoop is null
                    || kind is PendingKind.User or PendingKind.Report)
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)
                // Адаптер ведёт оркестрацию хода (фолбэк) — НЕ форсируем разбор очереди: ход,
                // вернувшийся из-под оркестрации через EnqueueBypass, должен дождаться её конца
                // (OrchestrationDone в finally адаптера), иначе drain↔requeue закрутит цикл на
                // рассинхроне «статус Active, но _turn ещё активен» (инцидент 2026-08-10 П3).
                && entry.Process?.OrchestrationActive != true;
        }
        await BroadcastPendingAsync(sessionId, entry);

        // ВНЕ лока: drain сам возьмёт PendingLock и достанет entry из реестра по sessionId.
        if (dispatchNow)
            _ = Task.Run(() => _turnStarter.Value.DrainNextPendingAsync(sessionId));

        // Dispatched говорит вызывающему, что доставка уже форсирована: прерывать ход после
        // такой постановки нельзя — это был бы собственный, только что запущенный ход
        return new SendAndWaitResult.Queued(position, Duplicate: false, Dispatched: dispatchNow);
    }

    // Снимок очереди для клиента и REST
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return [];
        lock (entry.PendingLock) return entry.Pending.ToList();
    }

    // Убрать поставленное (queued) сообщение по тексту — для служебных отправок (записка-передача),
    // которые не должны отработать отдельным ходом, если чат занялся между проверкой занятости и
    // отправкой. true, если что-то убрано.
    public bool RemovePendingByText(string sessionId, string text)
    {
        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return false;
        lock (entry.PendingLock)
        {
            var idx = entry.Pending.FindIndex(p => p.Text == text);
            if (idx < 0) return false;
            entry.Pending.RemoveAt(idx);
            return true;
        }
    }

    // Клиенту уходят только видимые элементы: silent — ход-реакция на доклад, чей текст
    // уже лежит в ленте отдельной репликой, призрак дублировал бы её служебным промптом.
    // Kind/AttachedPaths/Mode — только для пользовательских (карточка-призрак и композер).
    internal Task BroadcastPendingAsync(string sessionId, SessionEntry entry) =>
        _store.BroadcastSessionMessageAsync(sessionId, new PendingMessagesMessage(VisiblePending(entry)));

    // Ждущие устройство идут впереди обычной очереди: они пришли раньше и уйдут в работу
    // первыми (ReleaseDeviceWaitAsync ставит их в голову Pending).
    internal static IReadOnlyList<PendingMessageDto> VisiblePending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return [
                .. (entry.Info.DeviceWaitQueue ?? []).Select(m => new PendingMessageDto(
                    m.Id, m.Text, m.SenderPersonaId, m.SenderOrigin, m.QueuedAt, m.SenderChatName,
                    m.Route == DeviceWaitMessage.RouteUser ? "user" : "agent",
                    m.AttachedPaths, m.Route == DeviceWaitMessage.RouteUser ? m.Mode : null,
                    WaitingForDevice: true,
                    SenderUserId: m.Route == DeviceWaitMessage.RouteUser ? m.SenderUserId : null)),
                .. entry.Pending.Where(p => !p.Silent).Select(p => new PendingMessageDto(
                    p.Id, p.Text, p.SenderPersonaId, p.SenderOrigin, p.EnqueuedAt, p.SenderChatName,
                    p.Kind == PendingKind.User ? "user" : "agent",
                    p.AttachedPaths, p.Kind == PendingKind.User ? p.Mode : null,
                    SenderUserId: p.Kind == PendingKind.User ? p.SenderUserId : null)),
            ];
    }

    // Есть ли в очереди сообщение, продолжающее цикл. User — следующая итерация цикла;
    // Report — доклад исполнителя, требующий хода-реакции постановщика (тоже будит цикл,
    // иначе цикл висит в фазе waiting без движения). Посторонние Agent-сообщения не в счёт:
    // при активном цикле они ждут его конца (решение владельца 2026-09-01). Опирается на
    // гейт drain по result в OnMessageAsync — без такого сообщения цикл сам поднимет
    // ContinueWorkLoopAsync по своему LoopTurnInFlight-маркеру.
    internal static bool HasContinuingPending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return entry.Pending.Any(p => p.Kind is PendingKind.User or PendingKind.Report);
    }

    internal static bool HasPending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return entry.Pending.Count > 0;
    }

    // Снимок для replay при JoinSession (без служебных)
    public IReadOnlyList<PendingMessageDto> GetVisiblePending(string sessionId) =>
        _store.Sessions.TryGetValue(sessionId, out var entry) ? VisiblePending(entry) : [];

    // Новый снимок отложенного сообщения с переписанными по карте путями вложений; null —
    // менять нечего. Чужие пути (не входящие в перенос) и порядок списка сохраняются как были:
    // record неизменяем, поэтому замена — копия через with, а не правка на месте.
    internal static QueuedMessage? MapQueuedAttachedPaths(QueuedMessage q, IReadOnlyDictionary<string, string> map)
    {
        if (q.AttachedPaths is not { Count: > 0 } paths) return null;
        var changed = false;
        var next = new List<string>(paths.Count);
        foreach (var p in paths)
        {
            if (map.TryGetValue(p, out var to)) { next.Add(to); changed = true; }
            else next.Add(p);
        }
        return changed ? q with { AttachedPaths = next } : null;
    }
}
