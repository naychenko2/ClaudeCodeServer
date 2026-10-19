using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// OCR страницы локальной Qwen (фича «руки без UIA», этап A, шаг A1): картинка страницы → текст.
// Вызов vLLM в OpenAI-совместимом формате POST /v1/chat/completions: адрес и id модели — ТОЛЬКО
// из LlmProviderRegistry (провайдер local-qwen), живость эндпоинта — LocalEndpointProbe. Своих
// URL не заводить: chat/completions стыкуется к ApiBaseUrl (или AnthropicBaseUrl + /v1).
//
// Одна страница за раз: семафор = 1 на экземпляр. Общая GPU — префилл картинки душит остальных
// агентов, поэтому второй вызов ждёт слот до BusyWaitSec, иначе BusyTimeout.
//
// Ответы vLLM недоверенные: мусорный JSON, пустые choices, обрыв, не-2xx — всё BadResponse,
// не исключение наружу (для ожидаемых отказов наружу ничего не уходит). В логах — только
// агрегаты (размер, мс, токены): ни распознанного текста, ни base64-картинки.
public sealed class LocalOcrService(
    LlmProviderRegistry providers,
    ILocalEndpointProbe probe,
    IHttpClientFactory httpFactory,
    IOptionsMonitor<LlmGatewayOptions> options,
    ILogger<LocalOcrService> log)
{
    // Единственный источник адреса и id модели: локальная vision-модель.
    public const string ProviderKey = "local-qwen";
    // Именованный HttpClient (регистрация — в LlmSubsystem, рядом с остальным шлюзом).
    public const string HttpClientName = "local-ocr";
    // Потолок картинки: 8 МБ — единая константа (HandsOcr.MaxImageBytes, Core/Protocol):
    // сервер, сайдкар и мост не разойдутся.
    public const long MaxImageBytes = HandsOcr.MaxImageBytes;
    // Потолок генерации ответа (токенов): страницы короткие, запас на длинный Markdown.
    private const int MaxOutputTokens = 8192;

    // Промпт (английский, строго): транскрипция страницы в Markdown, без домыслов.
    internal const string Prompt =
        "Transcribe the text on this page exactly as written into Markdown. "
        + "Render tables as Markdown tables. Keep the original language. "
        + "Replace unreadable text with [?]. Do not add, summarize or explain anything.";

    // Одна страница за раз (общая GPU): 1 слот на экземпляр сервиса.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct)
    {
        var ocr = options.CurrentValue.Ocr;

        // Тумблер выключен — самый частый отказ (по умолчанию выключен).
        if (!ocr.Enabled)
            return OcrResult.Fail(OcrFailureReason.Disabled, "OCR выключен (LlmGateway:Ocr:Enabled=false)");

        // Потолок картинки: 8 МБ.
        if (png.Length > MaxImageBytes)
            return OcrResult.Fail(OcrFailureReason.TooLarge,
                $"картинка {png.Length} байт > потолка {MaxImageBytes}");

        // Адрес и id модели — только из реестра; неизвестный провайдер — LocalUnavailable.
        var provider = providers.GetByKey(ProviderKey);
        if (provider is null)
            return OcrResult.Fail(OcrFailureReason.LocalUnavailable,
                $"провайдер {ProviderKey} не найден в реестре LlmProviders");

        // Модель без зрения: картинка уйдёт, а ответит по заглушкам (галлюцинация).
        if (provider is { SupportsImages: false })
            return OcrResult.Fail(OcrFailureReason.LocalUnavailable,
                $"провайдер {ProviderKey} без зрения (SupportsImages=false)");

        // Живость эндпоинта: Down — локальная модель не запущена.
        var outcome = await probe.CheckAsync(provider, ct);
        if (outcome == LocalProbeOutcome.Down)
            return OcrResult.Fail(OcrFailureReason.LocalUnavailable,
                $"локальный эндпоинт {ProviderKey} не отвечает (проба Down)");

        // Слот OCR: одна страница за раз. Ожидание до BusyWaitSec, иначе BusyTimeout.
        var busyWait = TimeSpan.FromSeconds(Math.Max(1, ocr.BusyWaitSec));
        bool acquired;
        try
        {
            acquired = await _gate.WaitAsync(busyWait, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // отмена вызывающего — отмена, а не отказ
        }
        if (!acquired)
            return OcrResult.Fail(OcrFailureReason.BusyTimeout,
                $"ожидание слота OCR > {ocr.BusyWaitSec} с (занята другая страница)");

        try
        {
            return await RecognizeCoreAsync(png, provider, info, ocr.PageTimeoutSec, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Ядро: HTTP-вызов vLLM и разбор ответа. Вызывается под слотом OCR.
    private async Task<OcrResult> RecognizeCoreAsync(
        ReadOnlyMemory<byte> png, LlmProviderConfig provider, OcrRequestInfo info, int pageTimeoutSec, CancellationToken ct)
    {
        var model = provider.Models.FirstOrDefault()?.Id ?? "";
        var url = BuildChatCompletionsUrl(provider);
        var sw = Stopwatch.StartNew();

        // Таймаут одной страницы: отдельный CTS поверх токена вызывающего.
        using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pageCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, pageTimeoutSec)));

        HttpResponseMessage resp;
        try
        {
            resp = await httpFactory.CreateClient(HttpClientName)
                .SendAsync(BuildRequest(url, model, png), pageCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // отмена вызывающего — отмена, а не таймаут страницы
        }
        catch (Exception ex)
        {
            if (pageCts.IsCancellationRequested)
            {
                // Таймаут страницы: pageCts отменился по потолку PageTimeoutSec, внешний ct — нет.
                log.LogWarning("local-ocr: {Label} — {Size} байт, {Ms} мс — таймаут страницы",
                    info.Label, png.Length, sw.ElapsedMilliseconds);
                return OcrResult.Fail(OcrFailureReason.PageTimeout, $"таймаут страницы OCR: {pageTimeoutSec} с");
            }
            // Сетевой сбой (не таймаут): не 2xx и не разбор, а отказ.
            log.LogWarning("local-ocr: {Label} — {Size} байт, {Ms} мс — сбой: {Message}",
                info.Label, png.Length, sw.ElapsedMilliseconds, ex.Message);
            return OcrResult.Fail(OcrFailureReason.LocalUnavailable, $"сбой запроса: {ex.Message}");
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                // Не-2xx: тело ошибки не логируем (может нести текст), только статус.
                log.LogWarning("local-ocr: {Label} — {Size} байт, {Ms} мс — vLLM вернул {Status}",
                    info.Label, png.Length, sw.ElapsedMilliseconds, (int)resp.StatusCode);
                return OcrResult.Fail(OcrFailureReason.BadResponse, $"vLLM вернул {(int)resp.StatusCode}");
            }

            // Разбор ответа: мусорный JSON / пустые choices / обрыв → BadResponse, не исключение.
            (string Text, int PromptTokens, int CompletionTokens)? parsed;
            try
            {
                parsed = ParseResponse(body);
            }
            catch (Exception ex)
            {
                log.LogWarning("local-ocr: {Label} — {Size} байт, {Ms} мс — мусорный ответ: {Message}",
                    info.Label, png.Length, sw.ElapsedMilliseconds, ex.Message);
                return OcrResult.Fail(OcrFailureReason.BadResponse, $"ответ vLLM не разобрался: {ex.Message}");
            }

            if (parsed is null)
            {
                log.LogWarning("local-ocr: {Label} — {Size} байт, {Ms} мс — пустые choices",
                    info.Label, png.Length, sw.ElapsedMilliseconds);
                return OcrResult.Fail(OcrFailureReason.BadResponse, "пустые choices в ответе vLLM");
            }

            var (text, promptTokens, completionTokens) = parsed.Value;
            // Только агрегаты: размер, мс, токены. Ни текста, ни картинки.
            log.LogInformation("local-ocr: {Label} — {Size} байт, {Ms} мс, {Prompt}+{Completion} токенов, model={Model}",
                info.Label, png.Length, sw.ElapsedMilliseconds, promptTokens, completionTokens, model);

            return OcrResult.Success(text, model, (int)sw.ElapsedMilliseconds, promptTokens, completionTokens);
        }
    }

    // Тело запроса OpenAI chat completions: промпт + картинка (base64) в одном user-сообщении.
    private static HttpRequestMessage BuildRequest(string url, string model, ReadOnlyMemory<byte> png)
    {
        var image = Convert.ToBase64String(png.Span);
        var body = JsonSerializer.Serialize(new
        {
            model,
            temperature = 0,
            max_tokens = MaxOutputTokens,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = Prompt },
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{image}" } },
                    },
                },
            },
        });
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    // {ApiBaseUrl}/chat/completions; если ApiBaseUrl пуст — {AnthropicBaseUrl}/v1/chat/completions.
    internal static string BuildChatCompletionsUrl(LlmProviderConfig provider)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(provider.ApiBaseUrl)
            ? provider.ApiBaseUrl.TrimEnd('/')
            : provider.AnthropicBaseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/chat/completions";
        return baseUrl + "/v1/chat/completions";
    }

    // Разбор ответа vLLM (OpenAI chat completions). null — пустые choices; исключение — мусорный JSON.
    private static (string Text, int PromptTokens, int CompletionTokens)? ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null;

        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (content is null) return null;

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                completionTokens = ct.GetInt32();
        }
        return (content, promptTokens, completionTokens);
    }
}

// Контекст запроса OCR: Label — короткий log-safe идентификатор (id хода/окна) для атрибуции
// строки лога. Не текст и не картинка — их в лог не пускаем.
public sealed record OcrRequestInfo(string Label);

// Исходы отказа OCR (без исключений наружу для ожидаемых отказов).
public enum OcrFailureReason
{
    Disabled,
    LocalUnavailable,
    BusyTimeout,
    PageTimeout,
    TooLarge,
    BadResponse,
}

// Результат OCR: либо успех {Text, Model, Ms, PromptTokens, CompletionTokens},
// либо отказ {Reason, Message}.
public sealed record OcrResult
{
    public bool Ok { get; init; }
    public string? Text { get; init; }
    public string? Model { get; init; }
    public int Ms { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public OcrFailureReason? Reason { get; init; }
    public string? Message { get; init; }

    public static OcrResult Success(string text, string model, int ms, int promptTokens, int completionTokens) =>
        new() { Ok = true, Text = text, Model = model, Ms = ms, PromptTokens = promptTokens, CompletionTokens = completionTokens };

    public static OcrResult Fail(OcrFailureReason reason, string message) =>
        new() { Ok = false, Reason = reason, Message = message };
}
