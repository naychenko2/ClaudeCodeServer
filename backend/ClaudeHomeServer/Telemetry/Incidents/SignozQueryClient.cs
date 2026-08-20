using System.Text;
using ClaudeHomeServer.Telemetry.Alerts;

namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>
/// Чтение SigNoz для разбора инцидента: список алертов и запросы к
/// <c>POST /api/v5/query_range</c>.
///
/// Отдельный клиент, а не <see cref="SignozAlertsClient"/>, по одной причине: тот живёт
/// только когда доставка алертов включена (<c>AlertsOptions.IsUsable</c> → early return в
/// <c>ObservabilityExtensions</c>), а раздел «Инциденты» обязан работать и там, где push
/// намеренно выключен (дев). Разбор ответа общий — <see cref="AlertDigest.Parse"/>.
///
/// Любой отказ — <c>null</c>, а не исключение: раздел открывает человек, и «SigNoz не
/// ответил» здесь штатное состояние, а не авария приложения.
/// </summary>
public interface ISignozQueryClient
{
    Task<IReadOnlyList<SignozAlert>?> FetchAlertsAsync(CancellationToken ct);

    Task<string?> QueryRangeAsync(string body, CancellationToken ct);
}

public sealed class SignozQueryClient(
    IHttpClientFactory factory,
    IncidentsOptions options,
    ILogger<SignozQueryClient> log) : ISignozQueryClient
{
    public const string HttpClientName = "signoz-incidents";

    /// <summary>Активные алерты. <c>null</c> — опрос не удался (не путать с «алертов нет»).</summary>
    public async Task<IReadOnlyList<SignozAlert>?> FetchAlertsAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/v1/alerts", body: null, ct);
        return json is null ? null : AlertDigest.Parse(json);
    }

    /// <summary>Сырой ответ query_range. <c>null</c> — запрос не удался.</summary>
    public Task<string?> QueryRangeAsync(string body, CancellationToken ct)
        => SendAsync(HttpMethod.Post, "/api/v5/query_range", body, ct);

    private async Task<string?> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (!options.IsConfigured) return null;
        try
        {
            var http = factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(method, $"{options.SignozUrl.TrimEnd('/')}{path}");
            request.Headers.TryAddWithoutValidation("SIGNOZ-API-KEY", options.ApiKey);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 404 при живом SigNoz почти всегда — забытый base-path (/telemetry-proxy),
                // на этом уже обжигались при настройке доставки алертов
                log.LogWarning("SigNoz вернул {Code} на {Path} — проверь Telemetry:Alerts:SignozUrl (нужен base-path)",
                    (int)response.StatusCode, path);
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // отмена запроса самим человеком/остановка приложения — не отказ SigNoz
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Недоступный или подвисший SigNoz — штатный отказ опциональной зависимости.
            // Оговорка выше принципиальна: таймаут HttpClient прилетает тем же
            // OperationCanceledException (см. SignozAlertsClient).
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Запрос к SigNoz {Path} не удался", path);
            return null;
        }
    }
}
