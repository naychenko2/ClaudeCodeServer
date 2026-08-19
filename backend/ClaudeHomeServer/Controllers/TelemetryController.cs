using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Telemetry;
using ClaudeHomeServer.Telemetry.Incidents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Раздел «Телеметрия»: статус проброса SigNoz и разбор инцидентов.
///
/// Статус фронт дёргает при заходе в раздел и решает, показать <c>&lt;iframe&gt;</c> с
/// SigNoz или заглушку «настрой, администратор». Сам проброс живёт в middleware
/// <c>/telemetry-proxy/**</c> (Program.cs) — сюда вынесена только проверка доступности,
/// чтобы фронт не полагался на ненадёжный iframe onerror.
///
/// Инциденты — вкладка «Инциденты»: список горящих и недавних, досье по отпечатку и его
/// текстовое представление для задач/чата. Только для админов, как и весь раздел: в досье
/// видны чужие чаты.
/// </summary>
[ApiController]
[Route("api/telemetry")]
[Authorize(Roles = "admin")]
public class TelemetryController : ControllerBase
{
    private readonly TelemetryUiOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IncidentDossierService _incidents;
    private readonly ProjectManager _projects;

    public TelemetryController(
        TelemetryUiOptions options, IHttpClientFactory httpFactory,
        IncidentDossierService incidents, ProjectManager projects)
    {
        _options = options;
        _httpFactory = httpFactory;
        _incidents = incidents;
        _projects = projects;
    }

    /// <summary>Маркер репозитория продукта: по нему узнаём «свой» проект среди чужих.</summary>
    private static readonly string[] SelfMarker =
        ["backend", "ClaudeHomeServer", "ClaudeHomeServer.csproj"];

