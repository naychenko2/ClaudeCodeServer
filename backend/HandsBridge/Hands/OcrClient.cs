using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Клиент OCR (шаг A4 «руки без UIA»): POST снимка страницы (PNG) в <c>--ocr-endpoint</c>
/// хода и разбор ответа. Токен хода подставляет сайдкар, мост его не видит (A3). Ответ сервера
/// НЕДОВЕРЕННЫЙ: любой мусор — понятный отказ, исключения наружу не выходят. Картинка и текст
/// не логируются — только статус, размер и длительность.
/// </summary>
internal sealed class OcrClient
{
    /// <summary>
    /// Таймаут запроса: на 20 с больше серверного таймаута страницы
    /// (<c>LlmGateway:Ocr:PageTimeoutSec</c> = 60, A1). Сервер отменяет страницу первым; таймаут
    /// клиента — страховка от зависшего соединения.
    /// </summary>
    public const int TimeoutMs = 80_000;

    /// <summary>
    /// Потолок картинки на стороне клиента: те же 8 МБ, что на сервере
    /// (<see cref="HandsOcr.MaxImageBytes"/> = <c>LocalOcrService.MaxImageBytes</c> =
    /// <c>SidecarProxy.MaxOcrBodyBytes</c>). Превышение — понятный отказ ДО отправки:
    /// «уменьшите область».
    /// </summary>
    public const long MaxImageBytes = HandsOcr.MaxImageBytes;

    /// <summary>
    /// Эндпоинт OCR хода (адрес сайдкара <c>/t/{ключ}/ocr</c>); null — агент старый и
    /// <c>--ocr-endpoint</c> не передаёт: инструмент отвечает «обновите агента».
    /// </summary>
    public static string? Endpoint { get; set; }

    /// <summary>Шов для тестов: фабрика HTTP-обработчика (в бою — <see cref="HttpClientHandler"/>).</summary>
    internal static Func<HttpMessageHandler> HandlerFactory { get; set; } = () => new HttpClientHandler();

    /// <summary>
    /// Распознаёт страницу: POST PNG в эндпоинт. Возвращает <see cref="OcrOutcome"/>;
    /// исключений наружу нет — любой сбой (сеть, таймаут, мусорный JSON) — понятный отказ.
    /// </summary>
    public static async Task<OcrOutcome> RecognizeAsync(byte[] png, CancellationToken ct)
    {
        var endpoint = Endpoint;
        if (endpoint is null)
            return OcrOutcome.Fail("no_endpoint",
                "ocr unavailable: update the agent (it does not pass --ocr-endpoint to the bridge)");

        if (png.Length > MaxImageBytes)
            return OcrOutcome.Fail("too_large",
                $"the image is {png.Length} bytes, more than the {MaxImageBytes} ceiling: shrink the region");

        using var client = new HttpClient(HandlerFactory())
        {
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs),
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(png),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

            using var response = await client.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);

            // Агрегат в лог: статус, размер, длительность — картинка и текст в нём не бывают.
            HandsLog.Write($"ocr: {status} {png.Length} bytes {stopwatch.ElapsedMilliseconds} ms");

            return status switch
            {
                200 => ParseSuccess(body),
                503 => ParseBusy(body),
                502 => OcrOutcome.Fail("bad_response",
                    "the OCR model answered with an unparseable response; try again"),
                403 => OcrOutcome.Fail("hands_refused",
                    "hands are refused for this project (server-side check); ask the owner"),
                413 => OcrOutcome.Fail("too_large",
                    $"the image is more than the {MaxImageBytes} ceiling: shrink the region"),
                401 => OcrOutcome.Fail("unauthorized",
                    "the turn token was not accepted by the sidecar; try again"),
                _ => OcrOutcome.Fail("unexpected_status",
                    $"the OCR server answered with status {status}"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OcrOutcome.Fail("timeout",
                $"the OCR request timed out after {TimeoutMs / 1000} s; try a smaller region");
        }
        catch (Exception ex)
        {
            // Сеть, DNS, TLS… — наружу не выходит: понятный отказ.
            return OcrOutcome.Fail("network", $"the OCR endpoint is unreachable: {ex.Message}");
        }
    }

    /// <summary>
    /// 200: <c>{text, model, ms, promptTokens, completionTokens}</c>. Ответ недоверенный:
    /// нет <c>text</c> или он не строка — <c>bad_response</c>, а не исключение.
    /// </summary>
    private static OcrOutcome ParseSuccess(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return OcrOutcome.Fail("bad_response", "the OCR response has no text");

            var model = GetString(root, "model");
            var ms = GetInt64(root, "ms");
            var promptTokens = GetInt32(root, "promptTokens");
            var completionTokens = GetInt32(root, "completionTokens");

            return OcrOutcome.Success(text.GetString()!, model, ms, promptTokens, completionTokens);
        }
        catch (Exception)
        {
            return OcrOutcome.Fail("bad_response", "the OCR response is not valid JSON");
        }
    }

    /// <summary>
    /// 503: <c>{reason: disabled|local_unavailable|busy_timeout|page_timeout}</c>. Каждый reason —
    /// своя понятная подсказка; мусорный reason — общий отказ.
    /// </summary>
    private static OcrOutcome ParseBusy(string body)
    {
        string? reason = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                reason = r.GetString();
        }
        catch (Exception)
        {
            // Мусорный JSON — reason остаётся null → общий отказ.
        }

        return reason switch
        {
            "disabled" => OcrOutcome.Fail("disabled",
                "OCR is disabled on the server (LlmGateway:Ocr:Enabled=false); ask the owner to enable it"),
            "local_unavailable" => OcrOutcome.Fail("local_unavailable",
                "the local OCR model is unavailable (not registered or without vision)"),
            "busy_timeout" => OcrOutcome.Fail("busy_timeout",
                "the OCR slot is busy: another page is being recognized; try again later"),
            "page_timeout" => OcrOutcome.Fail("page_timeout",
                "the OCR page timed out: the model took too long; try a smaller region"),
            _ => OcrOutcome.Fail("busy",
                "the OCR server is busy or its reason is unknown; try again later"),
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long? GetInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : null;

    private static int? GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
}

/// <summary>
/// Результат OCR: текст с метаданными на успех; на отказ — <see cref="Reason"/> (код) и
/// <see cref="Message"/> (текст для модели).
/// </summary>
/// <param name="Ok">Успех.</param>
/// <param name="Text">Распознанный текст (успех).</param>
/// <param name="Model">Модель, которая распознала (успех).</param>
/// <param name="Ms">Длительность распознавания, мс (успех).</param>
/// <param name="PromptTokens">Токены запроса (успех).</param>
/// <param name="CompletionTokens">Токены ответа (успех).</param>
/// <param name="Reason">Код отказа (no_endpoint, too_large, disabled, …).</param>
/// <param name="Message">Текст отказа для модели.</param>
public sealed record OcrOutcome(
    bool Ok,
    string? Text = null,
    string? Model = null,
    long? Ms = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? Reason = null,
    string? Message = null)
{
    public static OcrOutcome Success(string text, string? model, long? ms, int? promptTokens, int? completionTokens) =>
        new(true, text, model, ms, promptTokens, completionTokens);

    public static OcrOutcome Fail(string reason, string message) =>
        new(false, Reason: reason, Message: message);
}
