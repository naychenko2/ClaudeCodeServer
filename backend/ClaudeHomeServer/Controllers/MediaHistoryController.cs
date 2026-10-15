using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.MediaHistory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// История медиа-генераций (ADR-030): 7 дней на диске, админ видит промпты, параметры,
// результаты и логин владельца. Обе ручки — [Authorize(Roles="admin")] (не-админ — 403) плюс
// серверная проверка флага local-model-stats (без флага — 404). Название чужого чата/проекта и
// переход в чат скрыты: наружу только логин.
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/local-model/history")]
public class MediaHistoryController(
    MediaHistoryIndex index,
    FeatureFlagService flags,
    IEnumerable<IMediaHistoryResultSource> sources,
    MediaHistoryThumbService thumbs,
    TimeProvider? time = null) : ControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Флаг local-model-stats у текущего пользователя; без него — 404 (UI-гейта мало: наружу чужие файлы)
    private bool Enabled() => UserId is { } id && flags.IsEnabled(id, FeatureFlagKeys.LocalModelStats);

    // Список: фильтры (вид, модель, исход, логин, период) + курсорная пагинация (50 на страницу).
    // kind/outcome/period — фиксированные наборы (контракт api.md): незнакомое значение — 400.
    [HttpGet]
    public IActionResult List(
        [FromQuery] string? kind,
        [FromQuery] string? model,
        [FromQuery] string? outcome,
        [FromQuery] string? login,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? period,
        [FromQuery] string? cursor,
        [FromQuery] int? limit)
    {
        if (!Enabled()) return NotFound();

        if (kind is not null && !Kinds.Contains(kind))
            return BadRequest(new { error = $"неизвестный kind: {kind}" });
        if (outcome is not null && !Outcomes.Contains(outcome))
            return BadRequest(new { error = $"неизвестный outcome: {outcome}" });
        if (period is not null && !Periods.Contains(period))
            return BadRequest(new { error = $"неизвестный period: {period}" });

        index.Load();

        // period задаёт окно по умолчанию; явные from/to перекрывают его (явное сильнее).
        var qFrom = ParseDate(from);
        var qTo = ParseDate(to);
        if (period is not null)
        {
            var (pFrom, pTo) = PeriodWindow(period);
            qFrom ??= pFrom;
            qTo ??= pTo;
        }

        var query = new MediaHistoryQuery
        {
            Kind = kind,
            Model = model,
            Outcome = outcome,
            Login = login,
            From = qFrom,
            To = qTo,
            Cursor = cursor,
            Limit = limit ?? MediaHistoryQuery.PageLimit,
        };
        return Ok(index.Query(query));
    }

    // Файл результата: только id записи и номер варианта n (путь с клиента не приходит).
    // size=thumb — миниатюра (лениво), full — оригинал. 410 — файла нет (reason — причина).
    [HttpGet("{recordId}/file")]
    public async Task<IActionResult> File(string recordId, [FromQuery] int n = 0,
        [FromQuery] string size = "full", CancellationToken ct = default)
    {
        if (!Enabled()) return NotFound();
        // recordId с «..» или чужими символами — 404 (единственный способ отдать файл, не открыв
        // произвольное чтение диска)
        if (recordId.Length == 0 || recordId.Contains("..") || recordId.IndexOfAny(['/', '\\', ':']) >= 0)
            return NotFound();

        index.Load();
        var record = index.Get(recordId);
        if (record is null) return NotFound();

        var origin = record.Origin;
        if (origin is null) return Gone("missing");

        // Источник по origin.Source; у выключенной подсистемы реализации нет → source_disabled
        var source = sources.FirstOrDefault(s => s.Source == origin.Source);
        if (source is null) return Gone("source_disabled");

        var reference = new MediaHistoryRef(record.PromptId, origin.OwnerId, origin.ProjectId, origin.SourceJobId, origin.SessionId);
        var resolution = await source.ResolveAsync(reference, n, ct);
        if (resolution is null) return Gone("missing");
        // Причина недоступности (failed/cancelled/interrupted, удалённый/истёкший файл) — 410,
        // и проверяется ДО n: у такой записи VariantCount==0, и n=0 дал бы ложный 404 (ADR-030 §4).
        // Для доступной записи (Reason==null) неверный n остаётся 404.
        if (resolution.Reason is { } reason) return Gone(ReasonWire(reason));
        if (n < 0 || n >= resolution.VariantCount) return NotFound();
        if (resolution.HostPath is not { } path || resolution.ContentType is not { } contentType)
            return Gone("missing");

        // size=thumb: миниатюра (лениво); отказ — отдаём оригинал, фронт рисует плитку
        if (size == "thumb")
        {
            var thumb = await thumbs.GetThumbAsync(recordId, n, path, contentType, ct);
            if (thumb is not null)
                return ServeFile(thumb, "image/webp");
        }

        return ServeFile(path, contentType);
    }

    // PhysicalFile с Range (звук/видео), inline, nosniff
    private IActionResult ServeFile(string path, string contentType)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = "inline";
        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    private IActionResult Gone(string reason) =>
        StatusCode(StatusCodes.Status410Gone, new { reason });

    // Wire-имена причин 410 (список открытый, фронт показывает незнакомую как «Файл недоступен»)
    private static string ReasonWire(MediaHistoryGoneReason reason) => reason switch
    {
        MediaHistoryGoneReason.Expired => "expired",
        MediaHistoryGoneReason.Missing => "missing",
        MediaHistoryGoneReason.ProjectGone => "project_gone",
        MediaHistoryGoneReason.LocalProject => "local_project",
        MediaHistoryGoneReason.SourceDisabled => "source_disabled",
        MediaHistoryGoneReason.NoResult => "no_result",
        _ => "missing",
    };

    // Допустимые значения фильтров (контракт api.md, раздел «Статистика локальных моделей»).
    // Незнакомое значение — 400, а не пустой список.
    private static readonly HashSet<string> Kinds = ["image", "audio", "video", "train"];
    private static readonly HashSet<string> Outcomes = ["completed", "cancelled", "failed", "interrupted"];
    private static readonly HashSet<string> Periods = ["today", "24h", "7d"];

    // period → окно from/to. «today» — по серверному локальному дню: фронт группирует по дням в
    // своём локальном поясе, который серверу неизвестен (см. api.md), поэтому берём локальный
    // день сервера как ближайший прокси. 24h/7d — назад от now (UTC). to не задаём: в окне
    // только прошлые записи.
    private (DateTimeOffset? from, DateTimeOffset? to) PeriodWindow(string period)
    {
        var now = _time.GetUtcNow();
        return period switch
        {
            "today" => (TodayStart(), null),
            "24h" => (now - TimeSpan.FromHours(24), null),
            "7d" => (now - TimeSpan.FromDays(7), null),
            _ => (null, null),
        };
    }

    // Начало «сегодня» в локальном поясе сервера (midnight с корректным смещением).
    // TimeZoneInfo.Local — локальный пояс машины; GetLocalTimeZone/ToTime — только .NET 9+.
    private DateTimeOffset TodayStart()
    {
        var midnight = _time.GetUtcNow().UtcDateTime.ToLocalTime().Date;
        return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, out var d) ? d : null;
}
