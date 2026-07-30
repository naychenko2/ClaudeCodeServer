namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>
/// Чтение активных алертов из SigNoz (<c>GET /api/v1/alerts</c>).
///
/// Направление запроса — от приложения к SigNoz. Обратное (webhook из контейнера в CCS)
/// упирается в привязку боевого хоста по имени: http.sys отвечает
/// «Bad Request - Invalid Hostname», см. docs/observability/overview.md.
/// </summary>
public sealed class SignozAlertsClient(
    IHttpClientFactory factory,
    AlertsOptions options,
    ILogger<SignozAlertsClient> log)
{
    /// <summary>
    /// Возвращает активные алерты либо <c>null</c>, если опрос не удался.
    ///
    /// Различие принципиально: пустой список означает «алертов нет» и порождает
    /// уведомления о восстановлении, а <c>null</c> — «мы не знаем» и заставляет
    /// пропустить тик. Вернув при сетевой ошибке пустоту, мы разослали бы
    /// «всё восстановилось» ровно в тот момент, когда связь с SigNoz пропала.
    /// </summary>
    public async Task<IReadOnlyList<SignozAlert>?> FetchAsync(CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient("signoz-alerts");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{options.SignozUrl.TrimEnd('/')}/api/v1/alerts");
            request.Headers.TryAddWithoutValidation("SIGNOZ-API-KEY", options.ApiKey);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("SigNoz вернул {Code} на запрос алертов", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return AlertDigest.Parse(json);
        }
        catch (OperationCanceledException)
        {
            throw;   // остановка приложения — не ошибка опроса
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось опросить алерты SigNoz");
            return null;
        }
    }
}
