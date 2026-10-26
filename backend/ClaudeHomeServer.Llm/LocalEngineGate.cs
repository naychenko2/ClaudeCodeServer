namespace ClaudeHomeServer.Services.Llm;

// Потолок конкурентности к ОДНОМУ локальному движку (инвариант И7, фича «OOM на странице»).
// Раньше клиенты движка не ограничивали ничего: сколько запросов успело породить приложение,
// столько и уходило на порт, а движок под нагрузкой упирался в память и умирал.
//
// Гейт ставится в ОДНОЙ точке — на клиенте движка (GatedLocalLlmClient), а не в каждом месте
// вызова. Счётчик ровно один на движок на процесс: основной LocalLlm и каждый именованный
// движок получают свой экземпляр при регистрации, а второй семафор где-то ещё делал бы потолок
// неправдой (так же устроен BuildConcurrencyGate).
//
// Очередь ограничена: пока слотов нет, ждут не больше MaxQueue вызовов, остальные получают отказ
// сразу (LocalEngineOverloadedException), а не растят очередь в памяти.
public sealed class LocalEngineGate
{
    // Дефолты кода — чтобы инстанс без настройки не был без предела. Прямые клиенты делят движок
    // с ходами исполнителей через claude CLI (у vLLM на :18020 max_num_seqs=4), поэтому им —
    // не больше половины слотов. Машинное значение — LocalLlm:MaxConcurrency в appsettings.Local.json.
    public const int DefaultConcurrency = 2;
    public const int DefaultQueue = 8;

    private readonly SemaphoreSlim _slots;
    private int _waiting;

    public LocalEngineGate(string engine, int concurrency = DefaultConcurrency, int maxQueue = DefaultQueue)
    {
        Engine = engine;
        Concurrency = concurrency > 0 ? concurrency : DefaultConcurrency;
        MaxQueue = maxQueue >= 0 ? maxQueue : DefaultQueue;
        _slots = new SemaphoreSlim(Concurrency, Concurrency);
    }

    public string Engine { get; }
    public int Concurrency { get; }
    public int MaxQueue { get; }

    // Свободных слотов и ждущих в очереди — для диагностики и тестов
    public int Available => _slots.CurrentCount;
    public int Waiting => Volatile.Read(ref _waiting);

    // Читает потолок из секции конфига (LocalLlm или LocalEngines:{name}); пустое или
    // неверное значение — дефолт кода, ноль и минус слоты не выключают.
    public static LocalEngineGate Read(IConfiguration config, string section, string engine) =>
        new(engine,
            ReadInt(config[$"{section}:MaxConcurrency"], DefaultConcurrency, min: 1),
            ReadInt(config[$"{section}:MaxQueue"], DefaultQueue, min: 0));

    private static int ReadInt(string? raw, int fallback, int min) =>
        int.TryParse(raw, out var v) && v >= min ? v : fallback;

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (!_slots.Wait(0))
        {
            if (Interlocked.Increment(ref _waiting) > MaxQueue)
            {
                Interlocked.Decrement(ref _waiting);
                throw new LocalEngineOverloadedException(Engine, Concurrency, MaxQueue);
            }
            try { await _slots.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _waiting); }
        }
        try { return await work(); }
        finally { _slots.Release(); }
    }
}

// Отказ по существу: движок занят, очередь ожидания заполнена
public sealed class LocalEngineOverloadedException(string engine, int concurrency, int maxQueue)
    : Exception($"Локальный движок «{engine}» перегружен: заняты все {concurrency} слотов, " +
                $"очередь ожидания ({maxQueue}) заполнена — запрос отклонён")
{
}

// Клиент движка под гейтом: единственная точка потолка конкурентности. Контракт ILocalLlmClient
// («null при любой беде — вызывающий идёт дальше по цепочке») сохраняется: отказ гейта
// пишется в лог с понятным текстом и отдаётся как null, а не как исключение, которое
// не ждут фоновые вызывающие.
public sealed class GatedLocalLlmClient(ILocalLlmClient inner, LocalEngineGate gate,
    ILogger<GatedLocalLlmClient> logger) : ILocalLlmClient
{
    public LocalEngineGate Gate { get; } = gate;

    public bool Enabled => inner.Enabled;
    public string BaseUrl => inner.BaseUrl;
    public string Model => inner.Model;
    public string TextModel => inner.TextModel;
    public string ProviderKey => inner.ProviderKey;

    public Task<string?> ChatJsonAsync(
        string systemPrompt, string userPrompt, object jsonFormat, CancellationToken ct = default,
        string? model = null, int? timeoutMs = null, int? numPredict = null, int? numCtx = null,
        string? ownerId = null, string? label = null) =>
        Guard<string?>(() => inner.ChatJsonAsync(systemPrompt, userPrompt, jsonFormat, ct, model,
            timeoutMs, numPredict, numCtx, ownerId, label), null, ct);

    public Task<string?> GenerateTextAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default) =>
        Guard<string?>(() => inner.GenerateTextAsync(prompt, model, timeout, numPredict, numCtx,
            ownerId, label, ct), null, ct);

    public Task<ChatTurnResult> ChatTurnAsync(
        IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
        int numPredict, int numCtx, string? ownerId,
        Func<string, Task>? onDelta = null, CancellationToken ct = default) =>
        Guard(() => inner.ChatTurnAsync(messages, model, timeout, numPredict, numCtx, ownerId,
            onDelta, ct), new ChatTurnResult(null, null), ct);

    public Task WarmUpAsync(string? model, CancellationToken ct = default) =>
        Guard<object?>(async () => { await inner.WarmUpAsync(model, ct); return null; }, null, ct);

    private async Task<T> Guard<T>(Func<Task<T>> call, T onRefusal, CancellationToken ct)
    {
        try
        {
            return await Gate.RunAsync(call, ct);
        }
        catch (LocalEngineOverloadedException ex)
        {
            logger.LogWarning("{Message}", ex.Message);
            return onRefusal;
        }
    }
}
