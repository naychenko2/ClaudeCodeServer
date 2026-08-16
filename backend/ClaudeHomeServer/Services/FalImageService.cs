using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeHomeServer.Services.Images;

namespace ClaudeHomeServer.Services;

// Генерация изображений через fal.ai (тем же ключом Fal:ApiKey, что и учёт стоимости).
// Синхронный вызов fal.run/{model}: возвращает картинку, которую мы скачиваем в байты.
// Драйвер IImageGenerator (ключ "fal"); выбором между драйверами занимается
// ImageGenerationService. Без ключа — Enabled=false (генерация недоступна).
public class FalImageService : IImageGenerator
{
    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly ILogger<FalImageService> _logger;
    private readonly IReadOnlyList<ImageModelInfo> _models;

    public string Key => "fal";
    public string DisplayName => "fal.ai";

    public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

    public IReadOnlyList<ImageModelInfo> Models => _models;

    public FalImageService(IHttpClientFactory http, IConfiguration config, ILogger<FalImageService> logger)
    {
        _http = http;
        _apiKey = config["Fal:ApiKey"] ?? Environment.GetEnvironmentVariable("FAL_KEY");
        // Быстрая дешёвая модель для аватаров; переопределяется конфигом
        _model = (config["Fal:ImageModel"] ?? "fal-ai/flux/schnell").Trim('/');
        _logger = logger;
        _models = BuildModels(_model);
    }

    // Курируемый список для пикера: две проверенные модели flux плюс модель из конфига,
    // если админ прописал в Fal:ImageModel что-то своё (иначе она бы не выбиралась в UI).
    private static IReadOnlyList<ImageModelInfo> BuildModels(string configured)
    {
        var models = new List<ImageModelInfo>
        {
            new("fal-ai/flux/schnell", "FLUX schnell", "Быстрая и дешёвая · иконки и простые сюжеты"),
            new("fal-ai/flux/dev", "FLUX dev", "Качественнее и дороже · лица и мелкие детали"),
        };
        if (!string.IsNullOrWhiteSpace(configured)
            && !models.Any(m => string.Equals(m.Id, configured, StringComparison.OrdinalIgnoreCase)))
            models.Insert(0, new ImageModelInfo(configured, configured, "Модель из конфига Fal:ImageModel"));
        return models;
    }

    // Сгенерировать одно квадратное изображение (первый вариант).
    public async Task<GeneratedImage?> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var many = await GenerateManyAsync(prompt, 1, ct);
        return many.Count > 0 ? many[0] : null;
    }

    // Сгенерировать несколько вариантов изображения по описанию (для выбора аватара).
    // Возвращает пустой список, если fal выключен/ошибка.
    public Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, CancellationToken ct = default) =>
        GenerateManyAsync(prompt, count, null, ct);

    // Перегрузка с явной моделью (null — дефолт из конфига Fal:ImageModel).
    public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(prompt)) return [];
        count = Math.Clamp(count, 1, 4);
        var endpoint = string.IsNullOrWhiteSpace(model) ? _model : model.Trim().Trim('/');
        try
        {
            // Именованный клиент, общий с FalAccountService/FalCostService: под ним висит
            // тихий логгер (Program.cs), иначе заблокированный fal печатает Error со стектрейсом.
            var client = _http.CreateClient("fal");
            client.Timeout = TimeSpan.FromSeconds(180);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fal.run/{endpoint}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);
            req.Content = JsonContent.Create(new
            {
                prompt,
                image_size = "square",
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
            if (!json.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<GeneratedImage>();
            foreach (var img in images.EnumerateArray())
            {
                var url = img.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;
                var (bytes, contentType) = await DownloadAsync(client, url, ct);
                if (bytes is not null) result.Add(new GeneratedImage(bytes, contentType));
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