    /// <summary>
    /// В каком проекте открывать «Обсудить». Разбор инцидента кончается правкой кода
    /// продукта, поэтому разговор идёт там, где этот код лежит.
    ///
    /// Ищем САМИ, а не спрашиваем настройку: id проекта у каждой инсталляции свой, и
    /// требовать его в конфиге — значит, что у всех, кроме автора, кнопка молча вела бы
    /// в безымянный чат. Признак «своего» проекта — файл проекта бэкенда в корне; имя
    /// для этого не годится, его переименовывают.
    ///
    /// Настройка <c>Telemetry:Incidents:DiscussProjectId</c> остаётся как перебивка для
    /// нестандартной раскладки. Ничего не нашли — null, и чат откроется вне проектов.
    /// </summary>
    private string? ResolveDiscussProject(string ownerId)
    {
        if (!string.IsNullOrWhiteSpace(_incidents.DiscussProjectId))
            return _incidents.DiscussProjectId;

        // Порядок детерминированный: сначала СВОИ проекты, потом ничьи, внутри — по id.
        // Одна папка может быть заведена у нескольких владельцев (это разрешено), и без
        // порядка кнопка вела бы то в один проект, то в другой — от перезапуска к перезапуску.
        var candidates = _projects.GetAll()
            .Where(p => p.OwnerId is null || p.OwnerId == ownerId)
            .Where(p => !string.IsNullOrWhiteSpace(p.RootPath))
            .OrderBy(p => p.OwnerId == ownerId ? 0 : 1)
            .ThenBy(p => p.Id, StringComparer.Ordinal);

        foreach (var project in candidates)
        {
            var marker = Path.Combine([project.RootPath, .. SelfMarker]);
            if (System.IO.File.Exists(marker)) return project.Id;
        }
        return null;
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
            // Проект, в котором открывать «Обсудить». Отдаём в статусе, а не в досье:
            // значение одно на инстанс и нужно ещё до того, как выбран инцидент.
            discussProjectId = ResolveDiscussProject(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? ""),
        });
    }

    /// <summary>
    /// Список инцидентов: горящие сейчас и недавно погасшие.
    ///
    /// <c>status</c> отдаётся ВСЕГДА и отдельно от списка: пустой список при выключенной
    /// телеметрии читался бы как «всё тихо», хотя на деле никто не смотрит.
    /// </summary>
    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents(CancellationToken ct)
    {
        var (status, items) = await _incidents.ListAsync(ct);
        return Ok(new
        {
            status = StatusName(status),
            items = items.Select(ToDto),
        });
    }

    /// <summary>Досье по отпечатку. 404 — инцидента нет ни среди горящих, ни в истории.</summary>
    [HttpGet("incidents/{fingerprint}")]
    public async Task<IActionResult> Incident(string fingerprint, CancellationToken ct)
    {
        var dossier = await _incidents.BuildAsync(fingerprint, ct);
        return dossier is null ? NotFound() : Ok(ToDto(dossier));
    }

    /// <summary>
    /// Досье текстом (markdown) — то, что уходит в описание задачи и черновик сообщения
    /// в чат. Одно представление на все действия: см. <see cref="IncidentDossierText"/>.
    /// </summary>
    [HttpGet("incidents/{fingerprint}/text")]
    public async Task<IActionResult> IncidentText(string fingerprint, CancellationToken ct)
    {
        var dossier = await _incidents.BuildAsync(fingerprint, ct);
        return dossier is null ? NotFound() : Ok(new { text = IncidentDossierText.Render(dossier) });
    }

    /// <summary>
    /// Заглушить инцидент или вернуть ему звук. Заглушённый ОСТАЁТСЯ в списке и
    /// открывается как обычно: глушится шум, а не сам факт проблемы — счётчик на кнопке
    /// и push. Заглушка живёт у нас, а не в SigNoz намеренно: silence там выключил бы
    /// правило целиком (в том числе для другого инстанса), а тут решение персональное
    /// и снимается одним кликом.
    /// </summary>
    [HttpPost("incidents/{fingerprint}/mute")]
    public async Task<IActionResult> Mute(string fingerprint, [FromQuery] bool muted, CancellationToken ct)
    {
        await _incidents.SetMutedAsync(fingerprint, muted, ct);
        return NoContent();
    }

    /// <summary>
    /// Разбор инцидента моделью — ЕДИНСТВЕННОЕ место фичи, где участвует LLM, и только по
    /// явному нажатию человека. В промпт уходит ровно то досье, что видно в карточке
    /// (состав зафиксирован таблицей приватности в docs/observability/incident-queries.md);
    /// маршрут — место каталога <c>incident-explain</c>, то есть админ может увести его на
    /// стороннего провайдера, и это надо понимать.
    ///
    /// 502 при отказе модели: раздел остаётся живым, карточка показывает ошибку с
    /// «Повторить» — качество разбора не стоит того, чтобы ронять экран.
    /// </summary>
    [HttpPost("incidents/{fingerprint}/explain")]
    public async Task<IActionResult> Explain(
        string fingerprint,
        [FromServices] Services.Llm.ICheapTextRunner cheap,
        [FromServices] ILogger<TelemetryController> log,
        CancellationToken ct)
    {
        var dossier = await _incidents.BuildAsync(fingerprint, ct);
        if (dossier is null) return NotFound();

        var ownerId = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
        try
        {
            var text = await cheap.RunAsync(
                Services.Llm.LocalActionCatalog.IncidentExplain,
                IncidentDossierText.ExplainPrompt(dossier),
                ownerId: ownerId, ct: ct);
            if (string.IsNullOrWhiteSpace(text))
                return StatusCode(502, new { error = "Модель не дала разбора" });
            return Ok(new { text });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Разбор инцидента {Fingerprint} не удался", fingerprint);
            return StatusCode(502, new { error = "Не удалось получить разбор" });
        }
    }

    private static string StatusName(IncidentStatus status) => status switch
    {
        IncidentStatus.NotConfigured => "notConfigured",
        IncidentStatus.Unavailable => "unavailable",
        _ => "ok",
    };

    private static object ToDto(IncidentSummary i) => new
    {
        fingerprint = i.Fingerprint,
        title = i.Title,
        description = i.Description,
        severity = i.Severity,
        environment = i.Environment,
        startedAt = i.StartedAt,
        resolvedAt = i.ResolvedAt,
        isFiring = i.IsFiring,
        isMuted = i.IsMuted,
    };

    private static object ToDto(IncidentDossier d) => new
    {
        incident = ToDto(d.Incident),
        status = StatusName(d.Status),
        from = d.From,
        to = d.To,
        isForeignEnvironment = d.IsForeignEnvironment,
        breakdownTag = d.BreakdownTag,
        breakdown = d.Breakdown.Select(r => new { label = r.Label, count = r.Count }),
        turns = d.Turns.Select(t => new
        {
            traceId = t.TraceId,
            chatId = t.ChatId,
            at = t.At,
            model = t.Model,
            provider = t.Provider,
            errorType = t.ErrorType,
            durationMs = t.DurationMs,
        }),
        turnsTotal = d.TurnsTotal,
        logs = d.Logs.Select(l => new { at = l.At, severity = l.Severity, message = l.Message }),
        logsTotal = d.LogsTotal,
        chats = d.Chats.Select(c => new
        {
            chatId = c.ChatId,
            projectId = c.ProjectId,
            title = c.Title,
            failures = c.Failures,
            totalTokens = c.TotalTokens,
            mcpFailures = c.McpFailures,
        }),
        rulePath = d.RulePath,
    };
}
