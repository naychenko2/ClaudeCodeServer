using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services;

// Каталог моделей: у Claude спрашивает claude CLI актуальный список моделей аккаунта
// (control request initialize → поле models в ответе) и кэширует его; если CLI недоступен —
// отдаёт статический fallback. Модели DeepSeek (при заданном ApiKey): конфиг DeepSeek:Models
// (окна/цены/thinking) + опрос GET /models их API — новые модели дописываются с дефолтами.
public class ModelCatalogService(IOptions<DeepSeekOptions> deepSeekOptions, IHttpClientFactory httpFactory)
{
    public record ModelInfo(string Value, string DisplayName, string? Description,
        string Provider = "claude", int? ContextWindow = null);

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

    // Кэш опроса DeepSeek API (GET /models) — отдельный от кэша claude CLI
    private readonly SemaphoreSlim _dsLock = new(1, 1);
    private List<string>? _dsApiIds;
    private DateTime _dsCachedAt;
    private bool _dsQueryFailed;

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
                    try { fresh = await QueryCliAsync(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ModelCatalog] Не удалось получить список моделей: {ex.Message}");
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

        return await WithDeepSeekAsync(claude, ct);
    }

    // Модели DeepSeek: записи конфига (приоритет — несут окно/цены/thinking) + модели
    // из их API, которых в конфиге нет (с дефолтами). Без ApiKey провайдер выключен.
    private async Task<IReadOnlyList<ModelInfo>> WithDeepSeekAsync(List<ModelInfo> claudeModels, CancellationToken ct)
    {
        var opts = deepSeekOptions.Value;
        if (!opts.Enabled) return claudeModels;

        var result = new List<ModelInfo>(claudeModels);
        result.AddRange(opts.Models.Select(m =>
            new ModelInfo(m.Id, m.DisplayName, null, "deepseek", m.ContextWindow)));

        var known = new HashSet<string>(
            opts.Models.SelectMany(m => new[] { m.Id, m.EffectiveApiModel }),
            StringComparer.OrdinalIgnoreCase);
        foreach (var id in await QueryDeepSeekApiAsync(opts, ct))
            if (!known.Contains(id))
                result.Add(new ModelInfo(id, id, "из API DeepSeek — окно/цены не настроены", "deepseek"));

        return result;
    }

    private async Task<IReadOnlyList<string>> QueryDeepSeekApiAsync(DeepSeekOptions opts, CancellationToken ct)
    {
        var ttl = _dsQueryFailed ? RetryTtl : CacheTtl;
        if (_dsApiIds is not null && DateTime.UtcNow - _dsCachedAt < ttl) return _dsApiIds;

        await _dsLock.WaitAsync(ct);
        try
        {
            if (_dsApiIds is not null && DateTime.UtcNow - _dsCachedAt < ttl) return _dsApiIds;
            try
            {
                var client = httpFactory.CreateClient("deepseek");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{opts.BaseUrl.TrimEnd('/')}/models");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                using var resp = await client.SendAsync(req, timeoutCts.Token);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeoutCts.Token));
                var ids = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var m in data.EnumerateArray())
                        // Только deepseek-префикс: по нему резолвится провайдер (LlmProviderResolver)
                        if (m.TryGetProperty("id", out var id) && id.GetString() is { } s
                            && s.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
                            ids.Add(s);
                _dsApiIds = ids;
                _dsQueryFailed = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ModelCatalog] Опрос моделей DeepSeek не удался: {ex.Message}");
                _dsApiIds ??= [];
                _dsQueryFailed = true;
            }
            _dsCachedAt = DateTime.UtcNow;
            return _dsApiIds;
        }
        finally { _dsLock.Release(); }
    }

    private bool IsCacheFresh()
    {
        if (_cached is null) return false;
        var ttl = _lastQueryFailed ? RetryTtl : CacheTtl;
        return DateTime.UtcNow - _cachedAt < ttl;
    }

    // Короткоживущий процесс claude: шлём initialize в stdin, ждём control_response с models
    private static async Task<List<ModelInfo>?> QueryCliAsync(CancellationToken ct)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = Llm.Claude.ClaudeCliLocator.FindClaudeExecutable(),
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            CreateNoWindow = true
        };
        foreach (var a in new[]
                 {
                     "--print", "--verbose", "--strict-mcp-config",
                     "--input-format", "stream-json", "--output-format", "stream-json"
                 })
            psi.ArgumentList.Add(a);

        const string requestId = "model-catalog";
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить claude process");
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
            foreach (var m in modelsEl.EnumerateArray())
            {
                var value = m.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(value)) continue;
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
