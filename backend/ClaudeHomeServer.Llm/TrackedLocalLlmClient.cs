namespace ClaudeHomeServer.Services.Llm;

// Прямой клиент локального движка под общим счётчиком LocalEngineBusyTracker. Всё, что ходит
// на физический движок (ходы CLI, one-shot, этот клиент), берёт слот в ОДНОМ счётчике под
// одним ключом: отдельного семафора на движке нет — два потолка в разных единицах делали бы
// каждый из них неправдой.
//
// Слот берётся БЕЗ ожидания (TryEnter): занято — null, контракт ILocalLlmClient «null при беде»
// сохраняется, вызывающий идёт по своей цепочке. Признак Busy слот не взводит: фоновый запрос
// не должен уводить в облако остальные фоновые.
public sealed class TrackedLocalLlmClient(ILocalLlmClient inner, LocalEngineBusyTracker tracker,
    string engineKey, Func<int> limit, ILogger<TrackedLocalLlmClient> logger) : ILocalLlmClient
{
    // Потолок, если у ключа нет провайдера с настройкой (совпадает с дефолтом MaxConcurrentTurns)
    public const int DefaultLimit = 3;
    public const string DefaultEngineKey = "LocalLlm";

    // LocalLlm:EngineKey — ключ провайдера, с которым клиент делит физический движок
    public static string ReadEngineKey(IConfiguration config) =>
        config["LocalLlm:EngineKey"] is { } k && !string.IsNullOrWhiteSpace(k) ? k.Trim() : DefaultEngineKey;

    // Опечатка в LocalLlm:EngineKey молча заводит отдельный счётчик: на одном движке окажутся два
    // независимых потолка. Ключ задан явно, а провайдера с таким ключом нет — предупреждаем.
    // true — предупреждение записано.
    public static bool WarnIfEngineKeyUnknown(IConfiguration config, LlmProviderRegistry? registry, ILogger logger)
    {
        if (registry is null || string.IsNullOrWhiteSpace(config["LocalLlm:EngineKey"])) return false;
        var key = ReadEngineKey(config);
        if (registry.GetByKey(key) is not null) return false;
        logger.LogWarning(
            "LocalLlm:EngineKey=\"{Key}\": провайдера с таким ключом нет в реестре LlmProviders — счётчик допуска "
            + "прямого клиента разойдётся со счётчиком ходов CLI, и на движке окажутся два независимых потолка. "
            + "Ожидается ключ существующего провайдера (в бою — local-qwen).", key);
        return true;
    }

    // Ключ слота в счётчике: у основного клиента — LocalLlm:EngineKey (провайдер, с которым
    // он делит движок) либо «LocalLlm», у именованного движка — его имя
    public string EngineKey { get; } = engineKey;

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
            timeoutMs, numPredict, numCtx, ownerId, label), null);

    public Task<string?> GenerateTextAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default) =>
        Guard<string?>(() => inner.GenerateTextAsync(prompt, model, timeout, numPredict, numCtx,
            ownerId, label, ct), null);

    public Task<ChatTurnResult> ChatTurnAsync(
        IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
        int numPredict, int numCtx, string? ownerId,
        Func<string, Task>? onDelta = null, CancellationToken ct = default) =>
        Guard(() => inner.ChatTurnAsync(messages, model, timeout, numPredict, numCtx, ownerId,
            onDelta, ct), new ChatTurnResult(null, null));

    public Task WarmUpAsync(string? model, CancellationToken ct = default) =>
        Guard<object?>(async () => { await inner.WarmUpAsync(model, ct); return null; }, null);

    private async Task<T> Guard<T>(Func<Task<T>> call, T onRefusal)
    {
        using var slot = tracker.TryEnter(EngineKey, limit(), raiseBusy: false);
        if (slot is null)
        {
            logger.LogWarning(
                "Локальный движок «{Engine}» занят: свободных слотов нет — прямой запрос отклонён",
                EngineKey);
            return onRefusal;
        }
        return await call();
    }
}
