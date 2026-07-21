using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Отбор БЕСПЛАТНЫХ моделей OpenRouter опросом GET {ApiBaseUrl}/models (OpenAI-совместимый).
// Один запрос, два фильтра поверх ответа:
//   • Agentic — годятся для агентского режима (провайдер через claude CLI): free +
//     поддержка tools и tool_choice + окно ≥ AgenticMinContext. Только с ними claude CLI
//     не спотыкается на tool-use.
//   • все free (Agentic=любой) — годятся для прямого HTTP-адаптера фоновых one-shot задач
//     (CloudCheapClient): требование одно — бесплатность и разумное окно (DirectMinContext),
//     агентность не нужна.
// Каталог сужается КОДОМ, а не ручным списком в конфиге: у OpenRouter 300+ моделей,
// состав и цены меняются. Кэш 6 ч; при сбое опроса — пустой список (потребители
// деградируют на локаль/claude).
public sealed class OpenRouterCatalogService(
    IHttpClientFactory httpFactory, LlmProviderRegistry providers, IConfiguration config,
    ILogger<OpenRouterCatalogService> log)
{
    // Ключ провайдера-агрегатора в LlmProviders, чей эндпоинт/ключ используем
    private readonly string _providerKey =
        config["OpenRouter:Provider"] is { Length: > 0 } p ? p : "openrouter";
    // Пороги окна: агентские — большие задачи и контекст инструментов; прямые — фоновый
    // one-shot (профиль Large грузит до ~16K, берём вдвое с запасом)
    private readonly int _agenticMinContext = config.GetValue("OpenRouter:AgenticMinContext", 200_000);
    private readonly int _directMinContext = config.GetValue("OpenRouter:DirectMinContext", 32_000);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryTtl = TimeSpan.FromMinutes(5);

    // Free-модель агрегатора. Agentic — прошла агентский фильтр (tools + окно); для прямого
    // адаптера флаг игнорируется (там годятся все free).
    public sealed record FreeModel(string Id, string DisplayName, int ContextWindow, bool Agentic);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<FreeModel>? _cached;
    private DateTime _cachedAt;
    private bool _lastFailed;

    // Настроен ли провайдер-источник (ключ + эндпоинт) — иначе отбирать нечем
    public bool Configured =>
        providers.GetByKey(_providerKey) is { Enabled: true } p && !string.IsNullOrWhiteSpace(p.ApiBaseUrl);

    // Все бесплатные модели (окно ≥ DirectMinContext) — для прямого адаптера
    public async Task<IReadOnlyList<FreeModel>> GetFreeModelsAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    // Только агентские бесплатные (tools + окно ≥ AgenticMinContext) — для провайдера (CLI)
    public async Task<IReadOnlyList<FreeModel>> GetAgenticFreeModelsAsync(CancellationToken ct = default) =>
        (await LoadAsync(ct)).Where(m => m.Agentic).ToList();

    private async Task<List<FreeModel>> LoadAsync(CancellationToken ct)
    {
        if (IsFresh()) return _cached!;
        await _lock.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _cached!;
            var fresh = await QueryAsync(ct);
            _lastFailed = fresh is null;
            if (fresh is not null) _cached = fresh;
            _cached ??= [];
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        finally { _lock.Release(); }
    }

    private bool IsFresh()
    {
        if (_cached is null) return false;
        return DateTime.UtcNow - _cachedAt < (_lastFailed ? RetryTtl : CacheTtl);
    }

    // null — опрос не удался (сеть/провайдер выключен); кэш держит прежний успех, если был
    private async Task<List<FreeModel>?> QueryAsync(CancellationToken ct)
    {
        var p = providers.GetByKey(_providerKey);
        if (p is not { Enabled: true } || string.IsNullOrWhiteSpace(p.ApiBaseUrl)) return null;
        try
        {
            var client = httpFactory.CreateClient("llm-provider");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/models");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await client.SendAsync(req, timeoutCts.Token);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<FreeModel>();
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!IsFree(m)) continue;
                var ctx = m.TryGetProperty("context_length", out var cl) && cl.ValueKind == JsonValueKind.Number
                    ? cl.GetInt32() : 0;
                if (ctx < _directMinContext) continue;

                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                var agentic = ctx >= _agenticMinContext && HasToolUse(m);
                result.Add(new FreeModel(id, name, ctx, agentic));
            }
            log.LogInformation("OpenRouter: отобрано {Total} бесплатных моделей ({Agentic} агентских)",
                result.Count, result.Count(x => x.Agentic));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "OpenRouter: опрос /models не удался");
            return null;
        }
    }

    // Бесплатная = нулевая цена и промпта, и ответа (строки "0"/"0.0" либо число 0)
    private static bool IsFree(JsonElement m)
    {
        if (!m.TryGetProperty("pricing", out var pr) || pr.ValueKind != JsonValueKind.Object) return false;
        return IsZero(pr, "prompt") && IsZero(pr, "completion");
    }

    private static bool IsZero(JsonElement pricing, string field)
    {
        if (!pricing.TryGetProperty(field, out var el)) return false;
        var raw = el.ValueKind == JsonValueKind.Number ? el.GetDouble()
            : double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        return raw == 0;
    }

    // Пригодна для агентского харнеса: заявляет и tools, и tool_choice
    private static bool HasToolUse(JsonElement m)
    {
        if (!m.TryGetProperty("supported_parameters", out var sp) || sp.ValueKind != JsonValueKind.Array)
            return false;
        bool tools = false, choice = false;
        foreach (var el in sp.EnumerateArray())
        {
            var s = el.GetString();
            if (s == "tools") tools = true;
            else if (s == "tool_choice") choice = true;
        }
        return tools && choice;
    }
}
