using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Состояние локального эндпоинта по результату пробы.
/// </summary>
public enum LocalProbeOutcome
{
    // Сервер ответил и модель загружена (status.value=="loaded" и status.failed!=true).
    Alive,
    // Сервер недоступен, ответил мусором или модель в статусе unloaded/failed.
    // Pre-flight FallbackLlmSessionAdapter должен завершить ход с TurnFailureText.LocalModelDown.
    Down,
    // Эндпоинт не сконфигурирован как локальный (нет IsLocal или провайдер не в реестре) —
    // проба не имеет смысла, вызывающий трактует как «не применять pre-flight».
    NotApplicable
}

/// <summary>
/// Pre-flight проба локального LLM-эндпоинта (vLLM/llama.cpp) — нужна, чтобы при остановленном
/// движке пользователь увидел понятную красную карточку «локальная модель не запущена» вместо
/// тихого ухода в фолбэк на платного облачного провайдера (GLM/MiniMax/Kimi), который бы зря
/// потратил лимиты на заведомо мёртвый эндпоинт.
///
/// ЗАЧЕМ. Отказ хода на локальном провайдере классифицируется как Unreachable — это «эндпоинт
/// мёртв». У отказа два корня, и лечатся противоположно:
///   • мёртвый эндпоинт вендора (glm/minimax/kimi упал) — лечится шагом цепочки на соседа;
///   • мёртвый локальный llama.cpp/vLLM — НЕ лечится цепочкой: сосед-облако отвечает за
///    свой эндпоинт, а локальный так и останется мёртвым. Шаг цепочки только сжигает лимит
///    и оставляет пользователя без объяснения «почему упал локальный, а не его облако».
/// Проба разводит эти случаи: локальный Down → FailLocalDownAsync, без подмены.
///
/// ПРОТОКОЛ. HTTP GET {AnthropicBaseUrl}/v1/models с парсингом JSON: для каждой модели
/// сервер отдаёт status.value ∈ {"loaded","unloaded"} и status.failed (bool). «Loaded»
/// без «failed:true» — Alive; иначе Down. Таймаут 1.5 с: локальный llama.cpp с разогревом
/// модели может отвечать дольше, чем EgressProbe.400ms. Кеш 5 с: за один ход фолбэк может
/// спрашивать пробу дважды (pre-flight + на повторе), кеш гасит пачку.
///
/// HTTP-уровень (а не TCP) — чтобы отличить «порт слушает, но модель unloaded» (TCP-коннект
/// успешен) от полного отказа. Health endpoint llama.cpp отвергнут: /health отдаёт
/// {"status":"ok"} даже при unloaded модели — не различает.
/// </summary>
public interface ILocalEndpointProbe
{
    Task<LocalProbeOutcome> CheckAsync(
        LlmProviderConfig provider,
        CancellationToken ct = default);

    // Тёплый сброс кеша (для тестов и редких ручных сценариев)
    void Invalidate(string providerKey);
}

/// <inheritdoc />
public sealed class LocalEndpointProbe : ILocalEndpointProbe
{
    public const string HttpClientName = "local-endpoint-probe";

    // Таймаут запроса: больше EgressProbe (400ms), потому что llama.cpp с холодной моделью
    // может ответить дольше, но мы на пути ОШИБКИ хода, а не на горячем цикле.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    // Кеш ответа на стороне клиента. За ход фолбэк спрашивает пробу максимум дважды
    // (pre-flight + повтор после ошибки); 5 с гасят пачку, но меньше паузы повтора хода
    // (5 с) и достаточно для одного хода.
    private static readonly TimeSpan DefaultCacheFor = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheFor;

    // Кеш по ключу провайдера: (когда проверяли, итог). Конкурентный доступ из разных
    // ходов — без блокировок на горячем пути.
    private readonly ConcurrentDictionary<string, (DateTime Until, LocalProbeOutcome Outcome)> _cache = new();

    public LocalEndpointProbe(IHttpClientFactory httpFactory,
        TimeSpan? timeout = null,
        TimeSpan? cacheFor = null)
    {
        _httpFactory = httpFactory;
        _timeout = timeout ?? DefaultTimeout;
        _cacheFor = cacheFor ?? DefaultCacheFor;
    }

