using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Images;

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

    // Контракт IImageGenerator: при model = null драйвер идёт на _model (ниже в
    // GenerateManyAsync: endpoint = model ?? _model). Здесь эффективная модель, а не
    // константа: с Fal:ImageModel пошёл бы другой endpoint, и учёт о нём соврал бы.
    string? IImageGenerator.DefaultModel => _model;

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
        var many = (await GenerateManyAsync(prompt, 1, ct)).Images;
        return many.Count > 0 ? many[0] : null;
    }

    // Сгенерировать несколько вариантов изображения по описанию (для выбора аватара).
    // Возвращает пустой Images, если fal выключен/ошибка.
    public Task<ImageGenerationResult> GenerateManyAsync(
        string prompt, int count, CancellationToken ct = default) =>
        GenerateManyAsync(prompt, count, null, ct);

    // Перегрузка с явной моделью (null — дефолт из конфига Fal:ImageModel).
    public async Task<ImageGenerationResult> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(prompt)) return ImageGenerationResult.Empty;
        count = Math.Clamp(count, 1, 4);
        var endpoint = string.IsNullOrWhiteSpace(model) ? _model : model.Trim().Trim('/');
        // Поднят над try: исключение после ответа (разбор тела, скачивание) не должно терять id —
        // до ответа он остаётся null (запрос не был принят).
        string? requestId = null;
        try
        {
            // Именованный клиент, общий с FalAccountService/FalCostService: под ним висит
            // тихий логгер (Program.cs), иначе заблокированный fal печатает Error со стектрейсом.
            var client = _http.CreateClient("fal");
            // Дальше 180 с ждём только приход заголовков: с ResponseHeadersRead (ниже) клиент
            // отдаёт ответ сразу после них, и до тела этот таймаут не дотягивается —
            // потолок чтения тела делает своим токеном, ResponseBodyTimeout.
            client.Timeout = HeadersTimeout;

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fal.run/{endpoint}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Key", _apiKey);
            req.Content = JsonContent.Create(BuildRequestBody(endpoint, prompt, count));

            // Тело читается отдельно от заголовков (п.2 хвоста R-S-SPEND-9a). С дефолтным
            // ResponseContentRead HttpClient буферизует тело внутри SendAsync: обрыв по своему
            // таймауту оставлял ответ невидимым, и id, который fal уже отправил заголовком,
            // терялся вместе с ним (проверяется мутацией на тесте ТаймаутЧтенияТела_IdСохраняется).
            // С ResponseHeadersRead ответ и его заголовки достаются сразу; скачивание картинок
            // это не касается — оно идёт своим путём через SafeMediaDownloader.
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // Идентификатор запроса берётся сразу после ответа, ДО проверки статуса и разбора
            // тела: у синхронного fal.run его в теле нет, он в заголовке (см. ResponseRequestId),
            // а запрос мог быть принят и оплачен даже при 4xx или без картинок.
            requestId = ResponseRequestId(resp);

            // Явный потолок чтения тела (свои 180 с, ResponseBodyTimeout). С headers-read клиент
            // сам тело не ограничивает (docs HttpCompletionOption), и без этого токена зависшее
            // тело висело бы до отмены вызывающего. Потолки пофазные: общий предел запроса —
            // TotalTimeout (заголовки + тело, ровно 360 с), а не прежние 180 с целиком.
            using var body = CancellationTokenSource.CreateLinkedTokenSource(ct);
            body.CancelAfter(ResponseBodyTimeout);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("fal генерация вернула {Status}: {Body}",
                    resp.StatusCode, await resp.Content.ReadAsStringAsync(body.Token));
                return new ImageGenerationResult([], requestId);
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(body.Token);
            // Не-объектное тело ([] , скаляр) — своя ветка, а не исключение в catch: TryGetProperty
            // на массиве бросает InvalidOperationException, и штатный случай выглядел в логе как
            // сбой связи (п.3 хвоста R-S-SPEND-9a). Поведение то же: id отдаётся и там и там.
            if (json.ValueKind != JsonValueKind.Object
                || !json.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array)
                return new ImageGenerationResult([], requestId);

            var result = new List<GeneratedImage>();
            foreach (var img in images.EnumerateArray())
            {
                var url = img.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;
                // content_type из ответа — запасной вариант, если storage не проставил заголовок
                // (у векторного recraft это image/svg+xml, и молча считать его png нельзя)
                var declared = img.TryGetProperty("content_type", out var c) ? c.GetString() : null;
                var (bytes, contentType) = await DownloadAsync(url, declared, ct);
                if (bytes is not null) result.Add(new GeneratedImage(bytes, contentType));
            }
            return new ImageGenerationResult(result, requestId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && requestId is null)
        {
            // Отмена вызывающего ДО ответа: id не пришёл — нечего ни тарифицировать, ни терять
            // (принят ли запрос, неизвестно: тот же задокументированный остаток, что в
            // RecordNoImagesSpend у роутера). Отмена идёт к вызывающему, как в соседнем драйвере
            // того же провайдера (FalImageEditor) и в опросе цены (FalCostService): проглотить её
            // здесь значит оставить ход работающим после того, как человек его отменил.
            _logger.LogInformation("Генерация через fal отменена вызывающим до ответа");
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Отмена ПОСЛЕ принятого ответа: запрос принят и, скорее всего, уже оплачен, id в руках.
            // Просто пробросить её нельзя — вместе с исключением потерялся бы id, а оплаченный
            // запрос исчез из учёта; id идёт обычным пустым результатом ([], id), по которому
            // роутер сам спросит цену и запишет трату, только если она есть (RecordNoImagesSpend),
            // а отмену продолжит сам — свой токен он знает (ImageGenerationService).
            _logger.LogInformation(
                "Генерация через fal отменена вызывающим после принятия запроса {RequestId}: id передан для учёта",
                requestId);
            return new ImageGenerationResult([], requestId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Свой потолок, а не сбой связи: ct вызывающего жив (его отмены разобраны выше), значит
            // не уложились заголовки (client.Timeout) или тело (ResponseBodyTimeout). Фазу выдаёт
            // id: он берётся из заголовков, так что есть id — не уложилось тело, нет — заголовки.
            // Поведение то же: пустой результат, id остаётся, если ответ был.
            _logger.LogWarning(
                "fal генерация не уложилась в потолок фазы {Phase} ({Headers}с на заголовки + {Body}с на тело, предел фаз HTTP-запроса {Total}с); id запроса: {RequestId}",
                requestId is null ? "заголовков" : "тела",
                HeadersTimeout.TotalSeconds, ResponseBodyTimeout.TotalSeconds, TotalTimeout.TotalSeconds, requestId);
            return new ImageGenerationResult([], requestId);
        }
        catch (Exception ex)
        {
            // Сбой связи или разбора тела: поведение как раньше, пустой результат; id остаётся,
            // если ответ был. Отменённый вызывающим ход и свои таймауты сюда не доходят: они
            // разобраны ветками выше.
            _logger.LogWarning(ex, "Ошибка генерации аватара через fal");
            return new ImageGenerationResult([], requestId);
        }
    }

    // Потолки пофазные: с ResponseHeadersRead client.Timeout до тела не дотягивается
    // (docs: HttpCompletionOption), поэтому на приход заголовков и на чтение тела свои 180 с.
    // Предел фаз HTTP-запроса к fal — их сумма, ровно 360 с (180 + 180: CTS тела создаётся сразу
    // после возврата заголовков, зазора нет). Скачивание картинок сюда не входит: у него свой
    // потолок (SafeMediaDownloader.Timeout, 60 с).
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ResponseBodyTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan TotalTimeout = HeadersTimeout + ResponseBodyTimeout;

    // Идентикатор запроса — из заголовка x-fal-request-id: синхронный fal.run его в тело
    // не кладёт (тело — вывод модели, форма зависит от схемы), а фал отдаёт его заголовком
    // и тем же id ключуется billing-events (docs fal: «Request Headers», «Billing Events»).
    // Существующий SpendMapping.TryExtractFalRequestId здесь неприменим: он разбирает ТЕЛО
    // tool_result чата (JSON/текст с маркером изображения) и живёт в сборке Main, куда
    // вертикаль Images не ссылается. Своего разбора не заводим — поля читать не надо.
    private static string? ResponseRequestId(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("x-fal-request-id", out var values)
            ? values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;

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

    // Ссылка пришла от fal — качаем только через SafeMediaDownloader (SSRF, потолок размера)
    private static async Task<(byte[]? Bytes, string ContentType)> DownloadAsync(
        string url, string? declaredType, CancellationToken ct)
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

        var download = await SafeMediaDownloader.Shared.DownloadAsync(url, SafeMediaDownloader.ImageMaxBytes, ct);
        return (download.Bytes, download.ContentType ?? fallback);
    }
}
