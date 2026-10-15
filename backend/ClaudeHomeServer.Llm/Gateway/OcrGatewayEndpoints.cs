using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// OCR страницы локальной Qwen (фича «руки без UIA», этап A, шаг A2):
// POST /gw/t/{turnId}/ocr — устройство шлёт картинку страницы (raw PNG),
// сервер распознаёт текстом через LocalOcrService и возвращает результат.
//
// Доступ — по токену хода (TurnTokenEndpointFilter, вход только для устройств,
// как у MCP-шлюза). Отказ для запрещённых рук — ProjectCapabilities.HandsRefusal
// (локальный проект, hands у устройства, тумблер проекта). Картинка и текст
// не логируются: в лог идут только агрегаты (размер, мс, токены).
//
// TODO(ADR-029): статистика локальных моделей — шов ILocalModelRequestSink
// спроектирован под SSE-стрим (LlmGatewayEndpoints); для one-shot OCR-запроса
// он не подходит без адаптера. Оставить до следующего шага, если метрики OCR
// понадобятся в панели «Локальные модели».
public static class OcrGatewayEndpoints
{
    public const string Route = "/gw/t/{" + TurnTokenEndpointFilter.RouteKey + "}/ocr";

    public static IEndpointConventionBuilder MapOcrGateway(this IEndpointRouteBuilder app) =>
        app.MapPost(Route, HandleAsync)
            .AddEndpointFilter<TurnTokenEndpointFilter>()
            .AllowAnonymous()
            .WithName("OcrGateway");

    private static async Task HandleAsync(
        HttpContext http,
        ISessionDirectory sessions,
        IProjectManager projects,
        IDeviceExecChannel? deviceChannel,
        LocalOcrService ocr,
        IOptionsMonitor<LlmGatewayOptions> options,
        ILoggerFactory loggers)
    {
        var grant = (TurnTokenGrant)http.Items[typeof(TurnTokenGrant)]!;

        // Тумблер OCR выключен — 503 disabled (та же логика, что в LocalOcrService).
        if (!options.CurrentValue.Ocr.Enabled)
        {
            await WriteJsonAsync(http, StatusCodes.Status503ServiceUnavailable,
                new { reason = "disabled" });
            return;
        }

        // Отказ для запрещённых рук: локальный проект, hands у устройства, тумблер.
        var refusal = HandsRefusal(grant, sessions, projects, deviceChannel);
        if (refusal is not null)
        {
            await WriteJsonAsync(http, StatusCodes.Status403Forbidden,
                new { error = "hands_refused" });
            return;
        }

        // Потолок тела: 8 МБ. 413 до чтения всего тела (ContentLength известен).
        if (http.Request.ContentLength is { } len and > LocalOcrService.MaxImageBytes)
        {
            await WriteJsonAsync(http, StatusCodes.Status413PayloadTooLarge,
                new { reason = "too_large" });
            return;
        }

        // Читаем тело: raw PNG.
        byte[] png;
        await using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
            png = ms.ToArray();
        }

        // Вызов OCR: Label — id хода (log-safe, не текст и не картинка).
        var result = await ocr.RecognizeAsync(png, new OcrRequestInfo(grant.TurnId), http.RequestAborted);

        if (result.Ok)
        {
            await WriteJsonAsync(http, StatusCodes.Status200OK, new
            {
                text = result.Text,
                model = result.Model,
                ms = result.Ms,
                promptTokens = result.PromptTokens,
                completionTokens = result.CompletionTokens,
            });
            return;
        }

        // Отказ: маппинг OcrFailureReason → HTTP-статус и reason-строка.
        if (result.Reason is not { } r)
        {
            await WriteJsonAsync(http, StatusCodes.Status500InternalServerError, new { reason = "unknown" });
            return;
        }
        var (status, reason) = MapFailure(r);
        await WriteJsonAsync(http, status, new { reason });
    }

    // ProjectCapabilities.HandsRefusal: null — можно, иначе причина.
    // Чат вне проекта (ProjectId = null) — серверный, руки не нужны.
    private static string? HandsRefusal(
        TurnTokenGrant grant, ISessionDirectory sessions, IProjectManager projects,
        IDeviceExecChannel? deviceChannel)
    {
        var session = sessions.GetById(grant.SessionId);
        if (session?.ProjectId is not { } projectId) return null;
        var project = projects.GetById(projectId);
        if (project is null) return null;
        var device = ProjectCapabilities.IsDeviceBound(project)
            ? deviceChannel?.GetStatus(grant.OwnerId, project.DeviceId!)
            : null;
        return ProjectCapabilities.HandsRefusal(project, device, project.HandsEnabled);
    }

    // OcrFailureReason → (HTTP-статус, reason-строка).
    private static (int Status, string Reason) MapFailure(OcrFailureReason reason) => reason switch
    {
        OcrFailureReason.Disabled => (StatusCodes.Status503ServiceUnavailable, "disabled"),
        OcrFailureReason.LocalUnavailable => (StatusCodes.Status503ServiceUnavailable, "local_unavailable"),
        OcrFailureReason.BusyTimeout => (StatusCodes.Status503ServiceUnavailable, "busy_timeout"),
        OcrFailureReason.PageTimeout => (StatusCodes.Status503ServiceUnavailable, "page_timeout"),
        OcrFailureReason.TooLarge => (StatusCodes.Status413PayloadTooLarge, "too_large"),
        OcrFailureReason.BadResponse => (StatusCodes.Status502BadGateway, "bad_response"),
        _ => (StatusCodes.Status500InternalServerError, "unknown"),
    };

    private static Task WriteJsonAsync(HttpContext http, int status, object body) =>
        Results.Json(body, statusCode: status).ExecuteAsync(http);
}
