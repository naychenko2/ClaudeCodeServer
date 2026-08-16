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
        // Дефолт — сильная растровая модель (Gemini 3.1 Flash Image): flux/schnell 2024 года
        // рисует иконки заметно хуже. Переопределяется конфигом.
        _model = (config["Fal:ImageModel"] ?? DefaultModel).Trim('/');
        _logger = logger;
        _models = BuildModels(_model);
    }

    // Дефолтная модель драйвера. Растровая намеренно: векторный recraft отдаёт SVG,
    // и хотя мы его сохраняем корректно (.svg), делать вектор дефолтом рано —
    // он остаётся выбором в пикере.
    public const string DefaultModel = "fal-ai/nano-banana-2";

    // Курируемый список для пикера: актуальные модели (сверено с живым каталогом fal
    // 2026-08-16) плюс модель из конфига, если админ прописал в Fal:ImageModel что-то
    // своё (иначе она бы не выбиралась в UI).
    private static IReadOnlyList<ImageModelInfo> BuildModels(string configured)
    {
        var models = new List<ImageModelInfo>
        {
            new("fal-ai/nano-banana-2", "Nano Banana 2 (Google)",
                "По умолчанию · быстрая и сильная · иконки и аватары"),
            new("fal-ai/flux-2/klein/9b", "FLUX.2 klein",
                "Базовая text-to-image fal · универсальная"),
            new("fal-ai/recraft/v4.1/text-to-vector", "Recraft V4.1 вектор",
                "Плоские логотипы и иконки · отдаёт SVG, один вариант за раз"),
            new("fal-ai/flux/schnell", "FLUX schnell", "Быстрая и дешёвая · простые сюжеты"),
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
            req.Content = JsonContent.Create(BuildRequestBody(endpoint, prompt, count));

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
                // content_type из ответа — запасной вариант, если storage не проставил заголовок
                // (у векторного recraft это image/svg+xml, и молча считать его png нельзя)
                var declared = img.TryGetProperty("content_type", out var c) ? c.GetString() : null;
                var (bytes, contentType) = await DownloadAsync(client, url, declared, ct);
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

    // Тело запроса собирается ПО МОДЕЛИ: схемы входа у семейств fal разные, и лишний
    // параметр возвращает 422 (nano-banana не знает image_size, у recraft-вектора нет
    // num_images). Незнакомая модель из конфига идёт по flux-подобной схеме — она же
    // была единственной до появления каталога.
    internal static Dictionary<string, object?> BuildRequestBody(string model, string prompt, int count)
    {
        var id = model.Trim().Trim('/');

        // Gemini-семейство: размер задаётся не image_size, а парой aspect_ratio + resolution
        if (id.Contains("nano-banana", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["aspect_ratio"] = "1:1",
                ["resolution"] = "1K",
                ["num_images"] = count,
            };

        // Векторные модели recraft: только image_size, вариантов всегда один
        if (id.Contains("text-to-vector", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["image_size"] = SquareSize,
            };

        return new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["image_size"] = SquareSize,
            ["num_images"] = count,
        };
    }

    // 1024×1024: у "square" сторона 512, и такая иконка в UI выглядит мылом
    private const string SquareSize = "square_hd";

    private static async Task<(byte[]? Bytes, string ContentType)> DownloadAsync(
        HttpClient client, string url, string? declaredType, CancellationToken ct)
    {
        var fallback = string.IsNullOrWhiteSpace(declaredType) ? "image/png" : declaredType.Trim();

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:image/png;base64,....
            var comma = url.IndexOf(',');
            if (comma < 0) return (null, fallback);
            var meta = url[5..comma];
            var contentType = meta.Split(';')[0];
            if (string.IsNullOrEmpty(contentType)) contentType = fallback;
            try { return (Convert.FromBase64String(url[(comma + 1)..]), contentType); }
            catch { return (null, contentType); }
        }

        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return (null, fallback);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var ct2 = resp.Content.Headers.ContentType?.MediaType ?? fallback;
        return (bytes, ct2);
    }
}
