using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Каталог моделей: у Claude спрашивает claude CLI актуальный список моделей аккаунта
// (control request initialize → поле models в ответе) и кэширует его; если CLI недоступен —
// отдаёт статический fallback. Модели CLI-провайдеров (LlmProviders, при заданном ApiKey):
// записи конфига (окна/цены) + опциональный опрос GET {ApiBaseUrl}/models
// (OpenAI-совместимый) — новые модели дописываются с дефолтами.
public class ModelCatalogService(LlmProviderRegistry providers, IHttpClientFactory httpFactory, IConfiguration config)
{
    // Опрос claude CLI можно выключить конфигом (ModelCatalog:QueryCli=false): интеграционные
    // тесты поднимают приложение десятки раз, и каждый прогрев каталога спавнил бы настоящий
    // claude.exe (мелькающие консольные окна его дочерних bash/cmd). Без опроса — Fallback.
    private readonly bool _queryCli = config.GetValue("ModelCatalog:QueryCli", true);

    // IsCurated=false — модель обнаружена опросом API провайдера, без ручной карточки
    // (нет описания/цен); UI может свернуть такие в «другие модели»
    public record ModelInfo(string Value, string DisplayName, string? Description,
        string Provider = "claude", int? ContextWindow = null, bool IsCurated = true);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    // Алиасы вместо конкретных версий — не протухают при выходе новых моделей
    private static readonly List<ModelInfo> Fallback =
    [
        new("default", "Default", null),
        new("opus", "Opus", null),
        new("sonnet", "Sonnet", null),
        new("haiku", "Haiku", null),
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<ModelInfo>? _cached;
    private DateTime _cachedAt;
    // Последняя попытка опроса CLI провалилась → кэш живёт RetryTtl вместо CacheTtl
    private bool _lastQueryFailed;

    // Кэш опроса API провайдеров (GET /models) — per-provider, отдельный от кэша claude CLI
    private sealed class ProviderApiCache
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public List<string>? Ids;
        public DateTime CachedAt;
        public bool QueryFailed;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderApiCache> _apiCaches = new();

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        List<ModelInfo> claude;
        if (IsCacheFresh())
        {
            claude = _cached!;
        }
        else
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (!IsCacheFresh())
                {
                    List<ModelInfo>? fresh = null;
                    if (_queryCli)
                    {
                        try { fresh = await QueryCliAsync(ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[ModelCatalog] Не удалось получить список моделей: {ex.Message}");
                        }
                    }

                    _lastQueryFailed = fresh is null or { Count: 0 };
                    // При провале сохраняем прежний успешный список, если он был
                    if (!_lastQueryFailed) _cached = fresh;
                    _cached ??= Fallback;
                    _cachedAt = DateTime.UtcNow;
                }
                claude = _cached!;
            }
            finally { _lock.Release(); }
        }

