using ClaudeHomeServer.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Статус встроенного раздела «Телеметрия». Фронт дёргает его при заходе в раздел и
/// решает, показать <c>&lt;iframe&gt;</c> с SigNoz или заглушку «настрой, администратор».
/// Сам проброс живёт в middleware <c>/telemetry-proxy/**</c> (Program.cs) — сюда вынесена
/// только проверка доступности, чтобы фронт не полагался на ненадёжный iframe onerror.
/// Только для админов — как и весь раздел.
/// </summary>
[ApiController]
[Route("api/telemetry")]
[Authorize(Roles = "admin")]
public class TelemetryController : ControllerBase
{
    private readonly TelemetryUiOptions _options;
    private readonly IHttpClientFactory _httpFactory;

    public TelemetryController(TelemetryUiOptions options, IHttpClientFactory httpFactory)
    {
        _options = options;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// <c>configured</c> — включён ли проброс в конфиге (<c>Telemetry:Ui:Enabled</c>);
    /// <c>reachable</c> — жив ли SigNoz прямо сейчас; <c>proxyPath</c> — путь для iframe src.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var reachable = false;
        if (_options.Enabled)
        {
            try
            {
                var client = _httpFactory.CreateClient("telemetry-ui");
                // Health на корне отвечает даже под base-path (спайк подтвердил: env двигает
                // только SPA, API замаунчен и на корне) — поэтому vendored healthcheck не трогаем.
                using var resp = await client.GetAsync(
                    $"{_options.InternalUrl.TrimEnd('/')}/api/v1/health", ct);
                reachable = resp.IsSuccessStatusCode;
            }
            catch
            {
                // SigNoz не поднят/недоступен — заглушка на фронте, не ошибка.
                reachable = false;
            }
        }

        return Ok(new
        {
            configured = _options.Enabled,
            reachable,
            proxyPath = "/telemetry-proxy/",
        });
    }
}
