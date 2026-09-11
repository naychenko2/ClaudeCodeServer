using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Images;

// Генерация изображений через glif.app — второй драйвер IImageGenerator (ключ "glif"),
// серверный путь без участия CLI-хода. Транспорт как у GlifAccountService: JSON-RPC
// tools/call на https://glif.app/api/mcp, Bearer Glif:McpToken, endpoint stateless
// (без initialize), ответ приходит SSE-кадром либо чистым JSON.
//
// Генерация асинхронная: compose_project сразу отдаёт jobId и pollIntervalSeconds,
// готовое медиа забирается опросом get_job_status. Схемы инструментов сверены с живым
// tools/list (2026-08-15): compose_project { prompt, project_id, attachments,
// output_preference: auto|image|video|audio|web|code, rationale, intelligence:
// lite|smart|genius }, additionalProperties: false (лишних полей слать нельзя);
// get_job_status { job_id } (обязателен).
//
// Без токена — Enabled=false: роутер молча уходит на fal.
public sealed class GlifImageGenerator : IImageGenerator
{
    private const string McpUrl = "https://glif.app/api/mcp";

    // Общий бюджет на генерацию: сам glif в описании compose_project заявляет штатное
    // время прогона 1–5 минут, поэтому ждём с запасом. Прежние 180с рвали ход на
    // середине штатной генерации — человек видел долгое ожидание и отказ. Если и 360с
    // не хватило, картинку догонит очередь ImageBackfillService (причина «no-image»
    // у неё транзиентная — повтор будет).
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(360);
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxPoll = TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyList<ImageModelInfo> Tiers =
    [
        new("lite", "Lite", "Дешевле и быстрее · простые иконки"),
        new("smart", "Smart", "Баланс качества и цены · по умолчанию"),
        new("genius", "Genius", "Самый сильный агент · сложные сюжеты"),
    ];

    private readonly IHttpClientFactory _http;
    private readonly string? _token;
    private readonly ILogger<GlifImageGenerator> _logger;

    public string Key => "glif";
    public string DisplayName => "glif.app";

    public bool Enabled => !string.IsNullOrWhiteSpace(_token);

    // У glif модель выбирает сам агент — пользователю доступен только уровень агента
    // (intelligence). Он и играет роль «модели» в пикере.
    public IReadOnlyList<ImageModelInfo> Models => Tiers;

    public GlifImageGenerator(IHttpClientFactory http, IConfiguration config, ILogger<GlifImageGenerator> logger)
    {
        _http = http;
        _token = config["Glif:McpToken"];
        _logger = logger;
    }