        var result = new List<ModelInfo>(claude);
        foreach (var p in providers.Enabled)
            await AppendProviderModelsAsync(result, p, ct);
        return result;
    }

    // Модели провайдера: записи конфига (приоритет — несут окно/цены) + модели из его API,
    // которых в конфиге нет (с дефолтами). Без ApiKey провайдер выключен и сюда не попадает.
    private async Task AppendProviderModelsAsync(List<ModelInfo> result, LlmProviderConfig p, CancellationToken ct)
    {
        result.AddRange(p.Models.Select(m =>
            new ModelInfo(m.Id, m.DisplayName, m.Description, p.Key, m.ContextWindow)));

        if (!p.QueryModelsApi || string.IsNullOrWhiteSpace(p.ApiBaseUrl)) return;

        // API-обнаруженные модели без ручной карточки: описания нет (UI решает, как показывать —
        // напр. свернуть в «другие модели»); IsCurated=false отличает их от курируемых
        var known = new HashSet<string>(p.Models.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in await QueryProviderApiAsync(p, ct))
            if (!known.Contains(id))
                result.Add(new ModelInfo(id, id, null, p.Key, IsCurated: false));
    }

    private async Task<IReadOnlyList<string>> QueryProviderApiAsync(LlmProviderConfig p, CancellationToken ct)
    {
        var cache = _apiCaches.GetOrAdd(p.Key, _ => new ProviderApiCache());
        var ttl = cache.QueryFailed ? RetryTtl : CacheTtl;
        if (cache.Ids is not null && DateTime.UtcNow - cache.CachedAt < ttl) return cache.Ids;

        await cache.Lock.WaitAsync(ct);
        try
        {
            if (cache.Ids is not null && DateTime.UtcNow - cache.CachedAt < ttl) return cache.Ids;
            try
            {
                var client = httpFactory.CreateClient("llm-provider");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.ApiBaseUrl.TrimEnd('/')}/models");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.ApiKey);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                using var resp = await client.SendAsync(req, timeoutCts.Token);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
                var ids = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var m in data.EnumerateArray())
                        // Только модели с префиксом провайдера: по нему резолвится провайдер
                        // (LlmProviderRegistry.ResolveByModel)
                        if (m.TryGetProperty("id", out var id) && id.GetString() is { } s
                            && s.StartsWith(p.EffectiveModelPrefix, StringComparison.OrdinalIgnoreCase))
                            ids.Add(s);
                cache.Ids = ids;
                cache.QueryFailed = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ModelCatalog] Опрос моделей {p.DisplayName} не удался: {ex.Message}");
                cache.Ids ??= [];
                cache.QueryFailed = true;
            }
            cache.CachedAt = DateTime.UtcNow;
            return cache.Ids;
        }
        finally { cache.Lock.Release(); }
    }

    private bool IsCacheFresh()
    {
        if (_cached is null) return false;
        var ttl = _lastQueryFailed ? RetryTtl : CacheTtl;
        return DateTime.UtcNow - _cachedAt < ttl;
    }

    // Короткоживущий процесс claude: шлём initialize в stdin, ждём control_response с models.
    // Системный вызов бэкенда — всегда локальная среда.
    private static async Task<List<ModelInfo>?> QueryCliAsync(CancellationToken ct)
    {
        var launcher = Execution.LocalProcessRunner.Instance;
        const string requestId = "model-catalog";
        using var process = launcher.Start(new Execution.ProcessSpec
        {
            FileName = launcher.ClaudeCliCommand,
            Args =
            [
                "--print", "--verbose", "--strict-mcp-config",
                "--input-format", "stream-json", "--output-format", "stream-json",
                // Хуки плагинов не нужны и плодят окна консоли на хосте
                .. Llm.Claude.ClaudeRuntimeSettings.HooksOffArgs(launcher)
            ],
            WorkingDirectory = Path.GetTempPath(),
            StdioEncoding = new System.Text.UTF8Encoding(false),
        });
        try
        {
            _ = process.StandardError.ReadToEndAsync(ct); // дренируем, чтобы не переполнить буфер

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "initialize" }
            }));
            await process.StandardInput.FlushAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(QueryTimeout);

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token);
                if (line is null) return null; // процесс завершился, ответа не было
                if (string.IsNullOrWhiteSpace(line)) continue;

                var models = TryParseModels(line, requestId);
                if (models is not null) return models;
            }
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
                try { process.Kill(); } catch { }
        }
    }

    private static List<ModelInfo>? TryParseModels(string line, string requestId)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "control_response") return null;
            var resp = root.GetProperty("response");
            if (resp.GetProperty("request_id").GetString() != requestId) return null;
            if (resp.GetProperty("subtype").GetString() != "success") return null;
            if (!resp.TryGetProperty("response", out var inner) ||
                !inner.TryGetProperty("models", out var modelsEl) ||
                modelsEl.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<ModelInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in modelsEl.EnumerateArray())
            {
                var value = m.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(value)) continue;
                // Сводим тир-алиас+окно (opus[1m]) к базовому алиасу (opus): CLI отдаёт Opus
                // только с суффиксом окна, а он хрупок при исполнении (см. StripClaudeWindowAlias),
                // и это единственный способ выбрать Opus в UI. Дедуп на случай коллизии value.
                value = Llm.LlmProviderRegistry.StripClaudeWindowAlias(value)!;
                if (!seen.Add(value)) continue;
                var displayName = m.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                var description = m.TryGetProperty("description", out var ds) ? ds.GetString() : null;
                result.Add(new ModelInfo(value, displayName ?? value, description));
            }
            return result.Count > 0 ? result : null;
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }
}
