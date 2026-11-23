using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Живая идентичность локального движка для панели «Локальные модели»: что стенд отдаёт
// в /v1/models, не сравнивая с каталогом (CheckAsync ищет только id из каталога и при смене
// served-model-name вернёт Down — панели нужен сам факт расхождения). Отдельная проба с
// отдельным кэшем и single-flight по нормализованному BaseUrl: два одновременных чтения
// одного URL — один HTTP /v1/models и общий результат; ответ сменившегося движка не уходит
// в кэш другого URL. Таймаут, HTTP-ошибка, битый JSON и неоднозначный ответ (несколько
// активных записей) не бросают исключений наружу и не оставляют вчерашнее live-имя свежее:
// кэш по URL убирается, наружу едет пустой снимок.
public interface ILocalEngineIdentityProbe
{
    Task<EngineIdentity> ProbeAsync(string? baseUrl, CancellationToken ct = default);
}

// Снимок живой идентичности: null-поля — «не измерено». ModelLive — id записи, однозначно
// загруженной (не обязательно совпадающей с каталогом). ContextWindowLive — max_model_len,
// запасной вариант engine.max_context из JSON /metrics (только Strata-адаптер, у него /metrics
// — JSON). EngineLive — только если S1 измерил поле целиком; у замеренного Inkling такого поля
// нет (версия движка не отдаётся), поэтому на текущих фикстурах всегда null.
public sealed record EngineIdentity(string? ModelLive, int? ContextWindowLive, string? EngineLive)
{
    public static readonly EngineIdentity Empty = new(null, null, null);
}

public sealed class LocalEngineIdentityProbe : ILocalEngineIdentityProbe
{
    // Таймаут запроса: как у LocalEndpointProbe — локальный стенд с холодной моделью отвечает медленно.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    // Кеш на панельный опрос (раз в секунды): 5 с достаточно, живое имя не скачет чаще смены движка.
    private static readonly TimeSpan DefaultCacheFor = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _cacheFor;

    private sealed record CacheEntry(DateTime Until, EngineIdentity Identity);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<EngineIdentity>>> _inflight = new();

    public LocalEngineIdentityProbe(IHttpClientFactory httpFactory,
        TimeSpan? timeout = null,
        TimeSpan? cacheFor = null)
    {
        _httpFactory = httpFactory;
        _timeout = timeout ?? DefaultTimeout;
        _cacheFor = cacheFor ?? DefaultCacheFor;
    }

    internal static string Normalize(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    public async Task<EngineIdentity> ProbeAsync(string? baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return EngineIdentity.Empty;
        var key = Normalize(baseUrl);
        if (_cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow)
            return cached.Identity;

        // Lazy: задача стартует только при первом обращении, ConcurrentDictionary.GetOrAdd
        // не гарантирует однократный вызов фабрики — без Lazy N читателей запустят N проб.
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<EngineIdentity>>(
            () => ProbeInternalAsync(k), LazyThreadSafetyMode.ExecutionAndPublication));
        EngineIdentity result;
        try
        {
            result = await lazy.Value;
        }
        catch (Exception)
        {
            // Внутренняя проба исключений наружу не отдаёт; страховка от отмены caller'а.
            result = EngineIdentity.Empty;
        }

        // Неудача не записывается в кэш и снимает вчерашний свежий ответ: live-имя не должно
        // «жить» дальше таймаута источника.
        if (result == EngineIdentity.Empty)
            _cache.TryRemove(key, out _);
        else
            _cache[key] = new CacheEntry(DateTime.UtcNow.Add(_cacheFor), result);
        // Inflight снимаем ПОСЛЕ записи кэша: читатель, пришедший между await и записью кэша,
        // ещё найдёт Lazy и присоединится к той же задаче.
        _inflight.TryRemove(key, out _);
        return result;
    }

    private async Task<EngineIdentity> ProbeInternalAsync(string trimmedBaseUrl)
    {
        HttpClient http;
        try
        {
            http = _httpFactory.CreateClient(LocalEndpointProbe.HttpClientName);
        }
        catch (Exception)
        {
            return EngineIdentity.Empty;
        }

        using var cts = new CancellationTokenSource(_timeout);
        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(LocalEndpointProbe.BuildModelsUrl(trimmedBaseUrl), cts.Token);
        }
        catch (Exception)
        {
            return EngineIdentity.Empty;
        }
        if (!resp.IsSuccessStatusCode) return EngineIdentity.Empty;

        JsonElement root;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            root = doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return EngineIdentity.Empty;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return EngineIdentity.Empty;

        var candidates = new List<(string Id, int? MaxLen)>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;

            // Активная запись: статус отсутствует (vLLM) или value="loaded" и failed != true.
            var loaded = true;
            if (entry.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Object)
            {
                if (statusEl.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String)
                {
                    var val = valEl.GetString();
                    if (!string.Equals(val, "loaded", StringComparison.OrdinalIgnoreCase)) loaded = false;
                }
                if (statusEl.TryGetProperty("failed", out var failEl) && failEl.ValueKind == JsonValueKind.True)
                    loaded = false;
            }
            if (!loaded) continue;

            int? maxLen = null;
            if (entry.TryGetProperty("max_model_len", out var mlEl) && mlEl.ValueKind == JsonValueKind.Number
                && mlEl.TryGetInt32(out var v) && v > 0)
            {
                maxLen = v;
            }
            candidates.Add((id, maxLen));
        }

        if (candidates.Count != 1) return EngineIdentity.Empty; // несколько кандидатов — не угадываем
        var (liveId, liveMaxLen) = candidates[0];
        var window = liveMaxLen ?? await ReadEngineMaxContextAsync(trimmedBaseUrl);
        return new EngineIdentity(liveId, window, null);
    }

    // Запасное окно: engine.max_context из JSON /metrics (Strata-адаптер); для vLLM это промах — окна нет.
    private async Task<int?> ReadEngineMaxContextAsync(string trimmedBaseUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(_timeout);
            var http = _httpFactory.CreateClient(LocalEndpointProbe.HttpClientName);
            using var resp = await http.GetAsync(LocalEndpointProbe.BuildMetricsUrl(trimmedBaseUrl), cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("engine", out var engine)
                || engine.ValueKind != JsonValueKind.Object
                || !engine.TryGetProperty("max_context", out var mc)
                || mc.ValueKind != JsonValueKind.Number
                || !mc.TryGetInt32(out var v) || v <= 0)
                return null;
            return v;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
