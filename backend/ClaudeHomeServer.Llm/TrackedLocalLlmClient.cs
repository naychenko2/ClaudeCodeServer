namespace ClaudeHomeServer.Services.Llm;

// Прямой клиент локального движка под общим счётчиком LocalEngineBusyTracker. Всё, что ходит
// на физический движок (ходы CLI, one-shot, этот клиент), берёт слот в ОДНОМ счётчике под
// одним ключом: отдельного семафора на движке нет — два потолка в разных единицах делали бы
// каждый из них неправдой.
//
// Слот берётся двумя способами — по тому, есть ли у вызывающего куда отступать:
//   • обычный путь (TryEnter, без ожидания): занято — null / ChatTurnResult.EngineBusy,
//     контракт ILocalLlmClient «null при беде» сохраняется, вызывающий идёт по своей цепочке
//     к следующему шагу (для фоновых действий с облачным фолбэком это и есть план);
//   • путь ожидания (*AwaitingSlotAsync, см. ILocalLlmClient): цепочка БЕЗ облачной страховки
//     (ночной разбор вики, ступень 2 отбора) стоит в общей очереди этого же счётчика, пока
//     слот не освободится. Ей некуда отступать, и отказ по занятости был потерей работы ночи
//     (прод 06.11: 108 отказов за минуту, модель не звалась ни разу). Не дождались —
//     LocalEngineSlotRefusalException с причиной, а не молчаливый ноль.
// Оба пути — ОДИН счётчик, тот же ключ и тот же потолок: второго семафора на движке нет.
// Признак Busy ни один из них не взводит: фоновый запрос не должен уводить в облако
// остальные фоновые.
public sealed class TrackedLocalLlmClient(ILocalLlmClient inner, LocalEngineBusyTracker tracker,
    string engineKey, Func<int> limit, ILogger<TrackedLocalLlmClient> logger,
    Func<LocalSlotWaitPolicy>? slotWait = null) : ILocalLlmClient
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

    // Detailed-варианты делегируются внутреннему клиенту, а не строковым методам: иначе
    // обёртка молча теряла бы usage, который тот уже разобрал (в DI ILocalLlmClient — этот
    // класс, так что через строковый метод метрик не дошёл бы ни до одного потребителя).
    // Отказ по занятости слота — та же пара «нет ответа»: (null, null).
    public Task<(string? Text, Protocol.UsageInfo? Usage)> ChatJsonDetailedAsync(
        string systemPrompt, string userPrompt, object jsonFormat, CancellationToken ct = default,
        string? model = null, int? timeoutMs = null, int? numPredict = null, int? numCtx = null,
        string? ownerId = null, string? label = null) =>
        Guard<(string? Text, Protocol.UsageInfo? Usage)>(() => inner.ChatJsonDetailedAsync(systemPrompt,
            userPrompt, jsonFormat, ct, model, timeoutMs, numPredict, numCtx, ownerId, label), (null, null));

    public Task<(string? Text, Protocol.UsageInfo? Usage)> GenerateTextDetailedAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default) =>
        Guard<(string? Text, Protocol.UsageInfo? Usage)>(() => inner.GenerateTextDetailedAsync(prompt, model,
            timeout, numPredict, numCtx, ownerId, label, ct), (null, null));

    // Путь ожидания слота — для цепочек без облачной страховки (см. ILocalLlmClient).
    // Внутренний клиент тот же: своей очереди у движка нет, ждёт обёртка в общем счётчике.
    // Отказ — LocalEngineSlotRefusalException с причиной, а не (null, null): «движок занят»
    // и «модель дала пустой ответ» должны различаться в итоге вызывающего (ночь 06.11).
    public Task<(string? Text, Protocol.UsageInfo? Usage)> ChatJsonAwaitingSlotAsync(
        string systemPrompt, string userPrompt, object jsonFormat, CancellationToken ct = default,
        string? model = null, int? timeoutMs = null, int? numPredict = null, int? numCtx = null,
        string? ownerId = null, string? label = null) =>
        GuardAwaitingSlotAsync<(string? Text, Protocol.UsageInfo? Usage)>(
            () => inner.ChatJsonDetailedAsync(systemPrompt, userPrompt, jsonFormat, ct, model,
                timeoutMs, numPredict, numCtx, ownerId, label), ct);

    public Task<(string? Text, Protocol.UsageInfo? Usage)> GenerateTextAwaitingSlotAsync(
        string prompt, string? model, TimeSpan timeout, int numPredict, int numCtx,
        string? ownerId = null, string? label = null, CancellationToken ct = default) =>
        GuardAwaitingSlotAsync<(string? Text, Protocol.UsageInfo? Usage)>(
            () => inner.GenerateTextDetailedAsync(prompt, model, timeout, numPredict, numCtx,
                ownerId, label, ct), ct);

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

    // Ждущий путь: ТОТ ЖЕ счётчик, тот же ключ и тот же потолок — слот берётся в общей
    // FIFO-очереди, и обгон стоящих в неё запрещён и для ночи (очередь одна на движке).
    // Признак Busy не взводится: фоновая заявка не должна отправлять в облако остальные
    // фоновые (тот же смысл, что у raiseBusy:false в TryEnter выше).
    // Отмена ct (остановка ночи, закрытие окна, остановка службы) уходит наружу как отмена,
    // а не как отказ «занято»: AcquireAsync сам снимает заявку с очереди и сам пробрасывает
    // OperationCanceledException по ct вызывающего — потолок ожидания даёт отказ только сам.
    private async Task<T> GuardAwaitingSlotAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        var (maxQueued, timeout) = slotWait?.Invoke() ?? LocalSlotWaitPolicy.Default;
        var max = limit();
        var admission = await tracker.AcquireAsync(EngineKey, max, maxQueued, timeout, ct, raiseBusy: false);
        if (admission.Slot is null)
        {
            logger.LogWarning(
                "Локальный движок «{Engine}» занят: слота не дождались ({Run} из {Limit}) — вызов отклонён",
                EngineKey, admission.Running, max);
            throw new LocalEngineSlotRefusalException(EngineKey, admission.Refusal, admission.Running, max);
        }
        using (admission.Slot) return await call();
    }
}

// Потолок ожидания слота для ждущего пути TrackedLocalLlmClient: сколько таких заявок может
// стоять в очереди ключа и сколько на них ждём. Значения задаёт тот же провайдер, что и для
// допуска ходов CLI (MaxQueuedTurns / TurnQueueTimeout): отдельного «своего» потолка для
// ночи не заводим (WN-4). Не передан — дефолты LlmProviderConfig, те же числа.
public sealed record LocalSlotWaitPolicy(int MaxQueued, TimeSpan Timeout)
{
    public static LocalSlotWaitPolicy Default { get; } = new(8, TimeSpan.FromMinutes(15));
}