    public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(prompt)) return [];
        count = Math.Clamp(count, 1, 4);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TotalTimeout);
        var token = timeout.Token;

        try
        {
            // Именованный клиент, общий с GlifAccountService
            var client = _http.CreateClient("glif");

            var job = await CallToolAsync(client, "compose_project", BuildComposeArgs(prompt, count, model), token);
            if (job.State == GlifJobState.Failed || string.IsNullOrWhiteSpace(job.JobId))
            {
                _logger.LogWarning("glif: compose_project не стартовал ({Error})", job.Error ?? "нет jobId");
                return [];
            }

            // Последний разобранный ответ — по нему объясняем в логе пустой финал
            var finished = job;
            IReadOnlyList<string> urls = job.State == GlifJobState.Completed ? job.MediaUrls : [];
            var poll = ClampPoll(job.PollIntervalSeconds);

            while (urls.Count == 0)
            {
                await Task.Delay(poll, token);
                var status = await CallToolAsync(client, "get_job_status",
                    new Dictionary<string, object?> { ["job_id"] = job.JobId }, token);

                if (status.State == GlifJobState.Failed)
                {
                    _logger.LogWarning("glif: джоба {JobId} провалилась ({Error})", job.JobId, status.Error);
                    return [];
                }
                if (status.State == GlifJobState.Completed)
                {
                    finished = status;
                    urls = status.MediaUrls;
                    break;
                }
                // Сервер может переопределить интервал по ходу опроса — уважаем
                poll = ClampPoll(status.PollIntervalSeconds ?? poll.TotalSeconds);
            }

            if (urls.Count == 0)
            {
                LogEmptyResult(finished);
                return [];
            }

            var result = new List<GeneratedImage>();
            foreach (var url in urls)
            {
                if (result.Count >= count) break;
                var image = await ImageDownload.FetchAsync(client, url, token);
                if (image is not null) result.Add(image);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;      // отмена снаружи — не наша ошибка, пробрасываем
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("glif: генерация не уложилась в {Seconds}с", TotalTimeout.TotalSeconds);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка генерации картинки через glif");
            return [];
        }
    }

    // Джоба завершилась, а картинки нет. Два разных случая, и в логе они должны читаться
    // по-разному: агент вместо генерации ответил текстом/вопросом («Want me to iterate…»,
    // поля summary + interaction) — это его штатное поведение, промпт надо уточнять;
    // либо пустой ответ сервера — это транзиентный сбой, лечится повтором.
    private void LogEmptyResult(GlifJobResult job)
    {
        if (job.HasAgentReply)
            _logger.LogWarning(
                "glif: агент по джобе {JobId} ответил текстом вместо картинки: «{Summary}» (interaction: {Interaction}) — нужен более однозначный промпт",
                job.JobId, Short(job.Summary) ?? "(без summary)", Short(job.Interaction) ?? "нет");
        else
            _logger.LogWarning("glif: джоба {JobId} завершилась без медиа", job.JobId);
    }

    private static string? Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ReplaceLineEndings(" ");
        return t.Length > 300 ? t[..300] + "…" : t;
    }

    // Аргументы compose_project. num_images у glif нет — число вариантов просим текстом;
    // сколько реально придёт, покажет медиа готовой джобы.
    private static Dictionary<string, object?> BuildComposeArgs(string prompt, int count, string? model)
    {
        var text = count > 1
            ? $"{prompt.Trim()}\n\nGenerate {count} distinct variations of this image."
            : prompt.Trim();

        var args = new Dictionary<string, object?>
        {
            ["prompt"] = text,
            ["output_preference"] = "image",
        };
        // Модель = уровень агента; чужое значение схема отвергнет, поэтому пропускаем только свои
        if (!string.IsNullOrWhiteSpace(model)
            && Tiers.Any(t => string.Equals(t.Id, model.Trim(), StringComparison.OrdinalIgnoreCase)))
            args["intelligence"] = model.Trim().ToLowerInvariant();
        return args;
    }

    private static TimeSpan ClampPoll(double? seconds)
    {
        if (seconds is not { } s || double.IsNaN(s) || s <= 0) return DefaultPoll;
        var span = TimeSpan.FromSeconds(s);
        return span < MinPoll ? MinPoll : span > MaxPoll ? MaxPoll : span;
    }

    private async Task<GlifJobResult> CallToolAsync(
        HttpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, McpUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        // MCP Streamable HTTP: POST обязан принимать оба типа контента
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("glif: {Tool} вернул {Status}: {Body}", tool, resp.StatusCode, text);
            return GlifJobResult.Empty with { State = GlifJobState.Failed, Error = $"HTTP {(int)resp.StatusCode}" };
        }
        return GlifJobParser.Parse(text);
    }
}

// Скачивание медиа в байты: обычный URL и data:-URL. Логика повторяет
// FalImageService.DownloadAsync — тот драйвер переедет сюда отдельной волной.
internal static class ImageDownload
{
    public static async Task<GeneratedImage?> FetchAsync(HttpClient client, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:image/png;base64,....
            var comma = url.IndexOf(',');
            if (comma < 0) return null;
            var meta = url[5..comma];
            var contentType = meta.Split(';')[0];
            if (string.IsNullOrEmpty(contentType)) contentType = "image/png";
            try { return new GeneratedImage(Convert.FromBase64String(url[(comma + 1)..]), contentType); }
            catch (FormatException) { return null; }
        }

        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return null;
        var type = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
        return new GeneratedImage(bytes, type);
    }
}
