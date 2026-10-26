namespace ClaudeHomeServer.Services.Llm;

// Гейт допуска ходов к локальному движку и одновременно признак «на нём идёт ход исполнителя».
//
// Признак. Живой прогон 2026-09-23 (tools/thinking-strip-proxy/live-run-2026-09-23.md, разбор
// промахов 9a7aeced): фоновые one-shot действия (теги, сводки, память) ходили в тот же vLLM
// параллельно с ходом исполнителя — 14 запросов на 213k токенов за 49 минут. Кэшем они не
// пользуются, а KV-бюджет (307 698 токенов) занимают и вытесняют префикс чата: два из шести
// промахов кэша пришли через 100–130 мс после такого запроса. Счётчик, а не флаг: локальных
// ходов может идти несколько (разные чаты на одном движке), и «свободно» наступает только когда
// закончился ПОСЛЕДНИЙ.
//
// Допуск. Реальную нагрузку на движок дают ходы CLI (пик 7 одновременных при max_num_seqs=4,
// waiting=4). Ёмкость задаётся по ключу провайдера: сверх потолка ход ждёт в ограниченной
// очереди FIFO с таймаутом — не стартовавший процесс CLI движок не грузит. Очередь полна или
// вышел таймаут — отказ (LocalTurnAdmission.Refusal), текст ошибки решает вызывающий. Ждущий
// ход Busy НЕ взводит: движок он не занимает. Второго семафора рядом заводить нельзя —
// счётчик единственный на процесс, иначе потолок становится неправдой.
//
// Статика, а не DI, — симметрично BuildConcurrencyGate: ClaudeSession создаётся руками
// (SessionManager), а не контейнером, и протянуть к нему singleton неоткуда. Instance
// регистрируется в DI как тот же объект — второй трекер где-то ещё сделал бы признак
// неправдой. Тестам объект передаётся через конструктор потребителя, чтобы не делить
// глобальное состояние между прогонами.
public sealed class LocalEngineBusyTracker
{
    public static LocalEngineBusyTracker Instance { get; } = new();

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.Ordinal);
    // Занятые слоты по всем ключам: источник Busy/Active. Меняется только под _lock.
    private int _active;

    public LocalEngineBusyTracker(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    // Идёт ли прямо сейчас хотя бы один ход на локальном движке.
    public bool Busy => Volatile.Read(ref _active) > 0;

    // Сколько ходов на локали идёт — для диагностики и тестов.
    public int Active => Volatile.Read(ref _active);

    // Сколько ходов занимает слоты провайдера / ждёт в его очереди.
    public int ActiveFor(string key) { lock (_lock) return _keys.TryGetValue(key, out var s) ? s.Active : 0; }
    public int QueuedFor(string key) { lock (_lock) return _keys.TryGetValue(key, out var s) ? s.Queue.Count : 0; }

    // Пометка «ход на локали начался» без ограничения ёмкости. Возвращённый токен обязан быть
    // освобождён по выходу из хода (using на всё тело хода), иначе движок навсегда останется
    // «занятым» и фоновые действия до рестарта уедут в облако.
    public IDisposable Enter()
    {
        lock (_lock) _active++;
        return new Slot(this, null, true);
    }

    // Допуск БЕЗ ожидания (one-shot действия): слот выдаётся, только если есть свободное место
    // и в очереди никого нет — чужую очередь обгонять нельзя. null — «занято»: вызывающий
    // действует по правилу LocalActionRouter (не лезет в движок). Слот берётся из ТОГО ЖЕ
    // счётчика, что и у ходов, — второго семафора нет.
    // raiseBusy=false — слот прямого клиента LocalLlm: он занимает ёмкость, но признак Busy НЕ
    // взводит. Иначе один фоновый запрос отправил бы в облако все остальные фоновые
    // (LocalBlockedByTurn), и расход вырос бы молча.
    public IDisposable? TryEnter(string key, int maxConcurrent, bool raiseBusy = true)
    {
        lock (_lock)
        {
            var state = StateFor(key);
            state.Limit = Math.Max(1, maxConcurrent);
            if (state.Active >= state.Limit || state.Queue.Count > 0) return null;
            state.Active++;
            if (raiseBusy) _active++;
            return new Slot(this, key, raiseBusy);
        }
    }

    // Допуск хода к провайдеру key. Есть свободный слот и никто не ждёт — слот выдаётся сразу;
    // иначе ход встаёт в очередь (FIFO), если в ней есть место, и ждёт слот не дольше timeout.
    // Отмена ct убирает ход из очереди без изменения счётчика и пробрасывается как
    // OperationCanceledException. Если Slot != null — его надо освободить (Dispose).
    // onWait — наблюдатель очереди: зовётся при постановке в очередь и при каждом сдвиге
    // позиции (позиция 1-based, затем занято слотов и потолок); исключения наблюдателя глотаются.
    public async Task<LocalTurnAdmission> AcquireAsync(string key, int maxConcurrent, int maxQueued,
        TimeSpan timeout, CancellationToken ct, Action<int, int, int>? onWait = null)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        int position, active, limit;
        lock (_lock)
        {
            var state = StateFor(key);
            state.Limit = Math.Max(1, maxConcurrent);
            if (state.Active < state.Limit && state.Queue.Count == 0)
            {
                state.Active++;
                _active++;
                return LocalTurnAdmission.Admitted(new Slot(this, key, true), state.Active);
            }
            if (state.Queue.Count >= Math.Max(0, maxQueued))
                return LocalTurnAdmission.Rejected(LocalTurnRefusal.QueueFull, state.Active);
            waiter = new Waiter { OnWait = onWait };
            waiter.Node = state.Queue.AddLast(waiter);
            position = state.Queue.Count;
            active = state.Active;
            limit = state.Limit;
        }
        Notify(onWait, position, active, limit);

        using var timeoutCts = new CancellationTokenSource();
        using var timer = _time.CreateTimer(
            _ => { try { timeoutCts.Cancel(); } catch (ObjectDisposedException) { } },
            null, timeout, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var slot = await waiter.Tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return LocalTurnAdmission.Admitted(slot, ActiveFor(key));
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                // Слот мог быть выдан в ту же долю секунды, что сработала отмена: возвращаем
                // его в пул сразу, иначе он утёк бы навсегда (владельца у него уже нет).
                if (waiter.Tcs.Task.IsCompletedSuccessfully)
                    waiter.Tcs.Task.Result.Dispose();
                else if (waiter.Node is { List: not null } node)
                    node.List.Remove(node);
            }
            if (ct.IsCancellationRequested) throw;
            return LocalTurnAdmission.Rejected(LocalTurnRefusal.Timeout, ActiveFor(key));
        }
    }

    private KeyState StateFor(string key)
    {
        if (!_keys.TryGetValue(key, out var state)) _keys[key] = state = new KeyState();
        return state;
    }

    // Освобождение слота: ключевой слот сразу отдаёт освободившееся место голове очереди.
    private void Release(string? key, bool busy)
    {
        List<(Action<int, int, int> Cb, int Pos, int Active, int Limit)>? shifted = null;
        lock (_lock)
        {
            if (busy) _active--;
            if (key is null) return;
            var state = _keys[key];
            state.Active--;
            while (state.Queue.First is { } head && state.Active < state.Limit)
            {
                state.Queue.RemoveFirst();
                head.Value.Node = null;
                state.Active++;
                _active++;
                head.Value.Tcs.TrySetResult(new Slot(this, key, true));
            }
            // Оставшимся в очереди позиция сдвинулась — сообщаем им вне лока.
            var pos = 0;
            foreach (var w in state.Queue)
            {
                pos++;
                if (w.OnWait is { } cb) (shifted ??= []).Add((cb, pos, state.Active, state.Limit));
            }
        }
        if (shifted is not null)
            foreach (var (cb, pos, act, lim) in shifted) Notify(cb, pos, act, lim);
    }

    private static void Notify(Action<int, int, int>? cb, int position, int active, int limit)
    {
        try { cb?.Invoke(position, active, limit); }
        catch { /* наблюдатель очереди не должен ронять допуск */ }
    }

    private sealed class KeyState
    {
        public int Active;
        public int Limit = int.MaxValue;
        public readonly LinkedList<Waiter> Queue = new();
    }

    private sealed class Waiter
    {
        public LinkedListNode<Waiter>? Node;
        public Action<int, int, int>? OnWait;
        public readonly TaskCompletionSource<Slot> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Освобождение ровно один раз: повторный Dispose иначе увёл бы счётчик в минус и
    // «занято» перестало бы наступать при живом ходе.
    private sealed class Slot(LocalEngineBusyTracker owner, string? key, bool busy) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(key, busy);
        }
    }
}

// One-shot действие не получило слот локального движка (он занят ходами): вызов не запускался.
// Вызывающие one-shot ловят общий Exception и идут по своей цепочке — как при LocalBlockedByTurn.
public sealed class LocalEngineBusyException(string providerKey)
    : InvalidOperationException($"Локальный движок «{providerKey}» занят ходами — one-shot не запущен");

public enum LocalTurnRefusal { None, QueueFull, Timeout }

// Исход допуска: слот (Slot != null) либо причина отказа. Running — сколько ходов шло на
// провайдере в момент решения (для текста отказа).
public sealed record LocalTurnAdmission(IDisposable? Slot, LocalTurnRefusal Refusal, int Running)
{
    internal static LocalTurnAdmission Admitted(IDisposable slot, int running) =>
        new(slot, LocalTurnRefusal.None, running);

    internal static LocalTurnAdmission Rejected(LocalTurnRefusal why, int running) =>
        new(null, why, running);
}
