namespace ClaudeHomeServer.Services.Llm;

// Прямой клиент локального движка под общим счётчиком LocalEngineBusyTracker. Всё, что ходит
// на физический движок (ходы CLI, one-shot, этот клиент), берёт слот в ОДНОМ счётчике под
// одним ключом: отдельного семафора на движке нет — два потолка в разных единицах делали бы
// каждый из них неправдой.
//
// Слот берётся БЕЗ ожидания (TryEnter): занято — null, контракт ILocalLlmClient «null при беде»
// сохраняется, вызывающий идёт по своей цепочке. Разговорный ход отдаёт ChatTurnResult.EngineBusy:
// интерактивный путь отличает «движок занят» от «движок не ответил». Признак Busy слот не взводит: фоновый запрос
// не должен уводить в облако остальные фоновые.
public sealed class TrackedLocalLlmClient(ILocalLlmClient inner, LocalEngineBusyTracker tracker,
    string engineKey, Func<int> limit, ILogger<TrackedLocalLlmClient> logger) : ILocalLlmClient
{
    // Потолок, если у ключа нет провайдера с настройкой (совпадает с дефолтом MaxConcurrentTurns)
    public const int DefaultLimit = 3;
    // В нижнем регистре, как и любой ключ после ReadEngineKey: иначе провайдер реестра с именем «LocalLlm»
    // (ключ «localllm») и дефолт клиента разошлись бы по корзинам Ordinal-счётчика.
    public const string DefaultEngineKey = "localllm";

    // LocalLlm:EngineKey — ключ провайдера, с которым клиент делит физический движок.
    // Нормализуется как ключ провайдера в реестре (там он всегда в нижнем регистре, поиск без учёта
    // регистра), а счётчик допуска сравнивает ключи по Ordinal: без приведения «Local-Qwen» уводил
    // бы слот клиента в другую корзину счётчика, то есть на движке были бы два потолка.
    public static string ReadEngineKey(IConfiguration config) =>
        config["LocalLlm:EngineKey"] is { } k && !string.IsNullOrWhiteSpace(k)
            ? k.Trim().ToLowerInvariant()
            : DefaultEngineKey;

    // Заданный, но не найденный в реестре ключ — ошибка настройки: счётчик прямого клиента разойдётся
    // со счётчиком ходов CLI, а потолок молча станет DefaultLimit вместо MaxConcurrentTurns провайдера.
    // Ключ в другом регистре реестр находит (и ReadEngineKey приводит его к нижнему), но об этом
    // тоже говорим. Не задан ключ — предупреждения нет (отдельный движок, dev/стенд).
    // true — предупреждение записано.
    public static bool WarnIfEngineKeyUnknown(IConfiguration config, LlmProviderRegistry? registry, ILogger logger)
    {
        if (registry is null || config["LocalLlm:EngineKey"] is not { } raw || string.IsNullOrWhiteSpace(raw))
            return false;
        var key = ReadEngineKey(config);
        if (registry.GetByKey(key) is null)
        {
            logger.LogWarning(
                "LocalLlm:EngineKey=\"{Key}\": провайдера с таким ключом нет в реестре LlmProviders — счётчик допуска "
                + "прямого клиента разойдётся со счётчиком ходов CLI, и на движке окажутся два независимых потолка. "
                + "Потолок взят по умолчанию ({Limit}), а не у провайдера. "
                + "Ожидается ключ существующего провайдера (в бою — local-qwen).", key, DefaultLimit);
            return true;
        }
        if (!string.Equals(raw.Trim(), key, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "LocalLlm:EngineKey=\"{Raw}\" записан не в нижнем регистре: ключ приведён к «{Key}», потолок взят "
                + "у этого провайдера ({Limit}). Поправьте значение в конфигурации.",
                raw.Trim(), key, registry.GetByKey(key)!.MaxConcurrentTurns);
            return true;
        }
        return false;
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
            onDelta, ct), ChatTurnResult.EngineBusy);

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
