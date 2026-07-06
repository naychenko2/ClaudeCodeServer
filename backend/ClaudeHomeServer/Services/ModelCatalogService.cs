using System.Diagnostics;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Каталог моделей Claude: спрашивает у claude CLI актуальный список моделей аккаунта
// (control request initialize → поле models в ответе) и кэширует его.
// Если CLI недоступен — отдаём статический fallback и повторяем попытку позже.
public class ModelCatalogService
{
    public record ClaudeModel(string Value, string DisplayName, string? Description);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    // Алиасы вместо конкретных версий — не протухают при выходе новых моделей
    private static readonly List<ClaudeModel> Fallback =
    [
        new("default", "Default", null),
        new("opus", "Opus", null),
        new("sonnet", "Sonnet", null),
        new("haiku", "Haiku", null),
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<ClaudeModel>? _cached;
    private DateTime _cachedAt;
    // Последняя попытка опроса CLI провалилась → кэш живёт RetryTtl вместо CacheTtl
    private bool _lastQueryFailed;

    public async Task<IReadOnlyList<ClaudeModel>> GetModelsAsync(CancellationToken ct = default)
    {
        if (IsCacheFresh()) return _cached!;

        await _lock.WaitAsync(ct);
        try
        {
            if (IsCacheFresh()) return _cached!;

            List<ClaudeModel>? fresh = null;
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
            return _cached;
        }
        finally { _lock.Release(); }
    }

    private bool IsCacheFresh()
    {
        if (_cached is null) return false;
        var ttl = _lastQueryFailed ? RetryTtl : CacheTtl;
        return DateTime.UtcNow - _cachedAt < ttl;
    }

    // Короткоживущий процесс claude: шлём initialize в stdin, ждём control_response с models
    private static async Task<List<ClaudeModel>?> QueryCliAsync(CancellationToken ct)
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

    private static List<ClaudeModel>? TryParseModels(string line, string requestId)
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

            var result = new List<ClaudeModel>();
            foreach (var m in modelsEl.EnumerateArray())
            {
                var value = m.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var displayName = m.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                var description = m.TryGetProperty("description", out var ds) ? ds.GetString() : null;
                result.Add(new ClaudeModel(value, displayName ?? value, description));
            }
            return result.Count > 0 ? result : null;
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }
}