    public async Task<LocalProbeOutcome> CheckAsync(LlmProviderConfig provider, CancellationToken ct = default)
    {
        // Не наш клиент — проба не нужна. Возвращаем NotApplicable вместо молчаливого false,
        // чтобы вызывающий мог отличить «провайдер не локальный» от «Alive без проверки».
        if (provider is null || !provider.IsLocal
            || string.IsNullOrWhiteSpace(provider.AnthropicBaseUrl))
            return LocalProbeOutcome.NotApplicable;

        // Каталог пуст: проба не знает, какую модель искать в /v1/models. Не выдумываем —
        // NotApplicable до HTTP, чтобы не ходить в сеть зазря и не маскировать ошибку
        // конфигурации провайдера под сетевой отказ.
        var wantedIds = provider.Models.Select(m => m.Id).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (wantedIds.Count == 0) return LocalProbeOutcome.NotApplicable;

        // Эндпоинт может быть многоэлементным (например, llama.cpp preset возвращает несколько
        // моделей). Проверяем модель по Id из каталога: если хоть одна loaded без failed:true —
        // Alive. Остальные модели llama.cpp-роутера пропускаем (нас интересует только та,
        // что в каталоге провайдера).
        var key = provider.Key;
        if (_cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow)
            return cached.Outcome;

        var outcome = await CheckInternalAsync(provider, ct);
        _cache[key] = (DateTime.UtcNow.Add(_cacheFor), outcome);
        return outcome;
    }

    public void Invalidate(string providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
            _cache.TryRemove(providerKey, out _);
    }

    private async Task<LocalProbeOutcome> CheckInternalAsync(LlmProviderConfig provider, CancellationToken ct)
    {
        // /v1/models — стандартный OpenAI-совместимый эндпоинт, llama.cpp/vLLM отдают его.
        // AnthropicBaseUrl у локального провайдера — база (например, http://127.0.0.1:8080);
        // /v1/models стыкуется к ней по обычным правилам HTTP.
        var url = BuildModelsUrl(provider.AnthropicBaseUrl!);
        HttpClient http;
        try
        {
            http = _httpFactory.CreateClient(HttpClientName);
        }
        catch (Exception)
        {
            // HttpClientName не зарегистрирован — это конфигурационная ошибка, не молчим:
            // возвращаем Down, чтобы pre-flight выдал понятную ошибку.
            return LocalProbeOutcome.Down;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(url, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Таймаут, ECONNREFUSED, TLS-обрыв, DNS-фейл — всё лечится одинаково: эндпоинт не
            // принял запрос вовремя. Возвращаем Down, без разделения причин.
            return LocalProbeOutcome.Down;
        }

        if (!resp.IsSuccessStatusCode) return LocalProbeOutcome.Down;

        JsonElement root;
        try
        {
            var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            root = doc.RootElement.Clone();
        }
        catch (Exception)
        {
            // 200 OK, но мусор вместо JSON — llama.cpp с битой моделью или прокси. Down.
            return LocalProbeOutcome.Down;
        }

        // Ищем модель по Id (из каталога провайдера). Если в каталоге несколько — Alive, если
        // хоть одна loaded без failed:true. Каталог проверяется в CheckAsync ДО HTTP, здесь
        // wantedIds уже непустой.
        var wantedIds = provider.Models.Select(m => m.Id).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return LocalProbeOutcome.Down;

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            // Не наша модель — пропускаем. На одном llama.cpp-роутере может крутиться несколько
            // моделей; нас интересует только та, что в каталоге провайдера.
            if (!wantedIds.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;

            // Нашли нашу модель. Читаем status.value и status.failed.
            var loaded = true;
            var failed = false;
            if (entry.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Object)
            {
                if (statusEl.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String)
                {
                    var val = valEl.GetString();
                    // "unloaded" / "not_loaded" / что-то ещё — считаем не готовым.
                    if (!string.Equals(val, "loaded", StringComparison.OrdinalIgnoreCase))
                        loaded = false;
                }
                if (statusEl.TryGetProperty("failed", out var failEl)
                    && failEl.ValueKind == JsonValueKind.True)
                {
                    failed = true;
                }
            }
            // Считаем модель живой только если явно loaded и НЕ failed.
            return (loaded && !failed) ? LocalProbeOutcome.Alive : LocalProbeOutcome.Down;
        }

        // Каталог провайдера не пуст, но /v1/models не вернул нужную модель — скорее всего
        // конфиг рассинхронизирован с реальным сервером. Считаем Down: безопаснее сказать
        // пользователю «не запущена», чем молча отдать ошибку лимита.
        return LocalProbeOutcome.Down;
    }

    // AnthropicBaseUrl — база (без пути); llama.cpp/vLLM кладут /v1/models под ней.
    // Если в конфиге база уже с /v1 (например, ApiBaseUrl использовали вместо AnthropicBaseUrl)
    // — отрезаем хвост, чтобы не получилось /v1/v1/models.
    internal static string BuildModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/models";
        return trimmed + "/v1/models";
    }
}
