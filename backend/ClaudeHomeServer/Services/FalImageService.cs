using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeHomeServer.Services;

// Генерация изображений через fal.ai (тем же ключом Fal:ApiKey, что и учёт стоимости).
// Синхронный вызов fal.run/{model}: возвращает картинку, которую мы скачиваем в байты.
// Используется для AI-аватаров персон. Без ключа — Enabled=false (генерация недоступна).
// При наличии SpendLogService логирует расход fal.ai для аналитики токенов.
public sealed record GeneratedImage(byte[] Bytes, string ContentType);

public class FalImageService
{
    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly SpendLogService? _spend;
    private readonly ILogger<FalImageService> _logger;

    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

    public FalImageService(IHttpClientFactory http, IConfiguration config, ILogger<FalImageService> logger,
        SpendLogService? spend = null)
    {
        _http = http;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        // Быстрая дешёвая модель для аватаров; переопределяется конфигом
        _model = (config["Fal:ImageModel"] ?? "fal-ai/flux/schnell").Trim('/');
        _spend = spend;
        _logger = logger;
    }

    // Сгенерировать одно квадратное изображение (первый вариант).
    public async Task<GeneratedImage?> GenerateAsync(string prompt, string? ownerId = null, CancellationToken ct = default)
    {
        var many = await GenerateManyAsync(prompt, 1, ownerId, ct);
        return many.Count > 0 ? many[0] : null;
    }

    // Сгенерировать несколько вариантов изображения по описанию (для выбора аватара).
    // Возвращает пустой список, если fal выключен/ошибка.
    // ownerId — владелец для логирования расхода в SpendLogService.
    public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, string? ownerId = null, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(prompt)) return [];
        count = Math.Clamp(count, 1, 4);
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(180);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fal.run/{_model}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);
            req.Content = JsonContent.Create(new
            {
                prompt,
                image_size = "square_hd",
                num_images = count,
            });

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("fal генерация вернула {Status}: {Body}",
                    resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
                return [];
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            // Парсим стоимость из ответа fal (если есть)
            double? cost = null;
            if (json.TryGetProperty("cost", out var costEl) && costEl.TryGetDouble(out var cv))
                cost = cv;

            // Скачиваем изображения
            var result = new List<GeneratedImage>();
            if (json.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in images.EnumerateArray())
                {
                    var url = img.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(url)) continue;
                    var (bytes, contentType) = await DownloadAsync(client, url, ct);
                    if (bytes is not null) result.Add(new GeneratedImage(bytes, contentType));
                }
            }

            // Логируем расход, если есть владелец и SpendLogService
            if (ownerId is not null && _spend is not null && result.Count > 0)
            {
                _spend.Append(
                    ownerId: ownerId,
                    projectId: null,
                    sessionId: null,
                    taskId: null,
                    personaId: null,
                    provider: "fal",
                    model: _model,
                    source: "fal",
                    ts: DateTime.UtcNow.ToString("O"),
                    inputTokens: 0,
                    outputTokens: 0,
                    cacheReadTokens: 0,
                    cacheCreationTokens: 0,
                    costUsd: cost ?? 0,
                    durationMs: null,
                    completed: true,
                    entityRef: null);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка генерации аватара через fal");
            return [];
        }
    }

    private static async Task<(byte[]? Bytes, string ContentType)> DownloadAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:image/png;base64,....
            var comma = url.IndexOf(',');
            if (comma < 0) return (null, "image/png");
            var meta = url[5..comma];
            var contentType = meta.Split(';')[0];
            if (string.IsNullOrEmpty(contentType)) contentType = "image/png";
            try { return (Convert.FromBase64String(url[(comma + 1)..]), contentType); }
            catch { return (null, contentType); }
        }

        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return (null, "image/png");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
        return (bytes, ct2);
    }
}
